import SwiftUI
import UIKit
import UniformTypeIdentifiers
import VoidstrapCore

struct FlagEditing: Identifiable {
    let id = UUID()
    var original: String?
    var name: String
    var value: String
}

struct FlagsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var editing: FlagEditing?
    @State private var importing = false
    @State private var pasting = false
    @State private var pasted = ""
    @State private var search = ""

    private var filtered: [FastFlag] {
        let query = search.trimmingCharacters(in: .whitespaces)
        return query.isEmpty ? model.flags : model.flags.filter { $0.name.localizedCaseInsensitiveContains(query) }
    }

    var body: some View {
        Page(title: "FastFlag Editor", subtitle: "Add, edit and import FastFlags for this profile.") {
            Card {
                Row(symbol: "plus", title: "Add FastFlag", tint: Palette.primary) { Chevron() }
                    .onTapGesture { editing = FlagEditing(original: nil, name: "", value: "") }
                    .accessibilityAddTraits(.isButton)
                RowDivider()
                Row(symbol: "doc.on.clipboard", title: "Import JSON", detail: "Paste FastFlags or pick a JSON file.") {
                    Menu {
                        Button { pasted = UIPasteboard.general.string ?? ""; pasting = true } label: { Label(Loc.t("Paste JSON"), systemImage: "doc.on.clipboard") }
                        Button { importing = true } label: { Label(Loc.t("Choose a file"), systemImage: "folder") }
                    } label: { Chevron().frame(width: 36, height: 36) }
                }
                RowDivider()
                Row(symbol: "square.and.arrow.up", title: "Copy as JSON", detail: "Copies every FastFlag on this page.") { Chevron() }
                    .onTapGesture {
                        UIPasteboard.general.string = String(decoding: FlagStore.encode(model.flags), as: UTF8.self)
                        model.show("Copied \(model.flags.count) FastFlags")
                    }
                    .accessibilityAddTraits(.isButton)
            }

            FlagPresetsView()

            HStack {
                SectionTitle(text: "FastFlags (\(model.flags.count))")
                if model.flagsDirty {
                    Button(Loc.t("Save")) { model.saveFlags() }.buttonStyle(PrimaryButtonStyle())
                }
            }
            if model.flags.isEmpty {
                Card { EmptyState(symbol: "flag", title: "No FastFlags", message: "Add one, import JSON or turn on a preset.") }
            } else {
                HStack(spacing: 8) {
                    Image(systemName: "magnifyingglass").foregroundColor(Palette.textTertiary)
                    TextField(Loc.t("Search FastFlags"), text: $search)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                }
                .padding(.horizontal, 12)
                .frame(minHeight: 40)
                .glass(RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous))
                LazyVStack(spacing: 0) {
                    ForEach(Array(filtered.enumerated()), id: \.element.id) { index, flag in
                        if index > 0 { RowDivider() }
                        flagRow(flag)
                    }
                }
                .glass(RoundedRectangle(cornerRadius: Metrics.cardRadius, style: .continuous))
            }
        }
        .sheet(item: $editing) { item in
            FlagEditor(item: item) { name, value in
                model.setFlag(FastFlag(name: name, value: value), replacing: item.original)
            }
        }
        .sheet(isPresented: $pasting) {
            PasteSheet(text: $pasted) { model.importFlags(Data($0.utf8)) }
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.json, .plainText], allowsMultipleSelection: false) { result in
            guard case .success(let urls) = result, let url = urls.first else { return }
            let access = url.startAccessingSecurityScopedResource()
            defer { if access { url.stopAccessingSecurityScopedResource() } }
            if let data = try? Data(contentsOf: url) { model.importFlags(data) } else { model.show("That file could not be read.", error: true) }
        }
    }

    private func flagRow(_ flag: FastFlag) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(flag.name)
                    .font(.system(size: 15, weight: .medium, design: .monospaced))
                    .foregroundColor(Palette.text)
                    .lineLimit(2)
                Text(flag.value)
                    .font(.system(size: 13, design: .monospaced))
                    .foregroundColor(Palette.textSecondary)
                    .lineLimit(1)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            if !flag.appliesOnIos {
                Badge(text: "Not on iOS", tint: Palette.textTertiary)
            }
            if flag.kind == .bool {
                Toggle("", isOn: Binding(get: { flag.value.lowercased() == "true" }, set: { model.setFlag(FastFlag(name: flag.name, value: $0 ? "True" : "False")) }))
                    .labelsHidden()
                    .accessibilityLabel(flag.name)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .contentShape(Rectangle())
        .onTapGesture { editing = FlagEditing(original: flag.name, name: flag.name, value: flag.value) }
        .contextMenu {
            Button { editing = FlagEditing(original: flag.name, name: flag.name, value: flag.value) } label: { Label(Loc.t("Edit"), systemImage: "pencil") }
            Button { UIPasteboard.general.string = flag.name } label: { Label(Loc.t("Copy name"), systemImage: "doc.on.doc") }
            Button(role: .destructive) { model.removeFlag(flag.name) } label: { Label(Loc.t("Delete"), systemImage: "trash") }
        }
    }
}

struct FlagEditor: View {
    @Environment(\.dismiss) private var dismiss
    @State var item: FlagEditing
    let onSave: (String, String) -> Void

    private var error: String? {
        item.name.isEmpty && item.value.isEmpty ? nil : FlagStore.validate(name: item.name, value: item.value)
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField(Loc.t("Name"), text: $item.name)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .font(.system(.body, design: .monospaced))
                    TextField(Loc.t("Value"), text: $item.value)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .font(.system(.body, design: .monospaced))
                } footer: {
                    if let error { Text(error).foregroundColor(Palette.critical) }
                    else if FlagValueKind.of(item.name) == .string, !item.name.isEmpty {
                        Text(loc: "Text FastFlags are saved but Roblox on iOS only applies True or False and number FastFlags.")
                    }
                }
            }
            .scrollContentBackground(.hidden)
            .background(GlassBackdrop())
            .navigationTitle(item.original == nil ? "Add FastFlag" : "Edit FastFlag")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button(Loc.t("Cancel")) { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button(Loc.t("Save")) {
                        let name = item.name.trimmingCharacters(in: .whitespaces)
                        var value = item.value.trimmingCharacters(in: .whitespaces)
                        if FlagValueKind.of(name) == .bool { value = value.lowercased() == "true" ? "True" : "False" }
                        onSave(name, value)
                        dismiss()
                    }
                    .disabled(item.name.isEmpty || FlagStore.validate(name: item.name, value: item.value) != nil)
                }
            }
        }
    }
}

struct PasteSheet: View {
    @Environment(\.dismiss) private var dismiss
    @Binding var text: String
    let onImport: (String) -> Void

    var body: some View {
        NavigationStack {
            TextEditor(text: $text)
                .font(.system(.body, design: .monospaced))
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .scrollContentBackground(.hidden)
                .padding(12)
                .glass(RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous))
                .padding(Metrics.pageInset)
                .background(GlassBackdrop())
                .navigationTitle(Loc.t("Import JSON"))
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) { Button(Loc.t("Cancel")) { dismiss() } }
                    ToolbarItem(placement: .confirmationAction) {
                        Button(Loc.t("Import")) {
                            onImport(text)
                            dismiss()
                        }
                        .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    }
                }
        }
    }
}
