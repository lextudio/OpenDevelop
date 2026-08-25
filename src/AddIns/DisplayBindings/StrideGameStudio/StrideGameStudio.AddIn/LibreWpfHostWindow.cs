// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Resolves a WPF Window's real Cocoa NSWindow handle under LibreWPF, using LibreWPF.ProGPU's
// public diagnostics entry point - see doc/technotes/stride-game-studio.md "Composition-bridge
// probes". This is the LibreWPF-side counterpart to SdlNativeWindow (the SDL/Stride side).
//
// Note: ProGpuWpfDiagnostics is diagnostics-surface naming, not a hardened public contract - if
// a future LibreWPF.ProGPU version renames or removes it, this is the one place to fix.

using System;
using System.Windows;

using System.Windows.Media.ProGPU;

namespace ICSharpCode.StrideGameStudio
{
	static class LibreWpfHostWindow
	{
		public static bool TryGetCocoaNsWindow(Window window, out IntPtr nsWindow)
		{
			nsWindow = IntPtr.Zero;
			if (!ProGpuWpfDiagnostics.TryGetWindowHost(window, out var host) || host?.SilkWindow == null)
				return false;

			var native = host.SilkWindow.Native;
			if (native?.Cocoa is not { } cocoa || cocoa == IntPtr.Zero)
				return false;

			nsWindow = cocoa;
			return true;
		}
	}
}
