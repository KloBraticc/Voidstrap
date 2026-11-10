import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public struct LibraryGame: Codable, Identifiable, Equatable, Sendable {
    public var placeId: Int64
    public var universeId: Int64
    public var name: String
    public var creator: String
    public var iconUrl: String?
    public var pinned: Bool
    public var lastPlayed: Date?
    public var timesPlayed: Int

    public var id: Int64 { placeId }

    public var launchUrl: URL? { URL(string: "roblox://placeId=\(placeId)") }
    public var pageUrl: URL? { URL(string: "https://www.roblox.com/games/\(placeId)") }
}

public enum LibraryStore {
    public static func load() -> [LibraryGame] { JSONFile.load([LibraryGame].self, from: Paths.library) ?? [] }
    public static func save(_ games: [LibraryGame]) { JSONFile.save(games, to: Paths.library) }

    public static func placeId(from input: String) -> Int64? {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if let id = Int64(text), id > 0 { return id }
        guard let range = text.range(of: "/games/") else {
            if let match = text.range(of: "placeId=") {
                return Int64(text[match.upperBound...].prefix { $0.isNumber })
            }
            return nil
        }
        return Int64(text[range.upperBound...].prefix { $0.isNumber })
    }
}

public enum RobloxApi {
    static let session: URLSession = {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 20
        return URLSession(configuration: config)
    }()

    static func json(_ url: URL) async throws -> Any {
        let (data, response) = try await session.data(from: url)
        if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            throw ZipError("Roblox returned error \(http.statusCode).")
        }
        return try JSONSerialization.jsonObject(with: data)
    }

    public static func game(placeId: Int64) async throws -> LibraryGame {
        guard let universeUrl = URL(string: "https://apis.roblox.com/universes/v1/places/\(placeId)/universe"),
              let universeObject = try await json(universeUrl) as? [String: Any],
              let universeId = (universeObject["universeId"] as? NSNumber)?.int64Value, universeId > 0 else {
            throw ZipError("Could not find a game with that PlaceId.")
        }
        guard let detailUrl = URL(string: "https://games.roblox.com/v1/games?universeIds=\(universeId)"),
              let detail = try await json(detailUrl) as? [String: Any],
              let info = (detail["data"] as? [[String: Any]])?.first else {
            throw ZipError("Could not find a game with that PlaceId.")
        }
        let name = info["name"] as? String ?? "Place \(placeId)"
        let creator = (info["creator"] as? [String: Any])?["name"] as? String ?? ""
        let icon = await iconUrl(universeId: universeId)
        return LibraryGame(placeId: placeId, universeId: universeId, name: name, creator: creator, iconUrl: icon, pinned: false, lastPlayed: nil, timesPlayed: 0)
    }

    public static func iconUrl(universeId: Int64) async -> String? {
        guard let url = URL(string: "https://thumbnails.roblox.com/v1/games/icons?universeIds=\(universeId)&size=150x150&format=Png&isCircular=false"),
              let object = try? await json(url) as? [String: Any],
              let first = (object["data"] as? [[String: Any]])?.first else { return nil }
        return first["imageUrl"] as? String
    }
}
