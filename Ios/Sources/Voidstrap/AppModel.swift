import SwiftUI
import UIKit
import VoidstrapCore

enum Tab: String, CaseIterable, Identifiable {
    case home, library, mods, flags, settings
    var id: String { rawValue }

    var title: String {
        switch self {
        case .home: return "Home"
        case .library: return "Library"
        case .mods: return "Mods"
        case .flags: return "FastFlags"
        case .settings: return "Settings"
        }
    }

    var symbol: String {
        switch self {
        case .home: return "house"
        case .library: return "square.grid.2x2"
        case .mods: return "paintbrush"
        case .flags: return "flag"
        case .settings: return "gearshape"
        }
    }
}

struct Notice: Identifiable, Equatable {
    let id = UUID()
    let text: String
    let isError: Bool
}

@MainActor
final class AppModel: ObservableObject {
    @Published var tab: Tab = .home
    @Published var settings = AppSettings.load() { didSet { if settings != oldValue { settings.save() } } }
    @Published var mods = ModStore.load()
    @Published var flags = FlagStore.load()
    @Published var presets = PresetState.load() { didSet { if presets != oldValue { presets.save() } } }
    @Published var flagsDirty = false
    @Published var games = LibraryStore.load()
    @Published var robloxIpa = RobloxIpa.cached()
    @Published var patched: URL? = FileManager.default.fileExists(atPath: Paths.patched.path) ? Paths.patched : nil
    @Published var busy = false
    @Published var status = ""
    @Published var progress: Double?
    @Published var notice: Notice?
    @Published var lastResult: PatchResult?
    @Published var update: AppRelease?
    @Published var updateFile: URL?
    private var work: Task<Void, Never>?

    var robloxVersion: String? {
        robloxIpa.map { $0.deletingPathExtension().lastPathComponent.split(separator: "-").last.map(String.init) ?? $0.lastPathComponent }
    }

    var appVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    func show(_ text: String, error: Bool = false) {
        notice = Notice(text: Loc.t(text), isError: error)
        let id = notice?.id
        Task { [weak self] in
            try? await Task.sleep(nanoseconds: 3_500_000_000)
            if self?.notice?.id == id { self?.notice = nil }
        }
    }

    nonisolated private func report(_ text: String, _ value: Double?) {
        Task { @MainActor [weak self] in
            self?.status = text
            self?.progress = value
        }
    }

    func saveMods() { ModStore.save(mods) }

    func importMods(_ urls: [URL]) {
        var added = 0
        for url in urls {
            do {
                mods.append(try ModStore.importZip(url))
                added += 1
            } catch {
                Log.error("AppModel::importMods", error)
                show(error.localizedDescription, error: true)
            }
        }
        if added > 0 {
            saveMods()
            show(added == 1 ? "Added 1 mod pack" : "Added \(added) mod packs")
        }
    }

    func deleteMod(_ mod: ModPack) {
        ModStore.delete(mod)
        mods.removeAll { $0.id == mod.id }
        saveMods()
    }

    func saveFlags() {
        FlagStore.save(flags)
        flagsDirty = false
        show("FastFlags saved")
    }

    func setFlag(_ flag: FastFlag, replacing old: String? = nil) {
        if let old { flags.removeAll { $0.name == old } }
        flags.removeAll { $0.name == flag.name }
        flags.append(flag)
        flags.sort { $0.name.lowercased() < $1.name.lowercased() }
        flagsDirty = true
    }

    func removeFlag(_ name: String) {
        flags.removeAll { $0.name == name }
        flagsDirty = true
    }

    func importFlags(_ data: Data) {
        do {
            let incoming = try FlagStore.parse(data)
            for flag in incoming { setFlag(flag) }
            show(incoming.count == 1 ? "Imported 1 FastFlag" : "Imported \(incoming.count) FastFlags")
        } catch {
            show(error.localizedDescription, error: true)
        }
    }

    func flag(_ name: String) -> String? { flags.first { $0.name == name }?.value }

    func putFlag(_ name: String, _ value: String?) {
        if let value { setFlag(FastFlag(name: name, value: value)) } else if flag(name) != nil { removeFlag(name) }
        FlagStore.save(flags)
        flagsDirty = false
    }

    func saveGames() { LibraryStore.save(games) }

    func addGame(_ input: String) async {
        guard let placeId = LibraryStore.placeId(from: input) else {
            show("Enter a valid PlaceId or Roblox game link.", error: true)
            return
        }
        if games.contains(where: { $0.placeId == placeId }) {
            show("That game is already in your library.")
            return
        }
        do {
            let game = try await RobloxApi.game(placeId: placeId)
            games.insert(game, at: 0)
            saveGames()
            show("Added \(game.name) to your library")
        } catch {
            Log.error("AppModel::addGame", error)
            show(error.localizedDescription, error: true)
        }
    }

    func play(_ game: LibraryGame) {
        guard let url = game.launchUrl else { return }
        if flagsDirty { FlagStore.save(flags); flagsDirty = false } else { RobloxFlags.apply(flags) }
        if let index = games.firstIndex(where: { $0.placeId == game.placeId }) {
            games[index].lastPlayed = Date()
            games[index].timesPlayed += 1
            saveGames()
        }
        UIApplication.shared.open(url) { [weak self] opened in
            if !opened {
                Task { @MainActor in self?.show("Roblox is not installed. Apply Roblox and install the IPA first.", error: true) }
            }
        }
    }

    func launchRoblox() {
        if let url = URL(string: "roblox://") { launchUrl(url) }
    }

    func launchUrl(_ url: URL) {
        if flagsDirty { FlagStore.save(flags); flagsDirty = false } else { RobloxFlags.apply(flags) }
        UIApplication.shared.open(url) { [weak self] opened in
            if !opened {
                Task { @MainActor in self?.show("Roblox is not installed. Apply Roblox and install the IPA first.", error: true) }
            }
        }
    }

    func importIpa(_ url: URL) {
        start("Importing Roblox") { model in
            let saved = try RobloxIpa.importIpa(url)
            await MainActor.run { model.robloxIpa = saved }
        }
    }

    func download() {
        start("Checking the latest Roblox") { model in
            let token = await model.settings.githubToken
            let url = try await RobloxIpa.fetch(token: token) { text, value in model.report(text, value) }
            await MainActor.run { model.robloxIpa = url }
        }
    }

    func patch() {
        if flagsDirty { FlagStore.save(flags); flagsDirty = false }
        let enabled = mods.filter(\.enabled)
        let flags = flags
        let settings = settings
        let presets = presets
        let assets = Bundle.module.url(forResource: "ModAssets", withExtension: nil)
        start("Preparing Roblox") { model in
            let token = settings.githubToken
            let ipa: URL
            if let cached = await model.robloxIpa, FileManager.default.fileExists(atPath: cached.path) {
                ipa = cached
            } else {
                ipa = try await RobloxIpa.fetch(token: token) { text, value in model.report(text, value) }
                await MainActor.run { model.robloxIpa = ipa }
            }
            let result = try await Task.detached(priority: .userInitiated) {
                try RobloxIpa.patch(ipa: ipa, mods: enabled, flags: flags, settings: settings, presets: presets, assets: assets) { text, value in model.report(text, value) }
            }.value
            await MainActor.run {
                model.patched = result.output
                model.lastResult = result
                if !settings.keepOriginalIpa { model.robloxIpa = RobloxIpa.cached() }
                model.show("Roblox \(result.robloxVersion) is ready to install")
            }
        }
    }

    func checkForUpdate() async {
        try? FileManager.default.removeItem(at: AppUpdate.file)
        update = await AppUpdate.check(current: appVersion)
    }

    func downloadUpdate() {
        guard let release = update else { return }
        start("Downloading Voidstrap \(release.version)") { model in
            let file = try await AppUpdate.download(release) { value in model.report("Downloading Voidstrap \(release.version)", value) }
            await MainActor.run { model.updateFile = file }
        }
    }

    func installUpdateInLiveContainer() {
        guard let release = update,
              let encoded = release.url.absoluteString.addingPercentEncoding(withAllowedCharacters: .alphanumerics),
              let url = URL(string: "livecontainer://install?url=\(encoded)") else { return }
        UIApplication.shared.open(url) { [weak self] opened in
            if !opened {
                Task { @MainActor in self?.show("LiveContainer could not be opened. Download the update and install it from Files.", error: true) }
            }
        }
    }

    func cancel() {
        work?.cancel()
    }

    private func start(_ label: String, _ body: @escaping @Sendable (AppModel) async throws -> Void) {
        guard !busy else { return }
        busy = true
        status = label
        progress = nil
        UIApplication.shared.isIdleTimerDisabled = true
        work = Task { [weak self] in
            guard let self else { return }
            do {
                try await body(self)
            } catch is CancellationError {
                self.show("Cancelled")
            } catch let error as URLError where error.code == .cancelled {
                self.show("Cancelled")
            } catch {
                Log.error("AppModel::\(label)", error)
                self.show(error.localizedDescription, error: true)
            }
            self.busy = false
            self.progress = nil
            self.status = ""
            UIApplication.shared.isIdleTimerDisabled = false
        }
    }
}
