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

		static readonly IntPtr setHasShadowSel = Sel("setHasShadow:");
		static readonly IntPtr addChildWindowSel = Sel("addChildWindow:ordered:");
		static readonly IntPtr removeChildWindowSel = Sel("removeChildWindow:");
		static readonly IntPtr setFrameSel = Sel("setFrame:display:");
		static readonly IntPtr orderOutSel = Sel("orderOut:");
		static readonly IntPtr orderFrontSel = Sel("orderFront:");
		static readonly IntPtr frameSel = Sel("frame");

		[StructLayout(LayoutKind.Sequential)]
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
	}
}
