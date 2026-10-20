using System.Windows;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class SelectionAdornerPlacementCalculatorTests
{
	[Fact]
	public void BaseOrigin_ClampsFitPlacementAndCarriesPanForBothConversions()
	{
		// A zoomed design is larger than this viewport, so Zoom produces negative centered
		// placement. The canvas deliberately top-left clamps that placement, then layers pan.
		var viewport = DesignViewport.Zoom(400, 300, 200, 100, 1, panX: 12, panY: -7);

		Assert.Equal((12d, -7d), viewport.BaseOrigin);
		Assert.Equal((32d, 23d), viewport.DesignToSurface(20, 30));
		Assert.Equal((20d, 30d), viewport.SurfaceToDesign(32, 23));
	}

	[Fact]
	public void PanForDesignPointAtSurface_KeepsCursorAnchorWhenSwitchingFitToAbsoluteZoom()
	{
		var fitted = DesignViewport.Fit(400, 300, 200, 100, 1, panX: 32, panY: 32);
		var designPoint = (X: 50d, Y: 20d);
		var cursor = fitted.DesignToSurface(designPoint.X, designPoint.Y);
		var absolute = DesignViewport.Zoom(400, 300, 200, 100, .5);
		var pan = absolute.PanForDesignPointAtSurface(designPoint.X, designPoint.Y, cursor.X, cursor.Y);
		var anchored = DesignViewport.Zoom(400, 300, 200, 100, .5, pan.X, pan.Y);

		Assert.Equal(cursor, anchored.DesignToSurface(designPoint.X, designPoint.Y));
	}

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
