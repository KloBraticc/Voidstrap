import Foundation

public enum Paths {
    public static let documents: URL = {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
    }()

    public static let shared: URL = {
        let guest = documents.path
        guard let range = guest.range(of: "/Documents") else { return documents }
        return URL(fileURLWithPath: String(guest[..<range.upperBound]), isDirectory: true)
    }()

    public static var isLiveContainer: Bool {
        shared.standardizedFileURL.path != documents.standardizedFileURL.path
    }

    public static let flags = shared.appendingPathComponent("fflags.json")

    public static let mods = documents.appendingPathComponent("Mods", isDirectory: true)
    public static let modIndex = documents.appendingPathComponent("mods.json")
    public static let roblox = documents.appendingPathComponent("Roblox", isDirectory: true)
    public static let patched = documents.appendingPathComponent("Roblox-Voidstrap.ipa")
    public static let settings = documents.appendingPathComponent("settings.json")
    public static let library = documents.appendingPathComponent("library.json")
    public static let log = documents.appendingPathComponent("voidstrap.log")
    public static let work = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
        .appendingPathComponent("voidstrap-work", isDirectory: true)

    public static func ensure(_ directory: URL) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }
}
