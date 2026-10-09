// Common out-of-process designer protocol (DDP), shared by the WinForms, WinUI/Uno and
// (once isolated) WPF design hosts. Wire compatibility: JSON field names are the contract,
// not CLR type identity - a child host only has to produce/consume these field names.
//
// The DTOs are deliberately a superset of the shipped WinForms and WinUI protocols:
//   - DesignerSessionState carries both the flat component list (WinForms) and the element
//     tree (WinUI/WPF); each child fills the shape it produces.
//   - DesignerRenderFrame carries both PngBase64 (WinForms) and Data/RenderMs (WinUI).
//   - DesignerHitTestResult carries both component-name (WinForms) and chain/pick-path
//     (WinUI) fields.
// This keeps both existing child processes wire-compatible while the IDE side converges on
// one contract. See doc/technotes/designer-common.md.

using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>Protocol version. Bump on incompatible wire changes; the handshake rejects
	/// mismatched peers and reports both supported ranges.</summary>
	public static class DesignerProtocol
	{
		public const int Version = 7;
	}

	/// <summary>Response to the <c>initialize</c> handshake.</summary>
	public sealed class HostHandshake
	{
		public int ProtocolVersion { get; set; }
		public string Runtime { get; set; } = "";
		public int ProcessId { get; set; }
		/// <summary>Session identity minted once per child lifetime; echoed back on every
		/// subsequent call so the host can detect a stale/reused child.</summary>
		public string SessionId { get; set; } = "";
		/// <summary>Optional runtime-specific capabilities discovered during the authenticated
		/// handshake. Keeping them inside the common handshake preserves one initialize response
		/// shape for every child host while still allowing a backend to expose its toolbox catalog.
		/// </summary>
		public DesignerCapabilities Capabilities { get; set; } = new DesignerCapabilities();
	}

	/// <summary>Host-authoritative document snapshot. The child never writes files.</summary>
	public sealed class DesignerDocumentSnapshot
	{
		/// <summary>Identifies the child process (host-chosen); stable for the child's life.</summary>
		public string SessionId { get; set; } = "";
		/// <summary>Identifies the document within the session; stable for the document's life.</summary>
		public string DocumentId { get; set; } = "";
		/// <summary>Per-document reload counter (the DDP "Generation"). All element IDs,
		/// selection, undo state and cached model tokens are invalid across a change.</summary>
		public long Version { get; set; }
		public string ProjectFileName { get; set; } = "";
		public string TargetFramework { get; set; } = "";
		public string Architecture { get; set; } = "";
		public string ProjectAssemblyPath { get; set; } = "";
		/// <summary>Absolute paths of the project's resolved reference assemblies (MSBuild's
		/// ResolveAssemblyReferences output), so a child can load referenced control types
		/// without reflecting into the IDE's project system. Empty for stock-controls-only
		/// documents.</summary>
		public List<string> ReferencedAssemblyPaths { get; set; } = new List<string>();
		public string PrimaryFileName { get; set; } = "";
		public string DesignerFileName { get; set; } = "";
		/// <summary>Roslyn language name: "CSharp" or "VisualBasic" (defaults to CSharp). XAML backends leave it empty.</summary>
		public string Language { get; set; } = "CSharp";
		/// <summary>"Enabled" or "Disabled" (safe mode without project code).</summary>
		public string ProjectCodeMode { get; set; } = "Enabled";
		public List<DesignerSourceFileSnapshot> Files { get; set; } = new List<DesignerSourceFileSnapshot>();
	}

	/// <summary>Common <c>session/open</c> and <c>session/update</c> request. The source model
	/// always travels as a complete host-owned snapshot; a frame-based backend may additionally
	/// receive the presenter viewport without turning it into document state.</summary>
	public sealed class DesignerDocumentRequest
	{
		public DesignerDocumentSnapshot Snapshot { get; set; } = new DesignerDocumentSnapshot();
		public DesignerViewport? Viewport { get; set; }
	}

	/// <summary>Presenter dimensions used only while materializing a rendered design frame.</summary>
	public sealed class DesignerViewport
	{
		public double Width { get; set; }
		public double Height { get; set; }
		public double Dpi { get; set; } = 1.0;
	}

	/// <summary>One target rectangle in an atomic <c>design/set-bounds-batch</c> mutation.
	/// Backends that support group placement consume the entire list as one undoable operation;
	/// callers must not emulate it with a sequence of single-element mutations.</summary>
	public sealed class DesignerBoundsEdit
	{
		public string ElementId { get; set; } = "";
		public double X { get; set; }
		public double Y { get; set; }
		public double Width { get; set; }
		public double Height { get; set; }
	}

	/// <summary>One file inside a snapshot/edit set. Text is the source when textual,
	/// Base64 carries binary content (.resx etc.).</summary>
	public sealed class DesignerSourceFileSnapshot
	{
		public string FileName { get; set; } = "";
		/// <summary>"Source", "Designer", "Resource" or "AppXaml".</summary>
		public string Kind { get; set; } = "Source";
		public string Text { get; set; } = "";
		public string Base64 { get; set; } = "";
	}

	/// <summary>Current state of the design session after open/update/mutation.</summary>
	public sealed class DesignerSessionState
	{
		public string SessionId { get; set; } = "";
		public string DocumentId { get; set; } = "";
		public long Version { get; set; }
		public bool Accepted { get; set; }
		public string Error { get; set; } = "";
		public string RootType { get; set; } = "";
		public int ComponentCount { get; set; }
		/// <summary>Flat component snapshot (WinForms shape).</summary>
		public List<DesignerComponentInfo> Components { get; set; } = new List<DesignerComponentInfo>();
		/// <summary>Element tree snapshot (WinUI/WPF shape).</summary>
		public DesignerElementNode? Tree { get; set; }
		/// <summary>Non-visual design objects exposed by the backend. Unlike the legacy
		/// WinForms-only <see cref="DesignerComponentInfo.IsTrayComponent"/> flag, this is the
		/// common DDP representation consumed by every designer front end. Items are selectable
		/// by <see cref="DesignerTrayItem.Id"/>, but have no surface bounds.</summary>
		public List<DesignerTrayItem> TrayComponents { get; set; } = new List<DesignerTrayItem>();
		public List<DesignerDiagnostic> Diagnostics { get; set; } = new List<DesignerDiagnostic>();
		public DesignerRenderFrame? Render { get; set; }
		/// <summary>Every currently-expanded floating surface (a ToolStripDropDown the real
		/// designer is holding open - a MenuStrip/ContextMenuStrip dropdown or a nested submenu),
		/// captured as ITS OWN bitmap rather than baked into <see cref="Render"/>. The client hosts
		/// each as an independent overlay positioned at X/Y (same surface-coordinate basis as
		/// DesignerComponentInfo.SurfaceX/Y), so it can receive pointer/keyboard input directly -
		/// hit-testing and dragging inside it never has to reverse through the root form's own
		/// coordinate space, and it is not clipped or occluded by the root frame's own adorners.</summary>
		public List<DesignerPopupFrame> Popups { get; set; } = new List<DesignerPopupFrame>();
		/// <summary>History availability reported by the document authority after every mutation.</summary>
		public bool CanUndo { get; set; }
		public bool CanRedo { get; set; }
		/// <summary>The id (tree path) of the element a <c>design/add-element</c> call just
		/// created, valid only on that RPC's own response - null for every other response
		/// (including a later <c>session/update</c>/<c>design/set-bounds</c> etc., where it would
		/// be stale). Lets a caller select the just-dropped element without needing to invent a
		/// name for it first (WinForms/WinUI instead have the caller supply a name up front and
		/// look the result up by name afterward - not an option here, since a freshly toolbox-
		/// dropped WPF element deliberately has no <c>x:Name</c> at all).</summary>
		public string? CreatedElementId { get; set; }
		/// <summary>The element id a <c>design/hit-test-popup</c> call just selected inside its
		/// popup (see <see cref="Popups"/>), valid only on that RPC's own response - null for
		/// every other response, and also null when the click did not land on an item. The
		/// client's own selection state is tracked entirely client-side (SelectedComponentName),
		/// so without this the child's real ISelectionService changing selection inside a popup -
		/// which DOES happen, correctly, per DesignerHostService.HitTestPopupAndSelect - would
		/// have no way to tell the client which component to adopt as the new selection.</summary>
		public string? PopupHitElementId { get; set; }
		/// <summary>Whether this session's project embeds any design-time theme (WPF shape only -
		/// WinForms/WinUI have their own real theme mechanisms and never need this). True when
		/// <see cref="DesignThemes"/> is non-empty.</summary>
		public bool SupportsThemeSwitch { get; set; }

		/// <summary>The design-time theme names the project embeds (WPF shape only): the file
		/// names (without extension) of the embedded <c>themes/*.xaml</c> resources. Drives the
		/// designer's theme combo, mirroring how the WinUI shape enumerates the app's
		/// ThemeDictionaries keys. Empty when no theme is embedded.</summary>
		public string[] DesignThemes { get; set; } = Array.Empty<string>();

		/// <summary>The document's <c>VisualStateManager.VisualStateGroups</c>, so the designer can
		/// offer them for preview. Empty for the overwhelming majority of documents, which is what
		/// collapses the toolbar's states panel entirely.</summary>
		public List<DesignerVisualStateGroup> VisualStateGroups { get; set; } = new List<DesignerVisualStateGroup>();
	}

	/// <summary>
	/// One <c>VisualStateGroup</c> from the designed document.
	///
	/// Reported per GROUP rather than as one flat list of states because groups are orthogonal: a
	/// page can be in <c>WideLayout</c> AND <c>NoResultsFound</c> at the same time (WinUI-Gallery's
	/// SearchResultsPage is exactly that), so a single "current state" for the whole document would
	/// be wrong by construction.
	/// </summary>
	public sealed class DesignerVisualStateGroup
	{
		public string Name { get; set; } = "";

		/// <summary>State names in declaration order.</summary>
		public List<string> States { get; set; } = new List<string>();

		/// <summary>The state the designer is currently forcing, or null when the document is left
		/// in whatever state it naturally loads in.</summary>
		public string? CurrentState { get; set; }
	}

	/// <summary>One Grid row/column's current pixel geometry (<c>design/query-grid-guides</c>),
	/// for drawing draggable divider guides over the rendered frame - the WPF shape's equivalent
	/// of the Uno/WinUI designer's own Grid-guide overlay, which reads offsets straight from the
	/// live XAML text editor instead (WPF has a real DesignItem/Grid model so this crosses the
	/// wire as measured layout instead).</summary>
	public sealed class DesignerGridTrackInfo
	{
		/// <summary>Cumulative pixel offset from the Grid's own origin (WPF's
		/// <c>RowDefinition.Offset</c>/<c>ColumnDefinition.Offset</c> - already cumulative, no
		/// summation needed).</summary>
		public double Offset { get; set; }
		/// <summary>The row's/column's current rendered size (<c>ActualHeight</c>/<c>ActualWidth</c>).</summary>
		public double Size { get; set; }
	}

	/// <summary>Response to <c>design/query-grid-guides</c>: a Grid element's current row/column
	/// track geometry, or <see cref="Accepted"/>=false with <see cref="Error"/> when the element
	/// isn't a Grid (or isn't found).</summary>
	public sealed class DesignerGridGuides
	{
		public bool Accepted { get; set; }
		public string Error { get; set; } = "";
		public List<DesignerGridTrackInfo> RowTracks { get; set; } = new List<DesignerGridTrackInfo>();
		public List<DesignerGridTrackInfo> ColumnTracks { get; set; } = new List<DesignerGridTrackInfo>();
	}

	/// <summary>Measured edge distances that explain an element's current placement.  This is
	/// descriptive, not a promise that every host can mutate every edge: a client must only offer
	/// a margin/position affordance when this object is present and recognises <see cref="Kind"/>.
	/// Values are in design units and are relative to the immediate layout container.</summary>
	public sealed class DesignerLayoutInsets
	{
		/// <summary><c>Margin</c> for a Grid child, <c>CanvasPosition</c> for a Canvas child.</summary>
		public string Kind { get; set; } = "";
		public double Left { get; set; }
		public double Top { get; set; }
		public double Right { get; set; }
		public double Bottom { get; set; }
	}

	/// <summary>A binding declared on a source-backed element. Hosts report only states they can
	/// establish without evaluating user code; <c>Declared</c> is deliberately distinct from a
	/// claim that the binding produced a value.</summary>
	public sealed class DesignerBindingInfo
	{
		public string Property { get; set; } = "";
		public string Status { get; set; } = "Declared";
	}

	/// <summary>Neutral element tree node. Id is generation-scoped; only Id crosses back
	/// into the child - never a runtime object or System.Type.</summary>
	public sealed class DesignerElementNode
	{
		public string Id { get; set; } = "";
		public string? Name { get; set; }
		public string Type { get; set; } = "";
		public double X { get; set; }
		public double Y { get; set; }
		public double Width { get; set; }
		public double Height { get; set; }
		/// <summary>The measured attached z-index of this node, or null when the node has no parent
		/// or the host cannot query a z-order. A value of zero is meaningful: it is the framework
		/// default after the host has actually measured the attached property.</summary>
		public int? ZIndex { get; set; }
		/// <summary>The measured baseline offset inside this node for a text-capable element, or
		/// null when the runtime exposes no meaningful text baseline. It is local to the node's
		/// own bounds (not a root-relative Y coordinate).</summary>
		public double? BaselineOffset { get; set; }
		/// <summary>Measured row/column geometry when this node is a Grid and the host can inspect
		/// its live layout. Null deliberately distinguishes non-Grid/unknown hosts from a Grid with
		/// zero explicitly declared tracks.</summary>
		public DesignerGridGuides? GridTracks { get; set; }
		/// <summary>Measured edge distances published only when the host knows their layout
		/// semantics. Null deliberately means unknown/not applicable rather than four zero margins.</summary>
		public DesignerLayoutInsets? LayoutInsets { get; set; }
		/// <summary>Bindings the host found in this source element's design model. Empty means none
		/// were observed; it must not be interpreted as a runtime-success report.</summary>
		public List<DesignerBindingInfo> Bindings { get; set; } = new List<DesignerBindingInfo>();
		/// <summary>How this node's immediate parent lays it out. This is deliberately an
		/// observable layout fact, not a claim that every node can be freely moved: clients must
		/// retain <c>Unknown</c> when a host cannot identify the parent container. Values are from
		/// <see cref="DesignerLayoutMode"/>.</summary>
		public string LayoutMode { get; set; } = DesignerLayoutMode.Unknown;
		/// <summary>Child-index path from the root (e.g. "0,2,1"), for mapping a pick back to the source.</summary>
		public string Path { get; set; } = "";
		/// <summary>False for template parts / non-source nodes.</summary>
		public bool IsDesignable { get; set; } = true;
		/// <summary>True when the host can prove this visible node was generated by a control
		/// template rather than reached from the document's markup object graph. Template parts are
		/// not source-editable and must not be offered by source-oriented Outline/Properties tools.</summary>
		public bool IsTemplatePart { get; set; }
		/// <summary>Whether this element is actually on screen in the rendered frame right now -
		/// false for anything inside a collapsed container or a non-selected TabItem/TabPage.
		///
		/// Clients MUST skip these when drawing overlays keyed off <see cref="X"/>/<see cref="Y"/>
		/// (tab-order badges, outlines, name tags). Those coordinates still describe where the
		/// element WOULD sit, and every tab of a tab control occupies the SAME rect, so an overlay
		/// drawn for a hidden tab's child lands exactly on top of whichever sibling tab IS showing.
		/// The WinForms surface shipped that bug in its always-on outlines and name tags, where it
		/// read as "the designer is rendering the wrong tab's content" and cost hours to diagnose -
		/// the bitmap was correct all along, the overlays were not. See the equivalent
		/// DesignerComponentInfo.IsVisible and doc/technotes/winforms-designer.md.</summary>
		public bool IsVisible { get; set; } = true;
		/// <summary>True when this source-backed object has no design-surface representation and is
		/// additionally published in <see cref="DesignerSessionState.TrayComponents"/>. This stays
		/// on the tree node so an Outline can describe the object in its structural context; clients
		/// must not infer tray membership from visibility or zero-sized bounds.</summary>
		public bool IsTrayComponent { get; set; }
		public List<DesignerElementNode> Children { get; set; } = new List<DesignerElementNode>();
		/// <summary>Optional per-node property list for the Properties pad, using the same
		/// <see cref="DesignerPropertyInfo"/> shape <see cref="DesignerComponentInfo"/> already
		/// uses (designer-common.md "Property and event values"). Empty for backends whose
		/// Properties pad is instead driven off the host-owned XAML document directly
		/// (WinUI/Uno's <c>WinUIXamlElementPropertyAdapter</c>) - this field exists for backends
		/// like WPF whose real property metadata/values only the child (via its DesignItem
		/// reflection) actually knows.</summary>
		public List<DesignerPropertyInfo> Properties { get; set; } = new List<DesignerPropertyInfo>();
		/// <summary>Events/signals supported by this element and their current handler names.</summary>
		public List<DesignerEventInfo> Events { get; set; } = new List<DesignerEventInfo>();
	}

	/// <summary>Portable vocabulary for <see cref="DesignerElementNode.LayoutMode"/>. Hosts infer
	/// only from a known parent container type; an unrecognised type remains <see cref="Unknown"/>
	/// so layout affordances cannot promise a mutation the backend cannot make.</summary>
	public static class DesignerLayoutMode
	{
		public const string Unknown = "Unknown";
		public const string Absolute = "Absolute";
		public const string Grid = "Grid";
		public const string Stack = "Stack";
		public const string Dock = "Dock";
		public const string Wrap = "Wrap";
		public const string Flow = "Flow";
		public const string Content = "Content";

		public static string InferFromContainerType(string? typeName)
		{
			if (string.IsNullOrWhiteSpace(typeName))
				return Unknown;
			var name = typeName.Trim();
			if (name.EndsWith("TableLayoutPanel", StringComparison.OrdinalIgnoreCase)
				|| name.EndsWith("Grid", StringComparison.OrdinalIgnoreCase)
				|| name.EndsWith("UniformGrid", StringComparison.OrdinalIgnoreCase))
				return Grid;
			if (name.EndsWith("StackPanel", StringComparison.OrdinalIgnoreCase)
				|| name.EndsWith("Box", StringComparison.OrdinalIgnoreCase))
				return Stack;
			if (name.EndsWith("DockPanel", StringComparison.OrdinalIgnoreCase))
				return Dock;
			if (name.EndsWith("WrapPanel", StringComparison.OrdinalIgnoreCase))
				return Wrap;
			if (name.EndsWith("FlowLayoutPanel", StringComparison.OrdinalIgnoreCase)
				|| name.EndsWith("ToolStrip", StringComparison.OrdinalIgnoreCase))
				return Flow;
			if (name.EndsWith("Canvas", StringComparison.OrdinalIgnoreCase)
				|| name.EndsWith("Form", StringComparison.OrdinalIgnoreCase)
				|| name.EndsWith("Panel", StringComparison.OrdinalIgnoreCase))
				return Absolute;
			if (name.EndsWith("ContentControl", StringComparison.OrdinalIgnoreCase)
				|| name.EndsWith("UserControl", StringComparison.OrdinalIgnoreCase)
				|| name.EndsWith("Border", StringComparison.OrdinalIgnoreCase)
				|| name.EndsWith("ContentPresenter", StringComparison.OrdinalIgnoreCase))
				return Content;
			return Unknown;
		}
	}

	/// <summary>A non-visual object presented below the design surface. The identifier uses the
	/// backend's normal selection identity (tree path for XAML designers, component name for
	/// WinForms), so selecting an item does not need a tray-specific RPC.</summary>
	public sealed class DesignerTrayItem
	{
		public string Id { get; set; } = "";
		public string Name { get; set; } = "";
		public string Type { get; set; } = "";
	}

	/// <summary>One rendered frame. PngBase64 (WinForms) or Data/RenderMs (WinUI) may be
	/// filled; the host presents whichever the child produced.</summary>
	public sealed class DesignerRenderFrame
	{
		public long Sequence { get; set; }
		public int Width { get; set; }
		public int Height { get; set; }
		public double Dpi { get; set; } = 1;
		public string PngBase64 { get; set; } = "";
		/// <summary>Deflate-compressed BGRA base64 (WinUI/Uno shape).</summary>
		public string Data { get; set; } = "";
		/// <summary>Render time in milliseconds (rasterize + compress).</summary>
		public double RenderMs { get; set; }
	}

	/// <summary>One floating design-time surface the real designer is holding open (an expanded
	/// ToolStripDropDown), captured as its own bitmap. See DesignerSessionState.Popups.</summary>
	public sealed class DesignerPopupFrame
	{
		/// <summary>A stable id for this popup - the owning ToolStripDropDownItem's element id
		/// (e.g. "fileToolStripMenuItem"), or "" for a strip's own ContextMenuStrip. Lets the
		/// client match this frame to the same WPF overlay across updates instead of recreating
		/// it (which would drop input focus/an in-progress drag).</summary>
		public string OwnerElementId { get; set; } = "";
		/// <summary>Surface-space position (same basis as DesignerComponentInfo.SurfaceX/Y: the
		/// root form's own screen origin, so it composites with everything else without a second
		/// coordinate system).</summary>
		public int X { get; set; }
		public int Y { get; set; }
		public DesignerRenderFrame Render { get; set; } = new DesignerRenderFrame();
		/// <summary>The real "Type Here" template node's own bounds, LOCAL to this popup (i.e.
		/// relative to X/Y above, not to the root form) - null when this dropdown has none (rare,
		/// but real WinForms can decline to create one). The client draws its own real WPF TextBox
		/// over this rect on click: a screenshot-based render pipeline cannot show the real
		/// control's blinking caret or accept keystrokes, so typing happens entirely client-side
		/// and is committed through the existing design/add-toolstrip-item RPC (parentItemId =
		/// this popup's OwnerElementId) rather than by focusing the real template node.</summary>
		public DesignerRectangle? TypeHereBounds { get; set; }
	}

	public struct DesignerRectangle
	{
		public int X { get; set; }
		public int Y { get; set; }
		public int Width { get; set; }
		public int Height { get; set; }
	}

	/// <summary>Flat component snapshot entry (WinForms shape).</summary>
	public sealed class DesignerComponentInfo
	{
		/// <summary>Designer-only insertion node bounds, local to this strip; never serialized as a user item.</summary>
		public DesignerRectangle? ItemInsertionBounds { get; set; }
		public string Name { get; set; } = "";
		public string Type { get; set; } = "";
		public string Parent { get; set; } = "";
		public string Text { get; set; } = "";
		public string AccessibleName { get; set; } = "";
		public string AccessibleDescription { get; set; } = "";
		public string AccessibleRole { get; set; } = "";
		public int X { get; set; }
		public int Y { get; set; }
		public int SurfaceX { get; set; }
		public int SurfaceY { get; set; }
		public int Width { get; set; }
		public int Height { get; set; }
		/// <summary>Whether this component belongs in the designer's component tray - the icon+name
		/// strip below the design surface - rather than on the surface itself. Mirrors the real
		/// WinForms rule (System.Windows.Forms.Design.ComponentTray's CanCreateComponentFromTool
		/// plus CanDisplayComponent): anything that is not a Control, OR is a Control whose
		/// designer is not a ControlDesigner, provided the type is design-time visible. That
		/// second clause is why ContextMenuStrip and PrintPreviewDialog are tray components while
		/// MenuStrip/ToolStrip/StatusStrip stay on the surface.</summary>
		public bool IsTrayComponent { get; set; }
		/// <summary>Whether this component is a real Control - false for a ToolStripItem (a menu
		/// item, a toolbar button) or any other non-visual component. design/set-bounds only
		/// operates on Controls (`host.Container.Components[id] as Control`), so the client must
		/// not show move/resize thumbs - which exist to drive that RPC - for anything this is
		/// false for, or dragging one throws "Control not found".</summary>
		public bool IsControl { get; set; } = true;
		/// <summary>Whether this is a ToolStripItem living inside a dropdown (its OwnerItem is
		/// another item) rather than directly on a strip. Those are drawn by the real designer's
		/// own adorners once the dropdown is expanded, so the client must not overdraw its own
		/// outline/name label on top of the rendered menu text.</summary>
		public bool IsDropDownItem { get; set; }
		/// <summary>Whether this component is actually on screen in the rendered frame right now -
		/// false for anything on a TabPage that is not its TabControl's SelectedTab, inside a
		/// hidden container, or explicitly Visible=false.
		///
		/// The client MUST skip these when drawing its own outlines/name tags and when hit-testing
		/// clicks locally. <see cref="SurfaceX"/>/<see cref="SurfaceY"/> still describe where such a
		/// component WOULD sit, and every TabPage of a TabControl occupies the SAME rect, so an
		/// overlay drawn for a hidden page's child lands exactly on top of whichever sibling page IS
		/// showing. That reads as "the designer rendered the wrong page's content" (it did not - the
		/// bitmap is correct; the phantom overlay is not) and makes a click on what looks like a
		/// control select its enclosing TabPage instead, since the child process's own hit-test
		/// correctly honours visibility and never resolves to a hidden control. Both symptoms were
		/// reported and misdiagnosed as a TabControl RENDERING bug before this flag existed - see
		/// doc/technotes/winforms-designer.md.</summary>
		public bool IsVisible { get; set; } = true;
		/// <summary>For a ToolStrip/MenuStrip/StatusStrip: how new items are added to it in the
		/// real designer, which differs per strip kind (see ToolStripTemplateNode's
		/// SetupNewEditNode). "" for anything that is not a strip.</summary>
		public string ItemInsertionStyle { get; set; } = "";
		/// <summary>For a strip: the item types its template node offers, most-default first -
		/// ToolStripDesignerUtils.GetStandardItemTypes' own order, whose FIRST entry is the type
		/// committed when the user just types a name without picking one.</summary>
		public List<string> NewItemTypeNames { get; set; } = new List<string>();
		public List<DesignerPropertyInfo> Properties { get; set; } = new List<DesignerPropertyInfo>();
		public List<DesignerEventInfo> Events { get; set; } = new List<DesignerEventInfo>();
		/// <summary>For a TabControl only: each tab HEADER's own rect (real
		/// TabControl.GetTabRect(i), one per TabPages[i] in order), in the same absolute surface
		/// basis SurfaceX/Y use. A tab header is not a component of its own - it is painted by the
		/// TabControl itself - so there is no other way for the client to hit-test a click on one
		/// (real VS's TabControlDesigner does this by intercepting WM_LBUTTONDOWN on the real
		/// control directly, which this screenshot-based client cannot do). Empty for anything that
		/// is not a TabControl.</summary>
		public List<DesignerRectangle> TabHeaderBounds { get; set; } = new List<DesignerRectangle>();
	}

	/// <summary>How a strip's template node lets the user add items, mirroring
	/// ToolStripTemplateNode.SetupNewEditNode's own branch: a MenuStrip (and any dropdown) gets an
	/// editable "Type Here" cell, while ToolStrip/StatusStrip/ContextMenuStrip get a split button
	/// with a type-picker dropdown.</summary>
	public static class DesignerItemInsertionStyles
	{
		public const string None = "";
		public const string TypeHere = "TypeHere";
		public const string SplitButton = "SplitButton";
	}

	public sealed class DesignerEventInfo
	{
		public string Name { get; set; } = "";
		public string Category { get; set; } = "";
		public string HandlerTypeName { get; set; } = "";
		public string Handler { get; set; } = "";
	}

	public sealed class DesignerPropertyInfo
	{
		public string Name { get; set; } = "";
		public string DisplayName { get; set; } = "";
		public string Description { get; set; } = "";
		public string Category { get; set; } = "";
		public string TypeName { get; set; } = "";
		public string Value { get; set; } = "";
		/// <summary>Tag for how <see cref="Value"/> is encoded: "Null" | "Boolean" | "String" |
		/// "Number" | "Enum" | "Point" | "Size" | "Rect" | "Thickness" | "Color" | "Brush" |
		/// "Uri" | "Xaml" | "Reference" | "ReadOnly" | "Unsupported" (designer-common.md
		/// "Property and event values"). Defaults to "String" so WinForms/WinUI, whose
		/// properties are all flat-string-representable today, need no change. A WPF backend
		/// may use the other kinds for properties whose value is itself a
		/// nested DesignItem - Binding, Brush, Gradient, Transform and other markup extensions -
		/// which "Xaml"/"Reference" carry as constrained XAML text in the same <see cref="Value"/>
		/// field rather than a runtime object.</summary>
		public string Kind { get; set; } = "String";
		/// <summary>Finite choices for a scalar property, currently used by the workflow
		/// designer for enum-backed custom Activity properties.</summary>
		public List<string> AllowedValues { get; set; } = new();
		public bool IsNull { get; set; }
		public bool IsReadOnly { get; set; }
		public bool ShouldSerialize { get; set; }
		public bool IsEnum { get; set; }
	}

	/// <summary>Versioned edit set returned by session/flush; applied atomically at BaseVersion.</summary>
	public sealed class DesignerEditSet
	{
		public string SessionId { get; set; } = "";
		public string DocumentId { get; set; } = "";
		public long BaseVersion { get; set; }
		public List<DesignerSourceFileSnapshot> Files { get; set; } = new List<DesignerSourceFileSnapshot>();
	}

	/// <summary>Hit-test result. ComponentName/ComponentType (WinForms) or Chain/PickPath
	/// (WinUI) depending on the backend.</summary>
	public sealed class DesignerHitTestResult
	{
		public string ComponentName { get; set; } = "";
		public string ComponentType { get; set; } = "";
		/// <summary>Named elements under the point, innermost first (WinUI shape).</summary>
		public List<string> Chain { get; set; } = new List<string>();
		/// <summary>Tree path of the innermost hit when it has no name (WinUI shape).</summary>
		public string PickPath { get; set; } = "";
		/// <summary>Whether anything was hit at all. Needed because the document ROOT's own
		/// <see cref="PickPath"/> is the empty string (paths are built root-first, see
		/// <c>WpfSurfaceHostService.BuildNode</c>), which is otherwise indistinguishable from
		/// "hit nothing" - so a click on the root used to clear the selection instead of
		/// selecting the root, making the Window/UserControl impossible to select or resize.
		/// Backends that never report a root hit can leave this false and keep using PickPath.</summary>
		public bool Hit { get; set; }
	}

	/// <summary>Runtime capabilities and toolbox catalog (WinUI shape).</summary>
	public sealed class DesignerCapabilities
	{
		public string Runtime { get; set; } = "";
		public string Version { get; set; } = "";
		public string SessionId { get; set; } = "";
		public List<DesignerToolboxItemInfo> Toolbox { get; set; } = new List<DesignerToolboxItemInfo>();
	}

	/// <summary>Where a newly inserted or moved element goes inside a layout-structured container.
	///
	/// A free-form canvas takes a position; a layout container does not, and every framework spells
	/// the alternative differently - GTK's Box inserts at a child index, its Grid at a column/row,
	/// WPF's StackPanel at an index and its Grid at (Row, Column) and RowSpan/ColumnSpan, WinForms'
	/// FlowLayoutPanel at an index and its TableLayoutPanel at a cell. All of that is the same
	/// decision, so it is one protocol shape rather than one argument list per framework.
	///
	/// Exactly one of <see cref="Index"/> / <see cref="Cell"/> / a <see cref="Rect"/> is meaningful,
	/// chosen by the container's own capability, so a backend that does not understand one of them
	/// says so by leaving it null rather than by being handed an argument it must ignore.
	/// </summary>
	public sealed class DesignerDropTarget
	{
		/// <summary>The container the element goes into, as a tree path. Null means the surface root.</summary>
		public string ContainerId { get; set; } = "";

		/// <summary>Insertion index among the container's children (a Box, StackPanel,
		/// FlowLayoutPanel, ItemsRepeater). Null when the container is not index-addressed, or when
		/// the caller only knows the point and wants the backend to decide.</summary>
		public int? Index { get; set; }

		/// <summary>The grid cell to occupy: column, row and their spans (a Grid, TableLayoutPanel).
		/// Null when the container is not grid-addressed. Spans default to 1.</summary>
		public DesignerGridCell? Cell { get; set; }

		/// <summary>A free-form position in design coordinates, for a Canvas or any container whose
		/// children are positioned rather than ordered. Null when the container is layout-structured
		/// and <see cref="Index"/> or <see cref="Cell"/> carries the intent instead.</summary>
		public double[] Rect { get; set; }

		/// <summary>How the container arranges its children, as reported by the host. Lets a client
		/// pick the right drop shape without hardcoding a per-framework type list, and lets the host
		/// say "this one is a box, ask me for an index" rather than rejecting a grid cell.</summary>
		public string Arrangement { get; set; } = "";

		/// <summary>Where a caret or highlight should be drawn to preview this drop: the gap between
		/// two children, the outline of one grid cell, or the element's own rect. Null when the host
		/// has nothing to draw, in which case a client should just drop without a preview.</summary>
		public double[] Indicator { get; set; }
	}

	/// <summary>A cell in a grid-addressed container.</summary>
	public sealed class DesignerGridCell
	{
		public int Column { get; set; }
		public int Row { get; set; }
		public int ColumnSpan { get; set; } = 1;
		public int RowSpan { get; set; } = 1;
	}

	/// <summary>How a container arranges its children. Reported by the host rather than inferred by
	/// the client from a type list, because the type list is per-framework and this is not.</summary>
	public static class DesignerArrangements
	{
		/// <summary>Children are positioned freely: the drop needs a rect.</summary>
		public const string Freeform = "Freeform";

		/// <summary>Children are stacked along one axis: the drop needs an index.</summary>
		public const string Stack = "Stack";

		/// <summary>Children occupy grid cells: the drop needs a cell.</summary>
		public const string Grid = "Grid";

		/// <summary>Children are pages of a single selected slot (a TabControl, Notebook): the drop
		/// needs an index and there is no position to preview between siblings.</summary>
		public const string Pages = "Pages";
	}

	public sealed class DesignerToolboxItemInfo
	{
		public string Name { get; set; } = "";
		public string DisplayName { get; set; } = "";
		public string Category { get; set; } = "";
		public string Template { get; set; } = "";
		public string XamlNamespace { get; set; } = "";
		public string TypeName { get; set; } = "";
	}

	/// <summary>Workflow-level argument projection. It deliberately carries no CoreWF runtime
	/// object so an out-of-process workflow host can expose its ActivityBuilder metadata safely.</summary>
	public sealed class WorkflowArgumentInfo
	{
		public string Name { get; set; } = "";
		public string TypeName { get; set; } = "Object";
		public string Direction { get; set; } = "In";
		public string DefaultValue { get; set; } = "";
	}

	/// <summary>Workflow variable projection for an activity scope. Like arguments, this
	/// deliberately remains transport-only and never leaks CoreWF types to the addin.</summary>
	public sealed class WorkflowVariableInfo
	{
		public string Name { get; set; } = "";
		public string TypeName { get; set; } = "Object";
		public string Scope { get; set; } = "Root activity";
		/// <summary>Structural activity path for mutations (empty string is the root).</summary>
		public string ScopeId { get; set; } = "";
		public string DefaultValue { get; set; } = "";
	}

	/// <summary>Cross-process result of executing the current CoreWF document.</summary>
	public sealed class WorkflowRunResult
	{
		public bool Succeeded { get; set; }
		public string Message { get; set; } = "";
		public Dictionary<string, string> Outputs { get; set; } = new();
		public List<string> Trace { get; set; } = new();
	}

	/// <summary>Transport-only snapshot of a running CoreWF document. Element IDs are the same
	/// structural paths used by the workflow designer tree, never CoreWF runtime objects.</summary>
	public sealed class WorkflowDebugState
	{
		public bool IsRunning { get; set; }
		public bool IsPaused { get; set; }
		/// <summary>True when the instance is waiting for an external Bookmark rather than
		/// executing or stopped at a debugger breakpoint.</summary>
		public bool IsIdle { get; set; }
		public string CurrentElementId { get; set; } = "";
		public List<string> BreakpointElementIds { get; set; } = new();
		/// <summary>Root workflow In/InOut arguments materialized for this run. CoreWF tracking does
		/// not safely expose arbitrary lexical variables, so this is intentionally the reliable,
		/// user-provided portion of the paused variable view.</summary>
		public Dictionary<string, string> Inputs { get; set; } = new();
		/// <summary>External bookmarks at which the current workflow instance is idle. These are
		/// names/owners only; the addin can resume one without receiving a runtime Bookmark object.</summary>
		public List<WorkflowBookmarkInfo> Bookmarks { get; set; } = new();
		/// <summary>Activity tracking records observed so far in the active run. These are plain
		/// strings so inspecting a paused workflow neither resumes it nor crosses the runtime boundary.</summary>
		public List<string> Trace { get; set; } = new();
		/// <summary>Designer-level activity stack for the current pause. This deliberately uses
		/// stable document IDs rather than exposing CoreWF runtime objects over DDP.</summary>
		public List<WorkflowDebugFrame> CallStack { get; set; } = new();
	}

	/// <summary>A transport-only workflow debugger frame. Virtual nodes such as a Switch case
	/// are retained so the stack matches what the designer canvas displays.</summary>
	public sealed class WorkflowDebugFrame
	{
		public string ElementId { get; set; } = "";
		public string DisplayName { get; set; } = "";
		public string TypeName { get; set; } = "";
	}

	public sealed class WorkflowBookmarkInfo
	{
		public string Name { get; set; } = "";
		public string OwnerDisplayName { get; set; } = "";
	}

	/// <summary>Syntax-only validation result for a C# (<c>=</c>) or Visual Basic
	/// (<c>=vb:</c>) workflow expression.</summary>
	public sealed class WorkflowExpressionValidation
	{
		public bool IsValid { get; set; }
		public List<string> Diagnostics { get; set; } = new();
		/// <summary>Structured Roslyn diagnostics whose offsets are relative to the user-entered
		/// expression (rather than the generated host wrapper).</summary>
		public List<WorkflowExpressionDiagnostic> Details { get; set; } = new();
	}

	public sealed class WorkflowExpressionDiagnostic
	{
		public string Code { get; set; } = "";
		public string Message { get; set; } = "";
		public int Line { get; set; }
		public int Column { get; set; }
		public int Length { get; set; }
	}

	/// <summary>CoreWF model-validation result returned without executing a workflow.</summary>
	public sealed class WorkflowValidationResult
	{
		public bool IsValid { get; set; }
		public List<WorkflowValidationDiagnostic> Diagnostics { get; set; } = new();
	}

	public sealed class WorkflowValidationDiagnostic
	{
		public string Message { get; set; } = "";
		public string ElementId { get; set; } = "";
		public string PropertyName { get; set; } = "";
		public bool IsWarning { get; set; }
	}

	/// <summary>Contextual symbols offered by the lightweight workflow expression editor.</summary>
	public sealed class WorkflowExpressionCompletion
	{
		public List<string> Items { get; set; } = new();
	}

	/// <summary>Parse/layout diagnostic with an optional source range. Line and columns are
	/// one-based; an absent end column is represented by zero so older hosts remain wire-compatible.
	/// The shell must treat a zero range as a point diagnostic rather than guessing from the message.</summary>
	public sealed class DesignerDiagnostic
	{
		public string Severity { get; set; } = "Error";
		public string Message { get; set; } = "";
		public string FileName { get; set; } = "";
		public int Line { get; set; }
		public int Column { get; set; }
		public int EndColumn { get; set; }
	}

	/// <summary>Result of loading App.xaml / merged resources into the child.</summary>
	public sealed class DesignerAppResourcesResult
	{
		public bool Success { get; set; }
		public string Error { get; set; } = "";
	}

	/// <summary>One <c>DesignerActionItem</c> exposed by a component's smart tag
	/// (<c>DesignerActionService.GetComponentActions</c>), serialized so the parent can render
	/// a popup without ever holding the live <c>DesignerActionList</c>/<c>DesignerActionItem</c>
	/// object (WinForms shape; Microsoft backend only - see
	/// <c>design/list-smart-tag-actions</c>).</summary>
	public sealed class DesignerSmartTagActionInfo
	{
		/// <summary>Index of the owning <c>DesignerActionList</c> within
		/// <c>DesignerActionListCollection</c>, needed to re-locate the same item on a later
		/// <c>design/invoke-smart-tag-method</c> call (the live list is never cached server-side
		/// between calls).</summary>
		public int ListIndex { get; set; }
		/// <summary>Index of the item within <c>DesignerActionList.GetSortedActionItems()</c>.</summary>
		public int ItemIndex { get; set; }
		/// <summary>"Method" | "Property" | "Text" | "Header".</summary>
		public string Kind { get; set; } = "Text";
		public string DisplayName { get; set; } = "";
		public string Description { get; set; } = "";
		public string Category { get; set; } = "";
		/// <summary>Backing member name for a Method or Property item (<c>MemberName</c>).</summary>
		public string MemberName { get; set; } = "";
		/// <summary>Current value for a Property item, invariant-string encoded like
		/// <see cref="DesignerPropertyInfo.Value"/>. Empty for Method/Text/Header items.</summary>
		public string Value { get; set; } = "";
		public string TypeName { get; set; } = "";
		public bool IsEnum { get; set; }
		public bool IsNull { get; set; }
		public bool IsReadOnly { get; set; }
		/// <summary>Element id that owns <see cref="MemberName"/> for a Property item - almost
		/// always the selected component's own element id, since the great majority of
		/// <c>DesignerActionPropertyItem</c>s simply forward to a same-named property on the
		/// component itself. Lets the client commit an edit through the existing
		/// <c>design/set-property</c> RPC without a new "set smart tag property" method.</summary>
		public string PropertyOwnerElementId { get; set; } = "";
		public List<string> AllowedValues { get; set; } = new();
	}

	/// <summary>Response to <c>design/list-smart-tag-actions</c>.</summary>
	public sealed class DesignerSmartTagActions
	{
		public bool Accepted { get; set; }
		public string Error { get; set; } = "";
		public List<DesignerSmartTagActionInfo> Items { get; set; } = new();
	}

	/// <summary>One <c>DesignerVerb</c> (VS's right-click context-menu item for a selected
	/// component - e.g. <c>TabControlDesigner</c>'s "Add Tab"/"Remove Tab" - distinct from a
	/// smart-tag action, see <see cref="DesignerSmartTagActionInfo"/>).</summary>
	public sealed class DesignerVerbInfo
	{
		/// <summary>Index within <c>ComponentDesigner.Verbs</c>, needed to re-locate the same verb
		/// on a later <c>design/invoke-verb</c> call (the live collection is never cached
		/// server-side between calls).</summary>
		public int Index { get; set; }
		public string Text { get; set; } = "";
		public string Description { get; set; } = "";
		public bool Enabled { get; set; } = true;
		public bool Visible { get; set; } = true;
	}

	/// <summary>Response to <c>design/list-verbs</c>.</summary>
	public sealed class DesignerVerbs
	{
		public bool Accepted { get; set; }
		public string Error { get; set; } = "";
		public List<DesignerVerbInfo> Items { get; set; } = new();
	}

	/// <summary>Response to <c>design/get-type-icon</c>: the real WinForms toolbox icon for a
	/// CLR type (e.g. <c>System.Windows.Forms.Button</c>), the same 16x16 embedded-resource icon
	/// real Visual Studio's Toolbox/smart-tag/insert-item UI uses
	/// (<c>System.Drawing.ToolboxBitmapAttribute.GetImageFromResource</c>) - not a VS chrome icon
	/// from the VS2017 Image Library. <see cref="PngBase64"/> is empty (with
	/// <see cref="Accepted"/> still true) when the type has no such resource - the caller falls
	/// back to its own placeholder rather than treating that as an error.</summary>
	public sealed class DesignerTypeIconResult
	{
		public bool Accepted { get; set; }
		public string Error { get; set; } = "";
		public string PngBase64 { get; set; } = "";
	}
}
