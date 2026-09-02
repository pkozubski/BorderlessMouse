import CryptoKit
import Foundation
import Security

/// Weryfikuje tożsamość wydawcy przed instalacją, niezależnie od sumy pliku z GitHuba.
enum ReleaseSignature {
    static func verifyRelease(at app: URL, certificateURL: URL) throws {
        let certificateData = try Data(contentsOf: certificateURL)
        guard SecCertificateCreateWithData(nil, certificateData as CFData) != nil else {
            throw SignatureError.missingCertificate
        }
        // Język wymagań macOS identyfikuje certyfikat jego odciskiem SHA-1.
        // Integralność samego kodu nadal weryfikuje Security.framework.
        let fingerprint = Insecure.SHA1.hash(data: certificateData)
            .map { byte in
                let hex = String(byte, radix: 16)
                return hex.count == 1 ? "0\(hex)" : hex
            }.joined()
        let expression = "identifier \"com.borderlessmouse.mac\" and anchor H\"\(fingerprint)\""
        var requirement: SecRequirement?
        try check(SecRequirementCreateWithString(expression as CFString, [], &requirement))
        guard let requirement else { throw SignatureError.missingCertificate }
        try verify(at: app, requirement: requirement)
    }

    static func verify(at app: URL, requirement: SecRequirement? = nil) throws {
        var code: SecStaticCode?
        try check(SecStaticCodeCreateWithPath(app as CFURL, [], &code))
        guard let code else { throw SignatureError.invalidSignature(errSecCSUnsigned) }
        let flags = SecCSFlags(rawValue: kSecCSStrictValidate | kSecCSCheckAllArchitectures | kSecCSCheckNestedCode)
        try check(SecStaticCodeCheckValidity(code, flags, requirement))
    }

    private static func check(_ status: OSStatus) throws {
        guard status == errSecSuccess else { throw SignatureError.invalidSignature(status) }
    }

    enum SignatureError: LocalizedError {
        case missingCertificate
        case invalidSignature(OSStatus)

        var errorDescription: String? {
            switch self {
            case .missingCertificate:
                return "Brak prawidłowego certyfikatu wydawcy w aplikacji. Aktualizacja nie została zainstalowana."
            case .invalidSignature(let status):
                let detail = SecCopyErrorMessageString(status, nil) as String? ?? "kod \(status)"
                return "Nieprawidłowy podpis aktualizacji: \(detail). Dotychczasowa aplikacja pozostaje bez zmian."
            }
        }
    }
}
