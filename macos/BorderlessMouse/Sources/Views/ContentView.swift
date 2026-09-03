import SwiftUI

/// Sekcje paska bocznego (NavigationSplitView), jak w Ustawieniach systemowych.
enum SidebarSection: String, CaseIterable, Identifiable {
    case connection, permissions, control, settings, log

    var id: String { rawValue }

    var title: String {
        switch self {
        case .connection: return "Połączenie"
        case .permissions: return "Uprawnienia"
        case .control: return "Sterowanie"
        case .settings: return "Ustawienia"
        case .log: return "Dziennik"
        }
    }

    var systemImage: String {
        switch self {
        case .connection: return "network"
        case .permissions: return "lock.shield"
        case .control: return "keyboard"
        case .settings: return "gearshape"
        case .log: return "list.bullet.rectangle"
        }
    }
}

struct ContentView: View {
    @EnvironmentObject private var state: AppState
    @State private var selection: SidebarSection?

    init(initialSection: SidebarSection = .connection) {
        _selection = State(initialValue: initialSection)
    }

    var body: some View {
        NavigationSplitView {
            SidebarView(selection: $selection)
        } detail: {
            DetailView(section: selection ?? .connection)
        }
        .frame(minWidth: 780, idealWidth: 880, minHeight: 540, idealHeight: 620)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                // natywny element paska narzędzi: bez własnego tła, system nakłada swój materiał
                let s = state.statusSummary
                HStack(spacing: 6) {
                    Circle().fill(s.color).frame(width: 8, height: 8)
                    Text(s.text).font(.callout.weight(.medium)).foregroundStyle(.primary)
                }
                .padding(.horizontal, 6)
            }
        }
    }
}

struct SidebarView: View {
    @EnvironmentObject private var state: AppState
    @Binding var selection: SidebarSection?

    var body: some View {
        List(selection: $selection) {
            Section {
                ForEach([SidebarSection.connection, .permissions, .control, .settings]) { section in
                    Label(section.title, systemImage: section.systemImage).tag(section)
                }
            } header: {
                SidebarHeader()
            }
            Section {
                Label(SidebarSection.log.title, systemImage: SidebarSection.log.systemImage)
                    .tag(SidebarSection.log)
            }
        }
        .listStyle(.sidebar)
        .navigationSplitViewColumnWidth(min: 190, ideal: 210, max: 260)
    }
}

private struct SidebarHeader: View {
    var body: some View {
        HStack(spacing: 10) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .interpolation(.high)
                .frame(width: 36, height: 36)
            VStack(alignment: .leading, spacing: 1) {
                Text("BorderlessMouse").font(.headline).foregroundStyle(.primary)
                Text("Mac").font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 6)
        .textCase(nil)
    }
}

struct DetailView: View {
    let section: SidebarSection

    var body: some View {
        Group {
            switch section {
            case .connection: ConnectionPage()
            case .permissions: PermissionsPage()
            case .control: ControlPage()
            case .settings: SettingsPage()
            case .log: LogPage()
            }
        }
        .navigationTitle(section.title)
        .navigationSubtitle("BorderlessMouse")
    }
}

// MARK: - Połączenie

struct ConnectionPage: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Form {
            Section("Serwer") {
                SettingRow(title: "Nasłuchiwanie TCP",
                           subtitle: state.listenError ?? "Port \(state.settings.controlPort) · discovery UDP \(ProtocolConstants.discoveryPort)") {
                    HStack(spacing: 6) {
                        StatusDot(ok: state.isListening)
                        Text(state.isListening ? "Aktywne" : "Nieaktywne").foregroundStyle(.secondary)
                    }
                }
            }
            Section("Komputer z Windows") {
                SettingRow(title: "Połączenie",
                           subtitle: state.peer.map { "\($0.name) · \($0.address)" } ?? "Uruchom aplikację na Windowsie i wybierz tego Maca") {
                    if state.peer != nil {
                        Button("Rozłącz") { state.disconnectPeer() }
                    } else {
                        HStack(spacing: 6) {
                            StatusDot(ok: nil)
                            Text("Oczekiwanie").foregroundStyle(.secondary)
                        }
                    }
                }
                SettingRow(title: "Kursor",
                           subtitle: state.cursorOnMac
                           ? "Sterujesz Makiem. Przesuń kursor przez krawędź, żeby wrócić na Windows."
                           : "Przesuń kursor przez krawędź ekranu Windows, żeby przejść na Maca.") {
                    Text(state.cursorOnMac ? "Na Macu" : "Na Windowsie")
                        .font(.callout.weight(.medium))
                        .foregroundStyle(state.cursorOnMac ? Color.green : Color.secondary)
                }
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: - Uprawnienia

struct PermissionsPage: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Form {
            Section("Sterowanie myszą i klawiaturą") {
                SettingRow(title: "Dostępność",
                           subtitle: state.accessibilityGranted
                           ? "Nadane – aplikacja może wstrzykiwać zdarzenia."
                           : "Wymagane. Ustawienia → Prywatność i ochrona → Dostępność.") {
                    HStack(spacing: 8) {
                        StatusDot(ok: state.accessibilityGranted)
                        if state.accessibilityGranted {
                            Text("OK").foregroundStyle(.secondary)
                        } else {
                            Button("Poproś") { state.requestAccessibility() }
                            Button("Ustawienia") { state.openAccessibilitySettings() }
                        }
                    }
                }
            }
            Section("Dźwięk") {
                SettingRow(title: "Nagrywanie dźwięku systemowego",
                           subtitle: "macOS zapyta przy pierwszym streamie. Ustawienia → Prywatność → Nagrywanie ekranu i dźwięku systemowego.") {
                    HStack(spacing: 8) {
                        StatusDot(ok: state.audioStreaming ? true : (state.audioError == nil ? nil : false))
                        Button("Ustawienia") { state.openAudioCaptureSettings() }
                    }
                }
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: - Sterowanie

struct ControlPage: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Form {
            Section("Klawiatura i mysz") {
                SettingRow(title: "Przyjmuj klawiaturę i mysz z Windowsa",
                           subtitle: "Windows steruje Makiem po przekroczeniu krawędzi ekranu.") {
                    Toggle("", isOn: $state.settings.inputEnabled).labelsHidden().toggleStyle(.switch)
                }
                SettingRow(title: "Zamień Ctrl ↔ Cmd",
                           subtitle: "Ctrl+C na klawiaturze PC działa jak ⌘C na Macu.") {
                    Toggle("", isOn: $state.settings.swapCtrlCmd).labelsHidden().toggleStyle(.switch)
                }
                SettingRow(title: "Kierunek przewijania",
                           subtitle: "Automatycznie uwzględnia ustawienie „naturalne przewijanie”.") {
                    Picker("", selection: $state.settings.scrollDirection) {
                        ForEach(ScrollDirectionMode.allCases) { Text($0.label).tag($0) }
                    }
                    .labelsHidden()
                    .frame(width: 170)
                }
                SettingRow(title: "Szybkość przewijania",
                           subtitle: String(format: "%.1f×", state.settings.scrollSpeed)) {
                    Slider(value: $state.settings.scrollSpeed, in: 0.25...4, step: 0.25).frame(width: 170)
                }
            }
            Section("Dźwięk do Windowsa") {
                SettingRow(title: "Udostępniaj dźwięk systemowy",
                           subtitle: "Windows pobiera cały dźwięk Maca (Core Audio tap, bez sterowników).") {
                    Toggle("", isOn: $state.settings.audioEnabled).labelsHidden().toggleStyle(.switch)
                }
                SettingRow(title: "Wycisz głośniki Maca podczas streamowania",
                           subtitle: "Dźwięk słychać tylko na Windowsie.") {
                    Toggle("", isOn: $state.settings.muteLocalAudio).labelsHidden().toggleStyle(.switch)
                }
                SettingRow(title: "Bufor przechwytywania",
                           subtitle: "Mniejszy = niższe opóźnienie, większy = odporniejszy.") {
                    Picker("", selection: $state.settings.audioBufferFrames) {
                        Text("128 ramek (~2,7 ms)").tag(128)
                        Text("256 ramek (~5,3 ms)").tag(256)
                        Text("512 ramek (~10,7 ms)").tag(512)
                    }
                    .labelsHidden()
                    .frame(width: 190)
                }
            }
            Section("Stan strumienia") {
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        StatusDot(ok: state.audioStreaming ? true : (state.audioError == nil ? nil : false))
                        if state.audioStreaming {
                            Text("Streamowanie: \(state.audioDescription)").font(.callout)
                            Spacer()
                            Button("Zatrzymaj") { state.stopAudio() }
                        } else if let error = state.audioError {
                            Text(error).font(.callout).foregroundStyle(.red)
                        } else {
                            Text("Nieaktywne – Windows włącza stream po stronie odbiornika.")
                                .font(.callout).foregroundStyle(.secondary)
                        }
                    }
                    LevelMeter(level: state.audioLevel)
                    if state.audioStreaming {
                        Text("Pakiety: \(state.audioPackets) · błędy wysyłki: \(state.audioSendErrors)")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
                .padding(.vertical, 2)
            }
            Section("Schowek") {
                SettingRow(title: "Synchronizuj schowek (tekst i zdjęcia)",
                           subtitle: "Kopiuj i wklejaj tekst, zdjęcia oraz zrzuty ekranu w obie strony. Obrazy do 32 MiB.") {
                    Toggle("", isOn: $state.settings.clipboardSyncEnabled).labelsHidden().toggleStyle(.switch)
                }
                SettingRow(title: "Ostatnia synchronizacja", subtitle: state.clipboardStatus) {
                    StatusDot(ok: state.settings.clipboardSyncEnabled ? (state.peer != nil ? true : nil) : false)
                }
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: - Ustawienia

struct SettingsPage: View {
    @EnvironmentObject private var state: AppState
    @State private var portText = ""
    @State private var showAdvanced = false

    var body: some View {
        Form {
            Section("Ogólne") {
                SettingRow(title: "Nazwa tego Maca", subtitle: "Widoczna na liście w aplikacji Windows.") {
                    TextField("", text: $state.settings.deviceName, prompt: Text("Nazwa"))
                        .labelsHidden()
                        .textFieldStyle(.roundedBorder)
                        .frame(width: 200)
                }
                SettingRow(title: "Port TCP", subtitle: "Zmiana restartuje nasłuchiwanie.") {
                    HStack {
                        TextField("", text: $portText, prompt: Text("47800"))
                            .labelsHidden()
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 90)
                            .onSubmit(applyPort)
                        Button("Zastosuj", action: applyPort)
                    }
                }
            }
            Section("Uruchamianie") {
                SettingRow(title: "Uruchamiaj przy logowaniu", subtitle: state.loginItemError ?? state.loginItemStatus) {
                    Toggle("", isOn: $state.settings.launchAtLogin).labelsHidden().toggleStyle(.switch)
                }
                SettingRow(title: "Przy autostarcie nie otwieraj okna", subtitle: "Aplikacja czeka w pasku menu.") {
                    Toggle("", isOn: $state.settings.startHidden).labelsHidden().toggleStyle(.switch)
                        .disabled(!state.settings.launchAtLogin)
                }
            }
            Section("Aktualizacje") {
                SettingRow(title: "Wersja \(Updater.currentVersion)", subtitle: statusText) {
                    HStack(spacing: 8) {
                        switch state.updater.state {
                        case .checking:
                            ProgressView().controlSize(.small)
                        case .available:
                            Button("Zainstaluj i uruchom ponownie") { state.installUpdate() }
                                .buttonStyle(.borderedProminent)
                        case .downloading(let p):
                            ProgressView(value: p).frame(width: 120)
                        case .installing:
                            ProgressView().controlSize(.small)
                        default:
                            Button("Sprawdź teraz") { state.checkForUpdates() }
                        }
                    }
                }
                if case .available(let release) = state.updater.state, !release.notes.isEmpty {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(release.notes.prefix(600))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(8)
                            .textSelection(.enabled)
                        Link("Zobacz wydanie na GitHubie", destination: release.pageURL).font(.caption)
                    }
                }
                SettingRow(title: "Sprawdzaj automatycznie", subtitle: "Po starcie i co 6 godzin, przez GitHub Releases.") {
                    Toggle("", isOn: $state.settings.autoCheckUpdates).labelsHidden().toggleStyle(.switch)
                }
                DisclosureGroup("Zaawansowane: podpisywanie aktualizacji", isExpanded: $showAdvanced) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Wydania od 1.3.4 używają stałego podpisu, aby zachować uprawnienia po aktualizacji. Przy przejściu ze starszej wersji może być potrzebna jeszcze jedna zgoda. Pole poniżej zostaw puste, chyba że używasz własnego certyfikatu do lokalnych buildów.")
                            .font(.caption).foregroundStyle(.secondary)
                        TextField("", text: $state.settings.codesignIdentity, prompt: Text("Opcjonalny własny certyfikat"))
                            .labelsHidden()
                            .textFieldStyle(.roundedBorder)
                    }
                    .padding(.top, 6)
                }
            }
        }
        .formStyle(.grouped)
        .onAppear { portText = String(state.settings.controlPort) }
    }

    private func applyPort() {
        if let p = Int(portText), (1024...65535).contains(p) {
            state.settings.controlPort = p
        } else {
            portText = String(state.settings.controlPort)
        }
    }

    private var statusText: String {
        switch state.updater.state {
        case .idle: return "Kliknij „Sprawdź teraz”, aby sprawdzić nowe wydania."
        case .checking: return "Sprawdzanie…"
        case .upToDate(let when): return "Masz najnowszą wersję · sprawdzono \(when)"
        case .available(let r): return "Dostępna wersja \(r.version)"
        case .downloading: return "Pobieranie aktualizacji…"
        case .installing: return "Instalowanie – aplikacja uruchomi się ponownie."
        case .failed(let msg): return "Błąd: \(msg)"
        }
    }
}

// MARK: - Dziennik

struct LogPage: View {
    @EnvironmentObject private var state: AppState

    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss"
        return f
    }()

    var body: some View {
        List {
            if state.log.isEmpty {
                Text("Brak zdarzeń").foregroundStyle(.secondary)
            }
            ForEach(state.log.suffix(200)) { entry in
                HStack(alignment: .top, spacing: 8) {
                    Text(Self.formatter.string(from: entry.time)).foregroundStyle(.secondary)
                    Text(entry.text)
                }
                .font(.system(.caption, design: .monospaced))
                .textSelection(.enabled)
            }
        }
        .listStyle(.inset(alternatesRowBackgrounds: true))
    }
}
