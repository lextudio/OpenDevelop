using System.Xml.Linq;
using ICSharpCode.WinUIXamlDesigner;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class WinUICanvasPositionEditorTests
{
	[Fact]
	public void PositionDelta_MovesLeadingCanvasAnchorsWithoutCreatingMargin()
	{
		var element = Canvas("<Button Canvas.Left=\"20\" Canvas.Top=\"30\" />");

		var changes = WinUICanvasPositionEditor.PositionDelta(element.Elements().Single(), 15, -10);

		Assert.Collection(changes,
			change => Assert.Equal(("Canvas.Left", "35"), (change.Attribute, change.Value)),
			change => Assert.Equal(("Canvas.Top", "20"), (change.Attribute, change.Value)));
	}

	[Fact]
	public void PositionDelta_MovesTrailingCanvasAnchorsInTheOppositeDirection()
	{
		var element = Canvas("<Button Canvas.Right=\"40\" Canvas.Bottom=\"25\" />");

		var changes = WinUICanvasPositionEditor.PositionDelta(element.Elements().Single(), 12, -5);

		Assert.Collection(changes,
			change => Assert.Equal(("Canvas.Right", "28"), (change.Attribute, change.Value)),
			change => Assert.Equal(("Canvas.Bottom", "30"), (change.Attribute, change.Value)));
	}

	[Fact]
	public void PositionDelta_MovesBothSidesOfAnAnchoredCanvasChild()
	{
		var element = Canvas("<Button Canvas.Left=\"20\" Canvas.Right=\"40\" />");

		var changes = WinUICanvasPositionEditor.PositionDelta(element.Elements().Single(), 12, 0);

		Assert.Collection(changes,
			change => Assert.Equal(("Canvas.Left", "32"), (change.Attribute, change.Value)),
			change => Assert.Equal(("Canvas.Right", "28"), (change.Attribute, change.Value)));
	}

	[Fact]
	public void PositionDelta_IgnoresSubPixelMovementWithoutCreatingDefaultAnchors()
	{
		var element = Canvas("<Button />");

		Assert.Empty(WinUICanvasPositionEditor.PositionDelta(element.Elements().Single(), 0.4, -0.4));
	}

	[Fact]
	public void PositionDelta_CreatesLeadingAnchorForWholePixelMovementWhenNoAnchorExists()
	{
		var element = Canvas("<Button />");

		var changes = WinUICanvasPositionEditor.PositionDelta(element.Elements().Single(), 5, 0);

		var change = Assert.Single(changes);
		Assert.Equal(("Canvas.Left", "5"), (change.Attribute, change.Value));
	}

	[Fact]
	public void PositionDelta_PreservesBoundAnchorInsteadOfOverwritingItWithAConstant()
	{
		var element = XElement.Parse("<Canvas xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Button Canvas.Left=\"{Binding X}\" /></Canvas>");

		Assert.Empty(WinUICanvasPositionEditor.PositionDelta(element.Elements().Single(), 5, 0));
	}

	[Fact]
	public void PositionDelta_DoesNotTreatACanvasFromAnotherNamespaceAsAWinUICanvas()
	{
		var element = XElement.Parse("<other:Canvas xmlns:other=\"urn:other\"><Button /></other:Canvas>");

		Assert.False(WinUICanvasPositionEditor.IsCanvasChild(element.Element("Button")!));
		Assert.Empty(WinUICanvasPositionEditor.PositionDelta(element.Element("Button")!, 5, 0));
	}

	[Fact]
	public void PositionDelta_DoesNotClaimNonCanvasChildren()
	{
		var element = XElement.Parse("<Grid><Button Margin=\"10\" /></Grid>");

		Assert.Empty(WinUICanvasPositionEditor.PositionDelta(element.Element("Button")!, 5, 5));
	}

	static XElement Canvas(string child)
		=> XElement.Parse("<Canvas xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" + child + "</Canvas>");
}
