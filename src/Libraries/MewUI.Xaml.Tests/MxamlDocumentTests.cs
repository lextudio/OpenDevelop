using LeXtudio.MewUI.Xaml;
using Xunit;

public sealed class MxamlDocumentTests
{
	const string Source = """
<?xml version="1.0" encoding="utf-8"?>
<MewUI xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow">
  <Window Name="mainWindow" Title="QuickNotes">
    <StackPanel Name="rootPanel" Spacing="8">
      <Label Name="heading" Text="QuickNotes"/>
      <StackPanel Name="toolRow" Spacing="6" Orientation="Horizontal">
        <Button Name="newButton" Content="New" Click="NewButton_Click"/>
      </StackPanel>
    </StackPanel>
  </Window>
</MewUI>
""";

	MxamlDocument NewDoc() { var d = MxamlDocument.Parse(Source); Assert.False(d.HasErrors, string.Join("\n", d.Diagnostics)); return d; }

	[Fact]
	public void Parse_BuildsTree_WithLineInfo()
	{
		var doc = NewDoc();
		Assert.Equal("App.MainWindow", doc.Class);
		Assert.Equal("rootPanel", doc.Root.Children[0].Name);
		Assert.Equal(8.0, double.Parse(doc.Root.Children[0].FindAttribute("Spacing")!.Value, System.Globalization.CultureInfo.InvariantCulture));
		Assert.True(doc.Root.Line > 0);
	}

	[Fact]
	public void SetProperty_NumericLookingValueOnTextProperty_GeneratesStringLiteral()
	{
		var doc = NewDoc();
		doc.SetProperty("heading", "Text", "123");
		var csharp = MewUICSharpGenerator.Generate(doc);
		Assert.Contains(".Text = \"123\";", csharp);
		Assert.DoesNotContain(".Text = 123;", csharp);
	}

	[Fact]
	public void SetProperty_NumericProperty_EmitsNumericLiteral()
	{
		var doc = NewDoc();
		doc.SetProperty("rootPanel", "Spacing", "12");
		var csharp = MewUICSharpGenerator.Generate(doc);
		Assert.Contains(".Spacing = 12.0;", csharp);
	}

	[Fact]
	public void Add_CreatesUniqueName_AndGeneratorEmitsStrictGrammar()
	{
		var doc = NewDoc();
		Assert.True(doc.Add("toolRow", "TextBox"));
		var csharp = MewUICSharpGenerator.Generate(doc);
		Assert.Contains("private TextBox textBox1 = null!;", csharp);
		Assert.Contains("textBox1 = new TextBox();", csharp);
		Assert.Contains("toolRow.Children(newButton, textBox1);", csharp);
		Assert.DoesNotContain("this.", csharp);
	}

	[Fact]
	public void Remove_RemovesSubtree()
	{
		var doc = NewDoc();
		Assert.True(doc.Remove("toolRow")); // removes toolRow AND its subtree (newButton)
		Assert.Null(doc.Find("toolRow"));
		Assert.Null(doc.Find("newButton"));
		Assert.Equal(3, Count(doc.Root)); // window + rootPanel + heading
	}

	static int Count(MxamlObject o) => 1 + o.Children.Sum(Count);

	[Fact]
	public void Rename_RejectsInvalidAndDuplicateNames()
	{
		var doc = NewDoc();
		Assert.False(doc.Rename("heading", "123bad"));
		Assert.False(doc.Rename("heading", "rootPanel"));
		Assert.True(doc.Rename("heading", "header"));
		Assert.NotNull(doc.Find("header"));
	}

	// #4 class: broken documents report positioned diagnostics instead of throwing.
	[Fact]
	public void Parse_NonContainerChild_ReportsPositionedError()
	{
		var bad = Source.Replace(
			"<Button Name=\"newButton\" Content=\"New\" Click=\"NewButton_Click\"/>",
			"<Button Name=\"newButton\" Content=\"New\"><Label Name=\"oops\"/></Button>");
		var doc = MxamlDocument.Parse(bad);
		Assert.Contains(doc.Diagnostics, d => d.Severity == MxamlDiagnosticSeverity.Error && d.Message.Contains("not a container"));
	}

	[Fact]
	public void Parse_DuplicateName_ReportsError()
	{
		var bad = Source.Replace("Name=\"heading\"", "Name=\"rootPanel\"");
		var doc = MxamlDocument.Parse(bad);
		Assert.Contains(doc.Diagnostics, d => d.Message.Contains("Duplicate Name 'rootPanel'"));
	}

	[Fact]
	public void Parse_MalformedXml_ThrowsWithMessage()
	{
		Assert.Throws<MxamlException>(() => MxamlDocument.Parse("<MewUI><unclosed>"));
		Assert.Throws<MxamlException>(() => MxamlDocument.Parse("<interface/>"));
	}

	// Transactional mutation: an invalid value must leave the document untouched.
	[Fact]
	public void SetProperty_InvalidNumber_IsRejectedAndRollsBack()
	{
		var doc = NewDoc();
		Assert.False(doc.SetProperty("rootPanel", "Spacing", "abc"));
		Assert.Equal("8", doc.Root.Children[0].FindAttribute("Spacing")!.Value);
	}

	[Fact]
	public void SetEvent_AddsAndRemovesWiring()
	{
		var doc = NewDoc();
		Assert.True(doc.SetEvent("heading", "Loaded", "OnHeadingLoaded"));
		Assert.Contains("heading.Loaded += OnHeadingLoaded;", MewUICSharpGenerator.Generate(doc));
		Assert.True(doc.SetEvent("heading", "Loaded", null));
		Assert.DoesNotContain("OnHeadingLoaded", MewUICSharpGenerator.Generate(doc));
	}

	[Fact]
	public void UnsupportedProperty_GeneratesComment_KeepsCodeCompilable()
	{
		var doc = NewDoc();
		doc.Root.Children[0].Attributes.Add(new MxamlAttribute { Name = "FutureThing", Value = "x" });
		var csharp = MewUICSharpGenerator.Generate(doc);
		Assert.Contains("// unsupported:", csharp);
	}

	[Fact]
	public void ToXaml_RoundTriips()
	{
		var doc = NewDoc();
		var second = MxamlDocument.Parse(doc.ToXaml());
		Assert.Equal(doc.ToXaml(), second.ToXaml());
		Assert.False(second.HasErrors, string.Join("\n", second.Diagnostics));
	}
}
