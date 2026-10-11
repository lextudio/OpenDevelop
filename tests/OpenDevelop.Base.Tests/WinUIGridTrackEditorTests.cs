using System.Xml.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WinUIXamlDesigner;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class WinUIGridTrackEditorTests
{
	[Fact]
	public void SplitRow_PreservesTrackUnitAndAdjustsChildCells()
	{
		var grid = XElement.Parse("""
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
			  <Grid.RowDefinitions><RowDefinition Height="2*"/><RowDefinition Height="40"/></Grid.RowDefinitions>
			  <Button x:Name="leading" Grid.Row="0"/>
			  <Button x:Name="later" Grid.Row="1"/>
			  <Button x:Name="spanning" Grid.Row="0" Grid.RowSpan="2"/>
			</Grid>
			""");
		var tracks = new[] {
			new DesignerGridTrackInfo { Offset = 0, Size = 80 },
			new DesignerGridTrackInfo { Offset = 80, Size = 40 }
		};

		Assert.True(WinUIGridTrackEditor.TrySplit(grid, isRow: true, tracks, position: 20, out var error), error);
		var definitions = grid.Element(grid.Name.Namespace + "Grid.RowDefinitions")!.Elements().ToArray();
		Assert.Equal(new[] { "0.5*", "1.5*", "40" }, definitions.Select(e => (string?)e.Attribute("Height")));
		Assert.Equal("2", (string?)grid.Elements().Single(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "leading").Attribute("Grid.RowSpan"));
		Assert.Equal("2", (string?)grid.Elements().Single(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "later").Attribute("Grid.Row"));
		Assert.Equal("3", (string?)grid.Elements().Single(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "spanning").Attribute("Grid.RowSpan"));
	}

	[Fact]
	public void SplitAutoColumn_UsesMeasuredPixels()
	{
		var grid = XElement.Parse("""<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions></Grid>""");
		var tracks = new[] { new DesignerGridTrackInfo { Offset = 0, Size = 90 } };

		Assert.True(WinUIGridTrackEditor.TrySplit(grid, isRow: false, tracks, position: 30, out var error), error);
		var widths = grid.Descendants().Where(e => e.Name.LocalName == "ColumnDefinition").Select(e => (string?)e.Attribute("Width"));
		Assert.Equal(new[] { "30", "60" }, widths);
	}

	[Fact]
	public void SplitMovesChildrenInsideAGridChildrenPropertyElement()
	{
		var grid = XElement.Parse("""
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
			  <Grid.ColumnDefinitions><ColumnDefinition Width="60"/><ColumnDefinition Width="60"/></Grid.ColumnDefinitions>
			  <Grid.Children><Button x:Name="later" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Grid.Column="1"/></Grid.Children>
			</Grid>
			""");

		Assert.True(WinUIGridTrackEditor.TrySplit(grid, isRow: false, new[] {
			new DesignerGridTrackInfo { Offset = 0, Size = 60 }, new DesignerGridTrackInfo { Offset = 60, Size = 60 }
		}, position: 30, out var error), error);
		Assert.Equal("2", (string?)grid.Descendants().Single(e => e.Name.LocalName == "Button").Attribute("Grid.Column"));
	}

	[Fact]
	public void SplitDoesNotTreatGridResourcesAsAChild()
	{
		var grid = XElement.Parse("""
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
			  <Grid.RowDefinitions><RowDefinition Height="60"/></Grid.RowDefinitions>
			  <Grid.Resources><SolidColorBrush x:Key="Accent" Color="Red" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"/></Grid.Resources>
			</Grid>
			""");

		Assert.True(WinUIGridTrackEditor.TrySplit(grid, isRow: true,
			new[] { new DesignerGridTrackInfo { Offset = 0, Size = 60 } }, position: 30, out var error), error);
		Assert.Null(grid.Element(grid.Name.Namespace + "Grid.Resources")!.Attribute("Grid.RowSpan"));
	}

	[Fact]
	public void SplitRejectsBoundaryWithoutChangingSource()
	{
		var grid = XElement.Parse("""<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Grid.RowDefinitions><RowDefinition Height="50"/></Grid.RowDefinitions></Grid>""");
		var before = grid.ToString(SaveOptions.DisableFormatting);

		Assert.False(WinUIGridTrackEditor.TrySplit(grid, isRow: true,
			new[] { new DesignerGridTrackInfo { Offset = 0, Size = 50 } }, position: 0, out _));
		Assert.Equal(before, grid.ToString(SaveOptions.DisableFormatting));
	}

	[Fact]
	public void SplitRejectsAnExpressionLengthWithoutChangingSource()
	{
		var grid = XElement.Parse("""<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Grid.RowDefinitions><RowDefinition Height="{Binding RowHeight}"/></Grid.RowDefinitions></Grid>""");
		var before = grid.ToString(SaveOptions.DisableFormatting);

		Assert.False(WinUIGridTrackEditor.TrySplit(grid, isRow: true,
			new[] { new DesignerGridTrackInfo { Offset = 0, Size = 50 } }, position: 25, out _));
		Assert.Equal(before, grid.ToString(SaveOptions.DisableFormatting));
	}

	[Fact]
	public void SplitRejectsAnExpressionSpanWithoutChangingSource()
	{
		var grid = XElement.Parse("""<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Grid.RowDefinitions><RowDefinition Height="50"/></Grid.RowDefinitions><Button Grid.RowSpan="{Binding Span}"/></Grid>""");
		var before = grid.ToString(SaveOptions.DisableFormatting);

		Assert.False(WinUIGridTrackEditor.TrySplit(grid, isRow: true,
			new[] { new DesignerGridTrackInfo { Offset = 0, Size = 50 } }, position: 25, out _));
		Assert.Equal(before, grid.ToString(SaveOptions.DisableFormatting));
	}

	[Fact]
	public void SplitUpdatesAttachedPropertyElement()
	{
		var grid = XElement.Parse("""<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Grid.RowDefinitions><RowDefinition Height="50"/><RowDefinition Height="50"/></Grid.RowDefinitions><Button><Grid.Row>1</Grid.Row></Button></Grid>""");

		Assert.True(WinUIGridTrackEditor.TrySplit(grid, isRow: true, new[] {
			new DesignerGridTrackInfo { Offset = 0, Size = 50 }, new DesignerGridTrackInfo { Offset = 50, Size = 50 }
		}, position: 25, out var error), error);
		Assert.Equal("2", grid.Descendants().Single(e => e.Name.LocalName == "Grid.Row").Value);
	}

	[Fact]
	public void SplitThroughDocumentEditor_IsUndoableAndRedoable()
	{
		const string source = """<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Grid.RowDefinitions><RowDefinition Height="50"/></Grid.RowDefinitions></Grid>""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(source, out var resetError), resetError);
		string error = null!;

		Assert.True(editor.TryMutate(() => WinUIGridTrackEditor.TrySplit(editor.Document.Root!, isRow: true,
			new[] { new DesignerGridTrackInfo { Offset = 0, Size = 50 } }, position: 25, out error)), error);
		var changed = editor.Text;
		Assert.Contains("Height=\"25\"", changed);

		Assert.True(editor.Undo());
		Assert.Equal(source, editor.Text);
		Assert.True(editor.Redo());
		Assert.Equal(changed, editor.Text);
	}

	[Fact]
	public void ContextFlyoutInsertion_IsSourceOwnedAndUndoable()
	{
		const string source = """<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><Button x:Name="Target"/></Grid>""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(source, out var error), error);

		var flyout = editor.InsertContextFlyout(editor.FindElement("Target")!);
		Assert.Equal("MenuFlyout1", (string?)flyout.Attribute(WinUIXamlDocumentEditor.NameDirective));
		Assert.Contains("<Button.ContextFlyout>", editor.Text);
		Assert.Contains("<MenuFlyoutItem Text=\"Menu item\"", editor.Text);

		Assert.True(editor.Undo());
		Assert.Equal(source, editor.Text);
		Assert.True(editor.Redo());
		Assert.Contains("<Button.ContextFlyout>", editor.Text);
		Assert.Throws<InvalidOperationException>(() => editor.InsertContextFlyout(editor.FindElement("Target")!));
	}

	[Fact]
	public void ContextFlyoutInsertion_RejectsAnExistingAttributeAssignment()
	{
		const string source = """<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><Button x:Name="Target" ContextFlyout="{StaticResource ExistingFlyout}"/></Grid>""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(source, out var error), error);

		Assert.Throws<InvalidOperationException>(() => editor.InsertContextFlyout(editor.FindElement("Target")!));
		Assert.Equal(source, editor.Text);
	}

	[Fact]
	public void MenuFlyoutItemInsertion_IsSourceOwnedAndUndoable()
	{
		const string source = """<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><Button x:Name="Target"><Button.ContextFlyout><MenuFlyout x:Name="MenuFlyout1"><MenuFlyoutItem Text="First"/></MenuFlyout></Button.ContextFlyout></Button></Grid>""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(source, out var error), error);

		var item = editor.InsertMenuFlyoutItem(editor.FindElement("MenuFlyout1")!);
		Assert.Equal("MenuFlyoutItem1", (string?)item.Attribute(WinUIXamlDocumentEditor.NameDirective));
		Assert.Equal("Menu item", (string?)item.Attribute("Text"));
		Assert.Equal(2, editor.FindElement("MenuFlyout1")!.Elements().Count());

		Assert.True(editor.Undo());
		Assert.Equal(source, editor.Text);
		Assert.True(editor.Redo());
		Assert.Contains("x:Name=\"MenuFlyoutItem1\"", editor.Text);
	}

	[Fact]
	public void MenuFlyoutItemInsertion_RejectsAVisualControl()
	{
		const string source = """<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><Button x:Name="Target"/></Grid>""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(source, out var error), error);

		Assert.Throws<ArgumentException>(() => editor.InsertMenuFlyoutItem(editor.FindElement("Target")!));
		Assert.Equal(source, editor.Text);
	}

	[Fact]
	public void MoveToEdge_CommitsAndRoundTripsACollectionItem()
	{
		const string source = """<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><Button x:Name="Target"><Button.ContextFlyout><MenuFlyout x:Name="MenuFlyout1"><MenuFlyoutItem x:Name="First" Text="First"/><MenuFlyoutItem x:Name="Second" Text="Second"/><MenuFlyoutItem x:Name="Third" Text="Third"/></MenuFlyout></Button.ContextFlyout></Button></Grid>""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(source, out var error), error);

		Assert.True(editor.MoveToEdge(editor.FindElement("Second")!, front: true));
		var moved = editor.Text;
		Assert.Equal(new[] { "First", "Third", "Second" }, editor.FindElement("MenuFlyout1")!.Elements()
			.Select(item => (string?)item.Attribute(WinUIXamlDocumentEditor.NameDirective)));
		Assert.True(editor.Undo());
		Assert.Equal(source, editor.Text);
		Assert.True(editor.Redo());
		Assert.Equal(moved, editor.Text);

		Assert.True(editor.MoveToEdge(editor.FindElement("Third")!, front: false));
		Assert.Equal(new[] { "Third", "First", "Second" }, editor.FindElement("MenuFlyout1")!.Elements()
			.Select(item => (string?)item.Attribute(WinUIXamlDocumentEditor.NameDirective)));
		Assert.False(editor.MoveToEdge(editor.FindElement("Third")!, front: false));
	}

	[Fact]
	public void MoveToEdge_LeavesTheNamedRootUntouched()
	{
		const string source = """<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="Root"><Button x:Name="Child"/></Grid>""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(source, out var error), error);

		Assert.False(editor.MoveToEdge(editor.FindElement("Root")!, front: true));
		Assert.Equal(source, editor.Text);
		Assert.False(editor.CanUndo);
	}

	[Fact]
	public void Paste_IsSourceOwnedAndUndoable()
	{
		const string source = """<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><Button x:Name="Target"/></Grid>""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(source, out var error), error);

		var pasted = editor.Paste("""<TextBlock Text="Copied"/>""", editor.Document.Root!);
		Assert.Equal("TextBlock1", (string?)pasted.Attribute(WinUIXamlDocumentEditor.NameDirective));
		Assert.Contains("Text=\"Copied\"", editor.Text);
		Assert.True(editor.Undo());
		Assert.Equal(source, editor.Text);
		Assert.True(editor.Redo());
		Assert.Contains("x:Name=\"TextBlock1\"", editor.Text);
	}

	[Fact]
	public void WrapInContainer_PreservesSiblingPositionAndUndo()
	{
		const string source = """<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><TextBlock x:Name="Before"/><Button x:Name="Target"/><TextBlock x:Name="After"/></Grid>""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(source, out var error), error);

		var wrapper = editor.WrapInContainer(editor.FindElement("Target")!, "StackPanel");
		Assert.Equal("StackPanel1", (string?)wrapper.Attribute(WinUIXamlDocumentEditor.NameDirective));
		Assert.Equal(new[] { "TextBlock", "StackPanel", "TextBlock" }, editor.Document.Root!.Elements().Select(element => element.Name.LocalName));
		Assert.Equal("Target", (string?)wrapper.Elements().Single().Attribute(WinUIXamlDocumentEditor.NameDirective));
		Assert.True(editor.Undo());
		Assert.Equal(source, editor.Text);
		Assert.False(editor.WrapInContainer(editor.Document.Root!, "Grid") is not null);
	}

	[Fact]
	public void Reparent_IsSourceOwnedUndoableAndRejectsCycles()
	{
		const string source = """<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><StackPanel x:Name="Left"><Button x:Name="Target"/></StackPanel><StackPanel x:Name="Right"/></Grid>""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(source, out var error), error);

		Assert.True(editor.Reparent(editor.FindElement("Target")!, editor.FindElement("Right")!));
		var changed = editor.Text;
		Assert.Empty(editor.FindElement("Left")!.Elements());
		Assert.Equal("Target", (string?)editor.FindElement("Right")!.Elements().Single().Attribute(WinUIXamlDocumentEditor.NameDirective));
		Assert.True(editor.Undo());
		Assert.Equal(source, editor.Text);
		Assert.True(editor.Redo());
		Assert.Equal(changed, editor.Text);

		Assert.False(editor.Reparent(editor.FindElement("Right")!, editor.FindElement("Target")!));
		Assert.False(editor.Reparent(editor.Document.Root!, editor.FindElement("Right")!));
	}

	[Fact]
	public void ContextFlyoutDeletion_RemovesItsEmptyPropertyElementInOneUndoEntry()
	{
		const string source = """<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><Button x:Name="Target"/></Grid>""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(source, out var error), error);
		var flyout = editor.InsertContextFlyout(editor.FindElement("Target")!);
		var withFlyout = editor.Text;

		editor.Remove(flyout);
		Assert.DoesNotContain("ContextFlyout", editor.Text);
		Assert.True(editor.Undo());
		Assert.Equal(withFlyout, editor.Text);
		Assert.True(editor.Redo());
		Assert.Equal(XDocument.Parse(source).ToString(SaveOptions.DisableFormatting),
			XDocument.Parse(editor.Text).ToString(SaveOptions.DisableFormatting));
	}

	[Fact]
	public void ContextFlyoutDeletion_RemovesAWhitespaceOnlyHandAuthoredPropertyElement()
	{
		const string source = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
			  <Button x:Name="Target">
			    <Button.ContextFlyout>
			      <MenuFlyout x:Name="HandAuthoredFlyout">
			        <MenuFlyoutItem Text="Menu item"/>
			      </MenuFlyout>
			    </Button.ContextFlyout>
			  </Button>
			</Grid>
			""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(source, out var error), error);

		editor.Remove(editor.FindElement("HandAuthoredFlyout")!);
		Assert.DoesNotContain("ContextFlyout", editor.Text);
		Assert.True(editor.Undo());
		Assert.Equal(source, editor.Text);
		Assert.True(editor.Redo());
		Assert.DoesNotContain("ContextFlyout", editor.Text);
	}
}
