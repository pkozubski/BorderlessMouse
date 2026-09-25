import AppKit

/// Rozpoznaje kształt kursora macOS (strzałka, kursor tekstowy, rączka, zmiana rozmiaru…),
/// żeby Windows pokazał nad oknem Maca odpowiedni kursor systemowy. macOS nie mówi
/// wprost, jaki kursor jest wyświetlany, więc porównujemy obraz `NSCursor.currentSystem`
/// ze wzorcami kursorów systemowych.
final class CursorTracker {
    /// Numery kształtów w protokole (CURSOR_SHAPE).
    enum Shape: UInt8, CaseIterable {
        case arrow = 0, iBeam, pointingHand, resizeLeftRight, resizeUpDown, crosshair,
             notAllowed, openHand, closedHand, resizeDiagonalDown, resizeDiagonalUp, wait
    }

    var onChange: ((Shape) -> Void)?

    private var timer: Timer?
    private var last: Shape?
    private lazy var fingerprints: [(Shape, [Float], CGPoint)] = Self.referenceFingerprints()

    /// Wątek główny.
    func start() {
        stop()
        last = nil
        timer = Timer.scheduledTimer(withTimeInterval: 1.0 / 30, repeats: true) { [weak self] _ in self?.poll() }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    private func poll() {
        guard let cursor = NSCursor.currentSystem else { return }
        let shape = identify(cursor)
        guard shape != last else { return }
        last = shape
        onChange?(shape)
    }

    func identify(_ cursor: NSCursor) -> Shape {
        guard let print = Self.fingerprint(cursor.image) else { return .arrow }
        var best = (Shape.arrow, Float.greatestFiniteMagnitude)
        for (shape, reference, _) in fingerprints {
            var distance: Float = 0
            for i in 0..<min(print.count, reference.count) { distance += abs(print[i] - reference[i]) }
            if distance < best.1 { best = (shape, distance) }
        }
        return best.0
    }

    private static func referenceFingerprints() -> [(Shape, [Float], CGPoint)] {
        var cursors: [(Shape, NSCursor)] = [
            (.arrow, .arrow), (.iBeam, .iBeam), (.pointingHand, .pointingHand),
            (.resizeLeftRight, .resizeLeftRight), (.resizeUpDown, .resizeUpDown), (.crosshair, .crosshair),
            (.notAllowed, .operationNotAllowed), (.openHand, .openHand), (.closedHand, .closedHand),
            (.resizeLeftRight, .resizeLeft), (.resizeLeftRight, .resizeRight),
            (.resizeUpDown, .resizeUp), (.resizeUpDown, .resizeDown),
        ]
        if #available(macOS 15.0, *) {
            cursors += [
                (.resizeDiagonalDown, NSCursor.frameResize(position: .topLeft, directions: .all)),
                (.resizeDiagonalDown, NSCursor.frameResize(position: .bottomRight, directions: .all)),
                (.resizeDiagonalUp, NSCursor.frameResize(position: .topRight, directions: .all)),
                (.resizeDiagonalUp, NSCursor.frameResize(position: .bottomLeft, directions: .all)),
                (.resizeLeftRight, NSCursor.frameResize(position: .left, directions: .all)),
                (.resizeUpDown, NSCursor.frameResize(position: .top, directions: .all)),
            ]
        }
        return cursors.compactMap { shape, cursor in
            fingerprint(cursor.image).map { (shape, $0, cursor.hotSpot) }
        }
    }

    /// Obraz kursora w 16×16 (kanał alfa i jasność) – odporne na skalę i odcień obrazu.
    static func fingerprint(_ image: NSImage) -> [Float]? {
        let side = 16
        guard image.size.width > 0, image.size.height > 0,
              let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: side, pixelsHigh: side, bitsPerSample: 8,
                                            samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                            bytesPerRow: side * 4, bitsPerPixel: 32),
              let data = bitmap.bitmapData else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
        // Proporcje zachowane – inaczej wąski kursor tekstowy przypominałby strzałkę.
        let scale = CGFloat(side) / max(image.size.width, image.size.height)
        let size = CGSize(width: image.size.width * scale, height: image.size.height * scale)
        image.draw(in: CGRect(x: (CGFloat(side) - size.width) / 2, y: (CGFloat(side) - size.height) / 2,
                              width: size.width, height: size.height))
        NSGraphicsContext.restoreGraphicsState()
        var values: [Float] = []
        values.reserveCapacity(side * side * 2)
        for i in 0..<(side * side) {
            let r = Float(data[i * 4]), g = Float(data[i * 4 + 1]), b = Float(data[i * 4 + 2]), a = Float(data[i * 4 + 3])
            values.append(a / 255)
            values.append((r + g + b) / (3 * 255))
        }
        return values
    }
}
