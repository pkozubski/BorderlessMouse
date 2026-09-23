import AppKit
import CoreVideo
import QuartzCore

/// Okna Windows na Macu (kierunek Windows → Mac). Windows ma wirtualny monitor (sterownik
/// Virtual Display Driver) o rozmiarze ekranu Maca, położony przy krawędzi, za którą stoi Mac.
/// Okna przeciągnięte na ten monitor pojawiają się na Macu w tym samym miejscu jako zwykłe
/// okna macOS; obraz to jeden strumień H.264 całego monitora, który każde okno przycina
/// do swojego prostokąta. Mysz i klawiatura nad takim oknem działają w Windowsie – Mac
/// tylko pokazuje kursor w odpowiednim miejscu.
final class WinViewSession {
    /// Ekran Maca (globalne współrzędne CG, punkty), który odpowiada monitorowi Windows.
    let screen: CGRect
    /// Poproszenie Windowsa o klatkę kluczową (błąd dekodera). Dowolny wątek.
    var onKeyframeNeeded: (() -> Void)?
    var onClosed: ((String?) -> Void)?

    private let receiver: WinViewReceiver
    private let decodeQueue = DispatchQueue(label: "blm.winview.decode", qos: .userInteractive)
    private let decoder = H264Decompressor()
    private var awaitingKeyframe = true
    private var lastKeyframeRequest = Date.distantPast
    private let proxies = WinWindowProxies()

    /// Migawka dla wątku wejścia: prostokąty okien w pikselach monitora Windows.
    private let lock = NSLock()
    private var hitRects: [CGRect] = []
    private var displaySize = CGSize(width: 1, height: 1)

    init(screen: CGRect, key: Data, token: Data) {
        self.screen = screen
        receiver = WinViewReceiver(key: key, token: token)
    }

    func start(completion: @escaping (Result<UInt16, Error>) -> Void) {
        receiver.onFrame = { [weak self] frame in self?.decode(frame) }
        receiver.onClosed = { [weak self] reason in self?.onClosed?(reason) }
        receiver.onConnected = { [weak self] in self?.onKeyframeNeeded?() }
        receiver.start(completion: completion)
    }

    func stop() {
        receiver.onFrame = nil
        receiver.onClosed = nil
        receiver.stop()
        decodeQueue.async { [decoder] in decoder.invalidate() }
        let proxies = self.proxies
        DispatchQueue.main.async { proxies.removeAll() }
        lock.withLock { hitRects = [] }
    }

    var stats: (frames: UInt64, bytes: UInt64) { (receiver.framesReceived, receiver.bytesReceived) }

    // MARK: - Okna

    func update(_ list: WinWindowList) {
        let rects = list.windows.map { CGRect(x: CGFloat($0.x), y: CGFloat($0.y), width: CGFloat($0.width), height: CGFloat($0.height)) }
        let size = CGSize(width: list.displayWidth, height: list.displayHeight)
        lock.withLock {
            hitRects = rects
            displaySize = size
        }
        let screen = self.screen
        DispatchQueue.main.async { [proxies] in proxies.update(list, screen: screen) }
    }

    /// Punkt ekranu Maca (globalne CG) → piksel monitora Windows, jeśli leży w którymś oknie Windows.
    func hitTest(_ point: CGPoint) -> (x: UInt16, y: UInt16)? {
        lock.withLock {
            guard !hitRects.isEmpty, screen.contains(point) else { return nil }
            let px = CGPoint(x: (point.x - screen.minX) * displaySize.width / screen.width,
                             y: (point.y - screen.minY) * displaySize.height / screen.height)
            guard hitRects.contains(where: { $0.contains(px) }) else { return nil }
            return (UInt16(clamping: Int(px.x.rounded(.down))), UInt16(clamping: Int(px.y.rounded(.down))))
        }
    }

    /// Piksel monitora Windows → punkt ekranu Maca (globalne CG).
    func macPoint(x: UInt16, y: UInt16) -> CGPoint {
        let size = lock.withLock { displaySize }
        let point = CGPoint(x: screen.minX + (CGFloat(x) + 0.5) * screen.width / size.width,
                            y: screen.minY + (CGFloat(y) + 0.5) * screen.height / size.height)
        return CGPoint(x: min(max(point.x, screen.minX), screen.maxX - 1), y: min(max(point.y, screen.minY), screen.maxY - 1))
    }

    /// Kursor Windows jest na wirtualnym monitorze: pokazujemy kursor Maca w tym miejscu.
    func cursor(_ update: WinCursorUpdate) {
        let point = macPoint(x: update.x, y: update.y)
        DispatchQueue.main.async {
            CGWarpMouseCursorPosition(point)
            WinViewSession.cursor(for: update.shape).set()
        }
    }

    /// Aktywna aplikacja decyduje o kursorze – nad oknem Windows musi to być BorderlessMouse.
    func pointerEntered() {
        DispatchQueue.main.async { [proxies] in
            NSApp.activate(ignoringOtherApps: true)
            proxies.bringToFront()
        }
    }

    /// Numer okna macOS pokazującego dane okno Windows (wątek główny; samotest).
    func proxyWindowNumber(_ id: UInt32) -> Int? { proxies.windowNumber(for: id) }

    private static func cursor(for shape: UInt8) -> NSCursor {
        switch CursorTracker.Shape(rawValue: shape) {
        case .iBeam: return .iBeam
        case .pointingHand: return .pointingHand
        case .resizeLeftRight: return .resizeLeftRight
        case .resizeUpDown: return .resizeUpDown
        case .crosshair: return .crosshair
        case .notAllowed: return .operationNotAllowed
        case .openHand: return .openHand
        case .closedHand: return .closedHand
        case .resizeDiagonalDown:
            if #available(macOS 15.0, *) { return .frameResize(position: .topLeft, directions: .all) }
            return .crosshair
        case .resizeDiagonalUp:
            if #available(macOS 15.0, *) { return .frameResize(position: .topRight, directions: .all) }
            return .crosshair
        default: return .arrow
        }
    }

    // MARK: - Obraz

    private func decode(_ frame: VideoStream.Frame) {
        decodeQueue.async { [weak self] in
            guard let self else { return }
            if self.awaitingKeyframe && !frame.isKeyframe { return }
            do {
                guard let image = try self.decoder.decode(frame.payload) else { return }
                self.awaitingKeyframe = false
                DispatchQueue.main.async { [proxies] in proxies.show(image) }
            } catch {
                self.awaitingKeyframe = true
                self.requestKeyframe()
            }
        }
    }

    private func requestKeyframe() {
        guard Date().timeIntervalSince(lastKeyframeRequest) > 0.5 else { return }
        lastKeyframeRequest = Date()
        onKeyframeNeeded?()
    }
}

/// Okna macOS pokazujące okna Windows (wątek główny).
final class WinWindowProxies {
    private final class Proxy {
        let window: NSWindow
        let clip = CALayer()
        let image = CALayer()
        var descriptor: WinWindowDescriptor

        init(descriptor: WinWindowDescriptor) {
            self.descriptor = descriptor
            window = NSWindow(contentRect: .zero, styleMask: [.borderless], backing: .buffered, defer: false)
            window.isReleasedWhenClosed = false
            window.isOpaque = false
            window.backgroundColor = .clear
            window.hasShadow = !descriptor.isPopup
            window.level = descriptor.isPopup ? .popUpMenu : .normal
            window.collectionBehavior = [.managed, .fullScreenNone]
            window.animationBehavior = .none
            let view = NSView()
            view.wantsLayer = true
            window.contentView = view
            clip.masksToBounds = true
            clip.backgroundColor = NSColor.clear.cgColor
            image.contentsGravity = .resize
            image.actions = ["contents": NSNull(), "position": NSNull(), "bounds": NSNull(), "frame": NSNull()]
            clip.actions = ["bounds": NSNull(), "position": NSNull(), "frame": NSNull(), "cornerRadius": NSNull()]
            clip.addSublayer(image)
            view.layer?.addSublayer(clip)
        }
    }

    private var proxies: [UInt32: Proxy] = [:]
    /// Kolejność od najwyższego (jak na Windowsie).
    private var order: [UInt32] = []
    private var displaySize = CGSize(width: 1, height: 1)
    private var screen = CGRect.zero
    private var surface: IOSurfaceRef?
    private var buffer: CVPixelBuffer?
    private var foreground: UInt32?

    func update(_ list: WinWindowList, screen: CGRect) {
        self.screen = screen
        displaySize = CGSize(width: list.displayWidth, height: list.displayHeight)
        let ids = list.windows.map(\.id)
        for (id, proxy) in proxies where !ids.contains(id) {
            proxy.window.orderOut(nil)
            proxies[id] = nil
        }
        var created = false
        for descriptor in list.windows {
            let proxy: Proxy
            if let existing = proxies[descriptor.id] {
                proxy = existing
                proxy.descriptor = descriptor
            } else {
                proxy = Proxy(descriptor: descriptor)
                proxies[descriptor.id] = proxy
                created = true
            }
            layout(proxy)
            if proxy.window.title != descriptor.title { proxy.window.title = descriptor.title }
        }
        let newForeground = list.windows.first(where: \.isForeground)?.id
        let reordered = ids != order
        order = ids
        if created || reordered || (newForeground != nil && newForeground != foreground) {
            // Okno aktywne na Windowsie (kliknięte, nowe) – cała grupa wychodzi na wierzch Maca.
            restack(bringToFront: created || newForeground != foreground)
        }
        foreground = newForeground
    }

    func bringToFront() { restack(bringToFront: true) }

    func windowNumber(for id: UInt32) -> Int? { proxies[id]?.window.windowNumber }

    func show(_ image: CVPixelBuffer) {
        guard let surface = CVPixelBufferGetIOSurface(image)?.takeUnretainedValue() else { return }
        buffer = image // trzyma IOSurface, dopóki nie przyjdzie następna klatka
        self.surface = surface
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for proxy in proxies.values { proxy.image.contents = surface }
        CATransaction.commit()
    }

    func removeAll() {
        for proxy in proxies.values { proxy.window.orderOut(nil) }
        proxies = [:]
        order = []
        surface = nil
        buffer = nil
        foreground = nil
    }

    private func restack(bringToFront: Bool) {
        // Od najniższego do najwyższego: każde kolejne nad poprzednim.
        var below: Int?
        for id in order.reversed() {
            guard let proxy = proxies[id] else { continue }
            if let below {
                proxy.window.order(.above, relativeTo: below)
            } else if bringToFront || !proxy.window.isVisible {
                proxy.window.orderFrontRegardless()
            } else {
                proxy.window.orderFront(nil)
            }
            below = proxy.window.windowNumber
        }
    }

    private func layout(_ proxy: Proxy) {
        // Część okna poza monitorem Windows nie ma obrazu i nie może wyjść poza ekran Maca.
        let full = proxy.descriptor
        let left = max(Int(full.x), 0), top = max(Int(full.y), 0)
        let right = min(Int(full.x) + Int(full.width), Int(displaySize.width))
        let bottom = min(Int(full.y) + Int(full.height), Int(displaySize.height))
        guard right > left, bottom > top else {
            proxy.window.orderOut(nil)
            return
        }
        let d = WinWindowDescriptor(id: full.id, x: Int32(left), y: Int32(top), width: UInt16(right - left),
                                    height: UInt16(bottom - top), flags: full.flags, title: full.title)
        let sx = screen.width / displaySize.width, sy = screen.height / displaySize.height
        // Globalne CG (y w dół) → AppKit (y w górę od dołu ekranu głównego).
        let primaryHeight = NSScreen.screens.first?.frame.height ?? screen.maxY
        let frame = CGRect(x: screen.minX + CGFloat(d.x) * sx,
                           y: primaryHeight - (screen.minY + CGFloat(d.y) * sy) - CGFloat(d.height) * sy,
                           width: max(CGFloat(d.width) * sx, 1), height: max(CGFloat(d.height) * sy, 1))
        if proxy.window.frame != frame { proxy.window.setFrame(frame, display: false) }
        let bounds = CGRect(origin: .zero, size: frame.size)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        proxy.clip.frame = bounds
        // Windows 11 zaokrągla okna promieniem 8 px; zmaksymalizowane i menu mają proste rogi.
        let maximized = d.width >= UInt16(clamping: Int(displaySize.width)) && d.height >= UInt16(clamping: Int(displaySize.height) - 80)
        proxy.clip.cornerRadius = maximized ? 0 : 8 * sx
        // Warstwa obrazu ma rozmiar całego monitora Windows i jest przesunięta tak,
        // żeby w oknie wypadł jego prostokąt (y w górę – warstwy AppKit nie są odwrócone).
        proxy.image.frame = CGRect(x: -CGFloat(d.x) * sx,
                                   y: -(displaySize.height - CGFloat(d.y) - CGFloat(d.height)) * sy,
                                   width: displaySize.width * sx, height: displaySize.height * sy)
        if let surface { proxy.image.contents = surface }
        CATransaction.commit()
        // Okno wróciło na monitor Windows po całkowitym zjechaniu z niego.
        if !proxy.window.isVisible { proxy.window.orderFrontRegardless() }
    }
}
