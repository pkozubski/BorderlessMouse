import ApplicationServices
import CoreGraphics
import Foundation

/// macOS pamięta, które okna były na ekranie wirtualnym (rozpoznaje go po stałym
/// numerze seryjnym) i przywraca je, gdy ekran znowu się pojawi. Bez sprzątania
/// na Windowsie od razu wyskoczyłyby okna, których użytkownik w tej sesji nie
/// przenosił. Przenosimy je więc na fizyczny ekran przez Accessibility API.
enum WindowEvacuator {
    /// Zwraca liczbę przeniesionych okien. Wymaga uprawnienia Dostępność.
    @discardableResult
    static func evacuate(from displayID: CGDirectDisplayID) -> Int {
        guard AXIsProcessTrusted() else { return 0 }
        let virtual = CGDisplayBounds(displayID)
        guard let target = targetArea(excluding: displayID), virtual.width > 0 else { return 0 }
        guard let info = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
                as? [[String: Any]] else { return 0 }
        let pids = Set(info.compactMap { window -> pid_t? in
            guard (window[kCGWindowLayer as String] as? Int) == 0,
                  let dict = window[kCGWindowBounds as String] as? NSDictionary,
                  let bounds = CGRect(dictionaryRepresentation: dict as CFDictionary),
                  virtual.contains(CGPoint(x: bounds.midX, y: bounds.midY)),
                  (window[kCGWindowOwnerPID as String] as? pid_t) != getpid() else { return nil }
            return window[kCGWindowOwnerPID as String] as? pid_t
        })
        var moved = 0
        for pid in pids {
            let app = AXUIElementCreateApplication(pid)
            var value: CFTypeRef?
            guard AXUIElementCopyAttributeValue(app, kAXWindowsAttribute as CFString, &value) == .success,
                  let windows = value as? [AXUIElement] else { continue }
            for window in windows {
                guard let frame = frame(of: window), virtual.contains(CGPoint(x: frame.midX, y: frame.midY)) else { continue }
                let size = CGSize(width: min(frame.width, target.width), height: min(frame.height, target.height))
                let origin = CGPoint(x: target.minX + (target.width - size.width) / 2,
                                     y: target.minY + (target.height - size.height) / 2)
                if size != frame.size { set(window, kAXSizeAttribute, size) }
                if set(window, kAXPositionAttribute, origin) { moved += 1 }
            }
        }
        return moved
    }

    /// Ekran fizyczny bez paska menu (współrzędne globalne, lewy górny róg).
    private static func targetArea(excluding displayID: CGDirectDisplayID) -> CGRect? {
        let main = CGMainDisplayID() == displayID ? nil : CGDisplayBounds(CGMainDisplayID())
        guard let main else { return nil }
        return CGRect(x: main.minX, y: main.minY + 40, width: main.width, height: main.height - 40)
    }

    private static func frame(of window: AXUIElement) -> CGRect? {
        var positionValue: CFTypeRef?, sizeValue: CFTypeRef?
        guard AXUIElementCopyAttributeValue(window, kAXPositionAttribute as CFString, &positionValue) == .success,
              AXUIElementCopyAttributeValue(window, kAXSizeAttribute as CFString, &sizeValue) == .success,
              let positionValue, let sizeValue else { return nil }
        var position = CGPoint.zero, size = CGSize.zero
        guard AXValueGetValue(positionValue as! AXValue, .cgPoint, &position),
              AXValueGetValue(sizeValue as! AXValue, .cgSize, &size) else { return nil }
        return CGRect(origin: position, size: size)
    }

    @discardableResult
    private static func set(_ window: AXUIElement, _ attribute: String, _ point: CGPoint) -> Bool {
        var point = point
        guard let value = AXValueCreate(.cgPoint, &point) else { return false }
        return AXUIElementSetAttributeValue(window, attribute as CFString, value) == .success
    }

    @discardableResult
    private static func set(_ window: AXUIElement, _ attribute: String, _ size: CGSize) -> Bool {
        var size = size
        guard let value = AXValueCreate(.cgSize, &size) else { return false }
        return AXUIElementSetAttributeValue(window, attribute as CFString, value) == .success
    }
}
