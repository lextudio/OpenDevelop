using System.Reflection;
using ICSharpCode.SharpDevelop.Project.HotReload;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// "Is this attribute value a C# expression" (the syntax of .NET MAUI that the LibreWPF XAML source generator follows) is decided in four
/// codebases: the generator (openavalon), OpenDevelop Base, WpfDesigner's XamlDom and winappvsce's WinUiXaml.Xaml. They must agree, or
/// the editor colours what the build reads as a markup extension. This runs one table through each copy that is built here.
/// The two copies this test project references are always checked; the generator and winappvsce are loaded from their build
/// output when it exists (build them first), and each missing one is reported, not skipped silently.
/// </summary>
public class ExpressionRuleConsistencyTests
{
	static readonly string[] Values = {
		"{Title}", "{= Title}", "{=Title}", "{this.Title}", "{.Title}", "{Items.Count}", "{!IsActive}", "{$'{First} {Last}'}",
		"{(s, e) => Count++}", "{async (s, e) => await Save()}", "{= Binding}", "{1 + 1}", "{Count GT 0 AND IsOn}", "{Name ?? 'none'}",
		"{ Title }", "  {Title}  ", "{Math.Max(A, B)}", "{BindingFoo}", "{Foo.Bar baz}",
		"{Binding Name}", "{Binding}", "{StaticResource Key}", "{DynamicResource Key}", "{TemplateBinding Width}", "{RelativeSource Self}",
		"{ComponentResourceKey X}", "{ThemeDictionary X}", "{MultiBinding}", "{PriorityBinding}", "{BindingExtension}", "{StaticResourceExtension K}",
		"{x:Static Foo.Bar}", "{x:Null}", "{x:Type local:T}", "{local:Anything}", "{}{Escaped}", "{}", "{ }", "{=}", "{= }",
		"plain", "OnClick", "", " ", "{", "}", "{Unclosed", "Closed}", "{{Double}}",
	};

	static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

	static Func<string, bool>? Load(string dll, string typeName, string method, bool hasOutParameter)
	{
		if (!File.Exists(dll))
			return null;
		var type = Assembly.LoadFrom(dll).GetType(typeName, throwOnError: true)!;
		var info = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
		return value => {
			var args = hasOutParameter ? new object?[] { value, null } : new object?[] { value };
			return (bool)info.Invoke(null, args)!;
		};
	}

	[Fact]
	public void EveryCopyOfTheRule_AgreesOnEveryValue()
	{
		var rules = new List<(string Name, Func<string, bool> IsExpression)> {
			("OpenDevelop Base", XamlExpressionSyntax.IsExpression),
			("WpfDesigner XamlDom", ICSharpCode.WpfDesign.XamlDom.XamlExpressionSyntax.IsExpression),
		};
		var missing = new List<string>();

		// The language server's own test project copies the assembly it was built against.
		var winappvsce = Load(Path.Combine(Root, "externals", "winappvsce", "server", "test", "WinUiXaml.LanguageServer.Tests", "bin", "Debug", "net10.0", "WinUiXaml.Xaml.dll"),
			"WinUiXaml.Xaml.XamlExpressionSyntax", "IsExpression", hasOutParameter: false);
		if (winappvsce is null) missing.Add("winappvsce WinUiXaml.Xaml"); else rules.Add(("winappvsce WinUiXaml.Xaml", winappvsce));

		var generator = Load(Path.Combine(Root, "..", "openavalon", "LibreWPF", "artifacts", "bin", "LibreWPF.Xaml.SourceGenerator", "Debug", "netstandard2.0", "LibreWPF.Xaml.SourceGenerator.dll"),
			"LibreWPF.Xaml.SourceGenerator.XamlExpressions", "TryGetExpression", hasOutParameter: true);
		if (generator is null) missing.Add("openavalon LibreWPF.Xaml.SourceGenerator"); else rules.Add(("openavalon generator", generator));

		var disagreements = new List<string>();
		foreach (var value in Values) {
			var answers = rules.Select(rule => (rule.Name, Answer: rule.IsExpression(value))).ToArray();
			if (answers.Select(a => a.Answer).Distinct().Count() > 1)
				disagreements.Add($"'{value}': " + string.Join(", ", answers.Select(a => a.Name + "=" + a.Answer)));
		}

		Assert.True(disagreements.Count == 0, "The copies of the expression rule disagree:\n" + string.Join("\n", disagreements));
		// A copy that was not built is named here, so a green run never hides it: build it and run again.
		// The two copies built here were still compared; an unbuilt one is a visible skip, not a failure (CI has no sibling checkouts).
		Assert.SkipWhen(missing.Count > 0, "Not compared (not built here): " + string.Join(", ", missing));
	}
}
