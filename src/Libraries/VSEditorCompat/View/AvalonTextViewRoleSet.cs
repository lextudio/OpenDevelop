// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// A minimal ITextViewRoleSet for the VS editor compatibility view layer (vs-editor-api.md
// section 35). Role sets are immutable-ish string sets used to describe which editor
// capabilities a view has.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Text.Editor;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>An immutable set of view role strings.</summary>
public sealed class AvalonTextViewRoleSet : ITextViewRoleSet
{
	readonly HashSet<string> roles;

	public AvalonTextViewRoleSet(IEnumerable<string> roles)
	{
		this.roles = new HashSet<string>(roles ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
	}

	public bool Contains(string textViewRole) => roles.Contains(textViewRole);

	public bool ContainsAll(IEnumerable<string> textViewRoles)
		=> textViewRoles.All(roles.Contains);

	public bool ContainsAny(IEnumerable<string> textViewRoles)
		=> textViewRoles.Any(roles.Contains);

	public ITextViewRoleSet UnionWith(ITextViewRoleSet roleSet)
	{
		var combined = new HashSet<string>(roles, StringComparer.Ordinal);
		if (roleSet != null)
			combined.UnionWith(roleSet.ToList());
		return new AvalonTextViewRoleSet(combined);
	}

	public IEnumerator<string> GetEnumerator() => roles.GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
