import AppKit

/// "Font, style, color and spacing…": one dialog for everything about the text, with live preview.
/// Every tweak calls `changed` so the widget updates as you go; `finished(false)` (Cancel, Esc, close box)
/// tells the caller to put its snapshot back.
final class SettingsWindowController: NSWindowController, NSWindowDelegate, NSTableViewDataSource, NSTableViewDelegate,
    NSSearchFieldDelegate, NSComboBoxDelegate {

    private static let defaultFontLabel = "Default (System font)"

    private var s: WidgetSettings
    private let changed: (WidgetSettings) -> Void
    private let finished: (Bool) -> Void
    private var done = false
    private var loading = true

    private let allFonts: [String] = NSFontManager.shared.availableFontFamilies
        .filter { !$0.hasPrefix(".") }
        .sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
    private var shownFonts: [String] = []
    private let search = NSSearchField()
    private let table = NSTableView()
    private var swatches: [(SwatchView, String)] = []

    init(settings: WidgetSettings, changed: @escaping (WidgetSettings) -> Void, finished: @escaping (Bool) -> Void) {
        s = settings
        self.changed = changed
        self.finished = finished
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 800, height: 560),
                         styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        w.title = "IP Widget – Text"
        w.minSize = NSSize(width: 700, height: 460)
        w.isReleasedWhenClosed = false
        w.level = .floating
        super.init(window: w)
        w.delegate = self
        build()
        w.center()
        loading = false
    }

    required init?(coder: NSCoder) { fatalError() }

    private func emit() {
        guard !loading else { return }
        changed(s)
    }

    // MARK: - layout

    private func header(_ t: String) -> NSTextField {
        let l = NSTextField(labelWithString: t)
        l.font = .systemFont(ofSize: NSFont.systemFontSize, weight: .semibold)
        return l
    }

    private func build() {
        guard let content = window?.contentView else { return }

        // ---- left: font list ----
        search.placeholderString = "Search fonts"
        search.delegate = self
        search.sendsSearchStringImmediately = true
        search.target = self
        search.action = #selector(searchChanged)

        let col = NSTableColumn(identifier: .init("font"))
        col.resizingMask = .autoresizingMask
        table.addTableColumn(col)
        table.headerView = nil
        table.rowHeight = 24
        table.dataSource = self
        table.delegate = self
        table.style = .plain
        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.hasVerticalScroller = true
        scroll.borderType = .bezelBorder

        let left = NSStackView(views: [header("Font  (type to search, ↑/↓ to browse)"), search, scroll])
        left.orientation = .vertical
        left.alignment = .leading
        left.spacing = 6
        search.widthAnchor.constraint(equalTo: left.widthAnchor).isActive = true
        scroll.widthAnchor.constraint(equalTo: left.widthAnchor).isActive = true
        scroll.setContentHuggingPriority(.defaultLow, for: .vertical)

        // ---- right ----
        let right = NSStackView()
        right.orientation = .vertical
        right.alignment = .leading
        right.spacing = 6
        right.addArrangedSubview(header("Size and style per field"))
        right.addArrangedSubview(fieldGrid())
        right.setCustomSpacing(14, after: right.arrangedSubviews.last!)
        right.addArrangedSubview(header("Color"))
        right.addArrangedSubview(colorRow())
        right.setCustomSpacing(14, after: right.arrangedSubviews.last!)
        right.addArrangedSubview(header("Hostname capitalization"))
        right.addArrangedSubview(caseBox())
        right.setCustomSpacing(14, after: right.arrangedSubviews.last!)
        right.addArrangedSubview(header("Spacing"))
        right.addArrangedSubview(gapSlider("Between rows", -60, 40, s.rowGap) { [weak self] v in self?.s.rowGap = v })
        right.addArrangedSubview(gapSlider("Between columns", -40, 80, s.columnGap) { [weak self] v in self?.s.columnGap = v })
        right.setContentHuggingPriority(.required, for: .horizontal)

        // ---- buttons ----
        let hint = NSTextField(labelWithString: "Changes show on the widget straight away. Cancel or Esc undoes them.")
        hint.font = .systemFont(ofSize: 11)
        hint.textColor = .secondaryLabelColor
        let cancel = NSButton(title: "Cancel", target: self, action: #selector(cancelPressed))
        cancel.keyEquivalent = "\u{1b}"
        let ok = NSButton(title: "OK", target: self, action: #selector(okPressed))
        ok.keyEquivalent = "\r"
        for b in [cancel, ok] { b.widthAnchor.constraint(greaterThanOrEqualToConstant: 90).isActive = true }
        let spacer = NSView()
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        let buttons = NSStackView(views: [hint, spacer, cancel, ok])
        buttons.orientation = .horizontal

        for v in [left, right, buttons] {
            v.translatesAutoresizingMaskIntoConstraints = false
            content.addSubview(v)
        }
        NSLayoutConstraint.activate([
            left.topAnchor.constraint(equalTo: content.topAnchor, constant: 12),
            left.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 14),
            left.bottomAnchor.constraint(equalTo: buttons.topAnchor, constant: -12),
            left.widthAnchor.constraint(greaterThanOrEqualToConstant: 220),
            right.topAnchor.constraint(equalTo: content.topAnchor, constant: 12),
            right.leadingAnchor.constraint(equalTo: left.trailingAnchor, constant: 16),
            right.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -14),
            right.bottomAnchor.constraint(lessThanOrEqualTo: buttons.topAnchor, constant: -12),
            buttons.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 14),
            buttons.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -14),
            buttons.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -12),
        ])

        fillFonts("")
        selectCurrentFont()
        window?.initialFirstResponder = search
    }

    // MARK: - per-field grid

    private struct FieldRow {
        let label: String
        let size: WritableKeyPath<WidgetSettings, Double>
        let style: WritableKeyPath<WidgetSettings, FieldStyle>
    }

    private let fieldRows: [FieldRow] = [
        FieldRow(label: "Hostname", size: \.hostnameSize, style: \.hostnameStyle),
        FieldRow(label: "IP address", size: \.ipSize, style: \.ipStyle),
        FieldRow(label: "MAC address", size: \.macSize, style: \.macStyle),
        FieldRow(label: "Type / name", size: \.typeSize, style: \.typeStyle),
        FieldRow(label: "Speed", size: \.speedSize, style: \.speedStyle),
    ]
    private var sizeBoxes: [NSComboBox] = []

    private func small(_ t: String) -> NSTextField {
        let l = NSTextField(labelWithString: t)
        l.font = .systemFont(ofSize: 11)
        l.textColor = .secondaryLabelColor
        return l
    }

    private func fieldGrid() -> NSView {
        var rows: [[NSView]] = [[NSGridCell.emptyContentView, small("Size"), small("Weight"),
                                 NSGridCell.emptyContentView, NSGridCell.emptyContentView]]
        for (i, f) in fieldRows.enumerated() {
            let size = NSComboBox()
            size.isEditable = true
            size.completes = false
            size.numberOfVisibleItems = 12
            size.addItems(withObjectValues: Look.sizePresets.map { fmt($0) })
            size.stringValue = fmt(s[keyPath: f.size])
            size.tag = i
            size.delegate = self
            size.target = self
            size.action = #selector(sizeCommitted(_:))
            size.widthAnchor.constraint(equalToConstant: 64).isActive = true
            sizeBoxes.append(size)

            let weight = NSPopUpButton()
            for w in Look.weights {
                weight.addItem(withTitle: w == "SemiBold" ? "Semibold" : w)
                weight.lastItem?.attributedTitle = NSAttributedString(
                    string: weight.lastItem!.title,
                    attributes: [.font: NSFont.systemFont(ofSize: NSFont.systemFontSize, weight: Look.systemWeight(w))])
                weight.lastItem?.representedObject = w
            }
            if let idx = Look.weights.firstIndex(where: { $0.caseInsensitiveCompare(s[keyPath: f.style].weight) == .orderedSame }) {
                weight.selectItem(at: idx)
            }
            weight.tag = i
            weight.target = self
            weight.action = #selector(weightChanged(_:))

            let italic = NSButton(checkboxWithTitle: "Italic", target: self, action: #selector(italicChanged(_:)))
            italic.attributedTitle = NSAttributedString(string: "Italic", attributes: [
                .font: NSFont(descriptor: NSFont.systemFont(ofSize: NSFont.systemFontSize).fontDescriptor.withSymbolicTraits(.italic),
                              size: NSFont.systemFontSize) ?? NSFont.systemFont(ofSize: NSFont.systemFontSize)])
            italic.state = s[keyPath: f.style].italic ? .on : .off
            italic.tag = i

            let underline = NSButton(checkboxWithTitle: "Underline", target: self, action: #selector(underlineChanged(_:)))
            underline.attributedTitle = NSAttributedString(string: "Underline", attributes: [
                .underlineStyle: NSUnderlineStyle.single.rawValue, .font: NSFont.systemFont(ofSize: NSFont.systemFontSize)])
            underline.state = s[keyPath: f.style].underline ? .on : .off
            underline.tag = i

            rows.append([NSTextField(labelWithString: f.label), size, weight, italic, underline])
        }
        let g = NSGridView(views: rows)
        g.rowSpacing = 4
        g.columnSpacing = 8
        g.rowAlignment = .firstBaseline
        return g
    }

    private func fmt(_ v: Double) -> String {
        v.rounded() == v ? String(Int(v)) : String(format: "%.1f", v)
    }

    @objc private func sizeCommitted(_ sender: NSComboBox) { commitSize(sender) }

    func comboBoxSelectionDidChange(_ notification: Notification) {
        guard let box = notification.object as? NSComboBox, box.indexOfSelectedItem >= 0,
              let v = box.objectValueOfSelectedItem as? String else { return }
        box.stringValue = v
        commitSize(box)
    }

    func controlTextDidEndEditing(_ obj: Notification) {
        if let box = obj.object as? NSComboBox { commitSize(box) }
    }

    func controlTextDidChange(_ obj: Notification) {
        if obj.object as? NSSearchField === search { fillFonts(search.stringValue) }
        else if let box = obj.object as? NSComboBox { commitSize(box) } // live while typing
    }

    private func commitSize(_ box: NSComboBox) {
        guard box.tag < fieldRows.count, let v = Double(box.stringValue.replacingOccurrences(of: ",", with: ".")),
              v >= 4, v <= 200 else { return }
        let kp = fieldRows[box.tag].size
        if abs(v - s[keyPath: kp]) > 0.001 {
            s[keyPath: kp] = v
            emit()
        }
    }

    @objc private func weightChanged(_ sender: NSPopUpButton) {
        guard let w = sender.selectedItem?.representedObject as? String else { return }
        s[keyPath: fieldRows[sender.tag].style].weight = w
        emit()
    }

    @objc private func italicChanged(_ sender: NSButton) {
        s[keyPath: fieldRows[sender.tag].style].italic = sender.state == .on
        emit()
    }

    @objc private func underlineChanged(_ sender: NSButton) {
        s[keyPath: fieldRows[sender.tag].style].underline = sender.state == .on
        emit()
    }

    // MARK: - colour

    private func colorRow() -> NSView {
        let row = NSStackView()
        row.orientation = .horizontal
        row.spacing = 6
        for (name, hex) in Look.colorPresets {
            let sw = SwatchView(color: Look.color(hex))
            sw.toolTip = name
            sw.onClick = { [weak self] in
                self?.s.textColor = hex
                self?.markSwatches()
                self?.emit()
            }
            swatches.append((sw, hex))
            row.addArrangedSubview(sw)
        }
        let custom = NSButton(title: "Custom…", target: self, action: #selector(customColor))
        custom.bezelStyle = .rounded
        row.addArrangedSubview(custom)
        markSwatches()
        return row
    }

    private func markSwatches() {
        for (sw, hex) in swatches { sw.selected = hex.caseInsensitiveCompare(s.textColor) == .orderedSame }
    }

    @objc private func customColor() {
        let p = NSColorPanel.shared
        p.showsAlpha = false
        p.isContinuous = true
        p.setTarget(self)
        p.setAction(#selector(colorPicked(_:)))
        p.color = Look.color(s.textColor)
        p.level = .floating
        p.orderFront(nil)
    }

    @objc private func colorPicked(_ sender: NSColorPanel) {
        s.textColor = Look.hex(sender.color)
        markSwatches()
        emit()
    }

    // MARK: - capitalization & spacing

    private func caseBox() -> NSView {
        let p = NSPopUpButton()
        let options = [("upper", "UPPERCASE"), ("title", "Capitalized"), ("lower", "lowercase"), ("asis", "As named on this Mac")]
        for (key, label) in options {
            p.addItem(withTitle: "\(label)   (\(Hostname.formatted(key)))")
            p.lastItem?.representedObject = key
            if s.hostnameCase == key { p.select(p.lastItem) }
        }
        p.target = self
        p.action = #selector(caseChanged(_:))
        p.widthAnchor.constraint(greaterThanOrEqualToConstant: 260).isActive = true
        return p
    }

    @objc private func caseChanged(_ sender: NSPopUpButton) {
        guard let key = sender.selectedItem?.representedObject as? String else { return }
        s.hostnameCase = key
        emit()
    }

    private var sliderSetters: [ObjectIdentifier: (Double) -> Void] = [:]
    private var sliderLabels: [ObjectIdentifier: NSTextField] = [:]

    private func gapSlider(_ label: String, _ min: Double, _ max: Double, _ value: Double,
                           set: @escaping (Double) -> Void) -> NSView {
        let name = NSTextField(labelWithString: label)
        name.widthAnchor.constraint(equalToConstant: 120).isActive = true
        let slider = NSSlider(value: value, minValue: min, maxValue: max, target: self, action: #selector(sliderMoved(_:)))
        slider.isContinuous = true
        slider.widthAnchor.constraint(equalToConstant: 240).isActive = true
        let v = NSTextField(labelWithString: String(Int(value.rounded())))
        v.alignment = .right
        v.widthAnchor.constraint(equalToConstant: 36).isActive = true
        sliderSetters[ObjectIdentifier(slider)] = set
        sliderLabels[ObjectIdentifier(slider)] = v
        let row = NSStackView(views: [name, slider, v])
        row.orientation = .horizontal
        return row
    }

    @objc private func sliderMoved(_ sender: NSSlider) {
        let v = sender.doubleValue.rounded()
        sender.doubleValue = v
        sliderLabels[ObjectIdentifier(sender)]?.stringValue = String(Int(v))
        sliderSetters[ObjectIdentifier(sender)]?(v)
        emit()
    }

    // MARK: - font list

    @objc private func searchChanged() { fillFonts(search.stringValue) }

    private func fillFonts(_ filter: String) {
        let f = filter.trimmingCharacters(in: .whitespaces)
        var list: [String] = []
        if f.isEmpty || Self.defaultFontLabel.localizedCaseInsensitiveContains(f) { list.append(Self.defaultFontLabel) }
        list += allFonts.filter { f.isEmpty || $0.localizedCaseInsensitiveContains(f) }
        let wasLoading = loading
        loading = true // rebuilding the list isn't a font choice
        shownFonts = list
        table.reloadData()
        selectCurrentFont()
        loading = wasLoading
    }

    private func selectCurrentFont() {
        let current = s.fontFamily ?? Self.defaultFontLabel
        if let i = shownFonts.firstIndex(of: current) {
            table.selectRowIndexes([i], byExtendingSelection: false)
            table.scrollRowToVisible(i)
        } else {
            table.deselectAll(nil)
        }
    }

    func numberOfRows(in tableView: NSTableView) -> Int { shownFonts.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let id = NSUserInterfaceItemIdentifier("fontCell")
        let cell = (tableView.makeView(withIdentifier: id, owner: nil) as? NSTextField) ?? {
            let t = NSTextField(labelWithString: "")
            t.identifier = id
            t.lineBreakMode = .byTruncatingTail
            return t
        }()
        let name = shownFonts[row]
        cell.stringValue = name
        cell.font = name == Self.defaultFontLabel
            ? .systemFont(ofSize: 15)
            : (NSFontManager.shared.font(withFamily: name, traits: [], weight: 5, size: 15) ?? .systemFont(ofSize: 15))
        return cell
    }

    func tableViewSelectionDidChange(_ notification: Notification) {
        guard !loading, table.selectedRow >= 0, table.selectedRow < shownFonts.count else { return }
        let name = shownFonts[table.selectedRow]
        s.fontFamily = name == Self.defaultFontLabel ? nil : name
        emit()
    }

    /// ↑/↓ in the search field move through the list.
    func control(_ control: NSControl, textView: NSTextView, doCommandBy sel: Selector) -> Bool {
        guard control === search, !shownFonts.isEmpty else { return false }
        let delta: Int
        switch sel {
        case #selector(NSResponder.moveDown(_:)): delta = 1
        case #selector(NSResponder.moveUp(_:)): delta = -1
        case #selector(NSResponder.cancelOperation(_:)): cancelPressed(); return true
        case #selector(NSResponder.insertNewline(_:)): okPressed(); return true
        default: return false
        }
        let next = max(0, min(shownFonts.count - 1, table.selectedRow + delta))
        table.selectRowIndexes([next], byExtendingSelection: false)
        table.scrollRowToVisible(next)
        return true
    }

    // MARK: - OK / Cancel

    @objc private func okPressed() { finish(true) }
    @objc private func cancelPressed() { finish(false) }

    override func cancelOperation(_ sender: Any?) { finish(false) }

    private func finish(_ ok: Bool) {
        guard !done else { return }
        done = true
        // Commit a size that's still being typed.
        if ok { sizeBoxes.forEach(commitSize) ; changed(s) }
        if NSColorPanel.sharedColorPanelExists { NSColorPanel.shared.orderOut(nil) }
        window?.close()
        finished(ok)
    }

    func windowWillClose(_ notification: Notification) {
        if !done { done = true; finished(false) }
    }
}

/// A clickable colour square with a blue ring when selected.
final class SwatchView: NSView {
    let color: NSColor
    var selected = false { didSet { needsDisplay = true } }
    var onClick: (() -> Void)?

    init(color: NSColor) {
        self.color = color
        super.init(frame: NSRect(x: 0, y: 0, width: 26, height: 26))
        widthAnchor.constraint(equalToConstant: 26).isActive = true
        heightAnchor.constraint(equalToConstant: 26).isActive = true
    }

    required init?(coder: NSCoder) { fatalError() }

    override func draw(_ dirtyRect: NSRect) {
        let p = NSBezierPath(roundedRect: bounds.insetBy(dx: 1.5, dy: 1.5), xRadius: 4, yRadius: 4)
        color.setFill()
        p.fill()
        (selected ? NSColor.controlAccentColor : NSColor.gray).setStroke()
        p.lineWidth = selected ? 3 : 1
        p.stroke()
    }

    override func mouseDown(with event: NSEvent) { onClick?() }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .pointingHand) }
}
