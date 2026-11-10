import SwiftUI
import WebKit
import VoidstrapCore

struct FeaturedBanner: View {
    @EnvironmentObject private var model: AppModel
    @State private var game: FeaturedGame? = Featured.cached()

    var body: some View {
        Group {
            if let game {
                ZStack(alignment: .bottomLeading) {
                    AsyncImage(url: game.thumbnail.flatMap(URL.init(string:))) { image in
                        image.resizable().scaledToFill()
                    } placeholder: { Color.black }
                    if let video = game.videoUrl {
                        MutedVideo(file: video).allowsHitTesting(false)
                    }
                    LinearGradient(colors: [.black.opacity(0.85), .black.opacity(0.4), .clear], startPoint: .bottom, endPoint: .top)
                        .allowsHitTesting(false)
                    VStack(alignment: .leading, spacing: 6) {
                        Text(loc: "Most played now")
                            .font(.system(size: 11, weight: .semibold)).foregroundColor(.white)
                            .padding(.horizontal, 10).padding(.vertical, 3)
                            .background(Capsule().fill(Color.white.opacity(0.15)))
                            .overlay(Capsule().stroke(Color.white.opacity(0.2), lineWidth: 1))
                        Text(verbatim: game.name).font(.system(size: 24, weight: .bold)).foregroundColor(.white).lineLimit(2)
                        HStack(spacing: 12) {
                            Label(game.players.formatted(), systemImage: "person.2.fill")
                            if let rating = game.rating { Label("\(rating)%", systemImage: "hand.thumbsup.fill") }
                        }
                        .font(.system(size: 13)).foregroundColor(.white.opacity(0.87))
                        Button {
                            if let url = game.launchUrl { model.launchUrl(url) }
                        } label: {
                            Label(Loc.t("Play"), systemImage: "play.fill")
                        }
                        .buttonStyle(PrimaryButtonStyle())
                        .padding(.top, 6)
                    }
                    .padding(18)
                }
                .frame(height: 260)
                .frame(maxWidth: .infinity)
                .clipShape(RoundedRectangle(cornerRadius: 22, style: .continuous))
            }
        }
        .task {
            if let fresh = await Featured.load() { game = fresh }
        }
    }
}

struct MutedVideo: UIViewRepresentable {
    let file: URL

    func makeUIView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        config.allowsInlineMediaPlayback = true
        config.mediaTypesRequiringUserActionForPlayback = []
        config.allowsPictureInPictureMediaPlayback = false
        let view = WKWebView(frame: .zero, configuration: config)
        view.isOpaque = false
        view.backgroundColor = .clear
        view.scrollView.backgroundColor = .clear
        view.scrollView.isScrollEnabled = false
        view.isUserInteractionEnabled = false
        load(view)
        return view
    }

    func updateUIView(_ view: WKWebView, context: Context) {
        if view.url?.lastPathComponent != "featured.html" || context.coordinator.file != file { load(view) }
        context.coordinator.file = file
    }

    func makeCoordinator() -> Coordinator { Coordinator(file: file) }

    final class Coordinator { var file: URL; init(file: URL) { self.file = file } }

    private func load(_ view: WKWebView) {
        let folder = file.deletingLastPathComponent()
        let page = folder.appendingPathComponent("featured.html")
        let html = "<!doctype html><html><head><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>html,body{margin:0;height:100%;background:transparent;overflow:hidden}video{width:100%;height:100%;object-fit:cover}</style></head><body><video src=\"\(file.lastPathComponent)\" autoplay muted loop playsinline disablepictureinpicture disableremoteplayback></video><script>var v=document.querySelector('video');v.muted=true;v.volume=0;</script></body></html>"
        try? html.write(to: page, atomically: true, encoding: .utf8)
        view.loadFileURL(page, allowingReadAccessTo: folder)
    }
}
