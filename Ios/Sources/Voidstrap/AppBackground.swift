import SwiftUI
import UIKit
import VoidstrapCore

@MainActor
final class AppBackground: ObservableObject {
    static let shared = AppBackground()
    static let defaultDim = 55
    nonisolated static let maxBytes = 40 << 20
    nonisolated static let maxEdge: CGFloat = 2560

    @Published private(set) var image: UIImage?
    @Published var enabled = false
    @Published var dim = defaultDim

    private var file: URL { Paths.documents.appendingPathComponent("app_background.jpg") }

    private init() {}

    var active: UIImage? { enabled ? image : nil }

    func sync(_ settings: AppSettings) {
        enabled = settings.background && FileManager.default.fileExists(atPath: file.path)
        dim = max(0, min(90, settings.backgroundDim))
        if enabled, image == nil { image = UIImage(contentsOfFile: file.path) }
        if !enabled { image = nil }
    }

    func install(_ data: Data) async -> Bool {
        guard data.count <= Self.maxBytes else { return false }
        let target = file
        let rendered: UIImage? = await Task.detached(priority: .userInitiated) {
            guard let source = UIImage(data: data), source.size.width > 0, source.size.height > 0 else { return nil }
            let goal = Self.maxEdge
            let longest = max(source.size.width, source.size.height)
            let scale = longest > goal ? goal / longest : 1
            let size = CGSize(width: max(1, (source.size.width * scale).rounded()), height: max(1, (source.size.height * scale).rounded()))
            let format = UIGraphicsImageRendererFormat()
            format.scale = 1
            format.opaque = true
            let image = UIGraphicsImageRenderer(size: size, format: format).image { _ in source.draw(in: CGRect(origin: .zero, size: size)) }
            guard let jpeg = image.jpegData(compressionQuality: 0.92) else { return nil }
            do { try jpeg.write(to: target, options: .atomic) } catch { return nil }
            return image
        }.value
        guard let rendered else { return false }
        image = rendered
        return true
    }

    func remove() {
        try? FileManager.default.removeItem(at: file)
        image = nil
        enabled = false
    }
}
