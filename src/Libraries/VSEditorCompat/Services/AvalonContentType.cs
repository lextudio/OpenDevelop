// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Minimal IContentType used by the VS editor compatibility layer. Content types are the VS
// editor's way to identify document flavors ("code", "CSharp", "XML", ...) and to let
// components activate for a whole family via base types. This spike keeps a plain registry
// (AvalonContentTypeRegistry) instead of implementing the full
// Microsoft.VisualStudio.Utilities.IContentTypeRegistryService - that is P1 (vs-editor-api.md
// section 39).

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>An immutable content type with a name, a display name, and base types.</summary>
public sealed class AvalonContentType : IContentType
{
	readonly IReadOnlyList<IContentType> baseTypes;

	public AvalonContentType(string typeName, string displayName, params IContentType[] baseTypes)
	{
		if (string.IsNullOrEmpty(typeName))
			throw new ArgumentException("A content type needs a type name.", nameof(typeName));
		TypeName = typeName;
		DisplayName = string.IsNullOrEmpty(displayName) ? typeName : displayName;
		this.baseTypes = baseTypes ?? Array.Empty<IContentType>();
	}

	public string TypeName { get; }

	public string DisplayName { get; }

	public IEnumerable<IContentType> BaseTypes => baseTypes;

	/// <summary>True when this content type is (directly or transitively) the named type.</summary>
	public bool IsOfType(string type)
	{
		if (string.Equals(TypeName, type, StringComparison.OrdinalIgnoreCase))
			return true;
		return baseTypes.Any(baseType => baseType.IsOfType(type));
	}

	public override string ToString() => TypeName;
}
