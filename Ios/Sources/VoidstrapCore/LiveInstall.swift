import Foundation

public enum LiveInstall {
    public static let folder = "Roblox.app"

    static var root: URL? {
        RobloxFlags.roots.first { FileManager.default.fileExists(atPath: $0.appendingPathComponent("Applications").path) }
    }

    public static var opened: Bool {
        guard let root, let info = RobloxFlags.plist(root.appendingPathComponent("Applications/\(folder)/LCAppInfo.plist")),
              let id = info["LCDataUUID"] as? String else { return false }
        let home = root.appendingPathComponent("Data/Application/\(id)")
        return RobloxFlags.markers.dropLast(2).contains { FileManager.default.fileExists(atPath: home.appendingPathComponent($0).path) }
    }

    static func patchExec(_ url: URL) throws {
        let handle = try FileHandle(forUpdating: url)
        defer { try? handle.close() }
        var head = try ZipReader.read(handle, 0, 32 + 72)
        guard head.le32(0) == 0xFEEDFACF else { return }
        func put(_ offset: Int, _ value: UInt64, _ size: Int) {
            for i in 0..<size { head[offset + i] = UInt8(value >> (8 * UInt64(i)) & 0xFF) }
        }
        put(12, 6, 4)
        put(24, UInt64((head.le32(24) | 0x100000) & ~0x200000), 4)
        let vmaddr = UInt64(head.le32(56)) | UInt64(head.le32(60)) << 32
        if head.le32(32) == 0x19, vmaddr == 0 {
            put(56, 0x1_0000_0000 - 0x4000, 8)
            put(64, 0x4000, 8)
        }
        try handle.seek(toOffset: 0)
        try handle.write(contentsOf: head)
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
            if let exec = RobloxFlags.plist(staging.appendingPathComponent("Info.plist"))?["CFBundleExecutable"] as? String {
                try patchExec(staging.appendingPathComponent(exec))
            }
            var info = RobloxFlags.plist(target.appendingPathComponent("LCAppInfo.plist")) ?? [:]
            if info["LCDataUUID"] == nil { info["LCDataUUID"] = UUID().uuidString }
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
