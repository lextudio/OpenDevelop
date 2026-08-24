using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Shell;
using Xunit;

namespace ICSharpCode.FormsDesigner.Host.Tests;

public sealed class DesignerSelectionControllerTests
{
	[Fact]
	public void RebuildRestoresSelectionByStableIdAndRecreatesPropertyAdapter()
	{
		var adapters = 0;
		var controller = new DesignerSelectionController(node => $"adapter-{node.Id}-{++adapters}");
		controller.UpdateTree(Tree("root", Tree("button")));
		Assert.True(controller.Select("button"));
		var original = controller.SelectedPropertyObject;

		controller.UpdateTree(Tree("root", Tree("button"), Tree("label")));

		Assert.Equal("button", controller.SelectedId);
		Assert.NotEqual(original, controller.SelectedPropertyObject);
		Assert.Equal(2, adapters);
	}

	[Fact]
	public void RebuildClearsRemovedSelectionAndRejectsUnknownId()
	{
		var controller = new DesignerSelectionController();
		controller.UpdateTree(Tree("root", Tree("button")));
		Assert.True(controller.Select("button"));
		controller.UpdateTree(Tree("root"));
		Assert.Null(controller.SelectedNode);
		Assert.False(controller.Select("missing"));
	}

	[Fact]
	public void RebuildRestoresPreferredSelectionWhenNodeReturns()
	{
		var controller = new DesignerSelectionController();
		controller.UpdateTree(Tree("root", Tree("button")));
		Assert.True(controller.Select("button"));
		controller.UpdateTree(Tree("root"));
		Assert.Null(controller.SelectedNode);

		controller.UpdateTree(Tree("root", Tree("button")));

		Assert.Equal("button", controller.SelectedId);
	}

	[Fact]
	public void SupportsForestsAndEmptyRootId()
	{
		var controller = new DesignerSelectionController();
		controller.UpdateRoots(new[] { Tree(""), Tree("dialog") });
		Assert.True(controller.Select(""));
		Assert.Equal("", controller.SelectedId);
		Assert.Equal(2, controller.Roots.Count);
	}

	static DesignerElementNode Tree(string id, params DesignerElementNode[] children) => new() { Id = id, Name = id, Type = "Control", Children = children.ToList() };
}

public sealed class DesignerCommandControllerTests
{
	[Fact]
	public void ExecuteHonorsEnablementAndInvalidatesState()
	{
		var enabled = false;
		var executions = 0;
		var changes = 0;
		var controller = new DesignerCommandController();
		controller.StateChanged += (_, _) => changes++;
		controller.Register("Undo", () => enabled, () => { executions++; return true; });

		Assert.False(controller.Execute("Undo"));
		enabled = true;
		controller.Invalidate();
		Assert.True(controller.Execute("Undo"));
		Assert.Equal(1, executions);
		Assert.Equal(3, changes);
	}

	[Fact]
	public void ExecuteRejectsReentrantCommand()
	{
		var nested = true;
		var controller = new DesignerCommandController();
		controller.Register("Delete", () => true, () => { nested = controller.Execute("Delete"); return true; });

		Assert.True(controller.Execute("Delete"));
		Assert.False(nested);
	}
}
