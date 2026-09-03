import AppKit
import CryptoKit
import Foundation

/// Auto-updater oparty o GitHub Releases: sprawdza najnowszy tag, pobiera
/// `BorderlessMouse-macOS.zip`, weryfikuje SHA-256, projektowy podpis ECDSA
/// i stały certyfikat wydawcy, a następnie atomowo podmienia bundle.
@MainActor
final class Updater: ObservableObject {
    static let owner = "pkozubski"
    static let repo = "BorderlessMouse"
    static let assetName = "BorderlessMouse-macOS.zip"
    static let checksumsName = "SHA256SUMS.txt"
    static let signatureName = "BorderlessMouse-macOS.zip.sig"
    private static let maximumDownloadBytes = 300 * 1024 * 1024

    struct Release: Equatable {
        let version: String
        let tag: String
        let notes: String
        let pageURL: URL
        let assetURL: URL
        let checksumsURL: URL?
        let signatureURL: URL
    }

    enum State: Equatable {
        case idle
        case checking
        case upToDate(String)
        case available(Release)
        case downloading(Double)
        case installing
        case failed(String)
    }

    @Published private(set) var state: State = .idle

    static var currentVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.0.0"
    }

    private static let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm"
        return f
    }()

    // MARK: - Sprawdzanie

    func check(silent: Bool) async {
        if case .downloading = state { return }
        if case .installing = state { return }
        state = .checking
        do {
            guard let release = try await fetchLatest() else {
                state = silent ? .idle : .failed(L10n.text("Brak wydań na GitHubie", "No releases found on GitHub"))
                return
            }
            if Self.isNewer(release.version, than: Self.currentVersion) {
                state = .available(release)
            } else {
                state = .upToDate(Self.timeFormatter.string(from: Date()))
            }
        } catch {
            state = silent ? .idle : .failed(error.localizedDescription)
        }
    }

    private func fetchLatest() async throws -> Release? {
        var request = URLRequest(url: URL(string: "https://api.github.com/repos/\(Self.owner)/\(Self.repo)/releases/latest")!)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("BorderlessMouse-macOS/\(Self.currentVersion)", forHTTPHeaderField: "User-Agent")
        request.timeoutInterval = 15
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { return nil }
        if http.statusCode == 404 { return nil }
        guard http.statusCode == 200 else { throw UpdaterError.http(http.statusCode) }
        guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = json["tag_name"] as? String,
              let page = (json["html_url"] as? String).flatMap(URL.init(string:)),
              let assets = json["assets"] as? [[String: Any]] else { throw UpdaterError.malformed }
        var assetURL: URL?
        var checksumsURL: URL?
        var signatureURL: URL?
        for asset in assets {
            guard let name = asset["name"] as? String,
                  let url = (asset["browser_download_url"] as? String).flatMap(URL.init(string:)) else { continue }
            if name == Self.assetName { assetURL = url }
            if name == Self.checksumsName { checksumsURL = url }
            if name == Self.signatureName { signatureURL = url }
        }
        guard let assetURL else { return nil }
        guard let signatureURL else { throw UpdaterError.missingSignature }
        let version = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
        return Release(version: version, tag: tag, notes: (json["body"] as? String) ?? "",
                       pageURL: page, assetURL: assetURL, checksumsURL: checksumsURL,
                       signatureURL: signatureURL)
    }

    static func isNewer(_ candidate: String, than current: String) -> Bool {
        func parts(_ v: String) -> [Int] {
            v.split(whereSeparator: { $0 == "." || $0 == "-" }).prefix(3).map { Int($0) ?? 0 }
        }
        let a = parts(candidate), b = parts(current)
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0
            let y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    // MARK: - Instalacja

    func install(codesignIdentity: String) async {
        guard case .available(let release) = state else { return }
        state = .downloading(0)
        do {
            let zipURL = try await download(release.assetURL) { [weak self] progress in
                Task { @MainActor in self?.state = .downloading(progress) }
            }
            state = .installing
            guard let checksumsURL = release.checksumsURL else { throw UpdaterError.missingChecksum }
            try await verifyChecksum(of: zipURL, against: checksumsURL)
            try await verifySignature(of: zipURL, against: release.signatureURL)
            let staging = zipURL.deletingLastPathComponent().appendingPathComponent("unpacked", isDirectory: true)
            try? FileManager.default.removeItem(at: staging)
            try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)
            try run("/usr/bin/ditto", ["-x", "-k", zipURL.path, staging.path])
            guard let newApp = try FileManager.default.contentsOfDirectory(at: staging, includingPropertiesForKeys: nil)
                .first(where: { $0.pathExtension == "app" }) else { throw UpdaterError.noAppInArchive }
            guard let certificateURL = Bundle.main.url(forResource: "ReleaseSigning", withExtension: "cer") else {
                throw ReleaseSignature.SignatureError.missingCertificate
            }
            try ReleaseSignature.verifyRelease(at: newApp, certificateURL: certificateURL)
            _ = try? run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", newApp.path])
            let identity = codesignIdentity.trimmingCharacters(in: .whitespacesAndNewlines)
            if !identity.isEmpty {
                // Nie wolno kontynuować po błędzie: utrata lokalnego podpisu zerwałaby zgodę TCC.
                guard identity != "-" else { throw UpdaterError.adHocIdentity }
                try run("/usr/bin/codesign", ["--force", "--sign", identity,
                                             "--entitlements", newApp.appendingPathComponent("Contents/Resources/BorderlessMouse.entitlements").path,
                                             newApp.path])
                try ReleaseSignature.verify(at: newApp)
            }
            try swapAndRelaunch(newApp: newApp)
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    private func download(_ url: URL, progress: @escaping (Double) -> Void) async throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("BorderlessMouse-update-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let dest = dir.appendingPathComponent(Self.assetName)
        var request = URLRequest(url: url)
        request.setValue("BorderlessMouse-macOS/\(Self.currentVersion)", forHTTPHeaderField: "User-Agent")
        let (bytes, response) = try await URLSession.shared.bytes(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw UpdaterError.http((response as? HTTPURLResponse)?.statusCode ?? -1)
        }
        if http.expectedContentLength > Self.maximumDownloadBytes {
            throw UpdaterError.downloadTooLarge
        }
        let total = Double(http.expectedContentLength)
        var received = 0
        var buffer = [UInt8]()
        buffer.reserveCapacity(1 << 20)
        let handle: FileHandle
        FileManager.default.createFile(atPath: dest.path, contents: nil)
        handle = try FileHandle(forWritingTo: dest)
        defer { try? handle.close() }
        var lastReport = Date()
        for try await byte in bytes {
            buffer.append(byte)
            if buffer.count >= 1 << 20 {
                handle.write(Data(buffer))
                received += buffer.count
                if received > Self.maximumDownloadBytes { throw UpdaterError.downloadTooLarge }
                buffer.removeAll(keepingCapacity: true)
                if total > 0, Date().timeIntervalSince(lastReport) > 0.1 {
                    lastReport = Date()
                    progress(Double(received) / total)
                }
            }
        }
        if !buffer.isEmpty {
            received += buffer.count
            if received > Self.maximumDownloadBytes { throw UpdaterError.downloadTooLarge }
            handle.write(Data(buffer))
        }
        progress(1)
        return dest
    }

    private func verifyChecksum(of file: URL, against checksumsURL: URL) async throws {
        let (data, _) = try await URLSession.shared.data(from: checksumsURL)
        let text = String(decoding: data, as: UTF8.self)
        guard let entry = text.split(separator: "\n").map({ $0.split(whereSeparator: { $0.isWhitespace }) })
            .first(where: { $0.count == 2 && $0[1] == Self.assetName }),
              let expected = entry.first,
              expected.count == 64, expected.allSatisfy({ $0.isHexDigit }) else { throw UpdaterError.missingChecksum }
        let digest = try ArtifactSignature.sha256Hex(of: file)
        guard digest == String(expected).lowercased() else { throw UpdaterError.checksumMismatch }
    }

    private func verifySignature(of file: URL, against signatureURL: URL) async throws {
        var request = URLRequest(url: signatureURL)
        request.setValue("BorderlessMouse-macOS/\(Self.currentVersion)", forHTTPHeaderField: "User-Agent")
        request.timeoutInterval = 15
        let (bytes, response) = try await URLSession.shared.bytes(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw UpdaterError.http((response as? HTTPURLResponse)?.statusCode ?? -1)
        }
        if http.expectedContentLength > 256 { throw UpdaterError.invalidSignatureFile }
        var signature = Data()
        signature.reserveCapacity(80)
        for try await byte in bytes {
            guard signature.count < 256 else { throw UpdaterError.invalidSignatureFile }
            signature.append(byte)
        }
        try ArtifactSignature.verify(file: file, signatureData: signature)
    }

    private func swapAndRelaunch(newApp: URL) throws {
        let current = Bundle.main.bundleURL
        let parent = current.deletingLastPathComponent()
        guard FileManager.default.isWritableFile(atPath: parent.path) else {
            throw UpdaterError.notWritable(parent.path)
        }
        let script = """
        #!/bin/sh
        PID="$1"; NEW="$2"; APP="$3"
        while kill -0 "$PID" 2>/dev/null; do sleep 0.2; done
        OLD="$APP.old-$$"
        mv "$APP" "$OLD" || exit 1
        if ! mv "$NEW" "$APP"; then mv "$OLD" "$APP"; exit 1; fi
        if open -n "$APP"; then
          rm -rf "$OLD"
        else
          rm -rf "$APP"
          mv "$OLD" "$APP"
          open -n "$APP"
          exit 1
        fi
        """
        let scriptURL = newApp.deletingLastPathComponent().appendingPathComponent("swap.sh")
        try script.write(to: scriptURL, atomically: true, encoding: .utf8)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = [scriptURL.path, String(ProcessInfo.processInfo.processIdentifier), newApp.path, current.path]
        try process.run()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
            NSApp.terminate(nil)
        }
    }

    @discardableResult
    private func run(_ tool: String, _ args: [String]) throws -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        let err = Pipe()
        p.standardError = err
        p.standardOutput = err
        try p.run()
        // Opróżniamy wspólny potok przed waitUntilExit, żeby narzędzie nie utknęło przy pełnym buforze.
        let output = err.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        if p.terminationStatus != 0 {
            let msg = String(decoding: output, as: UTF8.self)
            throw UpdaterError.tool(tool, msg.trimmingCharacters(in: .whitespacesAndNewlines))
        }
        return p.terminationStatus
    }

    enum UpdaterError: LocalizedError {
        case http(Int)
        case malformed
        case noAppInArchive
        case missingChecksum
        case missingSignature
        case invalidSignatureFile
        case checksumMismatch
        case downloadTooLarge
        case adHocIdentity
        case notWritable(String)
        case tool(String, String)

        var errorDescription: String? {
            switch self {
            case .http(let code): return L10n.text("GitHub odpowiedział kodem \(code)", "GitHub returned status \(code)")
            case .malformed: return L10n.text("Nieoczekiwana odpowiedź GitHuba", "Unexpected GitHub response")
            case .noAppInArchive: return L10n.text("W archiwum nie ma aplikacji", "The archive does not contain the app")
            case .missingChecksum: return L10n.text("W wydaniu brakuje prawidłowej sumy SHA-256 dla aplikacji", "The release has no valid SHA-256 checksum for the app")
            case .missingSignature: return L10n.text("W wydaniu brakuje podpisu kryptograficznego aplikacji", "The release has no cryptographic signature for the app")
            case .invalidSignatureFile: return L10n.text("Plik podpisu aktualizacji jest nieprawidłowy", "The update signature file is invalid")
            case .checksumMismatch: return L10n.text("Suma SHA-256 pobranego pliku nie zgadza się z wydaniem", "The downloaded file does not match the release SHA-256 checksum")
            case .downloadTooLarge: return L10n.text("Plik aktualizacji przekracza limit 300 MiB", "The update exceeds the 300 MiB limit")
            case .adHocIdentity: return L10n.text("Podpis ad-hoc nie zachowuje uprawnień. Zostaw pole certyfikatu puste, aby użyć stałego podpisu wydawcy.", "An ad-hoc signature cannot preserve permissions. Leave the certificate field empty to use the publisher signature.")
            case .notWritable(let path): return L10n.text("Brak prawa zapisu do \(path) – przenieś aplikację np. do ~/Applications", "Cannot write to \(path); move the app to a writable location such as ~/Applications")
            case .tool(let tool, let msg): return "\(tool): \(msg)"
            }
        }
    }
}
