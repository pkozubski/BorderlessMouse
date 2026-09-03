import AppKit
import Foundation
import ImageIO

/// Obserwuje NSPasteboard (po `changeCount`, co 0,5 s) i pozwala ustawić
/// schowek z zewnątrz bez odsyłania własnej zmiany z powrotem.
final class ClipboardSync {
    /// Wołane na wątku głównym.
    var onLocalChange: ((ClipboardContent) -> Void)?
    var onApplied: ((ClipboardContent) -> Void)?
    var onError: ((String) -> Void)?

    private let pasteboard: NSPasteboard
    private var timer: DispatchSourceTimer?
    private var lastChangeCount: Int
    private var lastContent: ClipboardContent?

    init(pasteboard: NSPasteboard = .general) {
        self.pasteboard = pasteboard
        lastChangeCount = pasteboard.changeCount
    }

    func start() {
        DispatchQueue.main.async {
            guard self.timer == nil else { return }
            self.lastChangeCount = self.pasteboard.changeCount
            let t = DispatchSource.makeTimerSource(queue: .main)
            t.schedule(deadline: .now() + 0.5, repeating: 0.5, leeway: .milliseconds(100))
            t.setEventHandler { [weak self] in self?.poll() }
            t.resume()
            self.timer = t
        }
    }

    func stop() {
        DispatchQueue.main.async {
            self.timer?.cancel()
            self.timer = nil
        }
    }

    /// Obraz udostępniamy jako PNG i TIFF, aby obsłużyć także aplikacje oczekujące TIFF.
    func apply(_ content: ClipboardContent) {
        DispatchQueue.main.async {
            let item = NSPasteboardItem()
            switch content.format {
            case .utf8Text:
                guard let text = content.text else { return }
                item.setString(text, forType: .string)
            case .png:
                guard let bitmap = NSBitmapImageRep(data: content.data),
                      let tiff = bitmap.tiffRepresentation else {
                    self.onError?(L10n.text("Nie udało się odczytać obrazu ze schowka Windowsa.",
                                            "Could not read the image received from the Windows clipboard."))
                    return
                }
                item.setData(content.data, forType: .png)
                item.setData(tiff, forType: .tiff)
            }
            self.pasteboard.clearContents()
            guard self.pasteboard.writeObjects([item]) else {
                self.onError?(L10n.text("Nie udało się zapisać schowka. Spróbuj skopiować ponownie.",
                                        "Could not update the clipboard. Copy the item again."))
                return
            }
            self.lastChangeCount = self.pasteboard.changeCount
            self.lastContent = content
            self.onApplied?(content)
        }
    }

    func poll() {
        let count = pasteboard.changeCount
        guard count != lastChangeCount else { return }
        // clearContents() zmienia licznik, ale późniejsze setData() w tej samej
        // generacji już nie. Pusty schowek sprawdzamy ponownie przy następnym ticku.
        guard let types = pasteboard.types, !types.isEmpty else {
            lastContent = nil
            return
        }
        let content = readContent()
        // Schowek może się zmienić podczas odczytu danych dostarczanych na żądanie.
        guard pasteboard.changeCount == count else { return }
        lastChangeCount = count
        let previous = lastContent
        lastContent = content
        guard let content, content != previous else { return }
        onLocalChange?(content)
    }

    private func readContent() -> ClipboardContent? {
        // Przeglądarki mogą kopiować obraz razem z URL-em; obraz ma pierwszeństwo.
        if let png = pasteboard.data(forType: .png) {
            guard let content = ClipboardContent(format: .png, data: png) else {
                onError?(L10n.text("Nieobsługiwany obraz lub przekroczony limit 32 MiB / 64 megapikseli.",
                                   "Unsupported image or the 32 MiB / 64 megapixel limit was exceeded."))
                return nil
            }
            return content
        }
        if let tiff = pasteboard.data(forType: .tiff) {
            guard let source = CGImageSourceCreateWithData(tiff as CFData, nil),
                  let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
                  let width = properties[kCGImagePropertyPixelWidth] as? Int,
                  let height = properties[kCGImagePropertyPixelHeight] as? Int,
                  width > 0, height > 0,
                  width <= ProtocolConstants.maxClipboardImagePixels,
                  height <= ProtocolConstants.maxClipboardImagePixels,
                  width * height <= ProtocolConstants.maxClipboardImagePixels,
                  let image = CGImageSourceCreateImageAtIndex(source, 0, nil),
                  let png = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]),
                  let content = ClipboardContent(format: .png, data: png) else {
                onError?(L10n.text("Nieobsługiwany obraz lub przekroczony limit 32 MiB / 64 megapikseli.",
                                   "Unsupported image or the 32 MiB / 64 megapixel limit was exceeded."))
                return nil
            }
            return content
        }
        guard let text = pasteboard.string(forType: .string) else { return nil }
        return ClipboardContent(format: .utf8Text, data: Data(text.utf8))
    }
}
