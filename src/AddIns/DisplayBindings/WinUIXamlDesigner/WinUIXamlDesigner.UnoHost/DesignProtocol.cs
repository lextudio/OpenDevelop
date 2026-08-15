using System.Collections.Generic;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// Runtime-agnostic protocol between the OpenDevelop host and the out-of-process
	/// WinUI design surface. DTOs are duplicated on both sides deliberately - JSON is
	/// the contract, not CLR type identity.
	/// </summary>
	public class DesignCapabilities
	{
		public string Runtime { get; set; } = "Uno.Skia";
		public string Version { get; set; } = "";
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

	public class DesignSnapshot
	{
		public ElementNode? Tree { get; set; }
		public List<DesignDiagnostic> Diagnostics { get; set; } = new();
		public RenderResult? Render { get; set; }
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
	}
}
