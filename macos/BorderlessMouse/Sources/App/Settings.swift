import Foundation

enum ScrollDirectionMode: String, Codable, CaseIterable, Identifiable {
    case auto, normal, inverted
    var id: String { rawValue }
    var label: String {
        switch self {
        case .auto: return L10n.text("Automatycznie", "Automatic")
        case .normal: return L10n.text("Jak na Windowsie", "Match Windows")
        case .inverted: return L10n.text("Odwrócony", "Inverted")
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
    var hasCompletedOnboarding = false
    /// Nazwa certyfikatu w pęku kluczy, którym updater podpisze pobraną wersję
    /// (dla lokalnych buildów). Puste = zachowaj stały podpis bundle z GitHuba.
    var codesignIdentity = ""

    init() {}

    private enum CodingKeys: String, CodingKey {
        case deviceName, controlPort, inputEnabled, audioEnabled, muteLocalAudio
        case swapCtrlCmd, scrollDirection, scrollSpeed, audioBufferFrames
        case clipboardSyncEnabled, autoCheckUpdates, launchAtLogin, startHidden
        case hasCompletedOnboarding, codesignIdentity
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        deviceName = try values.decodeIfPresent(String.self, forKey: .deviceName) ?? deviceName
        controlPort = try values.decodeIfPresent(Int.self, forKey: .controlPort) ?? controlPort
        inputEnabled = try values.decodeIfPresent(Bool.self, forKey: .inputEnabled) ?? inputEnabled
        audioEnabled = try values.decodeIfPresent(Bool.self, forKey: .audioEnabled) ?? audioEnabled
        muteLocalAudio = try values.decodeIfPresent(Bool.self, forKey: .muteLocalAudio) ?? muteLocalAudio
        swapCtrlCmd = try values.decodeIfPresent(Bool.self, forKey: .swapCtrlCmd) ?? swapCtrlCmd
        scrollDirection = try values.decodeIfPresent(ScrollDirectionMode.self, forKey: .scrollDirection) ?? scrollDirection
        scrollSpeed = try values.decodeIfPresent(Double.self, forKey: .scrollSpeed) ?? scrollSpeed
        audioBufferFrames = try values.decodeIfPresent(Int.self, forKey: .audioBufferFrames) ?? audioBufferFrames
        clipboardSyncEnabled = try values.decodeIfPresent(Bool.self, forKey: .clipboardSyncEnabled) ?? clipboardSyncEnabled
        autoCheckUpdates = try values.decodeIfPresent(Bool.self, forKey: .autoCheckUpdates) ?? autoCheckUpdates
        launchAtLogin = try values.decodeIfPresent(Bool.self, forKey: .launchAtLogin) ?? launchAtLogin
        startHidden = try values.decodeIfPresent(Bool.self, forKey: .startHidden) ?? startHidden
        hasCompletedOnboarding = try values.decodeIfPresent(Bool.self, forKey: .hasCompletedOnboarding) ?? false
        codesignIdentity = try values.decodeIfPresent(String.self, forKey: .codesignIdentity) ?? codesignIdentity
    }

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
                      clipboardSync: clipboardSyncEnabled,
                      pairingKey: PairingKeyStore.shared.key)
    }
}
