using System.Xml.Linq;
using ICSharpCode.WinUIXamlDesigner.UnoDesignHost;
using ICSharpCode.WinUIXamlDesigner.UnoHost;
using Microsoft.UI.Xaml;

namespace ICSharpCode.WinUIXamlDesigner.MicrosoftHost;

/// <summary>
/// Installs the WinUI framework's own default theme resources (Fluent v2 color/brush palette,
/// default text and control styles) so the design host can parse markup that assumes they exist.
///
/// See DefaultThemeResources/README.md for the full story of WHAT these files are and why they are
/// hand-authored XAML rather than the native <c>XamlControlsResources</c> type. This class handles
/// TWO separate problems that both stem from the same root cause:
///
///  1. Control TEMPLATES compiled into the framework's own generic.xaml reference these tokens via
///     `{ThemeResource ...}`, which resolves lazily by walking up the LIVE element's ambient
///     resource scope to Application.Resources. <see cref="Install"/> covers this: merge the
///     tokens once and add the result to Application.Resources.MergedDictionaries at startup.
///
///  2. A project's OWN markup (App.xaml, a page, an inserted toolbox element) can also reference a
///     token via `{StaticResource ...}`, which resolves EAGERLY at the parse it belongs to and
///     does NOT reach into Application.Resources from a separate, earlier XamlReader.Load call -
///     no amount of populating Application.Resources ahead of time helps. <see cref="Attach"/>
///     covers this by installing a DesignHost.TransformXamlBeforeLoad hook that merges the
///     framework tokens into the SAME text being parsed, every time, before it reaches
///     XamlReader.Load.
/// </summary>
static class FrameworkDefaultResources
{
	static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

	static string? mergedXaml;

	/// <summary>Builds the merged dictionary from the embedded curated files (once) and installs it
	/// into <paramref name="appResources"/> for ThemeResource-based lookups, AND wires up
	/// DesignHost.TransformXamlBeforeLoad for StaticResource-based lookups. Call once at
	/// startup, before any document can be parsed. Errors are reported to stderr rather than
	/// thrown - a missing default style should degrade a handful of lookups, not take down the
	/// whole child the way a failed session/open would.</summary>
	public static void Install(ResourceDictionary appResources)
	{
		mergedXaml = Build();
		if (mergedXaml is null)
		{
			return;
		}

		try
		{
			var dictionary = (ResourceDictionary)Microsoft.UI.Xaml.Markup.XamlReader.Load(mergedXaml);
			appResources.MergedDictionaries.Add(dictionary);
		}
		catch (Exception e)
		{
			Console.Error.WriteLine($"design-host: failed to install default theme resources into Application.Resources: {e.GetBaseException().Message}");
		}

		DesignHost.TransformXamlBeforeLoad = Transform;
	}

	static string? Build()
	{
		var assembly = typeof(FrameworkDefaultResources).Assembly;
		var names = assembly.GetManifestResourceNames()
			.Where(n => n.Contains(".DefaultThemeResources.") && n.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
			.OrderBy(n => n, StringComparer.Ordinal)
			.ToArray();
		if (names.Length == 0)
		{
			Console.Error.WriteLine("design-host: no DefaultThemeResources embedded resources found - Fluent v2 tokens will not resolve.");
			return null;
		}

		var tempFiles = new List<string>();
		try
		{
			// BuildFromDictionaryFiles takes file paths - materialize each embedded resource to a
			// temp file rather than teaching that engine a second, stream-based input mode for a
			// one-time startup cost.
			foreach (var name in names)
			{
				using var stream = assembly.GetManifestResourceStream(name)!;
				var path = Path.Combine(Path.GetTempPath(), $"opendevelop-theme-{Guid.NewGuid():N}.xaml");
				using (var file = File.Create(path)) stream.CopyTo(file);
				tempFiles.Add(path);
			}

			var errors = new List<string>();
			var xaml = AppResourceBuilder.BuildFromDictionaryFiles(tempFiles, errors);
			foreach (var error in errors)
				Console.Error.WriteLine($"design-host: default theme resource skipped: {error}");

			if (xaml is null)
			{
				Console.Error.WriteLine("design-host: default theme resources produced no content.");
				return null;
			}
			xaml = PruneUnresolvableTargetTypes(xaml);
			Console.Error.WriteLine($"design-host: built default theme resources from {names.Length} file(s).");
			return xaml;
		}
		catch (Exception e)
		{
			Console.Error.WriteLine($"design-host: failed to build default theme resources: {e.GetBaseException().Message}");
			return null;
		}
		finally
		{
			foreach (var path in tempFiles)
			{
				try { File.Delete(path); } catch { /* best effort */ }
			}
		}
	}

	// The same fixed clr-namespace candidate list WinUI's own markup compiler tries, in order, for
	// a bare (unprefixed) type name in the default presentation xmlns - observed empirically from
	// the sequence of GetXamlType(string) calls WinUI itself makes through
	// ReflectionXamlMetadataProvider while resolving a name like "AcrylicBrush".
	static readonly string[] DefaultNamespaceCandidates = {
		"Microsoft.UI.Xaml.Controls", "Microsoft.UI.Xaml.Data", "Microsoft.UI.Xaml",
		"Microsoft.UI.Xaml.Controls.Primitives", "Microsoft.UI.Xaml.Automation",
		"Microsoft.UI.Xaml.Shapes", "Microsoft.UI.Xaml.Media.Media3D",
		"Microsoft.UI.Xaml.Media.Imaging", "Microsoft.UI.Xaml.Media.Animation", "Microsoft.UI.Xaml.Media",
	};

	static bool BareTypeExists(string name)
		=> DefaultNamespaceCandidates.Any(ns => AppDomain.CurrentDomain.GetAssemblies()
			.Any(a => { try { return a.GetType($"{ns}.{name}", throwOnError: false) != null; } catch { return false; } }));

	/// <summary>
	/// Drops top-level Style/ControlTemplate entries whose TargetType is a BARE (unprefixed) name
	/// that does not resolve in this environment.
	///
	/// This is not hypothetical either: <c>MenuFlyout_themeresources.xaml</c>'s own default styles
	/// reference <c>TargetType="SplitMenuFlyoutItem"</c>, a control that plain reflection cannot
	/// find in this WindowsAppSDK install - it may be internal-only, or removed since the file was
	/// vendored. XamlReader.Load has no graceful-degradation mode for an unresolvable TargetType
	/// ("Failed to create a 'System.Type' from the text '...'"), and it is not scoped to the one
	/// style that used it - the WHOLE merged dictionary fails to parse, taking down every session,
	/// not just ones that would have used SplitMenuFlyoutItem. Dropping just the offending
	/// styles keeps the rest of that file's real, useful content (its OTHER default styles) instead
	/// of excluding the whole file over one bad reference.
	///
	/// Deliberately does NOT touch prefixed TargetTypes (`local:X`, `controls:X`, ...): those are
	/// either real framework types or the designed app's own types, both already resolved through
	/// the full metadata provider elsewhere, so a false negative here would silently discard
	/// legitimate content instead of a genuinely broken reference.
	/// </summary>
	static string PruneUnresolvableTargetTypes(string xaml)
	{
		XDocument document;
		try { document = XDocument.Parse(xaml); }
		catch { return xaml; }

		var root = document.Root!;
		var dropped = 0;
		foreach (var element in root.Elements().ToList())
		{
			var targetType = (string?)element.Attribute("TargetType");
			if (targetType is not { Length: > 0 } || targetType.Contains(':')) continue;
			if (BareTypeExists(targetType)) continue;

			var key = (string?)element.Attribute(X + "Key");
			Console.Error.WriteLine($"design-host: dropping default {element.Name.LocalName} for unresolvable TargetType '{targetType}'" + (key is null ? "" : $" (x:Key='{key}')"));
			element.Remove();
			dropped++;
		}
		return dropped == 0 ? xaml : document.ToString(SaveOptions.DisableFormatting);
	}

	static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	/// <summary>DesignHost.TransformXamlBeforeLoad implementation - see the class summary's
	/// problem #2. Dispatches on the incoming text's root: a plain ResourceDictionary (app/resources
	/// builds always are one) merges directly; anything else (a page, a toolbox item's element
	/// template) gets the tokens injected as a MergedDictionaries entry under its own
	/// `&lt;Root.Resources&gt;`.</summary>
	static string Transform(string xaml)
	{
		if (mergedXaml is null)
		{
			return xaml;
		}

		XElement root;
		try
		{
			root = XDocument.Parse(xaml).Root!;
		}
		catch
		{
			// Let the real XamlReader.Load report the parse error - do not mask it with a
			// different exception from here.
			return xaml;
		}

		// XamlReader.Load is a runtime parser, whereas x:Class is consumed only by
		// generated InitializeComponent/LoadComponent code.  A design host has no
		// generated partial for the user's page, so forwarding this directive makes
		// every normal WinUI page fail with xClassCanOnlyBeUsedOnLoadComponent.
		root.Attribute(X + "Class")?.Remove();

		if (root.Name == Xaml + "ResourceDictionary")
		{
			var errors = new List<string>();
			var merged = AppResourceBuilder.Merge(new[] { mergedXaml, xaml }, errors);
			foreach (var error in errors)
				Console.Error.WriteLine($"design-host: default theme resource merge: {error}");
			return merged ?? xaml;
		}

		return InjectIntoElementResources(root);
	}

	/// <summary>
	/// For a non-ResourceDictionary root (Page, UserControl, Grid, a bare Button from a toolbox
	/// template, ...): every FrameworkElement carries a Resources property, expressed in XAML as
	/// `&lt;RootTag.Resources&gt;`. Adds the framework defaults there as the FIRST
	/// (lowest-priority) MergedDictionaries entry, preserving whatever the document already
	/// declares - which still wins on a duplicate key, exactly like the app/resources merge path.
	/// </summary>
	static string InjectIntoElementResources(XElement root)
	{
		var resourcesName = XName.Get(root.Name.LocalName + ".Resources", root.Name.NamespaceName);
		var frameworkEntry = XElement.Parse(mergedXaml!);

		var existing = root.Element(resourcesName);
		if (existing is null)
		{
			var dictionary = new XElement(Xaml + "ResourceDictionary",
				new XElement(Xaml + "ResourceDictionary.MergedDictionaries", frameworkEntry));
			root.AddFirst(new XElement(resourcesName, dictionary));
		}
		else
		{
			// The property element's content may or may not already be wrapped in an explicit
			// <ResourceDictionary> - WinUI's XAML grammar allows the shorthand of listing resource
			// entries directly under <Root.Resources> because ResourceDictionary is its content
			// property, and the parsed tree reflects whichever form the author actually wrote.
			var existingChildren = existing.Elements().ToList();
			var dictionary = existingChildren is [var only] && only.Name == Xaml + "ResourceDictionary"
				? only
				: new XElement(Xaml + "ResourceDictionary", existingChildren);

			var mergedDictionaries = dictionary.Element(Xaml + "ResourceDictionary.MergedDictionaries");
			if (mergedDictionaries is null)
			{
				mergedDictionaries = new XElement(Xaml + "ResourceDictionary.MergedDictionaries");
				dictionary.AddFirst(mergedDictionaries);
			}
			mergedDictionaries.AddFirst(frameworkEntry);

			existing.RemoveNodes();
			existing.Add(dictionary);
		}

		return root.Document!.ToString(SaveOptions.DisableFormatting);
	}
}
