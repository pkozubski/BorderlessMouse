import CoreGraphics
import CoreMedia
import CoreVideo
import Foundation
import ScreenCaptureKit

/// Nagrywa jeden monitor przez ScreenCaptureKit. Dostarcza wyłącznie klatki
/// ze zmienioną zawartością (ScreenCaptureKit pomija klatki bezczynne),
/// w formacie NV12 (4:2:0, zakres wideo, BT.709) gotowym dla kodera H.264.
final class DisplayCapture: NSObject, SCStreamOutput, SCStreamDelegate {
    enum CaptureError: LocalizedError {
        case permissionDenied
        case displayNotFound

        var errorDescription: String? {
            switch self {
            case .permissionDenied:
                return L10n.text("Brak zgody na nagrywanie ekranu. Nadaj ją w Ustawieniach systemowych → Prywatność i ochrona → Nagrywanie ekranu i dźwięku.",
                                 "Screen Recording permission is missing. Grant it in System Settings → Privacy & Security → Screen & System Audio Recording.")
            case .displayNotFound:
                return L10n.text("ScreenCaptureKit nie widzi wirtualnego monitora.",
                                 "ScreenCaptureKit cannot see the virtual display.")
            }
        }
    }

    /// Klatka i czas prezentacji. Wywoływane na kolejce przechwytywania.
    var onFrame: ((CVPixelBuffer, CMTime) -> Void)?
    /// Strumień zatrzymany przez system (np. cofnięta zgoda).
    var onStopped: ((Error) -> Void)?

    private let sampleQueue = DispatchQueue(label: "blm.display.capture", qos: .userInteractive)
    private var stream: SCStream?
    /// W trybie okien kursor rysuje Windows (bez opóźnienia wideo), więc Mac go pomija.
    private(set) var showsCursor = true

    static var hasPermission: Bool { CGPreflightScreenCaptureAccess() }

    /// Pokazuje systemowe pytanie o zgodę (tylko raz; później macOS kieruje do Ustawień).
    @discardableResult
    static func requestPermission() -> Bool { CGRequestScreenCaptureAccess() }

    func start(displayID: CGDirectDisplayID, width: Int, height: Int, showsCursor: Bool = true) async throws {
        self.showsCursor = showsCursor
        guard Self.hasPermission else { throw CaptureError.permissionDenied }
        let display = try await Self.findDisplay(displayID)
        let filter = SCContentFilter(display: display, excludingWindows: [])
        let stream = SCStream(filter: filter, configuration: Self.configuration(width: width, height: height, showsCursor: showsCursor), delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: sampleQueue)
        try await stream.startCapture()
        self.stream = stream
    }

    func update(width: Int, height: Int, showsCursor: Bool? = nil) async throws {
        if let showsCursor { self.showsCursor = showsCursor }
        try await stream?.updateConfiguration(Self.configuration(width: width, height: height, showsCursor: self.showsCursor))
    }

    func stop() {
        guard let stream else { return }
        self.stream = nil
        stream.stopCapture { _ in }
    }

    private static func configuration(width: Int, height: Int, showsCursor: Bool) -> SCStreamConfiguration {
        let config = SCStreamConfiguration()
        config.width = width
        config.height = height
        config.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        config.colorMatrix = CGDisplayStream.yCbCrMatrix_ITU_R_709_2
        config.minimumFrameInterval = CMTime(value: 1, timescale: 60)
        config.queueDepth = 5
        config.showsCursor = showsCursor
        config.scalesToFit = true
        return config
    }

    /// Świeżo utworzony monitor pojawia się w ScreenCaptureKit z opóźnieniem.
    private static func findDisplay(_ id: CGDirectDisplayID) async throws -> SCDisplay {
        for attempt in 0..<20 {
            let content: SCShareableContent
            do {
                content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            } catch {
                if !hasPermission { throw CaptureError.permissionDenied }
                throw error
            }
            if let display = content.displays.first(where: { $0.displayID == id }) { return display }
            if attempt < 19 { try await Task.sleep(nanoseconds: 100_000_000) }
        }
        throw CaptureError.displayNotFound
    }

    // MARK: - SCStreamOutput / SCStreamDelegate

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let rawStatus = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: rawStatus) == .complete,
              let pixelBuffer = sampleBuffer.imageBuffer else { return }
        onFrame?(pixelBuffer, sampleBuffer.presentationTimeStamp)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        guard stream === self.stream else { return }
        self.stream = nil
        onStopped?(error)
    }
}
