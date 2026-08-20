// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// A minimal, dictionary-backed IEditorOptions. No global options catalog/definitions registry
// (vs-editor-api.md section 27 says start with explicit registration, add MEF metadata later) -
// values simply live in a name-keyed store with a parent-fallback chain, matching what
// ITextView.Options/GlobalOptions consumers actually need first: get/set/clear by key.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Text.Editor;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonEditorOptions : IEditorOptions
{
	readonly Dictionary<string, object> values = new(StringComparer.Ordinal);

	public AvalonEditorOptions(IEditorOptions parent = null)
	{
		Parent = parent;
	}

	public IEditorOptions GlobalOptions => Parent?.GlobalOptions ?? this;

	public IEditorOptions Parent { get; set; }

	public IEnumerable<EditorOptionDefinition> SupportedOptions => Enumerable.Empty<EditorOptionDefinition>();

	public event EventHandler<EditorOptionChangedEventArgs> OptionChanged;

	public T GetOptionValue<T>(string optionId)
	{
		if (values.TryGetValue(optionId, out var value))
			return (T)value;
		return Parent != null ? Parent.GetOptionValue<T>(optionId) : default;
	}

	public T GetOptionValue<T>(EditorOptionKey<T> key) => GetOptionValue<T>(key.Name);

	public object GetOptionValue(string optionId)
	{
		if (values.TryGetValue(optionId, out var value))
			return value;
		return Parent?.GetOptionValue(optionId);
	}

	public void SetOptionValue(string optionId, object value)
	{
		values[optionId] = value;
		OptionChanged?.Invoke(this, new EditorOptionChangedEventArgs(optionId));
	}

	public void SetOptionValue<T>(EditorOptionKey<T> key, T value) => SetOptionValue(key.Name, value);

	public bool IsOptionDefined(string optionId, bool localScopeOnly)
	{
		if (values.ContainsKey(optionId))
			return true;
		return !localScopeOnly && (Parent?.IsOptionDefined(optionId, false) ?? false);
	}

	public bool IsOptionDefined<T>(EditorOptionKey<T> key, bool localScopeOnly) => IsOptionDefined(key.Name, localScopeOnly);

	public bool ClearOptionValue(string optionId)
	{
		var removed = values.Remove(optionId);
		if (removed)
			OptionChanged?.Invoke(this, new EditorOptionChangedEventArgs(optionId));
		return removed;
	}

	public bool ClearOptionValue<T>(EditorOptionKey<T> key) => ClearOptionValue(key.Name);
}
