using System;
using System.IO;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>Decodes the child's deflate-compressed BGRA frame payload.</summary>
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
