using System.Collections.Generic;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// Runtime-agnostic protocol between the OpenDevelop host and the out-of-process
	/// WinUI design surface. DTOs are duplicated on both sides deliberately - JSON is
	/// the contract, not CLR type identity.
	/// </summary>
	/// <summary>Wire protocol version, matching the common designer protocol's
	/// <c>DesignerProtocol.Version</c> - bumped whenever the session envelope shape changes.</summary>
	public static class DesignProtocol
	{
		public const int Version = 2;
	}

	public class DesignCapabilities
	{
		public string Runtime { get; set; } = "Uno.Skia";
		public string Version { get; set; } = "";
		public string SessionId { get; set; } = "";
		public List<ToolboxItemInfo> Toolbox { get; set; } = new();
	}

	public class ToolboxItemInfo
	{
		public string Name { get; set; } = "";
		public string DisplayName { get; set; } = "";
		public string Category { get; set; } = "";
		public string Template { get; set; } = "";
		public string XamlNamespace { get; set; } = "";
	}

	public class LoadDesignRequest
	{
		public string SessionId { get; set; } = "";
		public string DocumentId { get; set; } = "";
		public long Version { get; set; }
		public string Xaml { get; set; } = "";
		public double Width { get; set; } = 640;
		public double Height { get; set; } = 480;
		public double Dpi { get; set; } = 1.0;
	}

	public class LayoutRequest
	{
		public double Width { get; set; } = 640;
		public double Height { get; set; } = 480;
		public double Dpi { get; set; } = 1.0;
	}

	public class ThemeRequest
	{
		/// <summary>"Light", "Dark" or "Default".</summary>
		public string Theme { get; set; } = "";
	}

	public class DesignSnapshot
	{
		public string SessionId { get; set; } = "";
		public string DocumentId { get; set; } = "";
		public long Version { get; set; }
		public bool Accepted { get; set; } = true;
		public string Error { get; set; } = "";
		public ElementNode? Tree { get; set; }
		public List<DesignDiagnostic> Diagnostics { get; set; } = new();
		public RenderResult? Render { get; set; }
	}

	/// <summary>Versioned edit set returned by session/flush; the child holds no independent
	/// edit buffer today, so this reports the current XAML as the sole file.</summary>
	public class DesignEditSet
	{
		public string SessionId { get; set; } = "";
		public string DocumentId { get; set; } = "";
		public long BaseVersion { get; set; }
		public List<DesignFileSnapshot> Files { get; set; } = new();
	}

	public class DesignFileSnapshot
	{
		public string FileName { get; set; } = "";
		public string Text { get; set; } = "";
	}

	public class ElementNode
	{
		public string? Name { get; set; }
		public string Type { get; set; } = "";
		public double X { get; set; }
		public double Y { get; set; }
		public double Width { get; set; }
		public double Height { get; set; }
		public List<ElementNode> Children { get; set; } = new();

		/// <summary>Child-index path from the root (e.g. "0,2,1"), for mapping a pick back to the source.</summary>
		public string Path { get; set; } = "";
	}

	public class DesignDiagnostic
	{
		public string Severity { get; set; } = "Error";
		public string Message { get; set; } = "";
		public int Line { get; set; }
		public int Column { get; set; }
	}

	public class RenderResult
	{
		public int Width { get; set; }
		public int Height { get; set; }
		public double Dpi { get; set; } = 1.0;
		public string Data { get; set; } = "";
		/// <summary>Render time in milliseconds (rasterize + compress), for performance reporting.</summary>
		public double RenderMs { get; set; }
	}

	public class AppResourcesResult
	{
		public bool Success { get; set; }
		public string Error { get; set; } = "";
	}

	public class HitTestRequest
	{
		public double X { get; set; }
		public double Y { get; set; }
	}

	public class HitTestResult
	{
		/// <summary>Named elements under the point, innermost first.</summary>
		public List<string> Chain { get; set; } = new();

		/// <summary>Tree path of the innermost hit when it has no name (the shell maps it to the source and auto-names it).</summary>
		public string PickPath { get; set; } = "";
	}
}
