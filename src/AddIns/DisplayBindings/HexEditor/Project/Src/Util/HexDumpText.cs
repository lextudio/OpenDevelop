using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace HexEditor.Util
{
	public static class HexDumpText
	{
		public const int DefaultBytesPerLine = 16;

		public static string Format(byte[] bytes)
		{
			return Format(bytes, DefaultBytesPerLine);
		}

		public static string Format(byte[] bytes, int bytesPerLine)
		{
			if (bytes == null)
				throw new ArgumentNullException("bytes");
			if (bytesPerLine <= 0)
				throw new ArgumentOutOfRangeException("bytesPerLine");

			var builder = new StringBuilder();
			for (var offset = 0; offset < bytes.Length; offset += bytesPerLine) {
				var count = Math.Min(bytesPerLine, bytes.Length - offset);
				builder.Append(offset.ToString("X8", CultureInfo.InvariantCulture));
				builder.Append("  ");

				for (var i = 0; i < bytesPerLine; i++) {
					if (i < count)
						builder.Append(bytes[offset + i].ToString("X2", CultureInfo.InvariantCulture));
					else
						builder.Append("  ");

					builder.Append(i == 7 ? "  " : " ");
				}

				builder.Append(" |");
				for (var i = 0; i < count; i++) {
					var b = bytes[offset + i];
					builder.Append(b >= 32 && b <= 126 ? (char)b : '.');
				}

				builder.AppendLine("|");
			}

			return builder.ToString();
		}

		public static byte[] Parse(string text)
		{
			if (text == null)
				throw new ArgumentNullException("text");

			var bytes = new List<byte>();
			foreach (var rawLine in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)) {
				var line = rawLine;
				var bar = line.IndexOf('|');
				if (bar >= 0)
					line = line.Substring(0, bar);

				var parts = line.Split(new[] { ' ', '\t', ':' }, StringSplitOptions.RemoveEmptyEntries);
				var start = parts.Length > 0 && parts[0].Length == 8 && IsHex(parts[0]) ? 1 : 0;
				for (var i = start; i < parts.Length; i++) {
					var token = parts[i];
					if (token.Length != 2 || !IsHex(token))
						continue;

					bytes.Add(byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
				}
			}

			return bytes.ToArray();
		}

		static bool IsHex(string value)
		{
			return value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F');
		}
	}
}
