using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ICSharpCode.Core;
using ICSharpCode.WinUIXamlDesigner;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>
/// Native-live mode for the Surface runtime: the renderer hosts the document in a real window, and the shell puts that window,
/// as a child of the IDE's main window, over the design canvas. The snapshot canvas underneath stays the fallback: leaving
/// native mode (or the renderer dying) removes the window and the last snapshot shows again. Windows only.
/// </summary>
sealed partial class UnoDesignRuntimeHost : IWinUIXamlNativeLive
{
	nint nativeWindow;

	public bool IsNativeLive => nativeWindow != 0;

	public string SetNativeLive(bool enabled)
	{
		if (!OperatingSystem.IsWindows())
			return Result(false, "native-live is Windows only");
		if (client == null)
			return Result(false, "the renderer is not started");
		try {
			if (enabled) {
				if (nativeWindow != 0)
					return Result(true, "already native");
				var reply = client.NativeLiveAsync(true).GetAwaiter().GetResult();
				if (!reply.Native || reply.Hwnd == 0)
					return Result(false, reply.Message ?? "the renderer stayed on snapshots");
				nativeWindow = (nint)reply.Hwnd;
				string placed = Place(nativeWindow);
				surface.LayoutUpdated += OnCanvasLayout;   // follow scroll, zoom and resize of the canvas
				return Result(true, placed);
			}
			if (nativeWindow == 0)
				return Result(false, "not native");
			surface.LayoutUpdated -= OnCanvasLayout;
			Detach(nativeWindow);
			nativeWindow = 0;
			var left = client.NativeLiveAsync(false).GetAwaiter().GetResult();
			return Result(false, left.Message ?? "back on snapshots");
		} catch (Exception e) {
			// A lost renderer: drop the window handle and fall back to the last snapshot.
			LoggingService.Warn("WinUI native-live failed", e);
			surface.LayoutUpdated -= OnCanvasLayout;
			if (nativeWindow != 0) { Detach(nativeWindow); nativeWindow = 0; }
			return Result(false, "the renderer was lost; showing the last snapshot (" + e.GetBaseException().Message + ")");
		}
	}

	(int X, int Y, int W, int H) lastPlacement;

	void OnCanvasLayout(object sender, EventArgs e)
	{
		if (nativeWindow != 0 && IsWindow(nativeWindow))
			Place(nativeWindow);
	}

	string Result(bool native, string message)
		=> JsonSerializer.Serialize(new { native, message, hwnd = (long)nativeWindow });

	// ---- Win32 placement ------------------------------------------------------------------------------------------------

	const int GWL_STYLE = -16;
	const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
	const uint SWP_NOZORDER = 0x0004, SWP_SHOWWINDOW = 0x0040, SWP_FRAMECHANGED = 0x0020;
	const int DWMWA_CLOAK = 13;

	string Place(nint hwnd)
	{
		var main = Process.GetCurrentProcess().MainWindowHandle;
		if (main == 0)
			return "no main window";
		var geometry = surface.SurfaceGeometry();
		var frame = geometry.Frame;
		// A child window paints over its siblings, so it must not extend past what the user can see of the canvas
		// (the frame can be larger than the pane and scrolled).
		if (!geometry.Viewport.IsEmpty)
			frame.Intersect(geometry.Viewport);
		if (frame.IsEmpty)
			return "no canvas bounds";
		// Screen rectangle of the rendered design (the geometry probe's coordinates), made relative to the main window.
		var origin = new POINT { X = (int)Math.Round(frame.X), Y = (int)Math.Round(frame.Y) };
		ScreenToClient(main, ref origin);
		int width = (int)Math.Round(frame.Width), height = (int)Math.Round(frame.Height);

		// LayoutUpdated fires often; only touch the window when its rectangle actually changed.
		if (lastPlacement == (origin.X, origin.Y, width, height) && GetParent(hwnd) == main)
			return "unchanged";
		lastPlacement = (origin.X, origin.Y, width, height);

		int cloak = 0;
		DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));   // the renderer keeps its window cloaked and off-screen
		SetParent(hwnd, main);
		uint style = GetWindowLong(hwnd, GWL_STYLE);
		style = (style & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME)) | WS_CHILD | WS_VISIBLE;
		SetWindowLong(hwnd, GWL_STYLE, style);
		SetWindowPos(hwnd, 0, origin.X, origin.Y, width, height, SWP_NOZORDER | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
		return $"placed {width}x{height} at {origin.X},{origin.Y} in the main window";
	}

	static void Detach(nint hwnd)
	{
		if (!IsWindow(hwnd))
			return;
		ShowWindow(hwnd, 0);
		SetParent(hwnd, 0);
	}

	[StructLayout(LayoutKind.Sequential)]
	struct POINT { public int X, Y; }

	[DllImport("user32.dll")] static extern bool IsWindow(nint hwnd);
	[DllImport("user32.dll")] static extern nint GetParent(nint hwnd);
	[DllImport("user32.dll")] static extern nint SetParent(nint child, nint parent);
	[DllImport("user32.dll")] static extern bool ScreenToClient(nint hwnd, ref POINT point);
	[DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int command);
	[DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern uint GetWindowLong(nint hwnd, int index);
	[DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern uint SetWindowLong(nint hwnd, int index, uint value);
	[DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
	[DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
