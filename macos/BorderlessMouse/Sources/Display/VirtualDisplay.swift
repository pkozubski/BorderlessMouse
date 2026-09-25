import CoreGraphics
import Foundation

/// Wirtualny monitor Maca utworzony przez CGVirtualDisplay. macOS traktuje go
/// jak zwykły ekran: można na niego przeciągać okna, a ScreenCaptureKit
/// nagrywa jego zawartość. Monitor znika razem z obiektem.
final class VirtualDisplay {
    enum DisplayError: LocalizedError {
        case unsupported
        case creationFailed
        case settingsRejected
        case notOnline

        var errorDescription: String? {
            switch self {
            case .unsupported:
                return L10n.text("Ta wersja macOS nie udostępnia wirtualnych monitorów.",
                                 "This macOS version does not provide virtual displays.")
            case .creationFailed:
                return L10n.text("macOS odmówił utworzenia wirtualnego monitora.",
                                 "macOS refused to create the virtual display.")
            case .settingsRejected:
                return L10n.text("macOS odrzucił tryby wirtualnego monitora.",
                                 "macOS rejected the virtual display modes.")
            case .notOnline:
                return L10n.text("Wirtualny monitor nie pojawił się w systemie.",
                                 "The virtual display did not come online.")
            }
        }
    }

    /// Stałe identyfikatory: macOS rozpoznaje ten sam monitor między sesjami.
    private static let vendorID: UInt32 = 0x0B1D
    private static let productID: UInt32 = 0x0002
    private static let serialNumber: UInt32 = 0x424C_4D31 // "BLM1"
    /// Granica H.264 poziomu 5.2 w dekoderach Media Foundation.
    static let maxPixelWidth = 4096
    static let maxPixelHeight = 2304

    static var isSupported: Bool {
        NSClassFromString("CGVirtualDisplay") != nil
            && NSClassFromString("CGVirtualDisplayDescriptor") != nil
            && NSClassFromString("CGVirtualDisplaySettings") != nil
            && NSClassFromString("CGVirtualDisplayMode") != nil
    }

    let displayID: CGDirectDisplayID
    let nativeWidth: Int
    let nativeHeight: Int
    private let display: CGVirtualDisplay
    private let queue = DispatchQueue(label: "blm.display.virtual")

    /// Tworzy monitor o rozdzielczości monitora Windows. Przy skalowaniu ≥ 150%
    /// domyślnym trybem jest HiDPI (połowa punktów, pełna ostrość pikseli).
    init(name: String, pixelWidth: Int, pixelHeight: Int, scalePercent: Int) throws {
        guard Self.isSupported else { throw DisplayError.unsupported }
        let (width, height) = Self.fit(width: pixelWidth, height: pixelHeight)
        nativeWidth = width
        nativeHeight = height

        let descriptor = CGVirtualDisplayDescriptor()
        descriptor.queue = queue
        descriptor.name = name
        descriptor.maxPixelsWide = UInt32(width)
        descriptor.maxPixelsHigh = UInt32(height)
        // Rozmiar fizyczny z DPI Windowsa: macOS dobiera na jego podstawie domyślne skalowanie.
        let dpi = 96.0 * Double(max(scalePercent, 100)) / 100.0
        descriptor.sizeInMillimeters = CGSize(width: Double(width) / dpi * 25.4, height: Double(height) / dpi * 25.4)
        descriptor.vendorID = Self.vendorID
        descriptor.productID = Self.productID
        descriptor.serialNum = Self.serialNumber
        descriptor.terminationHandler = nil

        guard let display = CGVirtualDisplay(descriptor: descriptor) else { throw DisplayError.creationFailed }
        self.display = display
        displayID = display.displayID

        let settings = CGVirtualDisplaySettings()
        settings.hiDPI = 1
        settings.modes = [(width, height), (width / 2, height / 2)].map { w, h in
            CGVirtualDisplayMode(width: UInt32(w), height: UInt32(h), refreshRate: 60)
        }
        guard display.apply(settings) else { throw DisplayError.settingsRejected }
        guard Self.waitUntilOnline(displayID) else { throw DisplayError.notOnline }
        selectDefaultMode(hiDPI: scalePercent >= 150)
    }

    /// Aktualny rozmiar obrazu w pikselach (zależy od trybu wybranego w macOS).
    var pixelSize: (width: Int, height: Int) {
        guard let mode = CGDisplayCopyDisplayMode(displayID) else { return (nativeWidth, nativeHeight) }
        return (mode.pixelWidth, mode.pixelHeight)
    }

    var bounds: CGRect { CGDisplayBounds(displayID) }

    /// Ustawia monitor obok najbardziej zewnętrznego fizycznego ekranu po
    /// stronie `edge` (tam, gdzie stoi Windows).
    func attach(to edge: ScreenEdge) {
        let others = Self.activeDisplays().filter { $0 != displayID }.map { CGDisplayBounds($0) }
        guard !others.isEmpty else { return }
        let size = bounds.size
        let anchor: CGRect
        let origin: CGPoint
        switch edge {
        case .right:
            anchor = others.max { $0.maxX < $1.maxX }!
            origin = CGPoint(x: anchor.maxX, y: anchor.minY)
        case .left:
            anchor = others.min { $0.minX < $1.minX }!
            origin = CGPoint(x: anchor.minX - size.width, y: anchor.minY)
        case .top:
            anchor = others.min { $0.minY < $1.minY }!
            origin = CGPoint(x: anchor.minX, y: anchor.minY - size.height)
        case .bottom:
            anchor = others.max { $0.maxY < $1.maxY }!
            origin = CGPoint(x: anchor.minX, y: anchor.maxY)
        }
        guard bounds.origin != origin else { return }
        var config: CGDisplayConfigRef?
        guard CGBeginDisplayConfiguration(&config) == .success else { return }
        CGConfigureDisplayOrigin(config, displayID, Int32(origin.x.rounded()), Int32(origin.y.rounded()))
        // Tylko na czas działania aplikacji – po wyjściu macOS przywraca układ.
        if CGCompleteDisplayConfiguration(config, .forAppOnly) != .success {
            CGCancelDisplayConfiguration(config)
        }
    }

    private func selectDefaultMode(hiDPI: Bool) {
        let options = [kCGDisplayShowDuplicateLowResolutionModes: true] as CFDictionary
        guard let modes = CGDisplayCopyAllDisplayModes(displayID, options) as? [CGDisplayMode] else { return }
        let wanted = modes.first { mode in
            mode.pixelWidth == nativeWidth && mode.pixelHeight == nativeHeight
                && (hiDPI ? mode.width * 2 == nativeWidth : mode.width == nativeWidth)
                && mode.isUsableForDesktopGUI()
        }
        guard let wanted, CGDisplayCopyDisplayMode(displayID).map({ !Self.same($0, wanted) }) ?? true else { return }
        var config: CGDisplayConfigRef?
        guard CGBeginDisplayConfiguration(&config) == .success else { return }
        CGConfigureDisplayWithDisplayMode(config, displayID, wanted, nil)
        if CGCompleteDisplayConfiguration(config, .forAppOnly) != .success {
            CGCancelDisplayConfiguration(config)
        }
    }

    private static func same(_ a: CGDisplayMode, _ b: CGDisplayMode) -> Bool {
        a.width == b.width && a.height == b.height && a.pixelWidth == b.pixelWidth && a.pixelHeight == b.pixelHeight
    }

    /// Wymiary parzyste (wymóg 4:2:0) i w granicach dekodera, z zachowaniem proporcji.
    static func fit(width: Int, height: Int) -> (Int, Int) {
        var w = Double(max(width, 320))
        var h = Double(max(height, 240))
        let scale = min(1, Double(maxPixelWidth) / w, Double(maxPixelHeight) / h)
        w *= scale
        h *= scale
        return (Int(w) & ~3, Int(h) & ~3)
    }

    /// Monitor utworzony przez BorderlessMouse (także zanim Engine zapamiętał jego identyfikator).
    static func isOwn(_ id: CGDirectDisplayID) -> Bool {
        CGDisplayVendorNumber(id) == vendorID && CGDisplaySerialNumber(id) == serialNumber
    }

    static func activeDisplays() -> [CGDirectDisplayID] {
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(0, nil, &count) == .success, count > 0 else { return [] }
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        guard CGGetActiveDisplayList(count, &ids, &count) == .success else { return [] }
        return Array(ids.prefix(Int(count)))
    }

    private static func waitUntilOnline(_ id: CGDirectDisplayID) -> Bool {
        for _ in 0..<60 {
            if activeDisplays().contains(id), CGDisplayBounds(id).width > 0 { return true }
            usleep(50_000)
        }
        return false
    }
}
