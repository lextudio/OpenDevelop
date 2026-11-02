using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>The shared context-menu vocabulary and rules every designer composes its menu from.</summary>
public sealed class DesignerStandardCommandsTests
{
	static DesignerElementNode Node(string id, params DesignerElementNode[] children)
	{
		var node = new DesignerElementNode { Id = id, Name = id };
		node.Children.AddRange(children);
		return node;
	}

	[Fact]
	public void EveryMenuEntry_ReadsBackToItsOwnMeaning()
	{
		foreach (var (header, command) in DesignerStandardCommands.AlignmentMenu) {
			Assert.NotNull(DesignerStandardCommands.Alignment(command));
			Assert.EndsWith(DesignerStandardCommands.Alignment(command)!.Value.Value, header);
		}
		foreach (var (header, command) in DesignerStandardCommands.GridUnitMenu) {
			var unit = DesignerStandardCommands.GridUnit(command);
			Assert.NotNull(unit);
			Assert.StartsWith(unit!.Value.IsRow ? "Grid Row/" : "Grid Column/", header);
		}
		foreach (var (_, command) in DesignerStandardCommands.MoveMenu)
			Assert.True(DesignerStandardCommands.IsMoveCommand(command));
		var all = DesignerStandardCommands.AlignmentMenu.Concat(DesignerStandardCommands.GridUnitMenu).Concat(DesignerStandardCommands.MoveMenu)
			.Concat(DesignerStandardCommands.EditMenu(true)).Where(e => e.Header != "-").Select(e => e.Command).ToArray();
		Assert.Equal(all.Length, all.Distinct().Count());
	}

	[Fact]
	public void Parsing_RejectsWhatTheMenuDoesNotOffer()
	{
		Assert.Equal(("HorizontalAlignment", "Center"), DesignerStandardCommands.Alignment("set:HorizontalAlignment:Center"));
		Assert.Null(DesignerStandardCommands.Alignment("set:VerticalAlignment:Left"));
		Assert.Null(DesignerStandardCommands.Alignment("set:Width:10"));
		Assert.Equal((true, "Pixel"), DesignerStandardCommands.GridUnit("grid-row-Pixel"));
		Assert.Equal((false, "Star"), DesignerStandardCommands.GridUnit("grid-column-Star"));
		Assert.Null(DesignerStandardCommands.GridUnit("grid-row-Fixed"));
		Assert.Null(DesignerStandardCommands.GridUnit("grid-cell-Auto"));
	}

	[Fact]
	public void EditMenu_HasPasteHereOnlyWhenAsked()
	{
		Assert.Contains(DesignerStandardCommands.EditMenu(true), e => e.Command == "paste-here");
		Assert.DoesNotContain(DesignerStandardCommands.EditMenu(false), e => e.Command == "paste-here");
		Assert.Equal("delete", DesignerStandardCommands.EditMenu(false)[^1].Command);
	}

	[Fact]
	public void WrapMenuAndContainer_MatchCaseInsensitivelyAndReturnTheSpellingGiven()
	{
		var menu = DesignerStandardCommands.WrapMenu("Grid", "StackPanel");
		Assert.Equal(new[] { "wrap-Grid", "wrap-StackPanel", "unwrap" }, menu.Select(e => e.Command));
		Assert.Equal("StackPanel", DesignerStandardCommands.WrapContainer("wrap-stackpanel", new[] { "Grid", "StackPanel" }));
		Assert.Equal("Grid", DesignerStandardCommands.WrapContainer("wrap-Grid", new[] { "Grid" }));
		Assert.Null(DesignerStandardCommands.WrapContainer("wrap-bogus", new[] { "Grid" }));
		Assert.Null(DesignerStandardCommands.WrapContainer("unwrap", new[] { "Grid" }));
		Assert.Null(DesignerStandardCommands.WrapContainer(null, new[] { "Grid" }));
	}

	[Theory]
	[InlineData("move-first", 2, 4, -2)]
	[InlineData("move-first", 0, 4, 0)]
	[InlineData("move-earlier", 2, 4, -1)]
	[InlineData("move-earlier", 0, 4, 0)]
	[InlineData("move-later", 2, 4, 1)]
	[InlineData("move-later", 3, 4, 0)]
	[InlineData("move-last", 1, 4, 2)]
	[InlineData("move-last", 3, 4, 0)]
	[InlineData("bogus", 1, 4, 0)]
	public void MoveDelta_AndCanMove_AgreeOnWhatCanMove(string command, int index, int count, int delta)
	{
		Assert.Equal(delta, DesignerStandardCommands.MoveDelta(command, index, count));
		Assert.Equal(delta != 0, DesignerStandardCommands.CanMove(command, index, count));
	}

	[Fact]
	public void WrapAndUnwrapRules()
	{
		Assert.True(DesignerStandardCommands.CanWrap(2, true, containerHoldsOneChild: false));
		Assert.False(DesignerStandardCommands.CanWrap(2, true, containerHoldsOneChild: true));
		Assert.True(DesignerStandardCommands.CanWrap(1, true, containerHoldsOneChild: true));
		Assert.False(DesignerStandardCommands.CanWrap(1, false, containerHoldsOneChild: false));
		Assert.False(DesignerStandardCommands.CanWrap(0, true, containerHoldsOneChild: false));
		Assert.True(DesignerStandardCommands.CanUnwrap(1, true, true));
		Assert.False(DesignerStandardCommands.CanUnwrap(2, true, true));
		Assert.False(DesignerStandardCommands.CanUnwrap(1, false, true));
		Assert.False(DesignerStandardCommands.CanUnwrap(1, true, false));
	}

	[Fact]
	public void TreeQueries_FindParentsSiblingsAndSharedParents()
	{
		var root = Node("root", Node("a", Node("a1"), Node("a2")), Node("b"), Node("c"));
		Assert.Equal(new[] { "root", "a", "a1", "a2", "b", "c" }, DesignerStandardCommands.Flatten(root).Select(n => n.Id));
		Assert.Equal("a", DesignerStandardCommands.ParentOf(root, "a2")!.Id);
		Assert.Null(DesignerStandardCommands.ParentOf(root, "root"));
		Assert.Equal((1, 3), DesignerStandardCommands.SiblingPosition(root, new[] { "b" }));
		Assert.Null(DesignerStandardCommands.SiblingPosition(root, new[] { "root" }));
		Assert.Null(DesignerStandardCommands.SiblingPosition(root, new[] { "a", "b" }));
		Assert.Null(DesignerStandardCommands.SiblingPosition(null, new[] { "a" }));
		Assert.True(DesignerStandardCommands.SharesOneParent(root, new[] { "b", "c", "a" }));
		Assert.False(DesignerStandardCommands.SharesOneParent(root, new[] { "a1", "b" }));
		Assert.False(DesignerStandardCommands.SharesOneParent(root, new[] { "root" }));
		Assert.False(DesignerStandardCommands.SharesOneParent(root, Array.Empty<string>()));
	}

	[Fact]
	public void NearestContainer_ReorderDelta_AndSelectedHasChildren()
	{
		var a1 = Node("a1"); var a2 = Node("a2"); var a = Node("a", a1, a2); var b = Node("b");
		var root = Node("root", a, b);
		Func<DesignerElementNode, bool> isContainer = node => node.Id is "a" or "root";
		Assert.Equal("a", DesignerStandardCommands.NearestContainer(root, a1, isContainer)!.Id);
		Assert.Equal("a", DesignerStandardCommands.NearestContainer(root, a, isContainer)!.Id);
		Assert.Equal("root", DesignerStandardCommands.NearestContainer(root, b, isContainer)!.Id);
		Assert.Null(DesignerStandardCommands.NearestContainer(root, b, _ => false));
		Assert.Null(DesignerStandardCommands.NearestContainer(null, b, isContainer));
		Assert.Equal(1, DesignerStandardCommands.ReorderDelta(root, a1, a2));
		Assert.Equal(-1, DesignerStandardCommands.ReorderDelta(root, a2, a1));
		Assert.Equal(0, DesignerStandardCommands.ReorderDelta(root, a1, a1));
		Assert.Equal(0, DesignerStandardCommands.ReorderDelta(root, a1, b));
		Assert.True(DesignerStandardCommands.SelectedHasChildren(root, new[] { "a" }));
		Assert.False(DesignerStandardCommands.SelectedHasChildren(root, new[] { "b" }));
		Assert.False(DesignerStandardCommands.SelectedHasChildren(root, new[] { "a", "b" }));
		Assert.False(DesignerStandardCommands.SelectedHasChildren(null, new[] { "a" }));
	}
}
