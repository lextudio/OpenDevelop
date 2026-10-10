// Copyright (c) 2026 OpenDevelop contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.

using System;
using System.IO;
using System.Net;
using System.Text;

namespace ICSharpCode.FormsDesigner.Services
{
	/// <summary>
	/// Replaces an existing image payload in a .resx document without deserializing it or
	/// reformatting unrelated XML. The out-of-process designer accepts the standard TypeConverter
	/// byte-array representation, while BinaryFormatter resource payloads intentionally remain
	/// outside this first safe editor slice.
	/// </summary>
	internal static class ResxImageResourceEditor
	{
		const string ByteArrayMimeType = "application/x-microsoft.net.object.bytearray.base64";

		public static byte[] ReplaceExistingImage(byte[] resourceBytes, string resourceKey, byte[] imageBytes)
		{
			if (resourceBytes == null)
				throw new ArgumentNullException(nameof(resourceBytes));
			if (String.IsNullOrWhiteSpace(resourceKey))
				throw new ArgumentException("A resource key is required.", nameof(resourceKey));
			if (imageBytes == null || imageBytes.Length == 0)
				throw new ArgumentException("The replacement image is empty.", nameof(imageBytes));

			var (text, encoding, preamble) = Decode(resourceBytes);
			if (!TryFindDataElement(text, resourceKey, out var data))
				throw new InvalidOperationException("The resource key '" + resourceKey + "' was not found.");

			var type = AttributeValue(data.Attributes, "type");
			var mimeType = AttributeValue(data.Attributes, "mimetype");
			if (!IsSupportedImageType(type) || !String.Equals(mimeType, ByteArrayMimeType, StringComparison.OrdinalIgnoreCase)) {
				throw new NotSupportedException("Only existing Image, Bitmap, or Icon .resx entries stored as TypeConverter byte arrays can be replaced.");
			}
			if (!HasSupportedImageHeader(type, imageBytes))
				throw new InvalidDataException("The selected file is not a supported image for the existing resource type.");

			if (!TryFindValue(text, data.BodyStart, data.EndStart, out var valueStart, out var valueEnd))
				throw new InvalidDataException("The resource key '" + resourceKey + "' has no value.");
			var payload = Convert.ToBase64String(imageBytes);
			var updated = text.Substring(0, valueStart) + payload + text.Substring(valueEnd);
			return Encode(updated, encoding, preamble);
		}

		readonly struct ElementRange
		{
			public ElementRange(string attributes, int bodyStart, int endStart)
			{
				Attributes = attributes;
				BodyStart = bodyStart;
				EndStart = endStart;
			}

			public string Attributes { get; }
			public int BodyStart { get; }
			public int EndStart { get; }
		}

		static bool TryFindDataElement(string text, string resourceKey, out ElementRange result)
		{
			var found = false;
			ElementRange match = default;
			for (var offset = 0; TryReadTag(text, ref offset, out var tag);) {
				if (tag.IsClosing || tag.IsSelfClosing || !String.Equals(tag.Name, "data", StringComparison.Ordinal))
					continue;
				var endOffset = tag.End;
				var foundClosingTag = false;
				XmlTag child = default;
				while (TryReadTag(text, ref endOffset, out child)) {
					if (child.IsClosing && String.Equals(child.Name, "data", StringComparison.Ordinal))
					{
						foundClosingTag = true;
						break;
					}
					if (!child.IsClosing && String.Equals(child.Name, "data", StringComparison.Ordinal))
						throw new InvalidDataException("Nested .resx data elements are not supported.");
				}
				if (!foundClosingTag)
					throw new InvalidDataException("A .resx data element has no closing tag.");
				if (!String.Equals(AttributeValue(tag.Attributes, "name"), resourceKey, StringComparison.Ordinal))
				{
					offset = endOffset;
					continue;
				}
				if (found)
					throw new InvalidDataException("The resource key '" + resourceKey + "' is defined more than once.");
				match = new ElementRange(tag.Attributes, tag.End, child.Start);
				found = true;
				offset = endOffset;
			}
			result = match;
			return found;
		}

		static bool TryFindValue(string text, int bodyStart, int dataEnd, out int valueStart, out int valueEnd)
		{
			var offset = bodyStart;
			while (offset < dataEnd && TryReadTag(text, ref offset, out var tag)) {
				if (tag.Start >= dataEnd)
					break;
				if (tag.IsClosing || tag.IsSelfClosing || !String.Equals(tag.Name, "value", StringComparison.Ordinal))
					continue;
				var closeOffset = tag.End;
				if (!TryReadTag(text, ref closeOffset, out var closing) || !closing.IsClosing || !String.Equals(closing.Name, "value", StringComparison.Ordinal))
					throw new InvalidDataException("The resource value is not a simple text value.");
				valueStart = tag.End;
				valueEnd = closing.Start;
				return true;
			}
			valueStart = valueEnd = 0;
			return false;
		}

		readonly struct XmlTag
		{
			public XmlTag(int start, int end, string name, string attributes, bool isClosing, bool isSelfClosing)
			{
				Start = start; End = end; Name = name; Attributes = attributes; IsClosing = isClosing; IsSelfClosing = isSelfClosing;
			}
			public int Start { get; }
			public int End { get; }
			public string Name { get; }
			public string Attributes { get; }
			public bool IsClosing { get; }
			public bool IsSelfClosing { get; }
		}

		static bool TryReadTag(string text, ref int offset, out XmlTag tag)
		{
			while (offset < text.Length) {
				var start = text.IndexOf('<', offset);
				if (start < 0) break;
				if (text.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal)) {
					var endComment = text.IndexOf("-->", start + 4, StringComparison.Ordinal);
					offset = endComment < 0 ? text.Length : endComment + 3;
					continue;
				}
				if (text.AsSpan(start).StartsWith("<![CDATA[", StringComparison.Ordinal)) {
					var endCData = text.IndexOf("]]>", start + 9, StringComparison.Ordinal);
					offset = endCData < 0 ? text.Length : endCData + 3;
					continue;
				}
				if (text.AsSpan(start).StartsWith("<?", StringComparison.Ordinal)) {
					var endDeclaration = text.IndexOf("?>", start + 2, StringComparison.Ordinal);
					offset = endDeclaration < 0 ? text.Length : endDeclaration + 2;
					continue;
				}
				var end = FindTagEnd(text, start + 1);
				if (end < 0) break;
				var content = text.Substring(start + 1, end - start - 1).Trim();
				offset = end + 1;
				if (content.Length == 0 || content[0] == '!') continue;
				var closing = content[0] == '/';
				if (closing) content = content.Substring(1).TrimStart();
				var selfClosing = !closing && content.EndsWith("/", StringComparison.Ordinal);
				if (selfClosing) content = content.Substring(0, content.Length - 1).TrimEnd();
				var nameLength = 0;
				while (nameLength < content.Length && !Char.IsWhiteSpace(content[nameLength])) nameLength++;
				if (nameLength == 0) continue;
				tag = new XmlTag(start, end + 1, content.Substring(0, nameLength), content.Substring(nameLength), closing, selfClosing);
				return true;
			}
			tag = default;
			return false;
		}

		static int FindTagEnd(string text, int offset)
		{
			char quote = '\0';
			for (var i = offset; i < text.Length; i++) {
				if (quote != '\0') {
					if (text[i] == quote) quote = '\0';
				} else if (text[i] == '\'' || text[i] == '"') {
					quote = text[i];
				} else if (text[i] == '>') {
					return i;
				}
			}
			return -1;
		}

		static string AttributeValue(string attributes, string name)
		{
			for (var offset = 0; offset < attributes.Length;) {
				while (offset < attributes.Length && Char.IsWhiteSpace(attributes[offset])) offset++;
				var nameStart = offset;
				while (offset < attributes.Length && !Char.IsWhiteSpace(attributes[offset]) && attributes[offset] != '=') offset++;
				if (nameStart == offset) break;
				var attributeName = attributes.Substring(nameStart, offset - nameStart);
				while (offset < attributes.Length && Char.IsWhiteSpace(attributes[offset])) offset++;
				if (offset >= attributes.Length || attributes[offset++] != '=') continue;
				while (offset < attributes.Length && Char.IsWhiteSpace(attributes[offset])) offset++;
				if (offset >= attributes.Length || (attributes[offset] != '\'' && attributes[offset] != '"')) continue;
				var quote = attributes[offset++];
				var valueStart = offset;
				while (offset < attributes.Length && attributes[offset] != quote) offset++;
				if (offset >= attributes.Length) break;
				var value = WebUtility.HtmlDecode(attributes.Substring(valueStart, offset++ - valueStart));
				if (String.Equals(attributeName, name, StringComparison.Ordinal)) return value;
			}
			return "";
		}

		static (string Text, Encoding Encoding, byte[] Preamble) Decode(byte[] bytes)
		{
			var encoding = DetectEncoding(bytes, out var preambleLength);
			string text;
			try {
				text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
			} catch (DecoderFallbackException exception) {
				throw new InvalidDataException("The .resx resource must be encoded as UTF-8 or UTF-16.", exception);
			}
			if (preambleLength == 0)
				return (text, encoding, Array.Empty<byte>());
			var preamble = new byte[preambleLength];
			Buffer.BlockCopy(bytes, 0, preamble, 0, preambleLength);
			return (text, encoding, preamble);
		}

		static byte[] Encode(string text, Encoding encoding, byte[] preamble)
		{
			var content = encoding.GetBytes(text);
			if (preamble.Length == 0)
				return content;
			var result = new byte[preamble.Length + content.Length];
			Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
			Buffer.BlockCopy(content, 0, result, preamble.Length, content.Length);
			return result;
		}

		static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
		{
			if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) {
				preambleLength = 3;
				return new UTF8Encoding(false, true);
			}
			if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) {
				preambleLength = 2;
				return new UnicodeEncoding(false, false, true);
			}
			if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) {
				preambleLength = 2;
				return new UnicodeEncoding(true, false, true);
			}
			preambleLength = 0;
			return new UTF8Encoding(false, true);
		}

		static bool IsSupportedImageType(string type)
		{
			return type.StartsWith("System.Drawing.Image,", StringComparison.Ordinal)
				|| type.StartsWith("System.Drawing.Bitmap,", StringComparison.Ordinal)
				|| type.StartsWith("System.Drawing.Icon,", StringComparison.Ordinal);
		}

		static bool HasSupportedImageHeader(string type, byte[] bytes)
		{
			if (type.StartsWith("System.Drawing.Icon,", StringComparison.Ordinal))
				return bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0;
			return IsPng(bytes) || IsBitmap(bytes) || IsGif(bytes) || IsJpeg(bytes);
		}

		static bool IsPng(byte[] bytes) => bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47 && bytes[4] == 0x0d && bytes[5] == 0x0a && bytes[6] == 0x1a && bytes[7] == 0x0a;
		static bool IsBitmap(byte[] bytes) => bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4d;
		static bool IsGif(byte[] bytes) => bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38 && (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61;
		static bool IsJpeg(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
	}
}
