using System.Xml.Linq;
using ICSharpCode.GtkDesigner;
using Xunit;

namespace GtkDesigner.Tests;

/// <summary>The preview copy GtkBuilder can instantiate. Each case here made the real
/// GtkBuilder reject the whole document (the design host aborted on "Invalid object type").</summary>
public sealed class GtkPreviewSanitizerTests
{
	static (XDocument Doc, List<string> Diagnostics) Sanitize(string ui)
	{
		var diagnostics = new List<string>();
		var xml = GtkPreviewSanitizer.Sanitize(ui, GirCatalogTests.LoadedCatalog(), diagnostics,
			parent => parent == "GtkGrid" ? "GtkGridLayoutChild" : null);
		return (XDocument.Parse(xml), diagnostics);
	}

	static XElement Object(XDocument doc, string id) => doc.Descendants().Single(e => e.Name.LocalName == "object" && (string?)e.Attribute("id") == id);

	[Fact]
	public void CustomWidget_BecomesALabelledPlaceholder_ThatKeepsItsId()
	{
		var (doc, diagnostics) = Sanitize("""
			<interface><requires lib="gtk" version="4.0"/>
			  <object class="GtkWindow" id="w"><child>
			    <object class="MyAppStarRating" id="rating"><property name="stars">4</property></object>
			  </child></object>
			</interface>
			""");
		var rating = Object(doc, "rating");
		Assert.Equal("GtkLabel", (string?)rating.Attribute("class"));
		Assert.Equal("‹MyAppStarRating›", rating.Elements("property").Single(p => (string?)p.Attribute("name") == "label").Value);
		Assert.DoesNotContain(rating.Elements("property"), p => (string?)p.Attribute("name") == "stars");
		Assert.Contains(diagnostics, d => d.Contains("MyAppStarRating", StringComparison.Ordinal));
	}

	[Fact]
	public void CustomContainer_BecomesABox_SoItsChildrenStillPreview()
	{
		var (doc, _) = Sanitize("""
			<interface><requires lib="gtk" version="4.0"/>
			  <object class="GtkWindow" id="w"><child>
			    <object class="MyAppCard" id="card"><child><object class="GtkLabel" id="inner"/></child></object>
			  </child></object>
			</interface>
			""");
		Assert.Equal("GtkBox", (string?)Object(doc, "card").Attribute("class"));
		Assert.Equal("GtkLabel", (string?)Object(doc, "inner").Attribute("class"));
	}

	[Fact]
	public void UnknownProperty_InvalidValue_MissingReference_AndUnknownLayout_AreDropped()
	{
		var (doc, diagnostics) = Sanitize("""
			<interface><requires lib="gtk" version="4.0"/>
			  <object class="GtkWindow" id="w">
			    <property name="default-widget">nowhere</property>
			    <child><object class="GtkGrid" id="grid">
			      <child><object class="GtkButton" id="b">
			        <property name="lable">typo</property>
			        <property name="halign">middle</property>
			        <property name="label">Fine</property>
			        <layout><property name="column">1</property><property name="colum">2</property></layout>
			      </object></child>
			    </object></child>
			  </object>
			</interface>
			""");
		var b = Object(doc, "b");
		var names = b.Elements("property").Select(p => (string?)p.Attribute("name")).ToList();
		Assert.Equal(new[] { "label" }, names);
		Assert.Equal(new[] { "column" }, b.Element("layout")!.Elements("property").Select(p => (string?)p.Attribute("name")));
		Assert.DoesNotContain(Object(doc, "w").Elements("property"), p => (string?)p.Attribute("name") == "default-widget");
		Assert.Equal(4, diagnostics.Count);
	}

	[Fact]
	public void CompositeTemplate_PreviewsAsItsParentType()
	{
		var (doc, diagnostics) = Sanitize("""
			<interface><requires lib="gtk" version="4.0"/>
			  <template class="MyWindow" parent="GtkWindow"><property name="title">T</property>
			    <child><object class="GtkLabel" id="l"/></child></template>
			</interface>
			""");
		var root = Object(doc, "MyWindow");
		Assert.Equal("GtkWindow", (string?)root.Attribute("class"));
		Assert.Null(root.Attribute("parent"));
		Assert.Empty(diagnostics);
	}

	[Fact]
	public void Editor_TemplateRoot_IsItsParentType_FoundByClass_AndNotRenameable()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""
			<interface><requires lib="gtk" version="4.0"/>
			  <template class="MyWindow" parent="GtkWindow"><child><object class="GtkBox" id="box"/></child></template>
			</interface>
			"""), editor.Error);
		Assert.Equal("MyWindow", editor.Roots[0].Id);
		Assert.Equal("GtkWindow", editor.Roots[0].ClassName);
		Assert.True(editor.SetProperty("MyWindow", "title", "Hello"));
		Assert.Contains("<property name=\"title\">Hello</property>", editor.Text);
		Assert.False(editor.Rename("MyWindow", "OtherWindow"));
		Assert.True(editor.Add("box", "GtkButton"));
	}

	[Fact]
	public void PropertyEmbeddedObject_IsKept_NotTreatedAsAMissingReference()
	{
		var (doc, diagnostics) = Sanitize("""
			<interface><requires lib="gtk" version="4.0"/>
			  <object class="GtkWindow" id="w">
			    <property name="child"><object class="GtkLabel" id="inside"><property name="label">Hi there</property></object></property>
			  </object>
			</interface>
			""");
		Assert.Equal("GtkLabel", (string?)Object(doc, "inside").Attribute("class"));
		Assert.Empty(diagnostics);
	}

	[Fact]
	public void RelativeFiles_ResolveAgainstTheUiFolder_ResourcesAndUrisDoNot()
	{
		// The .ui lives in ui-folder; the preview builds from a file in preview-folder, and GtkBuilder
		// loads images only through paths relative to that file.
		var baseDir = Path.Combine(Path.GetTempPath(), "ui-folder");
		var previewDir = Path.Combine(Path.GetTempPath(), "preview-folder");
		var uri = new Uri(Path.Combine(Path.GetTempPath(), "x.png")).AbsoluteUri;
		var diagnostics = new List<string>();
		var xml = GtkPreviewSanitizer.Sanitize($$"""
			<interface><requires lib="gtk" version="4.0"/>
			  <object class="GtkWindow" id="w"><child><object class="GtkBox" id="box">
			    <child><object class="GtkPicture" id="pic"><property name="file">images/logo.png</property></object></child>
			    <child><object class="GtkImage" id="img"><property name="file">icon.png</property></object></child>
			    <child><object class="GtkImage" id="res"><property name="resource">/org/example/icon.png</property></object></child>
			    <child><object class="GtkPicture" id="uri"><property name="file">{{uri}}</property></object></child>
			  </object></child></object>
			</interface>
			""", GirCatalogTests.LoadedCatalog(), diagnostics, baseDirectory: baseDir, previewDirectory: previewDir);
		var doc = XDocument.Parse(xml);
		string Value(string id, string name) => Object(doc, id).Elements("property").Single(p => (string?)p.Attribute("name") == name).Value;
		Assert.Equal("../ui-folder/images/logo.png", Value("pic", "file"));   // Gio.File
		Assert.Equal("../ui-folder/icon.png", Value("img", "file"));          // utf8 "file"
		Assert.Equal("/org/example/icon.png", Value("res", "resource"));
		Assert.Equal("../x.png", Value("uri", "file"));
		Assert.Empty(diagnostics);   // Gio.File is a path, not a missing object reference
		Assert.Equal(GirValueKind.Path, GirCatalogTests.LoadedCatalog().FindProperty("GtkPicture", "file")!.Kind);
	}

	[Fact]
	public void AValidDocument_IsUnchanged()
	{
		const string ui = """<interface><requires lib="gtk" version="4.0"/><object class="GtkWindow" id="w"><property name="title">T</property></object></interface>""";
		var diagnostics = new List<string>();
		Assert.Equal(XDocument.Parse(ui).ToString(SaveOptions.DisableFormatting), GtkPreviewSanitizer.Sanitize(ui, GirCatalogTests.LoadedCatalog(), diagnostics));
		Assert.Empty(diagnostics);
	}
}
