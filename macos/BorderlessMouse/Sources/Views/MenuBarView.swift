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
        Toggle("Przyjmuj klawiaturę i mysz", isOn: $state.settings.inputEnabled)
        Toggle("Udostępniaj dźwięk", isOn: $state.settings.audioEnabled)
        Toggle("Wycisz Maca podczas streamu", isOn: $state.settings.muteLocalAudio)
        Toggle("Synchronizuj schowek", isOn: $state.settings.clipboardSyncEnabled)
        Toggle("Uruchamiaj przy logowaniu", isOn: $state.settings.launchAtLogin)
        Divider()
        Button("Otwórz okno BorderlessMouse") {
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
        if state.peer != nil {
            Button("Rozłącz") { state.disconnectPeer() }
        }
        Button("Sprawdź aktualizacje…") {
            state.checkForUpdates()
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
        Divider()
        Button("Zakończ") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }
}
