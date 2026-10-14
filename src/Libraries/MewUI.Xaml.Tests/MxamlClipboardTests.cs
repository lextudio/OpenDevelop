using LeXtudio.MewUI.Xaml;
using Xunit;

public sealed class MxamlClipboardTests
{
	const string Source = """
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow" Name="mainWindow">
  <StackPanel Name="rootPanel">
    <Label Name="heading" Text="Notes"/>
    <StackPanel Name="toolRow" Orientation="Horizontal">
      <Button Name="newButton" Content="New" Click="NewButton_Click"/>
    </StackPanel>
    <Canvas Name="board">
      <Button Name="pin" Canvas.Left="10" Canvas.Top="20" Content="Pin"/>
    </Canvas>
  </StackPanel>
</Window>
""";

	static MxamlDocument NewDoc() { var d = MxamlDocument.Parse(Source); Assert.False(d.HasErrors, string.Join("\n", d.Diagnostics)); return d; }

	[Fact]
	public void CopyTakesOuterSelectionsAndRejectsTheRoot()
	{
		var doc = NewDoc();
		Assert.Null(doc.CopySnippets(new[] { "mainWindow" }));
		Assert.Null(doc.CopySnippets(new[] { "missing" }));
		var snippet = Assert.Single(doc.CopySnippets(new[] { "toolRow", "newButton" })!);
		Assert.StartsWith("<StackPanel Name=\"toolRow\"", snippet);
		Assert.Contains("Name=\"newButton\"", snippet);
	}

	[Fact]
	public void PasteMakesNamesUniqueKeepsHandlersAndUndoesInOneStep()
	{
		var doc = NewDoc();
		var before = doc.ToXaml();
		var snippets = doc.CopySnippets(new[] { "toolRow" })!;
		// Pasting onto a leaf goes to its container, after the given sibling.
		Assert.True(doc.Paste("heading", snippets, out var created, afterName: "heading"));
		Assert.Equal(new[] { "toolRow1" }, created);
		Assert.Equal(new[] { "heading", "toolRow1", "toolRow", "board" }, doc.Root.Children[0].Children.Select(c => c.Name));
		Assert.Equal("newButton1", doc.Find("toolRow1")!.Children[0].Name);
		Assert.Equal("NewButton_Click", doc.Find("newButton1")!.FindAttribute("Click")!.Value);
		Assert.True(doc.Undo());
		Assert.Equal(before, doc.ToXaml());
	}

	[Fact]
	public void PasteKeepsCanvasPositionsOnlyOnACanvas()
	{
		var doc = NewDoc();
		var pin = doc.CopySnippets(new[] { "pin" })!;
		Assert.True(doc.Paste("board", pin, out var onCanvas));
		Assert.Equal("10", doc.Find(onCanvas[0])!.FindAttribute("Canvas.Left")!.Value);
		Assert.True(doc.Paste("toolRow", pin, out var inStack));
		Assert.Null(doc.Find(inStack[0])!.FindAttribute("Canvas.Left"));
		Assert.Null(doc.Find(inStack[0])!.FindAttribute("Canvas.Top"));
	}

	[Fact]
	public void PasteRejectsTextThatIsNotMxamlWithoutAnUndoStep()
	{
		var doc = NewDoc();
		var before = doc.ToXaml();
		Assert.False(doc.Paste("rootPanel", new[] { "not xml <" }, out _));
		Assert.False(doc.Paste("rootPanel", new[] { "<Button Content=\"No name\"/>" }, out _));
		Assert.Equal(before, doc.ToXaml());
		Assert.False(doc.Undo());
	}
}

public sealed class MxamlWrapTests
{
	const string Source = """
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow" Name="mainWindow">
  <StackPanel Name="rootPanel">
    <Label Name="heading" Text="Notes"/>
    <Button Name="okButton" Content="OK"/>
    <Button Name="cancelButton" Content="Cancel"/>
    <Canvas Name="board">
      <Button Name="pin" Canvas.Left="10" Canvas.Top="20" Content="Pin"/>
    </Canvas>
  </StackPanel>
</Window>
""";

	static MxamlDocument NewDoc() { var d = MxamlDocument.Parse(Source); Assert.False(d.HasErrors, string.Join("\n", d.Diagnostics)); return d; }

	[Fact]
	public void WrapsSiblingsInPlaceKeepingOrderInOneUndoStep()
	{
		var doc = NewDoc();
		var before = doc.ToXaml();
		Assert.True(doc.Wrap(new[] { "cancelButton", "okButton" }, "StackPanel", out var created));
		Assert.Equal("stackPanel1", created);
		Assert.Equal(new[] { "heading", "stackPanel1", "board" }, doc.Find("rootPanel")!.Children.Select(c => c.Name));
		Assert.Equal(new[] { "okButton", "cancelButton" }, doc.Find("stackPanel1")!.Children.Select(c => c.Name));
		Assert.True(doc.Undo());
		Assert.Equal(before, doc.ToXaml());
	}

	[Fact]
	public void TheWrapperTakesOverAttachedPlacement()
	{
		var doc = NewDoc();
		Assert.True(doc.Wrap(new[] { "pin" }, "Border", out var created));
		var border = doc.Find(created)!;
		Assert.Equal("10", border.FindAttribute("Canvas.Left")!.Value);
		Assert.Equal("20", border.FindAttribute("Canvas.Top")!.Value);
		Assert.Null(doc.Find("pin")!.FindAttribute("Canvas.Left"));
	}

	[Fact]
	public void RefusesTheRootMixedParentsAndSeveralIntoASingleChildHost()
	{
		var doc = NewDoc();
		var before = doc.ToXaml();
		Assert.False(doc.Wrap(new[] { "mainWindow" }, "StackPanel", out _));
		Assert.False(doc.Wrap(new[] { "heading", "pin" }, "StackPanel", out _));
		Assert.False(doc.Wrap(new[] { "okButton", "cancelButton" }, "Border", out _));
		Assert.False(doc.Wrap(new[] { "okButton" }, "Label", out _));
		Assert.Equal(before, doc.ToXaml());
		Assert.False(doc.Undo());
	}
}

public sealed class MxamlUnwrapTests
{
	const string Source = """
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026" Class="App.MainWindow" Name="mainWindow">
  <StackPanel Name="rootPanel">
    <Label Name="heading" Text="Notes"/>
    <StackPanel Name="row"><Button Name="ok" Content="OK"/><Button Name="cancel" Content="Cancel"/></StackPanel>
    <Canvas Name="board"><Border Name="frame" Canvas.Left="10" Canvas.Top="20"><Button Name="pin" Content="Pin"/></Border></Canvas>
  </StackPanel>
</Window>
""";

	static MxamlDocument NewDoc() { var d = MxamlDocument.Parse(Source); Assert.False(d.HasErrors, string.Join("\n", d.Diagnostics)); return d; }

	[Fact]
	public void UnwrapPutsChildrenInPlaceInOneUndoStep()
	{
		var doc = NewDoc();
		var before = doc.ToXaml();
		Assert.True(doc.Unwrap("row"));
		Assert.Equal(new[] { "heading", "ok", "cancel", "board" }, doc.Find("rootPanel")!.Children.Select(c => c.Name));
		Assert.Null(doc.Find("row"));
		Assert.True(doc.Undo());
		Assert.Equal(before, doc.ToXaml());
	}

	[Fact]
	public void ALoneChildInheritsTheContainersPlacement()
	{
		var doc = NewDoc();
		Assert.True(doc.Unwrap("frame"));
		Assert.Equal("10", doc.Find("pin")!.FindAttribute("Canvas.Left")!.Value);
		Assert.Equal("20", doc.Find("pin")!.FindAttribute("Canvas.Top")!.Value);
	}

	[Fact]
	public void RefusesRootLeavesAndSeveralChildrenIntoASingleChildHost()
	{
		var doc = NewDoc();
		Assert.False(doc.Unwrap("mainWindow"));
		Assert.False(doc.Unwrap("ok"));
		// The window holds one child; unwrapping rootPanel's four children into it is refused.
		Assert.False(doc.Unwrap("rootPanel"));
		Assert.False(doc.Undo());
	}
}
