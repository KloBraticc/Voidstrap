import SwiftUI
import VoidstrapCore

struct LibraryView: View {
    @EnvironmentObject private var model: AppModel
    @State private var search = ""
    @State private var adding = ""
    @State private var working = false
    @State private var selected: LibraryGame?

    private var filtered: [LibraryGame] {
        let query = search.trimmingCharacters(in: .whitespaces)
        guard !query.isEmpty else { return model.games }
        return model.games.filter { $0.name.localizedCaseInsensitiveContains(query) || $0.creator.localizedCaseInsensitiveContains(query) || "\($0.placeId)".contains(query) }
    }

    var body: some View {
        Page(title: "Library", subtitle: "Keep your favorite games one tap away and launch them in Roblox.") {
            Card {
                HStack(spacing: 8) {
                    TextField(Loc.t("Add by PlaceId or link"), text: $adding)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .keyboardType(.URL)
                        .submitLabel(.done)
                        .onSubmit(add)
                        .padding(.horizontal, 12)
                        .frame(minHeight: 40)
                        .surface(RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous))
                    Button(action: add) {
                        if working { ProgressView() } else { Text(loc: "Add") }
                    }
                    .buttonStyle(PrimaryButtonStyle())
                    .disabled(working || adding.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                .padding(12)
            }

            if model.games.isEmpty {
                Card {
                    EmptyState(symbol: "square.grid.2x2", title: "Your library is empty",
                               message: "Add a game by PlaceId or paste a Roblox game link.")
                }
            } else {
                HStack(spacing: 8) {
                    Image(systemName: "magnifyingglass").foregroundColor(Palette.textTertiary)
                    TextField(Loc.t("Search games"), text: $search)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                }
                .padding(.horizontal, 12)
                .frame(minHeight: 40)
                .surface(RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous))

                group("RECENT", Array(filtered.filter { $0.lastPlayed != nil }.sorted { ($0.lastPlayed ?? .distantPast) > ($1.lastPlayed ?? .distantPast) }.prefix(5)))
                group("PINNED", filtered.filter(\.pinned))
                group("ALL", filtered.filter { !$0.pinned })
                if filtered.isEmpty {
                    Card { EmptyState(symbol: "magnifyingglass", title: "No results", message: "No games match your search.") }
                }
            }
        }
        .sheet(item: $selected) { GameSheet(game: $0) }
    }

    @ViewBuilder private func group(_ title: String, _ games: [LibraryGame]) -> some View {
        if !games.isEmpty {
            Text(loc: "\(title) (\(games.count))")
                .font(.system(size: 12.5, weight: .semibold))
                .foregroundColor(Palette.textTertiary)
                .padding(.top, 8)
            Card {
                ForEach(Array(games.enumerated()), id: \.element.id) { index, game in
                    if index > 0 { RowDivider() }
                    GameRow(game: game)
                        .contextMenu {
                            Button { selected = game } label: { Label(Loc.t("Details"), systemImage: "info.circle") }
                            Button { toggle(game) } label: {
                                Label(Loc.t(game.pinned ? "Unpin" : "Pin"), systemImage: game.pinned ? "pin.slash" : "pin")
                            }
                            if let page = game.pageUrl {
                                Link(destination: page) { Label(Loc.t("Open game page"), systemImage: "safari") }
                            }
                            Button(role: .destructive) { remove(game) } label: {
                                Label(Loc.t("Remove from library"), systemImage: "trash")
                            }
                        }
                }
            }
        }
    }

    private func add() {
        let text = adding
        guard !working, !text.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        working = true
        Task {
            await model.addGame(text)
            adding = ""
            working = false
        }
    }

    private func toggle(_ game: LibraryGame) {
        guard let index = model.games.firstIndex(where: { $0.id == game.id }) else { return }
        model.games[index].pinned.toggle()
        model.saveGames()
    }

    private func remove(_ game: LibraryGame) {
        model.games.removeAll { $0.id == game.id }
        model.saveGames()
        model.show("Removed \(game.name) from your library")
    }
}
