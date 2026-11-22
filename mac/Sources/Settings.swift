import AppKit

/// Weight / italic / underline for one field.
struct FieldStyle: Codable, Equatable {
    /// Light, Normal, Medium, SemiBold, Bold or Black (same names as the Windows version).
    var weight: String = "Normal"
    var italic = false
    var underline = false
}

/// Everything the widget remembers, stored as one JSON blob in UserDefaults.
struct WidgetSettings: Codable, Equatable {
    /// BSD names of the interfaces to show. Empty = Automatic (every active Wi-Fi/Ethernet with IPv4).
    var interfaces: [String] = []

    /// Top-left corner of the widget in screen coordinates (Cocoa: y grows upwards).
    var left: Double?
    var top: Double?
    /// Where the widget sits for each display configuration, relative to the screen it's on.
    /// Optional so settings saved by older builds still decode.
    var placementsByConfig: [String: Placement]?
    /// The last position used on each screen, in any configuration (fallback for new configurations).
    var placementsByScreen: [String: Placement]?

    var keepOnTop = false
    var showBackground = true
    /// Dark halo behind the text; only used when the background is off.
    var textShadow = true
    var textColor = "#FFFFFF"

    /// One font for every field; nil = system font (with the monospaced system font for the MAC).
    var fontFamily: String?
    /// "upper" = MERCURY, "title" = Mercury, "lower" = mercury, "asis" = as named in Sharing settings.
    var hostnameCase = "upper"

    var hostnameSize: Double = 17
    var ipSize: Double = 14
    var macSize: Double = 12
    var typeSize: Double = 12
    var speedSize: Double = 12

    var hostnameStyle = FieldStyle(weight: "SemiBold")
    var ipStyle = FieldStyle()
    var macStyle = FieldStyle()
    var typeStyle = FieldStyle()
    var speedStyle = FieldStyle()

    /// Gap between rows (-60…40, negative lets rows overlap) and between columns (-40…80).
    var rowGap: Double = 1
    var columnGap: Double = 18

    /// "full" = 192.168.26.214, "last2" = 26.214, "last1" = .214
    var ipFormat = "full"

    var hideHostname = false
    var hideIp = false
    var hideMac = false
    var hideType = false
    /// The BSD name (en0, en9) next to each row; off by default.
    var hideBsdName = true
    var hideSpeed = false

    private static let key = "settings.v1"

    static func load() -> WidgetSettings {
        guard let data = UserDefaults.standard.data(forKey: key),
              let s = try? JSONDecoder().decode(WidgetSettings.self, from: data) else { return WidgetSettings() }
        return s
    }

    func save() {
        if let data = try? JSONEncoder().encode(self) {
            UserDefaults.standard.set(data, forKey: Self.key)
        }
    }

    /// Puts back everything the text dialog can change (used when it's cancelled).
    mutating func copyTextSettings(from o: WidgetSettings) {
        fontFamily = o.fontFamily
        hostnameCase = o.hostnameCase
        textColor = o.textColor
        (hostnameSize, ipSize, macSize, typeSize, speedSize) = (o.hostnameSize, o.ipSize, o.macSize, o.typeSize, o.speedSize)
        (hostnameStyle, ipStyle, macStyle, typeStyle, speedStyle) = (o.hostnameStyle, o.ipStyle, o.macStyle, o.typeStyle, o.speedStyle)
        (rowGap, columnGap) = (o.rowGap, o.columnGap)
    }
}

// MARK: - shared look helpers

enum Look {
    static let weights = ["Light", "Normal", "Medium", "SemiBold", "Bold", "Black"]

    static let sizePresets: [Double] = [9, 10, 11, 12, 13, 14, 16, 17, 18, 20, 22, 24, 28, 32, 36, 42, 48, 56, 64, 72]

    static let colorPresets: [(String, String)] = [
        ("White", "#FFFFFF"), ("Light gray", "#C8C8C8"), ("Black", "#000000"), ("Yellow", "#FFE066"),
        ("Orange", "#FFA94D"), ("Green", "#69DB7C"), ("Cyan", "#66D9E8"), ("Blue", "#74C0FC"),
        ("Pink", "#F783AC"), ("Red", "#FF6B6B"),
    ]

    static func color(_ hex: String?) -> NSColor {
        var s = (hex ?? "#FFFFFF").trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("#") { s.removeFirst() }
        guard s.count == 6, let v = UInt32(s, radix: 16) else { return .white }
        return NSColor(srgbRed: CGFloat((v >> 16) & 0xFF) / 255, green: CGFloat((v >> 8) & 0xFF) / 255,
                       blue: CGFloat(v & 0xFF) / 255, alpha: 1)
    }

    static func hex(_ c: NSColor) -> String {
        let c = c.usingColorSpace(.sRGB) ?? .white
        return String(format: "#%02X%02X%02X", Int(round(c.redComponent * 255)), Int(round(c.greenComponent * 255)),
                      Int(round(c.blueComponent * 255)))
    }

    static func isDark(_ c: NSColor) -> Bool {
        let c = c.usingColorSpace(.sRGB) ?? .white
        return 0.2126 * c.redComponent + 0.7152 * c.greenComponent + 0.0722 * c.blueComponent < 0.5
    }

    static func systemWeight(_ name: String) -> NSFont.Weight {
        switch name {
        case "Light": return .light
        case "Medium": return .medium
        case "SemiBold": return .semibold
        case "Bold": return .bold
        case "Black": return .black
        default: return .regular
        }
    }

    /// NSFontManager's 0–15 weight scale.
    static func managerWeight(_ name: String) -> Int {
        switch name {
        case "Light": return 3
        case "Medium": return 6
        case "SemiBold": return 8
        case "Bold": return 9
        case "Black": return 11
        default: return 5
        }
    }

    /// The font for one field. `monospacedDefault` is used for the MAC when no family is chosen.
    static func font(family: String?, size: Double, style: FieldStyle, monospacedDefault: Bool = false) -> NSFont {
        let size = CGFloat(max(4, min(200, size)))
        var font: NSFont
        if let family, !family.isEmpty,
           let f = NSFontManager.shared.font(withFamily: family, traits: [], weight: managerWeight(style.weight), size: size) {
            font = f
        } else if let family, !family.isEmpty, let f = NSFont(name: family, size: size) {
            font = f
        } else if monospacedDefault {
            font = .monospacedSystemFont(ofSize: size, weight: systemWeight(style.weight))
        } else {
            font = .systemFont(ofSize: size, weight: systemWeight(style.weight))
        }
        if style.italic {
            let converted = NSFontManager.shared.convert(font, toHaveTrait: .italicFontMask)
            if converted.fontDescriptor.symbolicTraits.contains(.italic) {
                font = converted
            } else {
                // No italic face (system font, many families): slant it instead.
                let d = font.fontDescriptor.withSymbolicTraits(.italic)
                font = NSFont(descriptor: d, size: size)
                    ?? NSFont(descriptor: font.fontDescriptor.withMatrix(AffineTransform(m11: 1, m12: 0, m21: 0.2, m22: 1, tX: 0, tY: 0)), size: size)
                    ?? font
            }
        }
        return font
    }
}

enum Hostname {
    /// The short name, as typed in System Settings → General → Sharing.
    static var asNamed: String {
        if let n = SCDynamicStoreCopyLocalHostNameSafe(), !n.isEmpty { return n }
        if let n = Host.current().localizedName, !n.isEmpty { return n }
        return ProcessInfo.processInfo.hostName.components(separatedBy: ".").first ?? "Mac"
    }

    static func formatted(_ mode: String) -> String {
        let n = asNamed
        switch mode {
        case "title": return n.prefix(1).uppercased() + n.dropFirst().lowercased()
        case "lower": return n.lowercased()
        case "asis": return n
        default: return n.uppercased()
        }
    }
}
