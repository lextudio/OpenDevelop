// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// macOS-only Cocoa glue for the "overlay child window" composition bridge documented in
// doc/technotes/stride-game-studio.md ("Composition-bridge probes"): SDL is given its own
// completely normal top-level NSWindow (never a foreign NSView - that path segfaults inside
// MoltenVK, see the technote), and that window is pinned over a WPF placeholder element via
// Cocoa's own `addChildWindow:`/`setFrame:display:` - the same technique LibreWPF's
// SilkNetWpfWindowDecorationService.cs already uses for popup positioning.

using System;
using System.Runtime.InteropServices;

namespace ICSharpCode.StrideGameStudio
{
	static class CocoaOverlayInterop
	{
		const string ObjCLib = "/usr/lib/libobjc.A.dylib";

		[DllImport(ObjCLib, EntryPoint = "sel_registerName")]
		static extern IntPtr Sel(string name);

		[DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
		static extern void SendVoidBool(IntPtr recv, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool a1);

		[DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
		static extern void SendVoidPtrLong(IntPtr recv, IntPtr sel, IntPtr a1, long a2);

		[DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
		static extern void SendSetFrame(IntPtr recv, IntPtr sel, NSRect r, [MarshalAs(UnmanagedType.I1)] bool display);

		[DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
		static extern void SendVoidPtr(IntPtr recv, IntPtr sel, IntPtr a1);

		[DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
		static extern NSRect SendGetRect(IntPtr recv, IntPtr sel);

		[DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
		[return: MarshalAs(UnmanagedType.I1)]
		static extern bool SendGetBool(IntPtr recv, IntPtr sel);

		[DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
		static extern void SendVoidUlong(IntPtr recv, IntPtr sel, ulong a1);

		[DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
		static extern void SendVoidNoArg(IntPtr recv, IntPtr sel);

		[DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
		static extern long SendGetLong(IntPtr recv, IntPtr sel);

		[DllImport(ObjCLib, EntryPoint = "objc_getClass")]
		static extern IntPtr GetClass(string name);

		[DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
		static extern long SendWindowNumberAtPoint(IntPtr cls, IntPtr sel, NSPoint point, long belowWindowNumber);

		// NSWindowStyleMaskBorderless = 0 - removes the title bar, close/minimize buttons,
		// resizable frame AND the macOS rounded corners / shadow, so the overlay reads as a flat
		// content pane inside the document tab instead of a floating window.
		static readonly IntPtr setStyleMaskSel = Sel("setStyleMask:");
		static readonly IntPtr setHasShadowSel = Sel("setHasShadow:");
		static readonly IntPtr contentLayoutRectSel = Sel("contentLayoutRect");
		static readonly IntPtr addChildWindowSel = Sel("addChildWindow:ordered:");
		static readonly IntPtr removeChildWindowSel = Sel("removeChildWindow:");
		static readonly IntPtr setFrameSel = Sel("setFrame:display:");
		static readonly IntPtr orderOutSel = Sel("orderOut:");
		static readonly IntPtr orderFrontSel = Sel("orderFront:");
		static readonly IntPtr frameSel = Sel("frame");
		static readonly IntPtr isKeyWindowSel = Sel("isKeyWindow");
		static readonly IntPtr makeKeyWindowSel = Sel("makeKeyWindow");
		static readonly IntPtr windowNumberSel = Sel("windowNumber");
		static readonly IntPtr windowNumberAtPointSel = Sel("windowNumberAtPoint:belowWindowWithWindowNumber:");
		static readonly IntPtr ignoresMouseEventsSel = Sel("ignoresMouseEvents");
		static readonly IntPtr setIgnoresMouseEventsSel = Sel("setIgnoresMouseEvents:");
		static readonly IntPtr isMainWindowSel = Sel("isMainWindow");

		[StructLayout(LayoutKind.Sequential)]
		public struct NSPoint
		{
			public double X, Y;
			public NSPoint(double x, double y) { X = x; Y = y; }
		}

		public struct NSRect
		{
			public double X, Y, W, H;
			public NSRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
		}

		public static void AddChildWindow(IntPtr hostNsWindow, IntPtr childNsWindow)
		{
			SendVoidBool(childNsWindow, setHasShadowSel, false);
			SendVoidPtrLong(hostNsWindow, addChildWindowSel, childNsWindow, 1 /* NSWindowAbove */);
		}

		/// <summary>Strip the macOS window chrome (title bar, rounded corners) from the
		/// SDL-owned window so it presents as a flat pane instead of a native window. Only the
		/// <see cref="setStyleMask:"/> change (NSWindowStyleMaskBorderless) is needed; the shadow
		/// is separately disabled by <see cref="AddChildWindow"/>.</summary>
		public static void MakeBorderless(IntPtr nsWindow)
			=> SendVoidUlong(nsWindow, setStyleMaskSel, 0 /* NSWindowStyleMaskBorderless */);

		/// <summary>Restores a titled style mask. Diagnostic counterpart to <see cref="MakeBorderless"/>:
		/// AppKit refuses key status to a borderless window by default, so if clicks only work with a
		/// title bar present, the chrome-stripping is what costs the overlay its clicks.</summary>
		public static void MakeTitled(IntPtr nsWindow)
			=> SendVoidUlong(nsWindow, setStyleMaskSel, 1 /* NSWindowStyleMaskTitled */);

		public static void RemoveChildWindow(IntPtr hostNsWindow, IntPtr childNsWindow)
		{
			SendVoidPtr(hostNsWindow, removeChildWindowSel, childNsWindow);
		}

		/// <summary>
		/// Sets the child window's on-screen frame directly in Cocoa screen coordinates
		/// (bottom-left origin, points). Callers translate from WPF's top-left-origin screen
		/// coordinates - see <see cref="StrideSdlViewport.Reposition"/>.
		/// </summary>
		public static void SetFrame(IntPtr nsWindow, double x, double y, double w, double h)
			=> SendSetFrame(nsWindow, setFrameSel, new NSRect(x, y, w, h), true);

		public static void OrderOut(IntPtr nsWindow) => SendVoidPtr(nsWindow, orderOutSel, IntPtr.Zero);

		public static void OrderFront(IntPtr nsWindow) => SendVoidPtr(nsWindow, orderFrontSel, IntPtr.Zero);

		public static NSRect GetFrame(IntPtr nsWindow) => SendGetRect(nsWindow, frameSel);

		/// <summary>Whether the window currently receives keyboard input. A child window attached via
		/// <see cref="AddChildWindow"/> is NOT key by default, which is what would keep keyboard events
		/// from reaching a game hosted in one.</summary>
		public static bool IsKeyWindow(IntPtr nsWindow) => SendGetBool(nsWindow, isKeyWindowSel);

		public static bool IsMainWindow(IntPtr nsWindow) => SendGetBool(nsWindow, isMainWindowSel);

		/// <summary>Makes the window the key window, so it receives keyboard input and its view is in
		/// the mouse event path again after something else took key.</summary>
		public static void MakeKey(IntPtr nsWindow) => SendVoidNoArg(nsWindow, makeKeyWindowSel);

		/// <summary>Whether the window is transparent to clicks. A window with this set still lets the
		/// cursor sit over it (so a client polling global mouse state still tracks position) while the
		/// OS hit-tests clicks straight through to whatever is underneath - which looks exactly like
		/// "motion arrives but buttons never do".</summary>
		public static bool GetIgnoresMouseEvents(IntPtr nsWindow) => SendGetBool(nsWindow, ignoresMouseEventsSel);

		/// <summary>The window's server-side number, for identity comparisons against
		/// <see cref="WindowNumberAtPoint"/>.</summary>
		public static long GetWindowNumber(IntPtr nsWindow) => SendGetLong(nsWindow, windowNumberSel);

		/// <summary>Asks the window server which window is frontmost at a screen point - i.e. the one
		/// that would receive a click there. Comparing this against the overlay's own window number
		/// settles "who actually gets the click" without guessing at z-order.</summary>
		public static long WindowNumberAtPoint(double x, double y)
			=> SendWindowNumberAtPoint(GetClass("NSWindow"), windowNumberAtPointSel, new NSPoint(x, y), 0);

		public static void SetIgnoresMouseEvents(IntPtr nsWindow, bool value) => SendVoidBool(nsWindow, setIgnoresMouseEventsSel, value);

		/// <summary>Returns the window's CONTENT area on-screen rect in Cocoa screen coordinates
		/// (bottom-left origin, points) - i.e. the client area, excluding the title bar / chrome.
		/// Anchoring the overlay to this (instead of <see cref="GetFrame"/>'s full window frame)
		/// is what keeps the overlay aligned to the WPF content regardless of title-bar height.
		/// Uses <c>contentLayoutRect</c> (no-arg struct return, same ABI-safe shape as
		/// <c>frame</c>) rather than <c>convertRectToScreen:</c>, whose struct-arg + struct-return
		/// <c>objc_msgSend</c> signature crashes on arm64 (measured NSException).</summary>
		public static NSRect GetContentViewScreenRect(IntPtr nsWindow)
		{
			var frame = SendGetRect(nsWindow, frameSel);
			var layout = SendGetRect(nsWindow, contentLayoutRectSel);
			return new NSRect(frame.X + layout.X, frame.Y + layout.Y, layout.W, layout.H);
		}
	}
}
