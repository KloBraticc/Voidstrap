import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public actor Translator {
    public static let shared = Translator()

    public static let codes = [
        "af", "sq", "am", "ar", "hy", "az", "eu", "be", "bn", "bs", "bg", "ca", "ceb", "ny", "zh-CN",
        "zh-TW", "co", "hr", "cs", "da", "nl", "en", "eo", "et", "tl", "fi", "fr", "fy", "gl", "ka",
        "de", "el", "gu", "ht", "ha", "haw", "iw", "hi", "hmn", "hu", "is", "ig", "id", "ga", "it",
        "ja", "jw", "kn", "kk", "km", "ko", "ku", "ky", "lo", "la", "lv", "lt", "lb", "mk", "mg", "ms",
        "ml", "mt", "mi", "mr", "mn", "my", "ne", "no", "or", "ps", "fa", "pl", "pt", "pa", "ro", "ru",
        "rw", "sm", "gd", "sr", "st", "sn", "sd", "si", "sk", "sl", "so", "es", "su", "sw", "sv", "tg",
        "ta", "te", "tt", "th", "tr", "tk", "uk", "ur", "ug", "uz", "vi", "cy", "xh", "yi", "yo", "zu"
    ]

    public static let names = [
        "Afrikaans", "Albanian", "Amharic", "Arabic", "Armenian", "Azerbaijani", "Basque", "Belarusian",
        "Bengali", "Bosnian", "Bulgarian", "Catalan", "Cebuano", "Chichewa", "Chinese (Simplified)",
        "Chinese (Traditional)", "Corsican", "Croatian", "Czech", "Danish", "Dutch", "English",
        "Esperanto", "Estonian", "Filipino", "Finnish", "French", "Frisian", "Galician", "Georgian",
        "German", "Greek", "Gujarati", "Haitian Creole", "Hausa", "Hawaiian", "Hebrew", "Hindi",
        "Hmong", "Hungarian", "Icelandic", "Igbo", "Indonesian", "Irish", "Italian", "Japanese",
        "Javanese", "Kannada", "Kazakh", "Khmer", "Korean", "Kurdish (Kurmanji)", "Kyrgyz", "Lao",
        "Latin", "Latvian", "Lithuanian", "Luxembourgish", "Macedonian", "Malagasy", "Malay",
        "Malayalam", "Maltese", "Maori", "Marathi", "Mongolian", "Myanmar (Burmese)", "Nepali",
        "Norwegian", "Odia", "Pashto", "Persian", "Polish", "Portuguese", "Punjabi", "Romanian",
        "Russian", "Kinyarwanda", "Samoan", "Scots Gaelic", "Serbian", "Sesotho", "Shona", "Sindhi",
        "Sinhala", "Slovak", "Slovenian", "Somali", "Spanish", "Sundanese", "Swahili", "Swedish",
        "Tajik", "Tamil", "Telugu", "Tatar", "Thai", "Turkish", "Turkmen", "Ukrainian", "Urdu",
        "Uyghur", "Uzbek", "Vietnamese", "Welsh", "Xhosa", "Yiddish", "Yoruba", "Zulu"
    ]

    static let endpoint = "https://translate.googleapis.com/translate_a/t?client=gtx&sl=auto&tl="
    static let protected = ["Roblox Studio", "Voidstrap", "Roblox", "Discord", "Bloxstrap", "Chevstrap", "FastFlags", "FastFlag", "LiveContainer"]
    static let cacheFile = Paths.documents.appendingPathComponent("Cache/Translations.json")
    static let maxBatch = 64

    private var memory: [String: String] = [:]
    private var inFlight: Set<String> = []
    private var offlineUntil = Date.distantPast

    public init() {}

    public static func skip(_ text: String) -> Bool {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if t.isEmpty || protected.contains(where: { $0.caseInsensitiveCompare(t) == .orderedSame }) { return true }
        let lower = t.lowercased()
        if ["://", "www.", ".com", ".net", ".org", ".gg"].contains(where: { lower.contains($0) }) { return true }
        return !t.contains(where: { $0.isLetter })
    }

    public func load() -> [String: String] {
        if memory.isEmpty, let data = try? Data(contentsOf: Self.cacheFile),
           let object = try? JSONSerialization.jsonObject(with: data) as? [String: String] {
            memory = object
        }
        return memory
    }

    private func save() {
        Paths.ensure(Self.cacheFile.deletingLastPathComponent())
        if let data = try? JSONSerialization.data(withJSONObject: memory, options: [.sortedKeys]) {
            try? data.write(to: Self.cacheFile, options: .atomic)
        }
    }

    public static func key(_ lang: String, _ text: String) -> String { "\(lang)|\(text)" }

    static func token(_ i: Int) -> String { "VSTK\(i)Z" }

    static func mask(_ text: String) -> String {
        var out = text
        for (i, p) in protected.enumerated() { out = out.replacingOccurrences(of: p, with: token(i), options: .caseInsensitive) }
        return out
    }

    static func unmask(_ translated: String, _ source: String) -> String {
        var out = translated
        for (i, p) in protected.enumerated() {
            guard let range = source.range(of: p, options: .caseInsensitive) else { continue }
            out = out.replacingOccurrences(of: token(i), with: String(source[range]), options: .caseInsensitive)
        }
        return out
    }

    public func fetch(lang: String, texts: [String]) async -> [String: String] {
        _ = load()
        var pending: [String] = []
        for t in texts {
            let k = Self.key(lang, t)
            if Self.skip(t) || memory[k] != nil || pending.contains(t) || inFlight.contains(k) { continue }
            inFlight.insert(k)
            pending.append(t)
        }
        guard !pending.isEmpty else { return memory }
        var any = false
        var start = 0
        while start < pending.count {
            let chunk = Array(pending[start..<min(start + Self.maxBatch, pending.count)])
            start += Self.maxBatch
            if await request(lang, chunk) { any = true }
        }
        for t in pending { inFlight.remove(Self.key(lang, t)) }
        if any { save() }
        return memory
    }

    private func request(_ lang: String, _ chunk: [String]) async -> Bool {
        for _ in 0..<4 {
            guard Date() >= offlineUntil else { return false }
            let wait = await send(lang, chunk)
            if wait == 0 { return true }
            if wait < 0 { return false }
            try? await Task.sleep(nanoseconds: UInt64(wait) * 1_000_000)
        }
        return false
    }

    private func send(_ lang: String, _ chunk: [String]) async -> Int {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        let form = chunk.map { "q=" + (Self.mask($0).addingPercentEncoding(withAllowedCharacters: allowed) ?? "") }.joined(separator: "&")
        guard let url = URL(string: Self.endpoint + (lang.addingPercentEncoding(withAllowedCharacters: allowed) ?? lang)) else { return -1 }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.httpBody = Data(form.utf8)
        request.timeoutInterval = 15
        request.setValue("application/x-www-form-urlencoded; charset=utf-8", forHTTPHeaderField: "Content-Type")
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            if code == 429 || code / 100 == 5 { return 700 }
            guard code / 100 == 2 else {
                Log.write("Translator::send", "Translation endpoint returned \(code)")
                return -1
            }
            return parse(lang, chunk, data) ? 0 : -1
        } catch {
            offlineUntil = Date().addingTimeInterval(30)
            return -1
        }
    }

    private func parse(_ lang: String, _ chunk: [String], _ data: Data) -> Bool {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [Any] else { return false }
        func value(_ node: Any?) -> String? {
            if let s = node as? String { return s }
            if let a = node as? [Any] { return a.first as? String }
            return nil
        }
        if chunk.count == 1, let one = value(root.first) {
            memory[Self.key(lang, chunk[0])] = Self.unmask(one, chunk[0])
            return true
        }
        guard root.count == chunk.count else { return false }
        var any = false
        for (i, src) in chunk.enumerated() {
            if let v = value(root[i]) {
                memory[Self.key(lang, src)] = Self.unmask(v, src)
                any = true
            }
        }
        return any
    }
}
