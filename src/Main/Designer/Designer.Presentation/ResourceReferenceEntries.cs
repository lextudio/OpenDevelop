using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>
/// The drop-down text of a resource-reference Properties-pad row: "(none)", then every offered resource key as its
/// <c>{StaticResource key}</c> and <c>{DynamicResource key}</c> forms. Pure text mapping shared by the row and its tests.
/// </summary>
public static class ResourceReferenceEntries
{
	public const string NoneEntry = "(none)";

	/// <summary>The entries for <paramref name="keys"/>, in the order they are shown: none, all static forms, all dynamic forms.</summary>
	public static List<string> Entries(IEnumerable<string> keys)
	{
		var list = keys.ToList();
		var entries = new List<string> { NoneEntry };
		entries.AddRange(list.Select(key => "{StaticResource " + key + "}"));
		entries.AddRange(list.Select(key => "{DynamicResource " + key + "}"));
		return entries;
	}

	/// <summary>Splits an entry into the RPC's kind and key (both empty for none); false for text that is not an entry.</summary>
	public static bool TryParse(string? entry, out string kind, out string key)
	{
		(kind, key) = ("", "");
		if (entry == NoneEntry)
			return true;
		foreach (var candidate in new[] { "Static", "Dynamic" }) {
			var prefix = "{" + candidate + "Resource ";
			if (entry != null && entry.StartsWith(prefix, StringComparison.Ordinal) && entry.EndsWith('}')) {
				var parsed = entry.Substring(prefix.Length, entry.Length - prefix.Length - 1);
				if (parsed.Length == 0)
					return false;
				(kind, key) = (candidate, parsed);
				return true;
			}
		}
		return false;
	}
}
