using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
			Inline(entries, output, themeDictionaries, baseDir, visited, errors);
		}

		if (!output.Elements().Any() && themeDictionaries.Count == 0)
		{
			return null;
		}

		var result = new XElement(Xaml + "ResourceDictionary",
			new XAttribute("xmlns", Xaml),
			new XAttribute(XNamespace.Xmlns + "x", X));
		if (themeDictionaries.Count > 0)
		{
			var merged = new XElement(Xaml + "ResourceDictionary.ThemeDictionaries");
			foreach (var dictionary in themeDictionaries)
			{
				merged.Add(dictionary.Elements());
			}
			result.Add(merged);
		}
		foreach (var child in output.Elements())
		{
			result.Add(child);
		}
		return result.ToString(SaveOptions.DisableFormatting);
	}

	static void Inline(IEnumerable<XElement> children, XElement output,
		List<XElement> themeDictionaries, string baseDir, HashSet<string> visited,
		ICollection<string> errors)
	{
		foreach (var child in children)
		{
			if (child.Name == Xaml + "ResourceDictionary.ThemeDictionaries")
			{
				themeDictionaries.Add(child);
			}
			else if (child.Name == Xaml + "ResourceDictionary.MergedDictionaries")
			{
				foreach (var dictionary in child.Elements(Xaml + "ResourceDictionary"))
				{
					var source = (string)dictionary.Attribute("Source");
					if (string.IsNullOrEmpty(source))
					{
						Inline(dictionary.Elements(), output, themeDictionaries, baseDir, visited, errors);
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
								Path.GetDirectoryName(path), visited, errors);
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
				output.Add(child);
			}
		}
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
