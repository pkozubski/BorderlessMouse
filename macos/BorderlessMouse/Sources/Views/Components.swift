import SwiftUI

/// Karta z nagłówkiem – wspólny język wizualny z wersją Windows.
struct Card<Content: View>: View {
    let title: String
    let systemImage: String
    @ViewBuilder let content: Content

    init(_ title: String, systemImage: String, @ViewBuilder content: () -> Content) {
        self.title = title
        self.systemImage = systemImage
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label(title.uppercased(), systemImage: systemImage)
                .font(.caption.weight(.semibold))
                .kerning(0.6)
                .foregroundStyle(.secondary)
            content
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color(nsColor: .controlBackgroundColor)))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(Color(nsColor: .separatorColor), lineWidth: 1))
    }
}

/// Wiersz ustawienia: tytuł + opcjonalny opis po lewej, kontrolka po prawej.
struct SettingRow<Control: View>: View {
    let title: String
    var subtitle: String? = nil
    @ViewBuilder let control: Control

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                if let subtitle {
                    Text(subtitle).font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 8)
            control
        }
    }
}

struct StatusPill: View {
    let text: String
    let color: Color

    var body: some View {
        HStack(spacing: 6) {
            Circle().fill(color).frame(width: 8, height: 8)
                .shadow(color: color.opacity(0.6), radius: 3)
            Text(text).font(.callout.weight(.medium))
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(Capsule().fill(color.opacity(0.12)))
    }
}

struct StatusDot: View {
    let ok: Bool?
    var body: some View {
        Circle()
            .fill(ok == nil ? Color.gray : (ok! ? Color.green : Color.orange))
            .frame(width: 8, height: 8)
    }
}

struct LevelMeter: View {
    let level: Float

    var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.primary.opacity(0.08))
                Capsule()
                    .fill(LinearGradient(colors: [.green, .yellow, .red], startPoint: .leading, endPoint: .trailing))
                    .frame(width: max(0, geo.size.width * CGFloat(min(level, 1))))
                    .animation(.linear(duration: 0.05), value: level)
            }
        }
        .frame(height: 8)
    }
}

extension View {
    func rowDivider() -> some View {
        VStack(spacing: 12) {
            self
            Divider()
        }
    }
}
