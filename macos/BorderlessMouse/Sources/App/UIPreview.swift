import AppKit
import SwiftUI

/// Renderowanie interfejsu do pliku PNG bez uruchamiania serwera, audio ani
/// uprawnień – diagnostyka wyglądu (`--ui-preview <plik.png>`).
/// Odpowiednik `--screenshot` w wersji Windows.
///
/// Okno jest pokazywane na chwilę na ekranie: pasek boczny (materiał) i pasek
/// narzędzi renderuje dopiero kompozytor, więc zrzut z NSHostingView poza oknem
/// byłby niepełny. Sekcję paska bocznego wybiera `--ui-section <connection|permissions|control|settings|log>`.
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
        var section = SidebarSection.connection
        if let s = args.firstIndex(of: "--ui-section"), s + 1 < args.count, let parsed = SidebarSection(rawValue: args[s + 1]) {
            section = parsed
        }
        let root = ContentView(initialSection: section).environmentObject(state)
        let size = CGSize(width: 880, height: 620)

        let app = NSApplication.shared
        app.setActivationPolicy(.regular)
        let window = NSWindow(contentRect: CGRect(origin: .zero, size: size),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                              backing: .buffered, defer: false)
        window.title = "BorderlessMouse"
        window.toolbarStyle = .unified
        window.contentView = NSHostingView(rootView: root)
        window.center()
        window.makeKeyAndOrderFront(nil)
        app.activate(ignoringOtherApps: true)

        for _ in 0..<5 {
            RunLoop.main.run(until: Date().addingTimeInterval(0.3))
        }

        let windowID = CGWindowID(window.windowNumber)
        guard let cg = CGWindowListCreateImage(.null, .optionIncludingWindow, windowID, [.bestResolution, .boundsIgnoreFraming]) else {
            FileHandle.standardError.write(Data("nie udało się zrzucić okna\n".utf8))
            exit(3)
        }
        let rep = NSBitmapImageRep(cgImage: cg)
        guard let png = rep.representation(using: .png, properties: [:]) else { exit(3) }
        try? png.write(to: URL(fileURLWithPath: path))
        print("zapisano \(path) (\(cg.width)×\(cg.height) px)")
        exit(0)
    }
}
