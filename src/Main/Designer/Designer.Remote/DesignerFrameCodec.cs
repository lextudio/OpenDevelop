// Runtime-neutral DDP frame payload codec.

using System;
using System.IO;
using System.IO.Compression;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>Encodes and decodes the base64-deflate payload used by
	/// <see cref="DesignerRenderFrame.Data"/>. Pixel-format interpretation intentionally stays
	/// with each presentation adapter; this class only owns the wire-byte transform.</summary>
	public static class DesignerFrameCodec
	{
		public static string EncodeDeflateBase64(ReadOnlySpan<byte> data)
		{
			using var output = new MemoryStream();
			using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
				deflate.Write(data);
			return Convert.ToBase64String(output.ToArray());
		}

		public static byte[] DecodeDeflateBase64(string data)
		{
			ArgumentException.ThrowIfNullOrEmpty(data);
			using var input = new MemoryStream(Convert.FromBase64String(data));
			using var deflate = new DeflateStream(input, CompressionMode.Decompress);
			using var output = new MemoryStream();
			deflate.CopyTo(output);
			return output.ToArray();
		}

		/// <summary>Decodes a raw BGRA32 frame and verifies that its byte count exactly matches
		/// the dimensions declared on the DDP frame. Presentation adapters still choose their
		/// own bitmap type and alpha interpretation.</summary>
		public static byte[] DecodeBgra32(DesignerRenderFrame frame)
		{
			ArgumentNullException.ThrowIfNull(frame);
			if (frame.Width <= 0 || frame.Height <= 0)
				throw new InvalidDataException("A BGRA frame must declare positive dimensions.");
			var expectedLength = checked(frame.Width * frame.Height * 4);
			var pixels = DecodeDeflateBase64(frame.Data);
			if (pixels.Length != expectedLength)
				throw new InvalidDataException($"The BGRA frame payload has {pixels.Length} bytes; expected {expectedLength}.");
			return pixels;
		}
	}
}
