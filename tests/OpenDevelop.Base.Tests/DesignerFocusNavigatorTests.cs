using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class DesignerFocusNavigatorTests
{
	[Fact]
	public void Next_UsesPreOrderAndWrapsAtBothEnds()
	{
		var root = Node("root");
		var first = Node("first");
		var second = Node("second");
		var nested = Node("nested");
		root.Children.Add(first);
		root.Children.Add(second);
		second.Children.Add(nested);

		Assert.Equal("first", Next(root, "root"));
		Assert.Equal("second", Next(root, "first"));
		Assert.Equal("nested", Next(root, "second"));
		Assert.Equal("root", Next(root, "nested"));
		Assert.Equal("nested", Next(root, "root", backwards: true));
		Assert.Equal("root", Next(root, "first", backwards: true));
	}

	[Fact]
	public void Next_SkipsHiddenNonDesignableAndUnselectableNodes()
	{
		var root = Node("root");
		var hidden = Node("hidden", visible: false);
		hidden.Children.Add(Node("staleVisibleChild"));
		var templatePart = Node("part", designable: false);
		var unavailable = Node("unavailable");
		var target = Node("target");
		root.Children.Add(hidden);
		root.Children.Add(templatePart);
		root.Children.Add(unavailable);
		root.Children.Add(target);

		Assert.Equal("target", DesignerFocusNavigator.Next(root, "root", backwards: false,
			node => node.Name, key => key is "root" or "target"));
		Assert.Equal("root", DesignerFocusNavigator.Next(root, "target", backwards: false,
			node => node.Name, key => key is "root" or "target"));
	}

	[Fact]
	public void Next_UsesNearestEndForMissingSelectionAndToleratesCycles()
	{
		var root = Node("root");
		var child = Node("child");
		root.Children.Add(child);
		child.Children.Add(root);

		Assert.Equal("root", Next(root, "removed"));
		Assert.Equal("child", Next(root, "removed", backwards: true));
	}

	[Fact]
	public void Next_DoesNotEnterAHiddenAncestorsSubtree()
	{
		var root = Node("root");
		var hidden = Node("hidden", visible: false);
		hidden.Children.Add(Node("staleVisibleChild"));
		root.Children.Add(hidden);
		root.Children.Add(Node("target"));

		Assert.Equal("target", Next(root, "root"));
	}

	[Fact]
	public void Next_ReturnsNullForNoSnapshotAndWrapsASingleSelectableNode()
	{
		Assert.Null(DesignerFocusNavigator.Next(null, "anything", backwards: false,
			node => node.Name, _ => true));
		var root = Node("root");
		Assert.Equal("root", Next(root, "root"));
		Assert.Equal("root", Next(root, "root", backwards: true));
	}

	static string? Next(DesignerElementNode root, string? current, bool backwards = false)
		=> DesignerFocusNavigator.Next(root, current, backwards, node => node.Name, _ => true);

	static DesignerElementNode Node(string name, bool visible = true, bool designable = true)
		=> new() { Name = name, IsVisible = visible, IsDesignable = designable };
}
