import AppKit

/// Draws the hostname and one row per interface:  [icon]  IP   MAC   speed   en0
final class WidgetView: NSView {
    var settings = WidgetSettings() { didSet { relayout() } }
    var rows: [InterfaceInfo] = [] { didSet { relayout() } }
    /// Shift+Option held: show a blue outline (and a faint panel in text-only mode) so you can see what you're grabbing.
    var interactive = false { didSet { if interactive != oldValue { needsDisplay = true } } }

    var onRightClick: ((NSEvent) -> Void)?
    var onDragEnded: (() -> Void)?

    private let padding = NSEdgeInsets(top: 8, left: 14, bottom: 9, right: 14)
    private var layoutCache = Layout()

    override var isFlipped: Bool { true }
    override var isOpaque: Bool { false }

    // MARK: - mouse (only reaches us while the window accepts mouse events)

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        guard interactive, let window else { return }
        window.performDrag(with: event)
        onDragEnded?()
    }

    override func rightMouseDown(with event: NSEvent) {
        guard interactive else { return }
        onRightClick?(event)
    }

    // Ctrl+click is a right-click on a Mac too.
    override func menu(for event: NSEvent) -> NSMenu? { nil }

    // MARK: - layout

    private struct Cell {
        var text: NSAttributedString?
        var image: NSImage?
        var size: NSSize = .zero
        var ascent: CGFloat = 0
    }

    private struct Line {
        var cells: [Cell?] = []   // one per column; nil = nothing there
        var height: CGFloat = 0
        var ascent: CGFloat = 0
        var dimmed = false
    }

    private struct Layout {
        var host: Cell?
        var lines: [Line] = []
        var columnWidths: [CGFloat] = []
        var size = NSSize(width: 120, height: 40)
    }

    override var intrinsicContentSize: NSSize { layoutCache.size }

    private var textColor: NSColor { Look.color(settings.textColor) }
    private var darkText: Bool { Look.isDark(textColor) }

    private func secondary(_ c: NSColor) -> NSColor {
        settings.showBackground ? c.withAlphaComponent(0.7) : c
    }

    private func attributed(_ s: String, font: NSFont, color: NSColor, underline: Bool) -> NSAttributedString {
        var attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color]
        if underline { attrs[.underlineStyle] = NSUnderlineStyle.single.rawValue }
        return NSAttributedString(string: s, attributes: attrs)
    }

    private func textCell(_ s: String, font: NSFont, color: NSColor, underline: Bool) -> Cell {
        let a = attributed(s, font: font, color: color, underline: underline)
        let sz = a.size()
        return Cell(text: a, size: NSSize(width: ceil(sz.width), height: ceil(font.ascender - font.descender)), ascent: font.ascender)
    }

    func relayout() {
        let s = settings
        let base = textColor
        var L = Layout()

        if !s.hideHostname {
            let f = Look.font(family: s.fontFamily, size: s.hostnameSize, style: s.hostnameStyle)
            L.host = textCell(Hostname.formatted(s.hostnameCase), font: f, color: base, underline: s.hostnameStyle.underline)
        }

        let ipFont = Look.font(family: s.fontFamily, size: s.ipSize, style: s.ipStyle)
        let macFont = Look.font(family: s.fontFamily, size: s.macSize, style: s.macStyle, monospacedDefault: true)
        let typeFont = Look.font(family: s.fontFamily, size: s.typeSize, style: s.typeStyle)
        let speedFont = Look.font(family: s.fontFamily, size: s.speedSize, style: s.speedStyle)
        let sec = secondary(base)

        if rows.isEmpty {
            var line = Line()
            line.cells = [nil, textCell("No network connection", font: ipFont, color: base, underline: false), nil, nil, nil]
            L.lines.append(line)
        }

        for r in rows {
            var line = Line()
            line.dimmed = !r.isUp
            // Columns: 0 icon, 1 IP, 2 MAC, 3 speed, 4 BSD name
            var cells: [Cell?] = Array(repeating: nil, count: 5)
            if !s.hideType, let img = icon(for: r.kind, font: typeFont, color: base) {
                cells[0] = Cell(image: img, size: img.size, ascent: 0)
            }
            if !s.hideIp {
                let ip = r.isUp ? format(ip: r.ip ?? "No IPv4 address", s.ipFormat) : "disconnected"
                cells[1] = textCell(ip, font: ipFont, color: base, underline: s.ipStyle.underline)
            }
            if !s.hideMac {
                cells[2] = textCell(r.mac, font: macFont, color: sec, underline: s.macStyle.underline)
            }
            if !s.hideSpeed, r.isUp {
                cells[3] = textCell(r.speed ?? "–", font: speedFont, color: sec, underline: s.speedStyle.underline)
            }
            if !s.hideBsdName {
                let small = Look.font(family: s.fontFamily, size: max(8, s.typeSize - 2), style: s.typeStyle)
                cells[4] = textCell(r.bsdName, font: small, color: sec.withAlphaComponent(sec.alphaComponent * 0.8),
                                    underline: s.typeStyle.underline)
            }
            line.cells = cells
            L.lines.append(line)
        }

        // Per-line metrics, then shared column widths so the rows line up.
        var widths = Array(repeating: CGFloat(0), count: 5)
        for i in L.lines.indices {
            var asc: CGFloat = 0, desc: CGFloat = 0
            for (c, cell) in L.lines[i].cells.enumerated() {
                guard let cell else { continue }
                widths[c] = max(widths[c], cell.size.width)
                if cell.text != nil {
                    asc = max(asc, cell.ascent)
                    desc = max(desc, cell.size.height - cell.ascent)
                }
            }
            if asc == 0, let img = L.lines[i].cells[0] { asc = img.size.height; desc = 0 }
            L.lines[i].ascent = asc
            L.lines[i].height = max(asc + desc, L.lines[i].cells[0]?.size.height ?? 0)
        }
        L.columnWidths = widths

        let usedCols = widths.indices.filter { widths[$0] > 0 }
        var rowWidth: CGFloat = 0
        for (n, c) in usedCols.enumerated() {
            rowWidth += widths[c]
            if n < usedCols.count - 1 { rowWidth += c == 0 ? iconGap(typeFont) : CGFloat(s.columnGap) }
        }
        rowWidth = max(0, rowWidth)

        var h: CGFloat = 0
        var first = true
        if let host = L.host { h += host.size.height; first = false }
        for line in L.lines {
            if !first { h += CGFloat(s.rowGap) }
            h += line.height
            first = false
        }
        let contentW = max(rowWidth, L.host?.size.width ?? 0)
        L.size = NSSize(width: ceil(contentW + padding.left + padding.right),
                        height: ceil(max(1, h) + padding.top + padding.bottom))
        layoutCache = L
        invalidateIntrinsicContentSize()
        needsDisplay = true
    }

    /// The icon sits close to its IP; the column gap applies between the text columns.
    private func iconGap(_ f: NSFont) -> CGFloat { max(4, f.pointSize * 0.45) }

    private func icon(for kind: LinkKind, font: NSFont, color: NSColor) -> NSImage? {
        let names = kind == .wifi ? ["wifi"] : ["cable.connector", "network"]
        for name in names {
            guard let img = NSImage(systemSymbolName: name, accessibilityDescription: kind == .wifi ? "Wi-Fi" : "Ethernet") else { continue }
            let w = Look.systemWeight(settings.typeStyle.weight)
            let cfg = NSImage.SymbolConfiguration(pointSize: font.pointSize, weight: w)
                .applying(NSImage.SymbolConfiguration(paletteColors: [color]))
            return img.withSymbolConfiguration(cfg)
        }
        return nil
    }

    private func format(ip: String, _ fmt: String) -> String {
        let parts = ip.split(separator: ".")
        guard parts.count == 4 else { return ip }
        switch fmt {
        case "last2": return "\(parts[2]).\(parts[3])"
        case "last1": return ".\(parts[3])"
        default: return ip
        }
    }

    // MARK: - drawing

    override func draw(_ dirtyRect: NSRect) {
        let s = settings
        let card = bounds.insetBy(dx: 0.5, dy: 0.5)
        let path = NSBezierPath(roundedRect: card, xRadius: 12, yRadius: 12)

        if s.showBackground {
            (darkText ? NSColor(white: 0.95, alpha: 0.72) : NSColor(white: 0.08, alpha: 0.65)).setFill()
            path.fill()
        } else if interactive {
            NSColor(white: 0.08, alpha: 0.25).setFill()
            path.fill()
        }
        if interactive {
            NSColor(srgbRed: 0x6C / 255, green: 0xB8 / 255, blue: 1, alpha: 0.6).setStroke()
            path.lineWidth = 1.5
            path.stroke()
        } else if s.showBackground {
            (darkText ? NSColor(white: 0, alpha: 0.08) : NSColor(white: 1, alpha: 0.08)).setStroke()
            path.lineWidth = 1
            path.stroke()
        }

        if !s.showBackground && s.textShadow {
            // Two passes build a darker halo than one shadow can (same trick as the Windows version).
            let haloColor: NSColor = darkText ? .white : .black
            drawContent(shadow: shadow(haloColor.withAlphaComponent(0.8), blur: 6))
            drawContent(shadow: shadow(haloColor, blur: 3))
        } else {
            drawContent(shadow: nil)
        }
    }

    private func shadow(_ c: NSColor, blur: CGFloat) -> NSShadow {
        let sh = NSShadow()
        sh.shadowColor = c
        sh.shadowBlurRadius = blur
        sh.shadowOffset = .zero
        return sh
    }

    private func drawContent(shadow: NSShadow?) {
        NSGraphicsContext.saveGraphicsState()
        shadow?.set()
        let s = settings
        let L = layoutCache
        var y = padding.top
        var first = true
        if let host = L.host, let t = host.text {
            t.draw(at: NSPoint(x: padding.left, y: y))
            y += host.size.height
            first = false
        }
        let typeFont = Look.font(family: s.fontFamily, size: s.typeSize, style: s.typeStyle)
        for line in L.lines {
            if !first { y += CGFloat(s.rowGap) }
            first = false
            let baseline = y + line.ascent
            var x = padding.left
            let alpha: CGFloat = line.dimmed ? 0.45 : 1
            let used = L.columnWidths.indices.filter { L.columnWidths[$0] > 0 }
            for (n, c) in used.enumerated() {
                if let cell = line.cells[c] {
                    if let img = cell.image {
                        // Centre the icon on the text's x-height band.
                        let ipFont = Look.font(family: s.fontFamily, size: s.ipSize, style: s.ipStyle)
                        let mid = baseline - ipFont.xHeight / 2
                        let r = NSRect(x: x + (L.columnWidths[c] - img.size.width) / 2, y: line.ascent > 0 ? mid - img.size.height / 2 : y,
                                       width: img.size.width, height: img.size.height)
                        img.draw(in: r, from: .zero, operation: .sourceOver, fraction: alpha, respectFlipped: true, hints: nil)
                    } else if let t = cell.text {
                        let p = NSPoint(x: x, y: baseline - cell.ascent)
                        if alpha < 1 {
                            let m = NSMutableAttributedString(attributedString: t)
                            m.enumerateAttribute(.foregroundColor, in: NSRange(location: 0, length: m.length)) { v, r, _ in
                                if let col = v as? NSColor {
                                    m.addAttribute(.foregroundColor, value: col.withAlphaComponent(col.alphaComponent * alpha), range: r)
                                }
                            }
                            m.draw(at: p)
                        } else {
                            t.draw(at: p)
                        }
                    }
                }
                x += L.columnWidths[c]
                if n < used.count - 1 { x += c == 0 ? iconGap(typeFont) : CGFloat(s.columnGap) }
            }
            y += line.height
        }
        NSGraphicsContext.restoreGraphicsState()
    }
}
