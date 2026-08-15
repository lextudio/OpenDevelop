using System.Collections.Generic;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

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
	/// <summary>BGRA32 pixels, deflate-compressed then base64 (see DesignHost.RenderAsync).</summary>
	public string Data { get; set; } = "";
	/// <summary>Render time in milliseconds (rasterize + compress), for performance reporting.</summary>
	public double RenderMs { get; set; }
}

/// <summary>Shared frame-codec helpers (deflate is applied by the child, undone by the parent).</summary>
public static class RenderCodec
{
	public static byte[] Decode(string data)
	{
		var compressed = System.Convert.FromBase64String(data);
		using var input = new System.IO.MemoryStream(compressed);
		using var deflate = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress);
		using var output = new System.IO.MemoryStream();
		deflate.CopyTo(output);
		return output.ToArray();
	}
}

public class AppResourcesResult
{
	public bool Success { get; set; }
	public string Error { get; set; } = "";
}

public class HitTestResult
{
	/// <summary>Named elements under the point, innermost first.</summary>
	public List<string> Chain { get; set; } = new();

	/// <summary>Tree path of the innermost hit, when it has no name (the shell can map it to the source and auto-name it).</summary>
	public string PickPath { get; set; } = "";
}
