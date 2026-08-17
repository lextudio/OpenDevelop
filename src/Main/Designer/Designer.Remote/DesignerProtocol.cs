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
