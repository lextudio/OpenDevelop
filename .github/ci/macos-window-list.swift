import Cocoa

// Prints the main screen's frame and visible frame (what the menu bar and Dock leave) and every on-screen window of at
// least 100x100 points with its owner, layer and bounds in the top-left-origin screen coordinates cliclick uses. The
// macOS pointer journeys compute click points from the window geometry WPF reports; this shows the geometry the window
// server actually has, so the two can be compared when a click lands nowhere.
let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
guard let windows = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else { exit(1) }
let screen = NSScreen.main!
print("screen frame=\(screen.frame) visible=\(screen.visibleFrame) scale=\(screen.backingScaleFactor)")
for window in windows {
    let owner = window[kCGWindowOwnerName as String] as? String ?? "?"
    let layer = window[kCGWindowLayer as String] as? Int ?? 0
    let bounds = window[kCGWindowBounds as String] as? [String: CGFloat] ?? [:]
    guard let width = bounds["Width"], let height = bounds["Height"], width >= 100, height >= 100 else { continue }
    print("\(owner) layer=\(layer) x=\(Int(bounds["X"] ?? 0)) y=\(Int(bounds["Y"] ?? 0)) w=\(Int(width)) h=\(Int(height))")
}
