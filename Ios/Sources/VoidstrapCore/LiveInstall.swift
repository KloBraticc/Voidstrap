import Foundation

public enum LiveInstall {
    public static let folder = "Roblox.app"

    static var root: URL? {
        RobloxFlags.roots.first { FileManager.default.fileExists(atPath: $0.appendingPathComponent("Applications").path) }
    }

    public static var ready: Bool {
        guard let root, let info = RobloxFlags.plist(root.appendingPathComponent("Applications/\(folder)/LCAppInfo.plist")) else { return false }
        return (info["LCPatchRevision"] as? Int ?? 0) > 0
    }

    public static var installed: Bool {
        guard let root else { return false }
        return FileManager.default.fileExists(atPath: root.appendingPathComponent("Applications/\(folder)/Info.plist").path)
    }

    public static func install(ipa: URL, status: PatchStatus) throws -> URL {
        let fm = FileManager.default
        guard Paths.isLiveContainer, let root else { throw ZipError("Voidstrap is not running inside LiveContainer.") }
        let apps = root.appendingPathComponent("Applications", isDirectory: true)
        let target = apps.appendingPathComponent(folder, isDirectory: true)
        let staging = apps.appendingPathComponent(".\(folder).installing", isDirectory: true)
        try? fm.removeItem(at: staging)
        Paths.ensure(staging)
        do {
            let zip = try ZipReader(url: ipa)
            let files = zip.entries.filter { $0.name.hasPrefix("Payload/") }
            guard let first = files.first(where: { $0.name.split(separator: "/").count > 1 && $0.name.split(separator: "/")[1].hasSuffix(".app") }) else {
                throw ZipError("The Roblox IPA has no app inside.")
            }
            let prefix = "Payload/" + first.name.split(separator: "/")[1] + "/"
            var done = 0
            for entry in files where entry.name.hasPrefix(prefix) {
                let rel = String(entry.name.dropFirst(prefix.count))
                guard !rel.isEmpty, IpaPatcher.isSafe(rel) else { continue }
                let dest = staging.appendingPathComponent(rel)
                if entry.isDirectory { Paths.ensure(dest); continue }
                Paths.ensure(dest.deletingLastPathComponent())
                let data = try autoreleasepool { try zip.data(of: entry) }
                let mode = entry.mode
                if mode & 0o170000 == 0o120000, let link = String(data: data, encoding: .utf8) {
                    try fm.createSymbolicLink(atPath: dest.path, withDestinationPath: link)
                } else {
                    try data.write(to: dest)
                    try fm.setAttributes([.posixPermissions: mode & 0o777 == 0 ? 0o644 : mode & 0o777], ofItemAtPath: dest.path)
                }
                done += 1
                if done % 200 == 0 { status("Installing Roblox in LiveContainer", Double(done) / Double(files.count)) }
            }
            var info = RobloxFlags.plist(target.appendingPathComponent("LCAppInfo.plist")) ?? [:]
            if info["LCDataUUID"] == nil { info["LCDataUUID"] = UUID().uuidString }
            info["LCPatchRevision"] = -1
            let data = try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0)
            try data.write(to: staging.appendingPathComponent("LCAppInfo.plist"))
            try? fm.removeItem(at: target)
            try fm.moveItem(at: staging, to: target)
            if let id = info["LCDataUUID"] as? String {
                let home = root.appendingPathComponent("Data/Application/\(id)", isDirectory: true)
                for dir in ["Documents", "Library/Preferences", "Library/Caches", "Library/Application Support/ClientSettings", "tmp"] {
                    Paths.ensure(home.appendingPathComponent(dir, isDirectory: true))
                }
            }
            Log.write("LiveInstall::install", "Installed Roblox into \(target.path), \(done) files")
            return target
        } catch {
            try? fm.removeItem(at: staging)
            throw error
        }
    }
}
