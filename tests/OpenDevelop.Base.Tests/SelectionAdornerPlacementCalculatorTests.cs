using System.Windows;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class SelectionAdornerPlacementCalculatorTests
{
	[Fact]
	public void Calculate_MapsSelectionLabelAndEnabledHandleAnchorsThroughViewport()
	{
		var viewport = DesignViewport.Zoom(100, 100, 400, 300, 2, panX: 3, panY: -4);

		var placement = SelectionAdornerPlacementCalculator.Calculate(
			new Rect(10, 20, 30, 40), viewport, ["nw", "se"]);

		Assert.Equal(new Rect(123, 86, 60, 80), placement.Selection);
		Assert.Equal(new Point(123, 69), placement.LabelOrigin);
		Assert.Equal([("nw", new Point(123, 86)), ("se", new Point(183, 166))], placement.Handles);
	}

	[Fact]
	public void HandleAt_PreservesCenterMoveZoneAndHonorsEnabledHandles()
	{
		var viewport = DesignViewport.Identity(100, 100);
		var bounds = new Rect(10, 20, 30, 40);

		Assert.Null(SelectionAdornerPlacementCalculator.HandleAt(bounds, new Point(25, 40), viewport, ["nw", "se"]));
		Assert.Equal("se", SelectionAdornerPlacementCalculator.HandleAt(bounds, new Point(40, 60), viewport, ["nw", "se"]));
		Assert.Null(SelectionAdornerPlacementCalculator.HandleAt(bounds, new Point(10, 20), viewport, ["se"]));
	}
}
