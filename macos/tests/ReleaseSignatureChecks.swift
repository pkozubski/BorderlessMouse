import Foundation
import Security

@main
struct ReleaseSignatureChecks {
    static func main() throws {
        guard CommandLine.arguments.count == 4 else { fatalError("Expected certificate, fixtures and app paths") }
        let certificate = URL(fileURLWithPath: CommandLine.arguments[1])
        let fixtures = URL(fileURLWithPath: CommandLine.arguments[2])
        let release = URL(fileURLWithPath: CommandLine.arguments[3])
        let first = fixtures.appendingPathComponent("first.app")
        let second = fixtures.appendingPathComponent("second.app")

        for app in [first, second, release] {
            try ReleaseSignature.verifyRelease(at: app, certificateURL: certificate)
            print("PASS: verified publisher and complete signature of \(app.lastPathComponent)")
        }
        let firstRequirement = try designatedRequirement(of: first)
        let secondRequirement = try designatedRequirement(of: second)
        try ReleaseSignature.verify(at: second, requirement: firstRequirement)
        try ReleaseSignature.verify(at: first, requirement: secondRequirement)
        try ReleaseSignature.verify(at: release, requirement: firstRequirement)
        print("PASS: different builds and the release satisfy the same designated requirement")

        for name in ["adhoc.app", "unsigned.app", "tampered.app", "wrong-id.app", "missing.app"] {
            try expectRejection(name) {
                try ReleaseSignature.verifyRelease(at: fixtures.appendingPathComponent(name), certificateURL: certificate)
            }
        }
        for name in ["other.cer", "invalid.cer", "missing.cer"] {
            try expectRejection(name) {
                try ReleaseSignature.verifyRelease(at: second, certificateURL: fixtures.appendingPathComponent(name))
            }
        }
        print("Signature continuity and update validation checks passed.")
    }

    private static func designatedRequirement(of app: URL) throws -> SecRequirement {
        var code: SecStaticCode?
        let createStatus = SecStaticCodeCreateWithPath(app as CFURL, [], &code)
        guard createStatus == errSecSuccess, let code else { throw TestError.failed("Cannot read signed fixture") }
        var requirement: SecRequirement?
        let status = SecCodeCopyDesignatedRequirement(code, [], &requirement)
        guard status == errSecSuccess, let requirement else { throw TestError.failed("Missing designated requirement") }
        return requirement
    }

    private static func expectRejection(_ name: String, operation: () throws -> Void) throws {
        do {
            try operation()
        } catch {
            print("PASS: rejected \(name)")
            return
        }
        throw TestError.failed("Unexpectedly accepted \(name)")
    }

    private enum TestError: Error {
        case failed(String)
    }
}
