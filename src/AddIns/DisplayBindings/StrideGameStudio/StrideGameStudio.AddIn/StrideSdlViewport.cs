// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Fusion milestone 3: the WINDOWED Stride viewport, replacing StrideHeadlessViewport's
// headless+CPU-readback route (proven unviable - leaking/crashing GPU->CPU copy, see
// doc/technotes/stride-game-studio.md "G4 viewport stability/perf investigation"). The GPU now
// presents directly to a real native window; no pixels cross to the CPU.
//
// Composition bridge (see the technote's "Composition-bridge probes"): SDL owns a completely
// normal top-level window (never a foreign NSView - that segfaults inside MoltenVK). Its native
// NSWindow is extracted via SDL_GetWindowWMInfo and attached to the host WPF window via Cocoa
// `addChildWindow:`, then kept pinned over this element's on-screen rect on every layout change -
// the same technique LibreWPF's own SilkNetWpfWindowDecorationService.cs uses for popups.
//
// Threading: macOS requires Cocoa/SDL window creation and event pumping to happen on the main
// thread (confirmed by the technote's probes - a background-thread Game.Run() never receives
// window events). Stride's GameContextSDL(isUserManagingRun: true) + Game.Tick() lets the whole
// thing run driven by CompositionTarget.Rendering on the WPF UI thread (the process main thread)
// instead of Game.Run()'s own blocking loop, so it never fights the WPF dispatcher.

using System;
using System.Windows;
using System.Windows.Media;

using Stride.Games;
using SdlWindow = Stride.Graphics.SDL.Window;
using Stride.Graphics.SDL;

namespace ICSharpCode.StrideGameStudio
{
	public sealed class StrideSdlViewport : FrameworkElement, IDisposable
	{
		SdlWindow sdlWindow;
		GameContextSDL context;
		SdlOverlayGame game;
		IntPtr sdlNsWindow;
		IntPtr hostNsWindow;
		bool attached;
		bool running;
		readonly Brush background = Brushes.Black;

		public StrideSdlViewport()
		{
			// FrameworkElement paints nothing itself; the pixels come from the native overlay
			// window sitting on top of this element's screen rect. A background brush still
			// matters for the moment the overlay hasn't attached yet (host not in a window,
			// or on a platform where the Cocoa bridge doesn't apply).
			Loaded += OnLoaded;
			Unloaded += OnUnloaded;
			SizeChanged += (_, _) => Reposition();
			IsVisibleChanged += (_, _) => Reposition();
		}

		protected override void OnRender(DrawingContext drawingContext)
		{
			drawingContext.DrawRectangle(background, null, new Rect(RenderSize));
		}

		void OnLoaded(object sender, RoutedEventArgs e)
		{
			if (running)
				return;
			if (!OperatingSystem.IsMacOS())
			{
				ICSharpCode.Core.LoggingService.Warn("[StrideSdlViewport] windowed overlay is macOS-only (Cocoa addChildWindow bridge); no viewport on this platform yet.");
				return;
			}

			try
			{
				StartGame();
				AttachOverlay();
				CompositionTarget.Rendering += OnRendering;
				running = true;
			}
			catch (Exception ex)
			{
				ICSharpCode.Core.LoggingService.Error("[StrideSdlViewport] failed to start: " + ex);
				Teardown();
			}
		}

		void OnUnloaded(object sender, RoutedEventArgs e)
		{
			if (!running)
				return;
			CompositionTarget.Rendering -= OnRendering;
			running = false;
			Teardown();
		}

		void StartGame()
		{
			var w = Math.Max(1, (int)ActualWidth);
			var h = Math.Max(1, (int)ActualHeight);
			if (w <= 1 || h <= 1) { w = 640; h = 360; }

			sdlWindow = new SdlWindow("Stride viewport", IntPtr.Zero);
			context = new GameContextSDL(sdlWindow, w, h, isUserManagingRun: true);
			game = new SdlOverlayGame(w, h);
			game.Run(context); // returns immediately: IsUserManagingRun defers the loop to us

			sdlNsWindow = SdlNativeWindow.GetCocoaNsWindow(sdlWindow);
			if (sdlNsWindow == IntPtr.Zero)
				throw new InvalidOperationException("SDL_GetWindowWMInfo returned no Cocoa NSWindow handle.");
		}

		void AttachOverlay()
		{
			var hostWindow = System.Windows.Window.GetWindow(this);
			if (hostWindow == null)
			{
				ICSharpCode.Core.LoggingService.Warn("[StrideSdlViewport] not yet parented to a Window; overlay not attached.");
				return;
			}

			if (!LibreWpfHostWindow.TryGetCocoaNsWindow(hostWindow, out hostNsWindow) || hostNsWindow == IntPtr.Zero)
			{
				ICSharpCode.Core.LoggingService.Warn("[StrideSdlViewport] could not resolve the host window's native NSWindow (LibreWPF.ProGPU diagnostics); overlay not attached.");
				return;
			}

			CocoaOverlayInterop.AddChildWindow(hostNsWindow, sdlNsWindow);
			attached = true;
			Reposition();
		}

		void Reposition()
		{
			if (!attached || sdlNsWindow == IntPtr.Zero || hostNsWindow == IntPtr.Zero)
				return;

			if (!IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
			{
				CocoaOverlayInterop.OrderOut(sdlNsWindow);
				return;
			}

			var hostWindow = System.Windows.Window.GetWindow(this);
			if (hostWindow == null)
				return;

			// WPF screen coordinates are top-left origin (like Win32); Cocoa's NSWindow.frame is
			// bottom-left origin. Rather than reasoning about title-bar/chrome insets, take the
			// DELTA between this element's screen origin and the host window's own screen origin
			// in WPF's coordinate space, then apply that delta to the host's own Cocoa frame -
			// this way title bar/chrome height is accounted for automatically.
			var hostTopLeft = hostWindow.PointToScreen(new Point(0, 0));
			var viewportTopLeft = PointToScreen(new Point(0, 0));
			var offsetX = viewportTopLeft.X - hostTopLeft.X;
			var offsetY = viewportTopLeft.Y - hostTopLeft.Y;
			var w = ActualWidth;
			var h = ActualHeight;

			var hostFrame = CocoaOverlayInterop.GetFrame(hostNsWindow);
			var screenX = hostFrame.X + offsetX;
			var screenY = hostFrame.Y + (hostFrame.H - offsetY - h);

			CocoaOverlayInterop.SetFrame(sdlNsWindow, screenX, screenY, w, h);
			CocoaOverlayInterop.OrderFront(sdlNsWindow);
			game?.Resize((int)w, (int)h);
		}

		void OnRendering(object sender, EventArgs e)
		{
			if (!running || context == null)
				return;
			Stride.Graphics.SDL.Application.ProcessEvents();
			context.RunCallback?.Invoke();
		}

		void Teardown()
		{
			attached = false;
			try
			{
				if (game != null && context != null)
				{
					game.Exit();
					context.RunCallback?.Invoke();
					context.ExitCallback?.Invoke();
				}
			}
			catch (Exception ex)
			{
				ICSharpCode.Core.LoggingService.Warn("[StrideSdlViewport] teardown: " + ex.Message);
			}

			if (hostNsWindow != IntPtr.Zero && sdlNsWindow != IntPtr.Zero)
				CocoaOverlayInterop.RemoveChildWindow(hostNsWindow, sdlNsWindow);
			if (sdlNsWindow != IntPtr.Zero)
				CocoaOverlayInterop.OrderOut(sdlNsWindow);

			game?.Dispose();
			game = null;
			context = null;
			sdlWindow = null;
			sdlNsWindow = IntPtr.Zero;
			hostNsWindow = IntPtr.Zero;
		}

		public void Dispose()
		{
			if (running)
			{
				CompositionTarget.Rendering -= OnRendering;
				running = false;
			}
			Teardown();
		}
	}
}
