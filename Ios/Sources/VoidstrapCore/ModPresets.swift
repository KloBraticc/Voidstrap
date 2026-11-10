import Foundation
import zlib
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif
#if canImport(CryptoKit)
import CryptoKit
#endif

public struct PresetState: Codable, Equatable, Sendable {
    public var cursor = 0
    public var oldAvatar = false
    public var oldDeath = false
    public var deathSound = false
    public var emoji = 0
    public var sky: String?
    public var font = false
    public var hideGui = false
    public var shiftLock = false

    public init() {}

    public static func load() -> PresetState { JSONFile.load(PresetState.self, from: ModPresets.stateFile) ?? PresetState() }
    public func save() { JSONFile.save(self, to: ModPresets.stateFile) }
}

public enum ModPresets {
    public static let folder = Paths.documents.appendingPathComponent("Presets", isDirectory: true)
    static let stateFile = folder.appendingPathComponent("presets.json")
    public static let deathFile = folder.appendingPathComponent("Death.ogg")
    public static let fontFile = folder.appendingPathComponent("CustomFont.ttf")
    public static let customCursor = folder.appendingPathComponent("CustomCursor.png")
    public static let shiftLockFile = folder.appendingPathComponent("ShiftLock.png")
    static let skyFolder = folder.appendingPathComponent("Skyboxes", isDirectory: true)

    static let death = "content/sounds/oof.ogg"
    static let avatar = "ExtraContent/places/Mobile.rbxl"
    static let emojiPath = "content/fonts/TwemojiMozilla.ttf"
    static let fontPath = "content/fonts/CustomFont.ttf"
    static let fontAsset = "rbxasset://fonts/CustomFont.ttf"
    static let familyDir = "content/fonts/families/"
    static let cursorDir = "content/textures/Cursors/KeyboardMouse/"
    static let shiftLockPath = "content/textures/MouseLockedCursor.png"
    static let slotFiles = ["ArrowCursor.png", "ArrowFarCursor.png", "IBeamCursor.png", "ArrowCursorDecalDrag.png"]
    public static let skyFaces = ["sky512_bk.tex", "sky512_dn.tex", "sky512_ft.tex", "sky512_lf.tex", "sky512_rt.tex", "sky512_up.tex"]
    static let blankRoots = ["content/textures/ui/", "ExtraContent/textures/ui/ImageSet/", "ExtraContent/textures/ui/LuaChat/",
                             "ExtraContent/LuaPackages/Packages/_Index/FoundationImages/FoundationImages/SpriteSheets/"]
    static let blankKeep = ["content/textures/ui/Controls/", "content/textures/ui/Input/"]
    static let blankFontDir = "ExtraContent/LuaPackages/Packages/_Index/BuilderIcons/BuilderIcons/Font/"

    public static let cursorStyles: [(id: Int, title: String, folder: String?)] = [
        (0, "Default", nil), (9, "Voidstrap", "BibataModernIce"), (4, "Stoofs", "StoofsCursor"), (3, "Dot", "DotCursor"),
        (2, "Clean", "CleanCursor"), (1, "FPS", "FPSCursor"), (5, "From 2006", "From2006"), (6, "From 2013", "From2013"),
        (7, "White dot", "WhiteDotCursor"), (8, "Very small white dot", "VerySmallWhiteDot"), (10, "Custom", nil)
    ]

    public static let emojiTitles = ["Default", "Catmoji", "Windows 11", "Windows 10", "Windows 8"]
    static let emojiFiles = ["", "Catmoji.ttf", "Win1122H2SegoeUIEmoji.ttf", "Win10April2018SegoeUIEmoji.ttf", "Win8.1SegoeUIEmoji.ttf"]
    static let emojiHashes = ["", "58D781FF4800AB10144A6FC0BB30479881F5048ECD57196148C9AB791FDAB622",
                              "4E3CEC7D1995B6D74102C0B4669E4507AC35CBF9A9830A93AC14C6E40DFE36A9",
                              "7C0244DD8EEB7C6BDECDFC3F9E59833527FC18A66D0295CE47339069692A2B4F",
                              "86BE288EED6561684BE645F671409210C914815E3833A0FC3B587CBF64C03928"]
    static let emojiSizes = [0, 1335828, 2838884, 2072388, 676304]
    static let emojiUrl = "https://github.com/BloxstrapLabs/rbxcustom-fontemojis/releases/download/my-phone-is-78-percent/"
    static let skyApi = "https://api.github.com/repos/KloBraticc/SkyboxPackV2/contents"
    static let skyRaw = "https://raw.githubusercontent.com/KloBraticc/SkyboxPackV2/main/"

    static func emojiFile(_ i: Int) -> URL { folder.appendingPathComponent("Emoji-\(i).ttf") }

    public static func importFile(_ source: URL, to dest: URL, maxSize: Int) throws {
        let access = source.startAccessingSecurityScopedResource()
        defer { if access { source.stopAccessingSecurityScopedResource() } }
        let data = try Data(contentsOf: source)
        guard !data.isEmpty, data.count <= maxSize else { throw ZipError("That file is empty or too large.") }
        Paths.ensure(folder)
        try data.write(to: dest, options: .atomic)
    }

    static func sha256(_ data: Data) -> String {
        #if canImport(CryptoKit)
        return SHA256.hash(data: data).map { String(format: "%02X", $0) }.joined()
        #else
        return ""
        #endif
    }

    public static func downloadEmoji(_ i: Int) async throws {
        guard i > 0, i < emojiFiles.count else { return }
        let dest = emojiFile(i)
        if FileManager.default.fileExists(atPath: dest.path) { return }
        guard let url = URL(string: emojiUrl + emojiFiles[i]) else { return }
        let (data, response) = try await URLSession.shared.data(from: url)
        guard (response as? HTTPURLResponse)?.statusCode == 200, data.count == emojiSizes[i] else { throw ZipError("The emoji font could not be downloaded.") }
        #if canImport(CryptoKit)
        guard sha256(data) == emojiHashes[i] else { throw ZipError("The emoji font did not match its checksum.") }
        #endif
        Paths.ensure(folder)
        try data.write(to: dest, options: .atomic)
    }

    public static func skyPacks() async -> [String] {
        var request = URLRequest(url: URL(string: skyApi)!)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.timeoutInterval = 20
        guard let (data, _) = try? await URLSession.shared.data(for: request),
              let items = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
        return items.compactMap { $0["type"] as? String == "dir" ? $0["name"] as? String : nil }
            .filter { !$0.hasPrefix(".") }.sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
    }

    public static func downloadSky(_ name: String) async throws {
        guard !name.isEmpty, !name.contains("/"), name != ".", name != ".." else { throw ZipError("The selected skybox name is invalid.") }
        let dir = skyFolder.appendingPathComponent(name, isDirectory: true)
        if skyFaces.allSatisfy({ FileManager.default.fileExists(atPath: dir.appendingPathComponent($0).path) }) { return }
        let staging = skyFolder.appendingPathComponent(name + ".new", isDirectory: true)
        try? FileManager.default.removeItem(at: staging)
        Paths.ensure(staging)
        defer { try? FileManager.default.removeItem(at: staging) }
        for face in skyFaces {
            guard let encoded = name.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed),
                  let url = URL(string: skyRaw + encoded + "/" + face) else { throw ZipError("The skybox could not be downloaded.") }
            let (data, response) = try await URLSession.shared.data(from: url)
            guard (response as? HTTPURLResponse)?.statusCode == 200, !data.isEmpty else { throw ZipError("The skybox could not be downloaded.") }
            try data.write(to: staging.appendingPathComponent(face))
        }
        try? FileManager.default.removeItem(at: dir)
        try FileManager.default.moveItem(at: staging, to: dir)
    }

    static func read(_ url: URL?) -> Data? { url.flatMap { try? Data(contentsOf: $0) } }

    public static func files(state: PresetState, assets: URL?, zip: ZipReader, status: PatchStatus) -> [String: Data] {
        let ident = "ModPresets::files"
        var out: [String: Data] = [:]
        let names = Set(zip.entries.map(\.name))
        func place(_ rel: String, _ data: Data?) {
            guard let data else {
                Log.write(ident, "Preset file missing for \(rel)")
                return
            }
            out[resolve(rel, names)] = data
        }
        if state.hideGui { blankInterface(zip, assets, &out, status) }
        if state.oldAvatar { place(avatar, read(assets?.appendingPathComponent("OldAvatarBackground.rbxl"))) }
        if state.deathSound { place(death, read(deathFile)) } else if state.oldDeath { place(death, read(assets?.appendingPathComponent("Sounds/OldDeath.ogg"))) }
        if state.emoji > 0 { place(emojiPath, read(emojiFile(state.emoji))) }
        if let sky = state.sky {
            let dir = skyFolder.appendingPathComponent(sky, isDirectory: true)
            let target = zip.entries.first { $0.name.hasSuffix("/" + skyFaces[0]) }.map { String($0.name.dropLast(skyFaces[0].count)) }
            for face in skyFaces {
                if let target, let data = read(dir.appendingPathComponent(face)) { out[target + face] = data }
            }
            if target == nil { Log.write(ident, "No skybox folder found in this Roblox build") }
        }
        if let style = cursorStyles.first(where: { $0.id == state.cursor }), style.id != 0 {
            for (i, slot) in slotFiles.enumerated() {
                let data = style.id == 10 ? (i < 3 ? read(customCursor) : nil) : read(style.folder.flatMap { assets?.appendingPathComponent("Cursor/\($0)/\(slot)") })
                if let data { out[resolve(cursorDir + slot, names)] = data }
            }
        }
        if state.shiftLock { place(shiftLockPath, read(shiftLockFile)) }
        if state.font, let font = read(fontFile) {
            place(fontPath, font)
            for entry in zip.entries where entry.name.hasSuffix(".json") && entry.name.contains("/" + familyDir) {
                let file = String(entry.name[entry.name.range(of: "/" + familyDir)!.upperBound...])
                guard !file.contains("/"), let raw = try? zip.data(of: entry),
                      var family = try? JSONSerialization.jsonObject(with: raw) as? [String: Any],
                      let faces = family["faces"] as? [[String: Any]], !faces.isEmpty else { continue }
                family["faces"] = faces.map { face -> [String: Any] in
                    var f = face
                    f["assetId"] = fontAsset
                    return f
                }
                if let data = try? JSONSerialization.data(withJSONObject: family, options: [.prettyPrinted, .sortedKeys]) { out[entry.name] = data }
            }
        }
        Log.write(ident, "Preset files prepared: \(out.count)")
        return out
    }

    static func resolve(_ rel: String, _ names: Set<String>) -> String {
        let direct = IpaPatcher.modRoot + rel
        if names.contains(direct) { return direct }
        let parts = rel.split(separator: "/")
        let tail = "/" + parts.suffix(2).joined(separator: "/")
        if let hit = names.first(where: { $0.hasSuffix(tail) }) { return hit }
        return direct
    }

    static func blankInterface(_ zip: ZipReader, _ assets: URL?, _ out: inout [String: Data], _ status: PatchStatus) {
        status("Hiding the Roblox interface", nil)
        let font = read(assets?.appendingPathComponent("BlankIcons.ttf"))
        var blanks: [String: Data] = [:]
        for entry in zip.entries where entry.name.hasPrefix(IpaPatcher.modRoot) {
            let rel = String(entry.name.dropFirst(IpaPatcher.modRoot.count))
            let lower = rel.lowercased()
            if rel.hasPrefix(blankFontDir) {
                if (lower.hasSuffix(".ttf") || lower.hasSuffix(".otf")), let font { out[entry.name] = font }
                continue
            }
            guard lower.hasSuffix(".png"), blankRoots.contains(where: rel.hasPrefix), !blankKeep.contains(where: rel.hasPrefix),
                  let data = try? zip.data(of: entry), let size = pngSize(data), size.0 <= 4096, size.1 <= 4096 else { continue }
            let key = "\(size.0)x\(size.1)"
            if blanks[key] == nil { blanks[key] = blankPng(size.0, size.1) }
            out[entry.name] = blanks[key]
        }
    }

    static func pngSize(_ d: Data) -> (Int, Int)? {
        guard d.count >= 24, d.prefix(8) == Data([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) else { return nil }
        func be(_ o: Int) -> Int { d[d.startIndex + o..<d.startIndex + o + 4].reduce(0) { $0 << 8 | Int($1) } }
        let w = be(16), h = be(20)
        return w > 0 && h > 0 ? (w, h) : nil
    }

    static func blankPng(_ w: Int, _ h: Int) -> Data {
        func be32(_ v: UInt32) -> Data { withUnsafeBytes(of: v.bigEndian) { Data($0) } }
        func chunk(_ type: String, _ body: Data) -> Data {
            let typed = Data(type.utf8) + body
            let crc = typed.withUnsafeBytes { UInt32(crc32(0, $0.bindMemory(to: Bytef.self).baseAddress, uInt($0.count))) }
            return be32(UInt32(body.count)) + typed + be32(crc)
        }
        let raw = Data(count: (w * 4 + 1) * h)
        var size = compressBound(uLong(raw.count))
        var compressed = Data(count: Int(size))
        _ = compressed.withUnsafeMutableBytes { dst in
            raw.withUnsafeBytes { src in
                compress2(dst.bindMemory(to: Bytef.self).baseAddress, &size, src.bindMemory(to: Bytef.self).baseAddress, uLong(raw.count), 9)
            }
        }
        compressed.count = Int(size)
        let header = be32(UInt32(w)) + be32(UInt32(h)) + Data([8, 6, 0, 0, 0])
        return Data([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) + chunk("IHDR", header) + chunk("IDAT", compressed) + chunk("IEND", Data())
    }
}
