import SwiftUI

/// Kropka stanu z podpisem – jedyny własny element wizualny. Reszta interfejsu
/// to standardowe kontrolki SwiftUI (`Form`, `Section`, `LabeledContent`,
/// `Toggle`), żeby aplikacja wyglądała dokładnie tak, jak przewiduje system.
struct StatusLabel: View {
    let text: String
    let color: Color
    var font: Font = .callout

    var body: some View {
        HStack(spacing: 6) {
            Circle()
                .fill(color)
                .frame(width: 7, height: 7)
            Text(text).font(font)
        }
    }
}
