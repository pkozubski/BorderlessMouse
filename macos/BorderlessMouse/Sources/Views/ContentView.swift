import AppKit
import SwiftUI

struct ContentView: View {
    enum Pane: String, CaseIterable, Identifiable {
        case connection, input, audio, clipboard, startup, updates, log

        var id: String { rawValue }

        var title: String {
            switch self {
            case .connection: return "Połączenie"
            case .input: return "Klawiatura i mysz"
            case .audio: return "Dźwięk"
            case .clipboard: return "Schowek"
            case .startup: return "Uruchamianie"
            case .updates: return "Aktualizacje"
            case .log: return "Dziennik"
            }
        }

        var symbol: String {
            switch self {
            case .connection: return "network"
            case .input: return "keyboard"
            case .audio: return "speaker.wave.2"
            case .clipboard: return "clipboard"
            case .startup: return "power"
            case .updates: return "arrow.down.circle"
            case .log: return "list.bullet.rectangle"
            }
        }
    }

    @EnvironmentObject private var state: AppState
    @State private var selection: Pane?

    init(initialPane: Pane = .connection) {
        _selection = State(initialValue: initialPane)
    }

    private var pane: Pane { selection ?? .connection }

    var body: some View {
        NavigationSplitView {
            List(selection: $selection) {
                Section {
                    identity
                }
                Section {
                    ForEach(Pane.allCases) { item in
                        Label(item.title, systemImage: item.symbol).tag(item)
                    }
                }
            }
            .navigationSplitViewColumnWidth(min: 198, ideal: 214, max: 280)
        } detail: {
            detail
                .navigationTitle(pane.title)
                .toolbar {
                    ToolbarItem(placement: .primaryAction) {
                        let status = state.statusSummary
                        StatusLabel(text: status.text, color: status.color)
                    }
                }
        }
        .frame(minWidth: 760, minHeight: 520)
    }

    private var identity: some View {
        HStack(spacing: 10) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .interpolation(.high)
                .frame(width: 38, height: 38)
            VStack(alignment: .leading, spacing: 1) {
                Text("BorderlessMouse").font(.headline)
                Text(state.peer.map { "Windows: \($0.name)" } ?? "Czeka na Windows")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
        .padding(.vertical, 4)
    }

    @ViewBuilder private var detail: some View {
        switch pane {
        case .connection: ConnectionPane()
        case .input: InputPane()
        case .audio: AudioPane()
        case .clipboard: ClipboardPane()
        case .startup: StartupPane()
        case .updates: UpdatesPane()
        case .log: LogPane()
        }
    }
}
