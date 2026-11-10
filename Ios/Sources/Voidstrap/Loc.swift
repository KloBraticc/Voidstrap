import SwiftUI
import VoidstrapCore

@MainActor
final class Loc: ObservableObject {
    static let shared = Loc()

    @Published private(set) var revision = 0
    private(set) var language = "en"
    private var table: [String: String] = [:]
    private var missing: [String] = []
    private var scheduled = false

    private init() {}

    var isActive: Bool { language != "en" }

    func setLanguage(_ code: String) {
        language = Translator.codes.contains(code) ? code : "en"
        Task {
            table = await Translator.shared.load()
            revision += 1
        }
    }

    static func t(_ text: String) -> String { shared.lookup(text) }

    func lookup(_ text: String) -> String {
        guard isActive, !Translator.skip(text) else { return text }
        if let hit = table[Translator.key(language, text)] { return hit }
        if !missing.contains(text) { missing.append(text) }
        schedule()
        return text
    }

    private func schedule() {
        guard !scheduled else { return }
        scheduled = true
        Task {
            try? await Task.sleep(nanoseconds: 250_000_000)
            let lang = language
            let batch = missing
            missing.removeAll()
            scheduled = false
            let result = await Translator.shared.fetch(lang: lang, texts: batch)
            let before = table.count
            table = result
            if table.count != before { revision += 1 }
        }
    }
}

extension Text {
    @MainActor init(loc text: String) { self.init(verbatim: Loc.t(text)) }
}
