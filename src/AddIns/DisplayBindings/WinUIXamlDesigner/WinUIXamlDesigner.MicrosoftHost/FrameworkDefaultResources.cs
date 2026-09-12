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

	/// <summary>
	/// Gives the framework controls whose DEFAULT TEMPLATE natively crashes this host a design-time
	/// <c>Template</c> instead.
	///
	/// Two are known, both measured: a bare <c>&lt;AutoSuggestBox/&gt;</c> (default template hosts a
	/// <c>Popup</c>) and a bare <c>&lt;MapControl/&gt;</c> each take the child down with a native
	/// fault - <c>renderDiagnostics</c> stays empty and there is no managed exception, so they
	/// cannot be caught, only avoided. The replacement element-specific template renders instead
	/// (both measured at 1365xN). The element itself is untouched - selection, outline and the
	/// Properties pad still target it - and it renders as the designer-appropriate approximation.
	///
	/// This has to rewrite the DOCUMENT rather than add an implicit Style to
	/// Application.Resources: measured, an implicit style merged into Application.Resources at
	/// startup is not applied to these elements, while the same implicit style in Page.Resources
	/// is. Rewriting the element's own Template property is the highest-precedence form and works.
	/// </summary>
	static void ApplyDesignTimeControlTemplates(XElement root)
	{
		// Diagnostic switch (same shape as OD_DESIGNHOST_NO_SUBSTITUTE): disables the workaround so
		// the raw framework default templates run, which is how we check whether a newer
		// Windows App SDK fixed the native crash instead of masking it.
		if (Environment.GetEnvironmentVariable("OD_DESIGNHOST_NO_DESIGN_TEMPLATES") == "1")
			return;
		foreach (var element in root.DescendantsAndSelf().ToList())
		{
			var name = element.Name.LocalName;
			var ns = element.Name.Namespace;
			var content = name switch
			{
				// Design surface shows the typed text / placeholder, not the runtime suggestion popup.
				"AutoSuggestBox" => new XElement(ns + "TextBox",
					new XAttribute(X + "Name", "TextBox"),
					new XAttribute("Text", "{TemplateBinding Text}"),
					new XAttribute("PlaceholderText", "{TemplateBinding PlaceholderText}")),
				// A map needs a service token and network; the design surface shows a neutral placeholder.
				"MapControl" => new XElement(ns + "Border",
					new XAttribute("Background", "#FFE5E5E5"),
					new XElement(ns + "TextBlock",
						new XAttribute("Text", "Map (design-time placeholder)"),
						new XAttribute("HorizontalAlignment", "Center"),
						new XAttribute("VerticalAlignment", "Center"),
						new XAttribute("Foreground", "#FF808080"))),
				// RefreshContainer's default template presents nothing offscreen (an empty pull-to-
				// refresh gesture surface), so its content measured 0x0. A passthrough presenter makes
				// the content - eg WinUI-Gallery's 200px list - actually appear.
				"RefreshContainer" => new XElement(ns + "ContentPresenter",
					new XAttribute("Content", "{TemplateBinding Content}")),
				_ => null,
			};
			if (content is null)
				continue;
			var templateProperty = ns + name + ".Template";
			// Never override a Template the document already sets.
			if (element.Elements(templateProperty).Any())
				continue;
			element.Add(new XElement(templateProperty,
				new XElement(ns + "ControlTemplate",
					new XAttribute("TargetType", name),
					content)));
		}
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
			xaml = ApplyAccentPalette(xaml);
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

	/// <summary>
	/// Resolves the OS accent palette (<c>SystemAccentColor</c> + Light1-3/Dark1-3) and bakes it
	/// into the vendored theme markup.
	///
	/// The vendored theme files REFERENCE these seven keys (eight of them do) but none DEFINES
	/// them: on a real device the framework merges the OS's current accent colour into
	/// Application.Resources at startup, which is why a generic.xaml-style file only ever
	/// references them. This host deliberately does not use the framework's own resource
	/// pipeline (XamlControlsResources throws 0x8000FFFF unpackaged - see the class header), so
	/// the framework never performs that merge. Reading the palette back through
	/// <c>UISettings</c> is the honest fix: it exposes exactly these seven slots, so the design
	/// surface shows the user's real accent rather than a guess.
	///
	/// They are not merely cosmetic, and only ONE page in 125 ever noticed: a ResourceDictionary
	/// entry is instantiated LAZILY, so the accent AcrylicBrushes that carry
	/// <c>TintColor="{ThemeResource SystemAccentColorDark1}"</c> are never built - and never
	/// fail - until a page asks for one. WinUI-Gallery's XamlStylesPage is the one page that does
	/// (<c>Background="{ThemeResource AccentAcrylicBackgroundFillColorDefaultBrush}"</c>); with
	/// the references unresolved it failed with "Failed to assign to property
	/// '...AcrylicBrush.TintColor'" - the missing colour surfacing as an assignment failure on
	/// the brush being constructed.
	///
	/// PLACEMENT IS LOAD-BEARING. The key definitions go in as the first CONTENT items, after any
	/// property element (<c>&lt;ResourceDictionary.ThemeDictionaries&gt;</c>). XAML does not allow
	/// a property element once content has started, so inserting them ahead of ThemeDictionaries
	/// makes the WHOLE merged dictionary unparseable. That failure is silent in the worst way:
	/// Install swallows it and logs, Application.Resources ends up with no framework tokens at
	/// all, and every page in the corpus then breaks with the very error this method exists to
	/// fix. Measured: 119 pages that rendered fine regressed to the identical TintColor failure
	/// until the placement was corrected.
	/// </summary>
	static string ApplyAccentPalette(string xaml)
	{
		XDocument document;
		try { document = XDocument.Parse(xaml); }
		catch { return xaml; }

		var palette = ResolveAccentPalette();

		// 1. Bake every accent reference into a literal. Resource entries are instantiated LAZILY,
		//    so an accent AcrylicBrush is only built when a page asks for it; resolving the
		//    {ThemeResource} while that brush is being constructed is brittle across SDK versions -
		//    Windows App SDK 2.4 reports "Failed to assign to property '...AcrylicBrush.TintColor'"
		//    even though the key is declared in the same theme dictionary. A design host needs the
		//    value, not the indirection, so the literal removes the failure mode entirely.
		var rewritten = 0;
		foreach (var attribute in document.Descendants().Attributes())
		{
			foreach (var key in SystemAccentColorKeys)
			{
				if (attribute.Value == "{ThemeResource " + key + "}" || attribute.Value == "{StaticResource " + key + "}")
				{
					attribute.Value = palette[key];
					rewritten++;
					break;
				}
			}
		}

		// 2. Also define the keys (as Color) in the root and every per-theme dictionary, for
		//    anything that looks them up by key rather than through a rewritten attribute.
		var root = document.Root!;
		var added = DefineAccentColorsIn(root, palette);
		foreach (var themeDictionary in root.Elements(Xaml + "ResourceDictionary.ThemeDictionaries")
			.Elements(Xaml + "ResourceDictionary"))
		{
			added += DefineAccentColorsIn(themeDictionary, palette);
		}

		if (rewritten == 0 && added == 0) return xaml;
		Console.Error.WriteLine($"design-host: accent palette applied ({rewritten} reference(s) resolved, {added} key(s) defined).");
		return document.ToString(SaveOptions.DisableFormatting);
	}

	/// <summary>The OS accent palette, keyed by the seven SystemAccentColor* names. Falls back to
	/// WinUI's default accent when UISettings is unavailable (some host contexts have no settings
	/// service), so the markup is always complete.</summary>
	static Dictionary<string, string> ResolveAccentPalette()
	{
		var palette = new Dictionary<string, string>(StringComparer.Ordinal);
		try
		{
			var settings = new Windows.UI.ViewManagement.UISettings();
			palette["SystemAccentColor"] = FormatColor(settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent));
			palette["SystemAccentColorLight1"] = FormatColor(settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight1));
			palette["SystemAccentColorLight2"] = FormatColor(settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight2));
			palette["SystemAccentColorLight3"] = FormatColor(settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight3));
			palette["SystemAccentColorDark1"] = FormatColor(settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentDark1));
			palette["SystemAccentColorDark2"] = FormatColor(settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentDark2));
			palette["SystemAccentColorDark3"] = FormatColor(settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentDark3));
		}
		catch (Exception e)
		{
			Console.Error.WriteLine($"design-host: UISettings accent unavailable ({e.GetBaseException().Message}); using the default accent.");
		}
		foreach (var key in SystemAccentColorKeys)
		{
			if (!palette.ContainsKey(key)) palette[key] = DefaultAccentColor;
		}
		return palette;
	}

	static string FormatColor(Windows.UI.Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

	/// <summary>Adds any missing accent key to one dictionary scope, as its first CONTENT item -
	/// after every property element, never before one (see the caller's remarks on why that
	/// ordering is load-bearing).</summary>
	static int DefineAccentColorsIn(XElement dictionary, IReadOnlyDictionary<string, string> palette)
	{
		var existing = dictionary.Elements()
			.Select(e => (string?)e.Attribute(X + "Key"))
			.Where(k => k != null)
			.ToHashSet(StringComparer.Ordinal);
		var lastPropertyElement = dictionary.Elements()
			.LastOrDefault(e => e.Name.LocalName.Contains('.', StringComparison.Ordinal));

		var added = 0;
		foreach (var key in SystemAccentColorKeys.Reverse())
		{
			if (existing.Contains(key)) continue;
			var entry = new XElement(Xaml + "Color", new XAttribute(X + "Key", key), palette[key]);
			if (lastPropertyElement is null) dictionary.AddFirst(entry);
			else lastPropertyElement.AddAfterSelf(entry);
			added++;
		}
		return added;
	}

	/// <summary>WinUI's own default accent. Only its EXISTENCE matters at design time - the real
	/// shade-derivation algorithm is not reproduced, so all seven keys share this one value.</summary>
	const string DefaultAccentColor = "#FF0078D4";

	static readonly string[] SystemAccentColorKeys = {
		"SystemAccentColor",
		"SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
		"SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
	};

	static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	/// <summary>DesignHost.TransformXamlBeforeLoad implementation - see the class summary's
	/// problem #2. Dispatches on the incoming text's root: a plain ResourceDictionary (app/resources
	/// builds always are one) merges directly; anything else (a page, a toolbox item's element
	/// template) gets the tokens injected as a MergedDictionaries entry under its own
	/// `&lt;Root.Resources&gt;`.</summary>
	static string Transform(string xaml) => Combine(xaml);

	static string Combine(string xaml)
	{
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

		if (mergedXaml is null)
		{
			return xaml;
		}

		// XamlReader.Load is a runtime parser, whereas x:Class is consumed only by
		// generated InitializeComponent/LoadComponent code. A design host has no generated partial
		// for the user's page, so forwarding this directive makes every normal WinUI page fail with
		// xClassCanOnlyBeUsedOnLoadComponent. Strip it from the WHOLE document, not just the root:
		// a nested x:Class (or one that arrives on a merged dictionary's root) triggers the same
		// parser error, and the directive means nothing to a runtime parse wherever it appears.
		foreach (var element in root.DescendantsAndSelf())
		{
			element.Attribute(X + "Class")?.Remove();
		}

		if (root.Name == Xaml + "ResourceDictionary")
		{
			var errors = new List<string>();
			var merged = AppResourceBuilder.Merge(new[] { mergedXaml, xaml }, errors);
			foreach (var error in errors)
				Console.Error.WriteLine($"design-host: default theme resource merge: {error}");
			return merged ?? xaml;
		}

		ApplyDesignTimeControlTemplates(root);
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
