import Foundation

/// Wspólny format schowka; obrazy na łączu są zawsze PNG.
struct ClipboardContent: Equatable {
    let format: ClipboardFormat
    let data: Data

    init?(format: ClipboardFormat, data: Data) {
        switch format {
        case .utf8Text:
            guard !data.isEmpty, data.count <= ProtocolConstants.maxClipboardBytes,
                  String(data: data, encoding: .utf8) != nil else { return nil }
        case .png:
            // Sprawdź rozmiary przed dekodowaniem, także dla mocno skompresowanych PNG.
            guard data.count <= ProtocolConstants.maxClipboardImageBytes,
                  data.count >= 33,
                  data.starts(with: [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82]) else { return nil }
            let header = [UInt8](data.prefix(24))
            let width = header[16..<20].reduce(UInt64(0)) { ($0 << 8) | UInt64($1) }
            let height = header[20..<24].reduce(UInt64(0)) { ($0 << 8) | UInt64($1) }
            guard width > 0, height > 0,
                  width * height <= ProtocolConstants.maxClipboardImagePixels else { return nil }
        }
        self.format = format
        self.data = data
    }

    init?(payload: [UInt8]) {
        guard let first = payload.first, let format = ClipboardFormat(rawValue: first) else { return nil }
        self.init(format: format, data: Data(payload.dropFirst()))
    }

    var text: String? {
        format == .utf8Text ? String(data: data, encoding: .utf8) : nil
    }

    var summary: String {
        if let text { return L10n.text("\(text.count) zn.", "\(text.count) characters") }
        return L10n.text("obraz PNG (\((data.count + 1023) / 1024) KiB)",
                         "PNG image (\((data.count + 1023) / 1024) KiB)")
    }
}
