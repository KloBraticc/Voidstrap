import SwiftUI
import UniformTypeIdentifiers
import VoidstrapCore

struct ModsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var importing = false
    @State private var pendingDelete: ModPack?

    var body: some View {
        Page(title: "Mods", subtitle: "Customize Roblox with presets, mod packs and your own files.") {
            Card {
                Row(symbol: "plus", title: "Import mod pack", detail: "Choose a zip with content, ExtraContent, shaders or ios folders.", tint: Palette.primary) { Chevron() }
                    .onTapGesture { importing = true }
                    .accessibilityAddTraits(.isButton)
            }

            ModPresetsView()

            SectionTitle(text: "Mod packs")
            if model.mods.isEmpty {
                Card {
                    EmptyState(symbol: "paintbrush", title: "No mod packs yet",
                               message: "Mod packs made for Voidstrap, Bloxstrap or Fishstrap work here too.")
                }
            } else {
                Text(loc: "Later packs win when two packs change the same file.")
                    .font(.system(size: 13))
                    .foregroundColor(Palette.textTertiary)
                Card {
                    ForEach(Array(model.mods.enumerated()), id: \.element.id) { index, mod in
                        if index > 0 { RowDivider() }
                        row(mod, index: index)
                    }
                }
            }
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.zip], allowsMultipleSelection: true) { result in
            switch result {
            case .success(let urls): model.importMods(urls)
            case .failure(let error): model.show(error.localizedDescription, error: true)
            }
        }
        .confirmationDialog(Loc.t("Delete this mod pack?"), isPresented: Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } }),
                            titleVisibility: .visible, presenting: pendingDelete) { mod in
            Button(Loc.t("Delete \(mod.name)"), role: .destructive) { model.deleteMod(mod) }
        }
    }

    private func row(_ mod: ModPack, index: Int) -> some View {
        Row(symbol: "shippingbox", title: mod.name, detail: mod.fileCount == 1 ? "1 file" : "\(mod.fileCount) files",
            tint: mod.enabled ? Palette.primary : Palette.textTertiary) {
            Menu {
                Button { move(index, by: -1) } label: { Label(Loc.t("Move up"), systemImage: "arrow.up") }.disabled(index == 0)
                Button { move(index, by: 1) } label: { Label(Loc.t("Move down"), systemImage: "arrow.down") }.disabled(index == model.mods.count - 1)
                Button(role: .destructive) { pendingDelete = mod } label: { Label(Loc.t("Delete"), systemImage: "trash") }
            } label: {
                Image(systemName: "ellipsis")
                    .font(.system(size: 16, weight: .semibold))
                    .foregroundColor(Palette.textSecondary)
                    .frame(width: 36, height: 36)
                    .contentShape(Rectangle())
            }
            .accessibilityLabel("More options for \(mod.name)")
            Toggle("", isOn: Binding(get: { mod.enabled }, set: { value in
                guard let i = model.mods.firstIndex(where: { $0.id == mod.id }) else { return }
                model.mods[i].enabled = value
                model.saveMods()
            }))
            .labelsHidden()
            .accessibilityLabel("Enable \(mod.name)")
        }
    }

    private func move(_ index: Int, by offset: Int) {
        let target = index + offset
        guard model.mods.indices.contains(index), model.mods.indices.contains(target) else { return }
        model.mods.swapAt(index, target)
        model.saveMods()
    }
}
