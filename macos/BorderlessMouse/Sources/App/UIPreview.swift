import AppKit
import SwiftUI

/// Renderowanie interfejsu do pliku PNG bez uruchamiania serwera, audio ani
/// uprawnień – diagnostyka wyglądu (`--ui-preview <plik.png>`).
/// Odpowiednik `--screenshot` w wersji Windows.
@MainActor
enum UIPreview {
    static func run() -> Never {
        let args = CommandLine.arguments
        guard let idx = args.firstIndex(of: "--ui-preview"), idx + 1 < args.count else {
            FileHandle.standardError.write(Data("użycie: --ui-preview <plik.png>\n".utf8))
            exit(2)
        }
        let path = args[idx + 1]
        let state = AppState.demo()
        let root = ContentView().environmentObject(state)
        let size = CGSize(width: 560, height: 1180)
        // NSHostingView renderuje karty wiernie; materiały systemowe wymagałyby
        // prawdziwego okna na ekranie.
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
