import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public struct GameDetails: Sendable {
    public var description = ""
    public var genre = ""
    public var visits: Int64 = 0
    public var playing: Int64 = 0
    public var maxPlayers = 0
    public var created: Date?
    public var updated: Date?
    public var upVotes: Int64 = 0
    public var downVotes: Int64 = 0

    public var rating: Int? {
        let total = upVotes + downVotes
        return total > 0 ? Int((Double(upVotes) / Double(total) * 100).rounded()) : nil
    }
}

extension RobloxApi {
    static func date(_ value: Any?) -> Date? {
        guard let text = value as? String else { return nil }
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let d = f.date(from: text) { return d }
        f.formatOptions = [.withInternetDateTime]
        return f.date(from: text)
    }

    public static func details(universeId: Int64) async -> GameDetails {
        var out = GameDetails()
        if let url = URL(string: "https://games.roblox.com/v1/games?universeIds=\(universeId)"),
           let object = try? await json(url) as? [String: Any], let info = (object["data"] as? [[String: Any]])?.first {
            out.description = info["description"] as? String ?? ""
            out.genre = info["genre"] as? String ?? ""
            out.visits = (info["visits"] as? NSNumber)?.int64Value ?? 0
            out.playing = (info["playing"] as? NSNumber)?.int64Value ?? 0
            out.maxPlayers = (info["maxPlayers"] as? NSNumber)?.intValue ?? 0
            out.created = date(info["created"])
            out.updated = date(info["updated"])
        }
        if let url = URL(string: "https://games.roblox.com/v1/games/votes?universeIds=\(universeId)"),
           let object = try? await json(url) as? [String: Any], let votes = (object["data"] as? [[String: Any]])?.first {
            out.upVotes = (votes["upVotes"] as? NSNumber)?.int64Value ?? 0
            out.downVotes = (votes["downVotes"] as? NSNumber)?.int64Value ?? 0
        }
        return out
    }
}

extension LibraryGame {
    public func joinUrl(instance: String) -> URL? {
        let id = instance.trimmingCharacters(in: .whitespacesAndNewlines)
        guard UUID(uuidString: id) != nil else { return nil }
        return URL(string: "roblox://experiences/start?placeId=\(placeId)&gameInstanceId=\(id)")
    }
}
