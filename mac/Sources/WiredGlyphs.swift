import AppKit

/// Hand-drawn wired-network glyph, sized to match the "wifi" symbol's height.
enum WiredGlyphs {
    static func port(height h: CGFloat, color: NSColor, weight: String) -> NSImage {
        // Stroke weight roughly like SF Symbols at Regular; heavier weights get thicker lines.
        let wScale: CGFloat = ["Light": 0.8, "Medium": 1.1, "SemiBold": 1.2, "Bold": 1.35, "Black": 1.5][weight] ?? 1
        let line = max(1, h * 0.12 * wScale)
        return port(h, line, color)
    }

    /// RJ45 jack seen from the front: a solid block with the socket opening (and its latch notch) cut out.
    private static func port(_ h: CGFloat, _ line: CGFloat, _ color: NSColor) -> NSImage {
        let w = h * 1.12
        return NSImage(size: NSSize(width: w, height: h), flipped: true) { r in
            let outer = NSBezierPath(roundedRect: r.insetBy(dx: 0.25, dy: 0.25), xRadius: h * 0.16, yRadius: h * 0.16)
            // Socket: wide upper part, narrower latch notch at the bottom.
            let t = line * 1.15
            let left = t, right = w - t, top = t, bottom = h - t
            let notchW = w * 0.36, notchTop = top + (bottom - top) * 0.62
            let mid = w / 2
            let hole = NSBezierPath()
            hole.move(to: NSPoint(x: left, y: top))
            hole.line(to: NSPoint(x: right, y: top))
            hole.line(to: NSPoint(x: right, y: notchTop))
            hole.line(to: NSPoint(x: mid + notchW / 2, y: notchTop))
            hole.line(to: NSPoint(x: mid + notchW / 2, y: bottom))
            hole.line(to: NSPoint(x: mid - notchW / 2, y: bottom))
            hole.line(to: NSPoint(x: mid - notchW / 2, y: notchTop))
            hole.line(to: NSPoint(x: left, y: notchTop))
            hole.close()
            outer.append(hole)
            // Contact pins along the top of the socket.
            let pins = 4
            let pinW = max(0.8, line * 0.5), pinH = (notchTop - top) * 0.38
            let span = right - left - line * 1.2
            for i in 0..<pins {
                let x = left + line * 0.6 + span * (CGFloat(i) + 0.5) / CGFloat(pins) - pinW / 2
                outer.append(NSBezierPath(rect: NSRect(x: x, y: top, width: pinW, height: pinH)))
            }
            outer.windingRule = .evenOdd
            color.setFill()
            outer.fill()
            return true
        }
    }
}
