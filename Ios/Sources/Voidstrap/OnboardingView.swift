import SwiftUI
import VoidstrapCore

struct OnboardingView: View {
    static let key = "onboarded"
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var step = 0
    private let steps: [(String, String, String)] = [
        ("bolt.fill", "Welcome to Voidstrap", "Mods, FastFlags and your game library for Roblox on iOS. This quick setup gets everything ready."),
        ("paintpalette", "Appearance", "Pick a theme. You can change it any time in Settings."),
        ("arrow.down.circle", "Get Roblox", "Voidstrap downloads Roblox, applies your mods and FastFlags, and hands the IPA to LiveContainer. Install Roblox in the same LiveContainer as Voidstrap and open it once."),
        ("apps.iphone", "Add to Home Screen", "iOS does not let apps add themselves. In LiveContainer, long press Voidstrap and choose Add to Home Screen or Create App Clip to open it straight from your home screen."),
        ("checkmark.circle", "You are all set", "Apply Roblox from the bottom bar whenever you change mods or FastFlags.")
    ]

    var body: some View {
        VStack(spacing: 20) {
            HStack {
                Spacer()
                if step < steps.count - 1 { Button(Loc.t("Skip")) { finish() }.foregroundColor(Palette.textSecondary) }
            }
            ScrollView {
                VStack(spacing: 16) {
                    Image(systemName: steps[step].0)
                        .font(.system(size: 44, weight: .semibold))
                        .foregroundColor(Palette.primary)
                        .frame(width: 88, height: 88)
                        .surface(RoundedRectangle(cornerRadius: 24, style: .continuous))
                    Text(loc: steps[step].1).font(.system(size: 26, weight: .bold)).foregroundColor(Palette.text).multilineTextAlignment(.center)
                    Text(loc: steps[step].2).font(.system(size: 15)).foregroundColor(Palette.textSecondary).multilineTextAlignment(.center)
                    content.padding(.top, 8)
                }
                .padding(.top, 24)
                .id(step)
                .transition(.opacity)
            }
            HStack(spacing: 6) {
                ForEach(steps.indices, id: \.self) { i in
                    Capsule().fill(i == step ? Palette.primary : Palette.textTertiary.opacity(0.45)).frame(width: i == step ? 20 : 8, height: 8)
                }
            }
            .accessibilityHidden(true)
            HStack {
                Button(Loc.t("Back")) { go(step - 1) }.buttonStyle(StandardButtonStyle()).opacity(step == 0 ? 0 : 1).disabled(step == 0)
                Spacer()
                Button(Loc.t(step == 0 ? "Get started" : step == steps.count - 1 ? "Finish" : "Next")) {
                    if step == steps.count - 1 { finish() } else { go(step + 1) }
                }
                .buttonStyle(PrimaryButtonStyle())
            }
        }
        .padding(Metrics.pageInset)
        .background(GlassBackdrop())
        .interactiveDismissDisabled()
    }

    @ViewBuilder private var content: some View {
        switch step {
        case 1:
            Card {
                Row(symbol: "circle.lefthalf.filled", title: "Theme") {
                    Picker(Loc.t("Theme"), selection: $model.settings.theme) {
                        ForEach(AppTheme.allCases) { Text(loc: $0.title).tag($0) }
                    }
                    .labelsHidden()
                }
            }
        case 2:
            Card {
                Row(symbol: "arrow.down.circle", title: "Download latest Roblox", detail: model.robloxVersion.map { "Roblox \($0)" }) {
                    Button(Loc.t("Download")) { model.download() }.buttonStyle(StandardButtonStyle()).disabled(model.busy || model.robloxIpa != nil)
                }
            }
            if model.busy, let progress = model.progress { ProgressView(value: progress) }
        case 4:
            Card {
                Button { model.launchRoblox(); finish() } label: {
                    Row(symbol: "play.fill", title: "Launch Roblox", detail: "Opens Roblox with your FastFlags") { Chevron() }
                }
                .buttonStyle(.plain)
            }
        default:
            EmptyView()
        }
    }

    private func go(_ to: Int) {
        guard steps.indices.contains(to) else { return }
        withAnimation(.easeOut(duration: 0.22)) { step = to }
    }

    private func finish() {
        UserDefaults.standard.set(true, forKey: Self.key)
        dismiss()
    }
}
