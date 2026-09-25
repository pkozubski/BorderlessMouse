import AppKit
import ApplicationServices

/// Sterowanie oknami innych aplikacji (tryb okien): wyciągnięcie na wierzch, gdy
/// użytkownik aktywuje odpowiadające mu okno na Windowsie, i zamknięcie (Alt+F4).
/// Wymaga uprawnienia Dostępność, które aplikacja i tak ma do sterowania.
enum WindowControl {
    /// Prywatne `_AXUIElementGetWindow` łączy element AX z CGWindowID. Szukane
    /// dynamicznie – jeśli zniknie z systemu, okna dopasowujemy po położeniu.
    private typealias GetWindowFunction = @convention(c) (AXUIElement, UnsafeMutablePointer<CGWindowID>) -> AXError
    private static let getWindow: GetWindowFunction? = {
        guard let symbol = dlsym(UnsafeMutableRawPointer(bitPattern: -2), "_AXUIElementGetWindow") else { return nil }
        return unsafeBitCast(symbol, to: GetWindowFunction.self)
    }()

    static func raise(windowID: UInt32, pid: Int32, frame: CGRect?) {
        guard let window = element(windowID: windowID, pid: pid, frame: frame) else { return }
        AXUIElementPerformAction(window, kAXRaiseAction as CFString)
        AXUIElementSetAttributeValue(window, kAXMainAttribute as CFString, kCFBooleanTrue)
        NSRunningApplication(processIdentifier: pid)?.activate()
    }

    static func close(windowID: UInt32, pid: Int32, frame: CGRect?) {
        guard let window = element(windowID: windowID, pid: pid, frame: frame) else { return }
        var button: CFTypeRef?
        if AXUIElementCopyAttributeValue(window, kAXCloseButtonAttribute as CFString, &button) == .success, let button {
            AXUIElementPerformAction(button as! AXUIElement, kAXPressAction as CFString)
        }
    }

    /// Maksymalizacja lub zmiana rozmiaru ramką okna Windows (rozmiar w punktach).
    static func resize(windowID: UInt32, pid: Int32, frame: CGRect?, to size: CGSize) {
        guard let window = element(windowID: windowID, pid: pid, frame: frame) else { return }
        var size = size
        guard let value = AXValueCreate(.cgSize, &size) else { return }
        AXUIElementSetAttributeValue(window, kAXSizeAttribute as CFString, value)
    }

    /// Okno przeciągnięte na Windowsie do krawędzi po stronie Maca wraca na fizyczny ekran,
    /// tuż przy miejscu, w którym pojawi się kursor (`ratio` wzdłuż krawędzi).
    static func returnToMac(windowID: UInt32, pid: Int32, frame: CGRect, edge: ScreenEdge,
                            ratio: CGFloat, excluding virtualDisplay: CGDirectDisplayID) {
        guard let window = element(windowID: windowID, pid: pid, frame: frame) else { return }
        let physical = VirtualDisplay.activeDisplays().filter { $0 != virtualDisplay }.map { CGDisplayBounds($0) }
        let anchor: CGRect?
        switch edge {
        case .right: anchor = physical.max { $0.maxX < $1.maxX }
        case .left: anchor = physical.min { $0.minX < $1.minX }
        case .top: anchor = physical.min { $0.minY < $1.minY }
        case .bottom: anchor = physical.max { $0.maxY < $1.maxY }
        }
        guard let a = anchor else { return }
        let size = CGSize(width: min(frame.width, a.width), height: min(frame.height, a.height - 40))
        let clampedX = { (x: CGFloat) in min(max(x, a.minX), a.maxX - size.width) }
        let clampedY = { (y: CGFloat) in min(max(y, a.minY + 30), a.maxY - size.height) }
        let origin: CGPoint
        switch edge {
        case .right: origin = CGPoint(x: a.maxX - size.width, y: clampedY(a.minY + ratio * a.height - 20))
        case .left: origin = CGPoint(x: a.minX, y: clampedY(a.minY + ratio * a.height - 20))
        case .top: origin = CGPoint(x: clampedX(a.minX + ratio * a.width - size.width / 2), y: a.minY + 30)
        case .bottom: origin = CGPoint(x: clampedX(a.minX + ratio * a.width - size.width / 2), y: a.maxY - size.height)
        }
        var point = origin, newSize = size
        if size != frame.size, let value = AXValueCreate(.cgSize, &newSize) {
            AXUIElementSetAttributeValue(window, kAXSizeAttribute as CFString, value)
        }
        if let value = AXValueCreate(.cgPoint, &point) {
            AXUIElementSetAttributeValue(window, kAXPositionAttribute as CFString, value)
        }
    }

    /// Ikona aplikacji 64×64 w PNG – Windows pokazuje ją na pasku zadań i w Alt+Tab.
    static func iconPNG(pid: Int32) -> Data? {
        guard let icon = NSRunningApplication(processIdentifier: pid)?.icon else { return nil }
        let size = 64
        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size,
                                            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
        icon.draw(in: NSRect(x: 0, y: 0, width: size, height: size))
        NSGraphicsContext.restoreGraphicsState()
        return bitmap.representation(using: .png, properties: [:])
    }

    private static func element(windowID: UInt32, pid: Int32, frame: CGRect?) -> AXUIElement? {
        let app = AXUIElementCreateApplication(pid)
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(app, kAXWindowsAttribute as CFString, &value) == .success,
              let windows = value as? [AXUIElement] else { return nil }
        if let getWindow {
            for window in windows {
                var id: CGWindowID = 0
                if getWindow(window, &id) == .success, id == windowID { return window }
            }
        }
        guard let frame else { return nil }
        return windows.first { window in
            var position: CFTypeRef?
            guard AXUIElementCopyAttributeValue(window, kAXPositionAttribute as CFString, &position) == .success,
                  let position else { return false }
            var point = CGPoint.zero
            AXValueGetValue(position as! AXValue, .cgPoint, &point)
            return abs(point.x - frame.minX) < 2 && abs(point.y - frame.minY) < 2
        }
    }
}
