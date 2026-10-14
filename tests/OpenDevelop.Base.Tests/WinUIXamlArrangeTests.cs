using System.Xml.Linq;
using ICSharpCode.WinUIXamlDesigner;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class WinUIXamlArrangeTests
{
	const string Page = """
		<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
		  <Grid x:Name="RootGrid">
		    <StackPanel x:Name="Row" Grid.Row="1"><TextBlock x:Name="A"/><Button x:Name="B" Grid.Row="0"/></StackPanel>
		    <Grid x:Name="Inner" Grid.Column="1"><Grid.RowDefinitions><RowDefinition/></Grid.RowDefinitions><Button x:Name="C" Grid.Row="0"/></Grid>
		    <Border x:Name="Empty"/>
		    <Button x:Name="Host"><StackPanel x:Name="Pair"><TextBlock x:Name="D"/><TextBlock x:Name="E"/></StackPanel></Button>
		  </Grid>
		</Page>
		""";

	static string[] Names(XElement e) => e.Elements().Select(c => (string?)c.Attribute(WinUIXamlDocumentEditor.NameDirective) ?? c.Name.LocalName).ToArray();

	[Fact]
	public void UnwrapPutsChildrenInPlaceWithTheContainersPlacementInOneUndoStep()
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(Page, out var error), error);
		Assert.True(editor.Unwrap(editor.FindElement("Row")!));
		Assert.Equal(new[] { "A", "B", "Inner", "Empty", "Host" }, Names(editor.FindElement("RootGrid")!));
		Assert.Equal("1", (string?)editor.FindElement("A")!.Attribute("Grid.Row"));
		Assert.Equal("0", (string?)editor.FindElement("B")!.Attribute("Grid.Row"));

		// A Grid's own definitions and its children's Grid.* placement do not survive it.
		Assert.True(editor.Unwrap(editor.FindElement("Inner")!));
		var c = editor.FindElement("C")!;
		Assert.Null(c.Attribute("Grid.Row"));
		Assert.Equal("1", (string?)c.Attribute("Grid.Column"));
		Assert.DoesNotContain(editor.Document!.Descendants(), e => e.Name.LocalName == "Grid.RowDefinitions");

		editor.Undo();
		editor.Undo();
		Assert.Equal(new[] { "Row", "Inner", "Empty", "Host" }, Names(editor.FindElement("RootGrid")!));
	}

	[Fact]
	public void UnwrapRefusesTheRootControlsEmptyContainersAndManyChildrenIntoContent()
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(Page, out var error), error);
		Assert.False(editor.Unwrap(editor.FindElement("RootGrid")!.Parent!));
		Assert.False(editor.Unwrap(editor.FindElement("Host")!));
		Assert.False(editor.Unwrap(editor.FindElement("Empty")!));
		Assert.False(editor.Unwrap(editor.FindElement("Pair")!));
		Assert.False(editor.CanUndo);
	}

	[Fact]
	public void MoveOneStepSwapsWithTheNeighbouringChildAndSkipsPropertyElements()
	{
		const string grid = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="G">
			  <Grid.RowDefinitions><RowDefinition/></Grid.RowDefinitions>
			  <Button x:Name="A"/><Button x:Name="B"/><Button x:Name="C"/>
			</Grid>
			""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(grid, out var error), error);
		Assert.False(editor.MoveOneStep(editor.FindElement("A")!, forward: false));
		Assert.False(editor.MoveOneStep(editor.FindElement("C")!, forward: true));
		Assert.False(editor.MoveOneStep(editor.FindElement("G")!, forward: true));
		Assert.False(editor.CanUndo);

		Assert.True(editor.MoveOneStep(editor.FindElement("A")!, forward: true));
		Assert.Equal(new[] { "Grid.RowDefinitions", "B", "A", "C" }, Names(editor.FindElement("G")!));
		Assert.True(editor.MoveOneStep(editor.FindElement("C")!, forward: false));
		Assert.Equal(new[] { "Grid.RowDefinitions", "B", "C", "A" }, Names(editor.FindElement("G")!));
		editor.Undo();
		Assert.Equal(new[] { "Grid.RowDefinitions", "B", "A", "C" }, Names(editor.FindElement("G")!));
	}

	[Fact]
	public void CreateEmptyStyleAddsAKeyedStyleToTheRootResourcesInOneUndoStep()
	{
		const string page = """
			<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:local="using:App">
			  <StackPanel x:Name="Panel">
			    <Button x:Name="A"/><Button x:Name="B"/><Button x:Name="Styled" Style="{StaticResource X}"/><local:Card x:Name="Card"/>
			  </StackPanel>
			</Page>
			""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(page, out var error), error);
		Assert.Null(editor.CreateEmptyStyle(editor.Document!.Root!));
		Assert.Null(editor.CreateEmptyStyle(editor.FindElement("Styled")!));
		Assert.False(editor.CanUndo);

		Assert.Equal("ButtonStyle1", editor.CreateEmptyStyle(editor.FindElement("A")!));
		Assert.Equal("ButtonStyle2", editor.CreateEmptyStyle(editor.FindElement("B")!));
		Assert.Equal("CardStyle1", editor.CreateEmptyStyle(editor.FindElement("Card")!));
		var root = editor.Document!.Root!;
		var resources = Assert.Single(root.Elements(), e => e.Name.LocalName == "Page.Resources");
		Assert.Same(resources, root.Elements().First());
		XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
		Assert.Equal(new[] { "ButtonStyle1:Button", "ButtonStyle2:Button", "CardStyle1:local:Card" },
			resources.Elements().Select(e => (string?)e.Attribute(x + "Key") + ":" + (string?)e.Attribute("TargetType")));
		Assert.Equal("{StaticResource ButtonStyle1}", (string?)editor.FindElement("A")!.Attribute("Style"));
		Assert.True(new WinUIXamlDocumentEditor().Reset(editor.Document.ToString(), out error), error);

		editor.Undo();
		Assert.Null(editor.FindElement("Card")!.Attribute("Style"));
		Assert.Equal(2, editor.Document!.Root!.Elements().First().Elements().Count());
	}
}
