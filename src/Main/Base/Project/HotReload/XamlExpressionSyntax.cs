using System;
using System.Text.RegularExpressions;

namespace ICSharpCode.SharpDevelop.Project.HotReload
{
	/// <summary>
	/// Whether an XAML attribute value is a C# expression in the syntax of .NET MAUI, which the LibreWPF XAML source generator
	/// follows: a braced value that is not a markup extension (<c>{Items.Count}</c>, <c>{= Foo}</c>, <c>{this.Foo}</c>,
	/// <c>{$'...'}</c>, <c>{(s, e) =&gt; ...}</c>). It mirrors <c>XamlExpressions.TryGetExpression</c> in openavalon's
	/// <c>LibreWPF.Xaml.SourceGenerator</c>, which this assembly cannot reference, and must change with it.
	/// A value the generator would read as a markup extension but this reads as an expression costs a rebuild; the other
	/// way round loses a Hot Reload edit, so the lists below lean towards "expression".
	/// </summary>
	public static class XamlExpressionSyntax
	{
		// The unprefixed markup extensions of the presentation namespace; a prefixed name is always a markup extension.
		static readonly string[] MarkupExtensions = {
			"Binding", "MultiBinding", "PriorityBinding", "StaticResource", "DynamicResource", "TemplateBinding",
			"RelativeSource", "ComponentResourceKey", "ThemeDictionary",
		};

		static readonly Regex LeadingName = new Regex(@"^(?<prefix>[A-Za-z_]\w*:)?(?<name>[A-Za-z_][\w.]*)", RegexOptions.Compiled);

		public static bool IsExpression(string value)
		{
			if (value == null)
				return false;
			var text = value.Trim();
			if (text.Length < 3 || text[0] != '{' || text[text.Length - 1] != '}' || text[1] == '}')
				return false;
			var inner = text.Substring(1, text.Length - 2).Trim();
			if (inner.Length == 0)
				return false;
			if (inner[0] == '=')
				return inner.Substring(1).Trim().Length > 0;
			var match = LeadingName.Match(inner);
			if (!match.Success)
				return true;
			if (match.Groups["prefix"].Success)
				return false;
			var name = match.Groups["name"].Value;
			if (name.EndsWith("Extension", StringComparison.Ordinal))
				name = name.Substring(0, name.Length - "Extension".Length);
			return Array.IndexOf(MarkupExtensions, name) < 0;
		}
	}
}
