import Foundation

public typealias PatchStatus = @Sendable (String, Double?) -> Void

public enum IpaPatcher {
    public static let appRoot = "Payload/Roblox.app/"
    public static let modRoot = appRoot + "Frameworks/RobloxLib.framework/"
    public static let mainExecutable = appRoot + "Roblox"
    public static let robloxLib = modRoot + "RobloxLib"
    static let gameDirs: Set<String> = ["content", "ExtraContent", "shaders", "ssl", "ios"]

    @discardableResult
    public static func patch(ipa: URL, replacements: [String: Data], output: URL, status: PatchStatus) throws -> Int {
        let source = try ZipReader(url: ipa)
        let order = replacements.keys.sorted()
        let temp = output.appendingPathExtension("partial")
        try? FileManager.default.removeItem(at: temp)
        guard FileManager.default.createFile(atPath: temp.path, contents: nil) else { throw ZipError("Could not create the patched IPA.") }
        let out = try FileHandle(forWritingTo: temp)
        var closed = false
        defer {
            if !closed { try? out.close() }
            try? FileManager.default.removeItem(at: temp)
        }

        var pos = 0
        var central = Data()
        var count = 0
        let sorted = source.entries.sorted { $0.localOffset < $1.localOffset }
        for (i, entry) in sorted.enumerated() where replacements[entry.name] == nil && !entry.name.hasPrefix(appRoot + "_CodeSignature/") {
            let end = i + 1 < sorted.count ? sorted[i + 1].localOffset : source.centralOffset
            guard end >= entry.localOffset else { throw ZipError("The Roblox IPA is damaged.") }
            try source.copy(from: entry.localOffset, count: end - entry.localOffset, to: out)
            var record = entry.central
            record.replaceSubrange(record.startIndex + 42..<record.startIndex + 46, with: le32(pos))
            central.append(record)
            pos += end - entry.localOffset
            count += 1
            if i % 64 == 0 { status("Copying Roblox files", Double(entry.localOffset) / Double(max(source.centralOffset, 1)) * 0.85) }
        }

        for (k, path) in order.enumerated() {
            guard let data = replacements[path] else { continue }
            let name = Data(path.utf8)
            let crc = crc32(data)
            var local = le32(0x0403_4B50) + le16(20) + le16(0x0800) + le16(0)
            local += le16(0) + le16(0x21) + le32(crc)
            local += le32(data.count) + le32(data.count) + le16(name.count) + le16(0) + name
            try out.write(contentsOf: local)
            try out.write(contentsOf: data)
            let mode = path.hasSuffix(".dylib") || path == mainExecutable ? 0o100755 : 0o100644
            var record = le32(0x0201_4B50) + le16(0x031E) + le16(20) + le16(0x0800) + le16(0)
            record += le16(0) + le16(0x21) + le32(crc) + le32(data.count) + le32(data.count)
            record += le16(name.count) + le16(0) + le16(0) + le16(0) + le16(0)
            record += le32(mode << 16) + le32(pos) + name
            central.append(record)
            pos += local.count + data.count
            count += 1
            status("Adding mod files", 0.85 + Double(k + 1) / Double(max(order.count, 1)) * 0.13)
        }

        guard count <= 0xFFFF, pos + central.count <= 0xFFFF_FFFF else { throw ZipError("The patched IPA is too large.") }
        status("Writing IPA index", 0.99)
        try out.write(contentsOf: central)
        let eocd = le32(0x0605_4B50) + le16(0) + le16(0) + le16(count) + le16(count) + le32(central.count) + le32(pos) + le16(0)
        try out.write(contentsOf: eocd)
        try out.synchronize()
        try out.close()
        closed = true
        try? FileManager.default.removeItem(at: output)
        try FileManager.default.moveItem(at: temp, to: output)
        return order.count
    }

    public static func modFiles(_ url: URL) throws -> [(String, Data)] {
        let zip = try ZipReader(url: url)
        var files: [(String, Data)] = []
        for entry in zip.entries where !entry.isDirectory && !entry.name.contains("__MACOSX/") && !entry.name.hasSuffix(".DS_Store") {
            guard isSafe(entry.name) else { throw ZipError("The mod contains an unsafe path: \(entry.name)") }
            files.append((entry.name, try zip.data(of: entry)))
        }
        while true {
            let roots = Set(files.map { $0.0.contains("/") ? String($0.0.split(separator: "/")[0]) : "" })
            guard roots.count == 1, let root = roots.first, !root.isEmpty, !gameDirs.contains(root) else { break }
            files = files.map { (String($0.0.dropFirst(root.count + 1)), $0.1) }
        }
        return files.filter { $0.0 != "info.json" && !$0.0.isEmpty }
    }

    static func isSafe(_ path: String) -> Bool {
        !path.hasPrefix("/") && !path.contains("\\") && !path.split(separator: "/").contains("..")
    }
}
