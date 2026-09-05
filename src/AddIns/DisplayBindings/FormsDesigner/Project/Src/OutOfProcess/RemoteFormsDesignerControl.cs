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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Drawing.Design;

using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Widgets;

namespace ICSharpCode.FormsDesigner.OutOfProcess
{
	sealed class RemoteFormsDesignerControl : DesignerCanvas
	{
		readonly FormsDesignerHostClient client;
		readonly Grid designSurface = new Grid();
		readonly Canvas scrollContent = new Canvas();
		readonly ScrollViewer scroller = new() {
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto
		};
		// Stretch.Fill (matching WpfSurfaceDesignerControl/UnoDesignSurfaceControl exactly): the
		// bitmap must scale to fill framePresenter.Visual's Width/Height, which is all Resize()
		// actually changes on zoom - Stretch.None would keep showing the bitmap at its native
		// pixel size regardless of Width/Height, so the selection outline (computed independently
		// through viewport.Scale) would resize with zoom while the rendered form image itself
		// visibly stayed at its pre-zoom size.
		readonly DesignFramePresenter framePresenter = new(Stretch.Fill,
			horizontalAlignment: HorizontalAlignment.Left, verticalAlignment: VerticalAlignment.Top);
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
		readonly Canvas guides;
		// Drag-snap alignment guides (see SnapGuideCalculator): a vertical or horizontal line
		// shown while a component is being dragged near another component's edge/centre,
		// matching UnoDesignSurfaceControl's own guide overlay/rendering. Kept separate from
		// `guides` (which UpdateDesignGuides clears wholesale on every viewport/selection
		// change) so a live drag's guides aren't wiped by an unrelated redraw.
		readonly Canvas snapGuideOverlay = new Canvas { IsHitTestVisible = false };
		readonly List<Rectangle> snapGuides = new();
		// The component tray - the icon+name strip below the design surface that holds every
		// non-visual component (Timer/ImageList/ToolTip/dialogs) plus the Controls whose designer
		// is not a ControlDesigner (ContextMenuStrip, PrintPreviewDialog). It is deliberately a
		// SIBLING of the zoomable scroller rather than part of its content, mirroring how the real
		// designer hosts System.Windows.Forms.Design.ComponentTray through
		// ISplitWindowService.AddSplitWindow: the tray keeps its own scrollbar and its own fixed
		// item size, unaffected by the canvas zoom.
		readonly Border trayRegion;
		readonly WrapPanel trayItems = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 3, 4, 3) };
		/// <summary>The real designer's own default tray height (ComponentTray's _trayHeight).</summary>
		const double TrayHeight = 80;
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

		// showLabel stays false: this designer already shows a per-component name label for EVERY
		// control via UpdateDesignGuides' own "label" TextBlock (not just the selected one), so a
		// second, selection-only label from this shared layer would be redundant/wrong.
		readonly SelectionAdornerLayer adornerLayer = new(Array.Empty<string>(), SelectionBrush, showLabel: false);
		readonly Rectangle marqueeBorder;
		readonly Thumb moveThumb;
		readonly Thumb resizeHitTarget;
		readonly Thumb resizeThumb;
		/// <summary>Drag-to-reorder for a selected ToolStripItem (never a Control, so moveThumb -
		/// which drives design/set-bounds - is not applicable): covers the same bounds moveThumb
		/// would, but only ever accumulates a horizontal offset and, on drop, asks the real
		/// designer to move the item to a new INDEX among its siblings via
		/// design/reorder-toolstrip-item, rather than a pixel position.</summary>
		readonly Thumb reorderThumb;
		double reorderDragDeltaX;
		/// <summary>Drag-to-reorder for a selected item that is currently INSIDE an open popup
		/// (a MenuStrip submenu/ContextMenuStrip's own items) rather than laid out on a root
		/// strip - vertical, matching how a dropdown stacks its items top to bottom (the opposite
		/// orientation from <see cref="reorderThumb"/>'s root-strip case). Positioned from the
		/// same dragX/Y/Width/Height rect reorderThumb uses - a popup item's own SurfaceX/Y/Width/
		/// Height are already reported in the same absolute basis a root item's are (see
		/// OnPopupReorderDragCompleted's own note), so no separate coordinate source is needed.</summary>
		readonly Thumb popupReorderThumb;
		double popupReorderDeltaY;
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
		// default theme chrome is exactly the opaque-rectangle trap moveThumb's own comment
		// above describes) positioned by PositionAdorners like every other handle in this file.
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
		// The design surface is unscaled; the shared canvas shell's zoom toolbar controls the
		// presentation scale around it via DesignViewport - the same coordinate math
		// UnoDesignSurfaceControl uses for its zoom/pan, so both backends' conversions share
		// one type (see DesignViewport's doc comment). Zoom/Fit re-derive the viewport and
		// re-present without re-decoding the frame.
		DesignViewport viewport = DesignViewport.Identity(0, 0);
		DesignerComponentInfo selectedComponent;
		double dragX;
		double dragY;
		double dragStartX;
		double dragStartY;
		double dragWidth;
		double dragHeight;
		// Kept next to the Forms overlay rather than in Designer.Presentation so the add-in
		// never requires an ABI change in a host-provided shared presentation assembly.
		Rect renderedSelection;
		int selectedLocalX;
		int selectedLocalY;
		bool showTabOrder;
		/// <summary>Whether each component's name is drawn on the selection outline - wired to
		/// the shared design-canvas toolbar's "Show Names" toggle (DesignerCanvasCapabilities.
		/// ShowNames/ShowNamesRequested), which this control did not previously enable.</summary>
		bool showComponentLabels = true;
		bool resizingDrag;
		bool previewResizeDrag;
		Point previewDragPoint;
		bool marqueeSelecting;
		bool marqueeExtendsSelection;
		Point marqueeStart;
		readonly HashSet<string> selectedComponentNames = new HashSet<string>(StringComparer.Ordinal);
		readonly HashSet<string> lockedComponentNames = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>Empty space kept on every side of the design inside the canvas, so the root
		/// component's own resize handles are reachable and DesignerCanvas's tiled "EdgePattern"
		/// background is visible around the form - matches WpfSurfaceDesignerControl's own
		/// CanvasPadding (the WPF designer already has this; this control did not, which is
		/// exactly why the WinForms designer's canvas visibly had no border around the form
		/// while the WPF/WinUI ones did).</summary>
		const double CanvasMargin = 24;

		static readonly double[] ZoomPresets = { 0.25, 0.5, 0.75, 1.0, 1.5, 2.0 };
		static readonly string[] ZoomLabels = { "Fit", "25%", "50%", "75%", "100%", "150%", "200%" };
		// The zoom combo starts at "100%" (VS behavior), so the initial render must be a
		// literal 100% zoom, not Fit; Fit is a user choice.
		bool fitMode = false;
		double zoomScale = 1.0;

		/// <summary>A Thumb template that draws nothing but a transparent hit-target fill, so the
		/// thumb stays invisible while still receiving mouse input - see moveThumb's own comment
		/// on why relying on the theme's default Thumb template is not safe here.</summary>
		static ControlTemplate CreateTransparentThumbTemplate()
		{
			var surface = new FrameworkElementFactory(typeof(Border));
			surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
			return new ControlTemplate(typeof(Thumb)) { VisualTree = surface };
		}

		void RebuildViewport()
		{
			if (state?.Render == null)
				return;
			// Re-derive the viewport from the current toolbar zoom and re-present. This must
			// not go through Show's frame-sequence guard - a zoom change replays the same
			// SessionState (same Sequence), so the guard would early-return and the zoom would
			// never take effect.
			ApplyViewport();
		}

		public RemoteFormsDesignerControl(FormsDesignerHostClient client, string backendName)
		{
			this.client = client;
			Focusable = true;
			BackendName = backendName;
			Capabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit
				| DesignerCanvasCapabilities.StatusBar | DesignerCanvasCapabilities.ShowNames;
			IsShowingNames = showComponentLabels;
			ShowNamesRequested += (_, value) => {
				showComponentLabels = value;
				UpdateDesignGuides();
			};
			StatusText = $"Starting {BackendName} design host…";
			// The shared DesignerCanvas shell provides the dotted empty-canvas edge pattern and
			// the common zoom toolbar; the design surface is transparent so the edge pattern
			// shows around the rendered form bitmap.

			designSurface.Children.Add(framePresenter.Visual);
			guides = new Canvas { IsHitTestVisible = false };
			designSurface.Children.Add(guides);
			designSurface.Children.Add(snapGuideOverlay);
			adorners = new Canvas { IsHitTestVisible = true };
			marqueeBorder = new Rectangle {
				Stroke = SelectionBrush, StrokeThickness = 1,
				Fill = new SolidColorBrush(Color.FromArgb(35, 0x00, 0x78, 0xD4)),
				StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false,
				Visibility = Visibility.Collapsed
			};
			// moveThumb covers the WHOLE selected control and exists only as an invisible drag
			// target (the visible outline is drawn by adornerLayer). Its Template must be set
			// explicitly: the default WPF Thumb theme template paints its chrome from
			// SystemColors brushes, NOT from TemplateBinding Background, so setting
			// Background=Transparent alone does nothing. Under this app's dark theme (whose
			// Theme.Dark.xaml overrides ControlBrushKey/ControlLightLightColorKey to #252526 /
			// #333337) that chrome rendered as an OPAQUE DARK rectangle over the entire selected
			// control - the "selecting a Panel turns it black" bug, located with DevFlow's
			// ui/tree: the Thumb's inner template Borders reported background #252526/#333337
			// at exactly the panel's rect, on top of the rendered form bitmap.
			moveThumb = new Thumb {
				Background = Brushes.Transparent,
				Cursor = Cursors.SizeAll,
				Visibility = Visibility.Collapsed,
				Template = CreateTransparentThumbTemplate()
			};
			// moveThumb is drawn across the SELECTED component's whole bounds - which, for a
			// TabControl, includes its header strip (the header is visually part of the control's
			// own bounding rect). A plain SizeAll cursor there would advertise "drag to move" over
			// an area that actually switches tabs on click and never actually moves anything - so
			// swap to a plain pointer whenever the mouse sits over one of the reported
			// TabHeaderBounds, matching what a click there will really do.
			moveThumb.MouseMove += (sender, args) => {
				var point = args.GetPosition(framePresenter.Visual);
				var designPoint = new Point(point.X / viewport.Scale, point.Y / viewport.Scale);
				var overHeader = selectedComponent?.TabHeaderBounds.Any(rect =>
					new Rect(rect.X, rect.Y, rect.Width, rect.Height).Contains(designPoint)) == true;
				moveThumb.Cursor = overHeader ? Cursors.Arrow : Cursors.SizeAll;
			};
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
			resizeThumb = new Thumb { Width = 8, Height = 8, Background = Brushes.White, BorderBrush = SelectionBrush, BorderThickness = new Thickness(1), Cursor = Cursors.SizeNWSE, Visibility = Visibility.Collapsed };
			// Keep the conventional 8px visual handle while providing a forgiving transparent
			// input target around it.  At fractional DPI a real pointer can land one or two device
			// pixels off the visible square; without this, ScrollViewer sees the gesture instead of
			// the resize Thumb and scrolls the canvas rather than resizing the selected component.
			resizeHitTarget = new Thumb {
				Width = 20, Height = 20, Background = Brushes.Transparent,
				Cursor = Cursors.SizeNWSE, Visibility = Visibility.Collapsed,
				Template = CreateTransparentThumbTemplate()
			};
			adorners.Children.Add(marqueeBorder);
			adorners.Children.Add(adornerLayer.Visual);
			adorners.Children.Add(moveThumb);
			adorners.Children.Add(reorderThumb);
			adorners.Children.Add(popupReorderThumb);
			adorners.Children.Add(insertionLine);
			adorners.Children.Add(resizeHitTarget);
			adorners.Children.Add(resizeThumb);
			smartTagChevron = CreateSmartTagGlyph();
			smartTagChevron.MouseLeftButtonDown += (sender, args) => {
				args.Handled = true;
				if (selectedComponent != null)
					SmartTagRequested?.Invoke(this, new RemoteSmartTagRequestedEventArgs(selectedComponent.Name, smartTagChevron));
			};
			toolStripInsertChevron = CreateToolStripInsertGlyph();
			toolStripInsertChevron.MouseLeftButtonDown += (sender, args) => {
				args.Handled = true;
				if (toolStripHost != null)
					ToolStripInsertRequested?.Invoke(this, new RemoteToolStripInsertRequestedEventArgs(
						toolStripHost.Name, toolStripHost.Type, toolStripInsertChevron));
			};
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
			designSurface.Children.Add(adorners);
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
			designSurface.Children.Add(disconnectedOverlay);
			// A form can be resized beyond the visible design tab.  Hosting the surface in a
			// ScrollViewer lets the canvas grow with it instead of clipping the bottom-right
			// Thumb (and, consequently, releasing a resize drag outside the canvas).
			scrollContent.Children.Add(designSurface);
			scroller.Content = scrollContent;
			trayRegion = new Border {
				BorderThickness = new Thickness(0, 1, 0, 0),
				BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)),
				Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)),
				Height = TrayHeight,
				Visibility = Visibility.Collapsed,
				// The tray's own scrollbar: item layout is fixed-size, so a form with many
				// components scrolls the tray without touching the design surface's own scroll
				// position or zoom.
				Child = new ScrollViewer {
					VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
					HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
					Content = trayItems
				}
			};
			var contentLayout = new Grid();
			contentLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			contentLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			Grid.SetRow(scroller, 0);
			Grid.SetRow(trayRegion, 1);
			contentLayout.Children.Add(scroller);
			contentLayout.Children.Add(trayRegion);
			ContentHost.Content = contentLayout;

			// Only controls backed by this designer are visible. Editing commands remain in the
			// IDE command system; grid/theme/name/device controls are not inert toolbar chrome.
			foreach (var label in ZoomLabels)
				ZoomCombo.Items.Add(label);
			ZoomCombo.SelectedIndex = 4; // 100%
			ZoomChanged += (_, _) => {
				var index = ZoomCombo.SelectedIndex;
				if (index <= 0) {
					fitMode = true;
				} else {
					fitMode = false;
					zoomScale = ZoomPresets[index - 1];
				}
				RebuildViewport();
			};
			FitRequested += (_, _) => { fitMode = true; RebuildViewport(); };

			AllowDrop = true;
			// handledEventsToo, NOT a plain += handler: the ScrollViewer between the frame image
			// and this control marks MouseLeftButtonDown handled on its way up, so a bubbling
			// handler registered the normal way never ran at all and click-to-select on the canvas
			// could never work (only the Document Outline could change the selection). The
			// resize gesture already worked around the same swallowing with a Preview handler.
			AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseLeftButtonDown), true);
			// Same handledEventsToo requirement as MouseLeftButtonDown above: the ScrollViewer
			// swallows bubbling Move/Up too, so a plain += here left marqueeSelecting stuck true
			// (and the mouse still captured) forever after the FIRST click that missed every
			// known component's rect - silently breaking every subsequent click-to-select attempt,
			// not just marquee-drag, since OnMouseLeftButtonDown's own early-return guard bails
			// out whenever marqueeSelecting is still true.
			AddHandler(MouseMoveEvent, new MouseEventHandler(OnMouseMove), true);
			AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseLeftButtonUp), true);
			// The ScrollViewer hosting the expandable canvas can consume bubbling mouse events.
			// Preview handlers keep the root-form resize gesture reachable even after scrollbars
			// appear, matching the WPF/WinUI designer surfaces' input-routing strategy.
			PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
			PreviewMouseMove += OnPreviewMouseMove;
			PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
			moveThumb.DragStarted += OnDragStarted;
			moveThumb.DragDelta += OnMoveDragDelta;
			moveThumb.DragCompleted += OnDragCompleted;
			reorderThumb.DragStarted += (_, _) => { reorderDragDeltaX = 0; ShowReorderInsertionLine(vertical: false, 0); };
			reorderThumb.DragDelta += (_, e) => { reorderDragDeltaX += e.HorizontalChange; ShowReorderInsertionLine(vertical: false, reorderDragDeltaX); };
			reorderThumb.DragCompleted += (sender, e) => { insertionLine.Visibility = Visibility.Collapsed; OnReorderDragCompleted(sender, e); };
			popupReorderThumb.DragStarted += (_, _) => { popupReorderDeltaY = 0; ShowReorderInsertionLine(vertical: true, 0); };
			popupReorderThumb.DragDelta += (_, e) => { popupReorderDeltaY += e.VerticalChange; ShowReorderInsertionLine(vertical: true, popupReorderDeltaY); };
			popupReorderThumb.DragCompleted += (sender, e) => { insertionLine.Visibility = Visibility.Collapsed; OnPopupReorderDragCompleted(sender, e); };
			resizeThumb.DragStarted += OnDragStarted;
			resizeThumb.DragDelta += OnResizeDragDelta;
			resizeThumb.DragCompleted += OnDragCompleted;
			resizeHitTarget.DragStarted += OnDragStarted;
			resizeHitTarget.DragDelta += OnResizeDragDelta;
			resizeHitTarget.DragCompleted += OnDragCompleted;
			DragOver += OnDragOver;
			Drop += OnDrop;
			KeyDown += OnKeyDown;
		}

		public string SelectedComponentName { get; private set; } = "";

		/// <summary>
		/// Surface geometry for the integration tests' resize-drag assertions: the rendered form
		/// bitmap bounds, the current selection outline bounds, the selected element's bounds and
		/// the bottom-right resize handle position - all in screen coordinates. The selection
		/// outline must coincide with the rendered form (and the handle sit at its bottom-right
		/// corner) both before and after a resize drag; this is the smoke probe for that
		/// invariant.
		/// </summary>
		public DesignerSurfaceGeometry SurfaceGeometry()
		{
			var frame = DesignerSurfaceGeometryProbe.ScreenBoundsOf(framePresenter.Visual);
			Rect selection = default;
			if (selectedComponent != null)
			{
				selection = DesignerSurfaceGeometryProbe.DesignRectToScreen(viewport,
					new Rect(selectedComponent.SurfaceX, selectedComponent.SurfaceY,
						selectedComponent.Width, selectedComponent.Height),
					designSurface);
			}
			var resizeBounds = DesignerSurfaceGeometryProbe.ScreenBoundsOf(resizeThumb);
			var handle = resizeBounds.IsEmpty
				? new Point(selection.X + selection.Width, selection.Y + selection.Height)
				: new Point(resizeBounds.X + resizeBounds.Width / 2, resizeBounds.Y + resizeBounds.Height / 2);
			return new DesignerSurfaceGeometry(frame, selection, handle, selection);
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
		public event EventHandler<RemoteComponentEventArgs> DeleteRequested;
		public event EventHandler<RemoteComponentEventArgs> DefaultEventRequested;
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
		/// moveThumb's template comment on why a real Button/Thumb's default theme chrome cannot
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
			renameEditor.Text = selectedComponent.Name;
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
		}

		void CommitRename()
		{
			if (!renaming || selectedComponent == null) { CancelRename(); return; }
			var newName = renameEditor.Text?.Trim() ?? "";
			var oldName = selectedComponent.Name;
			renaming = false;
			renameEditor.Visibility = Visibility.Collapsed;
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
			// Before the frame-freshness early-returns below: the tray's contents come from the
			// component list, not from the rendered bitmap, so a state that carries no new frame
			// (or no frame at all) still has to refresh it.
			UpdateComponentTray();
			UpdatePopupOverlays(state);
			if (state.Render == null || (String.IsNullOrEmpty(state.Render.PngBase64) && String.IsNullOrEmpty(state.Render.Data))) {
				return;
			}
			if (state.Render.Sequence > 0 && state.Render.Sequence <= lastFrameSequence) {
				return;
			}
			lastFrameSequence = state.Render.Sequence;
			var dpiForStatus = Math.Max(1, state.Render.Dpi);
			StatusText = $"Rendered by {BackendName} design host ({state.Render.Width / dpiForStatus:0}×{state.Render.Height / dpiForStatus:0}).";
			ImageSource bitmap;
			if (!String.IsNullOrEmpty(state.Render.Data)) {
				var pixels = DesignerFrameCodec.DecodeBgra32(state.Render);
				bitmap = BitmapSource.Create(state.Render.Width, state.Render.Height, 96, 96,
					PixelFormats.Bgra32, null, pixels, state.Render.Width * 4);
				bitmap.Freeze();
			} else {
				var png = new BitmapImage();
				using (var stream = new MemoryStream(Convert.FromBase64String(state.Render.PngBase64))) {
					png.BeginInit();
					png.CacheOption = BitmapCacheOption.OnLoad;
					png.StreamSource = stream;
					png.EndInit();
					png.Freeze();
				}
				bitmap = png;
			}
			framePresenter.SetSource(bitmap);
			ApplyViewport();
			if (!String.IsNullOrEmpty(SelectedComponentName)) {
				selectedComponent = state.Components.FirstOrDefault(item => item.Name == SelectedComponentName);
				selectedComponentNames.RemoveWhere(name => !state.Components.Any(item => item.Name == name));
				lockedComponentNames.RemoveWhere(name => !state.Components.Any(item => item.Name == name));
				UpdateAdorners();
			}
			AutomationProperties.SetName(this, selectedComponent?.AccessibleName ?? "WinForms designer");
			AutomationProperties.SetHelpText(this, selectedComponent?.AccessibleDescription ?? "");
		}

		void ApplyViewport()
		{
			var dpi = Math.Max(1, state.Render.Dpi);
			var designWidth = state.Render.Width / dpi;
			var designHeight = state.Render.Height / dpi;
			// Fit/zoom inside an inset area, then shift everything back out by the same margin
			// through the viewport's own pan - so the frame bitmap, the guide overlay and every
			// DesignToSurface-based adorner all move together and stay aligned (matches
			// WpfSurfaceDesignerControl's identical CanvasPadding treatment).
			var availableWidth = Math.Max(0, scroller.ViewportWidth - 2 * CanvasMargin);
			var availableHeight = Math.Max(0, scroller.ViewportHeight - 2 * CanvasMargin);
			if (fitMode)
				viewport = DesignViewport.Fit(designWidth, designHeight, availableWidth, availableHeight, 1.0, CanvasMargin, CanvasMargin);
			else
				viewport = DesignViewport.Zoom(designWidth, designHeight, availableWidth, availableHeight, zoomScale, CanvasMargin, CanvasMargin);
			framePresenter.Resize(viewport);
			// The rendered form must sit at the viewport's base (centered-fit origin + pan),
			// exactly where the DesignToSurface-based guides/adorners are placed - otherwise the
			// selection outline and the bitmap drift apart whenever Scale != 1.
			framePresenter.Visual.Margin = new Thickness(
				Math.Max(0, viewport.OriginX) + viewport.PanX,
				Math.Max(0, viewport.OriginY) + viewport.PanY, 0, 0);
			UpdateCanvasExtent();
			UpdateDesignGuides();
			PositionPopupOverlays();
			if (selectedComponent != null)
				UpdateAdorners();
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
		}

		/// <summary>Places every live popup overlay at its reported surface position, sized by the
		/// current zoom - the same DesignToSurface basis every other adorner uses, so a popup
		/// stays visually attached to the strip it belongs to at any zoom level.</summary>
		void PositionPopupOverlays()
		{
			foreach (var image in popupOverlays.Values) {
				if (image.Tag is not DesignerPopupFrame popup || popup.Render == null)
					continue;
				var (left, top) = viewport.DesignToSurface(popup.X, popup.Y);
				Canvas.SetLeft(image, left);
				Canvas.SetTop(image, top);
				var dpi = Math.Max(1, popup.Render.Dpi);
				image.Width = popup.Render.Width / dpi * viewport.Scale;
				image.Height = popup.Render.Height / dpi * viewport.Scale;
				if (popupEditors.TryGetValue(popup.OwnerElementId, out var editor))
					editor.Reposition(viewport, popup.X, popup.Y);
			}
		}

		/// <summary>A click on a popup overlay: hit-test that popup's OWN surface directly (never
		/// the root form's coordinate space) and select whatever item is under the pointer.</summary>
		async void OnPopupClicked(string ownerElementId, Image image, MouseButtonEventArgs args)
		{
			try {
				var point = args.GetPosition(image);
				var designPoint = new Point(point.X / viewport.Scale, point.Y / viewport.Scale);
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
			} catch (Exception exception) {
				ICSharpCode.Core.LoggingService.Warn(
					"RemoteFormsDesignerControl.OnPopupClicked(" + ownerElementId + "): " + exception.Message);
			}
		}

		/// <summary>Rebuilds the component tray from the reported components. Each entry is the
		/// component's real WinForms icon plus its name, sized independently of the canvas zoom
		/// (the tray is not inside the zoomed surface), and selecting one routes through the same
		/// single-selection path as the Document Outline so the Properties pad and the outline
		/// follow along. Hidden entirely when the form has no tray components.</summary>
		void UpdateComponentTray()
		{
			var trayComponents = state?.Components?.Where(item => item.IsTrayComponent).ToArray()
				?? Array.Empty<DesignerComponentInfo>();
			trayItems.Children.Clear();
			trayRegion.Visibility = trayComponents.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
			foreach (var component in trayComponents) {
				var content = new StackPanel { Orientation = Orientation.Horizontal };
				var icon = TrayIconSource(component.Type);
				if (icon != null) {
					content.Children.Add(new Image {
						Source = icon, Width = 16, Height = 16,
						Margin = new Thickness(0, 0, 4, 0),
						VerticalAlignment = VerticalAlignment.Center
					});
				}
				content.Children.Add(new TextBlock {
					Text = component.Name, VerticalAlignment = VerticalAlignment.Center
				});
				var entry = new Border {
					Padding = new Thickness(4, 3, 6, 3),
					Margin = new Thickness(0, 0, 4, 3),
					CornerRadius = new CornerRadius(2),
					BorderThickness = new Thickness(1),
					Cursor = Cursors.Hand,
					ToolTip = component.Type,
					Tag = component.Name,
					Child = content
				};
				var componentName = component.Name;
				entry.MouseLeftButtonDown += (_, args) => {
					args.Handled = true;
					SelectSingleComponent(componentName, takeFocus: false);
				};
				trayItems.Children.Add(entry);
			}
			RefreshTrayHighlight();
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
				entry.Background = selected ? new SolidColorBrush(Color.FromRgb(0xCC, 0xE4, 0xF7)) : Brushes.Transparent;
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

		void UpdateDesignGuides()
		{
			guides.Children.Clear();
			if (state?.Render == null) return;
			// The form outline must cover exactly the rendered bitmap, so both corners go
			// through DesignToSurface (same space the frame sits in once zoomed/centered).
			var (fx, fy) = viewport.DesignToSurface(0, 0);
			var (fx2, fy2) = viewport.DesignToSurface(state.Render.Width / Math.Max(1, state.Render.Dpi),
				state.Render.Height / Math.Max(1, state.Render.Dpi));
			var formOutline = new Rectangle {
				Width = Math.Max(1, fx2 - fx), Height = Math.Max(1, fy2 - fy),
				Stroke = Brushes.Gray, StrokeThickness = 1
			};
			Canvas.SetLeft(formOutline, fx);
			Canvas.SetTop(formOutline, fy);
			guides.Children.Add(formOutline);
			// Items inside an expanded dropdown are skipped: once the child pushes selection into
			// the real designer, that dropdown is rendered by WinForms itself (with its own
			// adorners), and drawing our dashed outline plus a name label on top of it just
			// obscured the real menu text.
			// item.IsVisible: a control on a TabPage that is not its TabControl's SelectedTab still
			// reports the SurfaceX/Y it WOULD sit at, and every TabPage occupies the same rect - so
			// drawing its outline/name tag anyway put phantom overlays exactly on top of whichever
			// page really was showing. That is what made a correctly-rendered TabControl look like
			// it was painting the wrong page's content, and why clicking one of those phantoms
			// selected the enclosing TabPage instead (the child's own hit-test correctly refuses to
			// resolve to a hidden control). See DesignerComponentInfo.IsVisible.
			foreach (var component in state.Components.Where(item => !String.IsNullOrEmpty(item.Parent)
				&& !item.IsDropDownItem && item.IsVisible)) {
				var (surfaceX, surfaceY) = viewport.DesignToSurface(component.SurfaceX, component.SurfaceY);
				var (surfaceX2, surfaceY2) = viewport.DesignToSurface(
					component.SurfaceX + component.Width, component.SurfaceY + component.Height);
				var outline = new Rectangle {
					Width = Math.Max(1, surfaceX2 - surfaceX), Height = Math.Max(1, surfaceY2 - surfaceY),
					Stroke = lockedComponentNames.Contains(component.Name) ? Brushes.DarkOrange
						: selectedComponentNames.Contains(component.Name) ? SelectionBrush : new SolidColorBrush(Color.FromArgb(150, 80, 80, 80)),
					StrokeThickness = selectedComponentNames.Contains(component.Name) ? 2 : 1,
					StrokeDashArray = selectedComponentNames.Contains(component.Name) ? null : new DoubleCollection { 3, 2 }
				};
				Canvas.SetLeft(outline, surfaceX);
				Canvas.SetTop(outline, surfaceY);
				guides.Children.Add(outline);
				if (showComponentLabels && component.Height >= 18 && component.Width >= 35) {
					// Outside/above the control's own bounds (matching WinUI's own out-of-process
					// designer, whose selection/name label sits above the box rather than
					// overlapping the control's content) - previously drawn INSIDE at
					// (surfaceX + 2, surfaceY + 2), which covered up the control's own rendered
					// content (e.g. a Button's Text) right where a user would look for it.
					var label = new TextBlock {
						Text = component.Name, FontSize = 10, Foreground = Brushes.White,
						Background = new SolidColorBrush(Color.FromArgb(190, 80, 80, 80)),
						Padding = new Thickness(2, 0, 2, 0)
					};
					Canvas.SetLeft(label, surfaceX);
					// A TabPage is the one case where "above my own bounds" is never free: a page's
					// rect starts immediately below its TabControl's tab strip, so a label placed
					// above it covers the active tab's own header text (it hid the word "General"
					// on this repo's TabControlFixture). Keep that one inside its own page body.
					var labelSitsInside = component.Type == "System.Windows.Forms.TabPage";
					Canvas.SetTop(label, labelSitsInside ? surfaceY + 2 : Math.Max(0, surfaceY - 15));
					guides.Children.Add(label);
				}
				if (showTabOrder) {
					var tabIndex = component.Properties.FirstOrDefault(item => item.Name == "TabIndex")?.Value ?? "?";
					var badge = new Border {
						Background = Brushes.RoyalBlue, CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 1, 5, 1),
						Child = new TextBlock { Text = tabIndex, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11 }
					};
					Canvas.SetLeft(badge, surfaceX - 5);
					Canvas.SetTop(badge, surfaceY - 8);
					guides.Children.Add(badge);
				}
			}
		}

		public void SetTabOrderMode(bool value)
		{
			showTabOrder = value;
			UpdateDesignGuides();
		}

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
			UpdateDesignGuides();
			UpdateAdorners();
		}

		public void ShowDisconnected(string message)
		{
			disconnectedText.Text = message;
			disconnectedOverlay.Visibility = Visibility.Visible;
			adornerLayer.ClearSelection();
			moveThumb.Visibility = resizeHitTarget.Visibility = resizeThumb.Visibility = Visibility.Collapsed;
			StatusText = message;
		}

		public bool TryGetComponentScreenBounds(string componentName, out Rect bounds)
		{
			bounds = Rect.Empty;
			var component = state?.Components?.FirstOrDefault(item => item.Name == componentName);
			if (component == null || !framePresenter.Visual.IsVisible)
				return false;
			// Both corners through DesignToSurface so the UIA peer bounds track the (possibly
			// zoomed) design rect, then PointToScreen from the design surface grid.
			var (x, y) = viewport.DesignToSurface(component.SurfaceX, component.SurfaceY);
			var (x2, y2) = viewport.DesignToSurface(
				component.SurfaceX + component.Width, component.SurfaceY + component.Height);
			var topLeft = designSurface.PointToScreen(new Point(x, y));
			var bottomRight = designSurface.PointToScreen(new Point(x2, y2));
			bounds = new Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
			return true;
		}

		/// <summary>Whether the press originated outside the design surface's own CONTENT - i.e.
		/// anywhere in the surrounding chrome: the base DesignerCanvas' toolbar/status bar (Show
		/// Names, zoom combo, ...), the component tray below the surface, or the hosting
		/// ScrollViewer's own scrollbars. MUST be checked before anything else, because this
		/// handler is registered with handledEventsToo (see the constructor), so it also sees every
		/// press those unrelated controls already consumed. Without the check,
		/// e.GetPosition(framePresenter.Visual) computes a nonsense point far outside any known
		/// component and the handler starts a marquee-drag, whose zero-size completion then selects
		/// the ROOT FORM and calls Focus() - which is how clicking the Show Names button appeared
		/// to "move the canvas", how clicking a component-tray entry lost its selection a moment
		/// later, and how clicking a canvas scrollbar jumped the selection to the form.
		///
		/// The boundary is deliberately `scrollContent` rather than `ContentHost` or `scroller`:
		/// the tray is a sibling of the scroller inside ContentHost, and the scrollbars belong to
		/// the ScrollViewer's own template rather than to its Content, so only the content subtree
		/// is really "the surface". The empty canvas margin around the rendered form IS part of
		/// that subtree (designSurface is sized to at least the viewport), so rubber-band
		/// selection there keeps working.</summary>
		bool IsOutsideDesignSurface(object source)
		{
			for (var node = source as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node)) {
				if (node == scrollContent) return false;
			}
			return true;
		}

		/// <summary>This document's components as click candidates for
		/// <see cref="DesignSurfaceClickArbiter"/> - surface-space bounds, plus the Parent/IsVisible
		/// facts it needs to tell "drill into a child" from "drag what is already selected" and to
		/// ignore controls sitting on a TabPage that is not currently showing.</summary>
		IReadOnlyList<DesignSurfaceClickCandidate> ClickCandidates()
			=> state?.Components?.Select(component => new DesignSurfaceClickCandidate(
					component.Name, component.Parent,
					new Rect(component.SurfaceX, component.SurfaceY, component.Width, component.Height),
					component.IsVisible)).ToList()
				?? (IReadOnlyList<DesignSurfaceClickCandidate>)Array.Empty<DesignSurfaceClickCandidate>();

		/// <summary>Whether the click originated on one of the adorner-layer glyphs (drag/resize
		/// thumbs, smart tag, ToolStrip insert button), which handle their own clicks. Needed
		/// because this handler is registered with handledEventsToo, so it also sees the presses
		/// those glyphs already consumed.</summary>
		bool IsAdornerSource(object source)
		{
			for (var node = source as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node)) {
				if (node == adorners) return true;
				if (node == framePresenter.Visual) return false;
			}
			return false;
		}

		async void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			try {
				if (IsOutsideDesignSurface(e.OriginalSource))
					return;
				var extendSelection = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
				// GetPosition on the (possibly zoomed) frame image yields surface pixels;
				// component bounds and the child's hit-testing are design-space.
				var point = e.GetPosition(framePresenter.Visual);
				var designPoint = new Point(point.X / viewport.Scale, point.Y / viewport.Scale);
				// A tab HEADER is not a component of its own - it is painted by the TabControl
				// itself - so a click on one can never be found by the generic hit-test below (that
				// only ever resolves to a real component). Checked first, and deliberately BEFORE
				// the IsAdornerSource bail-out below: when the TabControl itself is the current
				// selection, moveThumb is drawn across its ENTIRE bounds - including the header
				// strip, since that strip is part of the control's own bounding rect - so a plain
				// IsAdornerSource(e.OriginalSource) check would see the click as landing on
				// moveThumb (cursor shows the "move" SizeAll cursor there) and return before ever
				// trying a header switch, no matter how many times the header was clicked. Real
				// VS's TabControlDesigner intercepts a header click before anything else gets a
				// chance to see it; this must too, even through an adorner drawn on top of it.
				if (!extendSelection && !previewResizeDrag && !resizingDrag && !marqueeSelecting
					&& await TrySwitchTabAsync(designPoint)) {
					// moveThumb (a real WPF Thumb) may already have captured the mouse for its own
					// drag gesture as this same event bubbled through it, before reaching here -
					// release it, or the immediately-following near-zero-delta MouseMove/MouseUp
					// would still start/complete a spurious move-drag of the TabControl right after
					// switching tabs.
					if (moveThumb.IsMouseCaptured) moveThumb.ReleaseMouseCapture();
					e.Handled = true;
					return;
				}
				if (previewResizeDrag || resizingDrag || marqueeSelecting)
					return;
				// Who owns this press - an adorner glyph drawn on top, a component underneath it, or
				// empty canvas - is decided by the shared DesignSurfaceClickArbiter rather than
				// inline here. Three separate regressions came out of this arbitration when it lived
				// in this method (tab headers unclickable, then move-drag broken outright, then
				// move-drag broken for nested controls only); see that type's own remarks, and
				// DesignSurfaceClickArbiterTests for the cases now pinned down.
				var decision = DesignSurfaceClickArbiter.Decide(
					ClickCandidates(), designPoint, SelectedComponentName, IsAdornerSource(e.OriginalSource));
				if (decision.Action == DesignSurfaceClickAction.LetAdornerHandle)
					return;
				if (decision.ReleaseAdornerCapture && moveThumb.IsMouseCaptured)
					moveThumb.ReleaseMouseCapture();
				if (decision.Action == DesignSurfaceClickAction.StartMarquee) {
					marqueeSelecting = true;
					marqueeExtendsSelection = extendSelection;
					marqueeStart = designPoint;
					marqueeBorder.Width = marqueeBorder.Height = 0;
					var (mx, my) = viewport.DesignToSurface(designPoint.X, designPoint.Y);
					Canvas.SetLeft(marqueeBorder, mx);
					Canvas.SetTop(marqueeBorder, my);
					marqueeBorder.Visibility = Visibility.Visible;
					CaptureMouse();
					e.Handled = true;
					return;
				}
				var result = await client.HitTestAsync(version, (int)designPoint.X, (int)designPoint.Y, CancellationToken.None);
				if (!extendSelection) selectedComponentNames.Clear();
				if (!String.IsNullOrEmpty(result.ComponentName)) {
					if (extendSelection && selectedComponentNames.Contains(result.ComponentName)) selectedComponentNames.Remove(result.ComponentName);
					else selectedComponentNames.Add(result.ComponentName);
				}
				SelectedComponentName = selectedComponentNames.Contains(result.ComponentName)
					? result.ComponentName : selectedComponentNames.FirstOrDefault() ?? "";
				selectedComponent = state?.Components?.FirstOrDefault(item => item.Name == SelectedComponentName);
				UpdateDesignGuides();
				UpdateAdorners();
				Focus();
				SelectionChanged?.Invoke(this, EventArgs.Empty);
				if (e.ClickCount == 2 && !extendSelection && !String.IsNullOrEmpty(SelectedComponentName)) {
					DefaultEventRequested?.Invoke(this, new RemoteComponentEventArgs(SelectedComponentName));
					e.Handled = true;
				}
			} catch (Exception exception) {
				// Was a blanket empty catch - any exception here (including a HitTestAsync RPC
				// fault) used to make a real click on a real control silently no-op instead of
				// selecting anything, with no diagnostic trail at all.
				try {
					System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OpenDevelop.FormsDesigner.host.log"),
						$"{DateTimeOffset.Now:O} OnMouseLeftButtonDown failed: {exception}{Environment.NewLine}");
				} catch { }
			}
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

		void OnMouseMove(object sender, MouseEventArgs e)
		{
			if (!marqueeSelecting || e.LeftButton != MouseButtonState.Pressed) return;
			// Marquee state is design-space; convert both corners before drawing so the
			// rubber band tracks the zoomed design rect exactly.
			var point = e.GetPosition(framePresenter.Visual);
			var designPoint = new Point(point.X / viewport.Scale, point.Y / viewport.Scale);
			var left = Math.Min(marqueeStart.X, designPoint.X);
			var top = Math.Min(marqueeStart.Y, designPoint.Y);
			var (sx, sy) = viewport.DesignToSurface(left, top);
			var (sx2, sy2) = viewport.DesignToSurface(
				left + Math.Abs(designPoint.X - marqueeStart.X),
				top + Math.Abs(designPoint.Y - marqueeStart.Y));
			Canvas.SetLeft(marqueeBorder, sx);
			Canvas.SetTop(marqueeBorder, sy);
			marqueeBorder.Width = Math.Max(0, sx2 - sx);
			marqueeBorder.Height = Math.Max(0, sy2 - sy);
		}

		void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (!marqueeSelecting) return;
			marqueeSelecting = false;
			ReleaseMouseCapture();
			var bounds = new Rect(Canvas.GetLeft(marqueeBorder), Canvas.GetTop(marqueeBorder),
				marqueeBorder.Width, marqueeBorder.Height);
			marqueeBorder.Visibility = Visibility.Collapsed;
			if (!marqueeExtendsSelection) selectedComponentNames.Clear();
			if (bounds.Width >= 3 || bounds.Height >= 3) {
				// IsVisible: rubber-banding over a TabControl must not also select the controls
				// sitting on its OTHER, hidden pages, whose reported bounds overlap the page that
				// is showing - see DesignerComponentInfo.IsVisible.
				foreach (var component in state.Components.Where(item => !String.IsNullOrEmpty(item.Parent) && item.IsVisible)) {
					// The marquee rect is drawn in surface space; convert each component rect
					// the same way before intersecting.
					var (cx, cy) = viewport.DesignToSurface(component.SurfaceX, component.SurfaceY);
					var (cx2, cy2) = viewport.DesignToSurface(
						component.SurfaceX + component.Width, component.SurfaceY + component.Height);
					var componentBounds = new Rect(cx, cy, cx2 - cx, cy2 - cy);
					if (bounds.IntersectsWith(componentBounds)) selectedComponentNames.Add(component.Name);
				}
			} else if (!marqueeExtendsSelection) {
				var root = state.Components.FirstOrDefault(item => String.IsNullOrEmpty(item.Parent));
				if (root != null) selectedComponentNames.Add(root.Name);
			}
			SelectedComponentName = selectedComponentNames.FirstOrDefault() ?? "";
			selectedComponent = state.Components.FirstOrDefault(item => item.Name == SelectedComponentName);
			UpdateDesignGuides();
			UpdateAdorners();
			Focus();
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			// SelectionChanged's own handlers (Properties pad, Outline pad) update their selected
			// row/object as a result of this click - if either grabs WPF keyboard focus doing so
			// (a common side effect of programmatically selecting a TreeView/grid row), it steals
			// focus AWAY from the canvas immediately after the Focus() call above, silently
			// breaking every canvas keyboard gesture (F2, Delete, Tab, arrows) for the rest of this
			// click. Re-asserting focus here, after those handlers have already run, wins that race.
			Focus();
			e.Handled = true;
		}

		void OnKeyDown(object sender, KeyEventArgs e)
		{
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

		void OnDragStarted(object sender, DragStartedEventArgs e)
		{
			if (selectedComponent == null || lockedComponentNames.Contains(selectedComponent.Name)) return;
			resizingDrag = ReferenceEquals(sender, resizeThumb) || ReferenceEquals(sender, resizeHitTarget);
			BeginDrag();
		}

		void BeginDrag()
		{
			if (selectedComponent == null)
				return;
			dragX = selectedComponent.SurfaceX;
			dragY = selectedComponent.SurfaceY;
			dragStartX = dragX;
			dragStartY = dragY;
			selectedLocalX = selectedComponent.X;
			selectedLocalY = selectedComponent.Y;
			dragWidth = selectedComponent.Width;
			dragHeight = selectedComponent.Height;
			SetSnapGuides(Array.Empty<(bool, double)>());
		}

		bool IsOverResizeHitTarget(Point point)
		{
			if (resizeHitTarget.Visibility != Visibility.Visible || !resizeHitTarget.IsEnabled)
				return false;
			// Compare in the root canvas's coordinate space rather than relying on Canvas.Left in
			// the ScrollViewer child.  LibreWPF's composed ScrollViewer can apply its own transform
			// between the two, whereas TranslatePoint follows the actual rendered visual chain.
			var centre = resizeThumb.TranslatePoint(
				new Point(resizeThumb.ActualWidth / 2, resizeThumb.ActualHeight / 2), this);
			const double hitSlop = 16;
			return Math.Abs(point.X - centre.X) <= hitSlop && Math.Abs(point.Y - centre.Y) <= hitSlop;
		}

		void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (selectedComponent == null || lockedComponentNames.Contains(selectedComponent.Name))
				return;
			var pt = e.GetPosition(this);
			var hitTest = IsOverResizeHitTarget(pt);
			var thumbCentre = resizeThumb.TranslatePoint(new Point(resizeThumb.ActualWidth / 2, resizeThumb.ActualHeight / 2), this);
			if (!hitTest)
				return;
			resizingDrag = true;
			previewResizeDrag = true;
			previewDragPoint = e.GetPosition(this);
			BeginDrag();
			CaptureMouse();
			e.Handled = true;
		}

		void OnPreviewMouseMove(object sender, MouseEventArgs e)
		{
			if (!previewResizeDrag)
				return;
			if (e.LeftButton != MouseButtonState.Pressed)
			{
				CompletePreviewResizeDrag(canceled: true);
				return;
			}
			var point = e.GetPosition(this);
			var scale = Math.Max(0.0001, viewport.Scale);
			var deltaX = (point.X - previewDragPoint.X) / scale;
			var deltaY = (point.Y - previewDragPoint.Y) / scale;
			dragWidth = Math.Max(8, dragWidth + deltaX);
			dragHeight = Math.Max(8, dragHeight + deltaY);
			previewDragPoint = point;
			UpdateCanvasExtent();
			PositionAdorners();
			ScrollResizeHandleIntoView();
			e.Handled = true;
		}

		void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (!previewResizeDrag)
				return;
			CompletePreviewResizeDrag(canceled: false);
			e.Handled = true;
		}

		void CompletePreviewResizeDrag(bool canceled)
		{
			if (!previewResizeDrag)
				return;
			previewResizeDrag = false;
			if (IsMouseCaptured)
				ReleaseMouseCapture();
			if (selectedComponent == null || canceled) {
				return;
			}
			var selection = renderedSelection;
			var selectionWidth = (int)Math.Round(selection.Width);
			var selectionHeight = (int)Math.Round(selection.Height);
			BoundsChanged?.Invoke(this, new RemoteBoundsChangedEventArgs(selectedComponent.Name,
				selectedLocalX + (int)Math.Round(selection.X - selectedComponent.SurfaceX),
				selectedLocalY + (int)Math.Round(selection.Y - selectedComponent.SurfaceY),
				selectionWidth, selectionHeight));
		}

		void OnMoveDragDelta(object sender, DragDeltaEventArgs e)
		{
			// DragDelta reports surface pixels; convert to design units (the adorner math and
			// the child's coordinates are design-space).
			var scale = viewport.Scale;
			var proposedX = Math.Max(0, dragX + e.HorizontalChange / scale);
			var proposedY = Math.Max(0, dragY + e.VerticalChange / scale);
			// Snap the dragged component's edges/centre to nearby siblings' edges/centres and
			// show alignment guides while dragging (move only - resizes are not snapped),
			// matching UnoDesignSurfaceControl's own ApplySnap behavior.
			var (snapDx, snapDy, guideLines) = SnapGuideCalculator.ApplySnap(
				(dragStartX, dragStartY, dragWidth, dragHeight),
				proposedX - dragStartX, proposedY - dragStartY, SiblingBounds());
			dragX = dragStartX + snapDx;
			dragY = dragStartY + snapDy;
			SetSnapGuides(guideLines);
			PositionAdorners();
		}

		/// <summary>Every other component's design-space bounds, for <see cref="SnapGuideCalculator"/>
		/// to snap the dragged component against.</summary>
		IEnumerable<(double X, double Y, double Width, double Height)> SiblingBounds()
		{
			if (state?.Components == null || selectedComponent == null)
				yield break;
			foreach (var component in state.Components)
			{
				if (component.Name == selectedComponent.Name)
					continue;
				yield return (component.SurfaceX, component.SurfaceY, component.Width, component.Height);
			}
		}

		/// <summary>Shows snap alignment guides at the given design positions
		/// ((isVertical, position) pairs); empty clears them.</summary>
		void SetSnapGuides(IReadOnlyList<(bool IsVertical, double Position)> guidesToShow)
		{
			foreach (var guide in snapGuides)
				snapGuideOverlay.Children.Remove(guide);
			snapGuides.Clear();
			if (state?.Render == null || guidesToShow.Count == 0)
				return;
			var dpi = Math.Max(1, state.Render.Dpi);
			var designWidth = state.Render.Width / dpi;
			var designHeight = state.Render.Height / dpi;
			var (x0, y0) = viewport.DesignToSurface(0, 0);
			var (x1, y1) = viewport.DesignToSurface(designWidth, designHeight);
			foreach (var (isVertical, position) in guidesToShow)
			{
				var guide = new Rectangle {
					Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x5D, 0x2A)),
					IsHitTestVisible = false
				};
				if (isVertical)
				{
					var (px, _) = viewport.DesignToSurface(position, 0);
					Canvas.SetLeft(guide, px);
					Canvas.SetTop(guide, y0);
					guide.Width = 1;
					guide.Height = Math.Max(1, y1 - y0);
				}
				else
				{
					var (_, py) = viewport.DesignToSurface(0, position);
					Canvas.SetLeft(guide, x0);
					Canvas.SetTop(guide, py);
					guide.Width = Math.Max(1, x1 - x0);
					guide.Height = 1;
				}
				snapGuides.Add(guide);
				snapGuideOverlay.Children.Add(guide);
			}
		}

		void OnResizeDragDelta(object sender, DragDeltaEventArgs e)
		{
			var scale = viewport.Scale;
			dragWidth = Math.Max(8, dragWidth + e.HorizontalChange / scale);
			dragHeight = Math.Max(8, dragHeight + e.VerticalChange / scale);
			UpdateCanvasExtent();
			PositionAdorners();
			// PositionAdorners runs while a new frame/selection can still be in WPF's measure
			// pass, when ScrollViewer.ViewportHeight is zero or stale.  Defer one dispatcher
			// turn so the actual scrollbar viewport is known before deciding whether to scroll.
			Dispatcher.BeginInvoke(new Action(() => {
				// A bottom-right thumb is reached from the bottom-right canvas corner.  When an
				// axis has a scrollbar, partial "just visible" scrolling still leaves the pointer
				// competing with that bar; place both viewport axes at their real ends first.
				scroller.ScrollToRightEnd();
				scroller.ScrollToBottom();
				ScrollResizeHandleIntoView();
			}), System.Windows.Threading.DispatcherPriority.Loaded);
		}

		/// <summary>Expands the scrollable design surface enough to retain the selected item's
		/// bottom-right resize handle and the normal empty-canvas margin.  It never shrinks while
		/// a drag is active, which avoids the scrollbar moving underneath the captured pointer.</summary>
		void UpdateCanvasExtent()
		{
			if (state?.Render == null)
				return;
			var dpi = Math.Max(1, state.Render.Dpi);
			var designWidth = state.Render.Width / dpi;
			var designHeight = state.Render.Height / dpi;
			if (selectedComponent != null)
			{
				designWidth = Math.Max(designWidth, dragX + dragWidth);
				designHeight = Math.Max(designHeight, dragY + dragHeight);
			}
			var (right, bottom) = viewport.DesignToSurface(designWidth, designHeight);
			designSurface.Width = Math.Max(scroller.ViewportWidth, right + CanvasMargin);
			designSurface.Height = Math.Max(scroller.ViewportHeight, bottom + CanvasMargin);
			scrollContent.Width = designSurface.Width;
			scrollContent.Height = designSurface.Height;
		}

		/// <summary>Keeps a live resize's handle inside the visible canvas.  This is deliberately
		/// performed during the drag, rather than after completion, so a user can continue growing
		/// a form without driving the pointer beyond the tab's edge.</summary>
		void ScrollResizeHandleIntoView()
		{
			// Do not derive an offset from design coordinates here.  That used to scroll on the
			// first drag sample (even while the handle was already visible), and the resulting
			// ScrollViewer transform canceled the captured pointer's next relative movement.
			// Instead, inspect the actual rendered handle in the viewport and scroll only after
			// it reaches the visible edge.
			if (scroller.ViewportWidth <= 0 || scroller.ViewportHeight <= 0)
				return;
			var handle = resizeThumb.TranslatePoint(
				new Point(resizeThumb.ActualWidth / 2, resizeThumb.ActualHeight / 2), scroller);
			const double edgeMargin = 24;
			if (handle.X > scroller.ViewportWidth - edgeMargin)
				scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + handle.X - (scroller.ViewportWidth - edgeMargin));
			else if (handle.X < edgeMargin)
				scroller.ScrollToHorizontalOffset(Math.Max(0, scroller.HorizontalOffset + handle.X - edgeMargin));
			if (handle.Y > scroller.ViewportHeight - edgeMargin)
				scroller.ScrollToVerticalOffset(scroller.VerticalOffset + handle.Y - (scroller.ViewportHeight - edgeMargin));
			else if (handle.Y < edgeMargin)
				scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset + handle.Y - edgeMargin));
		}

		void OnDragCompleted(object sender, DragCompletedEventArgs e)
		{
			SetSnapGuides(Array.Empty<(bool, double)>());
			if (selectedComponent == null || e.Canceled) return;
			if (!resizingDrag) {
				SelectionMoveRequested?.Invoke(this, new RemoteSelectionMoveEventArgs(
					(int)Math.Round(dragX - selectedComponent.SurfaceX), (int)Math.Round(dragY - selectedComponent.SurfaceY)));
				return;
			}
			BoundsChanged?.Invoke(this, new RemoteBoundsChangedEventArgs(selectedComponent.Name,
				selectedLocalX + (int)Math.Round(dragX - selectedComponent.SurfaceX),
				selectedLocalY + (int)Math.Round(dragY - selectedComponent.SurfaceY),
				(int)Math.Round(dragWidth), (int)Math.Round(dragHeight)));
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
			var (surfaceLeft, surfaceTop) = viewport.DesignToSurface(left, top);
			Canvas.SetLeft(insertionLine, surfaceLeft);
			Canvas.SetTop(insertionLine, surfaceTop);
			insertionLine.Width = Math.Max(1, lineWidth * viewport.Scale);
			insertionLine.Height = Math.Max(1, lineHeight * viewport.Scale);
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
			if (selectedComponent == null || e.Canceled || String.IsNullOrEmpty(selectedComponent.Parent) || Math.Abs(delta) < 1)
				return;
			var (targetIndex, _) = ComputeReorderTarget(vertical: true, delta);
			ReorderRequested?.Invoke(this, new RemoteReorderRequestedEventArgs(selectedComponent.Name, targetIndex));
		}

		void UpdateAdorners()
		{
			// Selection just changed (a fresh call to Show/a new click) - any in-progress F2 edit
			// belongs to the PREVIOUS selection and must not be left dangling over the new one.
			if (renaming) CancelRename();
			insertionLine.Visibility = Visibility.Collapsed;
			AutomationProperties.SetName(this, String.IsNullOrEmpty(selectedComponent?.AccessibleName)
				? selectedComponent?.Name ?? "WinForms designer" : selectedComponent.AccessibleName);
			AutomationProperties.SetHelpText(this, selectedComponent?.AccessibleDescription ?? "");
			// A component that has a tray entry but NO place on the surface (Timer, ImageList,
			// ToolTip, ContextMenuStrip, the dialogs) reports no meaningful bounds, so drawing the
			// selection outline, the move/resize thumbs or the smart-tag glyph for it would put
			// them at (0,0) over whatever happens to sit in the form's top-left corner. Reflect
			// the selection in the tray instead and keep the surface clean.
			//
			// Being a tray component is NOT enough to suppress the adorners: every
			// MenuStrip/ToolStrip/StatusStrip gets a tray entry too (DocumentDesigner adds
			// anything with a ToolStripDesigner) while still being laid out on the surface, and
			// those must keep their outline, thumbs, smart tag and insert-item chevron. A missing
			// Parent is what actually distinguishes "tray only" here.
			if (selectedComponent?.IsTrayComponent == true && String.IsNullOrEmpty(selectedComponent.Parent)) {
				adornerLayer.ClearSelection();
				moveThumb.Visibility = reorderThumb.Visibility = popupReorderThumb.Visibility = resizeHitTarget.Visibility = resizeThumb.Visibility =
					smartTagChevron.Visibility = toolStripInsertChevron.Visibility = Visibility.Collapsed;
				toolStripHost = null;
				RefreshTrayHighlight();
				return;
			}
			RefreshTrayHighlight();
			var visible = selectedComponent != null;
			var isRoot = visible && String.IsNullOrEmpty(selectedComponent.Parent);
			// The move/resize thumbs exist to drive design/set-bounds, which only ever operates on
			// a real Control ("host.Container.Components[id] as Control"). A selected
			// ToolStripItem (a menu item, a toolbar button) is never a Control, so showing these
			// for one and dragging it used to throw "Control not found" straight out of the child.
			var canResize = visible && selectedComponent.IsControl;
			resizeHitTarget.Visibility = resizeThumb.Visibility = canResize ? Visibility.Visible : Visibility.Collapsed;
			moveThumb.Visibility = canResize && !isRoot ? Visibility.Visible : Visibility.Collapsed;
			// A selected ToolStripItem with a Parent (i.e. not tray-only) can be dragged to reorder
			// among its siblings - design/reorder-toolstrip-item, index-based rather than
			// pixel-based, so it needs no "Control not found" guard the move/resize thumbs do.
			// Which of the two thumbs applies depends on whether the item is stacked vertically
			// inside an open popup or laid out horizontally on a root strip.
			var reorderable = visible && !selectedComponent.IsControl && !isRoot;
			var inPopup = reorderable && SelectionIsInsideOpenPopup();
			reorderThumb.Visibility = reorderable && !inPopup ? Visibility.Visible : Visibility.Collapsed;
			popupReorderThumb.Visibility = inPopup ? Visibility.Visible : Visibility.Collapsed;
			// The smart tag applies to (almost) any selected component - VS shows it even when
			// a given component's own action-list turns out empty, so showing it eagerly here
			// and only discovering "no actions" once the popup's own list-smart-tag-actions RPC
			// comes back empty matches VS's own behavior closer than hiding it up front would
			// (which would need a synchronous, per-selection RPC round-trip this control's
			// SelectionChanged path does not otherwise make).
			smartTagChevron.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
			// VS keeps the "insert new item" glyph visible next to the strip's last item even
			// while a child ToolStripItem (not the strip itself) is selected - resolve the owning
			// strip so selecting e.g. a StatusStrip's ProgressBar still shows the glyph.
			toolStripHost = !visible ? null
				: IsToolStripHost(selectedComponent.Type) ? selectedComponent
				: state?.Components?.FirstOrDefault(item => item.Name == selectedComponent.Parent) is { } parent
					&& IsToolStripHost(parent.Type) ? parent : null;
			// No client-drawn insertion affordance any more. Pushing the selection into the
			// child's real ISelectionService (DesignerHostService.SetSelection) makes the genuine
			// ToolStripTemplateNode visible - the "Type Here" cell for a MenuStrip, the split
			// button for ToolStrip/StatusStrip - and it renders straight into the frame as a real
			// item of the strip, together with the expanded dropdown and that level's own node.
			// Keeping these overlays would simply double-draw on top of the real thing.
			toolStripInsertChevron.Visibility = Visibility.Collapsed;
			typeHereCell.Visibility = Visibility.Collapsed;
			if (typeHereEditing)
				CommitTypeHere(TypeHereCommit.Cancel);
			if (!visible) {
				adornerLayer.ClearSelection();
				return;
			}
			var locked = lockedComponentNames.Contains(selectedComponent.Name);
			moveThumb.IsEnabled = reorderThumb.IsEnabled = popupReorderThumb.IsEnabled = !locked;
			resizeHitTarget.IsEnabled = resizeThumb.IsEnabled = isRoot || !locked;
			adornerLayer.SelectionStroke = locked ? Brushes.DarkOrange : SelectionBrush;
			dragX = selectedComponent.SurfaceX;
			dragY = selectedComponent.SurfaceY;
			selectedLocalX = selectedComponent.X;
			selectedLocalY = selectedComponent.Y;
			dragWidth = selectedComponent.Width;
			dragHeight = selectedComponent.Height;
			PositionAdorners();
			// Deliberately does NOT call ScrollResizeHandleIntoView() here: that used to force
			// the canvas to jump/scroll to the selected component's resize handle on every plain
			// selection (most jarring for the root Form, whose handle sits at its bottom-right
			// corner - selecting it could scroll far away from wherever the user was looking).
			// The handle-visibility problem this originally guarded against (a resize can't start
			// if the handle is hidden behind a scrollbar) only actually matters once a resize
			// drag is already in progress, where the OTHER two call sites (OnPreviewMouseMove/
			// OnResizeDragDelta) still keep the handle in view without touching plain selection.
		}

		void PositionAdorners()
		{
			renderedSelection = new Rect(dragX, dragY, dragWidth, dragHeight);
			adornerLayer.ShowSelection(renderedSelection, viewport);
			// Convert both design corners to surface coordinates so the move/resize handles
			// track the (possibly zoomed) design rect exactly.
			var (left, top) = viewport.DesignToSurface(dragX, dragY);
			var (right, bottom) = viewport.DesignToSurface(dragX + dragWidth, dragY + dragHeight);
			Canvas.SetLeft(moveThumb, left);
			Canvas.SetTop(moveThumb, top);
			moveThumb.Width = Math.Max(1, right - left);
			moveThumb.Height = Math.Max(1, bottom - top);
			Canvas.SetLeft(reorderThumb, left);
			Canvas.SetTop(reorderThumb, top);
			reorderThumb.Width = Math.Max(1, right - left);
			reorderThumb.Height = Math.Max(1, bottom - top);
			// Same rect as reorderThumb (a popup item's own SurfaceX/Y/Width/Height are already in
			// the same absolute basis - see OnPopupReorderDragCompleted's own note), but a higher
			// z-index: it must sit above the popup's own Image overlay (200) and its Type Here
			// editor (201) to remain draggable once a popup is open.
			Canvas.SetLeft(popupReorderThumb, left);
			Canvas.SetTop(popupReorderThumb, top);
			popupReorderThumb.Width = Math.Max(1, right - left);
			popupReorderThumb.Height = Math.Max(1, bottom - top);
			Panel.SetZIndex(popupReorderThumb, 202);
			Canvas.SetLeft(renameEditor, left);
			Canvas.SetTop(renameEditor, top);
			renameEditor.Width = Math.Max(1, right - left);
			renameEditor.Height = Math.Max(1, bottom - top);
			Panel.SetZIndex(renameEditor, 102);
			Canvas.SetLeft(resizeThumb, right - resizeThumb.Width / 2);
			Canvas.SetTop(resizeThumb, bottom - resizeThumb.Height / 2);
			Canvas.SetLeft(resizeHitTarget, right - resizeHitTarget.Width / 2);
			Canvas.SetTop(resizeHitTarget, bottom - resizeHitTarget.Height / 2);
			Panel.SetZIndex(resizeHitTarget, 99);
			Panel.SetZIndex(resizeThumb, 100);
			// Smart tag: anchored at the selection's top-right corner, offset half outside the
			// bounds - the same corner/offset VS's own smart-tag glyph uses.
			Canvas.SetLeft(smartTagChevron, right - smartTagChevron.Width / 2);
			Canvas.SetTop(smartTagChevron, top - smartTagChevron.Height / 2);
			Panel.SetZIndex(smartTagChevron, 101);
			// ToolStrip insert chevron: past the RIGHTMOST EXISTING ITEM, not the strip's own
			// right edge - a Dock=Top strip is normally as wide as its parent, so anchoring to
			// the control's own bounds would place the glyph off past the form's edge, outside
			// the visible/rendered area, for any strip that isn't already full of items. Uses the
			// HOST strip's own bounds/items - not the current selection's - since selecting a
			// child ToolStripItem (e.g. a StatusStrip's ProgressBar) still shows this glyph
			// anchored to its owning strip, matching real VS behavior.
			// Unlike smartTagChevron/resizeThumb (fixed-size adorner HANDLES, deliberately
			// screen-constant regardless of zoom - matching real drag-handle conventions), this
			// glyph is meant to read as a real ToolStripItem drawn ON the strip's own bitmap, so
			// it must scale with the strip the way real VS's actual sited ToolStripSplitButton
			// item naturally does when its DesignSurface bitmap is zoomed. A RenderTransform
			// leaves the logical Width/Height (used below for centering) unchanged, so the
			// effective on-screen size is computed separately as scaledWidth/scaledHeight.
			var scale = Math.Max(0.1, viewport.Scale);
			toolStripInsertChevron.RenderTransformOrigin = new Point(0, 0);
			toolStripInsertChevron.RenderTransform = new ScaleTransform(scale, scale);
			var scaledHeight = toolStripInsertChevron.Height * scale;
			var insertLeft = right;
			var insertTop = top + (bottom - top - scaledHeight) / 2;
			if (toolStripHost != null) {
				var (hostLeft, hostTop) = viewport.DesignToSurface(toolStripHost.SurfaceX, toolStripHost.SurfaceY);
				var (_, hostBottom) = viewport.DesignToSurface(toolStripHost.SurfaceX, toolStripHost.SurfaceY + toolStripHost.Height);
				var lastItem = state?.Components?.Where(item => item.Parent == toolStripHost.Name)
					.OrderByDescending(item => item.SurfaceX + item.Width).FirstOrDefault();
				if (lastItem != null) {
					var (itemRight, itemTop) = viewport.DesignToSurface(lastItem.SurfaceX + lastItem.Width, lastItem.SurfaceY);
					var (_, itemBottom) = viewport.DesignToSurface(lastItem.SurfaceX, lastItem.SurfaceY + lastItem.Height);
					insertLeft = itemRight;
					insertTop = itemTop + (itemBottom - itemTop - scaledHeight) / 2;
				} else {
					// No real items yet: sit just past the strip's own left edge instead of its
					// (typically much wider) right edge.
					insertLeft = hostLeft + 4;
					insertTop = hostTop + (hostBottom - hostTop - scaledHeight) / 2;
				}
			}
			Canvas.SetLeft(toolStripInsertChevron, insertLeft + 2);
			Canvas.SetTop(toolStripInsertChevron, insertTop);
			Panel.SetZIndex(toolStripInsertChevron, 101);
			// The "Type Here" cell occupies the same slot (the template node is the strip's last
			// item either way), just sized like a menu cell rather than a square button.
			Canvas.SetLeft(typeHereCell, insertLeft + 2);
			Canvas.SetTop(typeHereCell, insertTop);
			typeHereCell.MinHeight = toolStripInsertChevron.Height;
			Panel.SetZIndex(typeHereCell, 101);
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
			if (e.Data.GetData(typeof(ToolboxItem)) is not ToolboxItem item || String.IsNullOrEmpty(item.TypeName))
				return;
			// GetPosition on the (possibly zoomed) frame image yields surface pixels; the
			// child's hit-testing and the drop position are design-space.
			var point = e.GetPosition(framePresenter.Visual);
			var designX = point.X / viewport.Scale;
			var designY = point.Y / viewport.Scale;
			var hit = await client.HitTestAsync(version, (int)designX, (int)designY, CancellationToken.None);
			var target = state.Components.FirstOrDefault(component => component.Name == hit.ComponentName);
			if (target != null && !IsContainer(target.Type))
				target = state.Components.FirstOrDefault(component => component.Name == target.Parent);
			target ??= state.Components.FirstOrDefault(component => String.IsNullOrEmpty(component.Parent));
			if (target != null)
				ToolboxDrop?.Invoke(this, new RemoteToolboxDropEventArgs(item.TypeName, target.Name,
					(int)designX - target.SurfaceX, (int)designY - target.SurfaceY));
			e.Handled = true;
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

		public void Reposition(DesignViewport viewport, int popupX, int popupY)
		{
			var (left, top) = viewport.DesignToSurface(popupX + Bounds.X, popupY + Bounds.Y);
			Canvas.SetLeft(Cell, left);
			Canvas.SetTop(Cell, top);
			Cell.Width = Math.Max(1, Bounds.Width * viewport.Scale);
			Cell.Height = Math.Max(1, Bounds.Height * viewport.Scale);
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
			var strip = ownerInfo;
			var guard = 0;
			while (strip != null && !strip.IsControl && guard++ < 32)
				strip = owner.state?.Components?.FirstOrDefault(item => item.Name == strip.Parent);
			if (strip == null)
				return;
			var typeName = text == "-" && ownerInfo.NewItemTypeNames.Contains("System.Windows.Forms.ToolStripSeparator")
				? "System.Windows.Forms.ToolStripSeparator"
				: ownerInfo.NewItemTypeNames.FirstOrDefault();
			if (String.IsNullOrEmpty(typeName))
				return;
			owner.RaiseToolStripTypeHereCommitted(
				new RemoteToolStripTypeHereEventArgs(strip.Name, typeName, text, ownerElementId));
			if (keepEditing)
				owner.Dispatcher.BeginInvoke(new Action(Begin), System.Windows.Threading.DispatcherPriority.Background);
		}
	}
}
