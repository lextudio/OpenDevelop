using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class DesignerSnapshotHitTesterLockTests
{
	static DesignerElementNode Node(string name, double x, double y, double size, params DesignerElementNode[] children)
	{
		var node = new DesignerElementNode { Id = name, Name = name, X = x, Y = y, Width = size, Height = size };
		node.Children.AddRange(children);
		return node;
	}

	[Fact]
	public void ALockedElementAndItsChildrenAreSkippedSoTheClickFallsThrough()
	{
		var inner = Node("inner", 20, 20, 10);
		var front = Node("front", 10, 10, 40, inner);
		var back = Node("back", 10, 10, 40);
		var root = Node("root", 0, 0, 100, back, front);
		Assert.Equal("inner", DesignerSnapshotHitTester.FindNodeAt(root, 25, 25)!.Name);

		front.IsDesignTimeLocked = true;

		Assert.Equal("back", DesignerSnapshotHitTester.FindNodeAt(root, 25, 25)!.Name);
		front.IsDesignTimeLocked = null;
		Assert.Equal("inner", DesignerSnapshotHitTester.FindNodeAt(root, 25, 25)!.Name);
	}
}
