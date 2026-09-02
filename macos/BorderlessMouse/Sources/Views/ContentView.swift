import SwiftUI

struct ContentView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        ScrollView {
            VStack(spacing: 14) {
                HeaderView()
                ConnectionCard()
                PermissionsCard()
                InputCard()
                AudioCard()
                ClipboardCard()
                SettingsCard()
                UpdatesCard()
                LogCard()
            }
            .padding(20)
        }
        .frame(minWidth: 540, idealWidth: 560, maxWidth: 720, minHeight: 620, idealHeight: 860)
        .background(Color(nsColor: .windowBackgroundColor))
    }
}

struct HeaderView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        HStack(spacing: 14) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .interpolation(.high)
                .frame(width: 56, height: 56)
            VStack(alignment: .leading, spacing: 2) {
                Text("BorderlessMouse").font(.title2.weight(.semibold))
                Text("Mac: odbiera klawiaturę i mysz, nadaje dźwięk")
                    .font(.callout).foregroundStyle(.secondary)
            }
            Spacer()
            let s = state.statusSummary
            StatusPill(text: s.text, color: s.color)
        }
        .padding(.bottom, 4)
    }
}

struct ConnectionCard: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Card("Połączenie", systemImage: "network") {
            SettingRow(title: "Nasłuchiwanie TCP",
                       subtitle: state.listenError ?? "Port \(state.settings.controlPort) · discovery UDP \(ProtocolConstants.discoveryPort)") {
                HStack(spacing: 6) {
                    StatusDot(ok: state.isListening)
                    Text(state.isListening ? "Aktywne" : "Nieaktywne").foregroundStyle(.secondary)
                }
            }
            Divider()
            SettingRow(title: "Komputer z Windows",
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
            Divider()
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
}

struct PermissionsCard: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Card("Uprawnienia macOS", systemImage: "lock.shield") {
            SettingRow(title: "Dostępność (sterowanie myszą i klawiaturą)",
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
            Divider()
            SettingRow(title: "Nagrywanie dźwięku systemowego",
                       subtitle: "macOS zapyta przy pierwszym streamie. Ustawienia → Prywatność → Nagrywanie ekranu i dźwięku systemowego.") {
                HStack(spacing: 8) {
                    StatusDot(ok: state.audioStreaming ? true : (state.audioError == nil ? nil : false))
                    Button("Ustawienia") { state.openAudioCaptureSettings() }
                }
            }
        }
    }
}

struct InputCard: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Card("Klawiatura i mysz", systemImage: "keyboard") {
            SettingRow(title: "Przyjmuj klawiaturę i mysz z Windowsa",
                       subtitle: "Windows steruje Makiem po przekroczeniu krawędzi ekranu.") {
                Toggle("", isOn: $state.settings.inputEnabled).labelsHidden().toggleStyle(.switch)
            }
            Divider()
            SettingRow(title: "Zamień Ctrl ↔ Cmd",
                       subtitle: "Ctrl+C na klawiaturze PC działa jak ⌘C na Macu.") {
                Toggle("", isOn: $state.settings.swapCtrlCmd).labelsHidden().toggleStyle(.switch)
            }
            Divider()
            SettingRow(title: "Kierunek przewijania",
                       subtitle: "Automatycznie uwzględnia ustawienie „naturalne przewijanie”.") {
                Picker("", selection: $state.settings.scrollDirection) {
                    ForEach(ScrollDirectionMode.allCases) { Text($0.label).tag($0) }
                }
                .labelsHidden()
                .frame(width: 170)
            }
            Divider()
            SettingRow(title: "Szybkość przewijania",
                       subtitle: String(format: "%.1f×", state.settings.scrollSpeed)) {
                Slider(value: $state.settings.scrollSpeed, in: 0.25...4, step: 0.25).frame(width: 170)
            }
        }
    }
}

struct AudioCard: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Card("Dźwięk do Windowsa", systemImage: "speaker.wave.2") {
            SettingRow(title: "Udostępniaj dźwięk systemowy",
                       subtitle: "Windows pobiera cały dźwięk Maca (Core Audio tap, bez sterowników).") {
                Toggle("", isOn: $state.settings.audioEnabled).labelsHidden().toggleStyle(.switch)
            }
            Divider()
            SettingRow(title: "Wycisz głośniki Maca podczas streamowania",
                       subtitle: "Dźwięk słychać tylko na Windowsie.") {
                Toggle("", isOn: $state.settings.muteLocalAudio).labelsHidden().toggleStyle(.switch)
            }
            Divider()
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
            Divider()
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
        }
    }
}

struct ClipboardCard: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Card("Schowek", systemImage: "doc.on.clipboard") {
            SettingRow(title: "Synchronizuj schowek (tekst i zdjęcia)",
                       subtitle: "Kopiuj i wklejaj tekst, zdjęcia oraz zrzuty ekranu w obie strony. Obrazy do 32 MiB.") {
                Toggle("", isOn: $state.settings.clipboardSyncEnabled).labelsHidden().toggleStyle(.switch)
            }
            Divider()
            SettingRow(title: "Ostatnia synchronizacja", subtitle: state.clipboardStatus) {
                StatusDot(ok: state.settings.clipboardSyncEnabled ? (state.peer != nil ? true : nil) : false)
            }
        }
    }
}

struct SettingsCard: View {
    @EnvironmentObject private var state: AppState
    @State private var portText = ""

    var body: some View {
        Card("Ustawienia", systemImage: "gearshape") {
            SettingRow(title: "Nazwa tego Maca", subtitle: "Widoczna na liście w aplikacji Windows.") {
                TextField("Nazwa", text: $state.settings.deviceName)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 200)
            }
            Divider()
            SettingRow(title: "Uruchamiaj przy logowaniu", subtitle: state.loginItemError ?? state.loginItemStatus) {
                Toggle("", isOn: $state.settings.launchAtLogin).labelsHidden().toggleStyle(.switch)
            }
            Divider()
            SettingRow(title: "Przy autostarcie nie otwieraj okna", subtitle: "Aplikacja czeka w pasku menu.") {
                Toggle("", isOn: $state.settings.startHidden).labelsHidden().toggleStyle(.switch)
                    .disabled(!state.settings.launchAtLogin)
            }
            Divider()
            SettingRow(title: "Port TCP", subtitle: "Zmiana restartuje nasłuchiwanie.") {
                HStack {
                    TextField("47800", text: $portText)
                        .textFieldStyle(.roundedBorder)
                        .frame(width: 90)
                        .onSubmit(applyPort)
                    Button("Zastosuj", action: applyPort)
                }
            }
        }
        .onAppear { portText = String(state.settings.controlPort) }
    }

    private func applyPort() {
        if let p = Int(portText), (1024...65535).contains(p) {
            state.settings.controlPort = p
        } else {
            portText = String(state.settings.controlPort)
        }
    }
}

struct UpdatesCard: View {
    @EnvironmentObject private var state: AppState
    @State private var showAdvanced = false

    var body: some View {
        Card("Aktualizacje", systemImage: "arrow.down.circle") {
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
                Text(release.notes.prefix(600))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(8)
                    .textSelection(.enabled)
                Link("Zobacz wydanie na GitHubie", destination: release.pageURL).font(.caption)
            }
            Divider()
            SettingRow(title: "Sprawdzaj automatycznie", subtitle: "Po starcie i co 6 godzin, przez GitHub Releases.") {
                Toggle("", isOn: $state.settings.autoCheckUpdates).labelsHidden().toggleStyle(.switch)
            }
            Divider()
            DisclosureGroup("Zaawansowane: podpisywanie aktualizacji", isExpanded: $showAdvanced) {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Wydania od 1.3.4 używają stałego podpisu, aby zachować uprawnienia po aktualizacji. Przy przejściu ze starszej wersji może być potrzebna jeszcze jedna zgoda. Pole poniżej zostaw puste, chyba że używasz własnego certyfikatu do lokalnych buildów.")
                        .font(.caption).foregroundStyle(.secondary)
                    TextField("Opcjonalny własny certyfikat", text: $state.settings.codesignIdentity)
                        .textFieldStyle(.roundedBorder)
                }
                .padding(.top, 6)
            }
            .font(.callout)
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

struct LogCard: View {
    @EnvironmentObject private var state: AppState
    @State private var expanded = false

    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss"
        return f
    }()

    var body: some View {
        Card("Dziennik", systemImage: "list.bullet.rectangle") {
            DisclosureGroup(isExpanded: $expanded) {
                VStack(alignment: .leading, spacing: 3) {
                    ForEach(state.log.suffix(30)) { entry in
                        HStack(alignment: .top, spacing: 8) {
                            Text(Self.formatter.string(from: entry.time)).foregroundStyle(.secondary)
                            Text(entry.text)
                        }
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                    }
                }
                .padding(.top, 6)
            } label: {
                Text(state.log.last?.text ?? "Brak zdarzeń")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
    }
}
