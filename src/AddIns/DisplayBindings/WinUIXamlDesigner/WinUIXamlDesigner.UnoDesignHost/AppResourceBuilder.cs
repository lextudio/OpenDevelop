using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>
/// Builds a self-contained ResourceDictionary XAML from the project's App.xaml for the
/// out-of-process Uno design host: the child cannot load the project's own assembly
/// (code-behind, x:Class) or ms-appx resources, so the app's resources are extracted at
/// design time and re-expressed as one inline dictionary that XamlReader can load.
/// Merged dictionaries referenced by Source are resolved against their own file locations
/// and inlined recursively; ThemeDictionaries are hoisted to the top level where
/// ThemeResource lookup expects them. Types from the user project (xmlns:local) still
/// cannot load - such resources surface as a load diagnostic and the design renders
/// without them, degrading gracefully.
/// </summary>
static class AppResourceBuilder
{
	static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	/// <summary>
	/// Returns the combined ResourceDictionary XAML, or null when App.xaml has no
	/// resources. Read/parse failures and unresolvable merged dictionaries are reported
	/// in <paramref name="errors"/>.
	/// </summary>
	public static string Build(string appXamlPath, ICollection<string> errors)
	{
		XElement appRoot;
		try
		{
			appRoot = XDocument.Load(appXamlPath, LoadOptions.SetLineInfo).Root;
		}
		catch (Exception e)
		{
			errors.Add("App.xaml could not be read: " + e.GetBaseException().Message);
			return null;
		}
		if (appRoot?.Name != Xaml + "Application")
		{
			errors.Add("App.xaml root is not <Application>.");
			return null;
		}

		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var baseDir = Path.GetDirectoryName(Path.GetFullPath(appXamlPath));
		var output = new XElement(Xaml + "ResourceDictionary");
		var themeDictionaries = new List<XElement>();

		var resources = appRoot.Element(Xaml + "Application.Resources");
		var entries = resources?.Element(Xaml + "ResourceDictionary")?.Elements() ?? resources?.Elements();
		if (entries != null)
		{
			Inline(entries, output, themeDictionaries, baseDir, visited, errors, appRoot);
		}

		return Finish(output, themeDictionaries);
	}

	/// <summary>
	/// Same inlining/merge engine as <see cref="Build(string, ICollection{string})"/>, but for a
	/// set of independent <c>&lt;ResourceDictionary&gt;</c> root files rather than one
	/// <c>&lt;Application&gt;</c> - used to assemble the WinUI framework's own default theme
	/// resources (see <c>FrameworkDefaultResources</c> in the Microsoft WinUI host) from a curated
	/// set of the framework's own *_themeresources.xaml files. A shared <paramref name="errors"/>
	/// collector and a single dictionary de-duplication pass across every file, exactly as
	/// <see cref="Build(string, ICollection{string})"/> gives one App.xaml.
	/// </summary>
	public static string BuildFromDictionaryFiles(IEnumerable<string> xamlPaths, ICollection<string> errors)
	{
		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var output = new XElement(Xaml + "ResourceDictionary");
		var themeDictionaries = new List<XElement>();

		foreach (var path in xamlPaths)
		{
			if (!visited.Add(Path.GetFullPath(path)))
			{
				continue;
			}
			XElement root;
			try
			{
				root = XDocument.Load(path, LoadOptions.SetLineInfo).Root;
			}
			catch (Exception e)
			{
				errors.Add($"{path} could not be read: {e.GetBaseException().Message}");
				continue;
			}
			if (root?.Name != Xaml + "ResourceDictionary")
			{
				errors.Add($"{path} does not start with <ResourceDictionary>.");
				continue;
			}
			Inline(root.Elements(), output, themeDictionaries, Path.GetDirectoryName(Path.GetFullPath(path)), visited, errors, root);
		}

		return Finish(output, themeDictionaries);
	}

	/// <summary>
	/// Merges several ALREADY-BUILT <c>&lt;ResourceDictionary&gt;</c> XAML texts (typically this
	/// class's own prior output - e.g. the WinUI framework's default theme resources plus a
	/// project's App.xaml resources) into one. Earlier entries in <paramref name="xamlTexts"/> have
	/// LOWER priority: a duplicate x:Key from a LATER text wins, matching ordinary
	/// merged-dictionary override semantics (see <see cref="FrameworkDefaultResources"/> in the
	/// Microsoft WinUI host, which puts framework defaults first and the app's own resources last so
	/// the app can still override a default).
	///
	/// This exists because `{StaticResource ...}` resolves EAGERLY, at the parse the reference
	/// belongs to - it does not reach into whatever is already sitting in
	/// <c>Application.Resources</c> from a SEPARATE, earlier `XamlReader.Load` call the way
	/// `{ThemeResource ...}` does at live-tree lookup time. Two independently-built dictionaries
	/// that need to satisfy each other's StaticResource references have to be parsed together, in
	/// one call, which is what this produces the input for.
	/// </summary>
	public static string Merge(IEnumerable<string> xamlTexts, ICollection<string> errors)
	{
		var output = new XElement(Xaml + "ResourceDictionary");
		var themeDictionaries = new List<XElement>();

		foreach (var text in xamlTexts)
		{
			XElement root;
			try
			{
				root = XDocument.Parse(text, LoadOptions.SetLineInfo).Root;
			}
			catch (Exception e)
			{
				errors.Add($"A resource dictionary text could not be parsed: {e.GetBaseException().Message}");
				continue;
			}
			if (root?.Name != Xaml + "ResourceDictionary")
			{
				errors.Add("A resource dictionary text does not start with <ResourceDictionary>.");
				continue;
			}
			// No Source= resolution needed here (baseDir/visited unused in practice): every input is
			// this class's OWN prior output, which is already fully flattened - nothing left in it
			// references an external file.
			Inline(root.Elements(), output, themeDictionaries, "", new HashSet<string>(StringComparer.OrdinalIgnoreCase), errors, root);
		}

		return Finish(output, themeDictionaries);
	}

	static string Finish(XElement output, List<XElement> themeDictionaries)
	{
		if (!output.Elements().Any() && themeDictionaries.Count == 0)
		{
			return null;
		}

		var result = new XElement(Xaml + "ResourceDictionary",
			new XAttribute("xmlns", Xaml),
			new XAttribute(XNamespace.Xmlns + "x", X));
		if (themeDictionaries.Count > 0)
		{
			// Real WinUI apps commonly merge SEVERAL dictionaries that each declare their own
			// ResourceDictionary.ThemeDictionaries (WinUI-Gallery's App.xaml, Controls/CopyButton.xaml
			// and Styles/SelectorBar.xaml all define a "Light" theme, for one; the WinUI framework's
			// own *_themeresources.xaml files do the same thing among themselves) - the framework
			// resolves that by treating same-keyed theme dictionaries across every merged source as
			// one logical dictionary. Concatenating the raw <ResourceDictionary x:Key="Light">
			// elements as SIBLINGS instead - which is what this used to do - produces two elements
			// with the same x:Key in the same collection, which XamlReader rejects outright with
			// "The dictionary key 'Light' is already used", losing EVERY resource in the built XAML,
			// not just the duplicated one. Group by key and union each group's entries into a single
			// dictionary per theme name instead.
			var merged = new XElement(Xaml + "ResourceDictionary.ThemeDictionaries");
			foreach (var group in themeDictionaries.SelectMany(d => d.Elements(Xaml + "ResourceDictionary"))
				.GroupBy(d => (string)d.Attribute(X + "Key")))
			{
				var combined = new XElement(Xaml + "ResourceDictionary", new XAttribute(X + "Key", group.Key ?? ""));
				// The SAME "duplicate key" hazard as the outer Light/Dark/HighContrast grouping,
				// one level down: two DIFFERENT files can each declare an entry under the SAME
				// theme name AND the same inner x:Key (observed with the WinUI framework's own
				// curated files - an "Unknown" entry duplicated across files' "Light"
				// dictionaries). Concatenating every contributing file's raw elements the way this
				// used to reproduces the exact "key already used" failure the outer grouping fix
				// was written to prevent, just one level deeper - so the SAME last-wins dedup
				// applies to what actually lands in each merged per-theme dictionary.
				// A ResourceDictionary entry is only ever findable by key, so an "unkeyed" entry
				// here should not occur in practice - kept (not discarded) rather than assumed.
				var (deduped, unkeyedThemeEntries) = DeduplicateByKey(group.SelectMany(dictionary => dictionary.Elements()));
				combined.Add(deduped);
				combined.Add(unkeyedThemeEntries);
				merged.Add(combined);
			}
			result.Add(merged);
		}
		// Same "duplicate key" hazard as ThemeDictionaries, one level up: merging several curated
		// framework files can legitimately redeclare the same top-level x:Key (WinUI's own
		// TextBlock_themeresources_v2.5.xaml intentionally REDEFINES a handful of
		// TextBlock_themeresources.xaml's styles - BodyStrongTextBlockStyle, TitleLargeTextBlockStyle
		// - as a later, overriding layer). A plain resource dictionary rejects a repeated key exactly
		// like ThemeDictionaries does, with the same "already used" error. Last occurrence wins,
		// matching ordinary merged-dictionary override semantics; entries with no x:Key are never
		// merged away since there is no identity to compare them by.
		var (keyed, unkeyed) = DeduplicateByKey(output.Elements());
		foreach (var child in OrderByStaticResourceDependencies(keyed))
		{
			result.Add(child);
		}
		foreach (var child in unkeyed)
		{
			result.Add(child);
		}
		return result.ToString(SaveOptions.DisableFormatting);
	}

	/// <summary>Last-x:Key-occurrence-wins deduplication, splitting the survivors into keyed
	/// (original relative order preserved) and unkeyed (kept, since there is no identity to compare
	/// them by - they can never be a duplicate of anything).</summary>
	static (List<XElement> Keyed, List<XElement> Unkeyed) DeduplicateByKey(IEnumerable<XElement> elements)
	{
		var all = elements as IReadOnlyCollection<XElement> ?? elements.ToList();
		var seen = new Dictionary<string, XElement>(StringComparer.Ordinal);
		foreach (var child in all)
		{
			if (EffectiveKey(child) is { } key) seen[key] = child;
		}

		var keyed = new List<XElement>();
		var unkeyed = new List<XElement>();
		foreach (var child in all)
		{
			var key = EffectiveKey(child);
			if (key is null) { unkeyed.Add(child); continue; }
			if (ReferenceEquals(seen[key], child)) keyed.Add(child);
		}
		return (keyed, unkeyed);
	}

	/// <summary>An explicit x:Key, or - for a <c>&lt;Style TargetType="X"&gt;</c> with none - the
	/// synthetic identity WinUI itself uses for an implicit/default style: its TargetType. Real
	/// merged corpora collide on this just as often as on an explicit key - the WinUI framework's
	/// own Materials/Reveal/RevealBrush_themeresources.xaml declares an ALTERNATE unkeyed default
	/// style for ListViewItem and GridViewItem, meant to be a conditionally-merged opt-in
	/// alternative to ListViewItem_themeresources.xaml's/GridViewItem_themeresources.xaml's own
	/// unkeyed defaults - not a second one to merge unconditionally alongside them. Two unkeyed
	/// `Style TargetType="ListViewItem"` entries in the same dictionary is exactly as invalid as two
	/// `x:Key="Foo"` entries; WinUI's own diagnostic for it just names the TargetType instead of a
	/// string key, which is why this one class of duplicate does not show up as a recognizable
	/// x:Key in the error text. An element with neither an x:Key nor a Style TargetType has no
	/// identity to compare by and is left alone (kept, never deduplicated away).</summary>
	static string? EffectiveKey(XElement element)
	{
		if ((string)element.Attribute(X + "Key") is { } key) return key;
		if (element.Name == Xaml + "Style" && (string)element.Attribute("TargetType") is { Length: > 0 } targetType)
			return "Style:" + targetType;
		return null;
	}

	static readonly Regex ResourceReference = new(@"\{(?:StaticResource|ThemeResource)\s+([A-Za-z_][\w.]*)\s*\}", RegexOptions.Compiled);

	/// <summary>
	/// Reorders top-level resources so a StaticResource OR ThemeResource reference always comes
	/// AFTER the entry it points to.
	///
	/// This matters because we hand the merged text to the runtime XamlReader.Load parser, not
	/// WinUI's build-time XAML compiler. The compiler resolves both extensions in two passes and
	/// does not care about declaration order; the loose runtime parser resolves them in ONE pass
	/// and requires the referenced key to already exist. Real, shipped WinUI XAML routinely
	/// violates that ordering anyway - WinUI-Gallery's own Controls/CopyButton.xaml declares its
	/// implicit `&lt;Style TargetType="local:CopyButton"&gt;` (BasedOn="{StaticResource
	/// DefaultCopyButtonStyle}") BEFORE the named `DefaultCopyButtonStyle` it points to, and the
	/// WinUI framework's own CalendarDatePicker_themeresources.xaml reaches forward into
	/// CalendarView_themeresources.xaml the same way via `{ThemeResource
	/// DefaultCalendarViewStyle}`. Both are completely valid where every consumer's build compiles
	/// them, and unparseable here without this reordering pass.
	///
	/// ThemeResource is normally a LIVE, lazy lookup that only reaches Application.Resources once
	/// an element is in the running visual tree - not something declaration order should matter
	/// for at all. It matters here anyway because this whole dictionary is built and loaded via a
	/// single, isolated `XamlReader.Load` call with no live tree behind it yet; in that situation a
	/// forward ThemeResource reference fails to resolve exactly like a forward StaticResource one
	/// does, so both need the same ordering fix.
	///
	/// A stable pass: elements with no ordering constraint between them keep their original
	/// relative order, and an unresolvable cycle (should not occur for legitimate resources) falls
	/// back to leaving the remaining elements in their original order rather than looping forever.
	/// </summary>
	static List<XElement> OrderByStaticResourceDependencies(List<XElement> elements)
	{
		// EffectiveKey, not the raw x:Key attribute: by the time DeduplicateByKey hands this list
		// over, an unkeyed <Style TargetType="X"> already carries its own guaranteed-unique
		// synthetic identity here, and using the raw attribute instead would put a null key in this
		// dictionary for every one of them - fine for one, a crash (ToDictionary rejects duplicate
		// keys, including multiple nulls) the moment a second unkeyed style shows up.
		var keyOf = elements.ToDictionary(e => EffectiveKey(e)!, e => e, StringComparer.Ordinal);
		var dependsOn = new Dictionary<XElement, HashSet<string>>();
		foreach (var element in elements)
		{
			var references = new HashSet<string>(StringComparer.Ordinal);
			foreach (var node in element.DescendantsAndSelf())
			{
				foreach (var attribute in node.Attributes())
				{
					foreach (Match match in ResourceReference.Matches(attribute.Value))
						references.Add(match.Groups[1].Value);
				}
				if (node.Name.LocalName is "StaticResource" or "ThemeResource"
					&& (string)node.Attribute("ResourceKey") is { Length: > 0 } resourceKey)
				{
					references.Add(resourceKey);
				}
			}
			// A reference can only ever be to an EXPLICIT x:Key (StaticResource/ThemeResource name
			// forms have no notion of "by TargetType"), so self-reference removal only matters for
			// keyed elements - EffectiveKey's synthetic "Style:X" identity for an unkeyed style
			// could never appear in references in the first place.
			if ((string)element.Attribute(X + "Key") is { } ownKey) references.Remove(ownKey);
			dependsOn[element] = references;
		}

		var ordered = new List<XElement>(elements.Count);
		var placed = new HashSet<string>(StringComparer.Ordinal);
		var remaining = new List<XElement>(elements);
		while (remaining.Count > 0)
		{
			var progressed = false;
			for (var i = 0; i < remaining.Count; i++)
			{
				var candidate = remaining[i];
				var unmet = dependsOn[candidate].Where(keyOf.ContainsKey).Except(placed);
				if (unmet.Any()) continue;

				ordered.Add(candidate);
				placed.Add(EffectiveKey(candidate)!);
				remaining.RemoveAt(i);
				progressed = true;
				break;
			}
			if (!progressed)
			{
				// A genuine cycle, or a dependency this scan cannot see - append the rest as-is
				// rather than spinning forever on a corpus-driven edge case.
				ordered.AddRange(remaining);
				break;
			}
		}
		return ordered;
	}

	static void Inline(IEnumerable<XElement> children, XElement output,
		List<XElement> themeDictionaries, string baseDir, HashSet<string> visited,
		ICollection<string> errors, XElement declaringRoot)
	{
		foreach (var child in children)
		{
			if (child.Name == Xaml + "ResourceDictionary.ThemeDictionaries")
			{
				// Finish()'s merge keeps only the LEAF entries of each per-theme dictionary
				// (`combined.Add(dictionary.Elements())`) - the ThemeDictionaries wrapper and each
				// x:Key="Light"/"Dark"/... ResourceDictionary around them are discarded once their
				// contents are grouped by key. Stamping the wrapper itself would therefore stamp
				// something that never survives into the output; the entries that DO survive are
				// stamped here instead, for the same reason as the plain-resource branch below.
				foreach (var perTheme in child.Elements(Xaml + "ResourceDictionary"))
				{
					foreach (var entry in perTheme.Elements())
					{
						StampNamespaces(entry, declaringRoot);
					}
				}
				themeDictionaries.Add(child);
			}
			else if (child.Name == Xaml + "ResourceDictionary.MergedDictionaries")
			{
				foreach (var dictionary in child.Elements(Xaml + "ResourceDictionary"))
				{
					var source = (string)dictionary.Attribute("Source");
					if (string.IsNullOrEmpty(source))
					{
						Inline(dictionary.Elements(), output, themeDictionaries, baseDir, visited, errors, declaringRoot);
						continue;
					}
					var path = ResolvePath(baseDir, source);
					if (path == null)
					{
						errors.Add($"Merged dictionary source '{source}' is not a file path.");
						continue;
					}
					if (!visited.Add(path))
					{
						continue;
					}
					if (!File.Exists(path))
					{
						errors.Add($"Merged dictionary not found: {source}");
						continue;
					}
					try
					{
						var root = XDocument.Load(path, LoadOptions.SetLineInfo).Root;
						if (root?.Name == Xaml + "ResourceDictionary")
						{
							Inline(root.Elements(), output, themeDictionaries,
								Path.GetDirectoryName(path), visited, errors, root);
						}
						else
						{
							errors.Add($"Merged dictionary {source} does not start with <ResourceDictionary>.");
						}
					}
					catch (Exception e)
					{
						errors.Add($"Merged dictionary {source} could not be read: {e.GetBaseException().Message}");
					}
				}
			}
			else
			{
				// The child is about to be reparented into a combined document whose root only
				// declares the presentation/x namespaces (see Finish). Any OTHER prefix it or its
				// descendants use - xmlns:local, xmlns:controls, a converters namespace, whatever the
				// declaring file happened to bind - was resolved through THAT file's root, not
				// through anything travelling with the element itself. Without re-stamping it here,
				// a moved element like WinUI-Gallery's `<Style TargetType="local:CopyButton">`
				// fails with "Failed to create a 'System.Type' from the text 'local:CopyButton'"
				// the moment the combined document is parsed, because the prefix is simply undefined
				// in the new tree - not a type-resolution failure, a namespace-scope one.
				StampNamespaces(child, declaringRoot);
				output.Add(child);
			}
		}
	}

	/// <summary>Copies every xmlns declaration from <paramref name="declaringRoot"/> onto
	/// <paramref name="target"/> (skipping any prefix <paramref name="target"/> already declares
	/// itself), so a prefix resolved through the source file's root travels with the element when
	/// it is moved into a different document. See the call site in <see cref="Inline"/>.</summary>
	static void StampNamespaces(XElement target, XElement declaringRoot)
	{
		foreach (var attribute in declaringRoot.Attributes())
		{
			if (!attribute.IsNamespaceDeclaration || target.Attribute(attribute.Name) != null)
			{
				continue;
			}
			target.Add(new XAttribute(attribute.Name, attribute.Value));
		}
	}

	/// <summary>
	/// Extracts the design-theme names from a built app-resources XAML string: the x:Key
	/// values of the hoisted ResourceDictionary.ThemeDictionaries children ("Light", "Dark",
	/// "HighContrast" or app-specific names). Drives the designer's theme combo, so it lists
	/// exactly the themes the app under design carries. Falls back to an empty list when the
	/// XAML has none (the combo then keeps its default Light/Dark pair).
	/// </summary>
	public static List<string> GetThemeNames(string xaml)
	{
		var names = new List<string>();
		try
		{
			var themeDictionaries = XDocument.Parse(xaml).Root
				?.Element(Xaml + "ResourceDictionary.ThemeDictionaries");
			if (themeDictionaries != null)
			{
				foreach (var dictionary in themeDictionaries.Elements())
				{
					var key = (string)dictionary.Attribute(X + "Key");
					if (!string.IsNullOrEmpty(key) && !names.Contains(key))
					{
						names.Add(key);
					}
				}
			}
		}
		catch
		{
			// Malformed built XAML: the caller falls back to the default theme list.
		}
		return names;
	}

	/// <summary>Resolves a merged-dictionary Source to a file path, tolerating scheme prefixes.</summary>
	static string ResolvePath(string baseDir, string source)
	{
		string path;
		if (source.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				path = new Uri(source).LocalPath;
			}
			catch
			{
				return null;
			}
		}
		else if (source.StartsWith("ms-appx://", StringComparison.OrdinalIgnoreCase))
		{
			path = source.Substring("ms-appx:///".Length);
		}
		else if (source.StartsWith("pack://", StringComparison.OrdinalIgnoreCase)
			|| source.StartsWith("ms-appdata://", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		else
		{
			path = source;
		}
		if (string.IsNullOrEmpty(path))
		{
			return null;
		}
		try
		{
			return Path.GetFullPath(Path.Combine(baseDir, path.TrimStart('/')));
		}
		catch
		{
			return null;
		}
	}
}
