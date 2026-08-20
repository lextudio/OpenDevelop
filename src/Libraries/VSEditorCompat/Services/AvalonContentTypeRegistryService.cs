// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// The official Microsoft.VisualStudio.Utilities.IContentTypeRegistryService over AvalonContentType
// (vs-editor-api.md section 19). The registry pre-seeds the standard VS editor hierarchy
// (any / text / plaintext / inert, code + languages, XML + XAML) and lets extensions add more
// content types dynamically - the "any" base lets an IsOfType query span a whole family.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>A thread-safe content type registry implementing the official VS editor contract.</summary>
public sealed class AvalonContentTypeRegistryService : IContentTypeRegistryService
{
	readonly object gate = new();
	readonly Dictionary<string, IContentType> types = new(StringComparer.OrdinalIgnoreCase);

	public AvalonContentTypeRegistryService()
	{
		Any = Register("any", "any");
		UnknownContentType = Register("unknown", "unknown", Any);
		Text = Register("text", "text", Any);
		PlainText = Register("plaintext", "plaintext", Text);
		Inert = Register("inert", "inert", Any);
		Code = Register("code", "code", Text);
		CSharp = Register("CSharp", "C#", Code);
		Basic = Register("Basic", "Basic", Code);
		FSharp = Register("FSharp", "F#", Code);
		Xml = Register("XML", "XML", Text);
		Xaml = Register("XAML", "XAML", Xml);
	}

	/// <summary>The root of every content type.</summary>
	public IContentType Any { get; }

	/// <summary>Text with no language services; the base of plaintext.</summary>
	public IContentType PlainText { get; }

	/// <summary>A content type for buffers that should not participate in language services.</summary>
	public IContentType Inert { get; }

	/// <summary>The base of the code languages.</summary>
	public IContentType Code { get; }

	public IContentType CSharp { get; }
	public IContentType Basic { get; }
	public IContentType FSharp { get; }
	public IContentType Xml { get; }
	public IContentType Xaml { get; }

	/// <summary>Plain text; the base of every language content type.</summary>
	public IContentType Text { get; }

	public IContentType UnknownContentType { get; }

	public IEnumerable<IContentType> ContentTypes {
		get {
			lock (gate)
				return types.Values.ToArray();
		}
	}

	public IContentType GetContentType(string typeName)
	{
		if (string.IsNullOrEmpty(typeName))
			return null;
		lock (gate)
			return types.TryGetValue(typeName, out var type) ? type : null;
	}

	public IContentType AddContentType(string typeName, IEnumerable<string> baseTypeNames)
	{
		if (string.IsNullOrEmpty(typeName))
			throw new ArgumentException("A content type needs a type name.", nameof(typeName));
		IContentType[] baseTypes;
		lock (gate) {
			baseTypes = (baseTypeNames ?? Enumerable.Empty<string>())
				.Select(name => {
					if (types.TryGetValue(name, out var type))
						return type;
					throw new ArgumentException($"Unknown base content type '{name}'.", nameof(baseTypeNames));
				})
				.ToArray();
			var created = new AvalonContentType(typeName, typeName, baseTypes);
			types[typeName] = created;
			return created;
		}
	}

	public void RemoveContentType(string typeName)
	{
		if (string.IsNullOrEmpty(typeName))
			return;
		lock (gate) {
			types.Remove(typeName);
		}
	}

	IContentType Register(string typeName, string displayName, params IContentType[] baseTypes)
	{
		var type = new AvalonContentType(typeName, displayName, baseTypes);
		types[typeName] = type;
		return type;
	}
}
