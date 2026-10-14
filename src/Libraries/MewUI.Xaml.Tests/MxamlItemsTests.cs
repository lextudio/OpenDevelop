using LeXtudio.MewUI.Xaml;
using Xunit;

public sealed class MxamlItemsTests
{
	const string Source = """
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow" Name="mainWindow" Title="Prefs" Width="320">
  <StackPanel Name="rootPanel">
    <ComboBox Name="themeCombo" SelectedIndex="0">
      <Item>Light</Item>
      <Item>Dark &amp; "Dim"</Item>
    </ComboBox>
    <ListBox Name="recent"/>
    <Label Name="caption" Text="Hi"/>
  </StackPanel>
</Window>
""";

	static MxamlDocument NewDoc()
	{
		var doc = new MxamlDocument();
		Assert.True(doc.Reset(Source), doc.Error);
		return doc;
	}

	[Fact]
	public void ParsesAndRoundTripsItems()
	{
		var doc = NewDoc();
		Assert.Equal(new[] { "Light", "Dark & \"Dim\"" }, doc.Find("themeCombo")!.Items);
		Assert.Empty(doc.Find("recent")!.Items);
		var again = new MxamlDocument();
		Assert.True(again.Reset(doc.ToXaml()), again.Error);
		Assert.Equal(doc.Find("themeCombo")!.Items, again.Find("themeCombo")!.Items);
		Assert.Contains("<Item>Dark &amp; &quot;Dim&quot;</Item>", doc.ToXaml());
	}

	[Fact]
	public void SetItemsIsOneUndoStepAndRefusesNonItemControls()
	{
		var doc = NewDoc();
		Assert.True(doc.SetItems("recent", new[] { "a.txt", "b.txt" }));
		Assert.Equal(new[] { "a.txt", "b.txt" }, doc.Find("recent")!.Items);
		Assert.True(doc.Undo());
		Assert.Empty(doc.Find("recent")!.Items);
		Assert.False(doc.SetItems("caption", new[] { "x" }));
		Assert.False(doc.SetItems("rootPanel", new[] { "x" }));
		Assert.True(doc.SetItems("themeCombo", Array.Empty<string>()));
		Assert.DoesNotContain("<Item>", doc.ToXaml());
	}

	[Fact]
	public void GeneratesItemsCallsWithEscapedLiterals()
	{
		var csharp = MewUICSharpGenerator.Generate(NewDoc());
		Assert.Contains("themeCombo.Items(\"Light\", \"Dark & \\\"Dim\\\"\");", csharp);
		Assert.DoesNotContain("recent.Items(", csharp);
	}

	/// <summary>Regression: Window-level properties were emitted once per root attribute, and a
	/// Double one took the OUTER loop's value (Width = the Title text).</summary>
	[Fact]
	public void GeneratesEachWindowPropertyOnceWithItsOwnValue()
	{
		var csharp = MewUICSharpGenerator.Generate(NewDoc());
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(csharp, @"^\s*Title = ""Prefs"";", System.Text.RegularExpressions.RegexOptions.Multiline));
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(csharp, @"^\s*Width = 320\.0;", System.Text.RegularExpressions.RegexOptions.Multiline));
	}
}
