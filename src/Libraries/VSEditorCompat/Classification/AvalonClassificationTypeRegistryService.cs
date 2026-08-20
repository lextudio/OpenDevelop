// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// The official IClassificationTypeRegistryService (vs-editor-api.md section 26). Pre-seeds the
// well-known base classifications extensions expect to already exist, mirroring
// AvalonContentTypeRegistryService's bootstrap-hierarchy approach.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Text.Classification;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonClassificationTypeRegistryService : IClassificationTypeRegistryService
{
	readonly object gate = new();
	readonly Dictionary<string, IClassificationType> types = new(StringComparer.OrdinalIgnoreCase);

	// The official Microsoft.VisualStudio.Language.StandardClassification package (which defines
	// PredefinedClassificationTypeNames) is not referenced by this project - see vs-editor-api.md
	// section 5.1's package-family note - so the well-known names are spelled out directly; they
	// match that package's string values so a component expecting them by name still finds them.
	public AvalonClassificationTypeRegistryService()
	{
		Register("formal language");
		Register("natural language");
		Register("comment", "natural language");
		Register("identifier", "formal language");
		Register("keyword", "formal language");
		Register("literal", "formal language");
		Register("string", "literal");
		Register("number", "literal");
		Register("operator", "formal language");
		Register("whitespace", "formal language");
	}

	public IClassificationType GetClassificationType(string type)
	{
		lock (gate)
			return types.TryGetValue(type, out var found) ? found : null;
	}

	public IClassificationType CreateClassificationType(string type, IEnumerable<IClassificationType> baseTypes)
	{
		lock (gate) {
			if (types.ContainsKey(type))
				throw new InvalidOperationException($"A classification type named '{type}' already exists.");
			var created = new AvalonClassificationType(type, baseTypes);
			types[type] = created;
			return created;
		}
	}

	public IClassificationType CreateTransientClassificationType(IEnumerable<IClassificationType> baseTypes)
		=> new AvalonClassificationType($"transient#{Guid.NewGuid():N}", baseTypes);

	public IClassificationType CreateTransientClassificationType(params IClassificationType[] baseTypes)
		=> CreateTransientClassificationType((IEnumerable<IClassificationType>)baseTypes);

	public ILayeredClassificationType GetClassificationType(ClassificationLayer layer, string type)
		=> throw new NotSupportedException("Layered classification types are not implemented in this compatibility layer.");

	public ILayeredClassificationType CreateClassificationType(ClassificationLayer layer, string type, IEnumerable<IClassificationType> baseTypes)
		=> throw new NotSupportedException("Layered classification types are not implemented in this compatibility layer.");

	IClassificationType Register(string name, params string[] baseTypeNames)
	{
		var baseTypes = baseTypeNames.Select(n => types[n]).ToArray();
		var type = new AvalonClassificationType(name, baseTypes);
		types[name] = type;
		return type;
	}
}
