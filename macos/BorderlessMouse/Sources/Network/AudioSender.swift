import CryptoKit
import Darwin
import Foundation

/// Wysyła uwierzytelnione pakiety audio UDP do Windowsa. Każdy pakiet ma
/// odrębny nonce i tag AES-256-GCM; nagłówek jest chroniony jako AAD.
final class AudioSender {
    private var fd: Int32 = -1
    private var destination = sockaddr_in()
    private var sequence: UInt16 = 0
    private var frameIndex: UInt32 = 0
    private var counter: UInt64 = 0
    private let key: SymmetricKey
    private let sessionID: UInt64

    let channels: Int
    let format: AudioSampleFormat
    let maxFramesPerPacket: Int

    private(set) var packetsSent: UInt64 = 0
    private(set) var sendErrors: UInt64 = 0

    init(host: String, port: UInt16, channels: Int, format: AudioSampleFormat,
         key: Data, sessionID: UInt64) throws {
        guard key.count == 32, (1...8).contains(channels) else { throw POSIXError(.EINVAL) }
        self.channels = channels
        self.format = format
        self.key = SymmetricKey(data: key)
        self.sessionID = sessionID
        let bytesPerFrame = channels * (format == .int16 ? 2 : 4)
        maxFramesPerPacket = max(1, (1400 - ProtocolConstants.audioHeaderBytes - ProtocolConstants.audioTagBytes) / bytesPerFrame)

        let socket = Darwin.socket(AF_INET, SOCK_DGRAM, 0)
        guard socket >= 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
        let flags = fcntl(socket, F_GETFL, 0)
        _ = fcntl(socket, F_SETFL, flags | O_NONBLOCK)
        var sendBuffer: Int32 = 1 << 20
        setsockopt(socket, SOL_SOCKET, SO_SNDBUF, &sendBuffer, socklen_t(MemoryLayout<Int32>.size))
        var typeOfService: Int32 = 46 << 2
        setsockopt(socket, IPPROTO_IP, IP_TOS, &typeOfService, socklen_t(MemoryLayout<Int32>.size))

        destination.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        destination.sin_family = sa_family_t(AF_INET)
        destination.sin_port = port.bigEndian
        guard inet_pton(AF_INET, host, &destination.sin_addr) == 1 else {
            Darwin.close(socket)
            throw POSIXError(.EINVAL)
        }
        fd = socket
    }

    deinit { close() }

    func close() {
        if fd >= 0 { Darwin.close(fd); fd = -1 }
    }

    func send(int16Samples samples: UnsafePointer<Int16>, frames: Int) {
        guard fd >= 0, format == .int16, frames > 0 else { return }
        var offset = 0
        while offset < frames {
            let count = min(maxFramesPerPacket, frames - offset)
            let byteCount = count * channels * 2
            let clear = Data(bytes: samples.advanced(by: offset * channels), count: byteCount)
            guard let packet = makePacket(clear: clear, frames: count) else {
                sendErrors &+= 1
                return
            }
            transmit(packet)
            sequence &+= 1
            frameIndex &+= UInt32(count)
            counter &+= 1
            offset += count
        }
    }

    private func makePacket(clear: Data, frames: Int) -> Data? {
        guard counter != UInt64.max else { return nil }
        var header = [UInt8](repeating: 0, count: ProtocolConstants.audioHeaderBytes)
        Self.put(ProtocolConstants.audioMagic, into: &header, at: 0)
        header[2] = ProtocolConstants.audioVersion
        header[3] = 0
        Self.put(sessionID, into: &header, at: 4)
        Self.put(counter, into: &header, at: 12)
        Self.put(sequence, into: &header, at: 20)
        Self.put(UInt16(frames), into: &header, at: 22)
        header[24] = UInt8(channels)
        header[25] = format.rawValue
        Self.put(UInt16(0), into: &header, at: 26)
        Self.put(frameIndex, into: &header, at: 28)
        let aad = Data(header)
        do {
            let sealed = try AES.GCM.seal(clear,
                                          using: key,
                                          nonce: try AES.GCM.Nonce(data: nonce(counter)),
                                          authenticating: aad)
            var packet = aad
            packet.append(sealed.ciphertext)
            packet.append(sealed.tag)
            return packet
        } catch {
            return nil
        }
    }

    private func nonce(_ value: UInt64) -> Data {
        var id = sessionID.littleEndian
        var ctr = value.littleEndian
        var data = withUnsafeBytes(of: &id) { Data($0.prefix(4)) }
        data.append(withUnsafeBytes(of: &ctr) { Data($0) })
        return data
    }

    private func transmit(_ packet: Data) {
        let sent = packet.withUnsafeBytes { raw -> Int in
            withUnsafePointer(to: &destination) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    sendto(fd, raw.baseAddress, packet.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
        if sent == packet.count { packetsSent &+= 1 } else { sendErrors &+= 1 }
    }

    private static func put<T: FixedWidthInteger>(_ value: T, into bytes: inout [UInt8], at offset: Int) {
        var little = value.littleEndian
        withUnsafeBytes(of: &little) { raw in
            bytes.replaceSubrange(offset..<(offset + raw.count), with: raw)
        }
    }
}
