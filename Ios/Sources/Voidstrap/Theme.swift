import SwiftUI
import UIKit
import VoidstrapCore

enum Palette {
    nonisolated(unsafe) static var named: NamedTheme?

    static func dynamic(_ dark: UInt32, _ light: UInt32, _ pick: KeyPath<NamedTheme, UInt32>? = nil) -> Color {
        if let named, let pick { return Color(UIColor(hex: named[keyPath: pick])) }
        return Color(UIColor { $0.userInterfaceStyle == .dark ? UIColor(hex: dark) : UIColor(hex: light) })
    }

    static var primary: Color { dynamic(0xCCBEFF, 0x625690, \NamedTheme.primary) }
    static var onPrimary: Color { dynamic(0x33275E, 0xFFFFFF, \NamedTheme.onPrimary) }
    static var window: Color { dynamic(0x141318, 0xFDF7FF, \NamedTheme.window) }
    static var sidebar: Color { dynamic(0x201F24, 0xF1ECF4, \NamedTheme.sidebar) }
    static var card: Color { dynamic(0x1C1B20, 0xF7F2FA, \NamedTheme.card) }
    static var menu: Color { dynamic(0x2B292F, 0xEBE6EE, \NamedTheme.menu) }
    static var input: Color { dynamic(0x36343A, 0xE6E0E9, \NamedTheme.input) }
    static var text: Color { dynamic(0xE6E1E9, 0x1C1B20, \NamedTheme.text) }
    static var textSecondary: Color { dynamic(0xCAC4CF, 0x48454E, \NamedTheme.textSecondary) }
    static var textTertiary: Color { dynamic(0x938F99, 0x79757F, \NamedTheme.textTertiary) }
    static var divider: Color { dynamic(0x48454E, 0xCAC4CF, \NamedTheme.menu) }
    static var success: Color { dynamic(0x6CCB5F, 0x2E7D32) }
    static var critical: Color { dynamic(0xFFB4AB, 0xBA1A1A) }
    static var accent: Color { dynamic(0x7FB2FF, 0x5B8DEF, \NamedTheme.accent) }
}

enum Metrics {
    static let pageInset: CGFloat = 16
    static let cardRadius: CGFloat = 20
    static let controlRadius: CGFloat = 12
    static let iconTile: CGFloat = 40
    static let rail: CGFloat = 80
}

extension UIColor {
    convenience init(hex: UInt32) {
        self.init(red: CGFloat(hex >> 16 & 0xFF) / 255, green: CGFloat(hex >> 8 & 0xFF) / 255, blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
    }
}

extension View {
    @ViewBuilder func glass<S: Shape>(_ shape: S, tint: Color? = nil, interactive: Bool = false) -> some View {
        if #available(iOS 26.0, *) {
            glassEffect(Glass.voidstrap(tint: tint, interactive: interactive), in: shape)
        } else if let tint {
            background(tint, in: shape)
        } else {
            background(.ultraThinMaterial, in: shape)
        }
    }
}

extension View {
    func surface<S: Shape>(_ shape: S, fill: Color? = nil) -> some View {
        background(fill ?? Palette.card.opacity(0.92), in: shape)
            .overlay(shape.stroke(Palette.divider.opacity(fill == nil ? 0.35 : 0), lineWidth: 0.5))
    }
}

@available(iOS 26.0, *)
extension Glass {
    static func voidstrap(tint: Color?, interactive: Bool) -> Glass {
        var glass = Glass.regular
        if let tint { glass = glass.tint(tint) }
        if interactive { glass = glass.interactive() }
        return glass
    }
}

struct GlassBackdrop: View {
    @ObservedObject private var custom = AppBackground.shared

    var body: some View {
        if let image = custom.active {
            GeometryReader { geo in
                ZStack {
                    Image(uiImage: image).resizable().scaledToFill().frame(width: geo.size.width, height: geo.size.height).clipped()
                    Palette.window.opacity(Double(custom.dim) / 100)
                }
            }
            .ignoresSafeArea()
            .accessibilityHidden(true)
        } else {
            glow
        }
    }

    private var glow: some View {
        ZStack {
            Palette.window
            Circle()
                .fill(Palette.primary.opacity(0.35))
                .frame(width: 460, height: 460)
                .blur(radius: 120)
                .offset(x: -150, y: -280)
            Circle()
                .fill(Palette.accent.opacity(0.28))
                .frame(width: 420, height: 420)
                .blur(radius: 120)
                .offset(x: 170, y: 240)
        }
        .ignoresSafeArea()
        .accessibilityHidden(true)
    }
}
