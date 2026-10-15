using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// Maps a parse-error position reported against the text handed to <c>XamlReader.Load</c> back
	/// onto the document the user wrote.
	///
	/// The loaded text is never the user's: the host transform merges theme resources into it,
	/// <see cref="XamlDocumentRepair"/> reserializes it without formatting (so it is usually one
	/// line), and <c>d:DesignData</c> injects placeholder items. A "Line N, position M" from the
	/// native parser therefore indexes into text that exists nowhere on disk, and jumping the editor
	/// to it lands on an unrelated line. Keeping those rewrites length-preserving is not possible
	/// (they add and remove markup), so the position is translated instead: the element that
	/// contains it is found in the loaded text, located in the source by its path of element names
	/// (disambiguated by Name/x:Name/x:Key), and reported at the source element's own position. An
	/// element that exists only in the loaded text (injected resources) resolves to its nearest
	/// ancestor that exists in the source.
	/// </summary>
	static class XamlPositionMapper
	{
		static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

		/// <summary>
		/// Returns the source position for <paramref name="line"/>/<paramref name="column"/> (both
		/// 1-based, indexing <paramref name="loadedXaml"/>), with the range's end column, or null
		/// when no trustworthy mapping exists (the loaded text or the source is not well formed).
		/// A loaded text identical to the source maps to itself.
		/// </summary>
		public static (int Line, int Column, int EndColumn)? MapToSource(
			string sourceXaml, string loadedXaml, int line, int column)
		{
			if (string.IsNullOrEmpty(sourceXaml) || string.IsNullOrEmpty(loadedXaml) || line < 1)
				return null;
			if (string.Equals(sourceXaml, loadedXaml, StringComparison.Ordinal))
				return (line, Math.Max(1, column), Math.Max(1, column) + 1);

			XElement loadedRoot, sourceRoot;
			try
			{
				loadedRoot = XDocument.Parse(loadedXaml, LoadOptions.SetLineInfo).Root!;
				sourceRoot = XDocument.Parse(sourceXaml, LoadOptions.SetLineInfo).Root!;
			}
			catch (XmlException)
			{
				return null;
			}
			// A well-formed document may still have no root element (comments only).
			if (loadedRoot == null || sourceRoot == null)
				return null;

			var chain = ContainingChain(loadedRoot, line, Math.Max(1, column));
			// Walk the same chain in the source for as long as it matches.
			var current = sourceRoot;
			for (var i = 1; i < chain.Count; i++)
			{
				var next = FindCounterpart(current, chain[i], chain[i - 1]);
				if (next == null)
					break;
				current = next;
			}
			var info = (IXmlLineInfo)current;
			if (!info.HasLineInfo())
				return null;
			var start = info.LinePosition;
			return (info.LineNumber, start, start + current.Name.LocalName.Length);
		}

		/// <summary>The root-to-leaf elements whose start tags precede the position; the last one
		/// is the deepest element that starts at or before it and is still open there.</summary>
		static List<XElement> ContainingChain(XElement root, int line, int column)
		{
			var chain = new List<XElement> { root };
			var current = root;
			while (true)
			{
				XElement? candidate = null;
				foreach (var child in current.Elements())
				{
					var info = (IXmlLineInfo)child;
					if (!info.HasLineInfo()) continue;
					if (info.LineNumber < line || (info.LineNumber == line && info.LinePosition <= column))
						candidate = child; // the last child starting before the position
					else
						break;
				}
				if (candidate == null)
					return chain;
				chain.Add(candidate);
				current = candidate;
			}
		}

		/// <summary>The element of <paramref name="sourceParent"/> that corresponds to
		/// <paramref name="loaded"/>, a child of <paramref name="loadedParent"/>.</summary>
		static XElement? FindCounterpart(XElement sourceParent, XElement loaded, XElement loadedParent)
		{
			var sameName = sourceParent.Elements().Where(e => e.Name == loaded.Name).ToList();
			if (sameName.Count == 0)
				return null;
			var identity = Identity(loaded);
			if (identity != null)
			{
				var byIdentity = sameName.FirstOrDefault(e => Identity(e) == identity);
				if (byIdentity != null)
					return byIdentity;
			}
			// Fall back to the same occurrence among same-named siblings. Elements the load
			// injected ahead of the user's own can shift this, so only an unidentified element
			// resolves by position.
			var occurrence = loadedParent.Elements().Where(e => e.Name == loaded.Name).ToList().IndexOf(loaded);
			return occurrence >= 0 && occurrence < sameName.Count ? sameName[occurrence] : sameName[0];
		}

		static string? Identity(XElement element)
			=> (string?)element.Attribute(X + "Name") ?? (string?)element.Attribute("Name")
				?? (string?)element.Attribute(X + "Key");
	}
}
