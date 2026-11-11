// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

using ICSharpCode.SharpDevelop.Editor.CodeCompletion;
using SDAccessibility = ICSharpCode.TypeSystem.Accessibility;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
	/// <summary>
	/// The icon of a language-service completion item: its <see cref="CompletionItem.Glyph"/> (Roslyn's first tag, or the
	/// kind another service names: "Class", "Property", "Event", "Field", "Value") mapped to the editor's CompletionImage
	/// set, with the accessibility overlay from the Roslyn tags ("Private", "Protected", "Internal"; none means public).
	/// A kind with no icon in that set (a snippet, a .resx key) gets none rather than a wrong one.
	/// </summary>
	public static class LanguageServiceCompletionImages
	{
		public static ImageSource GetImage(CompletionItem item)
		{
			if (item == null)
				return null;
			return GetImage(item.Glyph, item.Tags);
		}

		public static ImageSource GetImage(string glyph, IReadOnlyList<string> tags)
		{
			if (string.IsNullOrEmpty(glyph))
				return null;
			switch (glyph.ToLowerInvariant()) {
				case "namespace":
					return CompletionImage.NamespaceImage;
				case "local":
				case "parameter":
				case "rangevariable":
					return SD.ResourceService.GetImage("Icons.16x16.Local")?.ImageSource;
				case "keyword":
					return SD.ResourceService.GetImage("Icons.16x16.Keyword")?.ImageSource;
			}
			CompletionImage image = GetCompletionImage(glyph);
			return image?.GetImage(GetAccessibility(tags));
		}

		static CompletionImage GetCompletionImage(string glyph)
		{
			switch (glyph.ToLowerInvariant()) {
				case "class": return CompletionImage.Class;
				case "structure":
				case "struct": return CompletionImage.Struct;
				case "interface": return CompletionImage.Interface;
				case "delegate": return CompletionImage.Delegate;
				case "enum": return CompletionImage.Enum;
				case "module": return CompletionImage.StaticClass;
				case "method": return CompletionImage.Method;
				case "extensionmethod": return CompletionImage.ExtensionMethod;
				case "property": return CompletionImage.Property;
				case "field": return CompletionImage.Field;
				case "constant":
				case "literal": return CompletionImage.Literal;
				case "enummember":
				case "value": return CompletionImage.EnumValue;
				case "event": return CompletionImage.Event;
				default: return null;
			}
		}

		static SDAccessibility GetAccessibility(IReadOnlyList<string> tags)
		{
			if (tags == null)
				return SDAccessibility.Public;
			if (tags.Contains("Private", StringComparer.OrdinalIgnoreCase))
				return SDAccessibility.Private;
			if (tags.Contains("Protected", StringComparer.OrdinalIgnoreCase))
				return tags.Contains("Internal", StringComparer.OrdinalIgnoreCase) ? SDAccessibility.ProtectedOrInternal : SDAccessibility.Protected;
			if (tags.Contains("Internal", StringComparer.OrdinalIgnoreCase))
				return SDAccessibility.Internal;
			return SDAccessibility.Public;
		}
	}
}
