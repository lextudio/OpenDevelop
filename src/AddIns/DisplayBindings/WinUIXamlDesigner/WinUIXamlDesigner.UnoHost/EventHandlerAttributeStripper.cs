using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// Drops event-handler attributes (<c>Click="OnClick"</c>, <c>SuggestionChosen="..."</c>, ...)
	/// before the runtime parser sees them.
	///
	/// A compiled app wires these through the generated <c>IComponentConnector.Connect</c>, which a
	/// design host never has - it parses the page's raw source with a bare
	/// <c>XamlReader.Load(text)</c>, and that overload cannot resolve an event handler AT ALL: there
	/// is no partial class to look the named method up on, compiled framework control or app control
	/// alike. The parser does not say so - it reports the attribute as an unknown PROPERTY ("The
	/// property 'SuggestionChosen' was not found in type 'AutoSuggestBox'"), which reads like a
	/// metadata gap and sent an earlier pass of this investigation looking for one that does not
	/// exist. Confirmed by grepping the same attribute name across unrelated pages/types
	/// (AutoSuggestBox.SuggestionChosen, Button.ContextRequested, AnnotatedScrollBar's own
	/// DetailLabelRequested) - a metadata gap would be type-specific; this fails identically for
	/// framework types that otherwise resolve fine.
	///
	/// Detection has to be by REFLECTION, not by attribute-name heuristics (there is no naming
	/// convention that reliably separates an event from a same-shaped string property): an element
	/// is resolved to its CLR type the same way <see cref="CompiledXamlControlSubstituter"/> and the
	/// Microsoft host's metadata provider do, and an attribute is dropped only when
	/// <c>Type.GetEvent</c> actually finds it. A type that cannot be resolved here (Uno host, or a
	/// namespace this file does not know) is left alone - false negatives just reproduce the
	/// original "property not found", never a wrong strip.
	/// </summary>
	static class EventHandlerAttributeStripper
	{
		static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

		/// <summary>CLR namespaces the default XAML xmlns maps onto, controls first. Kept in sync
		/// with the Microsoft host's own list (ReflectionXamlMetadata.FrameworkNamespaces) - this
		/// one only needs to be good enough to find a type's events, not to fully resolve XAML's
		/// generic/attached-property syntax.</summary>
		static readonly string[] FrameworkNamespaces = [
			"Microsoft.UI.Xaml.Controls",
			"Microsoft.UI.Xaml.Controls.Primitives",
			"Microsoft.UI.Xaml",
			"Microsoft.UI.Xaml.Shapes",
			"Microsoft.UI.Xaml.Media",
			"Microsoft.UI.Xaml.Media.Animation",
			"Microsoft.UI.Xaml.Media.Imaging",
			"Microsoft.UI.Xaml.Documents",
			"Microsoft.UI.Xaml.Data",
			"Microsoft.UI.Xaml.Input",
			"Microsoft.UI.Xaml.Automation",
			"Microsoft.UI.Xaml.Navigation",
		];

		// Memoized across the whole document (and across documents - types never unload): without
		// this, every element re-walks every loaded assembly under up to 12 candidate namespaces,
		// which on a real page's COMBINED document (the page plus injected theme/resource XAML,
		// routinely 100K+ characters) took long enough to read as a hung render rather than a slow
		// one - the first version of this file had no cache and its first real-corpus run never
		// returned. XName/full-name lookups only ever ADD entries, so no invalidation is needed.
		static readonly object cacheGate = new();
		static readonly Dictionary<XName, Type?> byElementName = new();
		static readonly Dictionary<string, Type?> byFullName = new(StringComparer.Ordinal);

		/// <summary>Rewrites <paramref name="root"/> in place, returning how many event handler
		/// attributes were dropped.</summary>
		public static int Strip(XElement root)
		{
			var stripped = 0;
			foreach (var element in root.DescendantsAndSelf().ToList())
			{
				if (element.Name.LocalName.Contains('.', StringComparison.Ordinal))
				{
					// A property element, not an object - its own attributes belong to whatever it
					// is a property OF, resolved when that ancestor element is visited.
					continue;
				}
				var type = ResolveType(element.Name);
				if (type is null)
				{
					continue;
				}
				foreach (var attribute in element.Attributes().ToList())
				{
					if (attribute.IsNamespaceDeclaration
						|| attribute.Name.Namespace == X
						|| attribute.Name.LocalName.Contains('.', StringComparison.Ordinal))
					{
						continue;
					}
					if (type.GetEvent(attribute.Name.LocalName,
						BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy) is null)
					{
						continue;
					}
					attribute.Remove();
					stripped++;
				}
			}
			return stripped;
		}

		/// <summary>Resolves an element to its CLR type: an app type via its <c>using:</c>/
		/// <c>clr-namespace:</c> declaration, or a framework control via the same short-name search
		/// <see cref="CompiledXamlControlSubstituter"/> and the Microsoft host's metadata provider
		/// use. Returns null rather than guessing - see the type-level remarks.</summary>
		static Type? ResolveType(XName name)
		{
			lock (cacheGate)
			{
				if (byElementName.TryGetValue(name, out var cached))
				{
					return cached;
				}
				var resolved = ResolveTypeCore(name);
				byElementName[name] = resolved;
				return resolved;
			}
		}

		static Type? ResolveTypeCore(XName name)
		{
			var declared = name.NamespaceName;
			string? clrNamespace = null;
			if (declared.StartsWith("using:", StringComparison.Ordinal))
			{
				clrNamespace = declared.Substring("using:".Length);
			}
			else if (declared.StartsWith("clr-namespace:", StringComparison.Ordinal))
			{
				clrNamespace = declared.Substring("clr-namespace:".Length).Split(';')[0];
			}
			if (!string.IsNullOrEmpty(clrNamespace))
			{
				return FindLoaded(clrNamespace + "." + name.LocalName);
			}
			// The default presentation xmlns: try the framework namespaces a short name is
			// conventionally found in.
			foreach (var candidate in FrameworkNamespaces)
			{
				if (FindLoaded(candidate + "." + name.LocalName) is { } found)
				{
					return found;
				}
			}
			return null;
		}

		static Type? FindLoaded(string fullName)
		{
			lock (cacheGate)
			{
				if (byFullName.TryGetValue(fullName, out var cached))
				{
					return cached;
				}
				Type? resolved = null;
				foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
				{
					try
					{
						if (assembly.GetType(fullName, throwOnError: false) is { } found) { resolved = found; break; }
					}
					catch { /* a broken or partially-loaded assembly must not fail the whole lookup */ }
				}
				byFullName[fullName] = resolved;
				return resolved;
			}
		}
	}
}
