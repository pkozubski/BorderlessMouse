import SwiftUI

struct MenuBarView: View {
    @EnvironmentObject private var state: AppState
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        let s = state.statusSummary
        Text(s.text)
        if let peer = state.peer {
            Text("\(peer.name) · \(peer.address)")
        }
        Divider()
        Toggle(LocalizedStringKey("Przyjmuj klawiaturę i mysz"), isOn: $state.settings.inputEnabled)
        Toggle(LocalizedStringKey("Udostępniaj dźwięk"), isOn: $state.settings.audioEnabled)
        Toggle(LocalizedStringKey("Wycisz Maca podczas streamu"), isOn: $state.settings.muteLocalAudio)
        Toggle(LocalizedStringKey("Synchronizuj schowek"), isOn: $state.settings.clipboardSyncEnabled)
        Toggle(LocalizedStringKey("Uruchamiaj przy logowaniu"), isOn: $state.settings.launchAtLogin)
        Divider()
        Button(LocalizedStringKey("Otwórz okno BorderlessMouse")) {
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
        if state.peer != nil {
            Button(LocalizedStringKey("Rozłącz")) { state.disconnectPeer() }
        }
        Button(LocalizedStringKey("Sprawdź aktualizacje…")) {
            state.checkForUpdates()
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
        Divider()
        Button(LocalizedStringKey("Zakończ")) { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }
}
