import Foundation

enum JSONFile {
    static func load<T: Decodable>(_ type: T.Type, from url: URL) -> T? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        do {
            return try JSONDecoder().decode(type, from: data)
        } catch {
            Log.write("JSONFile::load", "Could not read \(url.lastPathComponent): \(error.localizedDescription)")
            return nil
        }
    }

    static func save<T: Encodable>(_ value: T, to url: URL) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        do {
            Paths.ensure(url.deletingLastPathComponent())
            try encoder.encode(value).write(to: url, options: .atomic)
        } catch {
            Log.write("JSONFile::save", "Could not save \(url.lastPathComponent): \(error.localizedDescription)")
        }
    }
}

public enum AppTheme: String, Codable, CaseIterable, Identifiable, Sendable {
    case system, dark, light, voidstrap, ultra_gray, berry, blue, cyan, green, orange, pink, purple, red, yellow
    public var id: String { rawValue }
    public var named: NamedTheme? { NamedTheme.all.first { $0.id == rawValue } }
    public var title: String {
        switch self {
        case .system: return "Follow system"
        case .dark: return "Dark"
        case .light: return "Light"
        default: return named?.title ?? rawValue
        }
    }
}

public struct AppSettings: Codable, Equatable, Sendable {
    public var theme: AppTheme = .system
    public var githubToken: String = ""
    public var keepOriginalIpa: Bool = true
    public var language: String = "en"

    public init() {}

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        theme = (try? c.decode(AppTheme.self, forKey: .theme)) ?? .system
        githubToken = (try? c.decode(String.self, forKey: .githubToken)) ?? ""
        keepOriginalIpa = (try? c.decode(Bool.self, forKey: .keepOriginalIpa)) ?? true
        language = (try? c.decode(String.self, forKey: .language)) ?? "en"
    }

    public static func load() -> AppSettings { JSONFile.load(AppSettings.self, from: Paths.settings) ?? AppSettings() }
    public func save() { JSONFile.save(self, to: Paths.settings) }
}

public struct ModPack: Codable, Identifiable, Equatable, Sendable {
    public var id: UUID
    public var name: String
    public var file: String
    public var enabled: Bool
    public var fileCount: Int
    public var added: Date

    public var url: URL { Paths.mods.appendingPathComponent(file) }
}

public enum ModStore {
    public static func load() -> [ModPack] {
        (JSONFile.load([ModPack].self, from: Paths.modIndex) ?? []).filter { FileManager.default.fileExists(atPath: $0.url.path) }
    }

    public static func save(_ mods: [ModPack]) { JSONFile.save(mods, to: Paths.modIndex) }

    public static func importZip(_ source: URL) throws -> ModPack {
        let access = source.startAccessingSecurityScopedResource()
        defer { if access { source.stopAccessingSecurityScopedResource() } }
        Paths.ensure(Paths.mods)
        let id = UUID()
        let file = id.uuidString + ".zip"
        let dest = Paths.mods.appendingPathComponent(file)
        try FileManager.default.copyItem(at: source, to: dest)
        do {
            let files = try IpaPatcher.modFiles(dest)
            guard !files.isEmpty else { throw ZipError("This zip has no mod files.") }
            return ModPack(id: id, name: source.deletingPathExtension().lastPathComponent, file: file, enabled: true, fileCount: files.count, added: Date())
        } catch {
            try? FileManager.default.removeItem(at: dest)
            throw error
        }
    }

    public static func delete(_ mod: ModPack) {
        try? FileManager.default.removeItem(at: mod.url)
    }
}

public enum FlagValueKind {
    case bool, int, string

    public static func of(_ name: String) -> FlagValueKind {
        for prefix in ["DFFlag", "SFFlag", "PFFlag", "FFlag"] where name.hasPrefix(prefix) { return .bool }
        for prefix in ["DFInt", "SFInt", "PFInt", "FInt", "DFLog", "FLog"] where name.hasPrefix(prefix) { return .int }
        return .string
    }
}

public struct FastFlag: Identifiable, Equatable, Sendable {
    public var id: String { name }
    public var name: String
    public var value: String

    public init(name: String, value: String) {
        self.name = name
        self.value = value
    }

    public var kind: FlagValueKind { FlagValueKind.of(name) }

    public var appliesOnIos: Bool { kind != .string }
}

public enum FlagStore {
    public static func load() -> [FastFlag] {
        guard let data = try? Data(contentsOf: Paths.flags), let flags = try? parse(data) else { return [] }
        return flags
    }

    public static func parse(_ data: Data) throws -> [FastFlag] {
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw ZipError("FastFlags must be a JSON object.")
        }
        return object.compactMap { key, value -> FastFlag? in
            let name = key.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !name.isEmpty else { return nil }
            if let n = value as? NSNumber {
                if String(cString: n.objCType) == "c" { return FastFlag(name: name, value: n.boolValue ? "True" : "False") }
                return FastFlag(name: name, value: n.stringValue)
            }
            return FastFlag(name: name, value: "\(value)")
        }.sorted { $0.name.lowercased() < $1.name.lowercased() }
    }

    public static func encode(_ flags: [FastFlag]) -> Data {
        var object: [String: String] = [:]
        for flag in flags where !flag.name.isEmpty { object[flag.name] = flag.value }
        return (try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])) ?? Data("{}".utf8)
    }

    public static func save(_ flags: [FastFlag]) {
        do {
            Paths.ensure(Paths.flags.deletingLastPathComponent())
            try encode(flags).write(to: Paths.flags, options: .atomic)
            RobloxFlags.apply(flags)
        } catch {
            Log.error("FlagStore::save", error)
        }
    }

    public static func validate(name: String, value: String) -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        if trimmed.isEmpty { return "Enter a FastFlag name." }
        if trimmed.contains(where: { !$0.isLetter && !$0.isNumber && $0 != "_" }) { return "FastFlag names use only letters, numbers and underscores." }
        switch FlagValueKind.of(trimmed) {
        case .bool:
            if !["true", "false"].contains(value.lowercased()) { return "This FastFlag needs True or False." }
        case .int:
            if Int32(value) == nil { return "This FastFlag needs a whole number." }
        case .string:
            break
        }
        return nil
    }
}
