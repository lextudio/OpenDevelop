// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IClassificationType with a base-type chain, mirroring AvalonContentType's design (section 26).

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Text.Classification;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonClassificationType : IClassificationType
{
	readonly List<IClassificationType> baseTypes;

	public AvalonClassificationType(string classification, IEnumerable<IClassificationType> baseTypes = null)
	{
		if (string.IsNullOrEmpty(classification))
			throw new ArgumentException("A classification name is required.", nameof(classification));
		Classification = classification;
		this.baseTypes = baseTypes?.ToList() ?? new List<IClassificationType>();
	}

	public string Classification { get; }

	public IEnumerable<IClassificationType> BaseTypes => baseTypes;

	public bool IsOfType(string type)
	{
		if (string.Equals(Classification, type, StringComparison.OrdinalIgnoreCase))
			return true;
		return baseTypes.Any(baseType => baseType.IsOfType(type));
	}
}
