using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Drawing.Design;

using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.SharpDevelop.Designer.Surface;
using System.Threading.Tasks;

namespace ICSharpCode.FormsDesigner.OutOfProcess
{
	sealed class RemoteFormsDesignerControl : DesignSurface, IDesignCanvasBackend
	{
		readonly FormsDesignerHostClient client;
		readonly Canvas adorners;
		/// <summary>One live overlay per currently-expanded menu dropdown (see
		/// DesignerSessionState.Popups), keyed by OwnerElementId. Each is a real WPF Image that is
		/// a child of <see cref="adorners"/> - which is why it needs no new click-suppression
		/// guard in OnMouseLeftButtonDown: that handler already treats anything under adorners as
		/// self-handling (see IsAdornerSource) - and receives its own MouseLeftButtonDown to
		/// hit-test/select directly against that popup's own surface, without going through the
		/// root form's coordinate space at all.</summary>
		readonly Dictionary<string, Image> popupOverlays = new(StringComparer.Ordinal);
		/// <summary>One real WPF edit cell per popup that reports a TypeHereBounds - the WPF
		/// analogue of the real template node's own "Type Here" cell for THAT dropdown level,
		/// keyed the same way as <see cref="popupOverlays"/>. A screenshot-based render pipeline
		/// cannot show the real control's blinking caret or accept keystrokes aimed at it, so
		/// typing happens entirely in this WPF TextBox and commits through the existing
		/// design/add-toolstrip-item RPC (parentItemId = the popup's own OwnerElementId) rather
		/// than by forwarding input to the real template node.</summary>
		readonly Dictionary<string, PopupTypeHereEditor> popupEditors = new(StringComparer.Ordinal);
		// The component tray - the icon+name strip below the design surface that holds every
		// non-visual component (Timer/ImageList/ToolTip/dialogs) plus the Controls whose designer
		// is not a ControlDesigner (ContextMenuStrip, PrintPreviewDialog). It is deliberately a
		// SIBLING of the zoomable scroller rather than part of its content, mirroring how the real
		// designer hosts System.Windows.Forms.Design.ComponentTray through
		// ISplitWindowService.AddSplitWindow: the tray keeps its own scrollbar and its own fixed
		// item size, unaffected by the canvas zoom.
		readonly Border trayRegion;
		// A grid of equal cells: ItemWidth/ItemHeight make every entry the same size, and
		// LayoutTrayCells divides the available width evenly among as many columns as fit.
		readonly WrapPanel trayItems = new WrapPanel {
			Orientation = Orientation.Horizontal, Margin = new Thickness(TrayPadding), ItemHeight = TrayCellHeight
		};
		readonly ScrollViewer trayScroller = new ScrollViewer {
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
		};
		const double TrayCellHeight = 26;
		const double TrayMinCellWidth = 150;
		const double TrayPadding = 3;
		/// <summary>The tray grows with its rows up to this many, then scrolls.</summary>
		const int TrayMaxVisibleRows = 3;
		/// <summary>The same selection-blue WinUI's own UnoDesignSurfaceControl uses
		/// (Color.FromRgb(0x00, 0x78, 0xD4)) - unifies the two designers' selection look, which
		/// previously differed (this one used the brighter stock <c>Brushes.DodgerBlue</c>).</summary>
		static readonly SolidColorBrush SelectionBrush = MakeFrozenBrush(0x00, 0x78, 0xD4);

		static SolidColorBrush MakeFrozenBrush(byte r, byte g, byte b)
		{
			var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
			brush.Freeze();
			return brush;
		}

		/// <summary>Drag-to-reorder for a selected ToolStripItem (never a Control, so the canvas's
		/// own move - which drives design/set-bounds - is not applicable): covers the item's bounds,
		/// but only ever accumulates a horizontal offset and, on drop, asks the real
		/// designer to move the item to a new INDEX among its siblings via
		/// design/reorder-toolstrip-item, rather than a pixel position.</summary>
		readonly Thumb reorderThumb;
		double reorderDragDeltaX;
		/// <summary>Drag-to-reorder for a selected item that is currently INSIDE an open popup
		/// (a MenuStrip submenu/ContextMenuStrip's own items) rather than laid out on a root
		/// strip - vertical, matching how a dropdown stacks its items top to bottom (the opposite
		/// orientation from <see cref="reorderThumb"/>'s root-strip case). Positioned from the
		/// same rect reorderThumb uses - a popup item's own SurfaceX/Y/Width/
		/// Height are already reported in the same absolute basis a root item's are (see
		/// OnPopupReorderDragCompleted's own note), so no separate coordinate source is needed.</summary>
		readonly Thumb popupReorderThumb;
		double popupReorderDeltaY;
		bool popupReorderPointerDown;
		/// <summary>Live drag feedback for both reorder gestures above: a thin line shown at the
		/// CURRENT drop boundary while dragging (real VS shows the same insertion-line cue), not
		/// just applied silently on drop. Vertical (a "|") for reorderThumb's horizontal drags,
		/// horizontal (a "-") for popupReorderThumb's vertical ones - toggled by rotating Width/
		/// Height between the two axes in <see cref="ShowReorderInsertionLine"/> rather than two
		/// separate shapes.</summary>
		readonly Rectangle insertionLine;
		readonly Border disconnectedOverlay;
		readonly TextBlock disconnectedText;
		// The VS "smart tag" chevron (DesignerActionList popup) and the ToolStrip/StatusStrip/
		// MenuStrip "insert new item" chevron. Both are plain Borders (not Button - a Button's
		// default theme chrome is exactly the opaque-rectangle trap CreateTransparentThumbTemplate
		// describes) positioned by PositionAdorners in the canvas's extension layer.
		readonly Border smartTagChevron;
		readonly Border toolStripInsertChevron;
		// The MenuStrip flavour of the same affordance. ToolStripTemplateNode.SetupNewEditNode
		// branches exactly this way: a MenuStrip (and any dropdown) gets SetUpMenuTemplateNode's
		// editable "Type Here" cell, while ToolStrip/StatusStrip/ContextMenuStrip get
		// SetUpToolTemplateNode's split button (toolStripInsertChevron above). Only one of the two
		// is ever visible for a given strip.
		/// <summary>F2's in-place rename editor, matching real VS's Properties-pad-adjacent
		/// behavior: shown directly over the current selection (any component with a Parent - a
		/// Control or a ToolStripItem alike, both report SurfaceX/Y/Width/Height), prefilled with
		/// its current name. Enter/Tab commits via RenameRequested (which DesignerViewContent wires
		/// to the SAME RenameRemoteComponent the Properties pad's "(Name)" row already uses); Esc
		/// or losing focus cancels - matching typeHereEditor's own click-away-cancels behavior.</summary>
		readonly TextBox renameEditor;
		bool renaming;
		string renameComponentName;
		internal object ItemEditorStatus {
			get {
				// The template node can be at the bottom of a small canvas (most notably a
				// StatusStrip).  Return coordinates only after it has been brought into the
				// actual ScrollViewer viewport, so automation and a real user click the same
				// visible glyph rather than an off-screen/stale position.
				EnsureToolStripInsertionNodeVisible();
				return new {
					selectedName = SelectedComponentName,
					editing = renaming,
					componentName = renameComponentName,
					visible = renameEditor.IsVisible,
					focused = renameEditor.IsKeyboardFocusWithin,
					text = renameEditor.Text,
					selectedText = selectedComponent?.Text,
					popupOwners = state?.Popups?.Select(popup => popup.OwnerElementId).ToArray(),
					insertion = InsertionNodeStatus(),
					// The MenuStrip/ContextMenuStrip "Type Here" cell (typeHereCell/typeHereEditor)
					// had no status surface at all before - the bug where it was never shown, then
					// the bug where clicking it lost focus almost immediately, both had to be found
					// by manual reasoning about the code instead of an automated check. Exposed here
					// so a pointer-driven integration test can assert this cell's own visibility,
					// bounds and focus the same way insertion/PopupTypeHereStatus already do for the
					// other two "Type Here" surfaces.
					typeHereCell = TypeHereCellStatus()
				};
			}
		}
		internal object PopupTypeHereStatus => new {
			items = popupEditors.Select(entry => entry.Value.Status(entry.Key)).Where(item => item != null).ToArray(),
			// Overlays live inside the canvas scroller and are clipped by it: a cell's screen bounds
			// alone do not say a click there reaches it.
			canvasViewport = new {
				offsetY = canvasScroller.VerticalOffset, viewportHeight = canvasScroller.ViewportHeight,
				extentHeight = canvasScroller.ExtentHeight, scrollableHeight = canvasScroller.ScrollableHeight
			},
			cellsInView = popupEditors.Values.Select(editor => editor.Cell.IsVisible && IsInCanvasViewport(editor.Cell)).ToArray()
		};
		bool IsInCanvasViewport(FrameworkElement element)
		{
			if (!canvasScroller.IsVisible || element.ActualWidth <= 0) return false;
			var bounds = element.TransformToAncestor(canvasScroller).TransformBounds(new Rect(element.RenderSize));
			return bounds.Top >= 0 && bounds.Bottom <= canvasScroller.ViewportHeight + 0.5
				&& bounds.Left >= 0 && bounds.Right <= canvasScroller.ViewportWidth + 0.5;
		}
		/// <summary>Names currently rendered in the component tray. Kept separate from the
		/// document component list so integration tests can assert the tray ownership rule.</summary>
		internal object ComponentTrayStatus => new {
			visible = trayRegion.Visibility == Visibility.Visible,
			items = trayItems.Children.OfType<Border>().Select(item => item.Tag as string)
				.Where(name => !String.IsNullOrEmpty(name)).ToArray()
		};
		internal bool InputPopupTypeHere(string text, bool cancel) => popupEditors.Values.Any(editor => editor.Input(text, cancel));
		object TypeHereCellStatus()
		{
			if (!typeHereCell.IsVisible) return null;
			var origin = typeHereCell.PointToScreen(new Point(0, 0));
			var end = typeHereCell.PointToScreen(new Point(typeHereCell.ActualWidth, typeHereCell.ActualHeight));
			return new {
				x = origin.X, y = origin.Y, width = end.X - origin.X, height = end.Y - origin.Y,
				editing = typeHereEditing,
				editorVisible = typeHereEditor.IsVisible,
				editorFocused = typeHereEditor.IsKeyboardFocusWithin,
				text = typeHereEditor.Text
			};
		}

		object InsertionNodeStatus()
		{
			if (!toolStripInsertChevron.IsVisible) return null;
			var origin = toolStripInsertChevron.PointToScreen(new Point(0, 0));
			var end = toolStripInsertChevron.PointToScreen(new Point(toolStripInsertChevron.ActualWidth, toolStripInsertChevron.ActualHeight));
			return new { x = origin.X, y = origin.Y, width = end.X - origin.X, height = end.Y - origin.Y };
		}
		readonly Border typeHereCell;
		readonly TextBlock typeHereLabel;
		/// <summary>The in-place editor swapped in for <see cref="typeHereLabel"/> while typing -
		/// the WPF analogue of ToolStripTemplateNode swapping _centerLabel for _centerTextBox.</summary>
		readonly TextBox typeHereEditor;
		bool typeHereEditing;
		/// <summary>Placeholder text of the "Type Here" cell, matching
		/// SR.ToolStripDesignerTemplateNodeEnterText.</summary>
		const string TypeHereText = "Type Here";
		/// <summary>The ToolStrip/MenuStrip/StatusStrip the insert-item glyph currently targets -
		/// either the selected component itself, or (when a child ToolStripItem is selected
		/// instead, matching real VS behavior) that item's owning strip.  NULL hides the glyph.</summary>
		DesignerComponentInfo toolStripHost;
		internal long version;
		// internal, not private: PopupTypeHereEditor (a same-file, same-assembly sibling class,
		// not nested - matching the existing convention every other Remote*EventArgs class here
		// follows) needs the current state to resolve the strip that owns a popup's dropdown
		// chain, walking Parent up from the item being edited.
		internal DesignerSessionState state;
		long lastFrameSequence;
		DesignerComponentInfo selectedComponent;
		readonly HashSet<string> selectedComponentNames = new HashSet<string>(StringComparer.Ordinal);
		readonly HashSet<string> lockedComponentNames = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>A Thumb template that draws nothing but a transparent hit-target fill, so the
		/// thumb stays invisible while still receiving mouse input. The default WPF Thumb theme
		/// template paints its chrome from SystemColors brushes, not from TemplateBinding Background,
		/// so Background=Transparent alone does nothing: under the dark theme that chrome rendered as
		/// an opaque dark rectangle over the whole selected control.</summary>
		static ControlTemplate CreateTransparentThumbTemplate()
		{
			var surface = new FrameworkElementFactory(typeof(Border));
			surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
			return new ControlTemplate(typeof(Thumb)) { VisualTree = surface };
		}

		/// <summary>
		/// The WinForms designer on the shared design canvas (<see cref="DesignSurface"/> plus a
		/// name-keyed <see cref="DesignSurfaceController"/>; doc/technotes/designer-canvas-addin.md).
		/// The canvas draws the frame, zooms, selects (click, Ctrl-click, marquee), moves and resizes
		/// with snapping; this class turns those gestures into the WinForms host's own edits
		/// (<see cref="BoundsChanged"/>, <see cref="SelectionMoveRequested"/>) and keeps what only
		/// WinForms has - tab headers, locked components, the ToolStrip editors and popups, smart
		/// tags, the component tray - as extensions (its <see cref="DesignSurface.ExtensionLayer"/>
		/// and the tray below the canvas).
		/// </summary>
		public RemoteFormsDesignerControl(FormsDesignerHostClient client, string backendName)
		{
			this.client = client;
			BackendName = backendName;
			controller = new DesignSurfaceController(this, this, DesignSurfaceKeying.Name);
			adorners = ExtensionLayer;
			Capabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit
				| DesignerCanvasCapabilities.StatusBar | DesignerCanvasCapabilities.ShowNames;
			StatusText = $"Starting {BackendName} design host…";
			// Undo/redo is an IDE command for this designer, and Delete, arrows, Tab, Esc, F2 and
			// Ctrl+. are its own (OnKeyDown): the canvas must not consume them first. Right-click
			// raises ContextMenuRequested, and the host builds that menu from the designer verbs.
			HandlesUndoRedoKeys = false;
			ContextMenu = null;
			// A tab header is painted by its TabControl and is not a component: a press on one
			// switches the page instead of starting a selection or drag.
			DesignPressInterceptor = TryInterceptTabHeaderPress;

			// A native ToolStrip template node is rendered into the bitmap, while this
			// transparent WPF proxy is only an input target. Route by its transformed bounds
			// at the adorner root as well: an invisible Border can lose the ordinary bubbling
			// hit test when a selected strip's Thumb overlaps it.
			adorners.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
				new MouseButtonEventHandler(OnAdornerPreviewMouseLeftButtonDown), true);
			reorderThumb = new Thumb {
				Background = Brushes.Transparent,
				Cursor = Cursors.SizeWE,
				Visibility = Visibility.Collapsed,
				Template = CreateTransparentThumbTemplate()
			};
			popupReorderThumb = new Thumb {
				Background = Brushes.Transparent,
				Cursor = Cursors.SizeNS,
				Visibility = Visibility.Collapsed,
				Template = CreateTransparentThumbTemplate()
			};
			insertionLine = new Rectangle {
				Fill = SelectionBrush, Visibility = Visibility.Collapsed, IsHitTestVisible = false
			};
			adorners.Children.Add(reorderThumb);
			adorners.Children.Add(popupReorderThumb);
			adorners.Children.Add(insertionLine);
			smartTagChevron = CreateSmartTagGlyph();
			smartTagChevron.MouseLeftButtonDown += (sender, args) => {
				args.Handled = true;
				if (selectedComponent != null)
					SmartTagRequested?.Invoke(this, new RemoteSmartTagRequestedEventArgs(selectedComponent.Name, smartTagChevron));
			};
			toolStripInsertChevron = CreateToolStripInsertGlyph();
			// The native template node supplies the pixels; this border supplies input only.
			toolStripInsertChevron.Child = null;
			toolStripInsertChevron.Background = Brushes.Transparent;
			toolStripInsertChevron.BorderThickness = new Thickness(0);
			typeHereLabel = new TextBlock {
				Text = TypeHereText, FontSize = 11, Foreground = Brushes.DimGray,
				VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0)
			};
			typeHereEditor = new TextBox {
				FontSize = 11, BorderThickness = new Thickness(0), MinWidth = 60,
				Padding = new Thickness(2, 0, 2, 0), Visibility = Visibility.Collapsed
			};
			typeHereCell = new Border {
				Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
				BorderBrush = Brushes.Gray,
				BorderThickness = new Thickness(1),
				Visibility = Visibility.Collapsed,
				Cursor = Cursors.IBeam,
				ToolTip = "Type a name to add a new item; Enter keeps adding, Tab commits, Esc cancels.",
				Child = new Grid { Children = { typeHereLabel, typeHereEditor } }
			};
			// A click anywhere on the cell starts editing, mirroring CenterLabelClick.
			typeHereCell.MouseLeftButtonDown += (sender, args) => {
				args.Handled = true;
				BeginTypeHereEdit();
			};
			// handledEventsToo: true - see the popup-level editor's own AddHandler for why.
			typeHereEditor.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnTypeHereEditorKeyDown), true);
			typeHereEditor.LostKeyboardFocus += (sender, args) => CommitTypeHere(TypeHereCommit.Cancel);
			renameEditor = new TextBox {
				FontSize = 11, BorderThickness = new Thickness(1), BorderBrush = SelectionBrush,
				Background = Brushes.White, Padding = new Thickness(2, 0, 2, 0), Visibility = Visibility.Collapsed
			};
			// handledEventsToo: true - see PopupTypeHereEditor's own note on why a plain += is not
			// enough once an IME is active (it marks IME-routed keydowns Handled before a plain
			// instance handler ever sees them).
			renameEditor.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRenameEditorKeyDown), true);
			renameEditor.LostKeyboardFocus += (sender, args) => CancelRename();
			adorners.Children.Add(smartTagChevron);
			adorners.Children.Add(toolStripInsertChevron);
			adorners.Children.Add(typeHereCell);
			adorners.Children.Add(renameEditor);

			disconnectedText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
			var restartButton = new Button { Content = "Restart designer", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 5, 12, 5) };
			restartButton.Click += (sender, args) => RestartRequested?.Invoke(this, EventArgs.Empty);
			disconnectedOverlay = new Border {
				Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
				BorderBrush = Brushes.IndianRed,
				BorderThickness = new Thickness(1),
				Padding = new Thickness(20),
				Visibility = Visibility.Collapsed,
				Child = new StackPanel { Children = { disconnectedText, restartButton } }
			};

			trayRegion = new Border {
				BorderThickness = new Thickness(0, 1, 0, 0),
				// Height follows the rows (Auto); past TrayMaxVisibleRows the tray scrolls.
				MaxHeight = TrayMaxVisibleRows * TrayCellHeight + 2 * TrayPadding + 1,
				Visibility = Visibility.Collapsed,
				// The tray's own scrollbar: item layout is fixed-size, so a form with many
				// components scrolls the tray without touching the design surface's own scroll
				// position or zoom.
				Child = trayScroller
			};
			trayScroller.Content = trayItems;
			trayScroller.SizeChanged += (_, _) => LayoutTrayCells();
			// Theme brushes, not fixed light colors: under the dark theme the entries' text follows
			// the IDE foreground, which was unreadable on a hard-coded light gray.
			trayRegion.SetResourceReference(Border.BackgroundProperty, "ToolWindowBackground");
			trayRegion.SetResourceReference(Border.BorderBrushProperty, "Border");
			trayRegion.SetResourceReference(TextElement.ForegroundProperty, "Foreground");
			// The tray's own background menu. Registered on trayRegion rather than on trayItems so
			// the whole strip responds, including the empty space below a short row of entries, and
			// it fires only when no entry handled the press first.
			trayRegion.MouseRightButtonDown += (_, args) => {
				args.Handled = true;
				TrayContextMenuRequested?.Invoke(this, new RemoteComponentEventArgs(String.Empty));
			};
			// The component tray is deliberately a SIBLING of the zoomable canvas rather than part
			// of its content, mirroring how the real designer hosts ComponentTray through
			// ISplitWindowService.AddSplitWindow: it keeps its own scrollbar and fixed item size.
			canvasScroller = (ScrollViewer)ContentHost.Content;
			ContentHost.Content = null;
			var canvasArea = new Grid { Children = { canvasScroller, disconnectedOverlay } };
			var contentLayout = new Grid();
			contentLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			contentLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			Grid.SetRow(canvasArea, 0);
			Grid.SetRow(trayRegion, 1);
			contentLayout.Children.Add(canvasArea);
			contentLayout.Children.Add(trayRegion);
			ContentHost.Content = contentLayout;

			reorderThumb.DragStarted += (_, _) => { reorderDragDeltaX = 0; ShowReorderInsertionLine(vertical: false, 0); };
			reorderThumb.DragDelta += (_, e) => { reorderDragDeltaX += e.HorizontalChange; ShowReorderInsertionLine(vertical: false, reorderDragDeltaX); };
			reorderThumb.DragCompleted += (sender, e) => { insertionLine.Visibility = Visibility.Collapsed; OnReorderDragCompleted(sender, e); };
			popupReorderThumb.PreviewMouseLeftButtonDown += (_, _) => popupReorderPointerDown = true;
			popupReorderThumb.PreviewMouseLeftButtonUp += (_, _) => {
				if (!popupReorderPointerDown || Math.Abs(popupReorderDeltaY) >= SystemParameters.MinimumVerticalDragDistance)
					return;
				popupReorderPointerDown = false;
				// A selected popup item has a drag Thumb above the bitmap. Thumb does not
				// reliably raise DragCompleted for a zero-distance SendInput click, so route
				// that plain click to the same inline edit path as the bitmap below it.
				Dispatcher.BeginInvoke(new Action(BeginRename), System.Windows.Threading.DispatcherPriority.Input);
			};
			popupReorderThumb.DragStarted += (_, _) => { popupReorderDeltaY = 0; ShowReorderInsertionLine(vertical: true, 0); };
			popupReorderThumb.DragDelta += (_, e) => { popupReorderDeltaY += e.VerticalChange; ShowReorderInsertionLine(vertical: true, popupReorderDeltaY); };
			popupReorderThumb.DragCompleted += (sender, e) => { insertionLine.Visibility = Visibility.Collapsed; OnPopupReorderDragCompleted(sender, e); };

			controller.SelectionChanged += (_, names) => OnCanvasSelectionChanged(names);
			controller.ElementPicked += (_, name) => {
				// A drag started on an element that is not selected yet selects it first.
				if (!selectedComponentNames.Contains(name))
					SelectSingleComponent(name, takeFocus: false);
			};
			controller.ElementDragCommitted += (_, drag) => CommitCanvasDrag(drag);
			controller.ElementGroupDragCommitted += (_, moves) => CommitCanvasGroupDrag(moves);
			controller.ElementDoubleClicked += (_, info) => {
				if (info != null && !String.IsNullOrEmpty(info.Name))
					DefaultEventRequested?.Invoke(this, new RemoteComponentEventArgs(info.Name));
			};
			ViewportChanged += (_, _) => {
				PositionPopupOverlays();
				if (selectedComponent != null)
					PositionAdorners();
			};

			AllowDrop = true;
			DragOver += OnDragOver;
			Drop += OnDrop;
			PreviewKeyDown += OnKeyDown;
			PreviewMouseRightButtonDown += OnCanvasRightButtonDown;
		}

		readonly DesignSurfaceController controller;

		/// <summary>Why the last canvas click selected what it did (DevFlow).</summary>
		internal string LastPickDiagnostic => controller.LastPickDiagnostic;
		readonly ScrollViewer canvasScroller;
		/// <summary>Each synthesized tree node's path, by component name - the canvas's hit test
		/// answers with a path (see <see cref="CanvasSnapshot"/>).</summary>
		Dictionary<string, string> pathByName = new(StringComparer.Ordinal);

		/// <summary>Design point to <see cref="DesignSurface.ExtensionLayer"/> coordinates.</summary>
		(double X, double Y) ToContent(double x, double y)
		{
			var point = DesignToContentPoint(x, y);
			return (point.X, point.Y);
		}

		/// <summary>
		/// The session state as the canvas wants it: the host's flat component list (whose
		/// SurfaceX/SurfaceY are already in the rendered frame's space) turned into an element tree
		/// with absolute bounds, keyed by component name. The host's own Tree carries parent-relative
		/// Location values, which the canvas cannot place. Tray-only components (no parent) have no
		/// place on the surface and are left out; the frame is passed on only when it is new.
		/// </summary>
		DesignerSessionState CanvasSnapshot(DesignerSessionState session)
		{
			var components = session.Components ?? new List<DesignerComponentInfo>();
			var root = components.FirstOrDefault(item => item.Name == session.Tree?.Name)
				?? components.FirstOrDefault(item => String.IsNullOrEmpty(item.Parent) && item.IsControl && !item.IsTrayComponent);
			var children = components.Where(item => !String.IsNullOrEmpty(item.Parent))
				.GroupBy(item => item.Parent, StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
			var paths = new Dictionary<string, string>(StringComparer.Ordinal);
			DesignerElementNode Build(DesignerComponentInfo component, string path, int depth)
			{
				paths[component.Name] = path;
				var node = new DesignerElementNode {
					Id = component.Name,
					Name = component.Name,
					Type = component.Type,
					X = component.SurfaceX,
					Y = component.SurfaceY,
					Width = component.Width,
					Height = component.Height,
					Path = path,
					IsDesignable = true,
					IsVisible = component.IsVisible
				};
				// TabIndex is all the tab-order badges read; the root has no badge.
				if (depth > 0 && component.Properties.FirstOrDefault(item => item.Name == "TabIndex") is { } tabIndex)
					node.Properties.Add(new DesignerPropertyInfo { Name = "TabIndex", Value = tabIndex.Value });
				if (depth < 64 && children.TryGetValue(component.Name, out var list)) {
					for (var index = 0; index < list.Count; index++)
						node.Children.Add(Build(list[index], path.Length == 0 ? index.ToString(System.Globalization.CultureInfo.InvariantCulture)
							: path + "," + index.ToString(System.Globalization.CultureInfo.InvariantCulture), depth + 1));
				}
				return node;
			}
			var tree = root == null ? null : Build(root, "", 0);
			pathByName = paths;
			DesignerRenderFrame frame = null;
			if (session.Render != null && (!String.IsNullOrEmpty(session.Render.Data) || !String.IsNullOrEmpty(session.Render.PngBase64))
				&& (session.Render.Sequence <= 0 || session.Render.Sequence > lastFrameSequence)) {
				frame = session.Render;
				lastFrameSequence = session.Render.Sequence;
			}
			return new DesignerSessionState {
				SessionId = session.SessionId,
				DocumentId = session.DocumentId,
				Version = session.Version,
				Accepted = true,
				Render = frame,
				Tree = tree
			};
		}

		/// <summary>Answers the canvas's hit test from the design host (<c>design/hit-test</c>).
		/// Off the dispatcher thread, so the RPC's continuation cannot need the thread it blocks.</summary>
		DesignCanvasHit IDesignCanvasBackend.HitTest(double x, double y)
		{
			if (state == null)
				return null;
			var result = Task.Run(() => client.HitTestAsync(version, (int)x, (int)y, CancellationToken.None)).GetAwaiter().GetResult();
			var name = result.ComponentName;
			if (String.IsNullOrEmpty(name))
				return new DesignCanvasHit(false, null, Array.Empty<string>());
			return pathByName.TryGetValue(name, out var path)
				? new DesignCanvasHit(true, path, new[] { name })
				: new DesignCanvasHit(false, null, new[] { name });
		}

		/// <summary>The canvas changed the selection (click, Ctrl-click, marquee): mirror it into
		/// this designer's own selection, which the host, the Properties pad and the tray follow.</summary>
		void OnCanvasSelectionChanged(IReadOnlyList<string> names)
		{
			selectedComponentNames.Clear();
			foreach (var name in names)
				selectedComponentNames.Add(name);
			SelectedComponentName = names.FirstOrDefault() ?? "";
			selectedComponent = state?.Components?.FirstOrDefault(item => item.Name == SelectedComponentName);
			UpdateAdorners();
			Focus();
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			if (selectedComponent != null)
				_ = EnsureAncestorTabActiveAsync(selectedComponent);
		}

		/// <summary>Pushes this designer's selection to the canvas outline (primary first) without
		/// the canvas announcing it back. Tray-only components have no outline.</summary>
		void UpdateDesignGuides() => controller.RestoreSelection(SelectedComponentNames);

		/// <summary>A committed canvas move or resize. A move goes through the designer's own
		/// selection move (<see cref="SelectionMoveRequested"/>, which also carries its snapping to
		/// the grid and its undo unit); a resize through <see cref="BoundsChanged"/>, in the
		/// component's parent-relative coordinates. A locked component (bar the root's size) and a
		/// ToolStripItem, which is not a Control, cannot be dragged: the outline snaps back.</summary>
		void CommitCanvasDrag(ElementDragInfo drag)
		{
			var component = state?.Components?.FirstOrDefault(item => item.Name == drag.Name);
			var isRoot = component != null && String.IsNullOrEmpty(component.Parent);
			if (component == null || !component.IsControl || (!isRoot && lockedComponentNames.Contains(component.Name))) {
				UpdateDesignGuides();
				return;
			}
			var resized = Math.Abs(drag.EndWidth - drag.StartWidth) >= 0.5 || Math.Abs(drag.EndHeight - drag.StartHeight) >= 0.5;
			if (!resized) {
				var dx = (int)Math.Round(drag.EndX - drag.StartX);
				var dy = (int)Math.Round(drag.EndY - drag.StartY);
				if (dx != 0 || dy != 0)
					SelectionMoveRequested?.Invoke(this, new RemoteSelectionMoveEventArgs(dx, dy));
				else
					UpdateDesignGuides();
				return;
			}
			BoundsChanged?.Invoke(this, new RemoteBoundsChangedEventArgs(component.Name,
				component.X + (int)Math.Round(drag.EndX - component.SurfaceX),
				component.Y + (int)Math.Round(drag.EndY - component.SurfaceY),
				(int)Math.Round(drag.EndWidth), (int)Math.Round(drag.EndHeight)));
		}

		/// <summary>A multi-selection moved together: one selection move, unless any member is
		/// locked or not a Control.</summary>
		void CommitCanvasGroupDrag(IReadOnlyList<(string Name, double DX, double DY)> moves)
		{
			var blocked = moves.Any(move => lockedComponentNames.Contains(move.Name)
				|| state?.Components?.FirstOrDefault(item => item.Name == move.Name)?.IsControl != true);
			var first = moves.FirstOrDefault();
			var dx = (int)Math.Round(first.DX);
			var dy = (int)Math.Round(first.DY);
			if (blocked || (dx == 0 && dy == 0)) {
				UpdateDesignGuides();
				return;
			}
			SelectionMoveRequested?.Invoke(this, new RemoteSelectionMoveEventArgs(dx, dy));
		}

		/// <summary>The canvas's press hook: a tab header switches its TabControl's page.</summary>
		bool TryInterceptTabHeaderPress(Point designPoint)
		{
			if (state?.Components == null || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
				return false;
			var onHeader = state.Components.Any(component => component.TabHeaderBounds.Any(bounds =>
				new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height).Contains(designPoint)));
			if (!onHeader)
				return false;
			_ = SwitchTabAt(designPoint);
			return true;
		}

		async Task SwitchTabAt(Point designPoint)
		{
			try {
				await TrySwitchTabAsync(designPoint);
			} catch (Exception exception) {
				ICSharpCode.Core.LoggingService.Warn("RemoteFormsDesignerControl.SwitchTabAt: " + exception.Message);
			}
		}

		static bool IsWithin(object source, DependencyObject ancestor)
		{
			for (var node = source as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node)) {
				if (ReferenceEquals(node, ancestor))
					return true;
			}
			return false;
		}

		void OnAdornerPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
		{
			if (toolStripHost == null || toolStripInsertChevron.Visibility != Visibility.Visible)
				return;
			var bounds = toolStripInsertChevron.TransformToAncestor(adorners).TransformBounds(
				new Rect(0, 0, toolStripInsertChevron.ActualWidth, toolStripInsertChevron.ActualHeight));
			if (!bounds.Contains(args.GetPosition(adorners)))
				return;
			args.Handled = true;
			ToolStripInsertRequested?.Invoke(this, new RemoteToolStripInsertRequestedEventArgs(
				toolStripHost.Name, toolStripHost.Type, toolStripInsertChevron));
		}

		public string SelectedComponentName { get; private set; } = "";

		/// <summary>Sets the view: "fit", or an absolute zoom ("1" = 100%). Returns the toolbar's
		/// resulting zoom label.</summary>
		public string SetZoom(string value)
		{
			if (String.Equals(value, "fit", StringComparison.OrdinalIgnoreCase))
				FitView();
			else if (Double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var zoom))
				SetViewport(zoom, 0, 0);
			UpdateLayout();
			return ZoomCombo.SelectedItem as string;
		}

		public string[] SelectedComponentNames => String.IsNullOrEmpty(SelectedComponentName)
			? selectedComponentNames.ToArray()
			: new[] { SelectedComponentName }.Concat(selectedComponentNames.Where(name => name != SelectedComponentName)).ToArray();
		public bool IsLocked(string componentName) => lockedComponentNames.Contains(componentName);
		public void RenameSelection(string oldName, string newName)
		{
			if (selectedComponentNames.Remove(oldName)) selectedComponentNames.Add(newName);
			if (lockedComponentNames.Remove(oldName)) lockedComponentNames.Add(newName);
			if (SelectedComponentName == oldName) SelectedComponentName = newName;
		}
		public DesignerSessionState State => state;
		public event EventHandler SelectionChanged;
		public event EventHandler<RemoteToolboxDropEventArgs> ToolboxDrop;
		public event EventHandler<RemoteBoundsChangedEventArgs> BoundsChanged;
		public event EventHandler<RemoteSelectionMoveEventArgs> SelectionMoveRequested;
		public event EventHandler<RemoteReorderRequestedEventArgs> ReorderRequested;
		public event EventHandler<RemoteRenameRequestedEventArgs> RenameRequested;
		public event EventHandler<RemoteRenameRequestedEventArgs> ItemTextCommitted;
		public event EventHandler<RemoteComponentEventArgs> DeleteRequested;
		public event EventHandler<RemoteComponentEventArgs> DefaultEventRequested;
		/// <summary>A right-click landed on the design surface. <see cref="RemoteComponentEventArgs"/>
		/// carries the component it resolved to (already selected by then, matching real VS, where
		/// right-clicking both selects and opens the menu) - or an empty name for empty canvas, so
		/// the handler can still offer surface-level commands.</summary>
		public event EventHandler<RemoteComponentEventArgs> ContextMenuRequested;
		/// <summary>A right-click landed on the COMPONENT TRAY rather than the design surface, which
		/// gets its own pair of declared menus. The component name is empty for a click on the
		/// tray's own background - the tray is a real target even with nothing under the cursor,
		/// since that is where Paste-a-component and the tray's own layout commands belong.</summary>
		public event EventHandler<RemoteComponentEventArgs> TrayContextMenuRequested;
		public event EventHandler RestartRequested;
		/// <summary>The smart-tag chevron at the selection's top-right corner was clicked
		/// (VS calls this the "smart tag" - the popup listing a component's
		/// DesignerActionList items). The host owns the RPC round-trip and the popup itself
		/// (matching how <see cref="DeleteRequested"/>/<see cref="BoundsChanged"/> keep the host
		/// as the sole owner of remote mutation and undo/redo).</summary>
		public event EventHandler<RemoteSmartTagRequestedEventArgs> SmartTagRequested;
		/// <summary>The ToolStrip/StatusStrip/MenuStrip "insert new item" chevron was clicked.</summary>
		public event EventHandler<RemoteToolStripInsertRequestedEventArgs> ToolStripInsertRequested;
		public event EventHandler<RemoteToolStripTypeHereEventArgs> ToolStripTypeHereCommitted;
		/// <summary>Raises <see cref="ToolStripTypeHereCommitted"/> on behalf of
		/// PopupTypeHereEditor: an event can only be raised from within its declaring type, even
		/// when public, so that same-file/same-assembly sibling class needs this forwarder.</summary>
		internal void RaiseToolStripTypeHereCommitted(RemoteToolStripTypeHereEventArgs e) => ToolStripTypeHereCommitted?.Invoke(this, e);

		/// <summary>A small clickable glyph, drawn as a plain Border rather than a Button - see
		/// CreateTransparentThumbTemplate on why a real Button/Thumb's default theme chrome cannot
		/// be trusted to stay transparent under this app's dark theme.</summary>
		static Border CreateChevronGlyph(string glyph, Brush foreground) => new Border {
			Width = 9, Height = 9,
			Background = Brushes.White,
			BorderBrush = foreground,
			BorderThickness = new Thickness(1),
			Cursor = Cursors.Hand,
			Visibility = Visibility.Collapsed,
			Child = new TextBlock {
				Text = glyph, FontSize = 7, Foreground = foreground,
				HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0, -2, 0, 0)
			}
		};

		/// <summary>The smart-tag chevron, using the real VS "SmartTag" glyph (VS2017 Image
		/// Library - the same source CLAUDE.md documents for this repo's VS chrome icons)
		/// rather than a hand-drawn text glyph. Sized 16x16 (real VS's own DesignerActionGlyph
		/// paints its chevron procedurally via GDI+, not from an embedded bitmap resource - checked
		/// System.Windows.Forms.Design.dll's manifest resources directly, no such resource exists
		/// there - so this Image Library icon is the closest available "real" asset, just larger
		/// and more legible than the previous 9x9 hand-drawn one).</summary>
		static Border CreateSmartTagGlyph()
		{
			FrameworkElement icon;
			try {
				using var stream = typeof(RemoteFormsDesignerControl).Assembly.GetManifestResourceStream("SmartTagGlyph.xaml")
					?? throw new InvalidOperationException("SmartTagGlyph.xaml resource not found.");
				icon = (FrameworkElement)XamlReader.Load(stream);
			} catch {
				// Fall back to the old hand-drawn glyph rather than leave the chevron entirely
				// missing if the embedded resource is ever unavailable.
				return CreateChevronGlyph("»", Brushes.Goldenrod);
			}
			return new Border {
				Width = 16, Height = 16,
				Cursor = Cursors.Hand,
				Visibility = Visibility.Collapsed,
				Child = icon
			};
		}

		/// <summary>Mimics the real WinForms designer's "insert new item" affordance
		/// (LibreWinForms/dotnet-winforms <c>ToolStripTemplateNode.SetUpToolTemplateNode</c>):
		/// there it is a real <c>ToolStripSplitButton</c> sited at the end of the strip, sized to
		/// the strip's own item row (22px tall for a ToolStrip/StatusStrip, 19px for a
		/// MenuStrip/ContextMenuStrip - <c>TOOLSTRIP_TEMPLATE_HEIGHT_ORIGINAL</c>/
		/// <c>TEMPLATE_HEIGHT_ORIGINAL</c>), with <c>DisplayStyle=Image</c> plus its own built-in
		/// split-button dropdown arrow cell (<c>DropDownButtonWidth</c>) - i.e. a small icon+▾
		/// button drawn ON the strip's row, not a lone triangle floating past its bounds. This
		/// draws the same two-cell shape (icon cell + narrow arrow cell) directly rather than
		/// reflecting into the internal ToolStripSplitButton renderer.</summary>
		/// <summary>How a "Type Here" edit ended, which decides what happens next. Ported from
		/// ToolStripTemplateNode.Commit's own enterKeyPressed/tabKeyPressed pair.</summary>
		enum TypeHereCommit
		{
			/// <summary>Esc, or focus lost: discard the text, add nothing.</summary>
			Cancel,
			/// <summary>Enter: add the item and re-arm the cell so the next item can be typed
			/// straight away.</summary>
			EnterKey,
			/// <summary>Tab: add the item and leave edit mode.</summary>
			TabKey
		}

		void BeginTypeHereEdit()
		{
			if (typeHereEditing || toolStripHost == null)
				return;
			typeHereEditing = true;
			typeHereLabel.Visibility = Visibility.Collapsed;
			typeHereEditor.Text = "";
			typeHereEditor.Visibility = Visibility.Visible;
			typeHereEditor.Focus();
			typeHereEditor.SelectAll();
		}

		void OnTypeHereEditorKeyDown(object sender, KeyEventArgs e)
		{
			// An active IME reports every keystroke - including Enter/Tab/Escape - as
			// Key.ImeProcessed, with the real key only available via ImeProcessedKey.
			switch (e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key) {
				case Key.Enter:
					e.Handled = true;
					CommitTypeHere(TypeHereCommit.EnterKey);
					break;
				case Key.Tab:
					e.Handled = true;
					CommitTypeHere(TypeHereCommit.TabKey);
					break;
				case Key.Escape:
					e.Handled = true;
					CommitTypeHere(TypeHereCommit.Cancel);
					break;
				default:
					// Everything else belongs to the editor, NOT to the canvas. Marking the key
					// handled keeps the designer's own arrow/Delete handling (and the IDE's
					// shortcuts) out of the way while typing - the job
					// ISupportInSituService.IgnoreMessages does for the in-process designer.
					e.Handled = true;
					break;
			}
		}

		/// <summary>Ends an in-place edit. Mirrors ToolStripTemplateNode.CommitTextToDesigner:
		/// empty text adds nothing; a lone "-" in a dropdown becomes a separator; otherwise the
		/// strip's default new-item type (the first entry of its reported list, which is
		/// ToolStripDesignerUtils.GetStandardItemTypes' own order) is created with the typed
		/// text.</summary>
		void CommitTypeHere(TypeHereCommit commit)
		{
			if (!typeHereEditing)
				return;
			var text = typeHereEditor.Text?.Trim() ?? "";
			var host = toolStripHost;
			typeHereEditing = false;
			typeHereEditor.Visibility = Visibility.Collapsed;
			typeHereEditor.Text = "";
			typeHereLabel.Visibility = Visibility.Visible;
			if (commit == TypeHereCommit.Cancel || text.Length == 0 || host == null)
				return;
			var typeName = text == "-" && host.NewItemTypeNames.Contains("System.Windows.Forms.ToolStripSeparator")
				? "System.Windows.Forms.ToolStripSeparator"
				: host.NewItemTypeNames.FirstOrDefault();
			if (String.IsNullOrEmpty(typeName))
				return;
			ToolStripTypeHereCommitted?.Invoke(this,
				new RemoteToolStripTypeHereEventArgs(host.Name, typeName, text));
			// Enter keeps the cell armed so a run of items can be typed without re-clicking, the
			// same way the real template node stays in edit mode on Enter.
			if (commit == TypeHereCommit.EnterKey)
				Dispatcher.BeginInvoke(new Action(BeginTypeHereEdit), System.Windows.Threading.DispatcherPriority.Background);
		}

		/// <summary>F2: begins renaming the current selection in place, prefilled with its current
		/// name and fully selected (matching real VS's own F2 behavior of selecting the whole
		/// name, ready to be typed over).</summary>
		void BeginRename()
		{
			if (renaming || selectedComponent == null) return;
			renaming = true;
			renameComponentName = selectedComponent.Name;
			renameEditor.Text = selectedComponent.IsControl ? selectedComponent.Name : selectedComponent.Text;
			renameEditor.Visibility = Visibility.Visible;
			PositionAdorners();
			renameEditor.Focus();
			renameEditor.SelectAll();
		}

		void CancelRename()
		{
			if (!renaming) return;
			renaming = false;
			renameEditor.Visibility = Visibility.Collapsed;
			renameEditor.Text = "";
		}

		void CommitRename()
		{
			if (!renaming || selectedComponent == null) { CancelRename(); return; }
			var newName = renameEditor.Text?.Trim() ?? "";
			var oldName = selectedComponent.Name;
			renaming = false;
			renameEditor.Visibility = Visibility.Collapsed;
			if (!selectedComponent.IsControl) {
				if (newName != selectedComponent.Text)
					ItemTextCommitted?.Invoke(this, new RemoteRenameRequestedEventArgs(oldName, newName));
				return;
			}
			if (newName.Length == 0 || newName == oldName) return;
			RenameRequested?.Invoke(this, new RemoteRenameRequestedEventArgs(oldName, newName));
		}

		void OnRenameEditorKeyDown(object sender, KeyEventArgs e)
		{
			// An active IME reports every keystroke - including Enter/Escape - as
			// Key.ImeProcessed, with the real key only available via ImeProcessedKey (see
			// PopupTypeHereEditor's own note on this).
			switch (e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key) {
				case Key.Enter:
				case Key.Tab:
					e.Handled = true;
					CommitRename();
					break;
				case Key.Escape:
					e.Handled = true;
					CancelRename();
					break;
				default:
					// Everything else belongs to the editor, not the canvas - same reasoning as
					// OnTypeHereEditorKeyDown's own default case.
					e.Handled = true;
					break;
			}
		}

		static Border CreateToolStripInsertGlyph()
		{
			var icon = new Border {
				Width = 14, Height = 18,
				Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)),
				Child = new System.Windows.Shapes.Rectangle {
					Width = 8, Height = 8, Fill = Brushes.SeaGreen,
					HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
				}
			};
			var arrow = new Border {
				Width = 9, Height = 18,
				Background = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)),
				Child = new TextBlock {
					Text = "▾", FontSize = 8, Foreground = Brushes.Black,
					HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
					Margin = new Thickness(0, -3, 0, 0)
				}
			};
			var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, arrow } };
			return new Border {
				// 23x19: matches TOOLSTRIP_TEMPLATE_WIDTH/HEIGHT_ORIGINAL's proportions closely
				// enough to read as "a strip item", not a decoration past the strip's edge.
				Width = 23, Height = 19,
				BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
				Cursor = Cursors.Hand,
				Visibility = Visibility.Collapsed,
				Child = row,
				ToolTip = "Insert new item"
			};
		}

		protected override AutomationPeer OnCreateAutomationPeer() => new RemoteDesignerAutomationPeer(this);

		public void Show(DesignerSessionState state)
		{
			disconnectedOverlay.Visibility = Visibility.Collapsed;
			this.state = state;
			version = state.Version;
			// The tray's contents come from the component list, not from the rendered bitmap, so a
			// state that carries no new frame (or no frame at all) still has to refresh it.
			UpdateComponentTray();
			var snapshot = CanvasSnapshot(state);
			// Selectable on the surface: every placed component and the root form. Items inside an
			// open dropdown are selected through their popup overlay instead (OnPopupClicked).
			controller.SetSelectableNames((state.Components ?? new List<DesignerComponentInfo>())
				.Where(item => pathByName.ContainsKey(item.Name) && !item.IsDropDownItem)
				.Select(item => item.Name));
			controller.ApplySnapshot(snapshot);
			if (snapshot.Render is { } frame) {
				var dpiForStatus = Math.Max(1, frame.Dpi);
				StatusText = $"Rendered by {BackendName} design host ({frame.Width / dpiForStatus:0}×{frame.Height / dpiForStatus:0}).";
			}
			UpdatePopupOverlays(state);
			selectedComponentNames.RemoveWhere(name => state.Components?.Any(item => item.Name == name) != true);
			lockedComponentNames.RemoveWhere(name => state.Components?.Any(item => item.Name == name) != true);
			if (!selectedComponentNames.Contains(SelectedComponentName))
				SelectedComponentName = selectedComponentNames.FirstOrDefault() ?? "";
			selectedComponent = String.IsNullOrEmpty(SelectedComponentName) ? null
				: state.Components?.FirstOrDefault(item => item.Name == SelectedComponentName);
			UpdateDesignGuides();
			UpdateAdorners();
			AutomationProperties.SetName(this, selectedComponent?.AccessibleName ?? "WinForms designer");
			AutomationProperties.SetHelpText(this, selectedComponent?.AccessibleDescription ?? "");
		}


		/// <summary>Reconciles the live <see cref="popupOverlays"/> against
		/// <c>state.Popups</c>: keeps the same Image (and therefore any in-progress interaction)
		/// for a popup that is still open, decodes and swaps in new PNG bytes when its frame
		/// changed, adds a new Image for a popup that just opened, and removes one that closed.
		/// Geometry is handled separately by <see cref="PositionPopupOverlays"/> since zoom/pan
		/// changes need to reposition every popup without a new frame.</summary>
		void UpdatePopupOverlays(DesignerSessionState state)
		{
			var seen = new HashSet<string>(StringComparer.Ordinal);
			Image opened = null;
			foreach (var popup in state.Popups ?? []) {
				seen.Add(popup.OwnerElementId);
				if (!popupOverlays.TryGetValue(popup.OwnerElementId, out var image)) {
					image = new Image {
						Stretch = Stretch.Fill,
						HorizontalAlignment = HorizontalAlignment.Left,
						VerticalAlignment = VerticalAlignment.Top,
						Cursor = Cursors.Arrow
					};
					var ownerId = popup.OwnerElementId;
					image.MouseLeftButtonDown += (sender, args) => {
						args.Handled = true;
						OnPopupClicked(ownerId, image, args);
					};
					popupOverlays[popup.OwnerElementId] = image;
					adorners.Children.Add(image);
					Panel.SetZIndex(image, 200);
					opened = image;   // state.Popups runs outermost first, so this ends on the deepest
				}
				if (!String.IsNullOrEmpty(popup.Render?.PngBase64)) {
					using var stream = new MemoryStream(Convert.FromBase64String(popup.Render.PngBase64));
					var png = new BitmapImage();
					png.BeginInit();
					png.CacheOption = BitmapCacheOption.OnLoad;
					png.StreamSource = stream;
					png.EndInit();
					png.Freeze();
					image.Source = png;
				}
				image.Tag = popup;
				if (popup.TypeHereBounds is { } bounds) {
					if (!popupEditors.TryGetValue(popup.OwnerElementId, out var editor)) {
						editor = new PopupTypeHereEditor(this, popup.OwnerElementId);
						popupEditors[popup.OwnerElementId] = editor;
						adorners.Children.Add(editor.Cell);
						Panel.SetZIndex(editor.Cell, 201);
					}
					editor.Bounds = bounds;
				} else if (popupEditors.TryGetValue(popup.OwnerElementId, out var goneEditor)) {
					// This dropdown lost its template node (rare, but real WinForms can decline to
					// create one) - drop the editor along with it.
					goneEditor.Cancel();
					adorners.Children.Remove(goneEditor.Cell);
					popupEditors.Remove(popup.OwnerElementId);
				}
			}
			foreach (var staleId in popupOverlays.Keys.Where(id => !seen.Contains(id)).ToArray()) {
				adorners.Children.Remove(popupOverlays[staleId]);
				popupOverlays.Remove(staleId);
			}
			foreach (var staleId in popupEditors.Keys.Where(id => !seen.Contains(id)).ToArray()) {
				popupEditors[staleId].Cancel();
				adorners.Children.Remove(popupEditors[staleId].Cell);
				popupEditors.Remove(staleId);
			}
			PositionPopupOverlays();
			// A dropdown opens at its real place on the form, which in a short designer pane (the
			// split view, or a tall component tray) can be below the visible canvas - its items and
			// Type Here cell were then unreachable, and a click there landed on the tray. Scroll the
			// canvas to the dropdown that just opened, once layout has placed it.
			if (opened != null) {
				var target = opened;
				Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
					if (adorners.Children.Contains(target) && target.ActualWidth > 0)
						target.BringIntoView();
				}));
			}
		}

		/// <summary>Places every live popup overlay at its reported surface position, sized by the
		/// current zoom, in the canvas's extension layer - so a popup stays visually attached to the
		/// strip it belongs to at any zoom level.</summary>
		void PositionPopupOverlays()
		{
			foreach (var image in popupOverlays.Values) {
				if (image.Tag is not DesignerPopupFrame popup || popup.Render == null)
					continue;
				var (left, top) = ToContent(popup.X, popup.Y);
				Canvas.SetLeft(image, left);
				Canvas.SetTop(image, top);
				var dpi = Math.Max(1, popup.Render.Dpi);
				image.Width = popup.Render.Width / dpi * ViewportScale;
				image.Height = popup.Render.Height / dpi * ViewportScale;
				if (popupEditors.TryGetValue(popup.OwnerElementId, out var editor)) {
					var (cellLeft, cellTop) = ToContent(popup.X + editor.Bounds.X, popup.Y + editor.Bounds.Y);
					editor.Reposition(cellLeft, cellTop, ViewportScale);
				}
			}
		}

		/// <summary>A click on a popup overlay: hit-test that popup's OWN surface directly (never
		/// the root form's coordinate space) and select whatever item is under the pointer.</summary>
		async void OnPopupClicked(string ownerElementId, Image image, MouseButtonEventArgs args)
		{
			try {
				Focus();
				var point = args.GetPosition(image);
				var designPoint = new Point(point.X / ViewportScale, point.Y / ViewportScale);
				var result = await client.HitTestPopupAsync(version, ownerElementId, designPoint.X, designPoint.Y, CancellationToken.None);
				if (!result.Accepted)
					return;
				// The child's real ISelectionService already moved (HitTestPopupAndSelect), but
				// this control's own selection (SelectedComponentName et al.) is tracked entirely
				// client-side and has no other way to learn what got hit inside the popup.
				if (!String.IsNullOrEmpty(result.PopupHitElementId)) {
					selectedComponentNames.Clear();
					selectedComponentNames.Add(result.PopupHitElementId);
					SelectedComponentName = result.PopupHitElementId;
				}
				Show(result);
				if (!String.IsNullOrEmpty(result.PopupHitElementId))
					SelectionChanged?.Invoke(this, EventArgs.Empty);
				if (!String.IsNullOrEmpty(result.PopupHitElementId) && selectedComponent?.IsControl == false) {
					var clickedName = result.PopupHitElementId;
					// Finish selection mirroring and the current pointer event before taking
					// keyboard focus. A popup bitmap cannot host the native designer's editor.
					Dispatcher.BeginInvoke(new Action(() => {
						if (SelectedComponentName == clickedName) BeginRename();
					}), System.Windows.Threading.DispatcherPriority.Background);
				}
			} catch (Exception exception) {
				ICSharpCode.Core.LoggingService.Warn(
					"RemoteFormsDesignerControl.OnPopupClicked(" + ownerElementId + "): " + exception.Message);
			}
		}

		/// <summary>Rebuilds the component tray from the reported components. Each entry is the
		/// component's real WinForms icon plus its name, sized independently of the canvas zoom
		/// (the tray is not inside the zoomed surface), and selecting one routes through the same
		/// single-selection path as the Document Outline so the Properties pad and the outline
		/// follow along.  A ToolStripItem belongs to its owning strip/dropdown, not to the tray:
		/// only strip-level controls and root non-visual components are listed here.</summary>
		void UpdateComponentTray()
		{
			var trayComponents = state?.Components?.Where(item => item.IsTrayComponent
				&& (item.IsControl || String.IsNullOrEmpty(item.Parent))).ToArray()
				?? Array.Empty<DesignerComponentInfo>();
			trayItems.Children.Clear();
			trayRegion.Visibility = trayComponents.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
			foreach (var component in trayComponents) {
				// Icon column + a name that trims to the cell rather than widening it.
				var content = new Grid();
				content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				var icon = TrayIconSource(component.Type);
				if (icon != null) {
					content.Children.Add(new Image {
						Source = icon, Width = 16, Height = 16,
						Margin = new Thickness(0, 0, 4, 0),
						VerticalAlignment = VerticalAlignment.Center
					});
				}
				var label = new TextBlock {
					Text = component.Name, VerticalAlignment = VerticalAlignment.Center,
					TextTrimming = TextTrimming.CharacterEllipsis
				};
				Grid.SetColumn(label, 1);
				content.Children.Add(label);
				var entry = new Border {
					Padding = new Thickness(4, 2, 6, 2),
					Margin = new Thickness(1),
					CornerRadius = new CornerRadius(2),
					BorderThickness = new Thickness(1),
					Cursor = Cursors.Hand,
					ToolTip = component.Name + " (" + component.Type + ")",
					Tag = component.Name,
					Child = content
				};
				var componentName = component.Name;
				entry.MouseLeftButtonDown += (_, args) => {
					args.Handled = true;
					SelectSingleComponent(componentName, takeFocus: false);
				};
				// Right-click selects first, then asks for the menu - the same order as the design
				// surface, and what real VS does. Handled stops it reaching the tray background's
				// own handler, which would otherwise offer the empty-tray menu on top of an entry.
				entry.MouseRightButtonDown += (_, args) => {
					args.Handled = true;
					SelectSingleComponent(componentName, takeFocus: false);
					TrayContextMenuRequested?.Invoke(this, new RemoteComponentEventArgs(componentName));
				};
				trayItems.Children.Add(entry);
			}
			RefreshTrayHighlight();
			LayoutTrayCells();
		}

		/// <summary>Splits the tray's width evenly into as many columns as fit at
		/// <see cref="TrayMinCellWidth"/>, so every row is a row of equal cells that together fill
		/// the width. Re-run whenever the tray is resized.</summary>
		void LayoutTrayCells()
		{
			var available = trayScroller.ViewportWidth > 0 ? trayScroller.ViewportWidth : trayScroller.ActualWidth;
			available -= 2 * TrayPadding;
			if (available <= 0)
				return;
			var columns = Math.Max(1, (int)Math.Floor(available / TrayMinCellWidth));
			var width = Math.Floor(available / columns);
			if (!width.Equals(trayItems.ItemWidth))
				trayItems.ItemWidth = width;
		}

		/// <summary>Repaints just the tray entries' selected state. Split out of
		/// <see cref="UpdateComponentTray"/> because UpdateAdorners runs on every drag sample -
		/// rebuilding the entries (and re-decoding their icons) that often would be wasteful.</summary>
		void RefreshTrayHighlight()
		{
			foreach (var child in trayItems.Children) {
				if (child is not Border entry || entry.Tag is not string name)
					continue;
				var selected = selectedComponentNames.Contains(name);
				if (selected) {
					entry.SetResourceReference(Border.BackgroundProperty, "Selection");
					entry.SetResourceReference(TextElement.ForegroundProperty, "SelectionForeground");
				} else {
					entry.Background = Brushes.Transparent;
					entry.ClearValue(TextElement.ForegroundProperty);
				}
				entry.BorderBrush = selected ? SelectionBrush : Brushes.Transparent;
			}
		}

		/// <summary>The tray entry's icon: the same real per-type WinForms toolbox icon the Toolbox
		/// pad uses, read out of the installed Microsoft WinForms assembly (this process's own
		/// System.Windows.Forms is the portable fork, which embeds no icon resources).</summary>
		static ImageSource TrayIconSource(string typeName)
		{
			try {
				var bitmap = ICSharpCode.SharpDevelop.Gui.WinFormsToolboxIconProvider.GetIcon(typeName);
				if (bitmap == null) return null;
				using var stream = new MemoryStream();
				bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
				stream.Position = 0;
				var image = new BitmapImage();
				image.BeginInit();
				image.CacheOption = BitmapCacheOption.OnLoad;
				image.StreamSource = stream;
				image.EndInit();
				image.Freeze();
				return image;
			} catch (Exception exception) {
				ICSharpCode.Core.LoggingService.Warn(
					"RemoteFormsDesignerControl.TrayIconSource(" + typeName + "): " + exception.Message);
				return null;
			}
		}


		public void SetTabOrderMode(bool value) => controller.SetTabOrderMode(value);

		public void SelectAllComponents()
		{
			selectedComponentNames.Clear();
			foreach (var component in state.Components.Where(item => !String.IsNullOrEmpty(item.Parent)))
				selectedComponentNames.Add(component.Name);
			SelectedComponentName = selectedComponentNames.FirstOrDefault() ?? "";
			selectedComponent = state.Components.FirstOrDefault(item => item.Name == SelectedComponentName);
			UpdateDesignGuides();
			UpdateAdorners();
			SelectionChanged?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>
		/// Sets the whole selection to the named components (first is primary), keeping the rest
		/// of the selection machinery and the <see cref="SelectionChanged"/> event in sync -
		/// mirrors <see cref="SelectAllComponents"/> but from an explicit name list, so DevFlow
		/// actions can drive multi-select align/distribute the same way a rubber-band drag would.
		/// Unknown names are skipped; the first known name becomes the primary selection.
		/// </summary>
		public void SelectComponents(params string[] names)
		{
			var known = names == null
				? Array.Empty<string>()
				: names.Where(name => state?.Components?.Any(item => item.Name == name) == true).ToArray();
			selectedComponentNames.Clear();
			foreach (var name in known)
				selectedComponentNames.Add(name);
			SelectedComponentName = known.FirstOrDefault() ?? "";
			selectedComponent = String.IsNullOrEmpty(SelectedComponentName) ? null
				: state.Components.FirstOrDefault(item => item.Name == SelectedComponentName);
			UpdateDesignGuides();
			UpdateAdorners();
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			if (selectedComponent != null) _ = EnsureAncestorTabActiveAsync(selectedComponent);
		}

		/// <summary>Real VS's Document Outline switches the active tab automatically when you
		/// select a node nested inside a TabPage that isn't currently showing - selecting it
		/// without doing so would draw the selection adorner over whatever page IS visible, not
		/// over the actual (hidden) component. Best-effort and fire-and-forget: local selection
		/// state is already committed synchronously by the caller, this only corrects which page
		/// is showing afterward. A no-op when <paramref name="component"/> has no TabPage ancestor,
		/// or that TabPage is already the active one (design/select-tab setting the same
		/// SelectedIndex again is itself a harmless no-op server-side).</summary>
		async System.Threading.Tasks.Task EnsureAncestorTabActiveAsync(DesignerComponentInfo component)
		{
			var current = component;
			DesignerComponentInfo tabPage = null;
			while (current != null && !String.IsNullOrEmpty(current.Parent)) {
				var parent = state?.Components?.FirstOrDefault(item => item.Name == current.Parent);
				if (parent?.Type == "System.Windows.Forms.TabPage") { tabPage = parent; break; }
				current = parent;
			}
			if (tabPage == null || String.IsNullOrEmpty(tabPage.Parent)) return;
			var tabControlName = tabPage.Parent;
			// The flat Components list's order is container-registration order (declaration order
			// in InitializeComponent), NOT necessarily TabPages/layout order - the hierarchical
			// Tree's Children order IS guaranteed to match (see BuildElementTree, which walks
			// control.Controls directly), so the tab index must come from there, not from indexing
			// into Components.
			var tabControlNode = FindTreeNode(state?.Tree, tabControlName);
			var index = tabControlNode?.Children?.FindIndex(node => node.Name == tabPage.Name) ?? -1;
			if (index < 0) return;
			var result = await client.SelectTabAsync(version, tabControlName, index, CancellationToken.None);
			if (result.Accepted) Show(result);
		}

		static DesignerElementNode FindTreeNode(DesignerElementNode node, string name)
		{
			if (node == null) return null;
			if (node.Name == name) return node;
			foreach (var child in node.Children ?? Enumerable.Empty<DesignerElementNode>()) {
				var found = FindTreeNode(child, name);
				if (found != null) return found;
			}
			return null;
		}

		/// <summary>Selects a single component by name (no-op when unknown), keeping the rest
		/// of the selection machinery and the <see cref="SelectionChanged"/> event in sync.
		/// Used by the Document Outline pad. Deliberately does NOT move keyboard focus onto this
		/// canvas: doing so used to steal focus away from the Outline pad's own TreeView on every
		/// selection commit, breaking the Outline's own arrow-key navigation (the round trip is
		/// Outline click/arrow -&gt; SelectionCommitted -&gt; here -&gt; SelectionChanged -&gt;
		/// DesignerViewContent.RemoteSelectionChanged -&gt; outline.SelectNodeById, so a Focus()
		/// here always fires on the very next keystroke the user makes in the Outline).</summary>
		public void SelectComponent(string componentName)
		{
			if (componentName != null && state?.Components?.Any(item => item.Name == componentName) == true)
				SelectSingleComponent(componentName, takeFocus: false);
		}

		public void ToggleSelectedLocked()
		{
			var shouldLock = selectedComponentNames.Any(name => !lockedComponentNames.Contains(name));
			foreach (var name in selectedComponentNames) {
				if (shouldLock) lockedComponentNames.Add(name); else lockedComponentNames.Remove(name);
			}
			UpdateAdorners();
		}

		public void ShowDisconnected(string message)
		{
			disconnectedText.Text = message;
			disconnectedOverlay.Visibility = Visibility.Visible;
			controller.ClearSelection();
			StatusText = message;
		}

		public bool TryGetComponentScreenBounds(string componentName, out Rect bounds)
		{
			bounds = Rect.Empty;
			var component = state?.Components?.FirstOrDefault(item => item.Name == componentName);
			if (component == null || !HasRender)
				return false;
			bounds = SurfaceRectToScreen(component.SurfaceX, component.SurfaceY, component.Width, component.Height);
			return true;
		}

		/// <summary>Screen rect of one tab HEADER of a TabControl, for the same reason
		/// <see cref="TryGetComponentScreenBounds"/> exists: a synthetic click has to be aimed at
		/// real geometry. A header is not a component, so its rect cannot be obtained by name -
		/// and it must NOT be guessed from the TabControl's own rect either, because a TabControl's
		/// surface is almost entirely covered by its pages and its non-page chrome is only a few
		/// pixels wide. Guessing "just inside the border" lands inside the selected TabPage
		/// instead, which is exactly the kind of silently-wrong target this exists to prevent.
		/// Returns false when the component is not a TabControl or the index has no header.</summary>
		public bool TryGetTabHeaderScreenBounds(string tabControlName, int tabIndex, out Rect bounds)
		{
			bounds = Rect.Empty;
			var component = state?.Components?.FirstOrDefault(item => item.Name == tabControlName);
			if (component == null || !HasRender)
				return false;
			if (tabIndex < 0 || tabIndex >= component.TabHeaderBounds.Count)
				return false;
			var header = component.TabHeaderBounds[tabIndex];
			bounds = SurfaceRectToScreen(header.X, header.Y, header.Width, header.Height);
			return true;
		}

		/// <summary>A design rect in screen coordinates, through the canvas's own viewport.</summary>
		Rect SurfaceRectToScreen(double surfaceX, double surfaceY, double width, double height)
		{
			var topLeft = SurfacePointToScreen(surfaceX, surfaceY);
			var bottomRight = SurfacePointToScreen(surfaceX + width, surfaceY + height);
			return new Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
		}






		/// <summary>If designPoint lands inside one of a TabControl's own reported
		/// TabHeaderBounds (see DesignerComponentInfo's own doc comment - a header is not a
		/// component, so this is the only way to hit-test one), switches that TabControl's real
		/// SelectedIndex (design/select-tab, deliberately NOT design/set-property - see that RPC's
		/// own doc comment on why this must not persist or become an undo step) and selects the
		/// TabControl itself - matching real VS, where clicking a tab header both switches the
		/// active page AND selects the TabControl (not the page, and not whatever used to be
		/// selected). Returns false (a no-op) when the click did not land on any header, so the
		/// caller falls through to its own generic hit-test.</summary>
		async System.Threading.Tasks.Task<bool> TrySwitchTabAsync(Point designPoint)
		{
			var hit = state.Components.SelectMany(component => component.TabHeaderBounds
				.Select((bounds, index) => (component, bounds, index)))
				.FirstOrDefault(entry => new Rect(entry.bounds.X, entry.bounds.Y, entry.bounds.Width, entry.bounds.Height).Contains(designPoint));
			if (hit.component == null)
				return false;
			var result = await client.SelectTabAsync(version, hit.component.Name, hit.index, CancellationToken.None);
			if (!result.Accepted)
				return false;
			selectedComponentNames.Clear();
			selectedComponentNames.Add(hit.component.Name);
			SelectedComponentName = hit.component.Name;
			Show(result);
			Focus();
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			return true;
		}




		/// <summary>Right-click: select whatever is under the pointer, then ask the host to show a
		/// context menu for it. Real VS does both from one press, and selecting first is what makes
		/// the menu's contents well-defined - designer verbs (Add Tab/Remove Tab) are a property of
		/// the SELECTED component. Routed through the host's hit test: only the child process knows
		/// which component owns a pixel. Presses on the tray (its own menus) and on the extension
		/// layer's editors are not the surface's.</summary>
		void OnCanvasRightButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (state == null || !HasRender || IsWithin(e.OriginalSource, trayRegion) || !IsWithin(e.OriginalSource, canvasScroller)
				|| IsWithin(e.OriginalSource, renameEditor) || IsWithin(e.OriginalSource, typeHereCell))
				return;
			try {
				var designPoint = ToDesignPoint(e.GetPosition(this));
				var hit = Task.Run(() => client.HitTestAsync(version, (int)designPoint.X, (int)designPoint.Y, CancellationToken.None)).GetAwaiter().GetResult();
				if (!String.IsNullOrEmpty(hit.ComponentName) && hit.ComponentName != SelectedComponentName)
					SelectSingleComponent(hit.ComponentName, takeFocus: false);
				Focus();
				e.Handled = true;
				ContextMenuRequested?.Invoke(this, new RemoteComponentEventArgs(hit.ComponentName ?? ""));
			} catch (Exception exception) {
				// A faulted hit-test RPC must not silently swallow the gesture with no trail.
				ICSharpCode.Core.LoggingService.Warn("RemoteFormsDesignerControl.OnCanvasRightButtonDown: " + exception.Message);
			}
		}

		void OnKeyDown(object sender, KeyEventArgs e)
		{
			// Keys typed into an editor on the canvas (rename, Type Here) are the editor's.
			if (e.OriginalSource is TextBoxBase)
				return;
			if (e.Key == Key.Escape && selectedComponent != null && !String.IsNullOrEmpty(selectedComponent.Parent)) {
				SelectSingleComponent(selectedComponent.Parent);
				e.Handled = true;
				return;
			}
			// Ctrl+. (real VS's own "Edit.ShowSmartTag" shortcut) opens the smart-tag/verb popup
			// for the current selection without needing to hit the 9x9 chevron glyph - useful for
			// any component whose selection bounds put the chevron somewhere awkward to click, and
			// the only way to reach it at all via keyboard.
			if (e.Key == Key.OemPeriod && Keyboard.Modifiers == ModifierKeys.Control && selectedComponent != null) {
				SmartTagRequested?.Invoke(this, new RemoteSmartTagRequestedEventArgs(selectedComponent.Name, smartTagChevron));
				e.Handled = true;
				return;
			}
			if (e.Key == Key.Tab && state?.Components?.Count > 0) {
				var selectable = state.Components.Where(item => !String.IsNullOrEmpty(item.Parent))
					.OrderBy(item => ParseTabIndex(item)).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
				if (selectable.Length > 0) {
					var current = Array.FindIndex(selectable, item => item.Name == SelectedComponentName);
					var direction = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
					var next = (current + direction + selectable.Length) % selectable.Length;
					SelectSingleComponent(selectable[next].Name);
					e.Handled = true;
					return;
				}
			}
			if (e.Key == Key.Delete && selectedComponent != null && !String.IsNullOrEmpty(selectedComponent.Parent)
				&& !lockedComponentNames.Contains(selectedComponent.Name)) {
				DeleteRequested?.Invoke(this, new RemoteComponentEventArgs(selectedComponent.Name));
				e.Handled = true;
				return;
			}
			if (e.Key == Key.F2 && selectedComponent != null && !String.IsNullOrEmpty(selectedComponent.Parent)
				&& !lockedComponentNames.Contains(selectedComponent.Name)) {
				BeginRename();
				e.Handled = true;
				return;
			}
			if (selectedComponent == null || String.IsNullOrEmpty(selectedComponent.Parent) || lockedComponentNames.Contains(selectedComponent.Name)) return;
			var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
			var dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
			var dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
			if (dx == 0 && dy == 0) return;
			SelectionMoveRequested?.Invoke(this, new RemoteSelectionMoveEventArgs(dx, dy));
			e.Handled = true;
		}

		static int ParseTabIndex(DesignerComponentInfo component)
		{
			var value = component.Properties.FirstOrDefault(item => item.Name == "TabIndex")?.Value;
			return Int32.TryParse(value, out var result) ? result : Int32.MaxValue;
		}

		void SelectSingleComponent(string componentName, bool takeFocus = true)
		{
			var component = state?.Components?.FirstOrDefault(item => item.Name == componentName);
			if (component == null) return;
			selectedComponentNames.Clear();
			selectedComponentNames.Add(component.Name);
			SelectedComponentName = component.Name;
			selectedComponent = component;
			UpdateDesignGuides();
			UpdateAdorners();
			if (takeFocus) Focus();
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			_ = EnsureAncestorTabActiveAsync(component);
		}

		sealed class RemoteDesignerAutomationPeer : FrameworkElementAutomationPeer, ISelectionProvider
		{
			readonly RemoteFormsDesignerControl owner;

			public RemoteDesignerAutomationPeer(RemoteFormsDesignerControl owner) : base(owner) => this.owner = owner;

			protected override string GetClassNameCore() => nameof(RemoteFormsDesignerControl);
			protected override string GetNameCore() => "WinForms designer";
			protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
			protected override List<AutomationPeer> GetChildrenCore() => owner.state?.Components?
				.Where(item => !String.IsNullOrEmpty(item.Name) && String.IsNullOrEmpty(item.Parent))
				.Select(item => (AutomationPeer)new RemoteComponentAutomationPeer(owner, this, item)).ToList()
				?? new List<AutomationPeer>();
			public override object GetPattern(PatternInterface patternInterface)
				=> patternInterface == PatternInterface.Selection ? this : base.GetPattern(patternInterface);

			public bool CanSelectMultiple => true;
			public bool IsSelectionRequired => false;
			public IRawElementProviderSimple[] GetSelection() => owner.state.Components
				.Where(item => owner.selectedComponentNames.Contains(item.Name))
				.Select(item => new RemoteComponentAutomationPeer(owner, this, item))
				.Select(ProviderFromPeer).ToArray();
		}

		sealed class RemoteComponentAutomationPeer : AutomationPeer, ISelectionItemProvider
		{
			readonly RemoteFormsDesignerControl owner;
			readonly RemoteDesignerAutomationPeer container;
			readonly DesignerComponentInfo component;

			public RemoteComponentAutomationPeer(RemoteFormsDesignerControl owner,
				RemoteDesignerAutomationPeer container, DesignerComponentInfo component)
			{
				this.owner = owner;
				this.container = container;
				this.component = component;
			}

			protected override string GetNameCore() => String.IsNullOrEmpty(component.AccessibleName)
				? component.Name : component.AccessibleName;
			protected override string GetHelpTextCore() => component.AccessibleDescription ?? "";
			protected override string GetClassNameCore() => component.Type;
			protected override string GetAutomationIdCore() => component.Name;
			protected override string GetAcceleratorKeyCore() => "";
			protected override string GetAccessKeyCore() => "";
			protected override string GetItemStatusCore() => IsSelected ? "Selected" : "";
			protected override string GetItemTypeCore() => component.AccessibleRole ?? "";
			protected override AutomationControlType GetAutomationControlTypeCore() => ControlType(component.Type);
			protected override Rect GetBoundingRectangleCore()
				=> owner.TryGetComponentScreenBounds(component.Name, out var bounds) ? bounds : Rect.Empty;
			protected override Point GetClickablePointCore()
			{
				var bounds = GetBoundingRectangleCore();
				return bounds.IsEmpty ? new Point(Double.NaN, Double.NaN)
					: new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
			}
			protected override List<AutomationPeer> GetChildrenCore() => owner.state.Components
				.Where(item => item.Parent == component.Name)
				.Select(item => (AutomationPeer)new RemoteComponentAutomationPeer(owner, container, item)).ToList();
			protected override AutomationPeer GetLabeledByCore() => null;
			protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
			protected override bool IsControlElementCore() => true;
			protected override bool IsContentElementCore() => true;
			protected override bool IsEnabledCore() => true;
			protected override bool HasKeyboardFocusCore() => owner.IsKeyboardFocusWithin && IsSelected;
			protected override bool IsKeyboardFocusableCore() => true;
			protected override bool IsOffscreenCore() => GetBoundingRectangleCore().IsEmpty;
			protected override bool IsPasswordCore() => false;
			protected override bool IsRequiredForFormCore() => false;
			protected override void SetFocusCore() => owner.SelectSingleComponent(component.Name);
			public override object GetPattern(PatternInterface patternInterface)
				=> patternInterface == PatternInterface.SelectionItem ? this : null;

			public bool IsSelected => owner.selectedComponentNames.Contains(component.Name);
			public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(container);
			public void AddToSelection()
			{
				owner.selectedComponentNames.Add(component.Name);
				owner.SelectedComponentName = component.Name;
				owner.selectedComponent = component;
				owner.UpdateDesignGuides();
				owner.UpdateAdorners();
				owner.SelectionChanged?.Invoke(owner, EventArgs.Empty);
			}
			public void RemoveFromSelection()
			{
				owner.selectedComponentNames.Remove(component.Name);
				if (owner.SelectedComponentName == component.Name) {
					owner.SelectedComponentName = owner.selectedComponentNames.FirstOrDefault() ?? "";
					owner.selectedComponent = owner.state.Components.FirstOrDefault(item => item.Name == owner.SelectedComponentName);
				}
				owner.UpdateDesignGuides();
				owner.UpdateAdorners();
				owner.SelectionChanged?.Invoke(owner, EventArgs.Empty);
			}
			public void Select() => owner.SelectSingleComponent(component.Name);

			static AutomationControlType ControlType(string type) => type switch {
				"System.Windows.Forms.Button" => AutomationControlType.Button,
				"System.Windows.Forms.CheckBox" => AutomationControlType.CheckBox,
				"System.Windows.Forms.RadioButton" => AutomationControlType.RadioButton,
				"System.Windows.Forms.TextBox" => AutomationControlType.Edit,
				"System.Windows.Forms.ComboBox" => AutomationControlType.ComboBox,
				"System.Windows.Forms.ListBox" => AutomationControlType.List,
				"System.Windows.Forms.TreeView" => AutomationControlType.Tree,
				"System.Windows.Forms.DataGridView" => AutomationControlType.DataGrid,
				"System.Windows.Forms.Form" => AutomationControlType.Window,
				_ => AutomationControlType.Custom
			};
		}














		/// <summary>Keeps the native ToolStrip template node usable when the strip falls outside
		/// the canvas viewport.  Use rendered WPF coordinates rather than design coordinates: the
		/// latter omit zoom, pan, and the ScrollViewer's current transform.</summary>
		void EnsureToolStripInsertionNodeVisible()
		{
			if (!toolStripInsertChevron.IsVisible || canvasScroller.ViewportWidth <= 0 || canvasScroller.ViewportHeight <= 0)
				return;
			var topLeft = toolStripInsertChevron.TranslatePoint(new Point(0, 0), canvasScroller);
			var bottomRight = toolStripInsertChevron.TranslatePoint(
				new Point(toolStripInsertChevron.ActualWidth, toolStripInsertChevron.ActualHeight), canvasScroller);
			const double margin = 12;
			var horizontalOffset = canvasScroller.HorizontalOffset;
			var verticalOffset = canvasScroller.VerticalOffset;
			if (topLeft.X < margin)
				horizontalOffset = Math.Max(0, horizontalOffset + topLeft.X - margin);
			else if (bottomRight.X > canvasScroller.ViewportWidth - margin)
				horizontalOffset += bottomRight.X - (canvasScroller.ViewportWidth - margin);
			if (topLeft.Y < margin)
				verticalOffset = Math.Max(0, verticalOffset + topLeft.Y - margin);
			else if (bottomRight.Y > canvasScroller.ViewportHeight - margin)
				verticalOffset += bottomRight.Y - (canvasScroller.ViewportHeight - margin);
			if (horizontalOffset != canvasScroller.HorizontalOffset || verticalOffset != canvasScroller.VerticalOffset) {
				canvasScroller.ScrollToHorizontalOffset(horizontalOffset);
				canvasScroller.ScrollToVerticalOffset(verticalOffset);
				// PointToScreen must observe the new viewport transform before an automation
				// client receives the insertion-node coordinates below.
				canvasScroller.UpdateLayout();
			}
		}


		/// <summary>Whether the current selection is an item inside a currently-open popup (a
		/// MenuStrip submenu/ContextMenuStrip's own items), rather than laid out directly on a
		/// root strip - i.e. its Parent names one of state.Popups' own OwnerElementId. Decides
		/// which of reorderThumb (horizontal, root strips)/popupReorderThumb (vertical, popup
		/// items) applies to the current selection; both drive the SAME design/reorder-toolstrip-
		/// item RPC, since the server resolves the real owning collection from the item's own
		/// live Owner/OwnerItem regardless of which gesture asked for the move.</summary>
		bool SelectionIsInsideOpenPopup() => selectedComponent != null
			&& (state?.Popups?.Any(popup => popup.OwnerElementId == selectedComponent.Parent) ?? false);

		/// <summary>Shared by both reorder gestures (and their live insertion-line feedback): the
		/// dragged item's siblings (same Parent), ordered along the relevant axis, the target
		/// index (how many siblings now sit before the dragged item's current center), and the
		/// design-space coordinate along that axis where an insertion line should be drawn to mark
		/// that boundary - the midpoint between the two neighboring siblings' edges, or the single
		/// neighbor's own outer edge at either end of the list.</summary>
		(int TargetIndex, double LinePosition) ComputeReorderTarget(bool vertical, double delta)
		{
			if (selectedComponent == null) return (0, 0);
			double Edge(DesignerComponentInfo item, bool trailing) => vertical
				? item.SurfaceY + (trailing ? item.Height : 0)
				: item.SurfaceX + (trailing ? item.Width : 0);
			var siblings = (state?.Components ?? new List<DesignerComponentInfo>())
				.Where(item => item.Parent == selectedComponent.Parent && item.Name != selectedComponent.Name)
				.OrderBy(item => Edge(item, false)).ToList();
			var draggedCenter = (vertical ? selectedComponent.SurfaceY + selectedComponent.Height / 2.0
				: selectedComponent.SurfaceX + selectedComponent.Width / 2.0) + delta;
			var targetIndex = siblings.Count(item => Edge(item, false) + (vertical ? item.Height : item.Width) / 2.0 < draggedCenter);
			var linePosition = siblings.Count == 0 ? (vertical ? selectedComponent.SurfaceY : selectedComponent.SurfaceX)
				: targetIndex == 0 ? Edge(siblings[0], false)
				: targetIndex >= siblings.Count ? Edge(siblings[^1], true)
				: (Edge(siblings[targetIndex - 1], true) + Edge(siblings[targetIndex], false)) / 2.0;
			return (targetIndex, linePosition);
		}

		/// <summary>Live drag feedback for both reorder gestures: shows insertionLine at the
		/// CURRENT drop boundary (see ComputeReorderTarget) while the drag is in progress, rotated
		/// to a vertical "|" for a horizontal (root-strip) drag or a horizontal "-" for a vertical
		/// (popup-item) one, spanning the dragged item's own cross-axis extent and positioned at
		/// its own cross-axis origin (matching real VS's own insertion-line cue).</summary>
		void ShowReorderInsertionLine(bool vertical, double delta)
		{
			if (selectedComponent == null) return;
			var (_, linePosition) = ComputeReorderTarget(vertical, delta);
			// 4, not 2: a 2 design-unit line survives a screenshot's own downscaling/compression
			// poorly (confirmed with a temporary diagnostic dump of every computed coordinate -
			// all were sane and well within the visible canvas, so the earlier "invisible in a
			// screenshot" result was a thinness/compression artifact, not a positioning bug).
			const double thickness = 4;
			double left, top, lineWidth, lineHeight;
			if (vertical) {
				left = selectedComponent.SurfaceX; top = linePosition - thickness / 2;
				lineWidth = selectedComponent.Width; lineHeight = thickness;
			} else {
				left = linePosition - thickness / 2; top = selectedComponent.SurfaceY;
				lineWidth = thickness; lineHeight = selectedComponent.Height;
			}
			var (surfaceLeft, surfaceTop) = ToContent(left, top);
			Canvas.SetLeft(insertionLine, surfaceLeft);
			Canvas.SetTop(insertionLine, surfaceTop);
			insertionLine.Width = Math.Max(1, lineWidth * ViewportScale);
			insertionLine.Height = Math.Max(1, lineHeight * ViewportScale);
			Panel.SetZIndex(insertionLine, 203);
			insertionLine.Visibility = Visibility.Visible;
		}

		/// <summary>Drops a dragged ToolStripItem among its siblings - see ComputeReorderTarget.
		/// Horizontal-only: covers ToolStrip/StatusStrip/MenuStrip's own top-level items, which VS
		/// itself only ever lays out in a row; a popup's own vertically-stacked items use the
		/// analogous <see cref="OnPopupReorderDragCompleted"/> instead.</summary>
		void OnReorderDragCompleted(object sender, DragCompletedEventArgs e)
		{
			var delta = reorderDragDeltaX;
			reorderDragDeltaX = 0;
			if (selectedComponent == null || e.Canceled || String.IsNullOrEmpty(selectedComponent.Parent) || Math.Abs(delta) < 1)
				return;
			var (targetIndex, _) = ComputeReorderTarget(vertical: false, delta);
			ReorderRequested?.Invoke(this, new RemoteReorderRequestedEventArgs(selectedComponent.Name, targetIndex));
		}

		/// <summary>The vertical analogue of <see cref="OnReorderDragCompleted"/> for an item
		/// inside an open popup: a popup item's own SurfaceX/Y/Width/Height are ALREADY reported
		/// in the same absolute surface basis a popup's own DesignerPopupFrame.X/Y use (verified
		/// against DesignerHostService.CurrentState's generic "component is ToolStripItem ...
		/// SurfaceLocation(surfaceItem.Owner)" computation, which works for a dropdown Owner
		/// exactly like it does for a root strip), so no separate per-popup-item protocol field is
		/// needed here - only the axis (Y instead of X) differs from the root case.</summary>
		void OnPopupReorderDragCompleted(object sender, DragCompletedEventArgs e)
		{
			var delta = popupReorderDeltaY;
			popupReorderDeltaY = 0;
			popupReorderPointerDown = false;
			if (selectedComponent == null || e.Canceled || String.IsNullOrEmpty(selectedComponent.Parent))
				return;
			if (Math.Abs(delta) < SystemParameters.MinimumVerticalDragDistance) {
				// The selected item's Thumb sits above the popup image. A plain click
				// therefore comes here, not through OnPopupClicked.
				Dispatcher.BeginInvoke(new Action(BeginRename), System.Windows.Threading.DispatcherPriority.Input);
				return;
			}
			var (targetIndex, _) = ComputeReorderTarget(vertical: true, delta);
			ReorderRequested?.Invoke(this, new RemoteReorderRequestedEventArgs(selectedComponent.Name, targetIndex));
		}

		void UpdateAdorners()
		{
			// Selection just changed (a fresh call to Show/a new click) - any in-progress F2 edit
			// belongs to the PREVIOUS selection and must not be left dangling over the new one.
			if (renaming && renameComponentName != selectedComponent?.Name) CancelRename();
			insertionLine.Visibility = Visibility.Collapsed;
			AutomationProperties.SetName(this, String.IsNullOrEmpty(selectedComponent?.AccessibleName)
				? selectedComponent?.Name ?? "WinForms designer" : selectedComponent.AccessibleName);
			AutomationProperties.SetHelpText(this, selectedComponent?.AccessibleDescription ?? "");
			// A component that has a tray entry but NO place on the surface (Timer, ImageList,
			// ToolTip, ContextMenuStrip, the dialogs) has no outline (the canvas has no node for it)
			// and no surface glyphs; the tray shows its selection instead. Being a tray component
			// is NOT enough: every MenuStrip/ToolStrip/StatusStrip gets a tray entry too while
			// still being laid out on the surface. A missing Parent is what marks "tray only".
			if (selectedComponent?.IsTrayComponent == true && String.IsNullOrEmpty(selectedComponent.Parent)) {
				reorderThumb.Visibility = popupReorderThumb.Visibility =
					smartTagChevron.Visibility = toolStripInsertChevron.Visibility = Visibility.Collapsed;
				toolStripHost = null;
				RefreshTrayHighlight();
				return;
			}
			RefreshTrayHighlight();
			var visible = selectedComponent != null;
			var isRoot = visible && String.IsNullOrEmpty(selectedComponent.Parent);
			// A selected ToolStripItem with a Parent (i.e. not tray-only) can be dragged to reorder
			// among its siblings - design/reorder-toolstrip-item, index-based rather than
			// pixel-based. Which of the two thumbs applies depends on whether the item is stacked
			// vertically inside an open popup or laid out horizontally on a root strip.
			var reorderable = visible && !selectedComponent.IsControl && !isRoot;
			var inPopup = reorderable && SelectionIsInsideOpenPopup();
			reorderThumb.Visibility = reorderable && !inPopup ? Visibility.Visible : Visibility.Collapsed;
			popupReorderThumb.Visibility = inPopup ? Visibility.Visible : Visibility.Collapsed;
			// The smart tag applies to (almost) any selected component - VS shows it even when a
			// component's own action list turns out empty.
			smartTagChevron.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
			// VS keeps the "insert new item" glyph visible next to the strip's last item even
			// while a child ToolStripItem (not the strip itself) is selected - resolve the owning
			// strip so selecting e.g. a StatusStrip's ProgressBar still shows the glyph.
			toolStripHost = !visible ? null
				: IsToolStripHost(selectedComponent.Type) ? selectedComponent
				: state?.Components?.FirstOrDefault(item => item.Name == selectedComponent.Parent) is { } parent
					&& IsToolStripHost(parent.Type) ? parent : null;
			// The native template node renders into the bitmap, but bitmap pixels cannot
			// receive input. Place a transparent input surface at its reported bounds.
			toolStripInsertChevron.Visibility = toolStripHost?.ItemInsertionBounds != null
				&& toolStripHost.ItemInsertionStyle == DesignerItemInsertionStyles.SplitButton
				? Visibility.Visible : Visibility.Collapsed;
			// The MenuStrip/ContextMenuStrip/dropdown-item flavour of the same affordance.
			typeHereCell.Visibility = toolStripHost != null
				&& toolStripHost.ItemInsertionStyle == DesignerItemInsertionStyles.TypeHere
				? Visibility.Visible : Visibility.Collapsed;
			if (typeHereEditing)
				CommitTypeHere(TypeHereCommit.Cancel);
			var locked = visible && lockedComponentNames.Contains(selectedComponent.Name);
			reorderThumb.IsEnabled = popupReorderThumb.IsEnabled = !locked;
			SelectionStroke = locked ? Brushes.DarkOrange : null;
			if (!visible)
				return;
			PositionAdorners();
			// Unlike resize handles, this is an explicit insertion affordance. Selecting its
			// owning strip must make it reachable, including a StatusStrip below a short canvas.
			EnsureToolStripInsertionNodeVisible();
		}

		/// <summary>Places the extension-layer glyphs over the selection, in content coordinates
		/// (the canvas re-runs this on every zoom, fit or new frame through ViewportChanged).</summary>
		void PositionAdorners()
		{
			if (selectedComponent == null)
				return;
			var (left, top) = ToContent(selectedComponent.SurfaceX, selectedComponent.SurfaceY);
			var (right, bottom) = ToContent(selectedComponent.SurfaceX + selectedComponent.Width,
				selectedComponent.SurfaceY + selectedComponent.Height);
			Canvas.SetLeft(reorderThumb, left);
			Canvas.SetTop(reorderThumb, top);
			reorderThumb.Width = Math.Max(1, right - left);
			reorderThumb.Height = Math.Max(1, bottom - top);
			// Same rect as reorderThumb, but above the popup's own Image overlay (200) and its
			// Type Here editor (201) so it stays draggable once a popup is open.
			Canvas.SetLeft(popupReorderThumb, left);
			Canvas.SetTop(popupReorderThumb, top);
			popupReorderThumb.Width = Math.Max(1, right - left);
			popupReorderThumb.Height = Math.Max(1, bottom - top);
			Panel.SetZIndex(popupReorderThumb, 202);
			Canvas.SetLeft(renameEditor, left);
			Canvas.SetTop(renameEditor, top);
			renameEditor.Width = Math.Max(1, right - left);
			renameEditor.Height = Math.Max(1, bottom - top);
			Panel.SetZIndex(renameEditor, 302);
			// Smart tag: anchored at the selection's top-right corner, offset half outside the
			// bounds - the same corner/offset VS's own smart-tag glyph uses.
			Canvas.SetLeft(smartTagChevron, right - smartTagChevron.Width / 2);
			Canvas.SetTop(smartTagChevron, top - smartTagChevron.Height / 2);
			Panel.SetZIndex(smartTagChevron, 101);
			// ToolStrip insert chevron: past the RIGHTMOST EXISTING ITEM of the owning strip (a
			// Dock=Top strip is as wide as its parent, so its own right edge would put the glyph past
			// the form). It is drawn ON the strip, so it scales with the zoom like a real item.
			var scale = Math.Max(0.1, ViewportScale);
			toolStripInsertChevron.RenderTransformOrigin = new Point(0, 0);
			toolStripInsertChevron.RenderTransform = new ScaleTransform(scale, scale);
			var scaledHeight = toolStripInsertChevron.Height * scale;
			var insertLeft = right;
			var insertTop = top + (bottom - top - scaledHeight) / 2;
			if (toolStripHost != null) {
				var (hostLeft, hostTop) = ToContent(toolStripHost.SurfaceX, toolStripHost.SurfaceY);
				var (_, hostBottom) = ToContent(toolStripHost.SurfaceX, toolStripHost.SurfaceY + toolStripHost.Height);
				var lastItem = state?.Components?.Where(item => item.Parent == toolStripHost.Name)
					.OrderByDescending(item => item.SurfaceX + item.Width).FirstOrDefault();
				if (lastItem != null) {
					var (itemRight, itemTop) = ToContent(lastItem.SurfaceX + lastItem.Width, lastItem.SurfaceY);
					var (_, itemBottom) = ToContent(lastItem.SurfaceX, lastItem.SurfaceY + lastItem.Height);
					insertLeft = itemRight;
					insertTop = itemTop + (itemBottom - itemTop - scaledHeight) / 2;
				} else {
					// No real items yet: sit just past the strip's own left edge.
					insertLeft = hostLeft + 4;
					insertTop = hostTop + (hostBottom - hostTop - scaledHeight) / 2;
				}
			}
			Canvas.SetLeft(toolStripInsertChevron, insertLeft + 2);
			Canvas.SetTop(toolStripInsertChevron, insertTop);
			Panel.SetZIndex(toolStripInsertChevron, 101);
			if (toolStripHost?.ItemInsertionBounds is { } insertionBounds) {
				var (nodeLeft, nodeTop) = ToContent(
					toolStripHost.SurfaceX + insertionBounds.X, toolStripHost.SurfaceY + insertionBounds.Y);
				Canvas.SetLeft(toolStripInsertChevron, nodeLeft);
				Canvas.SetTop(toolStripInsertChevron, nodeTop);
				toolStripInsertChevron.Width = Math.Max(1, insertionBounds.Width);
				toolStripInsertChevron.Height = Math.Max(1, insertionBounds.Height);
			}
			// The "Type Here" cell occupies the same slot (the template node is the strip's last
			// item either way), just sized like a menu cell rather than a square button - snapped to
			// the host's own rendered template-node bounds when it reports them.
			Canvas.SetLeft(typeHereCell, insertLeft + 2);
			Canvas.SetTop(typeHereCell, insertTop);
			typeHereCell.MinHeight = toolStripInsertChevron.Height;
			Panel.SetZIndex(typeHereCell, 101);
			if (toolStripHost?.ItemInsertionBounds is { } typeHereBounds) {
				var (cellLeft, cellTop) = ToContent(
					toolStripHost.SurfaceX + typeHereBounds.X, toolStripHost.SurfaceY + typeHereBounds.Y);
				Canvas.SetLeft(typeHereCell, cellLeft);
				Canvas.SetTop(typeHereCell, cellTop);
				typeHereCell.Width = Math.Max(1, typeHereBounds.Width * ViewportScale);
				typeHereCell.Height = Math.Max(1, typeHereBounds.Height * ViewportScale);
				typeHereCell.MinHeight = 0;
			}
		}


		/// <summary>Whether <paramref name="type"/> is a ToolStrip/StatusStrip/MenuStrip itself
		/// (not one of its items) - the "insert new item" chevron is only drawn on the strip, not
		/// per-item; adding a submenu item still works through the same RPC
		/// (design/add-toolstrip-item's parentItemId), just not yet from this glyph.</summary>
		static bool IsToolStripHost(string type) => type is "System.Windows.Forms.ToolStrip"
			or "System.Windows.Forms.MenuStrip" or "System.Windows.Forms.StatusStrip";

		void OnDragOver(object sender, System.Windows.DragEventArgs e)
		{
			if (e.Data.GetDataPresent(typeof(ToolboxItem))) {
				e.Effects = System.Windows.DragDropEffects.Copy;
				e.Handled = true;
			}
		}

		async void OnDrop(object sender, System.Windows.DragEventArgs e)
		{
			if (e.Data.GetData(typeof(ToolboxItem)) is not ToolboxItem item || String.IsNullOrEmpty(item.TypeName) || state == null)
				return;
			e.Handled = true;
			try {
				// The child's hit-testing and the drop position are design-space.
				var design = ToDesignPoint(e.GetPosition(this));
				var designX = (double)design.X;
				var designY = (double)design.Y;
				var hit = await client.HitTestAsync(version, (int)designX, (int)designY, CancellationToken.None);
				var target = state.Components.FirstOrDefault(component => component.Name == hit.ComponentName);
				if (target != null && !IsContainer(target.Type))
					target = state.Components.FirstOrDefault(component => component.Name == target.Parent);
				target ??= state.Components.FirstOrDefault(component => String.IsNullOrEmpty(component.Parent) && component.IsControl && !component.IsTrayComponent);
				if (target != null)
					ToolboxDrop?.Invoke(this, new RemoteToolboxDropEventArgs(item.TypeName, target.Name,
						(int)designX - target.SurfaceX, (int)designY - target.SurfaceY));
			} catch (Exception exception) {
				ICSharpCode.Core.LoggingService.Warn("RemoteFormsDesignerControl.OnDrop: " + exception.Message);
			}
		}

		static bool IsContainer(string type) => type == "System.Windows.Forms.Form"
			|| type == "System.Windows.Forms.Panel" || type == "System.Windows.Forms.GroupBox"
			|| type == "System.Windows.Forms.TabPage" || type == "System.Windows.Forms.UserControl";
	}

	sealed class RemoteToolboxDropEventArgs : EventArgs
	{
		public RemoteToolboxDropEventArgs(string controlType, string parentName, int x, int y)
		{
			ControlType = controlType;
			ParentName = parentName;
			X = x;
			Y = y;
		}

		public string ControlType { get; }
		public string ParentName { get; }
		public int X { get; }
		public int Y { get; }
	}

	sealed class RemoteBoundsChangedEventArgs : EventArgs
	{
		public RemoteBoundsChangedEventArgs(string componentName, int x, int y, int width, int height)
		{
			ComponentName = componentName;
			X = x;
			Y = y;
			Width = width;
			Height = height;
		}
		public string ComponentName { get; }
		public int X { get; }
		public int Y { get; }
		public int Width { get; }
		public int Height { get; }
	}

	sealed class RemoteSelectionMoveEventArgs : EventArgs
	{
		public RemoteSelectionMoveEventArgs(int deltaX, int deltaY) { DeltaX = deltaX; DeltaY = deltaY; }
		public int DeltaX { get; }
		public int DeltaY { get; }
	}

	sealed class RemoteReorderRequestedEventArgs : EventArgs
	{
		public RemoteReorderRequestedEventArgs(string componentName, int targetIndex) { ComponentName = componentName; TargetIndex = targetIndex; }
		public string ComponentName { get; }
		public int TargetIndex { get; }
	}

	sealed class RemoteRenameRequestedEventArgs : EventArgs
	{
		public RemoteRenameRequestedEventArgs(string componentName, string newName) { ComponentName = componentName; NewName = newName; }
		public string ComponentName { get; }
		public string NewName { get; }
	}

	sealed class RemoteComponentEventArgs : EventArgs
	{
		public RemoteComponentEventArgs(string componentName) => ComponentName = componentName;
		public string ComponentName { get; }
	}

	/// <summary>The smart-tag chevron was clicked. <see cref="Anchor"/> is the glyph itself, for
	/// the popup's PlacementTarget.</summary>
	sealed class RemoteSmartTagRequestedEventArgs : EventArgs
	{
		public RemoteSmartTagRequestedEventArgs(string componentName, FrameworkElement anchor)
		{
			ComponentName = componentName;
			Anchor = anchor;
		}
		public string ComponentName { get; }
		public FrameworkElement Anchor { get; }
	}

	/// <summary>The ToolStrip/StatusStrip/MenuStrip "insert new item" chevron was clicked.
	/// <see cref="ComponentType"/> picks which item types the popup offers.</summary>
	sealed class RemoteToolStripInsertRequestedEventArgs : EventArgs
	{
		public RemoteToolStripInsertRequestedEventArgs(string componentName, string componentType, FrameworkElement anchor)
		{
			ComponentName = componentName;
			ComponentType = componentType;
			Anchor = anchor;
		}
		public string ComponentName { get; }
		public string ComponentType { get; }
		public FrameworkElement Anchor { get; }
	}

	/// <summary>A name typed into a MenuStrip's "Type Here" cell, already resolved to the item type
	/// to create (the strip's default, or ToolStripSeparator for a lone "-").</summary>
	sealed class RemoteToolStripTypeHereEventArgs : EventArgs
	{
		public RemoteToolStripTypeHereEventArgs(string componentName, string itemTypeName, string text, string parentItemId = "")
		{
			ComponentName = componentName;
			ItemTypeName = itemTypeName;
			Text = text;
			ParentItemId = parentItemId;
		}
		/// <summary>The real ToolStrip the new item's design/add-toolstrip-item call names as
		/// "elementId" - always a Control, never a ToolStripItem (see AddToolStripItem's own
		/// "ToolStrip not found" cast). For a strip's own top-level Type Here this is the strip
		/// itself; for a dropdown's Type Here (a popup overlay) it is the STRIP THAT OWNS the
		/// dropdown chain, not the dropdown item being edited - <see cref="ParentItemId"/> is what
		/// actually places the new item inside that item's own DropDownItems.</summary>
		public string ComponentName { get; }
		public string ItemTypeName { get; }
		public string Text { get; }
		/// <summary>"" to add directly to ComponentName's own Items (a strip's top-level Type
		/// Here), or the owning ToolStripDropDownItem's element id to add to ITS DropDownItems
		/// instead (a popup's own Type Here cell).</summary>
		public string ParentItemId { get; }
	}

	/// <summary>One popup's own "Type Here" edit cell - the WPF analogue of that dropdown level's
	/// real template node. Bundled into its own class (rather than a second copy of the
	/// strip-level typeHereCell/typeHereEditor fields) because there can be one of these per
	/// currently-open popup, at any nesting depth, appearing and disappearing as the user opens
	/// and closes submenus.</summary>
	sealed class PopupTypeHereEditor
	{
		readonly RemoteFormsDesignerControl owner;
		readonly string ownerElementId;
		readonly TextBlock label;
		readonly TextBox editor;
		bool editing;

		public PopupTypeHereEditor(RemoteFormsDesignerControl owner, string ownerElementId)
		{
			this.owner = owner;
			this.ownerElementId = ownerElementId;
			label = new TextBlock {
				Text = "Type Here", FontSize = 11, Foreground = Brushes.DimGray,
				VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 3, 0)
			};
			editor = new TextBox {
				FontSize = 11, BorderThickness = new Thickness(0), Padding = new Thickness(1, 0, 1, 0),
				Visibility = Visibility.Collapsed
			};
			Cell = new Border {
				Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
				Cursor = Cursors.IBeam,
				ToolTip = "Type a name to add a new item here; Enter keeps adding, Tab commits, Esc cancels.",
				Child = new Grid { Children = { label, editor } }
			};
			Cell.MouseLeftButtonDown += (_, args) => { args.Handled = true; Begin(); };
			// handledEventsToo: true - with an IME active, TextBox's own class handler marks
			// KeyDown Handled while routing composition, before a plain += handler would see it.
			editor.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
			editor.LostKeyboardFocus += (_, _) => Commit(commitOnEnter: false, keepEditing: false);
		}

		/// <summary>The real template node's own bounds within its popup, local to that popup - see
		/// DesignerPopupFrame.TypeHereBounds.</summary>
		public DesignerRectangle Bounds { get; set; }
		public Border Cell { get; }

		public object Status(string popupOwnerId)
		{
			if (!Cell.IsVisible) return null;
			var origin = Cell.PointToScreen(new Point(0, 0));
			var end = Cell.PointToScreen(new Point(Cell.ActualWidth, Cell.ActualHeight));
			return new {
				ownerId = popupOwnerId, x = origin.X, y = origin.Y,
				width = end.X - origin.X, height = end.Y - origin.Y,
				editing, focused = editor.IsKeyboardFocusWithin, text = editor.Text
			};
		}

		public bool Input(string text, bool cancel)
		{
			if (!editing || !editor.IsKeyboardFocusWithin) return false;
			editor.SelectAll();
			editor.SelectedText = text;
			editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), Environment.TickCount,
				cancel ? Key.Escape : Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
			return true;
		}

		/// <summary>Places the cell at a content point, sized by the current zoom.</summary>
		public void Reposition(double left, double top, double scale)
		{
			Canvas.SetLeft(Cell, left);
			Canvas.SetTop(Cell, top);
			Cell.Width = Math.Max(1, Bounds.Width * scale);
			Cell.Height = Math.Max(1, Bounds.Height * scale);
		}

		void Begin()
		{
			if (editing) return;
			editing = true;
			label.Visibility = Visibility.Collapsed;
			editor.Text = "";
			editor.Visibility = Visibility.Visible;
			editor.Focus();
		}

		public void Cancel()
		{
			if (!editing) return;
			editing = false;
			editor.Visibility = Visibility.Collapsed;
			editor.Text = "";
			label.Visibility = Visibility.Visible;
		}

		void OnKeyDown(object sender, KeyEventArgs e)
		{
			// An active IME reports every keystroke - including Enter/Tab/Escape - as
			// Key.ImeProcessed, with the real key only available via ImeProcessedKey.
			var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
			switch (key) {
				case Key.Enter: e.Handled = true; Commit(commitOnEnter: true, keepEditing: true); break;
				case Key.Tab: e.Handled = true; Commit(commitOnEnter: true, keepEditing: false); break;
				case Key.Escape: e.Handled = true; Cancel(); break;
				default: e.Handled = true; break;
			}
		}

		/// <summary>Mirrors ToolStripTemplateNode.CommitTextToDesigner: empty text cancels; a lone
		/// "-" becomes a separator when this dropdown's type list has one; otherwise the strip's
		/// default new-item type (NewItemTypeNames' own first entry). The real ToolStrip to name
		/// as design/add-toolstrip-item's "elementId" is resolved by walking Parent up from the
		/// owning item until a real Control (a ToolStripItem never is one) - that Control is the
		/// strip that owns the whole dropdown chain, while <paramref name="ownerElementId"/> stays
		/// the immediate parent whose own DropDownItems the new item is inserted into.</summary>
		void Commit(bool commitOnEnter, bool keepEditing)
		{
			if (!editing) return;
			var text = editor.Text?.Trim() ?? "";
			editing = false;
			editor.Visibility = Visibility.Collapsed;
			editor.Text = "";
			label.Visibility = Visibility.Visible;
			if (!commitOnEnter || text.Length == 0)
				return;
			var ownerInfo = owner.state?.Components?.FirstOrDefault(item => item.Name == ownerElementId);
			if (ownerInfo == null)
				return;
			// ContextMenuStrip is itself a ToolStrip but, unlike MenuStrip/StatusStrip, is
			// not a Control.  Its root popup's owner is therefore already the strip: do not
			// walk to an empty Parent and discard the commit.
			var isRootContextMenu = ownerInfo.Type == "System.Windows.Forms.ContextMenuStrip";
			var strip = ownerInfo;
			var guard = 0;
			while (strip != null && !strip.IsControl && !isRootContextMenu && guard++ < 32)
				strip = owner.state?.Components?.FirstOrDefault(item => item.Name == strip.Parent);
			if (strip == null)
				return;
			var typeName = text == "-" && ownerInfo.NewItemTypeNames.Contains("System.Windows.Forms.ToolStripSeparator")
				? "System.Windows.Forms.ToolStripSeparator"
				: ownerInfo.NewItemTypeNames.FirstOrDefault();
			if (String.IsNullOrEmpty(typeName))
				return;
			owner.RaiseToolStripTypeHereCommitted(
				new RemoteToolStripTypeHereEventArgs(strip.Name, typeName, text,
					isRootContextMenu ? "" : ownerElementId));
			if (keepEditing)
				owner.Dispatcher.BeginInvoke(new Action(Begin), System.Windows.Threading.DispatcherPriority.Background);
		}
	}
}
