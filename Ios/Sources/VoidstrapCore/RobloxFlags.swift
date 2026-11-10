import Foundation

public enum RobloxFlags {
    static let bundleId = "com.roblox.robloxmobile"
    static let relative = "Library/Application Support/ClientSettings/ClientAppSettings.json"
    static let markers = ["Library/Preferences/\(bundleId).plist", "Library/Caches/\(bundleId)", "Library/Application Support/ClientSettings", "Library/Caches/Roblox"]
    nonisolated(unsafe) static var lastReport = ""

    static var roots: [URL] {
        var out: [URL] = []
        let path = Paths.documents.standardizedFileURL.path
        if let range = path.range(of: "/Data/Application/", options: .backwards) {
            out.append(URL(fileURLWithPath: String(path[..<range.lowerBound]), isDirectory: true))
        }
        let shared = Paths.shared.standardizedFileURL
        if !out.contains(where: { $0.path == shared.path }) { out.append(shared) }
        return out
    }

    static func plist(_ url: URL) -> [String: Any]? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any]
    }

    static func registered(_ root: URL) -> [URL] {
        let fm = FileManager.default
        let apps = root.appendingPathComponent("Applications", isDirectory: true)
        var out: [URL] = []
        for app in (try? fm.contentsOfDirectory(at: apps, includingPropertiesForKeys: nil)) ?? [] {
            guard (plist(app.appendingPathComponent("Info.plist"))?["CFBundleIdentifier"] as? String)?.lowercased() == bundleId else { continue }
            let info = plist(app.appendingPathComponent("LCAppInfo.plist")) ?? [:]
            var ids: [String] = []
            if let id = info["LCDataUUID"] as? String { ids.append(id) }
            for container in info["LCContainers"] as? [[String: Any]] ?? [] {
                if let id = container["folderName"] as? String { ids.append(id) }
            }
            for id in ids {
                out.append(root.appendingPathComponent("Data/Application/\(id)", isDirectory: true))
            }
        }
        return out
    }

    static func scanned(_ root: URL) -> [URL] {
        let fm = FileManager.default
        let data = root.appendingPathComponent("Data/Application", isDirectory: true)
        let items = (try? fm.contentsOfDirectory(at: data, includingPropertiesForKeys: nil)) ?? []
        return items.filter { dir in markers.contains { fm.fileExists(atPath: dir.appendingPathComponent($0).path) } }
    }

    public static var containers: [URL] {
        guard Paths.isLiveContainer else { return [] }
        var seen = Set<String>()
        var out: [URL] = []
        for root in roots {
            for dir in registered(root) + scanned(root) where seen.insert(dir.standardizedFileURL.path).inserted {
                out.append(dir)
            }
        }
        return out
    }

    @discardableResult
    public static func apply(_ flags: [FastFlag]) -> Int {
        let data = FlagStore.encode(flags.filter(\.appliesOnIos))
        let found = containers
        var written = 0
        for container in found {
            let file = container.appendingPathComponent(relative)
            do {
                Paths.ensure(file.deletingLastPathComponent())
                try data.write(to: file, options: .atomic)
                written += 1
            } catch {
                Log.error("RobloxFlags::apply", error)
            }
        }
        let report = written > 0
            ? "Wrote FastFlags to \(written) Roblox data folders"
            : "No Roblox data folder found, searched \(roots.map(\.path).joined(separator: ", "))"
        if report != lastReport {
            lastReport = report
            Log.write("RobloxFlags::apply", report)
        }
        return written
    }
}
