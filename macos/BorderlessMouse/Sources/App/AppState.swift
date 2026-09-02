import AppKit
import Combine
import Foundation
import OSLog
import SwiftUI

struct LogEntry: Identifiable {
    let id = UUID()
    let time: Date
    let text: String
}

@MainActor
final class AppState: ObservableObject {
    static let shared = AppState()

    @Published var settings: Settings {
        didSet {
            guard settings != oldValue else { return }
            if settings.launchAtLogin != oldValue.launchAtLogin {
                applyLaunchAtLogin(settings.launchAtLogin)
            }
            settings.save()
            engine.update(config: settings.engineConfig)
        }
    }

    @Published private(set) var isListening = false
    @Published private(set) var listenError: String?
    @Published private(set) var peer: ControlServer.PeerInfo?
    @Published private(set) var cursorOnMac = false
    @Published private(set) var audioStreaming = false
    @Published private(set) var audioDescription = ""
    @Published private(set) var audioError: String?
    @Published private(set) var audioLevel: Float = 0
    @Published private(set) var audioPackets: UInt64 = 0
    @Published private(set) var audioSendErrors: UInt64 = 0
    @Published private(set) var accessibilityGranted = false
    @Published private(set) var clipboardStatus = "Brak synchronizacji w tej sesji"
    @Published private(set) var loginItemStatus = LoginItem.statusDescription
    @Published var loginItemError: String?
    @Published private(set) var log: [LogEntry] = []

    let engine: Engine
    let updater = Updater()
    private var permissionTimer: Timer?
    private var updateTimer: Timer?
    private var updaterSink: AnyCancellable?
    private let logger = Logger(subsystem: "com.borderlessmouse.mac", category: "app")

    /// Stan bez sieci i uprawnień, wypełniony przykładowymi danymi – tylko dla
    /// podglądu interfejsu (`--ui-preview`).
    static func demo() -> AppState {
        let state = AppState(preview: true)
        state.fillWithDemoData()
        return state
    }

    private let isPreview: Bool

    private init(preview: Bool = false) {
        isPreview = preview
        let s = Settings.load()
        settings = s
        engine = Engine(config: s.engineConfig)
        engine.onEvent = { [weak self] event in
            Task { @MainActor in self?.apply(event) }
        }
        // stan autostartu bierzemy z systemu – to on jest źródłem prawdy
        settings.launchAtLogin = LoginItem.isEnabled
        LoginItem.refreshIfNeeded()
        accessibilityGranted = InputInjector.isAccessibilityTrusted
        guard !preview else { return }
        engine.setAccessibilityGranted(accessibilityGranted)
        engine.start()
        permissionTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refreshPermissions() }
        }
        scheduleUpdateChecks()
        updaterSink = updater.$state
            .receive(on: DispatchQueue.main)
            .sink { [weak self] state in
                switch state {
                case .available(let r): self?.appendLog("Dostępna aktualizacja \(r.version) (\(r.tag))")
                case .upToDate: self?.appendLog("Aktualizacje: masz najnowszą wersję \(Updater.currentVersion)")
                case .failed(let msg): self?.appendLog("Aktualizacje: \(msg)")
                case .installing: self?.appendLog("Aktualizacja pobrana, podmiana i restart…")
                default: break
                }
            }
    }

    private func scheduleUpdateChecks() {
        // pierwsze sprawdzenie chwilę po starcie, potem co 6 godzin
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 5_000_000_000)
            guard let self, self.settings.autoCheckUpdates else { return }
            await self.updater.check(silent: true)
        }
        updateTimer = Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.settings.autoCheckUpdates else { return }
                await self.updater.check(silent: true)
            }
        }
    }

    private func fillWithDemoData() {
        isListening = true
        peer = ControlServer.PeerInfo(name: "PC-BIURO", address: "192.168.1.42")
        cursorOnMac = true
        accessibilityGranted = true
        audioStreaming = true
        audioDescription = "48000 Hz · stereo · 16-bit → 192.168.1.42:47802"
        audioLevel = 0.42
        audioPackets = 18_432
        clipboardStatus = "Wysłano 128 zn. do Windowsa · 21:40:12"
        loginItemStatus = LoginItem.statusDescription
        log = [
            LogEntry(time: Date().addingTimeInterval(-95), text: "Nasłuchiwanie TCP na porcie 47800"),
            LogEntry(time: Date().addingTimeInterval(-80), text: "Discovery UDP nasłuchuje na porcie 47801"),
            LogEntry(time: Date().addingTimeInterval(-42), text: "Połączono: PC-BIURO (192.168.1.42)"),
            LogEntry(time: Date().addingTimeInterval(-20), text: "Audio: 48000 Hz · stereo · 16-bit, bufor 256 ramek"),
            LogEntry(time: Date().addingTimeInterval(-4), text: "Kursor przeszedł na Maca"),
        ]
    }

    func clearLog() { log.removeAll() }

    private func applyLaunchAtLogin(_ enabled: Bool) {
        guard !isPreview else { return }
        do {
            let mechanism = try LoginItem.setEnabled(enabled)
            loginItemError = nil
            appendLog(enabled
                      ? "Autostart włączony (\(mechanism == .service ? "element logowania" : "LaunchAgent"))"
                      : "Autostart wyłączony")
        } catch {
            loginItemError = error.localizedDescription
            appendLog("Autostart: \(error.localizedDescription)")
        }
        loginItemStatus = LoginItem.statusDescription
    }

    func checkForUpdates() {
        Task { @MainActor in await updater.check(silent: false) }
    }

    func installUpdate() {
        Task { @MainActor in await updater.install(codesignIdentity: settings.codesignIdentity) }
    }

    var statusSummary: (text: String, color: Color) {
        if !isListening { return ("Nie nasłuchuje", .red) }
        if let peer {
            if cursorOnMac { return ("Sterowanie z \(peer.name)", .green) }
            return ("Połączono z \(peer.name)", .green) }
        return ("Czeka na Windows", .orange)
    }

    // MARK: - Akcje

    func refreshPermissions() {
        let granted = InputInjector.isAccessibilityTrusted
        if granted != accessibilityGranted {
            accessibilityGranted = granted
            engine.setAccessibilityGranted(granted)
            appendLog(granted ? "Uprawnienie Dostępność nadane" : "Brak uprawnienia Dostępność")
        }
    }

    func requestAccessibility() {
        InputInjector.requestAccessibility()
        refreshPermissions()
    }

    func openAccessibilitySettings() {
        open("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")
    }

    func openAudioCaptureSettings() {
        open("x-apple.systempreferences:com.apple.preference.security?Privacy_AudioCapture")
    }

    func disconnectPeer() { engine.disconnectPeer() }
    func stopAudio() { engine.stopAudio() }
    func restartServer() { engine.restartServer() }

    func shutdown() {
        permissionTimer?.invalidate()
        updateTimer?.invalidate()
        engine.stop()
    }

    private func open(_ url: String) {
        if let u = URL(string: url) { NSWorkspace.shared.open(u) }
    }

    // MARK: - Zdarzenia z silnika

    private func apply(_ event: Engine.Event) {
        switch event {
        case let .listening(ok, error):
            isListening = ok
            listenError = error
        case let .peerConnected(info):
            peer = info
        case .peerDisconnected:
            peer = nil
            cursorOnMac = false
        case let .cursorOnMac(active):
            cursorOnMac = active
        case let .audioStarted(desc):
            audioStreaming = true
            audioDescription = desc
            audioError = nil
            audioPackets = 0
            audioSendErrors = 0
        case .audioStopped:
            audioStreaming = false
            audioDescription = ""
            audioLevel = 0
        case let .audioError(message):
            audioStreaming = false
            audioError = message
        case let .audioLevel(level):
            audioLevel = level
        case let .audioStats(packets, errors):
            audioPackets = packets
            audioSendErrors = errors
        case let .clipboardSent(summary):
            clipboardStatus = "Wysłano \(summary) do Windowsa · \(Self.timeFormatter.string(from: Date()))"
        case let .clipboardReceived(summary):
            clipboardStatus = "Odebrano \(summary) z Windowsa · \(Self.timeFormatter.string(from: Date()))"
        case let .clipboardError(message):
            clipboardStatus = message
        case let .log(text):
            appendLog(text)
        }
    }

    private static let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss"
        return f
    }()

    private func appendLog(_ text: String) {
        logger.notice("\(text, privacy: .public)")
        log.append(LogEntry(time: Date(), text: text))
        if log.count > 200 { log.removeFirst(log.count - 200) }
    }
}
