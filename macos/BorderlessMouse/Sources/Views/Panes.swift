import SwiftUI

// Wszystkie panele używają standardowego `Form` w stylu `.grouped`,
// czyli tego samego układu co Ustawienia systemowe.

struct ConnectionPane: View {
    @EnvironmentObject private var state: AppState
    @State private var portText = ""

    var body: some View {
        Form {
            Section {
                LabeledContent("Nasłuchiwanie") {
                    StatusLabel(text: state.isListening ? "Aktywne" : "Nieaktywne",
                                color: state.isListening ? .green : .red)
                }
                LabeledContent("Komputer z Windows") {
                    if let peer = state.peer {
                        Text("\(peer.name) · \(peer.address)")
                    } else {
                        Text("Oczekiwanie").foregroundStyle(.secondary)
                    }
                }
                if state.peer != nil {
                    Button("Rozłącz") { state.disconnectPeer() }
                }
            } header: {
                Text("Połączenie")
            } footer: {
                Text(state.listenError ?? "Port TCP \(state.settings.controlPort), wykrywanie UDP \(Int(ProtocolConstants.discoveryPort)). Uruchom aplikację na Windowsie i wybierz tego Maca.")
            }

            Section {
                LabeledContent("Kursor") {
                    Text(state.cursorOnMac ? "Na Macu" : "Na Windowsie")
                        .foregroundStyle(state.cursorOnMac ? Color.green : Color.secondary)
                }
            } header: {
                Text("Sterowanie")
            } footer: {
                Text(state.cursorOnMac
                     ? "Sterujesz Makiem. Przesuń kursor przez krawędź, przez którą wszedł, aby wrócić na Windows."
                     : "Przesuń kursor przez krawędź ekranu Windows, aby przejść na Maca.")
            }

            Section {
                LabeledContent("Nazwa tego Maca") {
                    TextField("Nazwa", text: $state.settings.deviceName)
                        .labelsHidden()
                        .textFieldStyle(.roundedBorder)
                        .frame(width: 200)
                }
                LabeledContent("Port TCP") {
                    HStack {
                        TextField("47800", text: $portText)
                            .labelsHidden()
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 80)
                            .onSubmit(applyPort)
                        Button("Zastosuj", action: applyPort)
                    }
                }
            } header: {
                Text("Ten Mac")
            } footer: {
                Text("Nazwa jest widoczna na liście w aplikacji Windows. Zmiana portu restartuje nasłuchiwanie.")
            }
        }
        .formStyle(.grouped)
        .onAppear { portText = String(state.settings.controlPort) }
    }

    private func applyPort() {
        if let port = Int(portText), (1024...65535).contains(port) {
            state.settings.controlPort = port
        } else {
            portText = String(state.settings.controlPort)
        }
    }
}

struct InputPane: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Form {
            Section {
                Toggle(isOn: $state.settings.inputEnabled) {
                    Text("Przyjmuj klawiaturę i mysz z Windowsa")
                    Text("Windows steruje Makiem po przekroczeniu krawędzi ekranu.")
                }
            }

            Section {
                LabeledContent("Dostępność") {
                    if state.accessibilityGranted {
                        StatusLabel(text: "Nadane", color: .green)
                    } else {
                        HStack(spacing: 8) {
                            StatusLabel(text: "Wymagane", color: .orange)
                            Button("Poproś") { state.requestAccessibility() }
                            Button("Otwórz ustawienia") { state.openAccessibilitySettings() }
                        }
                    }
                }
            } header: {
                Text("Uprawnienie")
            } footer: {
                Text(state.accessibilityGranted
                     ? "Aplikacja może wstrzykiwać zdarzenia klawiatury i myszy."
                     : "Bez tego uprawnienia Mac natychmiast oddaje sterowanie Windowsowi. Ustawienia systemowe → Prywatność i ochrona → Dostępność.")
            }

            Section {
                Toggle(isOn: $state.settings.swapCtrlCmd) {
                    Text("Zamień Ctrl i Cmd")
                    Text("Ctrl+C na klawiaturze PC działa jak ⌘C na Macu.")
                }
                Picker("Kierunek przewijania", selection: $state.settings.scrollDirection) {
                    ForEach(ScrollDirectionMode.allCases) { Text($0.label).tag($0) }
                }
                LabeledContent("Szybkość przewijania") {
                    HStack {
                        Slider(value: $state.settings.scrollSpeed, in: 0.25...4, step: 0.25)
                            .frame(width: 180)
                        Text(String(format: "%.2f×", state.settings.scrollSpeed))
                            .font(.callout.monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                }
            } header: {
                Text("Zachowanie")
            } footer: {
                Text("Kierunek „Automatycznie” uwzględnia systemowe ustawienie naturalnego przewijania.")
            }
        }
        .formStyle(.grouped)
    }
}

struct AudioPane: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Form {
            Section {
                Toggle(isOn: $state.settings.audioEnabled) {
                    Text("Udostępniaj dźwięk systemowy")
                    Text("Windows pobiera cały dźwięk Maca przez Core Audio, bez sterowników.")
                }
                Toggle(isOn: $state.settings.muteLocalAudio) {
                    Text("Wycisz głośniki Maca podczas streamowania")
                    Text("Dźwięk słychać tylko na Windowsie.")
                }
                Picker("Bufor przechwytywania", selection: $state.settings.audioBufferFrames) {
                    Text("128 ramek (≈2,7 ms)").tag(128)
                    Text("256 ramek (≈5,3 ms)").tag(256)
                    Text("512 ramek (≈10,7 ms)").tag(512)
                }
            } footer: {
                Text("Mniejszy bufor daje niższe opóźnienie, większy jest odporniejszy na zakłócenia.")
            }

            Section {
                LabeledContent("Stan") {
                    if state.audioStreaming {
                        StatusLabel(text: "Streamowanie", color: .green)
                    } else if state.audioError != nil {
                        StatusLabel(text: "Błąd", color: .red)
                    } else {
                        StatusLabel(text: "Nieaktywne", color: .secondary)
                    }
                }
                if state.audioStreaming {
                    LabeledContent("Poziom") {
                        ProgressView(value: Double(state.audioLevel))
                            .progressViewStyle(.linear)
                            .frame(width: 180)
                    }
                    LabeledContent("Format") { Text(state.audioDescription) }
                    LabeledContent("Pakiety") {
                        Text("\(state.audioPackets), błędy wysyłki \(state.audioSendErrors)")
                            .monospacedDigit()
                    }
                    Button("Zatrzymaj stream") { state.stopAudio() }
                }
                Button("Otwórz ustawienia nagrywania dźwięku") { state.openAudioCaptureSettings() }
            } header: {
                Text("Stan strumienia")
            } footer: {
                Text(state.audioError
                     ?? "Stream włącza Windows. Przy pierwszym uruchomieniu macOS zapyta o zgodę na nagrywanie dźwięku systemowego.")
            }
        }
        .formStyle(.grouped)
    }
}

struct ClipboardPane: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Form {
            Section {
                Toggle(isOn: $state.settings.clipboardSyncEnabled) {
                    Text("Synchronizuj schowek")
                    Text("Skopiowany tekst pojawia się w schowku drugiego komputera w ok. 0,5 s, w obie strony.")
                }
                LabeledContent("Ostatnia synchronizacja") {
                    Text(state.clipboardStatus).foregroundStyle(.secondary)
                }
            } footer: {
                Text("Przesyłany jest tekst do 1 MB. Obrazy, pliki i formatowanie nie są synchronizowane.")
            }
        }
        .formStyle(.grouped)
    }
}

struct StartupPane: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Form {
            Section {
                Toggle(isOn: $state.settings.launchAtLogin) {
                    Text("Uruchamiaj przy logowaniu")
                    Text(state.loginItemError ?? state.loginItemStatus)
                }
                Toggle(isOn: $state.settings.startHidden) {
                    Text("Przy autostarcie nie otwieraj okna")
                    Text("Aplikacja czeka w pasku menu.")
                }
                .disabled(!state.settings.launchAtLogin)
            } footer: {
                Text("Pozycję widać w Ustawieniach systemowych → Ogólne → Elementy logowania.")
            }
        }
        .formStyle(.grouped)
    }
}

struct UpdatesPane: View {
    @EnvironmentObject private var state: AppState
    var body: some View {
        Form {
            Section {
                LabeledContent("Zainstalowana wersja") { Text(Updater.currentVersion).monospacedDigit() }
                LabeledContent("Stan") {
                    switch state.updater.state {
                    case .checking:
                        ProgressView().controlSize(.small)
                    case .available:
                        Button("Zainstaluj i uruchom ponownie") { state.installUpdate() }
                            .buttonStyle(.borderedProminent)
                    case .downloading(let progress):
                        ProgressView(value: progress).frame(width: 140)
                    case .installing:
                        ProgressView().controlSize(.small)
                    default:
                        Button("Sprawdź teraz") { state.checkForUpdates() }
                    }
                }
                if case .available(let release) = state.updater.state {
                    Link("Zobacz wydanie na GitHubie", destination: release.pageURL)
                }
            } footer: {
                Text(statusText)
            }

            if case .available(let release) = state.updater.state, !release.notes.isEmpty {
                Section("Co nowego") {
                    Text(release.notes.prefix(800))
                        .font(.callout)
                        .textSelection(.enabled)
                }
            }

            Section {
                Toggle(isOn: $state.settings.autoCheckUpdates) {
                    Text("Sprawdzaj automatycznie")
                    Text("Po starcie i co 6 godzin, przez GitHub Releases.")
                }
            }

            Section {
                LabeledContent("Certyfikat") {
                    TextField("np. BorderlessMouse Dev", text: $state.settings.codesignIdentity)
                        .labelsHidden()
                        .textFieldStyle(.roundedBorder)
                        .frame(width: 220)
                }
            } header: {
                Text("Podpisywanie aktualizacji")
            } footer: {
                Text("Wydania są podpisane ad-hoc, więc po aktualizacji macOS zapomina uprawnienie Dostępność. Podaj nazwę własnego certyfikatu z pęku kluczy, a updater podpisze nim pobraną wersję.")
            }
        }
        .formStyle(.grouped)
    }

    private var statusText: String {
        switch state.updater.state {
        case .idle: return "Sprawdź, czy jest nowsze wydanie."
        case .checking: return "Sprawdzanie…"
        case .upToDate(let when): return "Masz najnowszą wersję, sprawdzono \(when)."
        case .available(let release): return "Dostępna wersja \(release.version)."
        case .downloading: return "Pobieranie aktualizacji…"
        case .installing: return "Instalowanie, aplikacja uruchomi się ponownie."
        case .failed(let message): return message
        }
    }
}

struct LogPane: View {
    @EnvironmentObject private var state: AppState

    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss"
        return f
    }()

    var body: some View {
        Form {
            Section {
                if state.log.isEmpty {
                    Text("Brak zdarzeń").foregroundStyle(.secondary)
                } else {
                    ForEach(state.log.reversed()) { entry in
                        HStack(alignment: .firstTextBaseline, spacing: 10) {
                            Text(Self.formatter.string(from: entry.time))
                                .font(.callout.monospacedDigit())
                                .foregroundStyle(.secondary)
                            Text(entry.text)
                                .font(.callout)
                                .textSelection(.enabled)
                            Spacer(minLength: 0)
                        }
                    }
                }
            } header: {
                HStack {
                    Text("Ostatnie zdarzenia")
                    Spacer()
                    Button("Wyczyść") { state.clearLog() }
                        .buttonStyle(.link)
                        .disabled(state.log.isEmpty)
                }
            }
        }
        .formStyle(.grouped)
    }
}
