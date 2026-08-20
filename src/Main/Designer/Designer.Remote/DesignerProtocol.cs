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
		public const int Version = 2;
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
		public List<DesignerDiagnostic> Diagnostics { get; set; } = new List<DesignerDiagnostic>();
		public DesignerRenderFrame? Render { get; set; }
		/// <summary>The id (tree path) of the element a <c>design/add-element</c> call just
		/// created, valid only on that RPC's own response - null for every other response
		/// (including a later <c>session/update</c>/<c>design/set-bounds</c> etc., where it would
		/// be stale). Lets a caller select the just-dropped element without needing to invent a
		/// name for it first (WinForms/WinUI instead have the caller supply a name up front and
		/// look the result up by name afterward - not an option here, since a freshly toolbox-
		/// dropped WPF element deliberately has no <c>x:Name</c> at all).</summary>
		public string? CreatedElementId { get; set; }
		/// <summary>Whether this session's project embeds any design-time theme (WPF shape only -
		/// WinForms/WinUI have their own real theme mechanisms and never need this). True when
		/// <see cref="DesignThemes"/> is non-empty.</summary>
		public bool SupportsThemeSwitch { get; set; }

		/// <summary>The design-time theme names the project embeds (WPF shape only): the file
		/// names (without extension) of the embedded <c>themes/*.xaml</c> resources. Drives the
		/// designer's theme combo, mirroring how the WinUI shape enumerates the app's
		/// ThemeDictionaries keys. Empty when no theme is embedded.</summary>
		public string[] DesignThemes { get; set; } = Array.Empty<string>();
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
		/// <summary>Child-index path from the root (e.g. "0,2,1"), for mapping a pick back to the source.</summary>
		public string Path { get; set; } = "";
		/// <summary>False for template parts / non-source nodes.</summary>
		public bool IsDesignable { get; set; } = true;
		public List<DesignerElementNode> Children { get; set; } = new List<DesignerElementNode>();
		/// <summary>Optional per-node property list for the Properties pad, using the same
		/// <see cref="DesignerPropertyInfo"/> shape <see cref="DesignerComponentInfo"/> already
		/// uses (designer-common.md "Property and event values"). Empty for backends whose
		/// Properties pad is instead driven off the host-owned XAML document directly
		/// (WinUI/Uno's <c>WinUIXamlElementPropertyAdapter</c>) - this field exists for backends
		/// like WPF whose real property metadata/values only the child (via its DesignItem
		/// reflection) actually knows.</summary>
		public List<DesignerPropertyInfo> Properties { get; set; } = new List<DesignerPropertyInfo>();
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

	/// <summary>Flat component snapshot entry (WinForms shape).</summary>
	public sealed class DesignerComponentInfo
	{
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
		public List<DesignerPropertyInfo> Properties { get; set; } = new List<DesignerPropertyInfo>();
		public List<DesignerEventInfo> Events { get; set; } = new List<DesignerEventInfo>();
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
		/// (not yet implemented) needs the other kinds for properties whose value is itself a
		/// nested DesignItem - Binding, Brush, Gradient, Transform and other markup extensions -
		/// which "Xaml"/"Reference" carry as constrained XAML text in the same <see cref="Value"/>
		/// field rather than a runtime object.</summary>
		public string Kind { get; set; } = "String";
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

	/// <summary>Shape for a child-to-host selection-changed notification: the child owns
	/// selection (designer-common.md's "the host never runs a competing selection model"), so
	/// this carries only the selected element ids, never a live component/DesignItem. Not yet
	/// wired to any RPC/notification transport on any backend - WinForms and WinUI/Uno currently
	/// report selection through their own per-backend control events instead
	/// (SelectionChanged/OnSurfacePointerPressed); this type exists so a future shared transport
	/// has one settled shape to adopt rather than three per-backend ones.</summary>
	public sealed class DesignerSelectionChanged
	{
		public string SessionId { get; set; } = "";
		public string DocumentId { get; set; } = "";
		public List<string> ElementIds { get; set; } = new List<string>();
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

	public sealed class DesignerToolboxItemInfo
	{
		public string Name { get; set; } = "";
		public string DisplayName { get; set; } = "";
		public string Category { get; set; } = "";
		public string Template { get; set; } = "";
		public string XamlNamespace { get; set; } = "";
		public string TypeName { get; set; } = "";
	}

	/// <summary>Parse/layout diagnostic with source location.</summary>
	public sealed class DesignerDiagnostic
	{
		public string Severity { get; set; } = "Error";
		public string Message { get; set; } = "";
		public int Line { get; set; }
		public int Column { get; set; }
	}

	/// <summary>Result of loading App.xaml / merged resources into the child.</summary>
	public sealed class DesignerAppResourcesResult
	{
		public bool Success { get; set; }
		public string Error { get; set; } = "";
	}
}
