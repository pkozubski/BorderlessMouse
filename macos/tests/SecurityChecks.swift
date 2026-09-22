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

        let updateVector = Data("BorderlessMouse updater signature test vector v1".utf8)
        let updateSignature = Data(base64Encoded: "MEUCIQDp/vz4PuRSUycKTyZluJFz+XxYhRqOXtzU4wQ+RkI0ZQIgeGsnsrQKY2cVBzv+KKLDqsmH1lpZ7XDYI+E7TT/VSS4=")!
        expect(ArtifactSignature.verify(data: updateVector, signatureData: updateSignature), "release artifact signature vector")
        var alteredUpdateVector = updateVector
        alteredUpdateVector[0] ^= 1
        expect(!ArtifactSignature.verify(data: alteredUpdateVector, signatureData: updateSignature), "tampered release artifact rejected")
        expect(!ArtifactSignature.verify(data: updateVector, signatureData: Data(updateSignature.dropLast())), "truncated release signature rejected")
        checkVirtualDisplayProtocol()
        print("✓ Security: pairing, encrypted sessions, replay protection and signed update artifacts")
    }

    static func checkVirtualDisplayProtocol() {
        let request = DisplayStartRequest(pixelWidth: 2560, pixelHeight: 1440, scalePercent: 125, edge: .right,
                                          maxBitrateKbps: 20_000, mode: .windows)
        expect(request.payload == Array(data("000aa0057d000100204e000001")), "display start layout (shared with Windows)")
        expect(DisplayStartRequest(payload: request.payload) == request, "display start round trip")
        expect(DisplayStartRequest(payload: Array(request.payload.prefix(12)))?.mode == .fullscreen,
               "display start without mode byte means full desktop")
        let windows = Frame.displayWindows([NormalizedRect(x: 1, y: 2, width: 0x8000, height: 0xFFFF, flags: NormalizedRect.menuBar)])
        expect(windows == data("860a" + "01" + "0100" + "0200" + "0080" + "ffff" + "01"), "display windows layout")
        expect(Frame.windowHandoff(x: 0x1234, y: 0xFFFF) == data("34041234ffff".replacingOccurrences(of: "1234", with: "3412")),
               "window handoff layout")
        var badCodec = request.payload
        badCodec[7] = 9
        expect(DisplayStartRequest(payload: badCodec) == nil, "unknown video codec rejected")
        expect(DisplayStartRequest(payload: Array(request.payload.prefix(8))) == nil, "truncated display start rejected")

        let key = Data((0..<32).map { UInt8($0 * 7 & 0xFF) })
        let token = Data(repeating: 0x5A, count: VideoStream.tokenBytes)
        let ready = Frame.displayReady(status: 0, port: 50123, key: key, token: token, pixelWidth: 2560, pixelHeight: 1440, message: "")
        let readyPayload = Frame.parseSingle(ready)!.1
        expect(readyPayload.count == 1 + 2 + 32 + 16 + 2 + 2, "display ready layout")
        expect(Data(readyPayload[3..<35]) == key && Data(readyPayload[35..<51]) == token, "display ready carries key and token")
        expect(Frame.parseSingle(Frame.displayFailed("x"))!.1.first == 1, "display failure status")

        var sealer = VideoStream.Sealer(key: key)
        let first = sealer.seal(Data("BorderlessMouse video vector".utf8))!
        expect(first == data("34000000000000000000000036871e7baf0e68795351fe3aa757db1c7770d96fcdfc5ca5825e10e56bbecdbf5450f64c9f54fa2c4c9ccc41"),
               "video record vector")
        let second = sealer.seal(Data([1, 2, 3]))!
        var opener = VideoStream.Opener(key: key)
        expect(opener.open(first.dropFirst(4)) == Data("BorderlessMouse video vector".utf8), "video record opens")
        var replay = opener
        expect(replay.open(first.dropFirst(4)) == nil, "replayed video record rejected")
        var tampered = Data(second.dropFirst(4))
        tampered[tampered.count - 1] ^= 1
        expect(opener.open(tampered) == nil, "tampered video record rejected")
        expect(opener.open(second.dropFirst(4)) == Data([1, 2, 3]), "next video record opens")

        let fixtureURL = URL(fileURLWithPath: "tests/fixtures/video.json")
        guard let json = try? JSONSerialization.jsonObject(with: Data(contentsOf: fixtureURL)) as? [String: Any],
              let keyHex = json["keyHex"] as? String, let records = json["records"] as? [String],
              let keyframes = json["keyframes"] as? [Bool] else {
            expect(false, "video fixture readable")
            return
        }
        var fixtureOpener = VideoStream.Opener(key: data(keyHex))
        for (index, hex) in records.enumerated() {
            let record = data(hex)
            let length = Int(record[0]) | Int(record[1]) << 8 | Int(record[2]) << 16 | Int(record[3]) << 24
            expect(length == record.count - 4, "fixture record \(index) length")
            guard let clear = fixtureOpener.open(record.dropFirst(4)), let frame = VideoStream.Frame(decoding: clear) else {
                expect(false, "fixture record \(index) decrypts")
                return
            }
            expect(frame.kind == .h264AccessUnit && frame.width == 160 && frame.height == 96, "fixture frame \(index) header")
            expect(frame.isKeyframe == keyframes[index], "fixture frame \(index) keyframe flag")
            expect(frame.payload.starts(with: [0, 0, 0, 1]), "fixture frame \(index) is Annex B")
        }
    }
}
