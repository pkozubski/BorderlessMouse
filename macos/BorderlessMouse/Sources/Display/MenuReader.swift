import ApplicationServices
import Foundation

/// Odczytuje menu aplikacji Maca (Accessibility) i wywołuje jego pozycje. Windows
/// wstawia to menu do nagłówka okna zamiast całego paska menu Maca.
///
/// Format na łączu (rekurencyjnie): `u16 count`, potem dla każdej pozycji `u8 flags`
/// (bit 0 aktywna, bit 1 separator, bit 2 zaznaczona, bit 3 podmenu), `u8 len` + tytuł,
/// `u8 len` + skrót, a przy podmenu – zagnieżdżona lista. Pozycje są numerowane
/// w kolejności przeglądania (od 0) i tym numerem Windows prosi o wywołanie.
enum MenuReader {
    struct Snapshot {
        let payload: [UInt8]
        let items: [AXUIElement]
    }

    private static let maxItems = 1500
    private static let maxDepth = 5

    static func read(pid: Int32, swapCtrlCmd: Bool) -> Snapshot? {
        let app = AXUIElementCreateApplication(pid)
        guard let menuBar = element(app, kAXMenuBarAttribute) else { return nil }
        var items: [AXUIElement] = []
        var writer = ByteWriter()
        // Pierwsza pozycja to menu Apple (systemowe) – nie należy do aplikacji.
        let top = Array(children(menuBar).dropFirst())
        writeList(top, depth: 0, into: &writer, items: &items, swapCtrlCmd: swapCtrlCmd)
        return Snapshot(payload: writer.bytes, items: items)
    }

    static func invoke(_ item: AXUIElement) {
        AXUIElementPerformAction(item, kAXPressAction as CFString)
    }

    private static func writeList(_ elements: [AXUIElement], depth: Int, into writer: inout ByteWriter,
                                  items: inout [AXUIElement], swapCtrlCmd: Bool) {
        let visible = elements.prefix(max(0, maxItems - items.count))
        writer.u16(UInt16(visible.count))
        for element in visible {
            let title = string(element, kAXTitleAttribute) ?? ""
            let separator = title.isEmpty && string(element, kAXRoleAttribute) == (kAXMenuItemRole as String)
                && children(element).isEmpty
            let enabled = bool(element, kAXEnabledAttribute) ?? true
            let checked = !(string(element, kAXMenuItemMarkCharAttribute) ?? "").isEmpty
            // Pozycja paska menu i pozycja z podmenu mają jedno dziecko: AXMenu z pozycjami.
            let submenu = depth < maxDepth ? children(element).first.map(children) ?? [] : []
            var flags: UInt8 = 0
            if enabled { flags |= 1 }
            if separator { flags |= 2 }
            if checked { flags |= 4 }
            if !submenu.isEmpty { flags |= 8 }
            writer.u8(flags)
            writeString(title, into: &writer)
            writeString(shortcut(element, swapCtrlCmd: swapCtrlCmd), into: &writer)
            items.append(element)
            if !submenu.isEmpty {
                writeList(submenu, depth: depth + 1, into: &writer, items: &items, swapCtrlCmd: swapCtrlCmd)
            }
        }
    }

    /// Skrót w zapisie Windows. Przy zamianie Ctrl ↔ Cmd ⌘ odpowiada Ctrl na klawiaturze PC.
    private static func shortcut(_ element: AXUIElement, swapCtrlCmd: Bool) -> String {
        guard let raw = string(element, kAXMenuItemCmdCharAttribute), !raw.isEmpty else { return "" }
        // Klawisze specjalne mają zamiast znaku glif (np. ⌫ w „Opróżnij Kosz”).
        let glyphs: [Int: String] = [23: "Backspace", 10: "Del", 4: "Enter", 11: "Enter", 27: "Esc", 9: "Tab",
                                     100: "Left", 101: "Right", 104: "Up", 106: "Down", 98: "PgUp", 107: "PgDn",
                                     102: "Home", 105: "End"]
        let printable = raw.unicodeScalars.allSatisfy { $0.value >= 0x20 && $0.value < 0xF700 && $0.value != 0x7F }
        guard let key = printable ? raw : int(element, kAXMenuItemCmdGlyphAttribute).flatMap({ glyphs[$0] }) else { return "" }
        let modifiers = int(element, kAXMenuItemCmdModifiersAttribute) ?? 0
        var parts: [String] = []
        if modifiers & 4 != 0 { parts.append(swapCtrlCmd ? "Win" : "Ctrl") } // ⌃
        if modifiers & 2 != 0 { parts.append("Alt") }                         // ⌥
        if modifiers & 1 != 0 { parts.append("Shift") }                       // ⇧
        if modifiers & 8 == 0 { parts.append(swapCtrlCmd ? "Ctrl" : "Win") } // ⌘
        parts.append(key.uppercased())
        return parts.joined(separator: "+")
    }

    private static func writeString(_ value: String, into writer: inout ByteWriter) {
        let bytes = Array(String(decoding: value.utf8.prefix(120), as: UTF8.self).utf8)
        writer.u8(UInt8(bytes.count))
        writer.raw(bytes)
    }

    private static func element(_ parent: AXUIElement, _ attribute: String) -> AXUIElement? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(parent, attribute as CFString, &value) == .success, let value,
              CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return (value as! AXUIElement)
    }

    private static func children(_ parent: AXUIElement) -> [AXUIElement] {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(parent, kAXChildrenAttribute as CFString, &value) == .success,
              let list = value as? [AXUIElement] else { return [] }
        return list
    }

    private static func string(_ element: AXUIElement, _ attribute: String) -> String? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else { return nil }
        return value as? String
    }

    private static func bool(_ element: AXUIElement, _ attribute: String) -> Bool? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else { return nil }
        return (value as? NSNumber)?.boolValue
    }

    private static func int(_ element: AXUIElement, _ attribute: String) -> Int? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else { return nil }
        return (value as? NSNumber)?.intValue
    }
}
