import Foundation

/// Nieizolowany "silnik": łączy serwer TCP, discovery, wstrzykiwanie
/// wejścia i przechwytywanie audio. Cała logika działa na `eventsQueue`;
/// do UI trafiają tylko zdarzenia `Event`.
final class Engine {
    struct Config: Equatable {
        var deviceName: String
        var controlPort: UInt16
        var inputEnabled: Bool
        var audioEnabled: Bool
        var muteLocalAudio: Bool
        var swapCtrlCmd: Bool
        var invertScroll: Bool?
        var scrollPixelsPerNotch: Double
        var audioBufferFrames: UInt32
        var clipboardSync: Bool
    }

    enum Event {
        case listening(Bool, String?)
        case peerConnected(ControlServer.PeerInfo)
        case peerDisconnected
        case cursorOnMac(Bool)
        case audioStarted(String)
        case audioStopped
        case audioError(String)
        case audioLevel(Float)
        case audioStats(packets: UInt64, errors: UInt64)
        case clipboardSent(summary: String)
        case clipboardReceived(summary: String)
        case clipboardError(String)
        case log(String)
    }

    var onEvent: ((Event) -> Void)?

    let eventsQueue = DispatchQueue(label: "blm.events", qos: .userInteractive)

    private var config: Config
    private let server: ControlServer
    private let discovery: DiscoveryResponder
    private let injector = InputInjector()
    private let clipboard = ClipboardSync()
    private var tap: SystemAudioTap?
    private var sender: AudioSender?
    private var audioRequest: (host: String, port: UInt16)?
    private var peer: ControlServer.PeerInfo?
    private var statsTimer: DispatchSourceTimer?
    private var accessibilityGranted = false
    private var audioGeneration: UInt64 = 0
    private let audioQueue = DispatchQueue(label: "blm.audio.control", qos: .userInitiated)

    init(config: Config) {
        self.config = config
        let nameBox = NameBox(config.deviceName)
        let portBox = PortBox(config.controlPort)
        self.nameBox = nameBox
        self.portBox = portBox
        server = ControlServer(callbackQueue: eventsQueue) { nameBox.value }
        discovery = DiscoveryResponder(name: { nameBox.value }, controlPort: { portBox.value })
        wire()
    }

    private final class NameBox { var value: String; init(_ v: String) { value = v } }
    private final class PortBox { var value: UInt16; init(_ v: UInt16) { value = v } }
    private let nameBox: NameBox
    private let portBox: PortBox

    // MARK: - Cykl życia

    func start() {
        if config.clipboardSync { clipboard.start() }
        eventsQueue.async {
            self.server.start(port: self.config.controlPort)
            do {
                try self.discovery.start()
                self.emit(.log("Discovery UDP nasłuchuje na porcie \(ProtocolConstants.discoveryPort)"))
            } catch {
                self.emit(.log("Discovery UDP nie wystartował: \(error.localizedDescription)"))
            }
        }
    }

    func stop() {
        clipboard.stop()
        eventsQueue.sync {
            self.stopAudioLocked(notify: false)
            self.injector.deactivate()
            self.discovery.stop()
            self.server.stop()
        }
    }

    func restartServer() {
        eventsQueue.async {
            self.server.stop()
            self.server.start(port: self.config.controlPort)
        }
    }

    func update(config newConfig: Config) {
        eventsQueue.async {
            let old = self.config
            self.config = newConfig
            self.nameBox.value = newConfig.deviceName
            self.portBox.value = newConfig.controlPort
            self.applyInjectorConfig()
            if old.controlPort != newConfig.controlPort {
                self.server.stop()
                self.server.start(port: newConfig.controlPort)
            }
            if old.inputEnabled && !newConfig.inputEnabled, self.injector.isActive {
                self.injector.deactivate()
                self.server.send(Frame.leave(edge: .right, ratio: 0.5))
            }
            if old.audioEnabled && !newConfig.audioEnabled {
                self.stopAudioLocked(notify: true)
            }
            if self.tap != nil, old.muteLocalAudio != newConfig.muteLocalAudio || old.audioBufferFrames != newConfig.audioBufferFrames {
                self.restartAudio()
            }
            if old.clipboardSync != newConfig.clipboardSync {
                if newConfig.clipboardSync { self.clipboard.start() } else { self.clipboard.stop() }
            }
            self.sendStatus()
        }
    }

    func setAccessibilityGranted(_ granted: Bool) {
        eventsQueue.async {
            guard self.accessibilityGranted != granted else { return }
            self.accessibilityGranted = granted
            self.sendStatus()
        }
    }

    func disconnectPeer() {
        eventsQueue.async { self.server.disconnectPeer() }
    }

    func stopAudio() {
        eventsQueue.async { self.stopAudioLocked(notify: true) }
    }

    // MARK: - Okablowanie

    private func wire() {
        applyInjectorConfig()

        server.onListeningChanged = { [weak self] ok, error in
            self?.emit(.listening(ok, error))
            if ok { self?.emit(.log("Nasłuchiwanie TCP na porcie \(self?.config.controlPort ?? 0)")) }
            if let error { self?.emit(.log("Błąd nasłuchiwania: \(error)")) }
        }
        server.onPeerConnected = { [weak self] info in
            guard let self else { return }
            self.peer = info
            self.emit(.peerConnected(info))
            self.emit(.log("Połączono: \(info.name) (\(info.address))"))
            self.sendStatus()
        }
        server.onPeerDisconnected = { [weak self] in
            guard let self else { return }
            self.peer = nil
            self.injector.deactivate()
            self.stopAudioLocked(notify: true)
            self.emit(.peerDisconnected)
            self.emit(.log("Rozłączono"))
        }
        server.onMessage = { [weak self] type, payload in
            self?.handle(type, payload)
        }
        injector.onLeave = { [weak self] edge, ratio in
            guard let self else { return }
            self.server.send(Frame.leave(edge: edge, ratio: ratio))
        }
        injector.onActiveChanged = { [weak self] active in
            self?.emit(.cursorOnMac(active))
            self?.sendStatus()
        }
        clipboard.onLocalChange = { [weak self] content in
            guard let self else { return }
            self.eventsQueue.async {
                guard self.config.clipboardSync, self.peer != nil else { return }
                self.server.send(Frame.clipboard(content))
                self.emit(.clipboardSent(summary: content.summary))
            }
        }
        clipboard.onApplied = { [weak self] content in
            self?.eventsQueue.async { self?.emit(.clipboardReceived(summary: content.summary)) }
        }
        clipboard.onError = { [weak self] message in
            self?.eventsQueue.async { self?.emit(.clipboardError(message)) }
        }
    }

    private func applyInjectorConfig() {
        injector.swapCtrlCmd = config.swapCtrlCmd
        injector.invertScroll = config.invertScroll
        injector.scrollPixelsPerNotch = config.scrollPixelsPerNotch
    }

    private func emit(_ event: Event) {
        onEvent?(event)
    }

    private func sendStatus() {
        var flags = StatusFlags()
        if accessibilityGranted { flags.insert(.accessibilityGranted) }
        if tap != nil { flags.insert(.audioCapturing) }
        if injector.isActive { flags.insert(.cursorOnMac) }
        server.send(Frame.status(flags))
    }

    // MARK: - Obsługa wiadomości (eventsQueue)

    private func handle(_ type: MessageType, _ payload: [UInt8]) {
        var r = ByteReader(payload)
        switch type {
        case .mouseMove:
            guard let dx = r.i16(), let dy = r.i16() else { return }
            injector.moveBy(dx: Int(dx), dy: Int(dy))
        case .mouseButton:
            guard let button = r.u8(), let down = r.u8() else { return }
            injector.button(Int(button), down: down != 0)
        case .mouseWheel:
            guard let dx = r.i16(), let dy = r.i16() else { return }
            injector.wheel(dx: Int(dx), dy: Int(dy))
        case .key:
            guard let scan = r.u16(), let vk = r.u16(), let flags = r.u8() else { return }
            injector.key(scancode: scan, vk: vk,
                         extended: flags & 0x01 != 0,
                         down: flags & 0x02 != 0,
                         isRepeat: flags & 0x04 != 0)
        case .releaseAll:
            injector.releaseAll()
        case .enter:
            guard let edgeRaw = r.u8(), let edge = ScreenEdge(rawValue: edgeRaw), let ratio = r.f32() else { return }
            guard config.inputEnabled, InputInjector.isAccessibilityTrusted else {
                // natychmiast oddaj sterowanie
                server.send(Frame.leave(edge: edge, ratio: ratio))
                emit(.log(config.inputEnabled
                          ? "Odrzucono wejście – brak uprawnienia Dostępność"
                          : "Odrzucono wejście – odbiór wyłączony w ustawieniach"))
                return
            }
            injector.enter(edge: edge, ratio: ratio)
        case .audioStart:
            guard let port = r.u16() else { return }
            _ = r.u8()
            guard let peer else { return }
            guard config.audioEnabled else {
                server.send(Frame.audioFormat(sampleRate: 0, channels: 0, format: .int16, status: 1,
                                              message: "Udostępnianie dźwięku jest wyłączone na Macu"))
                return
            }
            audioRequest = (peer.address, port)
            startAudio(host: peer.address, port: port)
        case .audioStop:
            stopAudioLocked(notify: true)
        case .clipboard:
            guard config.clipboardSync, let content = ClipboardContent(payload: payload) else { return }
            clipboard.apply(content)
        default:
            break
        }
    }

    // MARK: - Audio (eventsQueue)

    /// Tworzenie tapu może zablokować wątek do czasu decyzji użytkownika w oknie
    /// zgody TCC ("nagrywanie dźwięku systemowego"), dlatego działa na osobnej
    /// kolejce, a wynik wraca na eventsQueue.
    private func startAudio(host: String, port: UInt16) {
        stopAudioLocked(notify: false)
        audioGeneration &+= 1
        let generation = audioGeneration
        let cfg = config
        emit(.log("Uruchamianie przechwytywania audio → \(host):\(port)"))
        audioQueue.async { [weak self] in
            let tap = SystemAudioTap()
            do {
                try tap.start(muteLocal: cfg.muteLocalAudio, bufferFrames: cfg.audioBufferFrames)
                guard let format = tap.format else { throw SystemAudioTap.TapError.unsupportedFormat("brak formatu") }
                let sender = try AudioSender(host: host, port: port, channels: format.channels, format: .int16)
                self?.eventsQueue.async {
                    guard let self, self.audioGeneration == generation, self.peer != nil else {
                        tap.stop()
                        sender.close()
                        return
                    }
                    tap.onSamples = { ptr, frames in sender.send(int16Samples: ptr, frames: frames) }
                    tap.onLevel = { [weak self] level in self?.emit(.audioLevel(level)) }
                    tap.onDefaultDeviceChanged = { [weak self] in
                        self?.eventsQueue.asyncAfter(deadline: .now() + 0.4) { self?.restartAudio() }
                    }
                    self.tap = tap
                    self.sender = sender
                    let desc = "\(Int(format.sampleRate)) Hz · \(format.channels == 2 ? "stereo" : "\(format.channels) kan.") · 16-bit → \(host):\(port)"
                    self.server.send(Frame.audioFormat(sampleRate: UInt32(format.sampleRate), channels: UInt8(format.channels),
                                                       format: .int16, status: 0, message: ""))
                    self.emit(.audioStarted(desc))
                    self.emit(.log("Audio: \(desc), bufor \(cfg.audioBufferFrames) ramek"))
                    self.startStatsTimer()
                    self.sendStatus()
                }
            } catch {
                tap.stop()
                let message = error.localizedDescription
                self?.eventsQueue.async {
                    guard let self, self.audioGeneration == generation else { return }
                    self.server.send(Frame.audioFormat(sampleRate: 0, channels: 0, format: .int16, status: 1, message: message))
                    self.emit(.audioError(message))
                    self.emit(.log("Audio błąd: \(message)"))
                    self.sendStatus()
                }
            }
        }
    }

    private func restartAudio() {
        guard let req = audioRequest, tap != nil else { return }
        emit(.log("Restart przechwytywania audio (zmiana urządzenia/ustawień)"))
        startAudio(host: req.host, port: req.port)
    }

    private func stopAudioLocked(notify: Bool) {
        statsTimer?.cancel()
        statsTimer = nil
        audioGeneration &+= 1 // unieważnia trwające uruchamianie
        if let tap {
            tap.onSamples = nil
            tap.stop()
            self.tap = nil
            sender?.close()
            sender = nil
            if notify {
                emit(.audioStopped)
                emit(.log("Audio zatrzymane"))
                sendStatus()
            }
        }
    }

    private func startStatsTimer() {
        let t = DispatchSource.makeTimerSource(queue: eventsQueue)
        t.schedule(deadline: .now() + 1, repeating: 1)
        t.setEventHandler { [weak self] in
            guard let self, let sender = self.sender else { return }
            self.emit(.audioStats(packets: sender.packetsSent, errors: sender.sendErrors))
        }
        t.resume()
        statsTimer = t
    }
}
