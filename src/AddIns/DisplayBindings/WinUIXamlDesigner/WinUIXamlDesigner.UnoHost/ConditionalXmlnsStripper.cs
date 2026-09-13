using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// Strips WinUI's compile-time <c>IXamlCondition</c> query string
	/// (<c>xmlns:newExp="...presentation?cond:FeatureFlagCondition(NewExperience)"</c>) out of
	/// every namespace declaration, leaving the plain namespace URI it decorates.
	///
	/// Conditional XAML (see WinUI-Gallery's CustomXamlConditionalsPage) is compile-time only: the
	/// XAML compiler evaluates the referenced <c>IXamlCondition</c> against build-time feature
	/// flags and either keeps or drops the element/attribute entirely before generating
	/// <c>InitializeComponent</c> - the emitted code never mentions the condition again. A runtime
	/// <c>XamlReader.Load</c> has no compiler pass to do that evaluation, and WinRT's native parser
	/// does not treat "?cond:..." as an extension it can ignore either: it fails the WHOLE document
	/// with a bare "External component has thrown an exception" - no line number, no mention of the
	/// namespace URI that upset it, which is what made this look like a native crash on some
	/// unrelated element until the URI syntax itself was the suspect.
	///
	/// This runs on the RAW TEXT, before <c>XDocument.Parse</c> - not as an XElement rewrite like
	/// the other repairs in this file's siblings. An element's resolved namespace name is baked in
	/// at PARSE time from whatever the nearest xmlns declaration says; editing an
	/// <see cref="System.Xml.Linq.XAttribute"/>'s value after parsing does not retroactively change
	/// the <see cref="System.Xml.Linq.XName"/> every element under that scope already resolved to,
	/// so the elements would still carry the untouched "?cond:..." namespace and the serializer
	/// would just re-synthesize an equivalent declaration to keep them valid - silently undoing the
	/// edit. Stripping the marker before anything is parsed is what makes the rest of the pipeline
	/// (which operates on the parsed DOM) see only ordinary, resolvable namespaces.
	///
	/// The repair itself is the same shape as CompileTimeBindingStripper's: drop what only the
	/// compiler could evaluate, and show the structure instead of failing outright. Mapping the
	/// prefix back to the condition's own base namespace - rather than deleting the
	/// conditionally-qualified content - renders every conditional branch as if its condition were
	/// true, the closest a design-time preview can get to "show me the markup" for something the
	/// runtime parser cannot evaluate.
	/// </summary>
	static class ConditionalXmlnsStripper
	{
		// Matches an xmlns declaration's value up to (not including) "?cond:", inside a quoted
		// attribute value. Deliberately anchored on "xmlns" (not just any attribute) so a `?cond:`
		// substring appearing in ordinary text content is never touched. Capture 1 is the prefix
		// name, so the same pass can also find that prefix's attribute usages below.
		static readonly Regex Pattern = new(
			"xmlns(?::(\\w+))?\\s*=\\s*\"[^\"?]*\\?cond:[^\"]*\"",
			RegexOptions.Compiled);

		/// <summary>Returns <paramref name="xaml"/> with every conditional namespace declaration's
		/// query string removed, and every conditional-prefixed ATTRIBUTE dropped, or unchanged when
		/// none is present.</summary>
		public static string Strip(string xaml, out int stripped)
		{
			var count = 0;
			var prefixes = new List<string>();
			foreach (Match match in Pattern.Matches(xaml))
			{
				if (match.Groups[1].Success)
				{
					prefixes.Add(match.Groups[1].Value);
				}
				count++;
			}

			var result = Pattern.Replace(xaml, match => match.Groups[1].Success
				? "xmlns:" + match.Groups[1].Value + "=\"" + PlainNamespaceOf(match.Value) + "\""
				: "xmlns=\"" + PlainNamespaceOf(match.Value) + "\"");

			// Conditional ATTRIBUTES (newExp:Background, legacy:Background, ...) cannot be evaluated by
			// a runtime parser, and once the namespace rewrite above collapses both conditions onto the
			// SAME presentation namespace, two mutually-exclusive conditions on one element become a
			// DUPLICATE attribute name - which fails the whole document before any other repair runs
			// (measured: WinUI-Gallery's CustomXamlConditionalsPage). Drop them the way x:Bind is
			// dropped: show the structure, not the compile-time evaluation. Conditional ELEMENTS
			// (newExp:InfoBar, legacy:Setter) are kept by the namespace rewrite. The prefix is matched
			// only at an attribute position (preceded by whitespace inside a tag), so an element name
			// like <newExp:InfoBar> is not touched.
			foreach (var prefix in prefixes.Distinct(StringComparer.Ordinal))
			{
				var attributePattern = new Regex(
					"\\s+" + Regex.Escape(prefix) + ":[A-Za-z_][\\w.-]*\\s*=\\s*\"[^\"]*\"",
					RegexOptions.Compiled);
				result = attributePattern.Replace(result, _ => { count++; return ""; });
			}

			stripped = count;
			return result;
		}

		/// <summary>Ends at the first '?', then trims the trailing quote - the plain namespace URI the
		/// conditional query string was decorating.</summary>
		static string PlainNamespaceOf(string declaration)
		{
			var equals = declaration.IndexOf('=');
			var value = declaration.Substring(equals + 1).Trim().Trim('"');
			var query = value.IndexOf('?');
			return query < 0 ? value : value.Substring(0, query);
		}
	}
}
