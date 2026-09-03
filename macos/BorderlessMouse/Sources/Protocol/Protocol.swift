import Foundation

/// Stałe protokołu – muszą być zgodne z PROTOCOL.md i implementacją Windows.
enum ProtocolConstants {
    static let version: UInt8 = 2
    static let defaultControlPort: UInt16 = 47800
    static let discoveryPort: UInt16 = 47801
    static let discoveryRequest = "BLM2?"
    static let discoveryReply = "BLM2!"
    static let audioMagic: UInt16 = 0x4D42
    static let audioVersion: UInt8 = 2
    static let audioHeaderBytes = 32
    static let audioTagBytes = 16
    /// Maksymalny rozmiar długiej ramki (ochrona przed błędnym nadawcą).
    static let maxLongFrame = maxClipboardImageBytes + 1
    /// Zewnętrzna ramka SECURE zawiera zaszyfrowaną ramkę aplikacyjną i narzut AEAD.
    static let maxWireFrame = maxLongFrame + 64
    /// Limit tekstu schowka.
    static let maxClipboardBytes = 1024 * 1024
    static let maxClipboardImageBytes = 32 * 1024 * 1024
    static let maxClipboardImagePixels = 64 * 1024 * 1024
}

enum MessageType: UInt8 {
    case hello = 0x01
    case challenge = 0x02
    case authenticate = 0x03
    case secure = 0x04
    case reject = 0x05
    case ready = 0x06
    case mouseMove = 0x10
    case mouseButton = 0x11
    case mouseWheel = 0x12
    case key = 0x20
    case releaseAll = 0x21
    case enter = 0x30
    case leave = 0x31
    case audioStart = 0x40
    case audioStop = 0x41
    case audioFormat = 0x42
    case ping = 0x50
    case pong = 0x51
    case status = 0x60
    case clipboard = 0x70
}

enum ClipboardFormat: UInt8 {
    case utf8Text = 0
    case png = 1
}

enum ScreenEdge: UInt8, CaseIterable {
    case left = 0, right = 1, top = 2, bottom = 3

    var opposite: ScreenEdge {
        switch self {
        case .left: return .right
        case .right: return .left
        case .top: return .bottom
        case .bottom: return .top
        }
    }

    var localizedName: String {
        switch self {
        case .left: return "lewej"
        case .right: return "prawej"
        case .top: return "górnej"
        case .bottom: return "dolnej"
        }
    }
}

enum AudioSampleFormat: UInt8 {
    case int16 = 0
    case float32 = 1
}

struct StatusFlags: OptionSet {
    let rawValue: UInt8
    static let accessibilityGranted = StatusFlags(rawValue: 1 << 0)
    static let audioCapturing = StatusFlags(rawValue: 1 << 1)
    static let cursorOnMac = StatusFlags(rawValue: 1 << 2)
}

/// Prosty czytnik little-endian po tablicy bajtów.
struct ByteReader {
    private let bytes: [UInt8]
    private(set) var offset = 0

    init(_ data: Data) { bytes = [UInt8](data) }
    init(_ bytes: [UInt8]) { self.bytes = bytes }

    var remaining: Int { bytes.count - offset }

    mutating func u8() -> UInt8? {
        guard remaining >= 1 else { return nil }
        defer { offset += 1 }
        return bytes[offset]
    }

    mutating func u16() -> UInt16? {
        guard remaining >= 2 else { return nil }
        let v = UInt16(bytes[offset]) | (UInt16(bytes[offset + 1]) << 8)
        offset += 2
        return v
    }

    mutating func i16() -> Int16? {
        guard let v = u16() else { return nil }
        return Int16(bitPattern: v)
    }

    mutating func u32() -> UInt32? {
        guard remaining >= 4 else { return nil }
        var v: UInt32 = 0
        for i in 0..<4 { v |= UInt32(bytes[offset + i]) << (8 * UInt32(i)) }
        offset += 4
        return v
    }

    mutating func u64() -> UInt64? {
        guard remaining >= 8 else { return nil }
        var v: UInt64 = 0
        for i in 0..<8 { v |= UInt64(bytes[offset + i]) << (8 * UInt64(i)) }
        offset += 8
        return v
    }

    mutating func f32() -> Float? {
        guard let bits = u32() else { return nil }
        return Float(bitPattern: bits)
    }

    mutating func restAsString() -> String {
        let s = String(decoding: bytes[offset...], as: UTF8.self)
        offset = bytes.count
        return s
    }
}

/// Prosty zapis little-endian.
struct ByteWriter {
    private(set) var bytes: [UInt8] = []

    mutating func u8(_ v: UInt8) { bytes.append(v) }
    mutating func u16(_ v: UInt16) { bytes.append(UInt8(v & 0xFF)); bytes.append(UInt8(v >> 8)) }
    mutating func i16(_ v: Int16) { u16(UInt16(bitPattern: v)) }
    mutating func u32(_ v: UInt32) { for i in 0..<4 { bytes.append(UInt8((v >> (8 * UInt32(i))) & 0xFF)) } }
    mutating func u64(_ v: UInt64) { for i in 0..<8 { bytes.append(UInt8((v >> (8 * UInt64(i))) & 0xFF)) } }
    mutating func f32(_ v: Float) { u32(v.bitPattern) }
    mutating func string(_ s: String) { bytes.append(contentsOf: Array(s.utf8)) }
    mutating func raw(_ data: [UInt8]) { bytes.append(contentsOf: data) }
}

/// Buduje kompletną ramkę: typ, długość, payload. Payloady ≥ 255 bajtów idą
/// jako długie ramki (`len = 0xFF` + `u32 length`).
enum Frame {
    static func make(_ type: MessageType, _ payload: [UInt8] = []) -> Data {
        precondition(payload.count <= ProtocolConstants.maxWireFrame, "payload too large")
        var out = [UInt8]()
        if payload.count < 255 {
            out.reserveCapacity(payload.count + 2)
            out.append(type.rawValue)
            out.append(UInt8(payload.count))
        } else {
            out.reserveCapacity(payload.count + 6)
            out.append(type.rawValue)
            out.append(0xFF)
            let n = UInt32(payload.count)
            out.append(UInt8(n & 0xFF))
            out.append(UInt8((n >> 8) & 0xFF))
            out.append(UInt8((n >> 16) & 0xFF))
            out.append(UInt8((n >> 24) & 0xFF))
        }
        out.append(contentsOf: payload)
        return Data(out)
    }

    static func clipboard(_ content: ClipboardContent) -> Data {
        make(.clipboard, [content.format.rawValue] + content.data)
    }

    static func hello(name: String, nonce: Data) -> Data {
        precondition(nonce.count == ControlCrypto.nonceBytes)
        var w = ByteWriter()
        w.u8(ProtocolConstants.version)
        w.raw(Array(nonce))
        w.string(String(decoding: name.utf8.prefix(200), as: UTF8.self))
        return make(.hello, w.bytes)
    }

    static func challenge(name: String, nonce: Data, proof: Data) -> Data {
        precondition(nonce.count == ControlCrypto.nonceBytes && proof.count == ControlCrypto.proofBytes)
        var w = ByteWriter()
        w.u8(ProtocolConstants.version)
        w.raw(Array(nonce))
        w.raw(Array(proof))
        w.string(String(decoding: name.utf8.prefix(200), as: UTF8.self))
        return make(.challenge, w.bytes)
    }

    static func authenticate(_ proof: Data) -> Data {
        precondition(proof.count == ControlCrypto.proofBytes)
        return make(.authenticate, Array(proof))
    }

    static func secure(_ envelope: Data) -> Data { make(.secure, Array(envelope)) }

    static func ready(name: String) -> Data {
        var w = ByteWriter()
        w.u8(ProtocolConstants.version)
        w.string(String(decoding: name.utf8.prefix(200), as: UTF8.self))
        return make(.ready, w.bytes)
    }

    static func reject(_ reason: String) -> Data {
        make(.reject, Array(String(decoding: reason.utf8.prefix(200), as: UTF8.self).utf8))
    }

    static func leave(edge: ScreenEdge, ratio: Float) -> Data {
        var w = ByteWriter()
        w.u8(edge.rawValue)
        w.f32(ratio)
        return make(.leave, w.bytes)
    }

    static func audioFormat(sampleRate: UInt32, channels: UInt8, format: AudioSampleFormat, status: UInt8, message: String) -> Data {
        var w = ByteWriter()
        w.u32(sampleRate)
        w.u8(channels)
        w.u8(format.rawValue)
        w.u8(status)
        w.string(String(message.utf8.prefix(200))!)
        return make(.audioFormat, w.bytes)
    }

    static func status(_ flags: StatusFlags) -> Data {
        make(.status, [flags.rawValue])
    }

    static func pong(_ payload: [UInt8]) -> Data { make(.pong, payload) }

    static func ping(_ ts: UInt64) -> Data {
        var w = ByteWriter()
        w.u64(ts)
        return make(.ping, w.bytes)
    }

    /// Odszyfrowana koperta zawsze zawiera dokładnie jedną kompletną ramkę.
    static func parseSingle(_ data: Data) -> (MessageType, [UInt8])? {
        let bytes = [UInt8](data)
        guard bytes.count >= 2, let type = MessageType(rawValue: bytes[0]) else { return nil }
        var length = Int(bytes[1])
        var header = 2
        if length == 0xFF {
            guard bytes.count >= 6 else { return nil }
            length = Int(bytes[2]) | (Int(bytes[3]) << 8) | (Int(bytes[4]) << 16) | (Int(bytes[5]) << 24)
            header = 6
        }
        guard length <= ProtocolConstants.maxLongFrame, bytes.count == header + length else { return nil }
        return (type, Array(bytes[header...]))
    }
}
