public struct NamedTheme: Sendable {
    public let id: String
    public let title: String
    public let window: UInt32
    public let sidebar: UInt32
    public let card: UInt32
    public let menu: UInt32
    public let input: UInt32
    public let text: UInt32
    public let textSecondary: UInt32
    public let textTertiary: UInt32
    public let primary: UInt32
    public let accent: UInt32
    public let onPrimary: UInt32

    public static let all: [NamedTheme] = [
        NamedTheme(id: "voidstrap", title: "Voidstrap", window: 0x1E0B2F, sidebar: 0x0A0412, card: 0x2C1A3B, menu: 0x342344, input: 0x180926, text: 0x7F8CBF, textSecondary: 0x6E78A5, textTertiary: 0x5C6390, primary: 0x5E52C2, accent: 0x5E52C2, onPrimary: 0xFFFFFF),
        NamedTheme(id: "ultra_gray", title: "Ultra Gray", window: 0x202020, sidebar: 0x0A0A0A, card: 0x262626, menu: 0x2E2E2E, input: 0x1A1A1A, text: 0xF5F5F5, textSecondary: 0xCCCCCC, textTertiary: 0xA0A0A0, primary: 0x808080, accent: 0x808080, onPrimary: 0x1B1B1B),
        NamedTheme(id: "berry", title: "Berry", window: 0x220A1C, sidebar: 0x190713, card: 0x2F0E26, menu: 0x350F2B, input: 0x1B0816, text: 0xF3E7F4, textSecondary: 0xBFB0BE, textTertiary: 0x8A7888, primary: 0xD083E0, accent: 0xD083E0, onPrimary: 0x1B1B1B),
        NamedTheme(id: "blue", title: "Blue", window: 0x0B1A2E, sidebar: 0x081220, card: 0x0F2743, menu: 0x102C4C, input: 0x091525, text: 0xE3ECF7, textSecondary: 0xADB8C5, textTertiary: 0x778392, primary: 0x4FA8F5, accent: 0x4FA8F5, onPrimary: 0x1B1B1B),
        NamedTheme(id: "cyan", title: "Cyan", window: 0x07201F, sidebar: 0x051616, card: 0x09302E, menu: 0x0A3734, input: 0x061A19, text: 0xDFF4F2, textSecondary: 0xA9BFBD, textTertiary: 0x738A88, primary: 0x35C9C0, accent: 0x35C9C0, onPrimary: 0x1B1B1B),
        NamedTheme(id: "green", title: "Green", window: 0x0A1E10, sidebar: 0x07150B, card: 0x0D2D17, menu: 0x0E341A, input: 0x08180D, text: 0xE2F3E7, textSecondary: 0xACBEB1, textTertiary: 0x76887C, primary: 0x4ED07A, accent: 0x4ED07A, onPrimary: 0x1B1B1B),
        NamedTheme(id: "orange", title: "Orange", window: 0x241307, sidebar: 0x190D05, card: 0x371E0A, menu: 0x3F220C, input: 0x1D0F06, text: 0xF6EADD, textSecondary: 0xC2B4A8, textTertiary: 0x8D7E72, primary: 0xE8923A, accent: 0xE8923A, onPrimary: 0x1B1B1B),
        NamedTheme(id: "pink", title: "Pink", window: 0x260A18, sidebar: 0x1B0711, card: 0x380F23, menu: 0x401128, input: 0x1E0813, text: 0xF8E6EF, textSecondary: 0xC4AFB9, textTertiary: 0x8F7884, primary: 0xFF77AA, accent: 0xFF77AA, onPrimary: 0x1B1B1B),
        NamedTheme(id: "purple", title: "Purple", window: 0x1B0B30, sidebar: 0x130822, card: 0x250F40, menu: 0x2A1148, input: 0x160926, text: 0xEAE2F6, textSecondary: 0xB6ACC4, textTertiary: 0x827693, primary: 0x9B6BE0, accent: 0x9B6BE0, onPrimary: 0x1B1B1B),
        NamedTheme(id: "red", title: "Red", window: 0x260B0B, sidebar: 0x1B0808, card: 0x381010, menu: 0x401212, input: 0x1E0909, text: 0xF6E3E3, textSecondary: 0xC2ADAD, textTertiary: 0x8E7777, primary: 0xE85A5A, accent: 0xE85A5A, onPrimary: 0x1B1B1B),
        NamedTheme(id: "yellow", title: "Yellow", window: 0x221E08, sidebar: 0x181506, card: 0x342D0B, menu: 0x3B340C, input: 0x1B1806, text: 0xF6F1DD, textSecondary: 0xC1BCA8, textTertiary: 0x8C8872, primary: 0xE8C83A, accent: 0xE8C83A, onPrimary: 0x1B1B1B)
    ]
}
