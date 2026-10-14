using LeXtudio.MewUI.Xaml;
using Xunit;

public sealed class MxamlItemsTests
{
	const string Source = """
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow" Name="mainWindow" Title="Prefs" Width="320" Height="200">
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
		// The list exists before SelectedIndex selects into it.
		Assert.True(csharp.IndexOf("themeCombo.Items(", StringComparison.Ordinal) < csharp.IndexOf("themeCombo.SelectedIndex", StringComparison.Ordinal));
	}

	/// <summary>Regression: Window-level properties were emitted once per root attribute, and a
	/// Double one took the OUTER loop's value. Window size is read-only (0.12 and 0.22), so it is one
	/// WindowSize.Resizable call with named arguments; Width without Height has no source form.</summary>
	[Fact]
	public void GeneratesEachWindowPropertyOnceAndSizeThroughWindowSize()
	{
		var csharp = MewUICSharpGenerator.Generate(NewDoc());
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(csharp, @"^\s*Title = ""Prefs"";", System.Text.RegularExpressions.RegexOptions.Multiline));
		Assert.Contains("WindowSize = WindowSize.Resizable(width: 320.0, height: 200.0);", csharp);
		Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"^\s*Width = ", System.Text.RegularExpressions.RegexOptions.Multiline), csharp);
		var widthOnly = MewUICSharpGenerator.Generate(MxamlDocument.Parse(Source.Replace(" Height=\"200\"", "")));
		Assert.Contains("// unsupported: a Window size needs both Width and Height", widthOnly);
		Assert.DoesNotContain("WindowSize =", widthOnly);
	}

	/// <summary>0.22 content controls take their caption through the .Content("text") extension.</summary>
	[Fact]
	public void GeneratesCaptionsThroughContentExtension()
	{
		const string source = """
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow" Name="mainWindow">
  <StackPanel Name="rootPanel">
    <Button Name="ok" Content="Say &quot;OK&quot;"/>
    <CheckBox Name="check" Text="Remember"/>
    <RadioButton Name="radio" Content="One"/>
  </StackPanel>
</Window>
""";
		var csharp = MewUICSharpGenerator.Generate(MxamlDocument.Parse(source));
		Assert.Contains("ok.Content(\"Say \\\"OK\\\"\");", csharp);
		Assert.Contains("check.Content(\"Remember\");", csharp);
		Assert.Contains("radio.Content(\"One\");", csharp);
		Assert.DoesNotContain(".Text =", csharp);
	}

	[Fact]
	public void GeneratesCommonAndRangeProperties()
	{
		const string source = """
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow" Name="mainWindow">
  <StackPanel Name="rootPanel" HorizontalAlignment="Center">
    <Slider Name="volume" Minimum="0" Maximum="10" Value="3" IsEnabled="False"/>
    <ProgressBar Name="busy" IsIndeterminate="True" Opacity="0.5"/>
    <Label Name="caption" VerticalAlignment="Diagonal"/>
  </StackPanel>
</Window>
""";
		var doc = MxamlDocument.Parse(source);
		// An enum value outside the runtime's members is an error, not silently generated.
		Assert.Contains(doc.Diagnostics, d => d.Severity == MxamlDiagnosticSeverity.Error && d.Message.Contains("VerticalAlignment"));
		doc = MxamlDocument.Parse(source.Replace(" VerticalAlignment=\"Diagonal\"", ""));
		Assert.False(doc.HasErrors, string.Join("\n", doc.Diagnostics));
		var csharp = MewUICSharpGenerator.Generate(doc);
		foreach (var line in new[] { "rootPanel.HorizontalAlignment = HorizontalAlignment.Center;", "volume.Minimum = 0.0;", "volume.Maximum = 10.0;",
			"volume.Value = 3.0;", "volume.IsEnabled = false;", "busy.IsIndeterminate = true;", "busy.Opacity = 0.5;" })
			Assert.Contains(line, csharp);
		Assert.DoesNotContain("// unsupported", csharp);
		Assert.Equal(new[] { "Left", "Center", "Right", "Stretch" }, MewUIControlCatalog.EnumValuesOf("Button", "HorizontalAlignment"));
		Assert.Empty(MewUIControlCatalog.EnumValuesOf("Window", "HorizontalAlignment"));
		// TabItem is not a FrameworkElement: only its own IsEnabled/HeaderText, none of the inherited set.
		Assert.True(MewUIControlCatalog.IsSupported("TabItem", "IsEnabled"));
		Assert.False(MewUIControlCatalog.IsSupported("TabItem", "Opacity"));
		// Width/Height are FrameworkElement members: not on TabItem (no Width), and not on unknown types.
		Assert.False(MewUIControlCatalog.IsSupported("TabItem", "Width"));
		Assert.True(MewUIControlCatalog.IsSupported("Button", "Width"));
		Assert.True(MewUIControlCatalog.IsSupported("TabControl", "Opacity"));
	}

	[Fact]
	public void GeneratesThicknessAndColourValues()
	{
		const string source = """
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow" Name="mainWindow" Background="White" Padding="12">
  <StackPanel Name="rootPanel" Margin="4,8" Padding="1 2 3 4">
    <Button Name="ok" Content="OK" Background="#336699" Foreground="#80FFFFFF" BorderBrush="CornflowerBlue" CornerRadius="4"/>
    <Image Name="logo" Margin="2"/>
  </StackPanel>
</Window>
""";
		var doc = MxamlDocument.Parse(source);
		Assert.False(doc.HasErrors, string.Join("\n", doc.Diagnostics));
		var csharp = MewUICSharpGenerator.Generate(doc);
		foreach (var line in new[] {
			"Background = Color.White;", "Padding = new Thickness(12.0);",
			"rootPanel.Margin = new Thickness(4.0, 8.0);", "rootPanel.Padding = new Thickness(1.0, 2.0, 3.0, 4.0);",
			"ok.Background = Color.FromArgb(0xFF336699u);", "ok.Foreground = Color.FromArgb(0x80FFFFFFu);",
			"ok.BorderBrush = Color.CornflowerBlue;", "ok.CornerRadius = 4.0;", "logo.Margin = new Thickness(2.0);" })
			Assert.Contains(line, csharp);
		Assert.DoesNotContain("// unsupported", csharp);
		// Panels and Image have no Control-level visuals; bad values are errors.
		Assert.False(MewUIControlCatalog.IsSupported("StackPanel", "Background"));
		Assert.False(MewUIControlCatalog.IsSupported("Image", "Padding"));
		foreach (var bad in new[] { "<Image Name=\"bad\" Margin=\"1,2,3\"/>", "<Label Name=\"bad\" Foreground=\"#12345\"/>", "<Label Name=\"bad\" Foreground=\"Chartreusey\"/>" })
			Assert.True(MxamlDocument.Parse(source.Replace("<Image Name=\"logo\" Margin=\"2\"/>", bad)).HasErrors, bad);
	}
}
