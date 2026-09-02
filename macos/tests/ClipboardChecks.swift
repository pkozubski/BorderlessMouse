import AppKit
import Foundation

@main
struct ClipboardChecks {
    static func expect(_ condition: @autoclosure () -> Bool, _ message: String) {
        guard condition() else { print("FAIL: \(message)"); exit(1) }
    }

    @MainActor
    static func apply(_ sync: ClipboardSync, _ content: ClipboardContent) async -> Bool {
        await withCheckedContinuation { continuation in
            sync.onApplied = { _ in continuation.resume(returning: true) }
            sync.onError = { _ in continuation.resume(returning: false) }
            sync.apply(content)
        }
    }

    @MainActor
    static func main() async throws {
        let fixtureURL = URL(fileURLWithPath: CommandLine.arguments[1])
        let fixture = try JSONSerialization.jsonObject(with: Data(contentsOf: fixtureURL)) as! [String: String]
        let png = Data(base64Encoded: fixture["pngBase64"]!)!
        let image = ClipboardContent(format: .png, data: png)!
        let text = ClipboardContent(format: .utf8Text, data: Data(fixture["text"]!.utf8))!
        // Wspólne wzorce Windows/macOS: dokładnie te same bajty na łączu.
        // Pad z lewej, także dla bajtów 0x01/0x0A.
        func wireHex(_ data: Data) -> String { data.map { let h = String($0, radix: 16); return h.count == 1 ? "0" + h : h }.joined() }
        expect(wireHex(Frame.clipboard(image)) == fixture["pngFrameHex"], "PNG wire compatibility")
        expect(wireHex(Frame.clipboard(text)) == fixture["textFrameHex"], "UTF-8 wire compatibility")
        expect(ClipboardContent(payload: [1] + png) == image, "PNG parse")
        expect(ClipboardContent(payload: []) == nil, "empty payload")
        expect(ClipboardContent(payload: [2, 1]) == nil, "unknown format")
        expect(ClipboardContent(payload: [0, 0xFF]) == nil, "invalid UTF-8")
        expect(ClipboardContent(payload: [1, 1, 2, 3]) == nil, "invalid PNG")
        let maxText = ClipboardContent(format: .utf8Text, data: Data(repeating: 65, count: ProtocolConstants.maxClipboardBytes))!
        expect(Frame.clipboard(maxText)[1] == 0xFF, "long text frame")
        expect(ClipboardContent(format: .utf8Text, data: maxText.data + [65]) == nil, "text limit")
        var maxPNG = png
        maxPNG.append(Data(count: ProtocolConstants.maxClipboardImageBytes - png.count))
        let maxImage = ClipboardContent(format: .png, data: maxPNG)!
        let frame = Frame.clipboard(maxImage)
        expect(frame.count == ProtocolConstants.maxLongFrame + 6, "maximum PNG frame")
        expect(Array(frame[2..<6]) == [1, 0, 0, 2], "32 MiB + format length, little-endian")
        expect(ClipboardContent(format: .png, data: maxPNG + [0]) == nil, "PNG byte limit")
        var oversized = png
        oversized.replaceSubrange(16..<24, with: [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF])
        expect(ClipboardContent(format: .png, data: oversized) == nil, "PNG pixel limit before decode")

        // Osobny pasteboard: test nigdy nie zmienia schowka użytkownika.
        let pasteboard = NSPasteboard(name: .init("com.borderlessmouse.clipboard-checks.\(UUID())"))
        defer { pasteboard.releaseGlobally() }
        let sync = ClipboardSync(pasteboard: pasteboard)
        var sent: [ClipboardContent] = []
        sync.onLocalChange = { sent.append($0) }
        pasteboard.clearContents()
        pasteboard.setString(text.text!, forType: .string)
        sync.poll()
        expect(sent == [text], "local text")
        let item = NSPasteboardItem()
        item.setString("https://example.com/photo", forType: .string)
        item.setData(png, forType: .png)
        pasteboard.clearContents()
        pasteboard.writeObjects([item])
        sync.poll()
        expect(sent.last == image, "image takes priority over URL")
        sync.poll()
        expect(sent.count == 2, "unchanged clipboard is not sent twice")
        let appliedImage = await apply(sync, image)
        expect(appliedImage, "apply PNG")
        expect(pasteboard.data(forType: .png) == png, "preserve original PNG bytes")
        let received = NSBitmapImageRep(data: pasteboard.data(forType: .tiff)!)!
        expect(received.pixelsWide == 2 && received.pixelsHigh == 2, "TIFF preserves pixel dimensions")
        let alpha = received.colorAt(x: 1, y: 0)!.alphaComponent
        expect(abs(alpha - 128.0 / 255.0) < 0.01, "TIFF preserves transparency")
        sync.poll()
        expect(sent.count == 2, "received image is not echoed")
        pasteboard.clearContents()
        pasteboard.setString(text.text!, forType: .string)
        sync.poll()
        expect(sent.count == 3 && sent.last == text, "text-image-text changes are not suppressed")
        pasteboard.clearContents()
        sync.poll()
        pasteboard.setString(text.text!, forType: .string)
        sync.poll()
        expect(sent.count == 4, "text after an empty clipboard is resent")
        pasteboard.clearContents()
        pasteboard.setData(received.tiffRepresentation!, forType: .tiff)
        sync.poll()
        expect(sent.count == 5 && sent.last?.format == .png, "TIFF-only screenshot converts to PNG")
        let roundTrip = NSBitmapImageRep(data: sent.last!.data)!
        expect(roundTrip.pixelsWide == 2 && abs(roundTrip.colorAt(x: 1, y: 0)!.alphaComponent - alpha) < 0.01, "TIFF-PNG round trip")
        let appliedText = await apply(sync, text)
        expect(appliedText && pasteboard.string(forType: .string) == text.text, "apply text")
        sync.poll()
        expect(sent.count == 5, "received text is not echoed")
        let truncated = ClipboardContent(format: .png, data: png.prefix(33))!
        let appliedInvalid = await apply(sync, truncated)
        expect(!appliedInvalid, "truncated PNG is rejected by decoder")
        expect(pasteboard.string(forType: .string) == text.text, "invalid image leaves clipboard intact")
        print("✓ Clipboard: shared wire fixtures, size limits, PNG/TIFF, transparency, text/image changes, no echo")
    }
}
