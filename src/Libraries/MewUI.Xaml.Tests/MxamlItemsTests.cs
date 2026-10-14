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

	[Fact]
	public void ContentControlsTakeOneChildOrACaption()
	{
		const string source = """
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow" Name="mainWindow">
  <StackPanel Name="rootPanel">
    <Button Name="iconButton"><Label Name="glyph" Text="+"/></Button>
    <GroupBox Name="group"><Label Name="inside" Text="a"/></GroupBox>
  </StackPanel>
</Window>
""";
		var doc = new MxamlDocument();
		Assert.True(doc.Reset(source), doc.Error);
		var csharp = MewUICSharpGenerator.Generate(doc);
		Assert.Contains("iconButton.Content = glyph;", csharp);

		// A second child, or a caption plus a child, has no source form: refused, document unchanged.
		Assert.False(doc.Add("group", "Label"));
		Assert.False(doc.SetProperty("iconButton", "Content", "Add"));
		Assert.Single(doc.Find("group")!.Children);
		Assert.True(MxamlDocument.Parse(source.Replace("<Label Name=\"inside\" Text=\"a\"/>", "<Label Name=\"one\"/><Label Name=\"two\"/>")).HasErrors);
		Assert.True(MxamlDocument.Parse(source.Replace("<Button Name=\"iconButton\">", "<Button Name=\"iconButton\" Content=\"Add\">")).HasErrors);
	}

	[Fact]
	public void GeneratesGridTracksAndCellPlacement()
	{
		const string source = """
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow" Name="mainWindow">
  <Grid Name="form" Rows="Auto,*" Columns="100 2* *" Spacing="4" ShowGridLine="True">
    <Label Name="caption" Text="Name" Grid.Row="0" Grid.Column="0"/>
    <TextBox Name="input" Grid.Row="0" Grid.Column="1" Grid.ColumnSpan="2"/>
  </Grid>
</Window>
""";
		var doc = new MxamlDocument();
		Assert.True(doc.Reset(source), doc.Error);
		var csharp = MewUICSharpGenerator.Generate(doc);
		foreach (var line in new[] {
			"form.Rows(GridLength.Auto, GridLength.Star);",
			"form.Columns(GridLength.Pixels(100.0), GridLength.Stars(2.0), GridLength.Star);",
			"form.Spacing = 4.0;", "form.ShowGridLine = true;",
			"Grid.SetRow(caption, 0);", "Grid.SetColumn(input, 1);", "Grid.SetColumnSpan(input, 2);" })
			Assert.Contains(line, csharp);
		Assert.DoesNotContain("// unsupported", csharp);

		// Cell placement only under a Grid; bad tracks and spans are errors (and roll back an edit).
		Assert.True(doc.SetProperty("input", "Grid.Row", "1"));
		Assert.False(doc.SetProperty("input", "Grid.RowSpan", "0"));
		Assert.False(doc.SetProperty("form", "Rows", "Auto,banana"));
		Assert.False(doc.SetProperty("form", "Grid.Row", "1"));
		Assert.True(MxamlDocument.Parse(source.Replace("Rows=\"Auto,*\"", "Rows=\"-5\"")).HasErrors);

		// Pasting into a StackPanel drops the Grid placement; pasting into a Grid keeps it.
		var nested = new MxamlDocument();
		Assert.True(nested.Reset(source.Replace("<Grid Name=\"form\"", "<StackPanel Name=\"page\">\n<StackPanel Name=\"other\"/>\n<Grid Name=\"form\"").Replace("</Grid>", "</Grid>\n</StackPanel>")), nested.Error);
		var snippets = nested.CopySnippets(new[] { "input" })!;
		Assert.True(nested.Paste("other", snippets, out var intoStack));
		Assert.DoesNotContain(nested.Find(intoStack.Single())!.Attributes, a => a.Name.StartsWith("Grid.", StringComparison.Ordinal));
		Assert.True(nested.Paste("form", snippets, out var intoGrid));
		Assert.Contains(nested.Find(intoGrid.Single())!.Attributes, a => a.Name == "Grid.ColumnSpan");
	}

	/// <summary>Hand-written bad scalars are positioned errors at parse, not a generator crash.</summary>
	[Theory]
	[InlineData("<Slider Name=\"bad\" Value=\"abc\"/>", "not a number")]
	[InlineData("<CheckBox Name=\"bad\" IsChecked=\"yes\"/>", "not a boolean")]
	[InlineData("<ComboBox Name=\"bad\" SelectedIndex=\"one\"/>", "not an integer")]
	public void ParseReportsBadScalarValues(string element, string message)
	{
		var doc = MxamlDocument.Parse($"""
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow" Name="mainWindow">
  <StackPanel Name="rootPanel">
    {element}
  </StackPanel>
</Window>
""");
		var error = Assert.Single(doc.Diagnostics, d => d.Severity == MxamlDiagnosticSeverity.Error);
		Assert.Contains(message, error.Message);
		Assert.True(error.Line > 0);
		Assert.True(error.Column > 0);
	}

	[Fact]
	public void ParseAcceptsNumbersTheGeneratorAccepts()
	{
		var doc = MxamlDocument.Parse("""
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow" Name="mainWindow">
  <StackPanel Name="rootPanel"><Slider Name="big" Maximum="1,000" Value="2.5e2"/></StackPanel>
</Window>
""");
		Assert.False(doc.HasErrors, string.Join("\n", doc.Diagnostics));
		Assert.Contains("big.Maximum = 1000.0;", MewUICSharpGenerator.Generate(doc));
	}

	[Fact]
	public void ParseReportsGridCellRanges()
	{
		var doc = MxamlDocument.Parse("""
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow" Name="mainWindow">
  <Grid Name="form" Rows="*"><Label Name="a" Grid.Row="-1" Grid.RowSpan="0"/></Grid>
</Window>
""");
		Assert.Equal(2, doc.Diagnostics.Count(d => d.Severity == MxamlDiagnosticSeverity.Error && d.Message.Contains("out of range")));
	}

	[Fact]
	public void GeneratesDockCanvasEdgesAndWrapPanel()
	{
		const string source = """
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow" Name="mainWindow">
  <DockPanel Name="dock" LastChildFill="True">
    <Label Name="top" Text="Top" DockPanel.Dock="Top"/>
    <Canvas Name="surface" DockPanel.Dock="Left"><Label Name="corner" Canvas.Right="4" Canvas.Bottom="8"/></Canvas>
    <WrapPanel Name="tiles" Orientation="Vertical" Spacing="2" ItemWidth="64" ItemHeight="32"/>
  </DockPanel>
</Window>
""";
		var doc = new MxamlDocument();
		Assert.True(doc.Reset(source), doc.Error);
		var csharp = MewUICSharpGenerator.Generate(doc);
		foreach (var line in new[] { "DockPanel.SetDock(top, Dock.Top);", "DockPanel.SetDock(surface, Dock.Left);",
			"Canvas.SetRight(corner, 4.0);", "Canvas.SetBottom(corner, 8.0);",
			"tiles.Orientation = Orientation.Vertical;", "tiles.ItemWidth = 64.0;", "tiles.ItemHeight = 32.0;" })
			Assert.Contains(line, csharp);
		Assert.DoesNotContain("// unsupported", csharp);
		Assert.True(MxamlDocument.Parse(source.Replace("DockPanel.Dock=\"Top\"", "DockPanel.Dock=\"Middle\"")).HasErrors);
		Assert.False(doc.SetProperty("corner", "DockPanel.Dock", "Left"));
	}
}
