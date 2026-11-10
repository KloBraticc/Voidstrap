import SwiftUI
import UniformTypeIdentifiers
import VoidstrapCore

extension UTType {
    static let ipa = UTType(filenameExtension: "ipa") ?? .data
}

struct HomeView: View {
    @EnvironmentObject private var model: AppModel

    private var recent: [LibraryGame] {
        model.games.filter { $0.lastPlayed != nil }.sorted { ($0.lastPlayed ?? .distantPast) > ($1.lastPlayed ?? .distantPast) }.prefix(6).map { $0 }
    }

    var body: some View {
        Page(title: "Home", subtitle: "Patch Roblox with your mods and FastFlags, then install it with your sideloader.") {
            if let release = model.update {
                Card {
                    Row(symbol: "arrow.down.app", title: "Voidstrap \(release.version) is available",
                        detail: model.updateFile == nil ? "You have \(model.appVersion). Update to get the newest fixes and features."
                            : "Downloaded. Share it to LiveContainer or your sideloader to install it over this version.",
                        tint: Palette.primary)
                    HStack(spacing: 8) {
                        if let file = model.updateFile {
                            ShareLink(item: file) { Label(Loc.t("Install"), systemImage: "square.and.arrow.up") }
                                .buttonStyle(PrimaryButtonStyle())
                        } else {
                            Button(Loc.t("Download")) { model.downloadUpdate() }
                                .buttonStyle(PrimaryButtonStyle())
                                .disabled(model.busy)
                        }
                        if Paths.isLiveContainer {
                            Button(Loc.t("Open in LiveContainer")) { model.installUpdateInLiveContainer() }
                                .buttonStyle(StandardButtonStyle())
                        }
                        Spacer()
                    }
                    .padding(.horizontal, 12)
                    .padding(.bottom, 12)
                }
            }
            Card {
                Row(symbol: "shippingbox", title: model.robloxVersion.map { "Roblox \($0)" } ?? "Roblox is not downloaded",
                    detail: model.robloxIpa == nil ? "Download the latest Roblox or import your own IPA from the bar below." : "Saved on this device.",
                    tint: model.robloxIpa == nil ? Palette.textTertiary : Palette.success)
                RowDivider()
                Row(symbol: "checkmark.seal", title: model.patched == nil ? "Not patched yet" : "Patched Roblox is ready",
                    detail: model.lastResult.map { "\($0.modFiles) mod files and \($0.flagCount) FastFlags from your last patch." }
                        ?? (model.patched == nil ? "Tap Patch Roblox to build your copy." : "Tap Install to send it to LiveContainer, SideStore or Files."),
                    tint: model.patched == nil ? Palette.textTertiary : Palette.success)
                RowDivider()
                Row(symbol: "paintbrush", title: "\(model.mods.filter(\.enabled).count) of \(model.mods.count) mod packs on",
                    detail: "Manage them on the Mods page.") { Chevron() }
                    .onTapGesture { model.tab = .mods }
                RowDivider()
                Row(symbol: "flag", title: "\(model.flags.filter(\.appliesOnIos).count) FastFlags",
                    detail: "Applied while Roblox runs.") { Chevron() }
                    .onTapGesture { model.tab = .flags }
            }

            SectionTitle(text: "Continue Playing")
            if recent.isEmpty {
                Card {
                    EmptyState(symbol: "gamecontroller", title: "No history yet",
                               message: "Games you launch from Voidstrap will show up here so you can jump back in.")
                }
            } else {
                Card {
                    ForEach(Array(recent.enumerated()), id: \.element.id) { index, game in
                        if index > 0 { RowDivider() }
                        GameRow(game: game)
                    }
                }
            }

            SectionTitle(text: "How it works")
            Card {
                Row(symbol: "1.circle", title: "Get Roblox", detail: "Download the newest Roblox IPA or import one you already have.")
                RowDivider()
                Row(symbol: "2.circle", title: "Choose mods and FastFlags", detail: "Turn on mod packs and add the FastFlags you want.")
                RowDivider()
                Row(symbol: "3.circle", title: "Patch and install", detail: "Patch Roblox, then share the IPA to LiveContainer, SideStore or another sideloader.")
                RowDivider()
                Row(symbol: "4.circle", title: "Update FastFlags any time",
                    detail: Paths.isLiveContainer
                        ? "Run Roblox once in LiveContainer, then saved FastFlags reach it on its next launch without patching again."
                        : "FastFlags need Roblox installed in LiveContainer next to Voidstrap, since iOS keeps other apps' files private.")
            }
        }
    }
}

struct GameIcon: View {
    let url: String?

    var body: some View {
        AsyncImage(url: url.flatMap(URL.init(string:))) { phase in
            if let image = phase.image {
                image.resizable().scaledToFill()
            } else {
                Palette.input.overlay(Image(systemName: "gamecontroller").foregroundColor(Palette.textTertiary))
            }
        }
        .frame(width: 48, height: 48)
        .clipShape(RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous))
        .accessibilityHidden(true)
    }
}

struct GameRow: View {
    @EnvironmentObject private var model: AppModel
    let game: LibraryGame

    var body: some View {
        HStack(spacing: 12) {
            GameIcon(url: game.iconUrl)
            VStack(alignment: .leading, spacing: 2) {
                Text(game.name).font(.system(size: 16, weight: .medium)).foregroundColor(Palette.text).lineLimit(1)
                Text(loc: detail).font(.system(size: 13)).foregroundColor(Palette.textSecondary).lineLimit(1)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Button(Loc.t("Play")) { model.play(game) }
                .buttonStyle(PrimaryButtonStyle())
                .accessibilityLabel("Play \(game.name)")
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private var detail: String {
        if let date = game.lastPlayed {
            return "Last played " + RelativeDateTimeFormatter().localizedString(for: date, relativeTo: Date())
        }
        return game.creator.isEmpty ? "Place \(game.placeId)" : "By \(game.creator)"
    }
}
