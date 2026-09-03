import Foundation

enum L10n {
    static var isPolish: Bool {
        Locale.preferredLanguages.first?.lowercased().hasPrefix("pl") == true
    }

    static func text(_ polish: String, _ english: String) -> String {
        isPolish ? polish : english
    }

    static func localized(_ source: String) -> String {
        NSLocalizedString(source, comment: "")
    }
}
