using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class DesignerDragGroupPlannerTests
{
	[Fact]
	public void SameParentGroup_UsesOnlyThePrimarysDirectSiblings()
	{
		var root = Node("root");
		var firstParent = Node("firstParent");
		var secondParent = Node("secondParent");
		var primary = Node("primary");
		var sibling = Node("sibling");
		var other = Node("other");
		var parents = new Dictionary<string, DesignerElementNode?> {
			["root"] = null, ["firstParent"] = root, ["secondParent"] = root,
			["primary"] = firstParent, ["sibling"] = firstParent, ["other"] = secondParent
		};

		var group = DesignerDragGroupPlanner.SameParentGroup(
			new[] { "primary", "other", "sibling", "sibling" }, "primary", parents);

		Assert.Equal(new[] { "primary", "sibling" }, group);
	}

	[Fact]
	public void CollectMovingSubtreeKeys_ExcludesNamedDescendantsThroughUnnamedNodes()
	{
		var root = Node("root");
		var unnamed = new DesignerElementNode();
		var namedGrandchild = Node("namedGrandchild");
		root.Children.Add(unnamed);
		unnamed.Children.Add(namedGrandchild);
		var nodes = new Dictionary<string, DesignerElementNode> { ["root"] = root, ["namedGrandchild"] = namedGrandchild };

		var moving = DesignerDragGroupPlanner.CollectMovingSubtreeKeys(new[] { "root" }, nodes, node => node.Name);

		Assert.Equal(new[] { "namedGrandchild", "root" }, moving.OrderBy(key => key));
	}

	[Fact]
	public void CollectMovingSubtreeKeys_ToleratesAnAccidentalCycle()
	{
		var root = Node("root");
		var child = Node("child");
		root.Children.Add(child);
		child.Children.Add(root);
		var nodes = new Dictionary<string, DesignerElementNode> { ["root"] = root, ["child"] = child };

		var moving = DesignerDragGroupPlanner.CollectMovingSubtreeKeys(new[] { "root" }, nodes, node => node.Name);

		Assert.Equal(2, moving.Count);
	}

	static DesignerElementNode Node(string name) => new() { Name = name };
}
