import SwiftUI
import UIKit
import VoidstrapCore

struct FlagPresetsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var profile = 0
    @State private var highFps = false

    private typealias K = FlagKeys

    private func num(_ key: String) -> Int? { model.flag(key).flatMap { Int($0.trimmingCharacters(in: .whitespaces)) } }
    private func on(_ key: String) -> Bool { model.flag(key)?.lowercased() == "true" }
    private func has(_ key: String) -> Bool { model.flag(key) != nil }
    private func put(_ key: String, _ value: Int?) { model.putFlag(key, value.map(String.init)) }
    private func put(_ key: String, _ value: Bool?) { model.putFlag(key, value.map { $0 ? "True" : "False" }) }

    private func pick(_ options: [Int], _ key: String) -> Int {
        guard has(key) else { return 0 }
        return options.indices.dropFirst().first { options[$0] == num(key) } ?? -1
    }

    private func setFps(_ fps: Int) {
        put(K.targetFps, fps > 0 ? fps : nil)
        put(K.limit240, fps > 240 ? false : nil)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            SectionTitle(text: "Graphics")
            Card {
                choice("photo", "Texture quality", "Sharper textures use more memory", ["Automatic", "Lowest", "Low", "Medium", "High"],
                       !has(K.texture) ? 0 : (num(K.texture).map { (0...3).contains($0) ? $0 + 1 : -1 } ?? -1), K.texture) { i in
                    put(K.texture, i == 0 ? nil : i - 1)
                    put(K.textureOn, i == 0 ? nil : true)
                }
                RowDivider()
                choice("square.on.square.dashed", "Anti aliasing", "Smooths jagged edges", ["Automatic", "1x", "2x", "4x", "8x"], pick(K.msaaValues, K.msaa), K.msaa) { i in
                    put(K.msaa, i == 0 ? nil : K.msaaValues[i])
                    put(K.msaaOptional, Int?.none)
                }
                RowDivider()
                toggle("cloud", "Gray sky", "Replaces the sky with plain gray", on(K.sky)) { put(K.sky, $0 ? true : nil) }
                RowDivider()
                toggle("arrow.up.left.and.arrow.down.right", "Full resolution", "Ignore display scaling for a sharper picture", on(K.dpi)) { put(K.dpi, $0 ? true : nil) }
                RowDivider()
                choice("rectangle.compress.vertical", "Render resolution", "Fixed resolution Roblox renders at",
                       ["Automatic", "144p", "240p", "360p", "480p", "720p", "1080p", "1440p", "2160p", "4320p"], K.resolutionIndex(num(K.resolution)), nil) { i in
                    put(K.resolution, i == 0 ? nil : K.kiloPixels(K.resolutions[i]))
                }
                RowDivider()
                toggle("shadow", "Disable shadows", "Turns off Roblox shadows", on(K.shadows[0]) && has(K.shadows[1])) {
                    put(K.shadows[0], $0 ? true : nil)
                    put(K.shadows[1], $0 ? -1 : nil)
                }
            }

            SectionTitle(text: "Performance")
            Card {
                choice("speedometer", "Frame rate limit", "Above 60 needs a ProMotion display",
                       ["Default", "30", "60", "90", "120", "144", "165", "240", "360", "480", "Unlimited"], pick(K.fpsValues, K.targetFps), K.targetFps) { i in
                    setFps(K.fpsValues[i])
                    if K.fpsValues[i] > 240 { highFps = true }
                }
                RowDivider()
                toggle("gauge", "FPS counter", "Shows frame rate in the corner", on(K.showFps)) { put(K.showFps, $0 ? true : nil) }
            }

            SectionTitle(text: "World detail")
            Card {
                toggle("cube", "Mesh detail", "How detailed distant objects look", has(K.lod[0])) { checked in
                    for (i, key) in K.lod.enumerated() { put(key, checked ? max(0, min(3, K.meshRange.upperBound - i)) : nil) }
                }
                if has(K.lod[0]) {
                    slider(K.meshRange, num(K.lod[0]) ?? K.meshRange.upperBound) { v in
                        for (i, key) in K.lod.enumerated() { put(key, max(0, min(3, v - i))) }
                    }
                }
                RowDivider()
                toggle("dial.medium", "Graphics quality override", "Locks the Roblox graphics level", has(K.frm)) { put(K.frm, $0 ? K.frmRange.upperBound : nil) }
                if has(K.frm) {
                    slider(K.frmRange, num(K.frm) ?? K.frmRange.upperBound) { put(K.frm, $0) }
                }
                RowDivider()
                choice("wind", "Grass movement", "How much grass sways",
                       ["No movement", "Minimal movement", "Medium movement", "High movement", "Ultra movement", "Default movement"],
                       !has(K.grassMotion) ? 5 : (num(K.grassMotion).map { (0...4).contains($0) ? $0 : -1 } ?? -1), K.grassMotion) { i in
                    put(K.grassMotion, i == 5 ? nil : i)
                }
                RowDivider()
                toggle("leaf", "Remove grass", "Hides the built in terrain grass", num(K.grassMin) == 0 && num(K.grassMax) == 0) {
                    put(K.grassMin, $0 ? 0 : nil)
                    put(K.grassMax, $0 ? 0 : nil)
                }
                RowDivider()
                Row(symbol: "ruler", title: "Grass distance", detail: "Set both to 0 to hide grass") {
                    HStack(spacing: 6) {
                        NumberField(placeholder: "100", value: num(K.grassMin)) { put(K.grassMin, $0) }
                        Text(Loc.t("to")).font(.system(size: 13)).foregroundColor(Palette.textTertiary)
                        NumberField(placeholder: "290", value: num(K.grassMax)) { put(K.grassMax, $0) }
                    }
                }
            }

            SectionTitle(text: "Interface")
            Card {
                toggle("drop", "Remove blur", "No blur behind Roblox menus", has(K.blur) && num(K.blur) == 0) { put(K.blur, $0 ? 0 : nil) }
                RowDivider()
                Row(symbol: "eye.slash", title: "Hide GUIs", detail: "Group ID you are in, toggled with keyboard shortcuts") {
                    NumberField(placeholder: "Off", value: num(K.hideGuiGroup), width: 110) {
                        put(K.hideGuiGroup, $0)
                        put(K.hideGuiToggles, $0 == nil ? nil : true)
                    }
                }
                RowDivider()
                Row(symbol: "textformat.size", title: "Font size", detail: "Changes the Roblox menu font size") {
                    NumberField(placeholder: "Off", value: num(K.fontPadding)) { put(K.fontPadding, $0) }
                }
            }

            SectionTitle(text: "Network")
            Card {
                toggle("waveform.path", "Reduce lag spikes", "Smooths out network output", has(K.lagSpikes[0]) && has(K.lagSpikes[1])) {
                    put(K.lagSpikes[0], $0 ? 96000 : nil)
                    put(K.lagSpikes[1], $0 ? 50 : nil)
                }
                RowDivider()
                toggle("hare", "Faster loading", "Preloads more assets to load games faster", num(K.fastLoad[0]) == 2147483647) {
                    put(K.fastLoad[0], $0 ? 2147483647 : nil)
                    model.putFlag(K.fastLoad[1], $0 ? "1" : nil)
                }
                RowDivider()
                Row(symbol: "network", title: "MTU size", detail: "Bytes sent per network packet") {
                    NumberField(placeholder: "Off", value: num(K.mtu)) { put(K.mtu, $0) }
                }
            }

            SectionTitle(text: "Debug")
            Card {
                Row(symbol: "list.bullet.rectangle", title: "Flag state", detail: "Shows these flag values in game, comma separated") {
                    TextInput(placeholder: "Off", value: model.flag(K.flagState) ?? "") { model.putFlag(K.flagState, $0.isEmpty ? nil : $0) }
                }
                RowDivider()
                toggle("antenna.radiowaves.left.and.right", "Ping breakdown", "Prints ping details to the Roblox console", on(K.ping)) { put(K.ping, $0 ? true : nil) }
            }

            SectionTitle(text: "Quick setup")
            Card {
                Row(symbol: "wand.and.stars", title: "Recommended", detail: "Sets textures and anti aliasing") {
                    Menu {
                        ForEach(Array(["Balanced", "Performance", "Quality"].enumerated()), id: \.offset) { item in
                            Button { profile = item.offset } label: {
                                if item.offset == profile { Label(Loc.t(item.element), systemImage: "checkmark") } else { Text(Loc.t(item.element)) }
                            }
                        }
                    } label: {
                        HStack(spacing: 4) {
                            Text(loc: ["Balanced", "Performance", "Quality"][profile]).lineLimit(1)
                            Image(systemName: "chevron.up.chevron.down").font(.system(size: 11, weight: .semibold))
                        }
                        .font(.system(size: 15, weight: .medium))
                        .foregroundColor(Palette.primary)
                        .fixedSize()
                    }
                    Button(Loc.t("Apply")) {
                        let p = K.profiles[profile]
                        put(K.textureOn, true)
                        put(K.texture, p.0)
                        put(K.msaa, p.1)
                        model.show(Loc.t("Profile applied"))
                    }
                    .buttonStyle(PrimaryButtonStyle())
                }
            }
        }
        .alert(Loc.t("Going above 240 FPS is not recommended, as this may cause latency issues."), isPresented: $highFps) {
            Button(Loc.t("OK"), role: .cancel) {}
        }
    }

    private func toggle(_ symbol: String, _ title: String, _ detail: String, _ value: Bool, _ change: @escaping (Bool) -> Void) -> some View {
        Row(symbol: symbol, title: title, detail: detail) {
            Toggle("", isOn: Binding(get: { value }, set: change)).labelsHidden().accessibilityLabel(Loc.t(title))
        }
    }

    private func choice(_ symbol: String, _ title: String, _ detail: String, _ labels: [String], _ selected: Int, _ key: String?, _ change: @escaping (Int) -> Void) -> some View {
        var shown = labels.map { Loc.t($0) }
        if selected < 0 { shown.append(Loc.t("Custom") + " (\(key.flatMap { model.flag($0) } ?? ""))") }
        let index = selected < 0 ? labels.count : selected
        return Row(symbol: symbol, title: title, detail: detail) {
            Menu {
                ForEach(Array(labels.enumerated()), id: \.offset) { item in
                    Button { change(item.offset) } label: {
                        if item.offset == index { Label(Loc.t(item.element), systemImage: "checkmark") } else { Text(Loc.t(item.element)) }
                    }
                }
            } label: {
                HStack(spacing: 4) {
                    Text(shown[min(index, shown.count - 1)]).lineLimit(1)
                    Image(systemName: "chevron.up.chevron.down").font(.system(size: 11, weight: .semibold))
                }
                .font(.system(size: 14, weight: .medium))
                .foregroundColor(Palette.primary)
            }
            .accessibilityLabel(Loc.t(title))
        }
    }

    private func slider(_ range: ClosedRange<Int>, _ value: Int, _ change: @escaping (Int) -> Void) -> some View {
        HStack(spacing: 10) {
            Text(Loc.t("Low")).font(.system(size: 13)).foregroundColor(Palette.textTertiary)
            Slider(value: Binding(get: { Double(min(max(value, range.lowerBound), range.upperBound)) }, set: { change(Int($0.rounded())) }),
                   in: Double(range.lowerBound)...Double(range.upperBound), step: 1)
            Text(Loc.t("High")).font(.system(size: 13)).foregroundColor(Palette.textTertiary)
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 8)
    }
}

struct NumberField: View {
    let placeholder: String
    let value: Int?
    var width: CGFloat = 80
    let change: (Int?) -> Void
    @State private var text = ""

    var body: some View {
        TextField(Loc.t(placeholder), text: $text)
            .keyboardType(.numberPad)
            .multilineTextAlignment(.center)
            .frame(width: width, height: 36)
            .surface(RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous))
            .onAppear { text = value.map(String.init) ?? "" }
            .onChange(of: text) { new in
                let t = new.trimmingCharacters(in: .whitespaces)
                if t.isEmpty { change(nil) } else if let n = Int32(t) { change(Int(n)) }
            }
    }
}

struct TextInput: View {
    let placeholder: String
    let value: String
    let change: (String) -> Void
    @State private var text = ""

    var body: some View {
        TextField(Loc.t(placeholder), text: $text)
            .textInputAutocapitalization(.never)
            .autocorrectionDisabled()
            .multilineTextAlignment(.center)
            .frame(width: 150, height: 36)
            .surface(RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous))
            .onAppear { text = value }
            .onChange(of: text) { change($0.trimmingCharacters(in: .whitespaces)) }
    }
}
