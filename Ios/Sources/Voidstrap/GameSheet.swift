import SwiftUI
import UIKit
import VoidstrapCore

struct GameSheet: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let game: LibraryGame
    @State private var details: GameDetails?
    @State private var instance = ""

    private func date(_ d: Date?) -> String {
        guard let d else { return Loc.t("Unknown") }
        if Calendar.current.isDateInToday(d) { return Loc.t("Today") }
        if Calendar.current.isDateInYesterday(d) { return Loc.t("Yesterday") }
        return d.formatted(date: .abbreviated, time: .omitted)
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    HStack(spacing: 14) {
                        GameIcon(url: game.iconUrl).frame(width: 72, height: 72).clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
                        VStack(alignment: .leading, spacing: 4) {
                            Text(game.name).font(.system(size: 20, weight: .bold)).foregroundColor(Palette.text)
                            if !game.creator.isEmpty { Text(Loc.t("By") + " " + game.creator).font(.system(size: 14)).foregroundColor(Palette.textSecondary) }
                        }
                    }
                    HStack {
                        Button(Loc.t("Play")) { model.play(game); dismiss() }.buttonStyle(PrimaryButtonStyle())
                        if let page = game.pageUrl {
                            ShareLink(item: page) { Label(Loc.t("Share link"), systemImage: "square.and.arrow.up") }.buttonStyle(StandardButtonStyle())
                        }
                    }

                    Card {
                        Row(symbol: "server.rack", title: "Join server", detail: "Paste a server instance id") {
                            TextField("", text: $instance).textInputAutocapitalization(.never).autocorrectionDisabled()
                                .frame(width: 140, height: 36).glass(RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous))
                            Button(Loc.t("Join")) {
                                guard let url = game.joinUrl(instance: instance) else { model.show("That server id is not valid.", error: true); return }
                                UIApplication.shared.open(url)
                            }
                            .buttonStyle(StandardButtonStyle())
                        }
                    }

                    if let d = details {
                        Card {
                            info("person.3", "Playing now", d.playing.formatted())
                            RowDivider()
                            info("eye", "Visits", d.visits.formatted())
                            RowDivider()
                            info("hand.thumbsup", "Rating", d.rating.map { "\($0)%" } ?? Loc.t("Unknown"))
                            RowDivider()
                            info("person.2", "Max players", "\(d.maxPlayers)")
                            RowDivider()
                            info("tag", "Genre", d.genre.isEmpty ? Loc.t("Unknown") : Loc.t(d.genre))
                            RowDivider()
                            info("calendar", "Created", date(d.created))
                            RowDivider()
                            info("arrow.clockwise", "Updated", date(d.updated))
                            RowDivider()
                            info("clock", "Last played", game.lastPlayed.map { date($0) } ?? Loc.t("Never"))
                            RowDivider()
                            info("number", "Times played", "\(game.timesPlayed)")
                        }
                        if !d.description.isEmpty {
                            Card {
                                Text(d.description).font(.system(size: 14)).foregroundColor(Palette.textSecondary)
                                    .frame(maxWidth: .infinity, alignment: .leading).padding(14)
                            }
                        }
                    } else {
                        ProgressView().frame(maxWidth: .infinity).padding()
                    }
                }
                .padding(Metrics.pageInset)
            }
            .background(GlassBackdrop())
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button(Loc.t("Close")) { dismiss() } } }
            .task { details = await RobloxApi.details(universeId: game.universeId) }
        }
    }

    private func info(_ symbol: String, _ title: String, _ value: String) -> some View {
        Row(symbol: symbol, title: title) {
            Text(verbatim: value).font(.system(size: 14, weight: .medium)).foregroundColor(Palette.textSecondary)
        }
    }
}
