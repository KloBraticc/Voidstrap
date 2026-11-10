import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public struct FeaturedGame: Codable, Equatable, Sendable {
    public var universeId: Int64
    public var placeId: Int64
    public var name: String
    public var players: Int64
    public var rating: Int?
    public var thumbnail: String?
    public var video: String?

    public var videoUrl: URL? { video.map { Featured.folder.appendingPathComponent($0) } }
    public var launchUrl: URL? { URL(string: "roblox://placeId=\(placeId)") }
}

public enum Featured {
    static let folder = Paths.documents.appendingPathComponent("Cache/GameVideos", isDirectory: true)
    static let cacheFile = Paths.documents.appendingPathComponent("Cache/Featured.json")
    static let maxBytes = 64 << 20
    static let candidates = 6

    public static func cached() -> FeaturedGame? {
        guard var game = JSONFile.load(FeaturedGame.self, from: cacheFile) else { return nil }
        if let url = game.videoUrl, !FileManager.default.fileExists(atPath: url.path) { game.video = nil }
        return game
    }

    public static func load() async -> FeaturedGame? {
        let ident = "Featured::load"
        guard let url = URL(string: "https://apis.roblox.com/explore-api/v1/get-sorts?sessionId=\(UUID().uuidString)"),
              let object = try? await RobloxApi.json(url) as? [String: Any],
              let sorts = object["sorts"] as? [[String: Any]] else {
            Log.write(ident, "The top games list could not be loaded")
            return nil
        }
        var top: [FeaturedGame] = []
        var fallback: [FeaturedGame] = []
        for sort in sorts {
            let id = sort["sortId"] as? String ?? ""
            for item in sort["games"] as? [[String: Any]] ?? [] {
                guard let game = read(item) else { continue }
                if id.localizedCaseInsensitiveContains("top-playing") { top.append(game) } else { fallback.append(game) }
            }
        }
        let picks = (top.isEmpty ? fallback : top)
            .filter { !$0.name.localizedCaseInsensitiveContains("Brookhaven") }
            .prefix(candidates)
        guard var chosen = picks.first else { return nil }
        let previous = cached()?.universeId
        for candidate in picks.sorted(by: { a, _ in a.universeId == previous }) {
            if let video = await video(universeId: candidate.universeId) {
                chosen = candidate
                chosen.video = video
                break
            }
        }
        chosen.thumbnail = await thumbnail(universeId: chosen.universeId)
        Paths.ensure(cacheFile.deletingLastPathComponent())
        JSONFile.save(chosen, to: cacheFile)
        if chosen.video == nil { Log.write(ident, "None of the top games has a trailer video") }
        return chosen
    }

    static func read(_ item: [String: Any]) -> FeaturedGame? {
        let universeId = (item["universeId"] as? NSNumber)?.int64Value ?? 0
        let placeId = (item["rootPlaceId"] as? NSNumber)?.int64Value ?? 0
        let name = item["name"] as? String ?? ""
        guard universeId > 0, placeId > 0, !name.isEmpty else { return nil }
        let up = (item["totalUpVotes"] as? NSNumber)?.int64Value ?? 0
        let down = (item["totalDownVotes"] as? NSNumber)?.int64Value ?? 0
        return FeaturedGame(universeId: universeId, placeId: placeId, name: name,
                            players: (item["playerCount"] as? NSNumber)?.int64Value ?? 0,
                            rating: up + down > 0 ? Int(up * 100 / (up + down)) : nil)
    }

    static func thumbnail(universeId: Int64) async -> String? {
        guard let url = URL(string: "https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds=\(universeId)&countPerUniverse=1&size=768x432&format=Png&isCircular=false"),
              let object = try? await RobloxApi.json(url) as? [String: Any],
              let first = (object["data"] as? [[String: Any]])?.first,
              let image = (first["thumbnails"] as? [[String: Any]])?.first else { return nil }
        return image["imageUrl"] as? String
    }

    static func video(universeId: Int64) async -> String? {
        let ident = "Featured::video"
        guard let mediaUrl = URL(string: "https://games.roblox.com/v2/games/\(universeId)/media"),
              let media = try? await RobloxApi.json(mediaUrl) as? [String: Any],
              let id = (media["data"] as? [[String: Any]])?.compactMap({ $0["videoId"] as? String }).first(where: { Int64($0) != nil }) else { return nil }
        let name = id + ".webm"
        let file = folder.appendingPathComponent(name)
        if FileManager.default.fileExists(atPath: file.path) { return name }
        guard let assetUrl = URL(string: "https://assetdelivery.roblox.com/v2/assetId/\(id)"),
              let asset = try? await RobloxApi.json(assetUrl) as? [String: Any],
              let location = ((asset["locations"] as? [[String: Any]])?.first?["location"] as? String).flatMap(URL.init(string:)) else { return nil }
        do {
            let (data, response) = try await RobloxApi.session.data(from: location)
            if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) { return nil }
            guard data.count <= maxBytes, let body = gunzip(data) else { return nil }
            try? FileManager.default.removeItem(at: folder)
            Paths.ensure(folder)
            try body.write(to: file, options: .atomic)
            return name
        } catch {
            Log.error(ident, error)
            return nil
        }
    }

    static func gunzip(_ data: Data) -> Data? {
        guard data.count > 18, data[data.startIndex] == 0x1F, data[data.startIndex + 1] == 0x8B else { return data }
        let bytes = [UInt8](data)
        let flags = bytes[3]
        var pos = 10
        if flags & 4 != 0, pos + 2 <= bytes.count { pos += 2 + Int(bytes[pos]) | Int(bytes[pos + 1]) << 8 }
        if flags & 8 != 0 { while pos < bytes.count, bytes[pos] != 0 { pos += 1 }; pos += 1 }
        if flags & 16 != 0 { while pos < bytes.count, bytes[pos] != 0 { pos += 1 }; pos += 1 }
        if flags & 2 != 0 { pos += 2 }
        guard pos < bytes.count - 8 else { return nil }
        let raw = Data(bytes[pos..<(bytes.count - 8)])
        guard let out = try? (raw as NSData).decompressed(using: .zlib) as Data, out.count <= maxBytes else { return nil }
        return out
    }
}
