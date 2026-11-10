import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public struct AppRelease: Equatable, Sendable {
    public let version: String
    public let url: URL
    public let size: Int
}

public enum AppUpdate {
    static let releaseApi = URL(string: "https://api.github.com/repos/KloBraticc/Voidstrap/releases/latest")!
    public static let file = Paths.documents.appendingPathComponent("Voidstrap-Update.ipa")

    public static func isNewer(_ candidate: String, than current: String) -> Bool {
        let a = candidate.split(separator: ".").map { Int($0) ?? 0 }
        let b = current.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0
            let y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    public static func version(of name: String) -> String? {
        let lower = name.lowercased()
        guard lower.hasPrefix("voidstrap-"), lower.hasSuffix(".ipa") else { return nil }
        let version = String(name.dropFirst(10).dropLast(4))
        guard !version.isEmpty, version.allSatisfy({ $0.isNumber || $0 == "." }),
              version.split(separator: ".").count >= 2 else { return nil }
        return version
    }

    public static func check(current: String) async -> AppRelease? {
        var request = URLRequest(url: releaseApi)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.timeoutInterval = 20
        do {
            let (data, response) = try await RobloxIpa.session.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else { return nil }
            struct Release: Decodable { let assets: [Asset] }
            struct Asset: Decodable { let name: String; let size: Int; let browser_download_url: String }
            let release = try JSONDecoder().decode(Release.self, from: data)
            var best: AppRelease?
            for asset in release.assets {
                guard let version = version(of: asset.name), isNewer(version, than: current),
                      best.map({ isNewer(version, than: $0.version) }) ?? true,
                      let url = URL(string: asset.browser_download_url) else { continue }
                best = AppRelease(version: version, url: url, size: asset.size)
            }
            return best
        } catch {
            Log.write("AppUpdate::check", "Update check failed: \(error.localizedDescription)")
            return nil
        }
    }

    public static func download(_ release: AppRelease, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let temp = try await RobloxIpa.download(release.url, expected: release.size, progress: progress)
        Paths.ensure(file.deletingLastPathComponent())
        try? FileManager.default.removeItem(at: file)
        try FileManager.default.moveItem(at: temp, to: file)
        Log.write("AppUpdate::download", "Downloaded Voidstrap \(release.version)")
        return file
    }
}
