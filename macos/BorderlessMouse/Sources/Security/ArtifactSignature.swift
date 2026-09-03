import CryptoKit
import Foundation

/// Verifies release artifacts with a project-owned key. This keeps beta updates
/// authenticated even when the operating-system code signature is not publicly trusted.
enum ArtifactSignature {
    private static let publicKeyX963 = Data(base64Encoded:
        "BOsxcC3CCHApvQSF3BNFaUcutGylG+R3O7IHx6Hwlj+LEhyq+Tv9gve/g5piFvEU8qxbRhw7sWVrGnATnAGqvws=")!

    static func verify(file: URL, signatureData: Data) throws {
        guard (64...80).contains(signatureData.count) else { throw SignatureError.invalid }
        do {
            let publicKey = try P256.Signing.PublicKey(x963Representation: publicKeyX963)
            let signature = try P256.Signing.ECDSASignature(derRepresentation: signatureData)
            let digest = try sha256Digest(of: file)
            guard publicKey.isValidSignature(signature, for: digest) else { throw SignatureError.invalid }
        } catch let error as SignatureError {
            throw error
        } catch {
            throw SignatureError.invalid
        }
    }

    static func verify(data: Data, signatureData: Data) -> Bool {
        do {
            let publicKey = try P256.Signing.PublicKey(x963Representation: publicKeyX963)
            let signature = try P256.Signing.ECDSASignature(derRepresentation: signatureData)
            return publicKey.isValidSignature(signature, for: data)
        } catch {
            return false
        }
    }

    static func sha256Hex(of file: URL) throws -> String {
        try sha256Digest(of: file).map { String(format: "%02x", $0) }.joined()
    }

    private static func sha256Digest(of file: URL) throws -> SHA256.Digest {
        let handle = try FileHandle(forReadingFrom: file)
        defer { try? handle.close() }
        var hasher = SHA256()
        while true {
            let chunk = try handle.read(upToCount: 1 << 20) ?? Data()
            if chunk.isEmpty { break }
            hasher.update(data: chunk)
        }
        return hasher.finalize()
    }

    enum SignatureError: LocalizedError {
        case invalid

        var errorDescription: String? {
            L10n.text(
                "Podpis kryptograficzny pliku aktualizacji jest nieprawidłowy. Dotychczasowa aplikacja pozostaje bez zmian.",
                "The update artifact signature is invalid. The installed app was not changed.")
        }
    }
}
