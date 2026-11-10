import Foundation

public enum RobloxFlags {
    static let relative = "Library/Application Support/ClientSettings/ClientAppSettings.json"
    static let markers = ["Library/Preferences/com.roblox.robloxmobile.plist", "Library/Application Support/ClientSettings"]

    public static var containers: [URL] {
        guard Paths.isLiveContainer else { return [] }
        let fm = FileManager.default
        let root = Paths.shared.appendingPathComponent("Data/Application", isDirectory: true)
        let items = (try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
        return items.filter { dir in markers.contains { fm.fileExists(atPath: dir.appendingPathComponent($0).path) } }
    }

    @discardableResult
    public static func apply(_ flags: [FastFlag]) -> Int {
        let data = FlagStore.encode(flags.filter(\.appliesOnIos))
        var written = 0
        for container in containers {
            let file = container.appendingPathComponent(relative)
            do {
                Paths.ensure(file.deletingLastPathComponent())
                try data.write(to: file, options: .atomic)
                written += 1
            } catch {
                Log.error("RobloxFlags::apply", error)
            }
        }
        Log.write("RobloxFlags::apply", "Wrote FastFlags to \(written) Roblox data folders")
        return written
    }
}
