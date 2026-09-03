import AppKit
import Foundation
import ServiceManagement

/// Autostart na macOS. Podstawowo używa `SMAppService.mainApp` (macOS 13+,
/// pozycja widoczna w Ustawieniach → Ogólne → Elementy logowania). Gdy to
/// zawiedzie (np. build ad-hoc bez stałej sygnatury), zapasowo instaluje
/// własny LaunchAgent w `~/Library/LaunchAgents`.
enum LoginItem {
    static let agentLabel = "com.borderlessmouse.mac.login"
    /// Argument przekazywany przy starcie z logowania (używa go tylko LaunchAgent).
    static let loginArgument = "--login"

    enum Mechanism: String {
        case none, service, launchAgent
    }

    private static var agentURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(agentLabel).plist")
    }

    // MARK: - Stan

    static var mechanism: Mechanism {
        if SMAppService.mainApp.status == .enabled { return .service }
        if FileManager.default.fileExists(atPath: agentURL.path) { return .launchAgent }
        return .none
    }

    static var isEnabled: Bool { mechanism != .none }

    /// Opis dla interfejsu.
    static var statusDescription: String {
        switch mechanism {
        case .service:
            return L10n.text("Włączony (element logowania macOS)", "Enabled (macOS login item)")
        case .launchAgent:
            return L10n.text("Włączony (LaunchAgent w ~/Library/LaunchAgents)", "Enabled (LaunchAgent in ~/Library/LaunchAgents)")
        case .none:
            switch SMAppService.mainApp.status {
            case .requiresApproval:
                return L10n.text("Wymaga zgody w Ustawieniach → Ogólne → Elementy logowania",
                                 "Approval required in System Settings → General → Login Items")
            default:
                return L10n.text("Wyłączony", "Disabled")
            }
        }
    }

    // MARK: - Włączanie / wyłączanie

    @discardableResult
    static func setEnabled(_ enabled: Bool) throws -> Mechanism {
        if enabled {
            // 1) nowoczesny element logowania
            do {
                if SMAppService.mainApp.status != .enabled {
                    try SMAppService.mainApp.register()
                }
                removeAgent()
                return .service
            } catch {
                // 2) zapasowo LaunchAgent (działa też przy podpisie ad-hoc)
                try installAgent()
                return .launchAgent
            }
        } else {
            var thrown: Error?
            if SMAppService.mainApp.status == .enabled {
                do { try SMAppService.mainApp.unregister() } catch { thrown = error }
            }
            removeAgent()
            if let thrown, mechanism == .service { throw thrown }
            return .none
        }
    }

    /// Po aktualizacji aplikacji ścieżka w LaunchAgencie mogła się zmienić.
    static func refreshIfNeeded() {
        guard FileManager.default.fileExists(atPath: agentURL.path),
              let plist = NSDictionary(contentsOf: agentURL) as? [String: Any],
              let args = plist["ProgramArguments"] as? [String],
              let recorded = args.first,
              recorded != executablePath else { return }
        try? installAgent()
    }

    // MARK: - LaunchAgent

    private static var executablePath: String {
        Bundle.main.executableURL?.path ?? CommandLine.arguments[0]
    }

    private static func installAgent() throws {
        let dir = agentURL.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let plist: [String: Any] = [
            "Label": agentLabel,
            "ProgramArguments": [executablePath, loginArgument],
            "RunAtLoad": true,
            "ProcessType": "Interactive",
            "LimitLoadToSessionType": "Aqua",
        ]
        let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
        try data.write(to: agentURL, options: .atomic)
        // przeładuj agenta (ignorujemy błędy – plik i tak zadziała po ponownym logowaniu)
        let uid = getuid()
        launchctl(["bootout", "gui/\(uid)/\(agentLabel)"])
        launchctl(["bootstrap", "gui/\(uid)", agentURL.path])
    }

    private static func removeAgent() {
        guard FileManager.default.fileExists(atPath: agentURL.path) else { return }
        launchctl(["bootout", "gui/\(getuid())/\(agentLabel)"])
        try? FileManager.default.removeItem(at: agentURL)
    }

    // MARK: - Diagnostyka (--login-item-test)

    /// Włącza autostart, wypisuje stan, przywraca poprzedni stan i kończy proces.
    static func runDiagnostics() -> Never {
        let wasEnabled = isEnabled
        print("start: mechanism=\(mechanism.rawValue) smStatus=\(SMAppService.mainApp.status.rawValue) status=\(statusDescription)")
        do {
            let mech = try setEnabled(true)
            print("enable: mechanism=\(mech.rawValue) status=\(statusDescription)")
            if mech == .launchAgent {
                let plist = NSDictionary(contentsOf: agentURL) as? [String: Any]
                print("  plist args=\((plist?["ProgramArguments"] as? [String]) ?? [])")
                print("  launchctl: \(launchctlPrint())")
            }
        } catch {
            print("enable FAILED: \(error.localizedDescription)")
        }
        if !wasEnabled {
            do {
                _ = try setEnabled(false)
                print("restore: mechanism=\(mechanism.rawValue) status=\(statusDescription)")
            } catch {
                print("restore FAILED: \(error.localizedDescription)")
            }
        }
        exit(0)
    }

    private static func launchctlPrint() -> String {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        p.arguments = ["print", "gui/\(getuid())/\(agentLabel)"]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = Pipe()
        try? p.run()
        p.waitUntilExit()
        let text = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        if p.terminationStatus != 0 { return "not loaded (status \(p.terminationStatus))" }
        return text.split(separator: "\n").first(where: { $0.contains("state = ") })?.trimmingCharacters(in: .whitespaces) ?? "loaded"
    }

    private static func launchctl(_ args: [String]) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        p.arguments = args
        p.standardOutput = Pipe()
        p.standardError = Pipe()
        try? p.run()
        p.waitUntilExit()
    }
}
