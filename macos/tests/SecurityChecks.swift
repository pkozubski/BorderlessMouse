import Foundation

@main
struct SecurityChecks {
    static func expect(_ condition: @autoclosure () -> Bool, _ message: String) {
        guard condition() else {
            FileHandle.standardError.write(Data("FAIL: \(message)\n".utf8))
            exit(1)
        }
    }

    static func data(_ hex: String) -> Data {
        var bytes = [UInt8]()
        var index = hex.startIndex
        while index < hex.endIndex {
            let end = hex.index(index, offsetBy: 2)
            bytes.append(UInt8(hex[index..<end], radix: 16)!)
            index = end
        }
        return Data(bytes)
    }

    static func main() {
        let secret = Data(0x00...0x0F)
        let clientNonce = Data(0x10...0x1F)
        let serverNonce = Data(0x20...0x2F)

        let code = PairingCodeCodec.encode(secret)
        expect(code == "AAAQE-AYEAU-DAOCA-JBIFQ-YDIOB-4", "stable Base32 pairing code")
        expect(PairingCodeCodec.decode(code.lowercased()) == secret, "pairing code round trip")
        expect(PairingCodeCodec.decode(code + "A") == nil, "pairing code rejects extra Base32 data")
        expect(PairingCodeCodec.decode(String(code.dropLast()) + "B") == nil, "pairing code rejects non-canonical padding")
        expect(PairingCodeCodec.decode("wrong") == nil, "invalid pairing code")

        let serverProof = ControlCrypto.proof(secret: secret, role: "server", clientNonce: clientNonce, serverNonce: serverNonce)
        let clientProof = ControlCrypto.proof(secret: secret, role: "client", clientNonce: clientNonce, serverNonce: serverNonce)
        expect(serverProof == data("4a9dcee988ea2e21921ed8d4a594e0f3af0ceb3805283584d28ddae944cb688b"), "server proof vector")
        expect(clientProof == data("b7015ba17131df196b87fb0238e631f1813de80a7d2fcc6c2d326d28fb313b92"), "client proof vector")

        var salt = clientNonce
        salt.append(serverNonce)
        expect(ControlCrypto.derive(secret: secret, salt: salt, info: "BorderlessMouse/v2/control/client-to-server")
            == data("fbf9a195b4321503c246bb0855572b6b496e49a292fff8fb8effa082db846191"), "HKDF vector")

        let client = SecureSession(secret: secret, clientNonce: clientNonce, serverNonce: serverNonce, role: .client)
        let server = SecureSession(secret: secret, clientNonce: clientNonce, serverNonce: serverNonce, role: .server)
        expect(client.audioKey == data("4f266a9ca00dc725ad16bfc37c25804926196cb2d3f7a9f07a22996cd9e54aaa"), "audio key vector")
        expect(client.audioSessionID == 0xE4D7B1D0FC6AACD5, "audio session id vector")
        let ping = Frame.ping(123_456)
        let clientEnvelope = client.seal(ping)!
        expect(server.open(clientEnvelope) == ping, "client-to-server authenticated encryption")
        expect(server.open(clientEnvelope) == nil, "control replay rejected")
        var tampered = client.seal(ping)!
        tampered[tampered.index(before: tampered.endIndex)] ^= 0x01
        expect(server.open(tampered) == nil, "tampered control frame rejected")
        let status = Frame.status([.accessibilityGranted, .audioCapturing])
        expect(client.open(server.seal(status)!) == status, "server-to-client authenticated encryption")
        expect(Frame.parseSingle(status)?.0 == .status, "single inner frame parsing")
        expect(Frame.parseSingle(status + Data([0])) == nil, "trailing bytes rejected")

        let wrongSecret = Data(repeating: 0xAA, count: 16)
        let impostor = SecureSession(secret: wrongSecret, clientNonce: clientNonce, serverNonce: serverNonce, role: .client)
        expect(server.open(impostor.seal(ping)!) == nil, "wrong pairing key rejected")
        print("✓ Security: pairing vectors, HKDF, AES-GCM, tamper and replay protection")
    }
}
