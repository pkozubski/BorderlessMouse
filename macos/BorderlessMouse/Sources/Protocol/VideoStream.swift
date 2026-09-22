import CryptoKit
import Foundation
import Security

/// Zaszyfrowany kanał wideo ekranu wirtualnego (osobne połączenie TCP).
///
/// Rekord na łączu: `u32 length` + `u64 counter` + szyfrogram + `16 B tag`,
/// gdzie `length` obejmuje licznik, szyfrogram i tag. Nonce i AAD to
/// `"BLMV" || counter`. Klucz i token są losowe dla każdego strumienia
/// i trafiają do Windowsa wyłącznie przez zaszyfrowany kanał sterowania.
enum VideoStream {
    static let keyBytes = 32
    static let tokenBytes = 16
    static let counterBytes = 8
    static let tagBytes = 16
    static let lengthBytes = 4
    /// kind, flags, width, height, captureMicros
    static let frameHeaderBytes = 14
    /// Klatka okna: dodatkowo `u32 streamID`, `u16 cornerRadius` (piksele).
    static let windowFrameHeaderBytes = 20
    static let maxRecordBytes = 16 * 1024 * 1024

    enum Kind: UInt8 {
        /// Cały ekran wirtualny.
        case h264AccessUnit = 1
        /// Jedno okno Maca (osobny strumień H.264 na okno).
        case windowAccessUnit = 2
    }

    struct FrameFlags: OptionSet {
        let rawValue: UInt8
        static let keyframe = FrameFlags(rawValue: 1 << 0)
    }

    struct Frame: Equatable {
        let kind: Kind
        let flags: FrameFlags
        let width: Int
        let height: Int
        let captureMicros: UInt64
        let payload: Data
        /// Tylko `windowAccessUnit`: identyfikator okna (CGWindowID) albo paska menu.
        let streamID: UInt32
        let cornerRadius: UInt16

        var isKeyframe: Bool { flags.contains(.keyframe) }

        func encoded() -> Data {
            var w = ByteWriter()
            w.u8(kind.rawValue)
            w.u8(flags.rawValue)
            w.u16(UInt16(clamping: width))
            w.u16(UInt16(clamping: height))
            w.u64(captureMicros)
            if kind == .windowAccessUnit {
                w.u32(streamID)
                w.u16(cornerRadius)
            }
            var data = Data(w.bytes)
            data.append(payload)
            return data
        }

        init(kind: Kind, flags: FrameFlags, width: Int, height: Int, captureMicros: UInt64, payload: Data,
             streamID: UInt32 = 0, cornerRadius: UInt16 = 0) {
            self.kind = kind
            self.flags = flags
            self.width = width
            self.height = height
            self.captureMicros = captureMicros
            self.payload = payload
            self.streamID = streamID
            self.cornerRadius = cornerRadius
        }

        init?(decoding data: Data) {
            guard let first = data.first, let kind = Kind(rawValue: first) else { return nil }
            let headerBytes = kind == .windowAccessUnit ? VideoStream.windowFrameHeaderBytes : VideoStream.frameHeaderBytes
            guard data.count > headerBytes else { return nil }
            var r = ByteReader([UInt8](data.prefix(headerBytes)))
            _ = r.u8()
            guard let flags = r.u8(), let width = r.u16(), let height = r.u16(),
                  let micros = r.u64(), width > 0, height > 0 else { return nil }
            var streamID: UInt32 = 0
            var radius: UInt16 = 0
            if kind == .windowAccessUnit {
                guard let id = r.u32(), let corner = r.u16() else { return nil }
                streamID = id
                radius = corner
            }
            self.init(kind: kind, flags: FrameFlags(rawValue: flags), width: Int(width), height: Int(height),
                      captureMicros: micros, payload: Data(data.dropFirst(headerBytes)),
                      streamID: streamID, cornerRadius: radius)
        }
    }

    static func randomBytes(_ count: Int) -> Data {
        var data = Data(count: count)
        let status = data.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, count, $0.baseAddress!) }
        precondition(status == errSecSuccess, "Nie można wygenerować losowych bajtów")
        return data
    }

    static func nonce(_ counter: UInt64) -> Data {
        Data("BLMV".utf8) + littleEndian(counter)
    }

    static func littleEndian(_ value: UInt64) -> Data {
        var v = value.littleEndian
        return withUnsafeBytes(of: &v) { Data($0) }
    }

    /// Szyfruje kolejne rekordy. Nie jest bezpieczny wątkowo – używać z jednej kolejki.
    struct Sealer {
        private let key: SymmetricKey
        private(set) var counter: UInt64 = 0

        init(key: Data) {
            precondition(key.count == VideoStream.keyBytes)
            self.key = SymmetricKey(data: key)
        }

        /// Zwraca kompletny rekord z prefiksem długości.
        mutating func seal(_ plaintext: Data) -> Data? {
            guard counter != .max,
                  plaintext.count + VideoStream.counterBytes + VideoStream.tagBytes <= VideoStream.maxRecordBytes else { return nil }
            let counterData = VideoStream.littleEndian(counter)
            guard let sealed = try? AES.GCM.seal(plaintext, using: key,
                                                 nonce: AES.GCM.Nonce(data: VideoStream.nonce(counter)),
                                                 authenticating: VideoStream.nonce(counter)) else { return nil }
            counter &+= 1
            let body = counterData.count + sealed.ciphertext.count + sealed.tag.count
            var record = Data(capacity: VideoStream.lengthBytes + body)
            var length = UInt32(body).littleEndian
            record.append(Data(bytes: &length, count: 4))
            record.append(counterData)
            record.append(sealed.ciphertext)
            record.append(sealed.tag)
            return record
        }
    }

    /// Strona odbiorcza (testy i samotest); Windows ma własną implementację.
    struct Opener {
        private let key: SymmetricKey
        private var expected: UInt64 = 0

        init(key: Data) {
            precondition(key.count == VideoStream.keyBytes)
            self.key = SymmetricKey(data: key)
        }

        /// `body` = rekord bez prefiksu długości. Licznik musi rosnąć o jeden (TCP).
        mutating func open(_ body: Data) -> Data? {
            guard body.count >= VideoStream.counterBytes + VideoStream.tagBytes else { return nil }
            let bytes = [UInt8](body)
            var counter: UInt64 = 0
            for i in 0..<8 { counter |= UInt64(bytes[i]) << (8 * UInt64(i)) }
            guard counter == expected,
                  let box = try? AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: VideoStream.nonce(counter)),
                                                   ciphertext: Data(bytes[8..<(bytes.count - VideoStream.tagBytes)]),
                                                   tag: Data(bytes.suffix(VideoStream.tagBytes))),
                  let clear = try? AES.GCM.open(box, using: key, authenticating: VideoStream.nonce(counter)) else { return nil }
            expected &+= 1
            return clear
        }
    }
}
