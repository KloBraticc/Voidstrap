import SwiftUI
import UniformTypeIdentifiers
import VoidstrapCore

struct ModPresetsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var picking: Pick?
    @State private var skies: [String] = []
    @State private var working = false

    enum Pick: Identifiable {
        case cursor, shiftLock, death, font
        var id: Self { self }
        var types: [UTType] {
            switch self {
            case .cursor, .shiftLock: return [.png]
            case .death: return [.audio, UTType(filenameExtension: "ogg") ?? .audio]
            case .font: return [.font, UTType(filenameExtension: "ttf") ?? .font, UTType(filenameExtension: "otf") ?? .font]
            }
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            SectionTitle(text: "Cursor")
            Card {
                menu("cursorarrow", "Cursor style", "Used with a mouse or trackpad", ModPresets.cursorStyles.map(\.title),
                     ModPresets.cursorStyles.firstIndex { $0.id == model.presets.cursor } ?? 0) { i in
                    let id = ModPresets.cursorStyles[i].id
                    if id == 10 && !exists(ModPresets.customCursor) { picking = .cursor } else { model.presets.cursor = id }
                }
                RowDivider()
                fileRow("lock", "Shift lock icon", model.presets.shiftLock, pick: .shiftLock) { model.presets.shiftLock = false }
            }

            SectionTitle(text: "Interface")
            Card {
                toggle("eye.slash", "Hide the Roblox interface", "Blanks Roblox menu images and icons", model.presets.hideGui) { model.presets.hideGui = $0 }
            }

            SectionTitle(text: "Appearance")
            Card {
                toggle("person.crop.rectangle", "Old avatar background", "Brings back the classic avatar editor backdrop", model.presets.oldAvatar) { model.presets.oldAvatar = $0 }
                RowDivider()
                menu("face.smiling", "Emoji style", nil, ModPresets.emojiTitles, model.presets.emoji) { i in
                    run("Downloading") {
                        try await ModPresets.downloadEmoji(i)
                        model.presets.emoji = i
                    }
                }
                RowDivider()
                menu("sun.haze", "Skybox", skies.isEmpty ? "Loading skybox packs" : nil, ["Default"] + skies,
                     model.presets.sky.flatMap { s in skies.firstIndex(of: s).map { $0 + 1 } } ?? 0) { i in
                    guard i > 0 else { model.presets.sky = nil; return }
                    let name = skies[i - 1]
                    run("Downloading") {
                        try await ModPresets.downloadSky(name)
                        model.presets.sky = name
                    }
                }
                RowDivider()
                fileRow("textformat", "Custom font", model.presets.font, pick: .font) { model.presets.font = false }
            }

            SectionTitle(text: "Sounds")
            Card {
                toggle("speaker.wave.2", "Old death sound", "Brings back the classic oof", model.presets.oldDeath) { model.presets.oldDeath = $0 }
                RowDivider()
                fileRow("music.note", "Custom death sound", model.presets.deathSound, pick: .death) { model.presets.deathSound = false }
            }
        }
        .disabled(working)
        .task { if skies.isEmpty { skies = await ModPresets.skyPacks() } }
        .fileImporter(isPresented: Binding(get: { picking != nil }, set: { if !$0 { picking = nil } }),
                      allowedContentTypes: picking?.types ?? [.data]) { result in
            guard let kind = picking, case .success(let url) = result else { return }
            imported(kind, url)
        }
    }

    private func exists(_ url: URL) -> Bool { FileManager.default.fileExists(atPath: url.path) }

    private func imported(_ kind: Pick, _ url: URL) {
        do {
            switch kind {
            case .cursor:
                try ModPresets.importFile(url, to: ModPresets.customCursor, maxSize: 8 << 20)
                model.presets.cursor = 10
            case .shiftLock:
                try ModPresets.importFile(url, to: ModPresets.shiftLockFile, maxSize: 8 << 20)
                model.presets.shiftLock = true
            case .death:
                try ModPresets.importFile(url, to: ModPresets.deathFile, maxSize: 256 << 20)
                model.presets.deathSound = true
            case .font:
                try ModPresets.importFile(url, to: ModPresets.fontFile, maxSize: 32 << 20)
                model.presets.font = true
            }
            model.show("Saved, patch Roblox to apply it")
        } catch {
            model.show(error.localizedDescription, error: true)
        }
    }

    private func run(_ label: String, _ body: @escaping @MainActor () async throws -> Void) {
        working = true
        model.show(label)
        Task { @MainActor in
            do { try await body() } catch { model.show(error.localizedDescription, error: true) }
            working = false
        }
    }

    private func toggle(_ symbol: String, _ title: String, _ detail: String, _ value: Bool, _ change: @escaping (Bool) -> Void) -> some View {
        Row(symbol: symbol, title: title, detail: detail) {
            Toggle("", isOn: Binding(get: { value }, set: change)).labelsHidden().accessibilityLabel(Loc.t(title))
        }
    }

    private func fileRow(_ symbol: String, _ title: String, _ on: Bool, pick: Pick, remove: @escaping () -> Void) -> some View {
        Row(symbol: symbol, title: title, detail: on ? "On" : "Off") {
            if on {
                Button(Loc.t("Remove"), action: remove).buttonStyle(StandardButtonStyle())
            }
            Button(Loc.t("Choose")) { picking = pick }.buttonStyle(StandardButtonStyle())
        }
    }

    private func menu(_ symbol: String, _ title: String, _ detail: String?, _ labels: [String], _ selected: Int, _ change: @escaping (Int) -> Void) -> some View {
        Row(symbol: symbol, title: title, detail: detail) {
            Menu {
                ForEach(Array(labels.enumerated()), id: \.offset) { item in
                    Button { change(item.offset) } label: {
                        if item.offset == selected { Label(Loc.t(item.element), systemImage: "checkmark") } else { Text(loc: item.element) }
                    }
                }
            } label: {
                HStack(spacing: 4) {
                    Text(loc: labels.indices.contains(selected) ? labels[selected] : "").lineLimit(1)
                    Image(systemName: "chevron.up.chevron.down").font(.system(size: 11, weight: .semibold))
                }
                .font(.system(size: 14, weight: .medium))
                .foregroundColor(Palette.primary)
            }
            .accessibilityLabel(Loc.t(title))
        }
    }
}
