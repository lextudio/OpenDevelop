// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Gap 2, big step: hosts the REAL Stride.Assets.Presentation.AssetEditors.SceneEditor.
// SceneEditorController/EditorGameController for a loaded scene asset - not the addin's own
// SdlOverlayGame placeholder. Reuses the exact composition bridge StrideSdlViewport proved out
// (SdlNativeWindow/LibreWpfHostWindow/CocoaOverlayInterop), just pointed at the real controller's
// SdlWindow/Tick() (added to the fork - see doc/technotes/stride-game-studio.md "gap 2, big step:
// wiring the real EditorGameController") instead of our own SdlOverlayGame.
//
// This gets meshes/materials/lighting/the real render pipeline for free - it's the actual editor
// engine, just without a WPF-hosted view around it (SceneEditorView.xaml etc. are not used here;
// this addin drives the controller directly). Selection/gizmos/undo are NOT wired yet - those
// live in EntityHierarchyEditorController's ~15 EditorGame*Service registrations, which run
// automatically once the controller starts, but nothing here surfaces their UI/input yet.

using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

using Vector2 = Stride.Core.Mathematics.Vector2;

using Stride.Assets.Presentation.AssetEditors.GameEditor.Services;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.ViewModels;
using SdlWindow = Stride.Graphics.SDL.Window;

namespace ICSharpCode.StrideGameStudio
{
	public sealed class StrideSceneEditorViewport : FrameworkElement, IDisposable
	{
		readonly Stride.Assets.Presentation.ViewModel.SceneViewModel sceneAsset;
		SceneEditorViewModel sceneEditor;
		IEditorGameController controller;
		SdlWindow sdlWindow;
		IntPtr sdlNsWindow;
		IntPtr hostNsWindow;

		/// <summary>
		/// The viewport currently hosting a running scene editor, or null. Exists for the DevFlow
		/// diagnostics action (see StrideGameStudioDevFlowActions): the scene renders into a separate
		/// native child window, which the host's WPF-based screenshot path cannot capture, so
		/// automated checks need a way to ask this object about its state directly.
		/// </summary>
		internal static StrideSceneEditorViewport Current { get; private set; }
		bool attached;
		bool running;
		// Button state is sampled from outside on a request boundary, but a synthetic click can be
		// pressed and released well inside one frame, so polling DownButtons always reads 0 and looks
		// identical to "the event never arrived". Latch the per-frame edges instead.
		/// <summary>
		/// A simulated input source whose mouse outranks the SDL one.
		///
		/// The editor services read <c>Game.Input.IsMouseButtonDown</c> / <c>MouseDelta</c>, which
		/// resolve through <c>InputManager.Mouse</c> - the first entry of the pointer list, which
		/// InputManager sorts by <c>Priority</c> descending **at registration time only**. So the
		/// priority has to be set before the device is registered; assigning it afterwards leaves the
		/// list ordered as it was. <c>InputSourceSimulated.AddMouse</c> constructs and registers in one
		/// step with the default priority of -1000 (real hardware should normally win), so registering
		/// the device here is the only way to get in between - and <c>RegisterDevice</c> is protected,
		/// which is why this is a subclass rather than a helper.
		/// </summary>
		sealed class PrimaryMouseInputSource : Stride.Input.InputSourceSimulated
		{
			public Stride.Input.MouseSimulated AddPrimaryMouse()
			{
				var mouse = new Stride.Input.MouseSimulated(this) { Priority = 1000 };
				RegisterDevice(mouse);
				return mouse;
			}

			public Stride.Input.KeyboardSimulated AddPrimaryKeyboard()
			{
				var keyboard = new Stride.Input.KeyboardSimulated(this) { Priority = 1000 };
				RegisterDevice(keyboard);
				return keyboard;
			}
		}

		Stride.Input.InputSourceSimulated simulatedInput;
		Stride.Input.MouseSimulated simulatedMouse;
		Stride.Input.KeyboardSimulated simulatedKeyboard;
		int forwardedMoves;
		int forwardedDowns;
		int forwardedKeyDowns;
		int gotKeyboardFocusCount;
		int gameFrameCount;
		int gameSystemCount;
		string scriptSystemState = "?";
		bool editorHidden;
		bool gameFaulted;
		string lastFault = "";
		int observedButtonPresses;
		int observedSimulatedPresses;
		int observedKeyPresses;
		int tickCount;
		readonly Brush background = Brushes.Black;

		public StrideSceneEditorViewport(Stride.Assets.Presentation.ViewModel.SceneViewModel sceneAsset)
		{
			this.sceneAsset = sceneAsset ?? throw new ArgumentNullException(nameof(sceneAsset));
			Loaded += OnLoaded;
			Unloaded += OnUnloaded;
			SizeChanged += (_, _) => Reposition();
			IsVisibleChanged += (_, _) => Reposition();
		}

		protected override void OnRender(DrawingContext drawingContext)
			=> drawingContext.DrawRectangle(background, null, new Rect(RenderSize));

		async void OnLoaded(object sender, RoutedEventArgs e)
		{
			if (running)
				return;
			if (!OperatingSystem.IsMacOS())
			{
				ICSharpCode.Core.LoggingService.Warn("[StrideSceneEditorViewport] windowed overlay is macOS-only; no viewport on this platform yet.");
				return;
			}

			try
			{
				// Constructing SceneEditorViewModel constructs SceneEditorController (and
				// therefore EditorGameController) synchronously via the controller-factory
				// closure - it does not start the game yet, StartGame() below does.
				sceneEditor = new SceneEditorViewModel(sceneAsset);
				controller = sceneEditor.Controller;

				// Initialize(), not StartGame(): StartGame only brings the game up. The editor's own
				// sequence is StartGame -> await GameContentLoaded -> CreateScene -> OnGameContentLoaded,
				// and it is CreateScene that initializes the editor game services - which is what
				// registers their per-frame update through Game.Script.AddTask. Calling StartGame alone
				// gives a rendering scene whose services never tick, so input reaches the game and
				// nothing acts on it (camera, selection, gizmos all silent).
				//
				// Must run on this thread (the WPF UI thread = process main thread): SDL/Cocoa window
				// creation happens inside StartGame() on macOS - see the fork's
				// EditorGameController.StartGame() implementation notes.
				if (!await sceneEditor.Initialize())
					throw new InvalidOperationException("SceneEditorViewModel.Initialize() reported failure.");

				sdlWindow = (SdlWindow)controller.SdlWindow
					?? throw new InvalidOperationException("EditorGameController.StartGame() did not produce an SdlWindow.");
				sdlNsWindow = SdlNativeWindow.GetCocoaNsWindow(sdlWindow);
				if (sdlNsWindow == IntPtr.Zero)
					throw new InvalidOperationException("SDL_GetWindowWMInfo returned no Cocoa NSWindow handle.");
				CocoaOverlayInterop.MakeBorderless(sdlNsWindow);
				// The overlay presents frames; it does not take input. Clicks must fall through it to
				// the WPF element underneath, which forwards them via AttachInputForwarding - the
				// native window swallows mouse-down without producing an SDL button event (see the
				// technote's input section), so leaving it in the click path loses input entirely.
				CocoaOverlayInterop.SetIgnoresMouseEvents(sdlNsWindow, true);

				// The editor game starts out treated as hidden, and EditorServiceGame.Update throttles
				// itself to an occasional frame in that state - which stops GameSystems, and therefore
				// the ScriptSystem that every editor service's per-frame work is scheduled on, from
				// running. Real Game Studio calls this when the editor's tab becomes visible; this
				// viewport is visible as soon as it is loaded.
				controller.OnShowGame();

				AttachOverlay();
				AttachInputForwarding();
				CompositionTarget.Rendering += OnRendering;
				running = true;
				Current = this;
			}
			catch (Exception ex)
			{
				ICSharpCode.Core.LoggingService.Error("[StrideSceneEditorViewport] failed to start: " + ex);
				Teardown();
			}
		}

		/// <summary>
		/// Feeds this WPF element's mouse input into the game through Stride's own simulated input
		/// source.
		///
		/// The overlay is a real SDL window and Stride's SDL input source is wired to it, so motion
		/// arrives natively - but mouse buttons never do: a click at the overlay costs it both Cocoa
		/// key status and SDL input focus, and no button event is produced, with the window verified
		/// visible, focused, frontmost at that point and not click-through (see the technote's input
		/// section for the measurements and the hypotheses ruled out). WPF does receive those clicks,
		/// so forwarding from here sidesteps the native focus question entirely rather than fighting
		/// it. Motion is forwarded too, so both come from one source with one coordinate convention.
		/// </summary>
		void AttachInputForwarding()
		{
			var input = CurrentInputManager();
			if (input == null)
			{
				ICSharpCode.Core.LoggingService.Warn("[StrideSceneEditorViewport] no InputManager; mouse input will not be forwarded.");
				return;
			}

			simulatedInput = new PrimaryMouseInputSource();
			input.Sources.Add(simulatedInput);
			var source = (PrimaryMouseInputSource)simulatedInput;
			simulatedMouse = source.AddPrimaryMouse();
			// Keyboard too: the editor's camera reads modifiers through Game.Input.IsKeyDown - Alt+drag
			// orbits, Shift changes move speed - so mouse-only forwarding leaves those interactions
			// unreachable.
			simulatedKeyboard = source.AddPrimaryKeyboard();

			KeyDown += OnForwardKeyDown;
			KeyUp += OnForwardKeyUp;
			// Counting focus acquisition separately from key arrival is what distinguishes "the element
			// never got keyboard focus" from "focus is fine, no key event was ever produced" - both of
			// which otherwise show up only as wpfKeyDowns staying 0.
			GotKeyboardFocus += (_, _) => gotKeyboardFocusCount++;

			MouseMove += OnForwardMouseMove;
			MouseDown += OnForwardMouseDown;
			MouseUp += OnForwardMouseUp;
			MouseWheel += OnForwardMouseWheel;
			// Without this the element is not hit-testable and gets no mouse events at all: it draws
			// only a background rectangle, and a Panel-derived element with no content is transparent
			// to hit testing.
			Focusable = true;
		}

		/// <summary>Normalized (0..1) position of a mouse event within this element, which is the
		/// convention Stride's mouse devices use.</summary>
		Vector2 NormalizedPosition(System.Windows.Input.MouseEventArgs e)
		{
			var p = e.GetPosition(this);
			var w = ActualWidth > 0 ? ActualWidth : 1;
			var h = ActualHeight > 0 ? ActualHeight : 1;
			return new Vector2((float)(p.X / w), (float)(p.Y / h));
		}

		static Stride.Input.MouseButton ToStrideButton(System.Windows.Input.MouseButton b) => b switch
		{
			System.Windows.Input.MouseButton.Left => Stride.Input.MouseButton.Left,
			System.Windows.Input.MouseButton.Right => Stride.Input.MouseButton.Right,
			System.Windows.Input.MouseButton.Middle => Stride.Input.MouseButton.Middle,
			System.Windows.Input.MouseButton.XButton1 => Stride.Input.MouseButton.Extended1,
			_ => Stride.Input.MouseButton.Extended2,
		};

		void OnForwardMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
		{
			forwardedMoves++;
			simulatedMouse?.SetPosition(NormalizedPosition(e));
		}

		void OnForwardMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			forwardedDowns++;
			// Clicking the viewport should also give it keyboard focus - both because that is what a
			// user expects, and because the editor's camera reads modifier keys (Alt to orbit, Shift
			// for speed) that only arrive as WPF KeyDown once this element is focused.
			Focus();
			if (simulatedMouse == null)
				return;
			simulatedMouse.SetPosition(NormalizedPosition(e));
			simulatedMouse.SimulateMouseDown(ToStrideButton(e.ChangedButton));
			// Capture so a drag that leaves the element still reports its release, which is what the
			// editor's camera and gizmo interactions rely on.
			CaptureMouse();
		}

		void OnForwardMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			if (simulatedMouse == null)
				return;
			simulatedMouse.SetPosition(NormalizedPosition(e));
			simulatedMouse.SimulateMouseUp(ToStrideButton(e.ChangedButton));
			ReleaseMouseCapture();
		}

		void OnForwardMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
			=> simulatedMouse?.SimulateMouseWheel(e.Delta / 120f);

		/// <summary>Maps a WPF key to Stride's enum by name. The two enums use the same names for the
		/// keys an editor cares about, so a name lookup avoids a switch over ~100 members; unknown keys
		/// are dropped rather than guessed at.</summary>
		static Stride.Input.Keys? ToStrideKey(System.Windows.Input.Key key)
			=> Enum.TryParse<Stride.Input.Keys>(key.ToString(), out var k) ? k : null;

		void OnForwardKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
		{
			// WPF reports Alt as SystemKey with Key.System in Key; the real key is in SystemKey.
			var wpfKey = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
			if (ToStrideKey(wpfKey) is { } k)
			{
				forwardedKeyDowns++;
				simulatedKeyboard?.SimulateDown(k);
			}
		}

		void OnForwardKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
		{
			var wpfKey = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
			if (ToStrideKey(wpfKey) is { } k)
				simulatedKeyboard?.SimulateUp(k);
		}

		void OnUnloaded(object sender, RoutedEventArgs e)
		{
			if (!running)
				return;
			CompositionTarget.Rendering -= OnRendering;
			running = false;
			Teardown();
		}

		void AttachOverlay()
		{
			var hostWindow = Window.GetWindow(this);
			if (hostWindow == null)
			{
				ICSharpCode.Core.LoggingService.Warn("[StrideSceneEditorViewport] not yet parented to a Window; overlay not attached.");
				return;
			}

			if (!LibreWpfHostWindow.TryGetCocoaNsWindow(hostWindow, out hostNsWindow) || hostNsWindow == IntPtr.Zero)
			{
				ICSharpCode.Core.LoggingService.Warn("[StrideSceneEditorViewport] could not resolve the host window's native NSWindow; overlay not attached.");
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

			var hostWindow = Window.GetWindow(this);
			if (hostWindow == null)
				return;

			var hostTopLeft = hostWindow.PointToScreen(new Point(0, 0));
			var viewportTopLeft = PointToScreen(new Point(0, 0));
			var offsetX = viewportTopLeft.X - hostTopLeft.X;
			var offsetY = viewportTopLeft.Y - hostTopLeft.Y;
			var w = ActualWidth;
			var h = ActualHeight;

			var content = CocoaOverlayInterop.GetContentViewScreenRect(hostNsWindow);
			if (content.W <= 0 || content.H <= 0)
				return;
			var screenX = content.X + offsetX;
			var screenY = content.Y + (content.H - offsetY - h);

			CocoaOverlayInterop.SetFrame(sdlNsWindow, screenX, screenY, w, h);
			CocoaOverlayInterop.OrderFront(sdlNsWindow);
		}

		void OnRendering(object sender, EventArgs e)
		{
			if (!running || controller == null)
				return;
			controller.Tick();
			LatchInputEdges();
		}

		/// <summary>
		/// Accumulates input edges seen during ticks, so an out-of-band status query can tell whether
		/// button/key events ever reached the game even though the transient state is long gone by the
		/// time it asks.
		/// </summary>
		void LatchInputEdges()
		{
			tickCount++;
			try
			{
				var input = CurrentInputManager();
				if (input == null)
					return;
				// InputManager.PressedButtons forwards to Mouse.PressedButtons - the *primary* mouse,
				// which is the SDL device that registered first. The simulated device this addin feeds
				// is a second mouse, so its presses never appear there; read it directly.
				observedButtonPresses += input.PressedButtons.Count;
				if (simulatedMouse != null)
					observedSimulatedPresses += simulatedMouse.PressedButtons.Count;
				observedKeyPresses += input.PressedKeys.Count;
			}
			catch
			{
				// Diagnostics only - never let this disturb the frame loop.
			}
		}

		/// <summary>
		/// The editor camera's position, as the observable for whether forwarded input actually drives
		/// the editor services (a right-drag should orbit it). Reached through the private service
		/// registry by reflection - same reasoning as CurrentInputManager: diagnostics should not widen
		/// production API.
		/// </summary>
		string DescribeCamera()
		{
			try
			{
				// serviceRegistry is private on the generic base EditorGameController<T>; GetField on the
				// concrete controller type does not see base-class private fields, so walk up.
				object registry = null;
				for (var t = controller?.GetType(); t != null && registry == null; t = t.BaseType)
				{
					registry = t.GetField("serviceRegistry", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
						?.GetValue(controller);
				}
				var services = registry?.GetType().GetProperty("Services")?.GetValue(registry) as System.Collections.IEnumerable;
				if (services == null)
					return "{\"error\":\"no registry\"}";

				var serviceCount = 0;
				foreach (var s0 in services) serviceCount++;
				foreach (var svc in services)
				{
					var posProp = svc?.GetType().GetProperty("Position");
					if (posProp == null || posProp.PropertyType != typeof(Stride.Core.Mathematics.Vector3))
						continue;
					var pos = (Stride.Core.Mathematics.Vector3)posProp.GetValue(svc);
					// A look-around drag changes orientation while leaving Position untouched, so position
					// alone cannot distinguish "rotation worked" from "input was ignored".
					var rotProp = svc.GetType().GetProperty("Yaw") != null ? svc.GetType() : null;
					var yaw = (rotProp?.GetProperty("Yaw")?.GetValue(svc) as float?) ?? float.NaN;
					var pitch = (rotProp?.GetProperty("Pitch")?.GetValue(svc) as float?) ?? float.NaN;
					// IsControllingMouse / IsMouseAvailable say whether the service is running at all and
					// whether it thinks it may act - the difference between "input never reached it" and
					// "it saw the input and declined".
					var controlling = svc.GetType().GetProperty("IsControllingMouse")?.GetValue(svc) as bool?;
					var available = svc.GetType().GetProperty("IsMouseAvailable")?.GetValue(svc) as bool?;
					// IsInitialized is set by InitializeService, which is also what registers the
					// service's per-frame task - false here means the service exists but was never
					// started, which no amount of input can work around.
					// What UpdateCamera itself sees, from the fork's diagnostics counters - the one link in
				// the chain that reflection from out here cannot observe.
				var svcType = svc.GetType();
				string Diag(string name)
				{
					for (var t = svcType; t != null; t = t.BaseType)
						if (t.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) is { } v)
							return v.ToString().ToLowerInvariant();
					return "?";
				}
				var initialized = svc.GetType().GetProperty("IsInitialized")?.GetValue(svc) as bool?;
					var active = svc.GetType().GetProperty("IsActive")?.GetValue(svc) as bool?;
					return string.Format(System.Globalization.CultureInfo.InvariantCulture,
						"{{\"svc\":\"{0}\",\"x\":{1:F3},\"y\":{2:F3},\"z\":{3:F3},\"controlling\":{4}," +
						"\"available\":{5},\"initialized\":{6},\"active\":{7},\"serviceCount\":{8}," +
						"\"updateCalls\":\"{9}\",\"sawAnyBtn\":\"{10}\",\"sawRightBtn\":\"{11}\"," +
						"\"yaw\":{12:F4},\"pitch\":{13:F4}}}",
						svc.GetType().Name, pos.X, pos.Y, pos.Z,
						controlling == true ? "true" : "false",
						available == true ? "true" : "false",
						initialized == true ? "true" : "false",
						active == true ? "true" : "false",
						serviceCount,
						Diag("DiagUpdateCount"), Diag("DiagSawAnyButton"), Diag("DiagSawRightButton"),
						yaw, pitch);
				}
				return "{\"error\":\"no camera service\"}";
			}
			catch (Exception ex)
			{
				return "{\"error\":\"" + ex.GetType().Name + "\"}";
			}
		}

		Stride.Input.InputManager CurrentInputManager()
		{
			var game = controller?.GetType()
				.GetProperty("Game", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
				?.GetValue(controller);
			return game?.GetType().GetProperty("Input")?.GetValue(game) as Stride.Input.InputManager;
		}

		void Teardown()
		{
			if (ReferenceEquals(Current, this))
				Current = null;
			attached = false;
			if (hostNsWindow != IntPtr.Zero && sdlNsWindow != IntPtr.Zero)
				CocoaOverlayInterop.RemoveChildWindow(hostNsWindow, sdlNsWindow);
			if (sdlNsWindow != IntPtr.Zero)
				CocoaOverlayInterop.OrderOut(sdlNsWindow);

			MouseMove -= OnForwardMouseMove;
			MouseDown -= OnForwardMouseDown;
			MouseUp -= OnForwardMouseUp;
			MouseWheel -= OnForwardMouseWheel;
			KeyDown -= OnForwardKeyDown;
			KeyUp -= OnForwardKeyUp;
			simulatedKeyboard = null;
			simulatedMouse = null;
			simulatedInput = null;

			(controller as IDisposable)?.Dispose();
			controller = null;
			sceneEditor = null;
			sdlWindow = null;
			sdlNsWindow = IntPtr.Zero;
			hostNsWindow = IntPtr.Zero;
		}

		/// <summary>
		/// Re-asserts the overlay's front z-order and key status. Diagnostic entry point for the
		/// question of whether the overlay needs this maintained continuously: after a click the
		/// overlay was observed to stop receiving mouse events entirely, which would be explained by
		/// something else taking key and the child window dropping out of the event path.
		/// </summary>
		internal void ReassertOverlay()
		{
			if (sdlNsWindow == IntPtr.Zero)
				return;
			// Show() through SDL, not just Cocoa: SDL gates input routing on its own shown flag, which
			// orderFront: alone leaves untouched.
			sdlWindow?.Show();
			CocoaOverlayInterop.OrderFront(sdlNsWindow);
			CocoaOverlayInterop.MakeKey(sdlNsWindow);
			CocoaOverlayInterop.SetIgnoresMouseEvents(sdlNsWindow, true);
		}

		/// <summary>
		/// Drives the simulated devices directly to perform a camera gesture, and reports the camera
		/// position before and after.
		///
		/// This exists because the remaining open question - do the editor game services act on input
		/// that reaches InputManager? - cannot be asked through the test harness: DevFlow injects real
		/// mouse events for the left button only and keyboard only semantically, while every camera
		/// gesture needs a right/middle button or a modifier. The WPF-to-simulated-device half of the
		/// path is already verified separately, so driving the devices from here tests exactly the part
		/// that is still unknown and nothing else.
		/// </summary>
		/// <param name="gesture">"orbit" (Alt+left drag), "rotate" (right drag) or "zoom" (wheel).</param>
		internal string SimulateCameraGesture(string gesture)
		{
			if (simulatedMouse == null || simulatedKeyboard == null)
				return "{\"error\":\"input forwarding not attached\"}";

			var before = DescribeCamera();

			switch (gesture)
			{
				case "rotate":
					simulatedMouse.SetPosition(new Vector2(0.5f, 0.5f));
					simulatedMouse.SimulateMouseDown(Stride.Input.MouseButton.Right);
					break;
				case "orbit":
					simulatedKeyboard.SimulateDown(Stride.Input.Keys.LeftAlt);
					simulatedMouse.SetPosition(new Vector2(0.5f, 0.5f));
					simulatedMouse.SimulateMouseDown(Stride.Input.MouseButton.Left);
					break;
				case "zoom":
					simulatedMouse.SimulateMouseWheel(3f);
					break;
				default:
					return "{\"error\":\"unknown gesture\"}";
			}

			// Feed motion across several frames: the camera integrates MouseDelta per frame, and a
			// single jump would be consumed as one frame's delta at most.
			var midButtons = "";
			var midDelta = "";
			var sawControlling = false;
			for (var i = 1; i <= 8; i++)
			{
				Tick();
				var im = CurrentInputManager();
				if (im != null)
				{
					// Sampled inside the gesture, because DownButtons/MouseDelta are per-frame state
					// that is gone by the time the action returns - the same sampling trap that made
					// the button forwarding look broken earlier.
					if (midButtons.Length == 0 && im.DownButtons.Count > 0)
						midButtons = string.Join("+", im.DownButtons);
					if (midDelta.Length == 0 && (im.MouseDelta.X != 0 || im.MouseDelta.Y != 0))
						midDelta = string.Format(System.Globalization.CultureInfo.InvariantCulture,
							"{0:F4},{1:F4}", im.MouseDelta.X, im.MouseDelta.Y);
				}
				if (DescribeCamera().Contains("\"controlling\":true"))
					sawControlling = true;
				simulatedMouse.SetPosition(new Vector2(0.5f + 0.04f * i, 0.5f + 0.01f * i));
			}
			Tick();

			if (gesture == "rotate")
				simulatedMouse.SimulateMouseUp(Stride.Input.MouseButton.Right);
			else if (gesture == "orbit")
			{
				simulatedMouse.SimulateMouseUp(Stride.Input.MouseButton.Left);
				simulatedKeyboard.SimulateUp(Stride.Input.Keys.LeftAlt);
			}
			Tick();

			return "{\"gesture\":\"" + gesture + "\",\"midButtons\":\"" + midButtons + "\",\"midDelta\":\"" + midDelta
				+ "\",\"sawControlling\":" + (sawControlling ? "true" : "false")
				+ ",\"before\":" + before + ",\"after\":" + DescribeCamera() + "}";

			void Tick()
			{
				controller?.Tick();
				LatchInputEdges();
			}
		}

		/// <summary>Diagnostic: give the overlay a titled style mask again, to test whether the
		/// borderless style is what makes AppKit refuse it key status on click.</summary>
		internal void MakeTitledForDiagnostics()
		{
			if (sdlNsWindow == IntPtr.Zero)
				return;
			CocoaOverlayInterop.MakeTitled(sdlNsWindow);
			CocoaOverlayInterop.OrderFront(sdlNsWindow);
			CocoaOverlayInterop.MakeKey(sdlNsWindow);
		}

		/// <summary>
		/// Detaches the overlay from the host's child-window relationship while leaving it on screen.
		/// Diagnostic discriminator for the lost-click problem: if buttons start arriving once the
		/// window is no longer a child, the parent/child relationship is what costs it key status on
		/// mouse-down; if they still do not, the host is reassigning key regardless.
		/// </summary>
		internal void DetachOverlay()
		{
			if (hostNsWindow == IntPtr.Zero || sdlNsWindow == IntPtr.Zero)
				return;
			CocoaOverlayInterop.RemoveChildWindow(hostNsWindow, sdlNsWindow);
			attached = false;
			CocoaOverlayInterop.OrderFront(sdlNsWindow);
			CocoaOverlayInterop.MakeKey(sdlNsWindow);
		}

		/// <summary>
		/// Reports overlay state as JSON for the DevFlow diagnostics action. Coordinates are Cocoa
		/// screen coordinates (bottom-left origin) - the same space synthetic OS-level pointer events
		/// are aimed in, so a caller can target the viewport centre without guessing.
		/// </summary>
		internal string DescribeForDevFlow()
		{
			var frame = sdlNsWindow != IntPtr.Zero
				? CocoaOverlayInterop.GetFrame(sdlNsWindow)
				: default;
			var isKey = sdlNsWindow != IntPtr.Zero && CocoaOverlayInterop.IsKeyWindow(sdlNsWindow);
			var isMain = sdlNsWindow != IntPtr.Zero && CocoaOverlayInterop.IsMainWindow(sdlNsWindow);
			var ignoresMouse = sdlNsWindow != IntPtr.Zero && CocoaOverlayInterop.GetIgnoresMouseEvents(sdlNsWindow);
			// Who does the window server say owns the overlay's centre point? If this is not our own
			// window number, clicks there are going somewhere else no matter what the z-order looks like.
			// What SDL itself believes about focus, as opposed to what Cocoa reports: SDL routes key
			// events to the window it considers input-focused, and that flag is maintained from
			// notifications SDL observes - which it can miss if key status is changed behind its back.
			var sdlFocused = sdlWindow?.Focused ?? false;
			// SDL's own idea of whether the window is shown. The window is created with the Hidden
			// flag and this addin only ever brings it on screen through Cocoa's orderFront:, never
			// through SDL's Show() - so SDL can still consider it hidden, and SDL does not route
			// input to windows it thinks are not shown.
			var sdlVisible = sdlWindow?.Visible ?? false;
			var myNum = sdlNsWindow != IntPtr.Zero ? CocoaOverlayInterop.GetWindowNumber(sdlNsWindow) : 0;
			var hostNum = hostNsWindow != IntPtr.Zero ? CocoaOverlayInterop.GetWindowNumber(hostNsWindow) : 0;
			var numAtCentre = frame.W > 0
				? CocoaOverlayInterop.WindowNumberAtPoint(frame.X + frame.W / 2, frame.Y + frame.H / 2)
				: 0;

			return string.Format(System.Globalization.CultureInfo.InvariantCulture,
				"{{\"running\":{0},\"attached\":{1},\"hasController\":{2},\"sdlWindow\":{3}," +
				"\"frame\":{{\"x\":{4},\"y\":{5},\"w\":{6},\"h\":{7}}}," +
				"\"isKeyWindow\":{8},\"isMainWindow\":{9},\"wpfSize\":{{\"w\":{10},\"h\":{11}}}," +
				"\"ignoresMouseEvents\":{13},\"windowNumber\":{14},\"hostWindowNumber\":{15}," +
				"\"windowNumberAtCentre\":{16},\"sdlFocused\":{17},\"sdlVisible\":{18},\"input\":{12}}}",
				running ? "true" : "false",
				attached ? "true" : "false",
				controller != null ? "true" : "false",
				sdlNsWindow != IntPtr.Zero ? "true" : "false",
				frame.X, frame.Y, frame.W, frame.H,
				isKey ? "true" : "false",
				isMain ? "true" : "false",
				ActualWidth, ActualHeight,
				DescribeInput(),
				ignoresMouse ? "true" : "false",
				myNum, hostNum, numAtCentre,
				sdlFocused ? "true" : "false",
				sdlVisible ? "true" : "false");
		}

		/// <summary>
		/// Reports the editor game's InputManager state, which is the only way to observe whether
		/// synthetic OS-level pointer events actually reach the game: the scene lives in a native
		/// child window that the WPF screenshot path cannot capture, and nothing on the input path
		/// logs. Reached by reflection on purpose - the controller exposes its game only through an
		/// internal interface, and widening production API for a diagnostics-only need is the wrong
		/// trade; a diagnostics probe degrading to "unavailable" if that shape changes is acceptable.
		/// </summary>
		string DescribeInput()
		{
			try
			{
				if (controller == null)
					return "null";

				var gameProp = controller.GetType().GetProperty("Game",
					System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
				var game = gameProp?.GetValue(controller);
				if (game == null)
					return "{\"error\":\"no Game\"}";

				var input = game.GetType().GetProperty("Input")?.GetValue(game) as Stride.Input.InputManager;
				if (input == null)
					return "{\"error\":\"no InputManager\"}";

				// The game window's own view of its size, to tell apart "the overlay is positioned
				// wrong" from "the game resized its window": ClientBounds is logical, ScaleFactor is
				// DrawableSize/ClientSize, so a scale of 1 here while the native frame is 2x the WPF
				// element means SDL is reporting pixels as logical size.
				// The game's own frame counter, versus our tick counter: if ours advances and this does
				// not, our Tick is not actually driving Game.Update - and nothing registered with the
				// script system (which is where every editor service's per-frame work lives) can run.
				editorHidden = game.GetType().GetProperty("IsEditorHidden")?.GetValue(game) as bool? ?? false;
				// EditorServiceGame.Update returns immediately and forever once Faulted is set, which
				// stops every GameSystem - including the ScriptSystem the editor services live on -
				// while the outer frame counter keeps climbing.
				gameFaulted = game.GetType().GetProperty("Faulted")?.GetValue(game) as bool? ?? false;
				// EditorGameRecoveryService stores whatever faulted the game on the editor view model,
				// which is the only place the exception survives - OnFault marks it handled.
				if (gameFaulted && sceneEditor?.LastException is { } lastEx)
					lastFault = lastEx.ToString().Replace("\\", "/").Replace("\"", "'").Replace("\r", " ").Replace("\n", " | ");
				var updateTime = game.GetType().GetProperty("UpdateTime")?.GetValue(game) as Stride.Games.GameTime;
				gameFrameCount = (int)(updateTime?.FrameCount ?? 0);

				// Is the ScriptSystem actually in the game's update list, and enabled? Every editor
				// service's per-frame work is a task scheduled on it, so a ScriptSystem that is absent
				// or disabled explains initialized-and-active services that never run.
				scriptSystemState = "absent";
				if (game.GetType().GetProperty("GameSystems")?.GetValue(game) is System.Collections.IEnumerable systems)
				{
					var total = 0;
					foreach (var sys in systems)
					{
						total++;
						if (sys is not Stride.Engine.Processors.ScriptSystem script)
							continue;
						// Microthread states, not just the count: 14 scheduled threads that are all
						// parked in the same state tell a very different story from 14 that are cycling.
						var states = new System.Collections.Generic.Dictionary<string, int>();
						foreach (var mt in script.Scheduler?.MicroThreads ?? (System.Collections.Generic.ICollection<Stride.Core.MicroThreading.MicroThread>)System.Array.Empty<Stride.Core.MicroThreading.MicroThread>())
						{
							var key = mt.State.ToString();
							states[key] = states.TryGetValue(key, out var n) ? n + 1 : 1;
						}
						scriptSystemState = string.Format(System.Globalization.CultureInfo.InvariantCulture,
							"enabled={0},scheduled={1},states=[{2}]",
							script.Enabled ? "true" : "false",
							script.Scheduler?.MicroThreads.Count ?? -1,
							string.Join(" ", states.Select(kv => kv.Key + ":" + kv.Value)));
					}
					gameSystemCount = total;
				}

				var gw = game.GetType().GetProperty("Window")?.GetValue(game) as Stride.Games.GameWindow;
				var cb = gw?.ClientBounds ?? default;
				var scale = gw?.ScaleFactor ?? 0f;

				var pos = input.MousePosition;
				var delta = input.MouseDelta;
				return string.Format(System.Globalization.CultureInfo.InvariantCulture,
					"{{\"hasMouse\":{0},\"hasKeyboard\":{1},\"mouse\":{{\"x\":{2},\"y\":{3}}}," +
					"\"delta\":{{\"x\":{4},\"y\":{5}}},\"downButtons\":{6},\"downKeys\":{7},\"sources\":{8}," +
					"\"gameWindow\":{{\"w\":{9},\"h\":{10},\"scale\":{11}}}," +
					"\"seenButtonPresses\":{12},\"seenKeyPresses\":{13},\"ticks\":{14}," +
					"\"wpfMoves\":{15},\"wpfDowns\":{16},\"simPresses\":{17},\"simDown\":{19},\"downNames\":\"{20}\",\"wpfKeyDowns\":{21},\"gotKbFocus\":{23},\"gameFrames\":{26},\"gameSystems\":{27},\"scriptSystem\":\"{28}\",\"editorHidden\":{29},\"gameFaulted\":{30},\"lastFault\":\"{31}\",\"isFocused\":{24},\"isKbFocused\":{25},\"downKeyNames\":\"{22}\",\"camera\":{18}}}",
					input.HasMouse ? "true" : "false",
					input.HasKeyboard ? "true" : "false",
					pos.X, pos.Y, delta.X, delta.Y,
					input.DownButtons.Count, input.DownKeys.Count, input.Sources.Count,
					cb.Width, cb.Height, scale,
					observedButtonPresses, observedKeyPresses, tickCount,
					forwardedMoves, forwardedDowns,
					observedSimulatedPresses,
					DescribeCamera(),
					// DownButtons is updated synchronously by HandleButtonDown, while PressedButtons and
					// the event queue only drain when the device's Update runs. Reporting both separates
					// "the forward never happened" from "the device is never updated".
					simulatedMouse?.DownButtons.Count ?? -1,
					// Which buttons, not just how many: the camera only rotates on the right button, so a
					// mis-mapped button looks exactly like "the service is not running".
					string.Join("+", input.DownButtons),
					forwardedKeyDowns,
					string.Join("+", input.DownKeys),
					gotKeyboardFocusCount,
					IsFocused ? "true" : "false",
					IsKeyboardFocused ? "true" : "false",
					gameFrameCount,
					gameSystemCount,
					scriptSystemState,
					editorHidden ? "true" : "false",
					gameFaulted ? "true" : "false",
					lastFault.Length > 600 ? lastFault.Substring(0, 600) : lastFault);
			}
			catch (Exception ex)
			{
				return "{\"error\":\"" + ex.GetType().Name + "\"}";
			}
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
