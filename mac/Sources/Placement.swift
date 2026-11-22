import AppKit
import CoreGraphics

/// A widget position relative to one screen: the screen's stable ID and the offset of the widget's
/// top-left corner from that screen's top-left corner (points, y measured downwards).
struct Placement: Codable, Equatable {
    var screen: String
    var dx: Double
    var dy: Double
    /// When it was saved, so the most recent wins when several screens have one.
    var savedAt: Double
}

/// What the placement logic needs to know about one connected screen.
struct ScreenDesc: Equatable {
    var id: String          // display UUID (stable across replugging), or name as a fallback
    var frame: NSRect
    var visibleFrame: NSRect
    var isMain: Bool        // the menu-bar screen

    static func current() -> [ScreenDesc] {
        NSScreen.screens.enumerated().map { i, s in
            ScreenDesc(id: stableID(s), frame: s.frame, visibleFrame: s.visibleFrame, isMain: i == 0)
        }
    }

    static func stableID(_ s: NSScreen) -> String {
        if let n = s.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber {
            let did = CGDirectDisplayID(n.uint32Value)
            if let u = CGDisplayCreateUUIDFromDisplayID(did)?.takeRetainedValue(),
               let str = CFUUIDCreateString(nil, u) as String? {
                return str
            }
            return "\(s.localizedName)#\(did)"
        }
        return s.localizedName
    }
}

enum PlacementLogic {
    /// Identifies the display configuration: every connected screen with its frame, sorted.
    static func configKey(_ screens: [ScreenDesc]) -> String {
        screens.map { s in
            "\(s.id)@\(Int(s.frame.minX)),\(Int(s.frame.minY)),\(Int(s.frame.width))x\(Int(s.frame.height))"
        }.sorted().joined(separator: "|")
    }

    /// The screen the widget is (mostly) on.
    static func screen(for r: NSRect, in screens: [ScreenDesc]) -> ScreenDesc? {
        screens.max { a, b in area(a.frame.intersection(r)) < area(b.frame.intersection(r)) }
            .flatMap { area($0.frame.intersection(r)) > 0 ? $0 : nil }
    }

    private static func area(_ r: NSRect) -> CGFloat { r.isNull ? 0 : r.width * r.height }

    static func placement(for r: NSRect, in screens: [ScreenDesc], now: Double) -> Placement? {
        guard let s = screen(for: r, in: screens) else { return nil }
        return Placement(screen: s.id, dx: Double(r.minX - s.frame.minX), dy: Double(s.frame.maxY - r.maxY), savedAt: now)
    }

    /// Frame for a placement on its screen, kept inside that screen. (The full frame, not the visible
    /// frame: the Dock can move between screens, and that shouldn't shift a saved spot.)
    static func frame(_ p: Placement, size: NSSize, on s: ScreenDesc) -> NSRect {
        let r = NSRect(x: s.frame.minX + CGFloat(p.dx), y: s.frame.maxY - CGFloat(p.dy) - size.height,
                       width: size.width, height: size.height)
        return clamp(r, to: s.frame)
    }

    static func clamp(_ r: NSRect, to area: NSRect) -> NSRect {
        var r = r
        r.origin.x = min(max(r.minX, area.minX), max(area.minX, area.maxX - r.width))
        r.origin.y = min(max(r.minY, area.minY), max(area.minY, area.maxY - r.height))
        return r
    }

    static func defaultFrame(size: NSSize, screens: [ScreenDesc]) -> NSRect {
        let area = (screens.first { $0.isMain } ?? screens.first)?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        return NSRect(x: area.maxX - size.width - 24, y: area.maxY - 24 - size.height, width: size.width, height: size.height)
    }

    /// Where the widget should go for this set of screens:
    /// 1. the position saved for exactly this configuration;
    /// 2. else the most recent position saved on any screen that's connected now (in any configuration);
    /// 3. else the legacy global position, if it's still on a screen;
    /// 4. else the top-right of the main screen.
    static func resolve(size: NSSize, screens: [ScreenDesc], settings: WidgetSettings) -> NSRect {
        let byID = Dictionary(screens.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        if let p = settings.placementsByConfig?[configKey(screens)], let s = byID[p.screen] {
            return frame(p, size: size, on: s)
        }
        if let p = (settings.placementsByScreen ?? [:]).values.filter({ byID[$0.screen] != nil }).max(by: { $0.savedAt < $1.savedAt }),
           let s = byID[p.screen] {
            return frame(p, size: size, on: s)
        }
        if let l = settings.left, let t = settings.top {
            let r = NSRect(x: l, y: t - size.height, width: size.width, height: size.height)
            if let s = screen(for: r, in: screens) { return clamp(r, to: s.frame) }
        }
        return defaultFrame(size: size, screens: screens)
    }

    /// Records a user-chosen position for the current configuration and for its screen.
    static func record(_ r: NSRect, screens: [ScreenDesc], into settings: inout WidgetSettings, now: Double) {
        guard let p = placement(for: r, in: screens, now: now) else { return }
        settings.placementsByConfig = (settings.placementsByConfig ?? [:]).merging([configKey(screens): p]) { _, n in n }
        settings.placementsByScreen = (settings.placementsByScreen ?? [:]).merging([p.screen: p]) { _, n in n }
        settings.left = Double(r.minX)
        settings.top = Double(r.maxY)
    }
}
