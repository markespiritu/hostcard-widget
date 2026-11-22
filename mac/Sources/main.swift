import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var controller: WidgetController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        // One copy is enough: it shows every interface. A second launch just quits.
        let me = ProcessInfo.processInfo.processIdentifier
        if let id = Bundle.main.bundleIdentifier,
           NSRunningApplication.runningApplications(withBundleIdentifier: id).contains(where: { $0.processIdentifier != me }) {
            NSApp.terminate(nil)
            return
        }
        controller = WidgetController()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
