import AppKit
import SwiftUI

/// Renderowanie interfejsu do pliku PNG bez uruchamiania serwera, audio ani
/// uprawnień – diagnostyka wyglądu (`--ui-preview <plik.png> [panel]`).
/// Odpowiednik `--screenshot` w wersji Windows.
@MainActor
enum UIPreview {
    static func run() -> Never {
        let args = CommandLine.arguments
        guard let idx = args.firstIndex(of: "--ui-preview"), idx + 1 < args.count else {
            FileHandle.standardError.write(Data("użycie: --ui-preview <plik.png> [panel]\n".utf8))
            exit(2)
        }
        let path = args[idx + 1]
        let paneName = idx + 2 < args.count ? args[idx + 2] : nil
        let state = AppState.demo()
        var view = ContentView()
        if let paneName, let pane = ContentView.Pane(rawValue: paneName) {
            view = ContentView(initialPane: pane)
        }
        let root = view.environmentObject(state)
        let size = CGSize(width: 1000, height: 700)
        // NSHostingView renderuje treść paneli wiernie; materiał paska bocznego
        // wymaga prawdziwego okna na ekranie, więc w podglądzie wychodzi jasny.
        let hosting = NSHostingView(rootView: root.frame(width: size.width, height: size.height))
        hosting.frame = CGRect(origin: .zero, size: size)
        hosting.layoutSubtreeIfNeeded()
        for _ in 0..<3 {
            RunLoop.main.run(until: Date().addingTimeInterval(0.3))
            hosting.layoutSubtreeIfNeeded()
        }
        guard let rep = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) else {
            FileHandle.standardError.write(Data("nie udało się utworzyć bitmapy\n".utf8))
            exit(3)
        }
        hosting.cacheDisplay(in: hosting.bounds, to: rep)
        guard let png = rep.representation(using: .png, properties: [:]) else { exit(3) }
        try? png.write(to: URL(fileURLWithPath: path))
        print("zapisano \(path) (\(Int(size.width))×\(Int(size.height)))")
        exit(0)
    }
}
