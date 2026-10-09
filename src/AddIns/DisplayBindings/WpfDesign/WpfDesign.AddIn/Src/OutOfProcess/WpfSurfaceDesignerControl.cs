#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Surface;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.WpfDesign.SurfaceHost;
using LeXtudio.DevFlow.Agent.Core;

namespace ICSharpCode.WpfDesign.AddIn.OutOfProcess
{
	/// <summary>
	/// The WPF designer's surface: the shared design canvas (<see cref="DesignSurface"/> plus a
	/// <see cref="DesignSurfaceController"/>, ICSharpCode.DesignerCanvas addin - see
	/// doc/technotes/designer-canvas-addin.md) driven through <see cref="WpfSurfaceHostClient"/>,
	/// the out-of-process WPF/LibreWPF design host.
	///
	/// The canvas owns everything framework-neutral - frame, zoom/fit/pan, selection outlines,
	/// click/Ctrl-click/marquee selection, move/resize with snapping, Grid guides, tab order,
	/// gridlines, keyboard - and this class supplies the WPF half: the host's hit test, turning
	/// committed gestures into DDP mutations, and WPF's own design-time chrome in the canvas's
	/// <see cref="DesignSurface.ExtensionLayer"/> (the detached-ContextMenu/submenu tray, the
	/// Menu/StatusBar/ToolBar "Type Here" hotspot and the inline editor they share).
	///
	/// Elements are keyed by their DDP <see cref="DesignerElementNode.Id"/> (the tree path; the
	/// document root is "", so "no selection" is null, never ""). Every mutation is a BLOCKING call
	/// on the dispatcher thread followed by <see cref="Show"/> - see its remarks for why.
	/// </summary>
	public sealed class WpfSurfaceDesignerControl : DesignSurface
	{
		readonly WpfSurfaceHostClient client;
		readonly DesignSurfaceController controller;

		DesignerSessionState? state;

		/// <summary>The document root's tree path/id (<c>WpfSurfaceHostService.BuildNode</c>).</summary>
		const string RootElementId = "";

		const DesignerCanvasCapabilities BaseCapabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit |
			DesignerCanvasCapabilities.Gridlines | DesignerCanvasCapabilities.ShowNames | DesignerCanvasCapabilities.StatusBar;

		// The Grid whose divider guides are shown, with its track START offsets as the host
		// reports them (design/query-grid-guides) - what a divider drag's new size is derived from.
		string? gridGuideElementId;
		Rect gridGuideRect;
		double[] gridRowOffsets = Array.Empty<double>();
		double[] gridColOffsets = Array.Empty<double>();

		// Inline text editing. One editor for every WPF edit - an element's Text/Content/Header in
		// the frame, a tray row's Header, a "Type Here" slot - living in the canvas's extension
		// layer, so it scrolls and zooms with the design. Committed via Enter or focus loss;
		// Escape cancels.
		readonly TextBox textEditor = new TextBox {
			Visibility = Visibility.Collapsed,
			BorderBrush = Brushes.DodgerBlue,
			BorderThickness = new Thickness(1),
			Padding = new Thickness(2),
			AcceptsReturn = false
		};
		bool textEditing;
		Rect textEditRect;
		string? textEditElementId;
		string? textEditPropertyName;
		/// <summary>True when <see cref="anchoredTextEditRect"/> (extension-layer coordinates of a
		/// tray row or hotspot) places the editor, rather than a design rect.</summary>
		bool textEditIsAnchored;
		Rect anchoredTextEditRect;
		/// <summary>Non-null while editing a "Type Here" slot rather than an existing element's
		/// Header: the id of the Menu/ContextMenu/MenuItem the committed text should be added to as a
		/// brand-new item. See <see cref="BeginNewMenuItemEdit"/>/<see cref="EndInlineEdit"/>.</summary>
		string? textEditNewItemParentId;
		/// <summary>The element Type of <see cref="textEditNewItemParentId"/>'s container, deciding
		/// which RPC <see cref="EndInlineEdit"/> commits a new item through.</summary>
		string? textEditNewItemContainerType;

		// WPF ContextMenu is detached from its owner's visual tree, so it cannot be clicked in
		// the rendered frame. When the strip itself is selected from Outline, expose its items in
		// this deliberately narrow design-time tray. It is not a general HeaderedControl overlay.
		readonly Border contextMenuTray = new() {
			Visibility = Visibility.Collapsed,
			Background = Brushes.White,
			BorderBrush = Brushes.DodgerBlue,
			BorderThickness = new Thickness(1),
			Padding = new Thickness(2)
		};
		readonly StackPanel contextMenuTrayItems = new();
		// A Grid, not contextMenuTray.Child = contextMenuTrayItems directly, so
		// contextMenuTrayInsertionLine can float above the rows during a drag - a Border only
		// supports one child.
		readonly Grid contextMenuTrayRoot = new();
		readonly Border contextMenuTrayInsertionLine = new() {
			Height = 2,
			Background = Brushes.DodgerBlue,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Top,
			IsHitTestVisible = false,
			Visibility = Visibility.Collapsed
		};
		string? contextMenuTrayRootId;
		/// <summary>Each real item's reorder-grip visual, keyed by element id, so
		/// <see cref="ContextMenuTrayStatus"/> can report their screen centers for pointer-driven
		/// integration tests.</summary>
		readonly Dictionary<string, Border> contextMenuTrayReorderGrips = new(StringComparer.Ordinal);
		// Drag-to-reorder state, through ReorderGestureCalculator's shared axis-agnostic math.
		bool trayReorderPending;
		string? trayReorderElementId;
		int trayReorderOriginalIndex;
		double trayReorderStartY;
		/// <summary>The tray's own trailing "Type Here" slot and the ContextMenu it belongs to, kept
		/// so a re-arm after committing a new item can re-open editing on the freshly-rebuilt slot.</summary>
		FrameworkElement? contextMenuTrayNewItemVisual;
		DesignerElementNode? contextMenuTrayContextMenuNode;

		/// <summary>A top-level Menu, StatusBar or ToolBar renders its items directly in the frame
		/// bitmap (unlike a detached ContextMenu or MenuItem submenu popup), so adding another item
		/// needs only one small floating "Type Here" hotspot placed just past the container's own
		/// content, not a whole tray duplicating items that are already visible.</summary>
		readonly Border menuTypeHereHotspot = new() {
			Visibility = Visibility.Collapsed,
			Background = Brushes.White,
			// Gray border, plain 11pt DimGray text, I-beam cursor: matches RemoteFormsDesignerControl's
			// own typeHereCell, so the two designers' "Type Here" affordance looks like one feature.
			BorderBrush = Brushes.Gray,
			BorderThickness = new Thickness(1),
			Padding = new Thickness(6, 2, 6, 2),
			Cursor = Cursors.IBeam,
			ToolTip = "Type a name to add a new item; Enter keeps adding, Esc cancels.",
			Child = new TextBlock { Text = "Type Here", FontSize = 11, Foreground = Brushes.DimGray }
		};
		string? menuTypeHereHotspotMenuId;
		double menuTypeHereHotspotContainerLeft, menuTypeHereHotspotContainerRight;
		/// <summary>The element Type of <see cref="menuTypeHereHotspotMenuId"/>'s container - decides
		/// which RPC a commit through this hotspot uses (see <see cref="BeginNewMenuItemEdit"/>).</summary>
		string? menuTypeHereHotspotContainerType;
		/// <summary>The hotspot's design-space anchor (just past the container's last child).</summary>
		Point menuTypeHereHotspotDesignPoint;

		public WpfSurfaceDesignerControl(WpfSurfaceHostClient client, string backendName)
		{
			this.client = client ?? throw new ArgumentNullException(nameof(client));
			BackendName = backendName;
			// A WPF Page/UserControl/Window has no Background of its own unless the design sets
			// one; without an opaque page the canvas pattern shows through its transparent parts.
			FrameBackground = Brushes.White;
			controller = new DesignSurfaceController(this, DesignSurfaceKeying.Id) {
				// A plain click that hits nothing clears the selection, as in every WPF designer.
				ClearsSelectionOnEmptyClick = true
			};

			contextMenuTrayRoot.Children.Add(contextMenuTrayItems);
			contextMenuTrayRoot.Children.Add(contextMenuTrayInsertionLine);
			contextMenuTray.Child = contextMenuTrayRoot;
			ExtensionLayer.Children.Add(contextMenuTray);
			menuTypeHereHotspot.MouseLeftButtonDown += (_, e) => {
				if (menuTypeHereHotspotMenuId is { } menuId && menuTypeHereHotspotContainerType is { } containerType)
					BeginNewMenuItemEdit(menuId, containerType, menuTypeHereHotspot);
				e.Handled = true;
			};
			ExtensionLayer.Children.Add(menuTypeHereHotspot);
			textEditor.KeyDown += OnTextEditorKeyDown;
			textEditor.LostKeyboardFocus += OnTextEditorLostFocus;
			ExtensionLayer.Children.Add(textEditor);
			ViewportChanged += (_, _) => LayoutExtensions();

			// Two toolbar entries stay hidden because they are other backends' concepts: design
			// size presets (a WPF Window/UserControl carries its own size in the XAML) and visual
			// states. The theme combo is per-project (DesignerSessionState.DesignThemes, see Show).
			Capabilities = BaseCapabilities;
			StatusText = string.Format(ResourceService.GetString("WpfDesign.Status.StartingHost"), BackendName);
			SetContextCommands(new[] { ("Delete", "delete") });
			RenameRequested += (_, _) => RequestRename();

			controller.SelectionChanged += (_, _) => OnCanvasSelectionChanged();
			controller.ElementPicked += (_, id) => {
				// A drag started on an element that is not selected yet selects it first, so the
				// Properties pad and outline follow the element being dragged.
				if (!controller.SelectedNames.Contains(id))
				{
					controller.RestoreSelection(new[] { id });
					OnCanvasSelectionChanged();
				}
			};
			controller.ElementDragCommitted += (_, drag) =>
				CommitBounds(drag.Name, new Rect(drag.EndX, drag.EndY, drag.EndWidth, drag.EndHeight));
			controller.ElementGroupDragCommitted += (_, moves) => CommitBoundsForEach(moves
				.Select(move => (Node: NodeById(move.Name), move.DX, move.DY))
				.Where(move => move.Node != null)
				.Select(move => (move.Node!.Id, new Rect(move.Node.X + move.DX, move.Node.Y + move.DY, move.Node.Width, move.Node.Height)))
				.ToList());
			controller.NudgeRequested += (_, delta) => NudgeSelection(delta.DX, delta.DY);
			controller.ElementDoubleClicked += (_, info) => OnElementDoubleClicked(info);
			controller.GridGuideDragCommitted += (_, guide) => CommitGridGuide(guide.IsRow, guide.Index, guide.Position);
			controller.GridTrackSplitRequested += (_, split) => CommitGridTrackSplit(split.IsRow, split.Position);
			controller.ContextCommandRequested += (_, command) => {
				if (command.Command == "delete")
					CommitDelete();
			};
			// ThemeRequested carries the chosen theme name; CommitTheme blocks like every mutation.
			DesignThemeRequested += (_, theme) => CommitTheme(theme);
			ComponentTraySelectionRequested += (_, id) => SelectElementId(id);

			AllowDrop = true;
			DragOver += OnDragOver;
			Drop += OnDrop;
		}

		public DesignerSessionState? State => state;

		/// <summary>Opens a document from a host-owned snapshot. Does NOT render the returned
		/// frame itself - see the note on <see cref="Show"/> for why every caller of this and the
		/// mutation methods below must call it explicitly instead.</summary>
		public async Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default)
		{
			state = await client.OpenAsync(snapshot, cancellationToken).ConfigureAwait(false);
			return state;
		}

		/// <summary>Delivers newer source (a host-side edit or external change). Does NOT render -
		/// see <see cref="Show"/>.</summary>
		public async Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default)
		{
			var updated = await client.UpdateAsync(snapshot, cancellationToken).ConfigureAwait(false);
			// session/update is allowed to be incremental just like design/select.  Do not assign it
			// to state here: callers deliberately hand the result to Show(), and overwriting state
			// first loses the last complete tree before Show() has a chance to merge it.  That was
			// exposed by redo followed by a selection from the Source half: the frame updated, but
			// the Properties pad could no longer construct an adapter for the selected element.
			updated.Tree ??= state?.Tree;
			return updated;
		}

		/// <summary>Renders <paramref name="newState"/>'s frame and re-places the selection.
		/// Deliberately a separate step the caller invokes explicitly, rather than a continuation
		/// this class's own async RPC wrappers run after their own await.
		///
		/// EVERY caller of this method blocks via <c>.GetAwaiter().GetResult()</c> and calls this
		/// directly afterward, on the same thread - including the gesture/toolbox-drop/Delete
		/// handlers, which are always already on the dispatcher thread. Those used to be
		/// fire-and-forget async methods awaiting with <c>ConfigureAwait(true)</c>, and that was
		/// proven unreliable live: the continuation resumed on a thread-pool thread, touching WPF
		/// objects threw, and the swallowed exception meant <see cref="DocumentChanged"/> never
		/// reached <c>WpfViewContent</c> - an applied edit was never marked dirty. Blocking needs no
		/// SynchronizationContext capture at all (doc/technotes/wpf-designer.md).</summary>
		internal void Show(DesignerSessionState newState)
		{
			// A rejected mutation deliberately has no Render payload: the child did not change its
			// document. Keep the last accepted frame and surface the rejection as status instead.
			if (!newState.Accepted && state?.Render is { Data.Length: > 0 })
			{
				StatusText = string.Format(ResourceService.GetString("WpfDesign.Status.HostError"), BackendName, newState.Error ?? ResourceService.GetString("WpfDesign.Status.OperationRejected"));
				return;
			}
			state = newState;
			// The theme combo lists exactly the themes the project's assembly embeds (its
			// themes/*.xaml resources); a project without any embedded theme hides the combo.
			Capabilities = BaseCapabilities |
				(newState.DesignThemes.Length > 0 ? DesignerCanvasCapabilities.Theme : DesignerCanvasCapabilities.None);
			if (Capabilities.HasFlag(DesignerCanvasCapabilities.Theme))
				SetDesignThemes(newState.DesignThemes);
			controller.ApplySnapshot(newState);
			var render = newState.Render;
			if (render == null || string.IsNullOrEmpty(render.Data) || render.Width <= 0 || render.Height <= 0)
			{
				StatusText = string.Format(ResourceService.GetString("WpfDesign.Status.NothingRenderedYet"), BackendName);
				controller.RestoreSelection(Array.Empty<string>());
				HideContextMenuTray();
				HideMenuTypeHereHotspot();
				EndInlineEdit(commit: false);
				SetGridGuideOverlay(null, default, Array.Empty<double>(), Array.Empty<double>());
				return;
			}
			StatusText = string.Format(ResourceService.GetString("WpfDesign.Status.RenderedByHost"), BackendName, render.Width, render.Height);
			// A reload shows the loading overlay on this surface (WpfViewContent.LoadInternal); a
			// frame is what ends it. Left up, it dims the design and swallows every press.
			SetLoading(false);
			// The tree was rebuilt: redraw the selection from its new bounds (dropping elements
			// that no longer exist) without re-announcing it.
			controller.RestoreSelection();
			UpdateSelectionChrome();
		}

		/// <summary>Whether the tab-order badge overlay is currently shown.</summary>
		public bool ShowTabOrder => controller.ShowTabOrder;

		/// <summary>Toggles the tab-order badges: a numbered badge near every visible element
		/// reporting a TabIndex (<c>WpfSurfaceHostService.BuildProperties</c> reflects it for every
		/// Control).</summary>
		public void SetTabOrderMode(bool show) => controller.SetTabOrderMode(show);

		#region Selection

		internal DesignerElementNode? SelectedNode => NodeById(controller.SelectedElementName);

		/// <summary>The node with this <see cref="DesignerElementNode.Id"/> (falling back to a tree
		/// path, which is the same thing for every WPF element but a header group), or null.</summary>
		DesignerElementNode? NodeById(string? id)
		{
			if (id == null || state?.Tree is not { } tree)
				return null;
			return FindNode(tree, node => node.Id == id) ?? FindNode(tree, node => node.Path == id);
		}

		static DesignerElementNode? FindNode(DesignerElementNode node, Func<DesignerElementNode, bool> match)
		{
			if (match(node))
				return node;
			foreach (var child in node.Children)
			{
				if (FindNode(child, match) is { } found)
					return found;
			}
			return null;
		}

		/// <summary>The canvas changed the selection (click, Ctrl-click, marquee, drag start).</summary>
		void OnCanvasSelectionChanged()
		{
			NotifySelectionChanged();
			UpdateSelectionChrome();
		}

		/// <summary>Everything that follows the selection: the Grid guides of a selected Grid, the
		/// ContextMenu tray and the "Type Here" hotspot.</summary>
		void UpdateSelectionChrome()
		{
			RefreshGridGuides(SelectedNode);
			UpdateContextMenuTray();
			UpdateMenuTypeHereHotspot();
			LayoutTextEditor();
		}

		/// <summary>Re-places the extension-layer chrome after a zoom, fit, pan or new frame.</summary>
		void LayoutExtensions()
		{
			if (contextMenuTray.Visibility == Visibility.Visible)
				PlaceContextMenuTray();
			if (menuTypeHereHotspot.Visibility == Visibility.Visible)
				PlaceMenuTypeHereHotspot();
			LayoutTextEditor();
		}

		public event EventHandler? SelectionChanged;
		/// <summary>Raised for F2 after the canvas has confirmed that an element is selected. The
		/// view owns the modal name editor and keeps this bitmap-only control free of shell UI.</summary>
		public event EventHandler? RenameRequestedByUser;

		void RequestRename() => RenameRequestedByUser?.Invoke(this, EventArgs.Empty);

		/// <summary>Raises <see cref="SelectionChanged"/> and round-trips the new selection to the
		/// child (<c>design/select</c>) so a collapsed <c>Expander</c> ancestor of the selection
		/// temporarily expands - Blend's "select inside a collapsed Expander" behavior (see
		/// <c>WpfSurfaceHostService.Select</c>). Every selection change goes through this; the
		/// re-render it may trigger goes through <see cref="Show"/>, which restores the selection
		/// without raising it again, so there is no recursion.
		///
		/// Blocking, like every other call here. A no-op response (no <c>Render</c> payload - most
		/// selections touch no Expander) is deliberately NOT passed to <see cref="Show"/>. Any
		/// failure is swallowed - this is a design-time convenience affordance, never something a
		/// selection change should be blocked or broken by.</summary>
		void NotifySelectionChanged()
		{
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			if (state == null)
				return;
			try
			{
				var newState = client.SelectAsync(RequireVersion(), controller.SelectedElementName).GetAwaiter().GetResult();
				if (newState.Render != null)
				{
					// design/select is normally an incremental response.  When selecting inside a
					// collapsed Expander it includes a new frame, but the host need not repeat
					// the unchanged design tree.  Replacing the full state with that frame-only
					// response made Show() index an empty tree and drop the very selection that
					// initiated the call (notably after undo/redo, when a render is common).
					newState.Tree ??= state.Tree;
					Show(newState);
					// Show() reapplies the controller selection against the newly committed tree.
					// The first notification above occurred before this incremental frame arrived;
					// it can therefore give Properties an adapter from the pre-update controller (or
					// none at all after undo/redo). Notify again only after Show has made the new
					// tree and selection authoritative.
					SelectionChanged?.Invoke(this, EventArgs.Empty);
				}
			}
			catch (Exception)
			{
				// Best-effort design-time affordance - see this method's own doc comment.
			}
		}

		/// <summary>Raised after an actual accepted mutation (bounds/add/delete/rename/property
		/// edit) commits and renders - distinct from <see cref="SelectionChanged"/>, which also
		/// fires for a plain selection. <c>WpfViewContent</c> uses this, not
		/// <see cref="DesignerSessionState.Version"/> comparisons, to decide when to mark the file
		/// dirty: every mutation RPC echoes back the caller's own version unchanged.</summary>
		public event EventHandler<DesignerSessionState>? DocumentChanged;

		/// <summary>Raises <see cref="DocumentChanged"/> for a mutation committed by a blocking
		/// caller outside this class (<c>WpfDesignDevFlowActions.DropToolboxItem</c>).</summary>
		internal void NotifyDocumentChanged(DesignerSessionState newState) => DocumentChanged?.Invoke(this, newState);

		/// <summary>The currently selected element's id (its tree path), or null.</summary>
		public string? SelectedElementId => controller.SelectedElementName;

		/// <summary>Every selected element's id, primary first.</summary>
		public IReadOnlyList<string> SelectedElementIds => controller.SelectedNames;

		/// <summary>The current element tree, ready to hand straight to
		/// <see cref="DocumentOutlineControl.SetRoot"/> - WPF's DDP shape already IS the tree the
		/// Outline pad wants.</summary>
		public DesignerElementNode? OutlineRoot => state?.Tree;

		/// <summary>Selects an element by id, or clears the selection when <paramref name="id"/>
		/// is null. The empty string is NOT "no selection" - it is the document root's own id, so
		/// passing it selects the Window/UserControl itself.</summary>
		public void SelectElementId(string? id)
		{
			// A preceding incremental host response (for example after redo followed by
			// design/select) may have updated the displayed frame without repopulating the
			// controller's lookup table.  Programmatic selection is addressed by the current
			// session tree, so make that tree authoritative before RestoreSelection consults
			// its index.
			if (state != null)
				controller.ApplySnapshot(state);
			controller.RestoreSelection(id == null ? Array.Empty<string>() : new[] { NodeById(id)?.Id ?? id });
			NotifySelectionChanged();
			UpdateSelectionChrome();
		}

		/// <summary>Sets the multi-selection to exactly the named elements (by
		/// <see cref="DesignerElementNode.Name"/>, resolved via <see cref="FindNodeByName"/>), the
		/// first becoming the primary. Unresolvable names are skipped.</summary>
		public void SetMultiSelection(IReadOnlyList<string> names)
		{
			controller.RestoreSelection(names
				.Select(FindNodeByName)
				.Where(node => node != null)
				.Select(node => node!.Id)
				.ToList());
			NotifySelectionChanged();
			UpdateSelectionChrome();
		}

		/// <summary>Finds a node by its <c>x:Name</c> (depth-first), falling back to matching the
		/// TYPE name of an unnamed element - without it the document root (normally carrying no
		/// x:Name) is unreachable by name. First match wins; the root is visited first.</summary>
		public DesignerElementNode? FindNodeByName(string name)
			=> state?.Tree is { } tree
				? FindNode(tree, node => node.Name == name) ?? FindNode(tree, node => node.Name == null && node.Type == name)
				: null;

		#endregion

		#region Geometry probes

		/// <summary>The current selection's rendered bounds in screen coordinates, or null when
		/// nothing is selected (<c>od.wpf-designer.surface-geometry</c>).</summary>
		public Rect? ScreenBoundsOfSelected() => ScreenBoundsOf(SelectedNode);

		/// <summary>Screen-coordinate bounds of an arbitrary node by id, not just the selection
		/// (<c>od.wpf-designer.query-element-screen-bounds</c>).</summary>
		public Rect? ScreenBoundsOf(string? elementId) => ScreenBoundsOf(NodeById(elementId));

		Rect? ScreenBoundsOf(DesignerElementNode? node)
		{
			if (node == null || !IsVisible)
				return null;
			var topLeft = SurfacePointToScreen(node.X, node.Y);
			var bottomRight = SurfacePointToScreen(node.X + node.Width, node.Y + node.Height);
			// SurfacePointToScreen is correctly relative to the canvas, but LibreWPF can expose
			// that point in client coordinates while its portable presentation source is active.
			// DevFlow's raw pointer endpoints use Quartz screen coordinates, so calibrate both
			// corners against the native content origin just as the Toolbox bounds probe does.
			if (OperatingSystem.IsMacOS()
				&& Window.GetWindow(this) is { } window
				&& MacOSWindowOrigin.TryGetContentOrigin() is { } nativeOrigin)
			{
				var reportedOrigin = window.PointToScreen(new Point());
				var delta = new Vector(nativeOrigin.X - reportedOrigin.X, nativeOrigin.Y - reportedOrigin.Y);
				topLeft += delta;
				bottomRight += delta;
			}
			return new Rect(topLeft, bottomRight);
		}

		/// <summary>State of the real on-canvas text editor, exposed only for pointer-driven
		/// integration tests: screen coordinates, not just visible/focused booleans, because the
		/// tray/hotspot flow has a real class of bug where the editor is focused but mispositioned.</summary>
		internal object InlineEditorStatus {
			get {
				var center = textEditor.PointToScreen(
					new Point(textEditor.ActualWidth / 2, textEditor.ActualHeight / 2));
				return new {
					editing = textEditing,
					visible = textEditor.IsVisible,
					focused = textEditor.IsKeyboardFocusWithin,
					elementId = textEditElementId,
					propertyName = textEditPropertyName,
					text = textEditor.Text,
					screenCenterX = center.X,
					screenCenterY = center.Y
				};
			}
		}

		internal bool InputInlineEditor(string text, bool cancel)
		{
			if (!textEditing || !textEditor.IsKeyboardFocusWithin)
				return false;
			textEditor.SelectAll();
			textEditor.SelectedText = text;
			textEditor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(textEditor),
				Environment.TickCount, cancel ? Key.Escape : Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
			return true;
		}

		/// <summary>Read-only geometry for the ContextMenu tray's pointer-driven integration test.
		/// Production selection and editing still go through the actual WPF routed mouse events.</summary>
		internal object ContextMenuTrayStatus => new {
			visible = contextMenuTray.IsVisible,
			contextMenuId = contextMenuTrayRootId,
			items = contextMenuTrayItems.Children.OfType<FrameworkElement>().Select(item => {
				var center = item.PointToScreen(new Point(item.ActualWidth / 2, item.ActualHeight / 2));
				var elementId = item.Tag as string;
				var grip = elementId != null && contextMenuTrayReorderGrips.TryGetValue(elementId, out var g) ? g : null;
				var gripCenter = grip != null ? grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2)) : (Point?)null;
				return new {
					elementId,
					centerX = center.X,
					centerY = center.Y,
					reorderGripCenterX = gripCenter?.X,
					reorderGripCenterY = gripCenter?.Y
				};
			}).ToArray()
		};

		/// <summary>Read-only geometry for the top-level Menu "Type Here" hotspot's pointer-driven
		/// integration test.</summary>
		internal object MenuTypeHereHotspotStatus {
			get {
				var center = menuTypeHereHotspot.PointToScreen(
					new Point(menuTypeHereHotspot.ActualWidth / 2, menuTypeHereHotspot.ActualHeight / 2));
				return new { visible = menuTypeHereHotspot.IsVisible, menuId = menuTypeHereHotspotMenuId, centerX = center.X, centerY = center.Y };
			}
		}

		#endregion

		#region Grid row/column guides

		/// <summary>Re-queries and re-shows the Grid guides for the current selection - hidden
		/// when nothing/a non-Grid is selected.</summary>
		void RefreshGridGuides(DesignerElementNode? node)
		{
			if (node == null || node.Type != "Grid" || state == null)
			{
				SetGridGuideOverlay(null, default, Array.Empty<double>(), Array.Empty<double>());
				return;
			}
			var guides = client.QueryGridGuidesAsync(state.Version, node.Id).GetAwaiter().GetResult();
			if (!guides.Accepted)
			{
				SetGridGuideOverlay(null, default, Array.Empty<double>(), Array.Empty<double>());
				return;
			}
			SetGridGuideOverlay(node.Id, new Rect(node.X, node.Y, node.Width, node.Height),
				guides.RowTracks.Select(t => t.Offset).ToArray(),
				guides.ColumnTracks.Select(t => t.Offset).ToArray());
		}

		/// <summary>Shows the dividers between the given track start offsets. The canvas takes
		/// boundary offsets (every track's start plus the far edge), so the Grid's own size is
		/// appended - a Grid with N rows gets N-1 interior dividers.</summary>
		void SetGridGuideOverlay(string? elementId, Rect gridRect, double[] rowOffsets, double[] colOffsets)
		{
			gridGuideElementId = elementId;
			gridGuideRect = gridRect;
			gridRowOffsets = rowOffsets;
			gridColOffsets = colOffsets;
			if (elementId == null)
			{
				SetGridGuides(null!, 0, 0, 0, 0, Array.Empty<double>(), Array.Empty<double>());
				return;
			}
			SetGridGuides(elementId, gridRect.X, gridRect.Y, gridRect.Width, gridRect.Height,
				rowOffsets.Length > 0 ? rowOffsets.Append(gridRect.Height).ToArray() : Array.Empty<double>(),
				colOffsets.Length > 0 ? colOffsets.Append(gridRect.Width).ToArray() : Array.Empty<double>());
		}

		/// <summary>Commits a divider drag: the row/column BEFORE the divider gets the size from its
		/// own start to the divider's new design position.</summary>
		void CommitGridGuide(bool isRow, int index, double position)
		{
			if (gridGuideElementId is not { } elementId || state == null)
				return;
			var offsets = isRow ? gridRowOffsets : gridColOffsets;
			if (index < 0 || index >= offsets.Length)
				return;
			var trackStart = (isRow ? gridGuideRect.Y : gridGuideRect.X) + offsets[index];
			var newSize = Math.Max(1, position - trackStart);
			var result = client.SetGridTrackSizeAsync(state.Version, elementId, isRow, index, newSize).GetAwaiter().GetResult();
			if (!result.Accepted) { Show(result); return; }
			Show(result);
			DocumentChanged?.Invoke(this, result);
		}

		/// <summary>Ctrl-clicking a highlighted insertion rail atomically splits its measured row;
		/// Ctrl+Shift-click does the same for a column.  The host owns child-cell adjustment so the
		/// client only ever submits the Grid-local coordinate the pointer identified.</summary>
		void CommitGridTrackSplit(bool isRow, double localPosition)
		{
			if (gridGuideElementId is not { } elementId || state == null)
				return;
			var result = client.SplitGridTrackAsync(state.Version, elementId, isRow, localPosition).GetAwaiter().GetResult();
			if (!result.Accepted) { Show(result); return; }
			Show(result);
			DocumentChanged?.Invoke(this, result);
		}

		#endregion

		#region Inline text editing

		/// <summary>The element's Text/Content property, if it holds a plain string value safe to
		/// edit inline - "Text" is preferred (TextBlock/TextBox/etc.), then "Content", then the
		/// string "Header" of a MenuItem (the WPF counterpart of a WinForms menu-strip item).
		/// Deliberately not every HeaderedControl: TabItem, TreeViewItem, Expander and GroupBox have
		/// distinct editing semantics. Returns null when none applies, e.g. a layout panel.</summary>
		static string? ResolveTextPropertyName(DesignerElementNode? node)
		{
			if (node == null)
				return null;
			bool IsEditableString(DesignerPropertyInfo p) => !p.IsReadOnly && p.Kind == "String";
			if (node.Properties.FirstOrDefault(p => p.Name == "Text" && IsEditableString(p)) != null)
				return "Text";
			if (node.Properties.FirstOrDefault(p => p.Name == "Content" && IsEditableString(p)) != null)
				return "Content";
			if (node.Type == "MenuItem"
				&& node.Properties.FirstOrDefault(p => p.Name == "Header" && IsEditableString(p)) != null)
				return "Header";
			return null;
		}

		void OnElementDoubleClicked(ElementDoubleClickInfo? info)
		{
			var hitNode = info != null ? NodeById(info.Name) : null;
			// MenuItem is rendered inside its Menu's fallback/host visual, which commonly wins the
			// child-side hit test. When the user has explicitly selected a MenuItem, retain that
			// intent for a double-click so Header remains editable.
			if (ResolveTextPropertyName(hitNode) == null && SelectedNode?.Type == "MenuItem")
				hitNode = SelectedNode;
			if (hitNode != null && ResolveTextPropertyName(hitNode) is { } propertyName)
			{
				var currentValue = hitNode.Properties.First(p => p.Name == propertyName).Value;
				BeginInlineEdit(hitNode.Id, propertyName, new Rect(hitNode.X, hitNode.Y, hitNode.Width, hitNode.Height), currentValue);
			}
		}

		/// <summary>Shows the inline editor over the given design rect, pre-filled with
		/// <paramref name="text"/>.</summary>
		void BeginInlineEdit(string elementId, string propertyName, Rect designRect, string? text)
		{
			textEditIsAnchored = false;
			textEditNewItemParentId = null;
			textEditNewItemContainerType = null;
			textEditElementId = elementId;
			textEditPropertyName = propertyName;
			textEditRect = designRect;
			OpenTextEditor(text);
		}

		void OpenTextEditor(string? text)
		{
			textEditing = true;
			textEditor.Text = text ?? "";
			textEditor.Visibility = Visibility.Visible;
			LayoutTextEditor();
			textEditor.Focus();
			textEditor.SelectAll();
		}

		void LayoutTextEditor()
		{
			if (!textEditing)
				return;
			if (textEditIsAnchored)
			{
				Canvas.SetLeft(textEditor, anchoredTextEditRect.X);
				Canvas.SetTop(textEditor, anchoredTextEditRect.Y);
				textEditor.Width = anchoredTextEditRect.Width;
				textEditor.Height = anchoredTextEditRect.Height;
				textEditor.FontSize = 14;
				return;
			}
			var scale = ViewportScale;
			var origin = DesignToContentPoint(textEditRect.X, textEditRect.Y);
			Canvas.SetLeft(textEditor, origin.X);
			Canvas.SetTop(textEditor, origin.Y);
			textEditor.Width = Math.Max(1, textEditRect.Width * scale);
			textEditor.Height = Math.Max(1, textEditRect.Height * scale);
			textEditor.FontSize = 14 * scale;
		}

		/// <summary>Places the editor over an extension-layer visual (a tray row or hotspot).</summary>
		void AnchorTextEditor(FrameworkElement anchorVisual)
		{
			// The re-arm path calls this right after the tray/hotspot was rebuilt or moved: without
			// a layout pass, TranslatePoint/ActualWidth still report the pre-arrange (0,0)/zero-size
			// state and the editor jumps to the layer's origin instead of following its anchor.
			ExtensionLayer.UpdateLayout();
			var origin = anchorVisual.TranslatePoint(new Point(0, 0), ExtensionLayer);
			anchoredTextEditRect = new Rect(origin.X, origin.Y,
				Math.Max(48, anchorVisual.ActualWidth), Math.Max(20, anchorVisual.ActualHeight));
			textEditIsAnchored = true;
		}

		void BeginContextMenuTrayTextEdit(DesignerElementNode item, FrameworkElement itemVisual)
		{
			var header = item.Properties.FirstOrDefault(p => p.Name == "Header" && !p.IsReadOnly && p.Kind == "String");
			if (header == null)
				return;
			AnchorTextEditor(itemVisual);
			textEditNewItemParentId = null;
			textEditNewItemContainerType = null;
			textEditElementId = item.Id;
			textEditPropertyName = "Header";
			OpenTextEditor(header.Value);
		}

		/// <summary>Opens the "Type Here" editor for a brand-new item under
		/// <paramref name="parentId"/> (a Menu, ContextMenu, MenuItem, StatusBar or ToolBar),
		/// anchored over <paramref name="anchorVisual"/> - the tray's trailing slot or the hotspot.
		/// <paramref name="containerType"/> decides which RPC <see cref="EndInlineEdit"/> commits
		/// through (design/add-menu-item for Menu/ContextMenu/MenuItem, design/add-strip-item for
		/// StatusBar/ToolBar).</summary>
		void BeginNewMenuItemEdit(string parentId, string containerType, FrameworkElement anchorVisual)
		{
			AnchorTextEditor(anchorVisual);
			textEditElementId = null;
			textEditNewItemParentId = parentId;
			textEditNewItemContainerType = containerType;
			textEditPropertyName = containerType is "StatusBar" or "ToolBar" ? "Content" : "Header";
			OpenTextEditor("");
		}

		void OnTextEditorKeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Enter)
			{
				EndInlineEdit(commit: true, reopenIfNewItem: true);
				e.Handled = true;
			}
			else if (e.Key == Key.Escape)
			{
				EndInlineEdit(commit: false);
				e.Handled = true;
			}
		}

		void OnTextEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
		{
			if (textEditing)
				EndInlineEdit(commit: true);
		}

		/// <summary>Closes the inline editor, committing through the matching RPC.
		/// <paramref name="reopenIfNewItem"/> only matters for a "Type Here" slot committed via
		/// Enter - matching RemoteFormsDesignerControl.CommitTypeHere's re-arm, so typing several
		/// sibling items back-to-back needs no re-click between them.</summary>
		void EndInlineEdit(bool commit, bool reopenIfNewItem = false)
		{
			if (!textEditing)
				return;
			var text = textEditor.Text;
			var elementId = textEditElementId;
			var propertyName = textEditPropertyName!;
			var newItemParentId = textEditNewItemParentId;
			var newItemContainerType = textEditNewItemContainerType;
			var refreshContextMenuTray = textEditIsAnchored;
			textEditing = false;
			textEditIsAnchored = false;
			textEditElementId = null;
			textEditPropertyName = null;
			textEditNewItemParentId = null;
			textEditNewItemContainerType = null;
			textEditor.Visibility = Visibility.Collapsed;
			if (!commit || state == null)
				return;
			DesignerSessionState result;
			if (newItemParentId != null)
			{
				// Empty/whitespace text is a no-op, matching WinForms' CommitTypeHere.
				var newText = StripTypeHereCommit.Resolve(text, cancelled: false);
				if (newText == null)
					return;
				result = newItemContainerType is "StatusBar" or "ToolBar"
					? client.AddStripItemAsync(RequireVersion(), newItemParentId, newText).GetAwaiter().GetResult()
					: client.AddMenuItemAsync(RequireVersion(), newItemParentId, newText).GetAwaiter().GetResult();
			}
			else
			{
				result = client.SetPropertyAsync(RequireVersion(), elementId!, propertyName, text).GetAwaiter().GetResult();
			}
			if (!result.Accepted) { Show(result); return; }
			if (refreshContextMenuTray)
				contextMenuTrayRootId = null;
			Show(result);
			DocumentChanged?.Invoke(this, result);
			if (reopenIfNewItem && newItemParentId != null && newItemContainerType != null && result.Accepted)
			{
				// Show() just rebuilt the tray/hotspot - re-derive the trailing slot's new visual.
				if (contextMenuTrayContextMenuNode?.Id == newItemParentId && contextMenuTrayNewItemVisual != null)
					BeginNewMenuItemEdit(newItemParentId, newItemContainerType, contextMenuTrayNewItemVisual);
				else if (menuTypeHereHotspotMenuId == newItemParentId && menuTypeHereHotspot.Visibility == Visibility.Visible)
					BeginNewMenuItemEdit(newItemParentId, newItemContainerType, menuTypeHereHotspot);
			}
		}

		#endregion

		#region ContextMenu tray and "Type Here" hotspot

		/// <summary>The element whose direct MenuItem children the tray should show: a selected
		/// ContextMenu (its top-level items) or a selected MenuItem (its own submenu, empty or not -
		/// the trailing "Type Here" slot is how a submenu gets its first item). A WPF submenu, like a
		/// ContextMenu, is a detached popup invisible to hit-testing; only the top-level Menu bar's
		/// own items render inline (see <see cref="UpdateMenuTypeHereHotspot"/>).</summary>
		DesignerElementNode? TrayContainerForSelection()
		{
			var selected = SelectedNode;
			return selected?.Type is "ContextMenu" or "MenuItem" ? selected : null;
		}

		void UpdateContextMenuTray()
		{
			var contextMenu = TrayContainerForSelection();
			if (contextMenu == null)
			{
				HideContextMenuTray();
				return;
			}
			if (contextMenuTrayRootId == contextMenu.Id && contextMenuTray.IsVisible)
			{
				PlaceContextMenuTray();
				return;
			}
			contextMenuTrayRootId = contextMenu.Id;
			contextMenuTrayItems.Children.Clear();
			contextMenuTrayReorderGrips.Clear();
			var menuItems = contextMenu.Children.Where(c => c.Type == "MenuItem").ToList();
			for (var i = 0; i < menuItems.Count; i++)
			{
				var child = menuItems[i];
				var header = child.Properties.FirstOrDefault(p => p.Name == "Header")?.Value ?? child.Name ?? "MenuItem";
				var row = new Grid();
				// Star, not Auto, for the text column: once every row is stretched to a shared
				// uniform Width below, a star column absorbs the extra width itself, so the grip
				// (Auto) always sits flush against the row's right edge.
				row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				var text = new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 4, 8, 4) };
				Grid.SetColumn(text, 0);
				row.Children.Add(text);
				var grip = MakeReorderGrip();
				Grid.SetColumn(grip, 1);
				row.Children.Add(grip);
				var itemVisual = new Border { Background = Brushes.White, Child = row, Tag = child.Id };
				itemVisual.MouseEnter += (_, _) => itemVisual.Background = Brushes.AliceBlue;
				itemVisual.MouseLeave += (_, _) => itemVisual.Background = Brushes.White;
				itemVisual.MouseLeftButtonDown += (_, e) => {
					if (e.ClickCount == 2)
						BeginContextMenuTrayTextEdit(child, itemVisual);
					SelectElementId(child.Id);
					e.Handled = true;
				};
				var capturedIndex = i;
				grip.MouseLeftButtonDown += (_, e) => {
					trayReorderPending = true;
					trayReorderElementId = child.Id;
					trayReorderOriginalIndex = capturedIndex;
					trayReorderStartY = e.GetPosition(contextMenuTrayItems).Y;
					grip.CaptureMouse();
					ShowTrayInsertionLine(capturedIndex);
					e.Handled = true;
				};
				grip.MouseMove += (_, e) => {
					if (!trayReorderPending || trayReorderElementId != child.Id)
						return;
					var dragDelta = e.GetPosition(contextMenuTrayItems).Y - trayReorderStartY;
					ShowTrayInsertionLine(ComputeTrayReorderTargetIndex(dragDelta));
				};
				grip.MouseLeftButtonUp += (_, e) => {
					if (!trayReorderPending || trayReorderElementId != child.Id)
						return;
					trayReorderPending = false;
					grip.ReleaseMouseCapture();
					var dragDelta = e.GetPosition(contextMenuTrayItems).Y - trayReorderStartY;
					var targetIndex = ComputeTrayReorderTargetIndex(dragDelta);
					HideTrayInsertionLine();
					var delta = targetIndex - trayReorderOriginalIndex;
					var elementId = trayReorderElementId;
					trayReorderElementId = null;
					if (delta != 0)
						CommitMoveMenuItem(elementId, delta);
				};
				contextMenuTrayReorderGrips[child.Id] = grip;
				contextMenuTrayItems.Children.Add(itemVisual);
			}
			// One trailing "Type Here" slot after the real items, always present, so adding another
			// MenuItem never needs a separate command. Tagged "@type-here" (not a real element id)
			// so ContextMenuTrayStatus's test hook finds it the same way it finds real items. Not
			// reorderable, matching WinForms' own insertion cell.
			var typeHereVisual = new Border {
				Background = Brushes.White,
				Padding = new Thickness(14, 4, 32, 4),
				Cursor = Cursors.IBeam,
				ToolTip = "Type a name to add a new item; Enter keeps adding, Esc cancels.",
				Child = new TextBlock {
					Text = "Type Here", FontSize = 11, Foreground = Brushes.DimGray,
					VerticalAlignment = VerticalAlignment.Center
				},
				Tag = "@type-here"
			};
			typeHereVisual.MouseEnter += (_, _) => typeHereVisual.Background = Brushes.AliceBlue;
			typeHereVisual.MouseLeave += (_, _) => typeHereVisual.Background = Brushes.White;
			typeHereVisual.MouseLeftButtonDown += (_, e) => {
				BeginNewMenuItemEdit(contextMenu.Id, contextMenu.Type, typeHereVisual);
				e.Handled = true;
			};
			contextMenuTrayItems.Children.Add(typeHereVisual);
			contextMenuTrayNewItemVisual = typeHereVisual;
			contextMenuTrayContextMenuNode = contextMenu;
			// Uniform width across every row (including the "Type Here" slot), so the tray's right
			// edge and the grip column stay aligned regardless of header length.
			contextMenuTrayItems.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			var maxRowWidth = contextMenuTrayItems.Children.OfType<FrameworkElement>()
				.Select(c => c.DesiredSize.Width).DefaultIfEmpty(0.0).Max();
			foreach (var rowVisual in contextMenuTrayItems.Children.OfType<FrameworkElement>())
				rowVisual.Width = maxRowWidth;
			contextMenuTray.Visibility = Visibility.Visible;
			PlaceContextMenuTray();
		}

		/// <summary>The tray floats at the page's top-left corner, like the popup it stands in for.</summary>
		void PlaceContextMenuTray()
		{
			var origin = DesignToContentPoint(0, 0);
			Canvas.SetLeft(contextMenuTray, origin.X);
			Canvas.SetTop(contextMenuTray, origin.Y);
		}

		void HideContextMenuTray()
		{
			contextMenuTrayRootId = null;
			contextMenuTrayItems.Children.Clear();
			contextMenuTrayReorderGrips.Clear();
			contextMenuTrayNewItemVisual = null;
			contextMenuTrayContextMenuNode = null;
			contextMenuTray.Visibility = Visibility.Collapsed;
			trayReorderPending = false;
			trayReorderElementId = null;
		}

		static Border MakeReorderGrip() => new() {
			Width = 20,
			Background = Brushes.Transparent,
			Cursor = Cursors.SizeNS,
			ToolTip = "Drag to reorder",
			Child = new TextBlock {
				Text = "⋮⋮", // two vertical-dots glyphs side by side, a conventional drag-grip affordance
				FontSize = 12,
				Foreground = Brushes.Gray,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			}
		};

		/// <summary>Real (non-"Type Here") rows' top-to-bottom extents in
		/// <see cref="contextMenuTrayItems"/>'s own coordinate space - the axis-agnostic input
		/// <see cref="ReorderGestureCalculator"/> needs.</summary>
		List<(double Start, double Length)> TrayReorderableRowExtents()
			=> contextMenuTrayItems.Children.OfType<Border>()
				.Where(b => b.Tag is string tag && tag != "@type-here")
				.Select(b => (b.TranslatePoint(new Point(0, 0), contextMenuTrayItems).Y, b.ActualHeight))
				.ToList();

		int ComputeTrayReorderTargetIndex(double dragDelta)
		{
			var extents = TrayReorderableRowExtents();
			if (trayReorderOriginalIndex >= extents.Count)
				return trayReorderOriginalIndex;
			return ReorderGestureCalculator.ComputeTargetIndex(extents, trayReorderOriginalIndex, dragDelta);
		}

		void ShowTrayInsertionLine(int targetIndex)
		{
			var extents = TrayReorderableRowExtents();
			if (extents.Count == 0)
			{
				contextMenuTrayInsertionLine.Visibility = Visibility.Collapsed;
				return;
			}
			var y = targetIndex < extents.Count
				? extents[targetIndex].Start
				: extents[^1].Start + extents[^1].Length;
			contextMenuTrayInsertionLine.Margin = new Thickness(0, y - 1, 0, 0);
			contextMenuTrayInsertionLine.Visibility = Visibility.Visible;
		}

		void HideTrayInsertionLine() => contextMenuTrayInsertionLine.Visibility = Visibility.Collapsed;

		/// <summary>Commits one tray reorder (<c>design/move-element</c>), forcing a tray rebuild
		/// even though the selected container itself hasn't changed.</summary>
		void CommitMoveMenuItem(string elementId, int delta)
		{
			if (state == null)
				return;
			var result = client.MoveElementAsync(RequireVersion(), elementId, delta).GetAwaiter().GetResult();
			if (!result.Accepted) { Show(result); return; }
			contextMenuTrayRootId = null;
			Show(result);
			DocumentChanged?.Invoke(this, result);
		}

		static bool IsHotspotContainerType(string? type) => type is "Menu" or "StatusBar" or "ToolBar";

		/// <summary>Positions the "Type Here" hotspot just past the LAST rendered child of a
		/// selected top-level Menu/StatusBar/ToolBar (or of the one containing the selection), or
		/// hides it. The container's OWN Width is not usable: a top-level bar stretches to its
		/// parent's width regardless of its items, which put the hotspot at the far edge of the
		/// window (outside the frame for an empty Menu). Coexists with the tray when a top-level
		/// MenuItem is selected: this adds a sibling, the tray adds the first item inside it.</summary>
		void UpdateMenuTypeHereHotspot()
		{
			var selected = SelectedNode;
			var container = IsHotspotContainerType(selected?.Type) ? selected : null;
			if (container == null && selected != null && state?.Tree != null
				&& FindParent(state.Tree, selected) is { } parent && IsHotspotContainerType(parent.Type))
			{
				container = parent;
			}
			if (container == null)
			{
				HideMenuTypeHereHotspot();
				return;
			}
			menuTypeHereHotspotMenuId = container.Id;
			menuTypeHereHotspotContainerType = container.Type;
			var rightEdge = container.Children.Count > 0
				? container.Children.Max(c => c.X + c.Width)
				: container.X;
			menuTypeHereHotspotDesignPoint = new Point(rightEdge, container.Y);
			menuTypeHereHotspotContainerLeft = container.X;
			menuTypeHereHotspotContainerRight = container.X + container.Width;
			menuTypeHereHotspot.Visibility = Visibility.Visible;
			PlaceMenuTypeHereHotspot();
			// A strip docked at the bottom (a StatusBar) can be below the visible canvas in a short
			// pane such as the split view, leaving its hotspot unreachable. Scroll to it then - but
			// ONLY then: scrolling for a hotspot that is already on screen moved the canvas between
			// the two clicks of a double-click on a menu item.
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => {
				if (menuTypeHereHotspot.IsVisible && menuTypeHereHotspot.ActualWidth > 0 && !IsInCanvasViewport(menuTypeHereHotspot))
					menuTypeHereHotspot.BringIntoView();
			}));
		}

		bool IsInCanvasViewport(FrameworkElement element)
		{
			DependencyObject node = element;
			while (node != null && node is not ScrollViewer)
				node = VisualTreeHelper.GetParent(node);
			if (node is not ScrollViewer scroller || !scroller.IsVisible)
				return true;   // nothing to scroll
			var bounds = element.TransformToAncestor(scroller).TransformBounds(new Rect(element.RenderSize));
			return bounds.Top >= 0 && bounds.Left >= 0
				&& bounds.Bottom <= scroller.ViewportHeight + 0.5 && bounds.Right <= scroller.ViewportWidth + 0.5;
		}

		void PlaceMenuTypeHereHotspot()
		{
			var origin = DesignToContentPoint(menuTypeHereHotspotDesignPoint.X, menuTypeHereHotspotDesignPoint.Y);
			// Keep the hotspot inside its container. A StatusBar's last item (and a full ToolBar)
			// stretches to the container's right edge, so "just past the last item" was outside the
			// form - beyond the canvas extent, where it could not be scrolled to and a click landed
			// on the scrollbar instead.
			if (menuTypeHereHotspot.ActualWidth <= 0)
				menuTypeHereHotspot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			var width = menuTypeHereHotspot.ActualWidth > 0 ? menuTypeHereHotspot.ActualWidth : menuTypeHereHotspot.DesiredSize.Width;
			var containerRight = DesignToContentPoint(menuTypeHereHotspotContainerRight, menuTypeHereHotspotDesignPoint.Y).X;
			var containerLeft = DesignToContentPoint(menuTypeHereHotspotContainerLeft, menuTypeHereHotspotDesignPoint.Y).X;
			// A container narrower than the hotspot (an empty Menu) keeps the hotspot past its edge.
			if (containerRight - containerLeft >= width && origin.X + width > containerRight)
				origin.X = containerRight - width;
			Canvas.SetLeft(menuTypeHereHotspot, origin.X);
			Canvas.SetTop(menuTypeHereHotspot, origin.Y);
		}

		void HideMenuTypeHereHotspot()
		{
			menuTypeHereHotspot.Visibility = Visibility.Collapsed;
			menuTypeHereHotspotMenuId = null;
			menuTypeHereHotspotContainerType = null;
		}

		static DesignerElementNode? FindParent(DesignerElementNode node, DesignerElementNode target)
		{
			foreach (var child in node.Children)
			{
				if (ReferenceEquals(child, target))
					return node;
				if (FindParent(child, target) is { } parent)
					return parent;
			}
			return null;
		}

		#endregion

		#region Properties pad

		/// <summary>An <see cref="System.ComponentModel.ICustomTypeDescriptor"/> for the shared
		/// Properties pad, backed by the selected element's DDP property list, or null. A fresh
		/// adapter per access, since the node instance changes on every re-render.</summary>
		public WpfSurfaceElementPropertyAdapter? SelectedPropertyAdapter =>
			SelectedNode is { } node
				? new WpfSurfaceElementPropertyAdapter(client, () => state!.Version, node, OnPropertyEdited)
				: null;

		public object[] SelectedPropertyAdapters => state?.Tree == null ? Array.Empty<object>() : SelectedElementIds
			.Select(NodeById)
			.Where(node => node != null)
			.Select(node => (object)new WpfSurfaceElementPropertyAdapter(client, () => state!.Version, node!, OnPropertyEdited))
			.ToArray();

		/// <summary>Renders a property edit's result, then raises <see cref="SelectionChanged"/>
		/// and <see cref="DocumentChanged"/> so the Properties pad refreshes and the file is marked
		/// dirty, like every other mutation path.</summary>
		void OnPropertyEdited(DesignerSessionState newState)
		{
			Show(newState);
			NotifySelectionChanged();
			DocumentChanged?.Invoke(this, newState);
		}

		#endregion

		#region Mutations (design/set-bounds, design/add-element, design/delete-elements, design/rename, design/theme)

		/// <summary>Moves/resizes the given element (<c>design/set-bounds</c>). Coordinates are
		/// design units. Does NOT render the result - see <see cref="Show"/>.</summary>
		public async Task<DesignerSessionState> SetBoundsAsync(string elementId, double x, double y, double width, double height, CancellationToken cancellationToken = default)
		{
			var result = await client.SetBoundsAsync(RequireVersion(), elementId, x, y, width, height, cancellationToken).ConfigureAwait(false);
			// A stale/rejected mutation describes no new design frame.  Keep the last accepted
			// state as the authority for following gestures instead of poisoning it with the
			// rejection's caller version.
			if (result.Accepted)
				state = result;
			return result;
		}

		/// <summary>Inserts a new element under <paramref name="parentId"/> (<c>design/add-element</c>).
		/// Does not change the selection. Does NOT render the result - see <see cref="Show"/>.</summary>
		public async Task<DesignerSessionState> AddElementAsync(string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, CancellationToken cancellationToken = default)
		{
			var result = await client.AddElementAsync(RequireVersion(), parentId, item, proposedName, x, y, dropTarget: null, cancellationToken).ConfigureAwait(false);
			if (result.Accepted) state = result;
			return result;
		}

		/// <summary>Removes the whole selection (<c>design/delete-elements</c>). Clears the
		/// selection first - the deleted elements' paths are meaningless once the tree is rebuilt.
		/// Does NOT raise <see cref="SelectionChanged"/> or render - see <see cref="Show"/>. Call on
		/// the dispatcher thread (the selection is read and cleared before the first await).</summary>
		public async Task<DesignerSessionState?> DeleteSelectedAsync(CancellationToken cancellationToken = default)
		{
			var ids = controller.SelectedNames.ToArray();
			if (ids.Length == 0)
				return null;
			controller.RestoreSelection(Array.Empty<string>());
			var result = await client.DeleteElementsAsync(RequireVersion(), ids, cancellationToken).ConfigureAwait(false);
			if (result.Accepted) state = result;
			else controller.RestoreSelection(ids);
			return result;
		}

		/// <summary>Renames the selected element (<c>design/rename</c>), keeping the selection.
		/// Does NOT render - see <see cref="Show"/>.</summary>
		public async Task<DesignerSessionState?> RenameSelectedAsync(string newName, CancellationToken cancellationToken = default)
		{
			if (controller.SelectedElementName is not { } id)
				return null;
			var result = await client.RenameAsync(RequireVersion(), id, newName, cancellationToken).ConfigureAwait(false);
			if (result.Accepted) state = result;
			return result;
		}

		/// <summary>Moves every selected non-root element by the requested design-space delta.
		/// Keyboard input and automation intentionally share this path so a multi-selection is
		/// committed through the host's single atomic bounds transaction, never as one RPC per
		/// element.</summary>
		public DesignerSessionState? NudgeSelection(double dx, double dy)
		{
			return CommitBoundsForEach(SelectedBoundsForLayout()
				.Where(item => item.Path != RootElementId)
				.Select(item => (item.Path, new Rect(item.Bounds.X + dx, item.Bounds.Y + dy, item.Bounds.Width, item.Bounds.Height)))
				.ToList());
		}

		/// <summary>Switches the design-time theme by name (<c>design/theme</c>) - rejected when the
		/// project embeds no such theme. Does NOT render - see <see cref="Show"/>.</summary>
		public async Task<DesignerSessionState> SetThemeAsync(string theme, CancellationToken cancellationToken = default)
		{
			var result = await client.SetThemeAsync(RequireVersion(), theme, cancellationToken).ConfigureAwait(false);
			if (result.Accepted) state = result;
			return result;
		}

		long RequireVersion() => state?.Version
			?? throw new InvalidOperationException("No document is open - call OpenAsync first.");

		/// <summary>Commits one move/resize. Blocking, on the dispatcher thread - see <see cref="Show"/>.</summary>
		void CommitBounds(string elementId, Rect bounds)
		{
			if (state == null)
				return;
			var result = SetBoundsAsync(elementId, bounds.X, bounds.Y, bounds.Width, bounds.Height).GetAwaiter().GetResult();
			if (!result.Accepted) { Show(result); return; }
			Show(result);
			DocumentChanged?.Invoke(this, result);
		}

		/// <summary>Commits a batch of bounds edits with a single re-render at the end - safe to
		/// compute every target up front: a bounds-only edit changes no element's tree path.</summary>
		DesignerSessionState? CommitBoundsForEach(IReadOnlyList<(string Path, Rect NewBounds)> edits)
		{
			if (edits.Count == 0 || state == null)
				return null;
			var result = edits.Count == 1
				? SetBoundsAsync(edits[0].Path, edits[0].NewBounds.X, edits[0].NewBounds.Y, edits[0].NewBounds.Width, edits[0].NewBounds.Height).GetAwaiter().GetResult()
				: client.SetBoundsBatchAsync(RequireVersion(), edits.Select(edit => new DesignerBoundsEdit {
					ElementId = edit.Path, X = edit.NewBounds.X, Y = edit.NewBounds.Y,
					Width = edit.NewBounds.Width, Height = edit.NewBounds.Height
				}).ToArray()).GetAwaiter().GetResult();
			if (!result.Accepted) { Show(result); return result; }
			Show(result);
			DocumentChanged?.Invoke(this, result);
			return result;
		}

		void CommitDelete()
		{
			var result = DeleteSelectedAsync().GetAwaiter().GetResult();
			if (result is not { Accepted: true }) { if (result != null) Show(result); return; }
			NotifySelectionChanged();
			Show(result);
			DocumentChanged?.Invoke(this, result);
		}

		void CommitTheme(string theme)
		{
			if (state == null)
				return;
			var result = SetThemeAsync(theme).GetAwaiter().GetResult();
			if (!result.Accepted) { Show(result); return; }
			Show(result);
			DocumentChanged?.Invoke(this, result);
		}

		#endregion

		#region Toolbox drop

		void OnDragOver(object sender, DragEventArgs e)
		{
			e.Effects = state != null && ToolboxItemOf(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
			e.Handled = true;
		}

		void OnDrop(object sender, DragEventArgs e)
		{
			if (state == null || ToolboxItemOf(e.Data) is not { } item)
				return;
			e.Handled = true;
			var design = ToDesignPoint(e.GetPosition(this));
			CommitDrop(item, design.X, design.Y);
		}

		/// <summary>Drops onto whatever element is under the pointer, falling back through its
		/// ancestors to the document root - the child decides whether each candidate accepts the
		/// new child (PlacementOperation), so no container knowledge is duplicated here.</summary>
		void CommitDrop(DesignerToolboxItemInfo item, double designX, double designY)
		{
			var hit = controller.FindNodeAtDesignPoint(designX, designY);
			var initialParentId = String.IsNullOrEmpty(hit?.Path) ? OutlineRoot?.Id ?? "" : hit.Path;
			// The visual hit is commonly a leaf (TextBlock inside a Button), not the panel the user
			// visually dropped onto: try the hit first, then each structural ancestor.
			var candidateParents = new List<string>();
			for (var candidate = initialParentId; ;)
			{
				if (!candidateParents.Contains(candidate))
					candidateParents.Add(candidate);
				var separator = candidate.LastIndexOf(',');
				if (separator < 0)
					break;
				candidate = candidate.Substring(0, separator);
			}
			// Panels without a Background are transparent to WPF's visual hit testing, so an empty-
			// canvas drop reports the Window itself even over its content Grid. The DDP tree holds
			// each item's arranged bounds, so append every containing node, deepest first.
			AppendLayoutDropCandidates(OutlineRoot, designX, designY, candidateParents);
			var rootId = OutlineRoot?.Id ?? "";
			if (!candidateParents.Contains(rootId))
				candidateParents.Add(rootId);

			DesignerSessionState? result = null;
			foreach (var parentId in candidateParents)
			{
				result = AddElementAsync(parentId, item, proposedName: "", designX, designY).GetAwaiter().GetResult();
				if (result.Accepted)
					break;
			}
			if (result == null || !result.Accepted)
			{
				StatusText = string.Format(ResourceService.GetString("WpfDesign.Status.HostError"), BackendName, result?.Error ?? ResourceService.GetString("WpfDesign.Status.DropRejected"));
				return;
			}
			Show(result);
			// A toolbox drop selects the element it just created. CreatedElementId is only
			// meaningful on this exact response (WPF cannot look the element up by name afterward).
			if (result.CreatedElementId != null)
				controller.RestoreSelection(new[] { result.CreatedElementId });
			NotifySelectionChanged();
			UpdateSelectionChrome();
			DocumentChanged?.Invoke(this, result);
		}

		static void AppendLayoutDropCandidates(DesignerElementNode? node, double x, double y, List<string> candidates)
		{
			if (node == null || node.Width <= 0 || node.Height <= 0
				|| x < node.X || y < node.Y || x > node.X + node.Width || y > node.Y + node.Height)
				return;
			foreach (var child in node.Children)
				AppendLayoutDropCandidates(child, x, y, candidates);
			if (!candidates.Contains(node.Id))
				candidates.Add(node.Id);
		}

		static DesignerToolboxItemInfo? ToolboxItemOf(IDataObject data)
			=> data?.GetDataPresent(typeof(DesignerToolboxItemInfo)) == true
				? data.GetData(typeof(DesignerToolboxItemInfo)) as DesignerToolboxItemInfo
				: null;

		#endregion

		#region Align / distribute / match-size (od.wpf-designer.align/.distribute/.match-size)

		/// <summary>The selection's ids and CURRENT bounds, primary first.</summary>
		List<(string Path, Rect Bounds)> SelectedBoundsForLayout()
			=> controller.SelectedNames
				.Select(NodeById)
				.Where(node => node != null)
				.Select(node => (node!.Id, new Rect(node.X, node.Y, node.Width, node.Height)))
				.ToList();

		/// <summary>Aligns every other selected element's edge/center to the PRIMARY selection's.
		/// mode: "left"/"center"/"right" (horizontal) or "top"/"middle"/"bottom" (vertical).</summary>
		public void AlignSelection(string mode)
		{
			var selected = SelectedBoundsForLayout();
			if (selected.Count < 2)
				return;
			var anchor = selected[0].Bounds;
			var edits = new List<(string, Rect)>();
			foreach (var (path, bounds) in selected.Skip(1))
			{
				var rect = bounds;
				switch (mode)
				{
					case "left": rect.X = anchor.X; break;
					case "center": rect.X = anchor.X + anchor.Width / 2 - bounds.Width / 2; break;
					case "right": rect.X = anchor.X + anchor.Width - bounds.Width; break;
					case "top": rect.Y = anchor.Y; break;
					case "middle": rect.Y = anchor.Y + anchor.Height / 2 - bounds.Height / 2; break;
					case "bottom": rect.Y = anchor.Y + anchor.Height - bounds.Height; break;
					default: continue;
				}
				edits.Add((path, rect));
			}
			CommitBoundsForEach(edits);
		}

		/// <summary>Equal-CENTER spacing along <paramref name="axis"/> ("horizontal"/"vertical"):
		/// moves only the interior elements - requires at least 3 selected elements.</summary>
		public void DistributeSelection(string axis)
		{
			var selected = SelectedBoundsForLayout();
			if (selected.Count < 3)
				return;
			var horizontal = axis == "horizontal";
			var ordered = selected
				.Select(item => (item.Path, item.Bounds,
					Center: horizontal ? item.Bounds.X + item.Bounds.Width / 2 : item.Bounds.Y + item.Bounds.Height / 2))
				.OrderBy(item => item.Center)
				.ToList();
			var min = ordered[0].Center;
			var max = ordered[^1].Center;
			var step = (max - min) / (ordered.Count - 1);
			var edits = new List<(string, Rect)>();
			for (var i = 1; i < ordered.Count - 1; i++)
			{
				var target = min + step * i;
				var rect = ordered[i].Bounds;
				if (horizontal)
					rect.X = target - rect.Width / 2;
				else
					rect.Y = target - rect.Height / 2;
				edits.Add((ordered[i].Path, rect));
			}
			CommitBoundsForEach(edits);
		}

		/// <summary>Resizes every other selected element to match the PRIMARY selection's
		/// width/height/both. mode: "width"/"height"/"both".</summary>
		public void MatchSizeSelection(string mode)
		{
			var selected = SelectedBoundsForLayout();
			if (selected.Count < 2)
				return;
			var anchor = selected[0].Bounds;
			var edits = new List<(string, Rect)>();
			foreach (var (path, bounds) in selected.Skip(1))
			{
				var rect = bounds;
				if (mode is "width" or "both")
					rect.Width = anchor.Width;
				if (mode is "height" or "both")
					rect.Height = anchor.Height;
				edits.Add((path, rect));
			}
			CommitBoundsForEach(edits);
		}

		#endregion
	}
}
