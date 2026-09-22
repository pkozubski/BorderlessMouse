import AppKit
import CoreGraphics
import Foundation

/// Wstrzykuje zdarzenia myszy i klawiatury przez CGEvent. Wszystkie metody
/// należy wołać z jednej kolejki (Engine.eventsQueue).
final class InputInjector {
    var swapCtrlCmd = false
    /// nil = auto (na podstawie ustawienia "naturalne przewijanie"), true/false = wymuszenie
    var invertScroll: Bool? = nil
    var scrollPixelsPerNotch: Double = 40

    /// Kursor uderzył w krawędź powrotną – należy oddać sterowanie Windowsowi.
    /// Trzeci argument: kursor wychodzi z ekranu wirtualnego (Windows stawia go po drugiej stronie monitora).
    var onLeave: ((ScreenEdge, Float, Bool) -> Void)?
    var onActiveChanged: ((Bool) -> Void)?
    /// Kursor sterowany z Windowsa wszedł na ekran wirtualny (true) lub z niego zszedł.
    var onVirtualDisplayFocus: ((Bool) -> Void)?

    /// Ekran wirtualny wyświetlany na Windowsie. Kursor z Windowsa nigdy nie
    /// wchodzi na niego bezpośrednio – trafia na fizyczny ekran Maca.
    var virtualDisplayID: CGDirectDisplayID? {
        didSet {
            guard virtualDisplayID != oldValue else { return }
            refreshDisplays()
        }
    }

    private(set) var isActive = false
    private(set) var isOnVirtualDisplay = false
    private var position = CGPoint.zero
    private var returnEdge: ScreenEdge = .right
    private var currentDisplay = Display(id: 0, bounds: .zero)
    private var displays: [Display] = []

    private struct Display {
        let id: CGDirectDisplayID
        let bounds: CGRect
    }

    private var pressedKeys = Set<CGKeyCode>()
    private var buttonsDown = Set<Int>()
    private var lastClickTime: [Int: TimeInterval] = [:]
    private var lastClickPos: [Int: CGPoint] = [:]
    private var clickCount: [Int: Int] = [:]
    private var scrollRemainderX = 0.0
    private var scrollRemainderY = 0.0

    private let source = CGEventSource(stateID: .hidSystemState)

    // MARK: - Uprawnienia

    static var isAccessibilityTrusted: Bool { AXIsProcessTrusted() }

    static func requestAccessibility() {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        _ = AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
    }

    // MARK: - Ekrany

    private static func activeDisplays() -> [Display] {
        var count: UInt32 = 0
        CGGetActiveDisplayList(0, nil, &count)
        guard count > 0 else { return [Display(id: CGMainDisplayID(), bounds: CGRect(x: 0, y: 0, width: 1920, height: 1080))] }
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        CGGetActiveDisplayList(count, &ids, &count)
        return ids.prefix(Int(count)).map { Display(id: $0, bounds: CGDisplayBounds($0)) }
    }

    /// Po dodaniu lub usunięciu ekranu wirtualnego (także w trakcie sterowania).
    func refreshDisplays() {
        guard isActive else { return }
        displays = Self.activeDisplays()
        if let fresh = displays.first(where: { $0.id == currentDisplay.id }) {
            currentDisplay = fresh
            if !fresh.bounds.contains(position) {
                position.x = min(max(position.x, fresh.bounds.minX), fresh.bounds.maxX - 1)
                position.y = min(max(position.y, fresh.bounds.minY), fresh.bounds.maxY - 1)
            }
        } else if let fallback = displays.first(where: { $0.id != virtualDisplayID }) {
            // Ekran wirtualny zniknął pod kursorem – wracamy na fizyczny ekran.
            currentDisplay = fallback
            position = CGPoint(x: fallback.bounds.midX, y: fallback.bounds.midY)
            postMouse(.mouseMoved, dx: 0, dy: 0)
        }
        setVirtualFocus(currentDisplay.id == virtualDisplayID && virtualDisplayID != nil)
    }

    private func isVirtual(_ display: Display) -> Bool {
        virtualDisplayID != nil && display.id == virtualDisplayID
    }

    private func setVirtualFocus(_ focused: Bool) {
        guard focused != isOnVirtualDisplay else { return }
        isOnVirtualDisplay = focused
        onVirtualDisplayFocus?(focused)
    }

    // MARK: - Wejście/wyjście kursora

    func enter(edge: ScreenEdge, ratio: Float) {
        displays = Self.activeDisplays()
        let physical = displays.filter { $0.id != virtualDisplayID }.map(\.bounds)
        let candidates = physical.isEmpty ? displays.map(\.bounds) : physical
        let target: CGRect
        switch edge {
        case .left: target = candidates.min { $0.minX < $1.minX }!
        case .right: target = candidates.max { $0.maxX < $1.maxX }!
        case .top: target = candidates.min { $0.minY < $1.minY }!
        case .bottom: target = candidates.max { $0.maxY < $1.maxY }!
        }
        let r = CGFloat(min(max(ratio, 0), 1))
        switch edge {
        case .left: position = CGPoint(x: target.minX + 2, y: target.minY + r * (target.height - 1))
        case .right: position = CGPoint(x: target.maxX - 3, y: target.minY + r * (target.height - 1))
        case .top: position = CGPoint(x: target.minX + r * (target.width - 1), y: target.minY + 2)
        case .bottom: position = CGPoint(x: target.minX + r * (target.width - 1), y: target.maxY - 3)
        }
        currentDisplay = displays.first { $0.bounds == target } ?? Display(id: 0, bounds: target)
        returnEdge = edge
        // Po ręcznym przełączeniu (Scroll Lock) kursor mógł zostać na ekranie wirtualnym.
        setVirtualFocus(false)
        if !isActive {
            isActive = true
            onActiveChanged?(true)
        }
        postMouse(.mouseMoved, dx: 0, dy: 0)
    }

    /// Kończy sterowanie (np. rozłączenie) – zwalnia wszystko.
    func deactivate() {
        releaseAll()
        setVirtualFocus(false)
        if isActive {
            isActive = false
            onActiveChanged?(false)
        }
    }

    // MARK: - Mysz

    func moveBy(dx: Int, dy: Int) {
        guard isActive else { return }
        let candidate = CGPoint(x: position.x + CGFloat(dx), y: position.y + CGFloat(dy))
        if let d = displays.first(where: { $0.bounds.contains(candidate) }),
           isVirtual(d), !isVirtual(currentDisplay), buttonsDown.isEmpty {
            // Ekran wirtualny stoi między Makiem a Windowsem. Wchodzi się na niego tylko,
            // przeciągając okno (przycisk wciśnięty); zwykły ruch wraca prosto do Windowsa.
            let b = currentDisplay.bounds
            let ratio: Float
            switch returnEdge {
            case .left, .right: ratio = Float((position.y - b.minY) / max(b.height - 1, 1))
            case .top, .bottom: ratio = Float((position.x - b.minX) / max(b.width - 1, 1))
            }
            let edge = returnEdge
            deactivate()
            onLeave?(edge, min(max(ratio, 0), 1), false)
            return
        } else if let d = displays.first(where: { $0.bounds.contains(candidate) }) {
            currentDisplay = d
            position = candidate
            setVirtualFocus(d.id == virtualDisplayID && virtualDisplayID != nil)
        } else {
            let b = currentDisplay.bounds
            var leaving = false
            var ratio: Float = 0
            switch returnEdge {
            case .left where candidate.x < b.minX,
                 .right where candidate.x >= b.maxX:
                leaving = true
                ratio = Float((position.y - b.minY) / max(b.height - 1, 1))
            case .top where candidate.y < b.minY,
                 .bottom where candidate.y >= b.maxY:
                leaving = true
                ratio = Float((position.x - b.minX) / max(b.width - 1, 1))
            default:
                break
            }
            if leaving {
                let edge = returnEdge
                let fromVirtual = isOnVirtualDisplay
                deactivate()
                onLeave?(edge, min(max(ratio, 0), 1), fromVirtual)
                return
            }
            position.x = min(max(candidate.x, b.minX), b.maxX - 1)
            position.y = min(max(candidate.y, b.minY), b.maxY - 1)
        }
        postMouse(dragTypeForCurrentButtons(), dx: dx, dy: dy, button: dragButton())
    }

    func button(_ id: Int, down: Bool) {
        guard isActive else { return }
        let cgButton: CGMouseButton
        let type: CGEventType
        switch id {
        case 0: cgButton = .left; type = down ? .leftMouseDown : .leftMouseUp
        case 1: cgButton = .right; type = down ? .rightMouseDown : .rightMouseUp
        default:
            cgButton = CGMouseButton(rawValue: UInt32(id)) ?? .center
            type = down ? .otherMouseDown : .otherMouseUp
        }
        if down {
            let now = ProcessInfo.processInfo.systemUptime
            if let t = lastClickTime[id], now - t < NSEvent.doubleClickInterval,
               let p = lastClickPos[id], abs(p.x - position.x) < 5, abs(p.y - position.y) < 5 {
                clickCount[id] = (clickCount[id] ?? 1) + 1
            } else {
                clickCount[id] = 1
            }
            lastClickTime[id] = now
            lastClickPos[id] = position
            buttonsDown.insert(id)
        } else {
            buttonsDown.remove(id)
        }
        guard let ev = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: position, mouseButton: cgButton) else { return }
        ev.flags = currentFlags()
        ev.setIntegerValueField(.mouseEventClickState, value: Int64(clickCount[id] ?? 1))
        ev.setIntegerValueField(.mouseEventButtonNumber, value: Int64(id))
        ev.post(tap: .cghidEventTap)
    }

    /// dx/dy w jednostkach Windows (120 = jeden ząbek kółka).
    func wheel(dx: Int, dy: Int) {
        guard isActive else { return }
        let invert: Bool
        if let forced = invertScroll {
            invert = forced
        } else {
            invert = Self.naturalScrollingEnabled
        }
        let sign = invert ? -1.0 : 1.0
        scrollRemainderY += Double(dy) / 120.0 * scrollPixelsPerNotch * sign
        scrollRemainderX += Double(dx) / 120.0 * scrollPixelsPerNotch * sign
        let py = scrollRemainderY.rounded(.towardZero)
        let px = scrollRemainderX.rounded(.towardZero)
        scrollRemainderY -= py
        scrollRemainderX -= px
        guard px != 0 || py != 0 else { return }
        // Windows: dodatni dx = przewijanie w prawo; macOS: dodatni wheel2 = w lewo
        guard let ev = CGEvent(scrollWheelEvent2Source: source, units: .pixel, wheelCount: 2,
                               wheel1: Int32(py), wheel2: Int32(-px), wheel3: 0) else { return }
        ev.location = position
        ev.flags = currentFlags()
        ev.setIntegerValueField(.scrollWheelEventIsContinuous, value: 1)
        ev.post(tap: .cghidEventTap)
    }

    private static var naturalScrollingEnabled: Bool {
        let defaults = UserDefaults.standard.persistentDomain(forName: UserDefaults.globalDomain)
        return defaults?["com.apple.swipescrolldirection"] as? Bool ?? true
    }

    // MARK: - Klawiatura

    func key(scancode: UInt16, vk: UInt16, extended: Bool, down: Bool, isRepeat: Bool) {
        guard isActive else { return }
        guard let target = KeyMap.lookup(scancode: scancode, vk: vk, extended: extended) else { return }
        switch target {
        case .media(let code):
            postMediaKey(code, down: down)
        case .key(let original):
            let code = swapCtrlCmd ? Self.swapped(original) : original
            postKey(code, down: down, isRepeat: isRepeat)
        }
    }

    private static func swapped(_ code: CGKeyCode) -> CGKeyCode {
        switch code {
        case KeyMap.VK.control: return KeyMap.VK.command
        case KeyMap.VK.command: return KeyMap.VK.control
        case KeyMap.VK.rightControl: return KeyMap.VK.rightCommand
        case KeyMap.VK.rightCommand: return KeyMap.VK.rightControl
        default: return code
        }
    }

    private func postKey(_ code: CGKeyCode, down: Bool, isRepeat: Bool) {
        let isModifier = KeyMap.modifierKeys.contains(code)
        if down { pressedKeys.insert(code) } else { pressedKeys.remove(code) }
        guard let ev = CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: down) else { return }
        if isModifier { ev.type = .flagsChanged }
        ev.flags = currentFlags(for: code)
        if isRepeat && down { ev.setIntegerValueField(.keyboardEventAutorepeat, value: 1) }
        ev.post(tap: .cghidEventTap)
    }

    private func postMediaKey(_ key: Int32, down: Bool) {
        let modifier: UInt = down ? 0xA00 : 0xB00
        let data1 = Int((Int(key) << 16) | ((down ? 0xA : 0xB) << 8))
        guard let ev = NSEvent.otherEvent(with: .systemDefined, location: .zero,
                                          modifierFlags: NSEvent.ModifierFlags(rawValue: modifier),
                                          timestamp: ProcessInfo.processInfo.systemUptime,
                                          windowNumber: 0, context: nil, subtype: 8,
                                          data1: data1, data2: -1) else { return }
        ev.cgEvent?.post(tap: .cghidEventTap)
    }

    func releaseAll() {
        // najpierw zwykłe klawisze, potem modyfikatory
        let ordered = pressedKeys.sorted { a, b in
            let am = KeyMap.modifierKeys.contains(a), bm = KeyMap.modifierKeys.contains(b)
            return am == bm ? a < b : !am
        }
        for code in ordered {
            pressedKeys.remove(code)
            guard let ev = CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: false) else { continue }
            if KeyMap.modifierKeys.contains(code) { ev.type = .flagsChanged }
            ev.flags = currentFlags(for: code)
            ev.post(tap: .cghidEventTap)
        }
        pressedKeys.removeAll()
        for id in buttonsDown.sorted() {
            buttonsDown.remove(id)
            let type: CGEventType = id == 0 ? .leftMouseUp : (id == 1 ? .rightMouseUp : .otherMouseUp)
            let btn: CGMouseButton = id == 0 ? .left : (id == 1 ? .right : (CGMouseButton(rawValue: UInt32(id)) ?? .center))
            guard let ev = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: position, mouseButton: btn) else { continue }
            ev.setIntegerValueField(.mouseEventButtonNumber, value: Int64(id))
            ev.post(tap: .cghidEventTap)
        }
        buttonsDown.removeAll()
    }

    // MARK: - Pomocnicze

    private func currentFlags(for key: CGKeyCode? = nil) -> CGEventFlags {
        var flags = CGEventFlags()
        for code in pressedKeys {
            switch code {
            case KeyMap.VK.shift, KeyMap.VK.rightShift: flags.insert(.maskShift)
            case KeyMap.VK.control, KeyMap.VK.rightControl: flags.insert(.maskControl)
            case KeyMap.VK.option, KeyMap.VK.rightOption: flags.insert(.maskAlternate)
            case KeyMap.VK.command, KeyMap.VK.rightCommand: flags.insert(.maskCommand)
            case KeyMap.VK.capsLock: flags.insert(.maskAlphaShift)
            case KeyMap.VK.function: flags.insert(.maskSecondaryFn)
            default: break
            }
        }
        if let key {
            if KeyMap.arrowAndNavKeys.contains(key) {
                flags.insert(.maskSecondaryFn)
                flags.insert(.maskNumericPad)
            } else if KeyMap.functionKeys.contains(key) {
                flags.insert(.maskSecondaryFn)
            } else if KeyMap.keypadKeys.contains(key) {
                flags.insert(.maskNumericPad)
            }
        }
        return flags
    }

    private func dragTypeForCurrentButtons() -> CGEventType {
        if buttonsDown.contains(0) { return .leftMouseDragged }
        if buttonsDown.contains(1) { return .rightMouseDragged }
        if !buttonsDown.isEmpty { return .otherMouseDragged }
        return .mouseMoved
    }

    private func dragButton() -> CGMouseButton {
        if buttonsDown.contains(0) { return .left }
        if buttonsDown.contains(1) { return .right }
        if let other = buttonsDown.min() { return CGMouseButton(rawValue: UInt32(other)) ?? .center }
        return .left
    }

    private func postMouse(_ type: CGEventType, dx: Int, dy: Int, button: CGMouseButton = .left) {
        guard let ev = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: position, mouseButton: button) else { return }
        ev.flags = currentFlags()
        ev.setIntegerValueField(.mouseEventDeltaX, value: Int64(dx))
        ev.setIntegerValueField(.mouseEventDeltaY, value: Int64(dy))
        ev.post(tap: .cghidEventTap)
    }
}
