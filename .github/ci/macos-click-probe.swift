import Cocoa

// Shows one window, prints where its centre is in the top-left-origin screen coordinates cliclick uses, and
// reports whether a real mouse-down reached it (the OS-level click delivery the pointer journeys depend on).
final class Catcher: NSView {
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) {
        print("DOWN window=\(event.locationInWindow) app-active=\(NSApp.isActive)"); fflush(stdout)
        NSApp.terminate(nil)
    }
}

setbuf(stdout, nil)
let app = NSApplication.shared
app.setActivationPolicy(.regular)
let screen = NSScreen.main!.frame
let frame = NSRect(x: 200, y: 200, width: 300, height: 300)
let window = NSWindow(contentRect: frame, styleMask: [.titled], backing: .buffered, defer: false)
window.contentView = Catcher(frame: NSRect(origin: .zero, size: frame.size))
window.title = "click probe"
window.makeKeyAndOrderFront(nil)
app.activate(ignoringOtherApps: true)
DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
    let f = window.frame
    let topLeftY = screen.height - (f.origin.y + f.height / 2)
    print("READY \(Int(f.origin.x + f.width / 2)) \(Int(topLeftY)) screen=\(Int(screen.width))x\(Int(screen.height)) active=\(app.isActive) key=\(window.isKeyWindow)")
}
DispatchQueue.main.asyncAfter(deadline: .now() + 40) { print("TIMEOUT"); NSApp.terminate(nil) }
app.run()
