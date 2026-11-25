using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Markup;
using ICSharpCode.WpfDesign.AddIn;
using ICSharpCode.WpfDesign.SurfaceHost;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The three project kinds the WPF designer serves. BAML and the XAML source generator (XSG) are separate compile paths of which a project
/// uses one; Simpler XAML (a document without xmlns) is a feature of the XSG path only:
///   1. old-style XAML, BAML            - the reader is a plain one, exactly as before;
///   2. old-style XAML, XSG             - the document's own declarations are used;
///   3. new-style XAML, XSG, Simpler XAML on - the default and x namespaces are the reader's ambient context.
/// </summary>
public sealed class ImplicitXamlNamespacesTests
{
	const string Presentation = ImplicitXamlNamespaceReader.PresentationNamespace;
	const string Xaml = ImplicitXamlNamespaceReader.XamlNamespace;

	const string OldStyle = "<Grid xmlns=\"" + Presentation + "\" xmlns:x=\"" + Xaml + "\"><TextBlock x:Name=\"Status\" Text=\"plain\" /></Grid>";
	const string NewStyle = "<Grid><TextBlock x:Name=\"Status\" Text=\"plain\" /></Grid>";

	static Func<string, string> Properties(params (string Name, string Value)[] values)
		=> name => values.Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault();

	static object Load(string text, bool implicitNamespaces)
	{
		using var reader = ImplicitXamlNamespaceReader.Create(new StringReader(text), implicitNamespaces);
		return XamlReader.Load(reader);
	}

	// ---- the project's switches ------------------------------------------------------------------------------------------------

	[Fact]
	public void ABamlProject_HasNoImplicitNamespaces()
	{
		// Nothing set: the BAML compiler, exactly as upstream WPF.
		Assert.False(XsgProjectFeatures.ImplicitXamlNamespaces(Properties()));
		Assert.False(XsgProjectFeatures.ImplicitXamlNamespaces(Properties(("UseLibreWpfXamlSourceGenerator", "false"))));
	}

	[Fact]
	public void CSharpExpressions_NeedTheXsgSwitch_AndFollowIt()
	{
		Assert.False(XsgProjectFeatures.CSharpExpressions(Properties()));
		Assert.False(XsgProjectFeatures.CSharpExpressions(Properties(("LibreWpfCSharpExpressions", "true"))));
		Assert.True(XsgProjectFeatures.CSharpExpressions(Properties(("UseLibreWpfXamlSourceGenerator", "true"))));
		Assert.False(XsgProjectFeatures.CSharpExpressions(Properties(("UseLibreWpfXamlSourceGenerator", "true"), ("LibreWpfCSharpExpressions", "false"))));
	}

	[Fact]
	public void AFeatureSetWithoutTheOverallSwitch_DoesNothing()
	{
		Assert.False(XsgProjectFeatures.ImplicitXamlNamespaces(Properties(("LibreWpfSimplerXaml", "true"))));
		Assert.False(XsgProjectFeatures.ImplicitXamlNamespaces(Properties(("UseLibreWpfXamlSourceGenerator", "false"), ("LibreWpfSimplerXaml", "true"))));
	}

	[Theory]
	[InlineData("true")]
	[InlineData("True")]
	[InlineData("  true ")]
	public void AnXsgProject_HasThemWhenTheFeatureFollowsTheOverallSwitchOrIsOn(string overall)
	{
		Assert.True(XsgProjectFeatures.ImplicitXamlNamespaces(Properties(("UseLibreWpfXamlSourceGenerator", overall))));
		Assert.True(XsgProjectFeatures.ImplicitXamlNamespaces(Properties(("UseLibreWpfXamlSourceGenerator", overall), ("LibreWpfSimplerXaml", "true"))));
		Assert.True(XsgProjectFeatures.ImplicitXamlNamespaces(Properties(("UseLibreWpfXamlSourceGenerator", overall), ("LibreWpfSimplerXaml", ""))));
	}

	[Theory]
	[InlineData("false")]
	[InlineData("FALSE")]
	[InlineData(" false ")]
	public void AnXsgProjectWithSimplerXamlOff_HasNoImplicitNamespaces(string feature)
	{
		Assert.False(XsgProjectFeatures.ImplicitXamlNamespaces(Properties(("UseLibreWpfXamlSourceGenerator", "true"), ("LibreWpfSimplerXaml", feature))));
	}

	[Fact]
	public void TheOtherFeatureDoesNotMatter()
	{
		Assert.True(XsgProjectFeatures.ImplicitXamlNamespaces(Properties(("UseLibreWpfXamlSourceGenerator", "true"), ("LibreWpfCSharpExpressions", "false"))));
		Assert.False(XsgProjectFeatures.ImplicitXamlNamespaces(Properties(("UseLibreWpfXamlSourceGenerator", "true"), ("LibreWpfSimplerXaml", "false"), ("LibreWpfCSharpExpressions", "true"))));
	}

	[Fact]
	public void AFailingEvaluationOrNoProject_IsAPlainProject()
	{
		Assert.False(XsgProjectFeatures.ImplicitXamlNamespaces(null));
		Assert.False(XsgProjectFeatures.ImplicitXamlNamespaces(_ => throw new InvalidOperationException("evaluation failed")));
	}

	// ---- the reader ------------------------------------------------------------------------------------------------------------

	[UIFact]
	public void Case1_OldStyleXaml_ReadsWithAPlainReader()
	{
		Assert.IsType<Grid>(Load(OldStyle, implicitNamespaces: false));
	}

	[UIFact]
	public void Case1_NewStyleXamlInABamlProject_IsStillAnError()
	{
		// As upstream WPF: a document without declarations does not load unless the project is on the XSG path with Simpler XAML.
		Assert.ThrowsAny<Exception>(() => Load(NewStyle, implicitNamespaces: false));
	}

	[UIFact]
	public void Case2_OldStyleXaml_ReadsTheSameWhetherOrNotTheNamespacesAreImplicit()
	{
		var plain = Assert.IsType<Grid>(Load(OldStyle, implicitNamespaces: false));
		var implicitly = Assert.IsType<Grid>(Load(OldStyle, implicitNamespaces: true));

		Assert.Equal(((TextBlock)plain.Children[0]).Text, ((TextBlock)implicitly.Children[0]).Text);
		Assert.Equal("Status", ((TextBlock)implicitly.Children[0]).Name);
	}

	[UIFact]
	public void Case3_NewStyleXaml_ReadsWithTheAmbientNamespaces()
	{
		var grid = Assert.IsType<Grid>(Load(NewStyle, implicitNamespaces: true));

		var text = Assert.IsType<TextBlock>(Assert.Single(grid.Children));
		Assert.Equal("Status", text.Name);
		Assert.Equal("plain", text.Text);
	}

	[UIFact]
	public void ADocumentsOwnDeclarationsOverrideTheAmbientOnes()
	{
		// A declared default namespace and prefix win; the ambient context only fills what the document leaves out.
		var xaml = "<Grid xmlns=\"" + Presentation + "\" xmlns:x=\"" + Xaml + "\" xmlns:s=\"clr-namespace:System;assembly=System.Runtime\">"
			+ "<TextBlock x:Name=\"A\" Tag=\"{x:Static s:String.Empty}\" /></Grid>";

		var grid = Assert.IsType<Grid>(Load(xaml, implicitNamespaces: true));

		Assert.Equal(string.Empty, ((TextBlock)grid.Children[0]).Tag);
	}

	[UIFact]
	public void ADocumentWithOnlySomeDeclarations_UsesTheAmbientRest()
	{
		// x declared by the document, the default namespace implicit.
		var xaml = "<Grid xmlns:x=\"" + Xaml + "\"><TextBlock x:Name=\"A\" /></Grid>";

		Assert.IsType<Grid>(Load(xaml, implicitNamespaces: true));
	}

	[Fact]
	public void TheAmbientContextIsPerReader()
	{
		// An implicit reader does not leak its namespaces into the next, plain one.
		using (ImplicitXamlNamespaceReader.Create(new StringReader(NewStyle), implicitNamespaces: true)) { }

		using var plain = ImplicitXamlNamespaceReader.Create(new StringReader(NewStyle), implicitNamespaces: false);
		Assert.ThrowsAny<Exception>(() => { while (plain.Read()) { } });
	}

	// ---- App.xaml and merged dictionaries (documents that are not the design document) ----------------------------------------

	const string NewStyleApp = "<Application x:Class=\"Sample.App\"><Application.Resources><SolidColorBrush x:Key=\"B\" Color=\"Red\" /></Application.Resources></Application>";

	[Fact]
	public void ANewStyleAppDocument_GetsExplicitDeclarationsOnItsRootOfTheInMemoryCopy()
	{
		var root = ImplicitXamlNamespaceReader.LoadDocument(NewStyleApp, implicitNamespaces: true).DocumentElement!;

		Assert.Equal(Presentation, root.GetAttribute("xmlns"));
		Assert.Equal(Xaml, root.GetAttribute("xmlns:x"));
	}

	[UIFact]
	public void TheResourcesOfANewStyleAppDocument_ParseOnceTheirRootsNamespacesAreCopiedOntoThem()
	{
		// What ParseAppResources does: the children are re-parsed detached from the root, with the root's xmlns attributes.
		var root = ImplicitXamlNamespaceReader.LoadDocument(NewStyleApp, implicitNamespaces: true).DocumentElement!;
		var resources = root.ChildNodes.Cast<System.Xml.XmlNode>().Single(n => n.LocalName.EndsWith(".Resources", StringComparison.Ordinal));
		var xaml = "<ResourceDictionary xmlns=\"" + Presentation + "\" xmlns:x=\"" + Xaml + "\">" + resources.InnerXml + "</ResourceDictionary>";

		var dictionary = Assert.IsType<System.Windows.ResourceDictionary>(XamlReader.Parse(xaml));

		Assert.True(dictionary.Contains("B"));
	}

	[Fact]
	public void AnOldStyleAppDocument_IsLeftAsItIs()
	{
		var xaml = "<Application xmlns=\"" + Presentation + "\" xmlns:x=\"" + Xaml + "\" x:Class=\"Sample.App\" />";

		var implicitly = ImplicitXamlNamespaceReader.LoadDocument(xaml, implicitNamespaces: true);
		var plain = ImplicitXamlNamespaceReader.LoadDocument(xaml, implicitNamespaces: false);

		Assert.Equal(plain.OuterXml, implicitly.OuterXml);
	}

	[Fact]
	public void ANewStyleAppDocumentOfABamlProject_IsStillAnError()
	{
		Assert.ThrowsAny<Exception>(() => ImplicitXamlNamespaceReader.LoadDocument(NewStyleApp, implicitNamespaces: false));
	}

	[Fact]
	public void ADocumentWithOnlyTheDefaultNamespaceDeclared_GetsTheXPrefix()
	{
		var xaml = "<Application xmlns=\"" + Presentation + "\" x:Class=\"Sample.App\" />";

		var root = ImplicitXamlNamespaceReader.LoadDocument(xaml, implicitNamespaces: true).DocumentElement!;

		Assert.Equal(Presentation, root.GetAttribute("xmlns"));
		Assert.Equal(Xaml, root.GetAttribute("xmlns:x"));
	}

	// ---- the snapshot ----------------------------------------------------------------------------------------------------------

	[Fact]
	public void TheSnapshotFlagDefaultsToOff_AndSurvivesTheWire()
	{
		Assert.False(new DesignerDocumentSnapshot().ImplicitXamlNamespaces);

		var json = JsonSerializer.Serialize(new DesignerDocumentSnapshot { ImplicitXamlNamespaces = true });
		Assert.True(JsonSerializer.Deserialize<DesignerDocumentSnapshot>(json)!.ImplicitXamlNamespaces);
		// A message from an older host does not carry it: it reads as a stock document.
		Assert.False(JsonSerializer.Deserialize<DesignerDocumentSnapshot>("{\"ProjectFileName\":\"a.csproj\"}")!.ImplicitXamlNamespaces);
	}

	[Fact]
	public void GlobalUsings_AreTheNamespaceDirectivesWithoutAliasOrStatic()
	{
		const string source = """
			global using System.Text;
			global using Sample.Controls;
			   global   using   Spaced.Name ;
			global using global::Qualified.Name;
			global using Alias = Sample.Other;
			global using static Sample.Helper;
			using Plain.Using;
			namespace Sample { class C { } }
			""";
		Assert.Equal(new[] { "System.Text", "Sample.Controls", "Spaced.Name", "Qualified.Name" }, XsgProjectFeatures.ParseGlobalUsings(source));
		Assert.Empty(XsgProjectFeatures.ParseGlobalUsings(null));
	}
}
