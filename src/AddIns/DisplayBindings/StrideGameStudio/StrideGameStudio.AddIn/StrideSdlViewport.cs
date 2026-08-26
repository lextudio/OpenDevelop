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
		System.Collections.Generic.IReadOnlyList<SceneAssetReader.EntityMarker> pendingEntities;

		/// <summary>Gap 2, small-first slice: real entity markers from the loaded session's
		/// scene asset, replacing the synthetic placeholder scene. Safe to call before or after
		/// the game has started (queues until <see cref="StartGame"/> creates it).</summary>
		public void SetEntities(System.Collections.Generic.IReadOnlyList<SceneAssetReader.EntityMarker> entities)
		{
			pendingEntities = entities;
			game?.SetEntities(entities);
		}

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
			if (pendingEntities != null)
				game.SetEntities(pendingEntities);

			sdlNsWindow = SdlNativeWindow.GetCocoaNsWindow(sdlWindow);
			if (sdlNsWindow == IntPtr.Zero)
				throw new InvalidOperationException("SDL_GetWindowWMInfo returned no Cocoa NSWindow handle.");

			// Strip the macOS title bar / rounded corners so the overlay reads as a flat content
			// pane inside the document tab, not a native window floating on top of it.
			CocoaOverlayInterop.MakeBorderless(sdlNsWindow);
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

			// Compute this element's position WITHIN the host content area, in WPF client
			// coordinates (title-bar independent - both points are the same PointToScreen space).
			var hostClientTopLeft = hostWindow.PointToScreen(new Point(0, 0));
			var viewportTopLeft = PointToScreen(new Point(0, 0));
			var offsetX = viewportTopLeft.X - hostClientTopLeft.X;
			var offsetY = viewportTopLeft.Y - hostClientTopLeft.Y;
			var w = ActualWidth;
			var h = ActualHeight;

			// Anchor to the host's CONTENT view screen rect (excludes title bar), not the full
			// window frame - using GetFrame's frame here would shift the overlay up by the title-bar
			// height and cover the document-tab text above it.
			var content = CocoaOverlayInterop.GetContentViewScreenRect(hostNsWindow);
			if (content.W <= 0 || content.H <= 0)
				return;
			var screenX = content.X + offsetX;
			var screenY = content.Y + (content.H - offsetY - h);

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
