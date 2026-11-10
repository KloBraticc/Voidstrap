import SwiftUI
import VoidstrapCore

@main
struct VoidstrapApp: App {
    @StateObject private var model = AppModel()

    init() {
        Paths.ensure(Paths.documents)
        Log.write("App::Init", "Voidstrap \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "") started, LiveContainer \(Paths.isLiveContainer)")
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(model)
                .preferredColorScheme(model.settings.theme == .light ? .light : model.settings.theme == .system ? nil : .dark)
                .tint(Palette.primary)
                .task { await model.checkForUpdate() }
        }
    }
}

struct RootView: View {
    @EnvironmentObject private var model: AppModel
    @ObservedObject private var loc = Loc.shared
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var onboarding = !UserDefaults.standard.bool(forKey: OnboardingView.key)

    var body: some View {
        Group {
            if sizeClass == .regular {
                HStack(spacing: 0) {
                    SideRail()
                    VStack(spacing: 0) {
                        content
                        ActionBar()
                    }
                }
            } else {
                VStack(spacing: 0) {
                    content
                    ActionBar()
                    BottomBar()
                }
            }
        }
        .background(GlassBackdrop())
        .overlay(alignment: .top) { NoticeBanner() }
        .fullScreenCover(isPresented: $onboarding) { OnboardingView().environmentObject(model) }
        .id("\(loc.revision)|\(model.settings.theme.rawValue)")
        .onAppear { applyLook() }
        .onChange(of: model.settings.theme) { _ in applyLook() }
        .onChange(of: model.settings.background) { _ in AppBackground.shared.sync(model.settings) }
        .onChange(of: model.settings.language) { Loc.shared.setLanguage($0) }
    }

    private func applyLook() {
        AppBackground.shared.sync(model.settings)
        Palette.named = model.settings.theme.named
        Loc.shared.setLanguage(model.settings.language)
    }

    @ViewBuilder private var content: some View {
        ZStack {
            switch model.tab {
            case .home: HomeView()
            case .library: LibraryView()
            case .mods: ModsView()
            case .flags: FlagsView()
            case .settings: SettingsView()
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

struct BottomBar: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        HStack(spacing: 0) {
            ForEach(Tab.allCases) { tab in
                Button {
                    model.tab = tab
                } label: {
                    VStack(spacing: 4) {
                        Image(systemName: model.tab == tab ? tab.symbol + ".fill" : tab.symbol)
                            .font(.system(size: 17, weight: .medium))
                            .frame(width: 56, height: 28)
                            .background(model.tab == tab ? Palette.primary.opacity(0.22) : .clear)
                            .clipShape(Capsule())
                        Text(loc: tab.title)
                            .font(.system(size: 11.5, weight: model.tab == tab ? .semibold : .regular))
                            .lineLimit(1)
                            .minimumScaleFactor(0.8)
                    }
                    .foregroundColor(model.tab == tab ? Palette.text : Palette.textSecondary)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 8)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(tab.title)
                .accessibilityAddTraits(model.tab == tab ? .isSelected : [])
            }
        }
        .padding(.horizontal, 6)
        .glass(Capsule())
        .padding(.horizontal, Metrics.pageInset)
        .padding(.bottom, 4)
    }
}

struct SideRail: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "bolt.fill")
                .font(.system(size: 22, weight: .bold))
                .foregroundColor(Palette.primary)
                .padding(.vertical, 16)
                .accessibilityHidden(true)
            ForEach(Tab.allCases) { tab in
                Button {
                    model.tab = tab
                } label: {
                    VStack(spacing: 4) {
                        Image(systemName: model.tab == tab ? tab.symbol + ".fill" : tab.symbol)
                            .font(.system(size: 18, weight: .medium))
                            .frame(width: 56, height: 32)
                            .background(model.tab == tab ? Palette.primary.opacity(0.22) : .clear)
                            .clipShape(Capsule())
                        Text(loc: tab.title)
                            .font(.system(size: 12, weight: model.tab == tab ? .semibold : .regular))
                            .lineLimit(1)
                            .minimumScaleFactor(0.7)
                    }
                    .foregroundColor(model.tab == tab ? Palette.text : Palette.textSecondary)
                    .frame(width: Metrics.rail)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(tab.title)
                .accessibilityAddTraits(model.tab == tab ? .isSelected : [])
            }
            Spacer()
        }
        .frame(width: Metrics.rail)
        .padding(.bottom, 12)
        .glass(RoundedRectangle(cornerRadius: 28, style: .continuous))
        .padding(8)
    }
}

struct ActionBar: View {
    @EnvironmentObject private var model: AppModel
    @State private var importing = false

    var body: some View {
        VStack(spacing: 6) {
            if model.busy {
                VStack(alignment: .leading, spacing: 4) {
                    Text(model.status.isEmpty ? "Working" : model.status)
                        .font(.system(size: 13))
                        .foregroundColor(Palette.textSecondary)
                        .lineLimit(1)
                    if let progress = model.progress {
                        ProgressView(value: progress)
                    } else {
                        ProgressView().progressViewStyle(.linear)
                    }
                }
            }
            HStack(spacing: 8) {
                Menu {
                    Button { model.download() } label: { Label(Loc.t("Download latest Roblox"), systemImage: "arrow.down.circle") }
                    Button { importing = true } label: { Label(Loc.t("Import Roblox IPA"), systemImage: "square.and.arrow.down") }
                } label: {
                    HStack(spacing: 6) {
                        Text(model.robloxVersion.map { "Roblox \($0)" } ?? "Roblox")
                            .lineLimit(1)
                        Image(systemName: "chevron.up").font(.system(size: 11, weight: .semibold))
                    }
                    .font(.system(size: 15, weight: .medium))
                    .foregroundColor(Palette.text)
                    .padding(.horizontal, 12)
                    .frame(minHeight: 40)
                    .frame(maxWidth: 130)
                    .glass(Capsule(), interactive: true)
                }
                .disabled(model.busy)
                Spacer(minLength: 8)
                if model.busy {
                    Button(Loc.t("Cancel")) { model.cancel() }.buttonStyle(StandardButtonStyle())
                } else {
                    if let patched = model.patched {
                        ShareLink(item: patched) {
                            Image(systemName: "square.and.arrow.up")
                        }
                        .buttonStyle(StandardButtonStyle())
                        .accessibilityLabel(Loc.t("Install"))
                    }
                    Button { model.launchRoblox() } label: {
                        Image(systemName: "play.fill")
                    }
                    .buttonStyle(StandardButtonStyle())
                    .accessibilityLabel(Loc.t("Launch Roblox"))
                    Button { model.patch() } label: {
                        Text(model.flagsDirty ? "Save & Apply" : "Apply Roblox").lineLimit(1).minimumScaleFactor(0.7)
                    }
                    .buttonStyle(PrimaryButtonStyle())
                }
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .glass(RoundedRectangle(cornerRadius: 26, style: .continuous))
        .padding(.horizontal, Metrics.pageInset)
        .padding(.vertical, 4)
        .fileImporter(isPresented: $importing, allowedContentTypes: [.ipa], allowsMultipleSelection: false) { result in
            if case .success(let urls) = result, let url = urls.first { model.importIpa(url) }
        }
    }
}

struct NoticeBanner: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        if let notice = model.notice {
            HStack(spacing: 10) {
                Image(systemName: notice.isError ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
                    .foregroundColor(notice.isError ? Palette.critical : Palette.success)
                Text(notice.text)
                    .font(.system(size: 14, weight: .medium))
                    .foregroundColor(Palette.text)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .glass(RoundedRectangle(cornerRadius: 20, style: .continuous))
            .padding(.horizontal, Metrics.pageInset)
            .padding(.top, 8)
            .transition(.move(edge: .top).combined(with: .opacity))
            .onTapGesture { model.notice = nil }
            .accessibilityAddTraits(.isStaticText)
        }
    }
}
