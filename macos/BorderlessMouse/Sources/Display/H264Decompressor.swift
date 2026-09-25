import CoreMedia
import CoreVideo
import Foundation
import VideoToolbox

/// Dekoduje H.264 Annex B (strumień z Windowsa) przez VideoToolbox do buforów BGRA
/// opartych na IOSurface – gotowych do pokazania jako `CALayer.contents`.
/// Nie jest bezpieczny wątkowo: używać z jednej kolejki.
final class H264Decompressor {
    enum DecodeError: Error {
        case missingParameterSets
        case formatDescription(OSStatus)
        case session(OSStatus)
        case decode(OSStatus)
    }

    private var session: VTDecompressionSession?
    private var format: CMVideoFormatDescription?
    private var sps: Data?
    private var pps: Data?

    deinit { invalidate() }

    func invalidate() {
        if let session {
            VTDecompressionSessionWaitForAsynchronousFrames(session)
            VTDecompressionSessionInvalidate(session)
        }
        session = nil
        format = nil
    }

    /// Dekoduje jedną jednostkę dostępu; zwraca obraz albo nil, gdy dekoder jeszcze czeka
    /// (np. brak klatki kluczowej). Rzuca przy uszkodzonym strumieniu – wtedy trzeba
    /// poprosić o klatkę kluczową.
    func decode(_ accessUnit: Data) throws -> CVPixelBuffer? {
        let units = Self.nalUnits(in: accessUnit)
        var slices: [Data] = []
        var newSPS: Data?, newPPS: Data?
        for unit in units {
            guard let header = unit.first else { continue }
            switch header & 0x1F {
            case 7: newSPS = unit
            case 8: newPPS = unit
            case 9, 12: break // separator jednostek, wypełnienie
            default: slices.append(unit)
            }
        }
        if let newSPS, let newPPS, newSPS != sps || newPPS != pps || session == nil {
            try configure(sps: newSPS, pps: newPPS)
        }
        guard let session, let format else {
            if slices.isEmpty { return nil }
            throw DecodeError.missingParameterSets
        }
        guard !slices.isEmpty else { return nil }

        var avcc = Data()
        for slice in slices {
            var length = UInt32(slice.count).bigEndian
            avcc.append(Data(bytes: &length, count: 4))
            avcc.append(slice)
        }
        var block: CMBlockBuffer?
        var status = CMBlockBufferCreateWithMemoryBlock(allocator: kCFAllocatorDefault, memoryBlock: nil,
                                                        blockLength: avcc.count, blockAllocator: kCFAllocatorDefault,
                                                        customBlockSource: nil, offsetToData: 0, dataLength: avcc.count,
                                                        flags: 0, blockBufferOut: &block)
        guard status == noErr, let block else { throw DecodeError.decode(status) }
        status = avcc.withUnsafeBytes {
            CMBlockBufferReplaceDataBytes(with: $0.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: avcc.count)
        }
        guard status == noErr else { throw DecodeError.decode(status) }
        var sample: CMSampleBuffer?
        var size = avcc.count
        status = CMSampleBufferCreateReady(allocator: kCFAllocatorDefault, dataBuffer: block, formatDescription: format,
                                           sampleCount: 1, sampleTimingEntryCount: 0, sampleTimingArray: nil,
                                           sampleSizeEntryCount: 1, sampleSizeArray: &size, sampleBufferOut: &sample)
        guard status == noErr, let sample else { throw DecodeError.decode(status) }

        var output: CVPixelBuffer?
        var decodeStatus: OSStatus = noErr
        status = VTDecompressionSessionDecodeFrame(session, sampleBuffer: sample, flags: [], infoFlagsOut: nil) { result, _, image, _, _ in
            decodeStatus = result
            output = image
        }
        guard status == noErr else {
            if status == kVTInvalidSessionErr { invalidate() }
            throw DecodeError.decode(status)
        }
        guard decodeStatus == noErr else { throw DecodeError.decode(decodeStatus) }
        return output
    }

    private func configure(sps: Data, pps: Data) throws {
        invalidate()
        var description: CMFormatDescription?
        let status = sps.withUnsafeBytes { spsBytes in
            pps.withUnsafeBytes { ppsBytes in
                let pointers = [spsBytes.bindMemory(to: UInt8.self).baseAddress!, ppsBytes.bindMemory(to: UInt8.self).baseAddress!]
                let sizes = [sps.count, pps.count]
                return CMVideoFormatDescriptionCreateFromH264ParameterSets(allocator: kCFAllocatorDefault, parameterSetCount: 2,
                                                                           parameterSetPointers: pointers, parameterSetSizes: sizes,
                                                                           nalUnitHeaderLength: 4, formatDescriptionOut: &description)
            }
        }
        guard status == noErr, let description else { throw DecodeError.formatDescription(status) }
        let attributes: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_32BGRA,
            kCVPixelBufferIOSurfacePropertiesKey: [:] as [CFString: Any],
            kCVPixelBufferMetalCompatibilityKey: true,
        ]
        let specification: [CFString: Any] = [
            kVTVideoDecoderSpecification_EnableHardwareAcceleratedVideoDecoder: true,
        ]
        var created: VTDecompressionSession?
        let sessionStatus = VTDecompressionSessionCreate(allocator: kCFAllocatorDefault, formatDescription: description,
                                                         decoderSpecification: specification as CFDictionary,
                                                         imageBufferAttributes: attributes as CFDictionary,
                                                         outputCallback: nil, decompressionSessionOut: &created)
        guard sessionStatus == noErr, let created else { throw DecodeError.session(sessionStatus) }
        VTSessionSetProperty(created, key: kVTDecompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        session = created
        format = description
        self.sps = sps
        self.pps = pps
    }

    /// Jednostki NAL (bez kodów startu) ze strumienia Annex B.
    static func nalUnits(in data: Data) -> [Data] {
        let bytes = [UInt8](data)
        var starts: [(start: Int, payload: Int)] = []
        var i = 0
        while i + 2 < bytes.count {
            if bytes[i] == 0, bytes[i + 1] == 0 {
                if bytes[i + 2] == 1 {
                    starts.append((i, i + 3))
                    i += 3
                    continue
                }
                if i + 3 < bytes.count, bytes[i + 2] == 0, bytes[i + 3] == 1 {
                    starts.append((i, i + 4))
                    i += 4
                    continue
                }
            }
            i += 1
        }
        var units: [Data] = []
        for (index, entry) in starts.enumerated() {
            var end = index + 1 < starts.count ? starts[index + 1].start : bytes.count
            // Zera kończące (trailing_zero_8bits) nie należą do jednostki.
            while end > entry.payload, bytes[end - 1] == 0 { end -= 1 }
            if end > entry.payload { units.append(Data(bytes[entry.payload..<end])) }
        }
        return units
    }
}
