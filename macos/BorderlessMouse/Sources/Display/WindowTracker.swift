import CoreGraphics
import Foundation

/// Śledzi okna leżące na ekranie wirtualnym (tryb okien). Windows dostaje ich
/// prostokąty i pokazuje obraz tylko w tych miejscach – reszta monitora to
/// zwykły pulpit Windows. Lista jest odświeżana kilkanaście razy na sekundę
/// i wysyłana tylko po zmianie.
final class WindowTracker {
    private let queue = DispatchQueue(label: "blm.display.windows", qos: .userInitiated)
    private var timer: DispatchSourceTimer?
    private var last: [NormalizedRect]?

    /// Wywoływane na kolejce trackera, tylko gdy lista się zmieniła.
    func start(displayID: CGDirectDisplayID, onChange: @escaping ([NormalizedRect]) -> Void) {
        stop()
        queue.sync { last = nil }
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: .milliseconds(66), leeway: .milliseconds(10))
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            let rects = Self.snapshot(displayID: displayID)
            guard rects != self.last else { return }
            self.last = rects
            onChange(rects)
        }
        timer.resume()
        self.timer = timer
    }

    func stop() {
        timer?.cancel()
        timer = nil
    }

    /// Wymusza ponowne wysłanie listy przy następnym odświeżeniu.
    func resend() {
        queue.async { self.last = nil }
    }

    /// Okna od najwyższego, obcięte do ekranu, w jednostkach 0…65535.
    static func snapshot(displayID: CGDirectDisplayID) -> [NormalizedRect] {
        let display = CGDisplayBounds(displayID)
        guard display.width > 0, display.height > 0,
              let info = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
                as? [[String: Any]] else { return [] }
        var rects: [NormalizedRect] = []
        for window in info {
            guard let layer = window[kCGWindowLayer as String] as? Int, (0..<1000).contains(layer),
                  let boundsDict = window[kCGWindowBounds as String] as? NSDictionary,
                  let bounds = CGRect(dictionaryRepresentation: boundsDict as CFDictionary) else { continue }
            let alpha = window[kCGWindowAlpha as String] as? Double ?? 1
            let owner = window[kCGWindowOwnerName as String] as? String ?? ""
            let name = window[kCGWindowName as String] as? String ?? ""
            let isMenuBar = owner == "Window Server" && name == "Menubar"
            // Dock i elementy systemowe nie są oknami, które warto przenosić na Windows.
            if owner == "Dock" || (owner == "Window Server" && !isMenuBar) || alpha < 0.01 { continue }
            let visible = bounds.intersection(display)
            guard !visible.isNull, visible.width > 2, visible.height > 2 else { continue }
            // Przezroczyste nakładki systemu i narzędzi (poza zwykłą warstwą 0) zajmują cały
            // ekran – pokazałyby na Windowsie całą tapetę Maca zamiast pojedynczych okien.
            if layer != 0, !isMenuBar, visible.width * visible.height >= display.width * display.height * 0.9 { continue }
            rects.append(NormalizedRect(x: normalize(visible.minX - display.minX, display.width),
                                        y: normalize(visible.minY - display.minY, display.height),
                                        width: normalize(visible.width, display.width),
                                        height: normalize(visible.height, display.height),
                                        flags: isMenuBar ? NormalizedRect.menuBar : 0))
        }
        return rects
    }

    static func normalize(_ value: CGFloat, _ total: CGFloat) -> UInt16 {
        UInt16(clamping: Int((value / total * 65535).rounded()))
    }

    /// Punkt 0…65535 → współrzędne globalne macOS na ekranie wirtualnym.
    static func point(x: UInt16, y: UInt16, on displayID: CGDirectDisplayID) -> CGPoint {
        let display = CGDisplayBounds(displayID)
        return CGPoint(x: display.minX + CGFloat(x) / 65535 * (display.width - 1),
                       y: display.minY + CGFloat(y) / 65535 * (display.height - 1))
    }
}
