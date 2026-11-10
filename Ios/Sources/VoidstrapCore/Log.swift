import Foundation

public enum Log {
    private static let lock = NSLock()
    private static let limit = 512 * 1024
    private static let formatter: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }()

    public static func write(_ ident: String, _ message: String) {
        lock.lock()
        defer { lock.unlock() }
        let line = "\(formatter.string(from: Date())) [\(ident)] \(message)\n"
        guard let data = line.data(using: .utf8) else { return }
        let url = Paths.log
        let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int) ?? 0
        if size > limit, let existing = try? Data(contentsOf: url) {
            try? existing.suffix(limit / 2).write(to: url, options: .atomic)
        }
        if let handle = try? FileHandle(forWritingTo: url) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: data)
        } else {
            try? data.write(to: url, options: .atomic)
        }
    }

    public static func error(_ ident: String, _ error: Error) {
        write(ident, "\(type(of: error)): \(error.localizedDescription)")
    }

    public static func read() -> String {
        lock.lock()
        defer { lock.unlock() }
        return (try? String(contentsOf: Paths.log, encoding: .utf8)) ?? ""
    }

    public static func clear() {
        lock.lock()
        defer { lock.unlock() }
        try? FileManager.default.removeItem(at: Paths.log)
    }
}
