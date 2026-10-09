using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class DesignerSnapshotHitTesterTests
{
	[Fact]
	public void FindsTheLaterVisibleSiblingWithoutAnyHostRoundTrip()
	{
		var root = Node("root", 0, 0, 100, 100);
		root.Children.Add(Node("first", 10, 10, 60, 60));
		root.Children.Add(Node("topmost", 20, 20, 60, 60));

		var hit = DesignerSnapshotHitTester.FindNodeAt(root, 30, 30);

		Assert.Equal("topmost", hit!.Name);
	}

	[Fact]
	public void SkipsHiddenAndNonDesignableNodesButCanReachTheirSourceChild()
	{
		var root = Node("root", 0, 0, 100, 100);
		root.Children.Add(Node("hidden", 10, 10, 70, 70, visible: false));
		var template = Node("template", 20, 20, 50, 50, designable: false);
		template.IsTemplatePart = true;
		template.Children.Add(Node("source", 25, 25, 30, 30));
		root.Children.Add(template);

		Assert.Equal("source", DesignerSnapshotHitTester.FindNodeAt(root, 30, 30)!.Name);
		Assert.Equal("root", DesignerSnapshotHitTester.FindNodeAt(root, 22, 22)!.Name);
		Assert.Equal("root", DesignerSnapshotHitTester.FindNodeAt(root, 15, 15)!.Name);
	}

	static DesignerElementNode Node(string name, double x, double y, double width, double height,
		bool visible = true, bool designable = true)
		=> new() { Name = name, Id = name, Path = name, X = x, Y = y, Width = width, Height = height, IsVisible = visible, IsDesignable = designable };
}
