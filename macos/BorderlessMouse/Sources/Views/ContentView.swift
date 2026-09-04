import SwiftUI

/// Sekcje paska bocznego (NavigationSplitView), jak w Ustawieniach systemowych.
enum SidebarSection: String, CaseIterable, Identifiable {
    case connection, permissions, control, settings, log

    var id: String { rawValue }

    var title: String {
        switch self {
        case .connection: return L10n.localized("Połączenie")
        case .permissions: return L10n.localized("Uprawnienia")
        case .control: return L10n.localized("Sterowanie")
        case .settings: return L10n.localized("Ustawienia")
        case .log: return L10n.localized("Dziennik")
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
        .sheet(isPresented: Binding(
            get: { !state.settings.hasCompletedOnboarding },
            set: { presented in if !presented { state.completeOnboarding() } }
        )) {
            OnboardingView()
                .environmentObject(state)
                .interactiveDismissDisabled()
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

// MARK: - Pierwsze uruchomienie

private struct OnboardingView: View {
    @EnvironmentObject private var state: AppState
    @State private var step = 0

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                ForEach(0..<3, id: \.self) { index in
                    Capsule()
                        .fill(index <= step ? Color.accentColor : Color.secondary.opacity(0.22))
                        .frame(width: index == step ? 28 : 10, height: 6)
                }
                Spacer()
                Text("Krok \(step + 1) z 3")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            .padding(.bottom, 28)

            Group {
                switch step {
                case 0: welcome
                case 1: pairing
                default: permissions
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)

            HStack {
                if step > 0 { Button("Wstecz") { step -= 1 } }
                Spacer()
                if step < 2 {
                    Button("Dalej") { step += 1 }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.large)
                } else {
                    Button("Zakończ konfigurację") { state.completeOnboarding() }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.large)
                }
            }
            .padding(.top, 24)
        }
        .padding(32)
        .frame(width: 560, height: 430)
    }

    private var welcome: some View {
        VStack(alignment: .leading, spacing: 16) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable().interpolation(.high).frame(width: 64, height: 64)
            Text("Jedno biurko. Dwa komputery.")
                .font(.largeTitle.bold())
            Text("BorderlessMouse połączy klawiaturę, mysz, schowek i dźwięk między Windowsem a tym Makiem — lokalnie, bez konta i bez chmury.")
                .font(.title3).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Label("Dane pozostają w Twojej sieci lokalnej", systemImage: "house.and.flag")
                .foregroundStyle(.secondary)
        }
    }

    private var pairing: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Sparuj komputer z Windows")
                .font(.largeTitle.bold())
            Text("Uruchom BorderlessMouse na Windowsie i przepisz ten kod parowania. Sekret jest przechowywany w Pęku kluczy.")
                .font(.title3).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            pairingCode
            Button("Kopiuj kod") { state.copyPairingCode() }
                .controlSize(.large)
            if let error = state.pairingStorageError {
                Label(error, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.red)
            }
        }
    }

    private var permissions: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Nadaj wymagane uprawnienie")
                .font(.largeTitle.bold())
            Text("macOS musi zezwolić aplikacji na sterowanie kursorem i klawiaturą. Zgoda na dźwięk pojawi się dopiero przy pierwszym uruchomieniu strumienia.")
                .font(.title3).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 10) {
                StatusDot(ok: state.accessibilityGranted)
                Text(state.accessibilityGranted
                     ? L10n.text("Dostępność jest gotowa", "Accessibility is ready")
                     : L10n.text("Dostępność wymaga zgody", "Accessibility permission required"))
                    .font(.headline)
            }
            if !state.accessibilityGranted {
                HStack {
                    Button("Poproś o dostęp") { state.requestAccessibility() }
                        .buttonStyle(.borderedProminent)
                    Button("Otwórz Ustawienia systemowe") { state.openAccessibilitySettings() }
                }
                .controlSize(.large)
            }
        }
    }

    private var pairingCode: some View {
        Text(state.pairingCode)
            .font(.system(.title2, design: .monospaced).weight(.semibold))
            .textSelection(.enabled)
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 12))
            .accessibilityLabel(L10n.text("Kod parowania \(state.pairingCode)", "Pairing code \(state.pairingCode)"))
    }
}

// MARK: - Połączenie

struct ConnectionPage: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Form {
            Section("Gotowość") {
                SettingRow(title: state.isListening
                           ? L10n.text("Ten Mac jest gotowy", "This Mac is ready")
                           : L10n.text("Ten Mac nie jest dostępny", "This Mac is unavailable"),
                           subtitle: state.listenError ?? (state.peer == nil
                           ? L10n.text("Otwórz aplikację na Windowsie i użyj kodu parowania poniżej.", "Open the Windows app and use the pairing code below.")
                           : L10n.text("Połączenie jest uwierzytelnione i szyfrowane.", "The connection is authenticated and encrypted."))) {
                    HStack(spacing: 6) {
                        StatusDot(ok: state.isListening)
                        Text(state.isListening ? L10n.text("Gotowy", "Ready") : L10n.text("Wymaga uwagi", "Needs attention")).foregroundStyle(.secondary)
                    }
                }
            }
            Section("Bezpieczne parowanie") {
                VStack(alignment: .leading, spacing: 10) {
                    Text("Wpisz ten kod na komputerze z Windows. Nie udostępniaj go osobom spoza swojego biurka.")
                        .font(.callout).foregroundStyle(.secondary)
                    HStack(spacing: 10) {
                        Text(state.pairingCode)
                            .font(.system(.body, design: .monospaced).weight(.semibold))
                            .textSelection(.enabled)
                        Spacer()
                        Button("Kopiuj") { state.copyPairingCode() }
                    }
                    if let error = state.pairingStorageError {
                        Label(error, systemImage: "exclamationmark.triangle.fill")
                            .font(.caption).foregroundStyle(.red)
                    }
                }
                PairingResetRow()
            }
            Section("Komputer z Windows") {
                SettingRow(title: "Połączenie",
                           subtitle: state.peer.map { "\($0.name) · \($0.address)" }
                           ?? L10n.text("Uruchom aplikację na Windowsie i wybierz tego Maca", "Open the Windows app and select this Mac")) {
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
                           ? L10n.text("Sterujesz Makiem. Przesuń kursor przez krawędź, żeby wrócić na Windows.", "You are controlling the Mac. Cross the edge to return to Windows.")
                           : L10n.text("Przesuń kursor przez krawędź ekranu Windows, żeby przejść na Maca.", "Cross the selected Windows screen edge to control the Mac.")) {
                    Text(state.cursorOnMac ? "Na Macu" : "Na Windowsie")
                        .font(.callout.weight(.medium))
                        .foregroundStyle(state.cursorOnMac ? Color.green : Color.secondary)
                }
            }
            Section {
                DisclosureGroup("Szczegóły sieci") {
                    LabeledContent("Kanał sterowania", value: "TCP \(state.settings.controlPort) · AES-256-GCM")
                    LabeledContent("Wykrywanie", value: "UDP \(ProtocolConstants.discoveryPort)")
                    LabeledContent("Protokół", value: "v\(ProtocolConstants.version)")
                }
            }
        }
        .formStyle(.grouped)
    }
}

private struct PairingResetRow: View {
    @EnvironmentObject private var state: AppState
    @State private var confirming = false

    var body: some View {
        SettingRow(title: "Unieważnij dostęp",
                   subtitle: "Nowy kod natychmiast rozłączy komputer i unieważni poprzedni kod.") {
            Button("Wygeneruj nowy kod", role: .destructive) { confirming = true }
        }
        .alert("Wygenerować nowy kod parowania?", isPresented: $confirming) {
            Button("Anuluj", role: .cancel) { }
            Button("Wygeneruj", role: .destructive) { state.regeneratePairingCode() }
        } message: {
            Text("Windows będzie wymagał ponownego wpisania kodu.")
        }
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
                           ? L10n.text("Nadane – aplikacja może wstrzykiwać zdarzenia.", "Granted — the app can receive input.")
                           : L10n.text("Wymagane. Ustawienia → Prywatność i ochrona → Dostępność.", "Required. System Settings → Privacy & Security → Accessibility.")) {
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
                           subtitle: state.audioPermissionSubtitle) {
                    HStack(spacing: 8) {
                        if case .checking = state.audioPermission {
                            ProgressView().controlSize(.small)
                        } else {
                            StatusDot(ok: state.audioPermissionGranted)
                        }
                        if state.audioPermission.isGranted {
                            Text("OK").foregroundStyle(.secondary)
                            Button("Sprawdź") { state.checkAudioPermission(force: true) }
                        } else {
                            Button("Poproś") { state.checkAudioPermission(force: true) }
                            if state.audioPermissionGranted == false {
                                Button("Napraw zgodę") { state.repairAudioPermission() }
                            }
                            Button("Ustawienia") { state.openAudioCaptureSettings() }
                        }
                    }
                    .disabled({ if case .checking = state.audioPermission { return true }; return false }())
                }
            }
        }
        .formStyle(.grouped)
        .onAppear { state.checkAudioPermission(force: false) }
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
                            Text(L10n.text("Streamowanie: \(state.audioDescription)", "Streaming: \(state.audioDescription)"))
                                .font(.callout)
                            Spacer()
                            Button("Zatrzymaj") { state.stopAudio() }
                        } else if let error = state.audioError {
                            Text(error).font(.callout).foregroundStyle(.red)
                        } else {
                            Text(L10n.text("Nieaktywne – Windows włącza stream po stronie odbiornika.", "Inactive — start the receiver on Windows."))
                                .font(.callout).foregroundStyle(.secondary)
                        }
                    }
                    LevelMeter(level: state.audioLevel)
                    if state.audioStreaming {
                        Text(L10n.text("Pakiety: \(state.audioPackets) · błędy wysyłki: \(state.audioSendErrors)",
                                       "Packets: \(state.audioPackets) · send errors: \(state.audioSendErrors)"))
                            .monospacedDigit()
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
                SettingRow(title: L10n.text("Wersja \(Updater.currentVersion)", "Version \(Updater.currentVersion)"), subtitle: statusText) {
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
                        Text(L10n.text(
                            "Bezpłatna beta ma stały podpis bundle i osobny podpis ECDSA aktualizacji, ale nie jest notaryzowana przez Apple. Pole poniżej zostaw puste, chyba że podpisujesz lokalne buildy własnym certyfikatem.",
                            "The free beta has a stable bundle signature and a separate ECDSA update signature, but is not notarized by Apple. Leave the field below empty unless you sign local builds with your own certificate."))
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
        case .idle: return L10n.text("Kliknij „Sprawdź teraz”, aby sprawdzić nowe wydania.", "Select Check now to look for new releases.")
        case .checking: return L10n.text("Sprawdzanie…", "Checking…")
        case .upToDate(let when): return L10n.text("Masz najnowszą wersję · sprawdzono \(when)", "Up to date · checked \(when)")
        case .available(let r): return L10n.text("Dostępna wersja \(r.version)", "Version \(r.version) available")
        case .downloading: return L10n.text("Pobieranie aktualizacji…", "Downloading update…")
        case .installing: return L10n.text("Instalowanie – aplikacja uruchomi się ponownie.", "Installing — the app will restart.")
        case .failed(let msg): return L10n.text("Błąd: \(msg)", "Error: \(msg)")
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
