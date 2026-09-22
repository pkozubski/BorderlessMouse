import CoreMedia
import CoreVideo
import Foundation
import VideoToolbox

/// Sprzętowy koder H.264 (VideoToolbox) w trybie niskiego opóźnienia: bez
/// klatek B, każda klatka wychodzi od razu. Wynik to strumień Annex B
/// (start code 00 00 00 01), a klatki kluczowe zawierają SPS i PPS –
/// tego oczekuje dekoder Media Foundation po stronie Windows.
final class VideoEncoder {
    struct EncodedFrame {
        let annexB: Data
        let isKeyframe: Bool
        let width: Int
        let height: Int
        let presentationTime: CMTime
    }

    enum EncoderError: LocalizedError {
        case osStatus(OSStatus, String)

        var errorDescription: String? {
            switch self {
            case let .osStatus(status, what):
                return L10n.text("Koder wideo: \(what) (błąd \(status))", "Video encoder: \(what) (error \(status))")
            }
        }
    }

    let width: Int
    let height: Int
    let bitrate: Int
    /// Wywoływane na wątku VideoToolbox.
    var onFrame: ((EncodedFrame) -> Void)?
    var onError: ((OSStatus) -> Void)?

    private var session: VTCompressionSession?
    private let lock = NSLock()
    private var forceKeyframe = true
    /// Szereguje wywołania kodera (klatki z ScreenCaptureKit i powtórki klatki kluczowej).
    private let encodeLock = NSLock()
    private var lastPresentationTime = CMTime.invalid

    /// Bitrate w bitach na sekundę; 0 = automatycznie z rozdzielczości.
    init(width: Int, height: Int, bitrate: Int = 0) throws {
        self.width = width
        self.height = height
        self.bitrate = bitrate > 0 ? bitrate : Self.automaticBitrate(width: width, height: height)
        session = try Self.makeSession(width: width, height: height, bitrate: self.bitrate)
    }

    deinit { invalidate() }

    /// Około 0,1 bita na piksel przy 60 kl./s – treść ekranu jest zwykle statyczna,
    /// więc realny ruch jest dużo mniejszy.
    static func automaticBitrate(width: Int, height: Int) -> Int {
        let estimate = Double(width * height) * 60 * 0.1
        return Int(min(max(estimate, 8_000_000), 60_000_000))
    }

    func requestKeyframe() {
        lock.lock()
        forceKeyframe = true
        lock.unlock()
    }

    func encode(_ pixelBuffer: CVPixelBuffer, presentationTime: CMTime) {
        encodeLock.lock()
        defer { encodeLock.unlock() }
        lock.lock()
        let key = forceKeyframe
        forceKeyframe = false
        let session = self.session
        lock.unlock()
        guard let session else { return }
        // Bez zmiany kolejności klatek czasy muszą rosnąć – powtórka ostatniej
        // klatki może się minąć ze świeżą klatką z ScreenCaptureKit.
        var presentationTime = presentationTime
        if lastPresentationTime.isValid, CMTimeCompare(presentationTime, lastPresentationTime) <= 0 {
            presentationTime = CMTimeAdd(lastPresentationTime, CMTime(value: 1, timescale: 600))
        }
        lastPresentationTime = presentationTime
        let properties: CFDictionary? = key ? [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary : nil
        let status = VTCompressionSessionEncodeFrame(session, imageBuffer: pixelBuffer,
                                                     presentationTimeStamp: presentationTime,
                                                     duration: .invalid,
                                                     frameProperties: properties,
                                                     infoFlagsOut: nil) { [weak self] status, _, sampleBuffer in
            self?.handleOutput(status: status, sampleBuffer: sampleBuffer)
        }
        if status != noErr {
            if key { requestKeyframe() }
            onError?(status)
        }
    }

    func invalidate() {
        lock.lock()
        let session = self.session
        self.session = nil
        lock.unlock()
        guard let session else { return }
        VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
        VTCompressionSessionInvalidate(session)
    }

    // MARK: - Private

    private func handleOutput(status: OSStatus, sampleBuffer: CMSampleBuffer?) {
        guard status == noErr else {
            requestKeyframe()
            onError?(status)
            return
        }
        guard let sampleBuffer, let frame = Self.annexB(from: sampleBuffer, width: width, height: height) else { return }
        onFrame?(frame)
    }

    private static func makeSession(width: Int, height: Int, bitrate: Int) throws -> VTCompressionSession {
        // Najpierw tryb niskiego opóźnienia (macOS 11.3+); jeśli sprzęt go nie ma – zwykły tryb realtime.
        let specifications: [[CFString: Any]] = [
            [kVTVideoEncoderSpecification_EnableLowLatencyRateControl: true],
            [kVTVideoEncoderSpecification_EnableHardwareAcceleratedVideoEncoder: true],
            [:],
        ]
        var lastStatus: OSStatus = noErr
        for (index, spec) in specifications.enumerated() {
            var session: VTCompressionSession?
            lastStatus = VTCompressionSessionCreate(allocator: nil, width: Int32(width), height: Int32(height),
                                                    codecType: kCMVideoCodecType_H264,
                                                    encoderSpecification: spec as CFDictionary,
                                                    imageBufferAttributes: nil,
                                                    compressedDataAllocator: nil,
                                                    outputCallback: nil, refcon: nil,
                                                    compressionSessionOut: &session)
            guard lastStatus == noErr, let session else { continue }
            configure(session, bitrate: bitrate, lowLatency: index == 0)
            lastStatus = VTCompressionSessionPrepareToEncodeFrames(session)
            if lastStatus == noErr { return session }
            VTCompressionSessionInvalidate(session)
        }
        throw EncoderError.osStatus(lastStatus, L10n.text("nie można utworzyć sesji H.264", "cannot create an H.264 session"))
    }

    private static func configure(_ session: VTCompressionSession, bitrate: Int, lowLatency: Bool) {
        func set(_ key: CFString, _ value: Any) {
            VTSessionSetProperty(session, key: key, value: value as CFTypeRef)
        }
        set(kVTCompressionPropertyKey_RealTime, kCFBooleanTrue!)
        set(kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse!)
        set(kVTCompressionPropertyKey_ProfileLevel, lowLatency
            ? kVTProfileLevel_H264_ConstrainedHigh_AutoLevel
            : kVTProfileLevel_H264_High_AutoLevel)
        set(kVTCompressionPropertyKey_H264EntropyMode, kVTH264EntropyMode_CABAC)
        set(kVTCompressionPropertyKey_AverageBitRate, bitrate as CFNumber)
        // Krótkie skoki (np. przewijanie) do 1,5× średniej w każdej sekundzie.
        set(kVTCompressionPropertyKey_DataRateLimits, [bitrate * 3 / 16, 1] as CFArray)
        set(kVTCompressionPropertyKey_ExpectedFrameRate, 60 as CFNumber)
        set(kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, 10 as CFNumber)
        set(kVTCompressionPropertyKey_ColorPrimaries, kCVImageBufferColorPrimaries_ITU_R_709_2)
        set(kVTCompressionPropertyKey_TransferFunction, kCVImageBufferTransferFunction_ITU_R_709_2)
        set(kVTCompressionPropertyKey_YCbCrMatrix, kCVImageBufferYCbCrMatrix_ITU_R_709_2)
        if #available(macOS 12.0, *) {
            set(kVTCompressionPropertyKey_PrioritizeEncodingSpeedOverQuality, kCFBooleanTrue!)
        }
    }

    /// AVCC (długości NAL) → Annex B, z SPS/PPS przed klatką kluczową.
    static func annexB(from sampleBuffer: CMSampleBuffer, width: Int, height: Int) -> EncodedFrame? {
        guard let block = sampleBuffer.dataBuffer, let format = sampleBuffer.formatDescription else { return nil }
        var isKeyframe = true
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[CFString: Any]],
           let notSync = attachments.first?[kCMSampleAttachmentKey_NotSync] as? Bool {
            isKeyframe = !notSync
        }
        let startCode: [UInt8] = [0, 0, 0, 1]
        var output = Data()
        output.reserveCapacity(CMBlockBufferGetDataLength(block) + 128)

        var nalLengthSize: Int32 = 4
        var parameterSetCount = 0
        CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, parameterSetIndex: 0, parameterSetPointerOut: nil,
                                                           parameterSetSizeOut: nil, parameterSetCountOut: &parameterSetCount,
                                                           nalUnitHeaderLengthOut: &nalLengthSize)
        if isKeyframe {
            for index in 0..<parameterSetCount {
                var pointer: UnsafePointer<UInt8>?
                var size = 0
                guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, parameterSetIndex: index,
                                                                         parameterSetPointerOut: &pointer,
                                                                         parameterSetSizeOut: &size,
                                                                         parameterSetCountOut: nil,
                                                                         nalUnitHeaderLengthOut: nil) == noErr,
                      let pointer else { return nil }
                output.append(contentsOf: startCode)
                output.append(pointer, count: size)
            }
        }

        let total = CMBlockBufferGetDataLength(block)
        var bytes = [UInt8](repeating: 0, count: total)
        guard CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: total, destination: &bytes) == noErr else { return nil }
        let lengthSize = Int(nalLengthSize)
        guard (1...4).contains(lengthSize) else { return nil }
        var offset = 0
        while offset + lengthSize <= total {
            var nalLength = 0
            for i in 0..<lengthSize { nalLength = (nalLength << 8) | Int(bytes[offset + i]) }
            offset += lengthSize
            guard nalLength > 0, offset + nalLength <= total else { return nil }
            output.append(contentsOf: startCode)
            output.append(contentsOf: bytes[offset..<(offset + nalLength)])
            offset += nalLength
        }
        return EncodedFrame(annexB: output, isKeyframe: isKeyframe, width: width, height: height,
                            presentationTime: sampleBuffer.presentationTimeStamp)
    }
}
