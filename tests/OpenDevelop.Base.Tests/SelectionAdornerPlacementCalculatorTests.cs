using System.Windows;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class SelectionAdornerPlacementCalculatorTests
{
	[Fact]
	public void BaseOrigin_ClampsFitPlacementAndCarriesPanForBothConversions()
	{
		// A zoomed design is larger than this viewport; the placement is top-left anchored whatever the viewport size,
		// so the pan is the only offset.
		var viewport = DesignViewport.Zoom(400, 300, 200, 100, 1, panX: 12, panY: -7);

		Assert.Equal((12d, -7d), viewport.BaseOrigin);
		Assert.Equal((32d, 23d), viewport.DesignToSurface(20, 30));
		Assert.Equal((20d, 30d), viewport.SurfaceToDesign(32, 23));
	}

	[Fact]
	public void FitAndZoom_AnchorTheDesignAtTheTopLeftInsteadOfCentringIt()
	{
		// A 100x50 design in a 1000x800 pane: centring would put it at (450, 375); the one placement policy puts it at the pan
		// (the canvas margin), whatever the pane size, for Fit and for an absolute zoom alike.
		var zoomed = DesignViewport.Zoom(100, 50, 1000, 800, 1, panX: 32, panY: 32);
		var fitted = DesignViewport.Fit(100, 50, 1000, 800, 1, panX: 32, panY: 32);

		Assert.Equal((32d, 32d), zoomed.BaseOrigin);
		Assert.Equal((32d, 32d), fitted.BaseOrigin);
		Assert.Equal((32d, 32d), zoomed.DesignToSurface(0, 0));
		Assert.Equal((32d, 32d), fitted.DesignToSurface(0, 0));
	}

	[Theory]
	[InlineData(1280, 64, 2451, false)] // far smaller than the pane: no scrollbar (the 2451x518 <-> 2434x501 toggling case)
	[InlineData(1280, 64, 1344, false)] // exactly fills the pane: still none, the boundary must not flip
	[InlineData(1280, 64, 1343, true)]  // one pixel short of the pane: scrolls
	[InlineData(3000, 64, 2451, true)]
	public void NeedsScrollBar_DependsOnlyOnTheDesignAndThePaneExtent(double design, double margins, double pane, bool expected)
		=> Assert.Equal(expected, DesignViewport.NeedsScrollBar(design, margins, pane));

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

		// Top-left anchored: only the pan places the design, so the selection (10,20) at 2x lands at 3 + 20, -4 + 40.
		Assert.Equal(new Rect(23, 36, 60, 80), placement.Selection);
		Assert.Equal(new Point(23, 19), placement.LabelOrigin);
		Assert.Equal([("nw", new Point(23, 36)), ("se", new Point(83, 116))], placement.Handles);
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
