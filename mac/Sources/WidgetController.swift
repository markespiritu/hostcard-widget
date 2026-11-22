import AppKit
import ServiceManagement

/// Borderless, non-activating panel that never becomes key or main.
final class WidgetPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

final class WidgetController: NSObject, NSWindowDelegate, NSMenuDelegate {
    private var settings = WidgetSettings.load()
    private let panel: WidgetPanel
    private let view = WidgetView()
    private var watcher: NetworkWatcher?
    private var keyTimer: Timer?
    private var refreshTimer: Timer?
    private var debounce: DispatchWorkItem?
    private var interactive = false
    private var menuOpen = false
    private var dialog: SettingsWindowController?
    private var candidates: [InterfaceInfo] = []
    /// The app that was in front before the menu/dialog took the keyboard; it gets focus back afterwards.
    private var previousApp: NSRunningApplication?

    /// Just above the Finder's desktop icons, below every normal window.
    private static let desktopLevel = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopIconWindow)) + 1)

    override init() {
        panel = WidgetPanel(contentRect: NSRect(x: 0, y: 0, width: 200, height: 60),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        super.init()
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.isFloatingPanel = false
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.ignoresMouseEvents = true
        panel.isMovable = true
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenAuxiliary]
        panel.title = "IP Widget"
        panel.delegate = self
        applyLevel()

        view.onRightClick = { [weak self] e in self?.showMenu(e) }
        view.onDragEnded = { [weak self] in self?.savePosition() }
        panel.contentView = view

        view.settings = settings
        refresh()
        place()
        panel.orderFrontRegardless()

        watcher = NetworkWatcher { [weak self] in self?.scheduleRefresh() }
        keyTimer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in self?.pollKeys() }
        // Wi-Fi rate moves around; the watcher covers address/link changes.
        refreshTimer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in self?.refresh() }
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                               object: nil, queue: .main) { [weak self] _ in self?.clampOnScreen() }
    }

    // MARK: - position

    private func applyLevel() {
        panel.level = (settings.keepOnTop || dialog != nil) ? .floating : Self.desktopLevel
    }

    private func resizeKeepingTopLeft() {
        let size = view.intrinsicContentSize
        let f = panel.frame
        let topLeft = NSPoint(x: f.minX, y: f.maxY)
        guard f.size != size else { return }
        panel.setFrame(NSRect(x: topLeft.x, y: topLeft.y - size.height, width: size.width, height: size.height), display: true)
    }

    private func place() {
        let size = view.intrinsicContentSize
        if let l = settings.left, let t = settings.top {
            let r = NSRect(x: l, y: t - size.height, width: size.width, height: size.height)
            if isOnScreen(r) {
                panel.setFrame(r, display: true)
                return
            }
        }
        placeDefault()
    }

    private func placeDefault() {
        let size = view.intrinsicContentSize
        let area = NSScreen.screens.first?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        panel.setFrame(NSRect(x: area.maxX - size.width - 24, y: area.maxY - 24 - size.height,
                              width: size.width, height: size.height), display: true)
    }

    /// At least 40 pt of the widget must be on some screen.
    private func isOnScreen(_ r: NSRect) -> Bool {
        NSScreen.screens.contains { s in
            let i = s.frame.intersection(r)
            return !i.isNull && i.width >= min(40, r.width) && i.height >= min(40, r.height)
        }
    }

    private func clampOnScreen() {
        if !isOnScreen(panel.frame) { placeDefault() }
    }

    private func savePosition() {
        settings.left = Double(panel.frame.minX)
        settings.top = Double(panel.frame.maxY)
        settings.save()
    }

    func windowDidMove(_ notification: Notification) {
        if interactive { savePosition() }
    }

    // MARK: - Shift+Option = interactive

    private func pollKeys() {
        let flags = NSEvent.modifierFlags.intersection(.deviceIndependentFlagsMask)
        let held = flags.contains(.shift) && flags.contains(.option)
        let want = held || menuOpen || NSEvent.pressedMouseButtons & 1 != 0 && interactive
        guard want != interactive else { return }
        interactive = want
        panel.ignoresMouseEvents = !want
        view.interactive = want
    }

    // MARK: - data

    private func scheduleRefresh() {
        debounce?.cancel()
        let w = DispatchWorkItem { [weak self] in self?.refresh() }
        debounce = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 1, execute: w)
    }

    func refresh() {
        candidates = NetworkInfo.candidates()
        let shown: [InterfaceInfo]
        if settings.interfaces.isEmpty {
            shown = candidates.filter { $0.isUp }
        } else {
            let wanted = Set(settings.interfaces)
            shown = candidates.filter { wanted.contains($0.bsdName) }
        }
        if shown != view.rows { view.rows = shown }
        resizeKeepingTopLeft()
    }

    private func applySettings() {
        view.settings = settings
        resizeKeepingTopLeft()
    }

    // MARK: - menu

    private func showMenu(_ event: NSEvent) {
        let menu = buildMenu()
        menu.delegate = self
        menuOpen = true
        // Activate so the menu gets the keyboard (Esc closes it). LSUIElement: no Dock icon appears.
        rememberFrontApp()
        NSApp.activate(ignoringOtherApps: true)
        menu.popUp(positioning: nil, at: view.convert(event.locationInWindow, from: nil), in: view)
    }

    func menuDidClose(_ menu: NSMenu) {
        guard menu.supermenu == nil else { return }
        menuOpen = false
        // Give focus back unless the menu opened the dialog.
        DispatchQueue.main.async { [weak self] in if self?.dialog == nil { self?.restoreFrontApp() } }
    }

    private func rememberFrontApp() {
        if let front = NSWorkspace.shared.frontmostApplication, front != NSRunningApplication.current {
            previousApp = front
        }
    }

    private func restoreFrontApp() {
        if let p = previousApp, !p.isTerminated { p.activate() } else { NSApp.deactivate() }
        previousApp = nil
    }

    private func item(_ title: String, checked: Bool = false, enabled: Bool = true, action: @escaping () -> Void) -> NSMenuItem {
        let i = ClosureMenuItem(title: title, handler: action)
        i.state = checked ? .on : .off
        i.isEnabled = enabled
        return i
    }

    private func buildMenu() -> NSMenu {
        let m = NSMenu()
        m.autoenablesItems = false
        let header = NSMenuItem(title: "IP Widget — \(Hostname.asNamed)", action: nil, keyEquivalent: "")
        header.isEnabled = false
        m.addItem(header)
        m.addItem(.separator())

        // Interfaces
        let ifaces = NSMenu()
        ifaces.autoenablesItems = false
        ifaces.addItem(item("Automatic (all active)", checked: settings.interfaces.isEmpty) { [weak self] in
            self?.settings.interfaces = []
            self?.settingsChanged(refresh: true)
        })
        ifaces.addItem(.separator())
        for c in candidates {
            let state = c.isUp ? (c.ip ?? "connected") : "disconnected"
            let on = settings.interfaces.isEmpty ? c.isUp : settings.interfaces.contains(c.bsdName)
            let it = item("\(c.displayName) (\(c.bsdName)) — \(state)", checked: on) { [weak self] in
                self?.toggleInterface(c.bsdName)
            }
            it.image = NSImage(systemSymbolName: c.kind == .wifi ? "wifi" : "cable.connector", accessibilityDescription: nil)
                ?? NSImage(systemSymbolName: "network", accessibilityDescription: nil)
            ifaces.addItem(it)
        }
        let ifItem = NSMenuItem(title: "Interfaces", action: nil, keyEquivalent: "")
        ifItem.submenu = ifaces
        m.addItem(ifItem)

        m.addItem(item("Keep on top of other windows", checked: settings.keepOnTop) { [weak self] in
            guard let self else { return }
            settings.keepOnTop.toggle()
            settingsChanged()
            applyLevel()
        })

        m.addItem(fieldsMenu())
        m.addItem(ipFormatMenu())

        let text = item("Font, style, color and spacing…") { [weak self] in
            DispatchQueue.main.async { self?.openTextSettings() }
        }
        text.attributedTitle = NSAttributedString(string: text.title,
                                                  attributes: [.font: NSFont.menuFont(ofSize: 0).withWeight(.semibold)])
        m.addItem(text)

        m.addItem(item("Show background", checked: settings.showBackground) { [weak self] in
            self?.settings.showBackground.toggle()
            self?.settingsChanged()
        })
        m.addItem(item("Text shadow (when background is off)", checked: settings.textShadow,
                       enabled: !settings.showBackground) { [weak self] in
            self?.settings.textShadow.toggle()
            self?.settingsChanged()
        })

        let login = SMAppService.mainApp.status
        let loginTitle = login == .requiresApproval ? "Launch at login (approve in System Settings)" : "Launch at login"
        m.addItem(item(loginTitle, checked: login == .enabled) { [weak self] in self?.toggleLoginItem() })

        m.addItem(item("Refresh now") { [weak self] in self?.refresh() })
        m.addItem(item("Reset position") { [weak self] in
            guard let self else { return }
            settings.left = nil
            settings.top = nil
            settings.save()
            placeDefault()
        })
        m.addItem(.separator())
        m.addItem(item("Quit IP Widget") { NSApp.terminate(nil) })
        return m
    }

    private func toggleInterface(_ bsd: String) {
        // Coming from Automatic: start from what's showing now.
        var set = settings.interfaces.isEmpty ? candidates.filter { $0.isUp }.map(\.bsdName) : settings.interfaces
        if let i = set.firstIndex(of: bsd) { set.remove(at: i) } else { set.append(bsd) }
        settings.interfaces = set // empty again = back to Automatic
        settingsChanged(refresh: true)
    }

    private func fieldsMenu() -> NSMenuItem {
        typealias F = (String, WritableKeyPath<WidgetSettings, Bool>)
        let fields: [F] = [("Hostname", \.hideHostname), ("IP address", \.hideIp), ("MAC address", \.hideMac),
                           ("Type icon (Wi-Fi / Ethernet)", \.hideType), ("Interface name (en0)", \.hideBsdName),
                           ("Speed", \.hideSpeed)]
        let sub = NSMenu()
        sub.autoenablesItems = false
        let shown = fields.filter { !settings[keyPath: $0.1] }.count
        for (label, kp) in fields {
            let visible = !settings[keyPath: kp]
            // Don't allow hiding the last visible field, or the widget would be empty.
            sub.addItem(item(label, checked: visible, enabled: !(visible && shown == 1)) { [weak self] in
                self?.settings[keyPath: kp].toggle()
                self?.settingsChanged()
            })
        }
        let parent = NSMenuItem(title: "Show fields", action: nil, keyEquivalent: "")
        parent.submenu = sub
        return parent
    }

    private func ipFormatMenu() -> NSMenuItem {
        let sample = view.rows.first(where: { $0.ip != nil })?.ip ?? "192.168.26.214"
        let p = sample.split(separator: ".")
        let sub = NSMenu()
        sub.autoenablesItems = false
        let options: [(String, String, String)] = [
            ("full", "Full", sample),
            ("last2", "Last two parts", p.count == 4 ? "\(p[2]).\(p[3])" : sample),
            ("last1", "Last part only", p.count == 4 ? ".\(p[3])" : sample),
        ]
        for (key, label, example) in options {
            sub.addItem(item("\(label)   \(example)", checked: settings.ipFormat == key) { [weak self] in
                self?.settings.ipFormat = key
                self?.settingsChanged()
            })
        }
        let parent = NSMenuItem(title: "IP address format", action: nil, keyEquivalent: "")
        parent.submenu = sub
        return parent
    }

    private func settingsChanged(refresh doRefresh: Bool = false) {
        settings.save()
        applySettings()
        if doRefresh { refresh() }
    }

    private func toggleLoginItem() {
        let svc = SMAppService.mainApp
        do {
            if svc.status == .enabled || svc.status == .requiresApproval {
                try svc.unregister()
            } else {
                try svc.register()
                if svc.status == .requiresApproval { SMAppService.openSystemSettingsLoginItems() }
            }
        } catch {
            let a = NSAlert()
            a.messageText = "Couldn't change Launch at login"
            a.informativeText = error.localizedDescription
            NSApp.activate(ignoringOtherApps: true)
            a.runModal()
        }
    }

    // MARK: - text dialog

    private func openTextSettings() {
        if let dialog { dialog.window?.makeKeyAndOrderFront(nil); return }
        let snapshot = settings
        let dlg = SettingsWindowController(settings: settings) { [weak self] s in
            // Live preview.
            self?.settings.copyTextSettings(from: s)
            self?.applySettings()
        } finished: { [weak self] ok in
            guard let self else { return }
            if ok {
                settings.save()
            } else {
                settings.copyTextSettings(from: snapshot)
            }
            dialog = nil
            applyLevel()
            applySettings()
            restoreFrontApp()
        }
        dialog = dlg
        rememberFrontApp()
        applyLevel() // lift the widget above windows so the preview is visible
        NSApp.activate(ignoringOtherApps: true)
        dlg.showWindow(nil)
        dlg.window?.makeKeyAndOrderFront(nil)
    }
}

/// NSMenuItem that runs a closure.
final class ClosureMenuItem: NSMenuItem {
    private let handler: () -> Void
    init(title: String, handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(fire), keyEquivalent: "")
        target = self
    }
    required init(coder: NSCoder) { fatalError() }
    @objc private func fire() { handler() }
}

extension NSFont {
    func withWeight(_ w: NSFont.Weight) -> NSFont {
        .systemFont(ofSize: pointSize, weight: w)
    }
}
