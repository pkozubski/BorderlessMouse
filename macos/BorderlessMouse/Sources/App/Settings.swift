import Foundation

enum ScrollDirectionMode: String, Codable, CaseIterable, Identifiable {
    case auto, normal, inverted
    var id: String { rawValue }
    var label: String {
        switch self {
        case .auto: return "Automatycznie"
        case .normal: return "Jak na Windowsie"
        case .inverted: return "Odwrócony"
        }
    }
    var forcedInvert: Bool? {
        switch self {
        case .auto: return nil
        case .normal: return false
        case .inverted: return true
        }
    }
}

struct Settings: Codable, Equatable {
    var deviceName: String = Host.current().localizedName ?? "Mac"
    var controlPort: Int = Int(ProtocolConstants.defaultControlPort)
    var inputEnabled = true
    var audioEnabled = true
    var muteLocalAudio = true
    var swapCtrlCmd = true
    var scrollDirection: ScrollDirectionMode = .auto
    var scrollSpeed: Double = 1.0
    var audioBufferFrames: Int = 256
    var clipboardSyncEnabled = true
    var autoCheckUpdates = true
    var launchAtLogin = false
    /// Przy starcie z logowania nie otwieraj okna – tylko ikona w pasku menu.
    var startHidden = true
    /// Nazwa certyfikatu w pęku kluczy, którym updater podpisze pobraną wersję
    /// (żeby uprawnienia Dostępność przeżyły aktualizację). Puste = bez podpisywania.
    var codesignIdentity = ""

    private static let key = "blm.settings"

    static func load() -> Settings {
        guard let data = UserDefaults.standard.data(forKey: key),
              let s = try? JSONDecoder().decode(Settings.self, from: data) else { return Settings() }
        return s
    }

    func save() {
        if let data = try? JSONEncoder().encode(self) {
            UserDefaults.standard.set(data, forKey: Self.key)
        }
    }

    var engineConfig: Engine.Config {
        Engine.Config(deviceName: deviceName,
                      controlPort: UInt16(clamping: controlPort),
                      inputEnabled: inputEnabled,
                      audioEnabled: audioEnabled,
                      muteLocalAudio: muteLocalAudio,
                      swapCtrlCmd: swapCtrlCmd,
                      invertScroll: scrollDirection.forcedInvert,
                      scrollPixelsPerNotch: 40 * scrollSpeed,
                      audioBufferFrames: UInt32(clamping: audioBufferFrames),
                      clipboardSync: clipboardSyncEnabled)
    }
}
