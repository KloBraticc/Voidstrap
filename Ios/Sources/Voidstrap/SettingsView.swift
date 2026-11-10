import SwiftUI
import PhotosUI
import VoidstrapCore

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var showingLog = false
    @State private var showingToken = false
    @State private var photo: PhotosPickerItem?
    @State private var picking = false
    @ObservedObject private var custom = AppBackground.shared

    var body: some View {
        Page(title: "Settings", subtitle: "Appearance, applying and information about Voidstrap.") {
            SectionTitle(text: "Appearance")
            Card {
                Row(symbol: "circle.lefthalf.filled", title: "Theme", detail: Loc.t(model.settings.theme.title)) {
                    Picker(Loc.t("Theme"), selection: $model.settings.theme) {
                        ForEach(AppTheme.allCases) { Text(loc: $0.title).tag($0) }
                    }
                    .labelsHidden()
                }
                RowDivider()
                Row(symbol: "photo", title: "Background", detail: "Shows your own image behind every page.") {
                    Menu {
                        Button { setBackground(false) } label: { choice("None", !custom.enabled) }
                        Button { picking = true } label: { choice("Custom image", custom.enabled) }
                    } label: {
                        Text(loc: custom.enabled ? "Custom image" : "None").font(.system(size: 14, weight: .medium)).foregroundColor(Palette.primary)
                    }
                    .accessibilityLabel(Loc.t("Background"))
                }
                if custom.enabled {
                    RowDivider()
                    Row(symbol: "photo.on.rectangle", title: "Change image", detail: "Pick a different image from your photos.") { Chevron() }
                        .contentShape(Rectangle())
                        .onTapGesture { picking = true }
                    RowDivider()
                    Row(symbol: "circle.lefthalf.striped.horizontal", title: "Background dim", detail: "Fades the image so text stays easy to read.") {
                        Slider(value: Binding(get: { Double(custom.dim) }, set: { custom.dim = Int($0.rounded()) }), in: 0...90, step: 5) { editing in
                            if !editing { model.settings.backgroundDim = custom.dim }
                        }
                        .frame(width: 140)
                        .accessibilityLabel(Loc.t("Background dim"))
                        Text(verbatim: "\(custom.dim)%").font(.system(size: 13, weight: .medium)).foregroundColor(Palette.textSecondary).frame(width: 40, alignment: .trailing)
                    }
                }
            }
            .photosPicker(isPresented: $picking, selection: $photo, matching: .images)
            .onChange(of: photo) { item in
                guard let item else { return }
                photo = nil
                Task { @MainActor in
                    let data = try? await item.loadTransferable(type: Data.self)
                    if let data, await custom.install(data) {
                        setBackground(true)
                    } else {
                        model.show("That image could not be used. Pick a JPG, PNG or WEBP photo.", error: true)
                    }
                }
            }

            Card {
                Row(symbol: "globe", title: "Language", detail: "Translates Voidstrap with Google Translate.") {
                    Picker("", selection: $model.settings.language) {
                        ForEach(Array(Translator.codes.enumerated()), id: \.offset) { Text(verbatim: Translator.names[$0.offset]).tag($0.element) }
                    }
                    .labelsHidden()
                }
            }

            SectionTitle(text: "Applying")
            Card {
                Row(symbol: "externaldrive", title: "Keep the original Roblox IPA", detail: "Applying again is faster, but uses more storage.") {
                    Toggle("", isOn: $model.settings.keepOriginalIpa).labelsHidden().accessibilityLabel("Keep the original Roblox IPA")
                }
                RowDivider()
                Row(symbol: "key", title: "GitHub token", detail: model.settings.githubToken.isEmpty ? "Optional, raises the download check limit." : "Saved") { Chevron() }
                    .onTapGesture { showingToken = true }
                    .accessibilityAddTraits(.isButton)
            }

            SectionTitle(text: "Storage")
            Card {
                Row(symbol: "trash", title: "Remove downloaded Roblox", detail: model.robloxIpa == nil ? "Nothing saved." : "Frees the saved original IPA.") { Chevron() }
                    .onTapGesture {
                        if let ipa = model.robloxIpa { try? FileManager.default.removeItem(at: ipa) }
                        model.robloxIpa = RobloxIpa.cached()
                        model.show("Removed the saved Roblox IPA")
                    }
                    .disabled(model.robloxIpa == nil || model.busy)
                    .accessibilityAddTraits(.isButton)
                RowDivider()
                Row(symbol: "trash", title: "Remove patched Roblox", detail: model.patched == nil ? "Nothing saved." : "Frees the patched IPA.") { Chevron() }
                    .onTapGesture {
                        try? FileManager.default.removeItem(at: Paths.patched)
                        model.patched = nil
                        model.lastResult = nil
                        model.show("Removed the patched IPA")
                    }
                    .disabled(model.patched == nil || model.busy)
                    .accessibilityAddTraits(.isButton)
                RowDivider()
                Row(symbol: "doc.text", title: "View log", detail: "Voidstrap and FastFlag loader activity.") { Chevron() }
                    .onTapGesture { showingLog = true }
                    .accessibilityAddTraits(.isButton)
            }

            SectionTitle(text: "About")
            Card {
                Row(symbol: "info.circle", title: "Voidstrap \(model.appVersion)", detail: Paths.isLiveContainer ? "Running in LiveContainer" : "Running as a standalone app")
                RowDivider()
                Row(symbol: "person.2", title: "Credits", detail: "iOS patching is based on ipa-patcher and brsh by TheMallyGuy.")
                RowDivider()
                if let url = URL(string: "https://github.com/KloBraticc/Voidstrap") {
                    Link(destination: url) {
                        Row(symbol: "chevron.left.forwardslash.chevron.right", title: "Source code", detail: "github.com/KloBraticc/Voidstrap") { Chevron() }
                    }
                }
            }
        }
        .sheet(isPresented: $showingLog) { LogSheet() }
        .sheet(isPresented: $showingToken) { TokenSheet(token: model.settings.githubToken) { model.settings.githubToken = $0 } }
    }

    private func choice(_ title: String, _ on: Bool) -> some View {
        Group {
            if on { Label(Loc.t(title), systemImage: "checkmark") } else { Text(loc: title) }
        }
    }

    private func setBackground(_ on: Bool) {
        if !on { custom.remove() }
        model.settings.background = on
        AppBackground.shared.sync(model.settings)
    }
}

struct LogSheet: View {
    @Environment(\.dismiss) private var dismiss
    @State private var text = ""

    var body: some View {
        NavigationStack {
            ScrollView {
                Text(text.isEmpty ? "The log is empty." : text)
                    .font(.system(size: 12, design: .monospaced))
                    .foregroundColor(Palette.textSecondary)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(Metrics.pageInset)
            }
            .background(GlassBackdrop())
            .navigationTitle(Loc.t("Log"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button(Loc.t("Done")) { dismiss() } }
                ToolbarItem(placement: .primaryAction) {
                    ShareLink(item: text) { Image(systemName: "square.and.arrow.up") }.disabled(text.isEmpty)
                }
                ToolbarItem(placement: .destructiveAction) {
                    Button(Loc.t("Clear"), role: .destructive) {
                        Log.clear()
                        text = ""
                    }
                }
            }
            .task {
                text = Log.read()
            }
        }
    }
}

struct TokenSheet: View {
    @Environment(\.dismiss) private var dismiss
    @State var token: String
    let onSave: (String) -> Void

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    SecureField(Loc.t("Token"), text: $token)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                } footer: {
                    Text(loc: "A GitHub token is only used to check for new Roblox releases. It stays on this device.")
                }
            }
            .scrollContentBackground(.hidden)
            .background(GlassBackdrop())
            .navigationTitle(Loc.t("GitHub token"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button(Loc.t("Cancel")) { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button(Loc.t("Save")) {
                        onSave(token.trimmingCharacters(in: .whitespacesAndNewlines))
                        dismiss()
                    }
                }
            }
        }
    }
}
