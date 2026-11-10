import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public struct RobloxRelease: Equatable {
    public let name: String
    public let size: Int
    public let url: URL

    public var version: String {
        let base = name.replacingOccurrences(of: ".ipa", with: "")
        return base.split(separator: "-").last.map(String.init) ?? base
    }
}

public struct PatchResult: Sendable {
    public let output: URL
    public let modFiles: Int
    public let flagCount: Int
    public let robloxVersion: String
}

public enum RobloxIpa {
    static let releaseApi = URL(string: "https://api.github.com/repos/Roblox-DeployHistory-Updates/ios-runner/releases/latest")!
    static let session: URLSession = {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 60 * 60
        return URLSession(configuration: config)
    }()

    public static func cached() -> URL? {
        let files = (try? FileManager.default.contentsOfDirectory(at: Paths.roblox, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        return files.filter { $0.pathExtension == "ipa" }.max {
            let a = (try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            let b = (try? $1.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            return a < b
        }
    }

    public static func latest(token: String) async throws -> RobloxRelease {
        var request = URLRequest(url: releaseApi)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        if !token.trimmingCharacters(in: .whitespaces).isEmpty {
            request.setValue("Bearer \(token.trimmingCharacters(in: .whitespaces))", forHTTPHeaderField: "Authorization")
        }
        let (data, response) = try await session.data(for: request)
        if let http = response as? HTTPURLResponse, http.statusCode != 200 {
            if http.statusCode == 403 || http.statusCode == 429 { throw ZipError("GitHub rate limit reached. Add a GitHub token in Settings or try later.") }
            throw ZipError("GitHub returned error \(http.statusCode).")
        }
        struct Release: Decodable { let assets: [Asset] }
        struct Asset: Decodable { let name: String; let size: Int; let browser_download_url: String }
        let release = try JSONDecoder().decode(Release.self, from: data)
        guard let asset = release.assets.first(where: { $0.name.hasSuffix(".ipa") }), let url = URL(string: asset.browser_download_url) else {
            throw ZipError("No Roblox IPA was found in the latest release.")
        }
        return RobloxRelease(name: asset.name, size: asset.size, url: url)
    }

    public static func fetch(token: String, status: @escaping PatchStatus) async throws -> URL {
        Paths.ensure(Paths.roblox)
        status("Checking the latest Roblox", nil)
        let release: RobloxRelease
        do {
            release = try await latest(token: token)
        } catch {
            Log.error("RobloxIpa::fetch", error)
            if let cached = cached() {
                status("Using the saved Roblox \(cached.deletingPathExtension().lastPathComponent)", nil)
                return cached
            }
            throw error
        }
        let dest = Paths.roblox.appendingPathComponent(release.name)
        if let size = try? dest.resourceValues(forKeys: [.fileSizeKey]).fileSize, size == release.size {
            status("Using the saved Roblox \(release.version)", nil)
            return dest
        }
        let label = "Downloading Roblox \(release.version)"
        status(label, 0)
        let temp = try await download(release.url, expected: release.size) { status(label, $0) }
        try? FileManager.default.removeItem(at: dest)
        try FileManager.default.moveItem(at: temp, to: dest)
        for old in (try? FileManager.default.contentsOfDirectory(at: Paths.roblox, includingPropertiesForKeys: nil)) ?? []
            where old.lastPathComponent != dest.lastPathComponent {
            try? FileManager.default.removeItem(at: old)
        }
        Log.write("RobloxIpa::fetch", "Downloaded \(release.name)")
        return dest
    }

    public static func importIpa(_ source: URL) throws -> URL {
        let access = source.startAccessingSecurityScopedResource()
        defer { if access { source.stopAccessingSecurityScopedResource() } }
        let zip = try ZipReader(url: source)
        guard zip.entry(named: IpaPatcher.robloxLib) != nil else { throw ZipError("This IPA is not Roblox.") }
        Paths.ensure(Paths.roblox)
        let dest = Paths.roblox.appendingPathComponent(source.lastPathComponent)
        if dest.standardizedFileURL != source.standardizedFileURL {
            for old in (try? FileManager.default.contentsOfDirectory(at: Paths.roblox, includingPropertiesForKeys: nil)) ?? [] {
                try? FileManager.default.removeItem(at: old)
            }
            try FileManager.default.copyItem(at: source, to: dest)
        }
        return dest
    }

    static func download(_ url: URL, expected: Int, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let delegate = DownloadDelegate(expected: expected, progress: progress)
        let downloadSession = URLSession(configuration: .default, delegate: delegate, delegateQueue: nil)
        defer { downloadSession.finishTasksAndInvalidate() }
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                delegate.continuation = continuation
                let task = downloadSession.downloadTask(with: url)
                delegate.task = task
                task.resume()
            }
        } onCancel: {
            delegate.cancel()
        }
    }

    public static func patch(ipa: URL, mods: [ModPack], flags: [FastFlag], settings: AppSettings, presets: PresetState = PresetState(), assets: URL? = nil, status: @escaping PatchStatus) throws -> PatchResult {
        let ident = "RobloxIpa::patch"
        let started = Date()
        let zip = try ZipReader(url: ipa)
        guard zip.entry(named: IpaPatcher.robloxLib) != nil else { throw ZipError("This IPA is not Roblox.") }
        var version = "unknown"
        if let plist = zip.entry(named: IpaPatcher.appRoot + "Info.plist"), let data = try? zip.data(of: plist),
           let info = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
           let v = info["CFBundleShortVersionString"] as? String {
            version = v
        }

        var replacements: [String: Data] = [:]
        replacements.merge(ModPresets.files(state: presets, assets: assets, zip: zip, status: status)) { _, new in new }
        for (i, mod) in mods.enumerated() {
            status("Reading mod \(i + 1) of \(mods.count)", nil)
            for (rel, data) in try IpaPatcher.modFiles(mod.url) {
                replacements[IpaPatcher.modRoot + rel] = data
            }
        }
        let modFileCount = replacements.count

        let applicable = flags.filter(\.appliesOnIos)

        Paths.ensure(Paths.patched.deletingLastPathComponent())
        try IpaPatcher.patch(ipa: ipa, replacements: replacements, output: Paths.patched, status: status)
        if !settings.keepOriginalIpa { try? FileManager.default.removeItem(at: ipa) }
        Log.write(ident, "Patched Roblox \(version) with \(modFileCount) mod files in \(String(format: "%.1f", Date().timeIntervalSince(started))) seconds")
        return PatchResult(output: Paths.patched, modFiles: modFileCount, flagCount: applicable.count, robloxVersion: version)
    }
}

final class DownloadDelegate: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private let expected: Int
    private let progress: @Sendable (Double) -> Void
    private var lastReport = Date.distantPast
    var continuation: CheckedContinuation<URL, Error>?
    var task: URLSessionDownloadTask?

    init(expected: Int, progress: @escaping @Sendable (Double) -> Void) {
        self.expected = expected
        self.progress = progress
    }

    private func finish(_ result: Result<URL, Error>) {
        lock.lock()
        let pending = continuation
        continuation = nil
        lock.unlock()
        pending?.resume(with: result)
    }

    func cancel() {
        task?.cancel()
        finish(.failure(CancellationError()))
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64, totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        let total = totalBytesExpectedToWrite > 0 ? Double(totalBytesExpectedToWrite) : Double(max(expected, 1))
        let now = Date()
        lock.lock()
        let due = now.timeIntervalSince(lastReport) > 0.2
        if due { lastReport = now }
        lock.unlock()
        if due { progress(min(Double(totalBytesWritten) / total, 1)) }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        if let http = downloadTask.response as? HTTPURLResponse, http.statusCode != 200 {
            finish(.failure(ZipError("The download failed with error \(http.statusCode).")))
            return
        }
        let keep = Paths.work.appendingPathComponent(UUID().uuidString + ".ipa")
        do {
            Paths.ensure(Paths.work)
            try FileManager.default.moveItem(at: location, to: keep)
            finish(.success(keep))
        } catch {
            finish(.failure(error))
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let error { finish(.failure(error)) }
    }
}
