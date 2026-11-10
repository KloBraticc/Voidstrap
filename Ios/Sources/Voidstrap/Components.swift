import SwiftUI

struct PageHeader: View {
    let title: String
    let subtitle: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(loc: title)
                .font(.system(size: 28, weight: .bold))
                .foregroundColor(Palette.text)
                .accessibilityAddTraits(.isHeader)
            Text(loc: subtitle)
                .font(.system(size: 15))
                .foregroundColor(Palette.textSecondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.top, 8)
        .padding(.bottom, 16)
    }
}

struct SectionTitle: View {
    let text: String

    var body: some View {
        Text(loc: text)
            .font(.system(size: 20, weight: .medium))
            .foregroundColor(Palette.text)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.top, 12)
            .padding(.bottom, 8)
            .accessibilityAddTraits(.isHeader)
    }
}

struct Card<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        VStack(spacing: 0) { content }
            .surface(RoundedRectangle(cornerRadius: Metrics.cardRadius, style: .continuous))
    }
}

struct IconTile: View {
    let symbol: String
    var tint: Color = Palette.textSecondary

    var body: some View {
        RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous)
            .fill(Palette.input.opacity(0.55))
            .frame(width: Metrics.iconTile, height: Metrics.iconTile)
            .overlay(Image(systemName: symbol).font(.system(size: 17, weight: .medium)).foregroundColor(tint))
            .accessibilityHidden(true)
    }
}

struct Row<Trailing: View>: View {
    let symbol: String
    let title: String
    var detail: String? = nil
    var tint: Color = Palette.textSecondary
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(spacing: 12) {
            IconTile(symbol: symbol, tint: tint)
            VStack(alignment: .leading, spacing: 2) {
                Text(loc: title)
                    .font(.system(size: 16, weight: .medium))
                    .foregroundColor(Palette.text)
                    .lineLimit(2)
                if let detail, !detail.isEmpty {
                    Text(loc: detail)
                        .font(.system(size: 13.5))
                        .foregroundColor(Palette.textSecondary)
                        .lineLimit(3)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            trailing
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .frame(minHeight: 56)
        .contentShape(Rectangle())
    }
}

extension Row where Trailing == EmptyView {
    init(symbol: String, title: String, detail: String? = nil, tint: Color = Palette.textSecondary) {
        self.init(symbol: symbol, title: title, detail: detail, tint: tint) { EmptyView() }
    }
}

struct Chevron: View {
    var body: some View {
        Image(systemName: "chevron.right")
            .font(.system(size: 13, weight: .semibold))
            .foregroundColor(Palette.textTertiary)
            .accessibilityHidden(true)
    }
}

struct RowDivider: View {
    var body: some View {
        Rectangle().fill(Palette.divider.opacity(0.5)).frame(height: 0.5).padding(.leading, 64)
    }
}

struct Badge: View {
    let text: String
    var tint: Color = Palette.textSecondary

    var body: some View {
        Text(loc: text)
            .font(.system(size: 12, weight: .medium))
            .foregroundColor(tint)
            .padding(.horizontal, 8)
            .padding(.vertical, 2)
            .background(Palette.input.opacity(0.55), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
    }
}

struct PrimaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 15, weight: .semibold))
            .foregroundColor(Palette.onPrimary)
            .padding(.horizontal, 16)
            .frame(minHeight: 40)
            .opacity(enabled ? 1 : 0.5)
            .surface(Capsule(), fill: Palette.primary.opacity(configuration.isPressed ? 0.75 : 0.9))
    }
}

struct StandardButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 15, weight: .medium))
            .foregroundColor(Palette.text.opacity(enabled ? 1 : 0.4))
            .padding(.horizontal, 14)
            .frame(minHeight: 40)
            .surface(Capsule())
    }
}

struct EmptyState: View {
    let symbol: String
    let title: String
    let message: String

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: symbol)
                .font(.system(size: 30))
                .foregroundColor(Palette.textTertiary)
                .accessibilityHidden(true)
            Text(loc: title)
                .font(.system(size: 17, weight: .semibold))
                .foregroundColor(Palette.text)
            Text(loc: message)
                .font(.system(size: 14))
                .foregroundColor(Palette.textSecondary)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 32)
        .padding(.horizontal, 24)
    }
}

struct Page<Content: View>: View {
    let title: String
    let subtitle: String
    @ViewBuilder var content: Content

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                PageHeader(title: title, subtitle: subtitle)
                content
            }
            .frame(maxWidth: 760)
            .padding(.horizontal, Metrics.pageInset)
            .padding(.bottom, 24)
            .frame(maxWidth: .infinity)
        }
    }
}
