using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class BrushHandleLayoutTests
{
	static DesignerBrushInfo Linear(string mapping = "RelativeToBoundingBox") => new() {
		Kind = BrushEditModel.LinearKind, StartX = 0, StartY = 0, EndX = 1, EndY = 1, MappingMode = mapping,
		Stops = { new DesignerGradientStopInfo { Color = "#FFFF0000", Offset = 0 }, new DesignerGradientStopInfo { Color = "#FF0000FF", Offset = 0.5 } }
	};

	[Fact]
	public void LinearHandlesSitAtFractionsOfTheBoundsAndStopsAlongTheLine()
	{
		var handles = BrushHandleLayout.Handles(Linear(), 10, 20, 100, 50);
		Assert.Equal(new[] { new BrushHandle("Start", 10, 20), new BrushHandle("End", 110, 70) }, handles);
		var markers = BrushHandleLayout.StopMarkers(Linear(), 10, 20, 100, 50);
		Assert.Equal((10d, 20d), (markers[0].X, markers[0].Y));
		Assert.Equal((60d, 45d), (markers[1].X, markers[1].Y));
	}

	[Fact]
	public void AbsoluteMappingUsesElementLocalUnits()
	{
		var brush = Linear("Absolute");
		brush.EndX = 40; brush.EndY = 8;
		var handles = BrushHandleLayout.Handles(brush, 10, 20, 100, 50);
		Assert.Equal(new BrushHandle("End", 50, 28), handles[1]);
		var moved = BrushHandleLayout.Move(brush, "End", 30, 30, 10, 20, 100, 50)!;
		Assert.Equal((20d, 10d), (moved.EndX, moved.EndY));
	}

	[Fact]
	public void DraggingAHandleIsTheInverseOfItsPositionAndLeavesTheOriginalAlone()
	{
		var brush = Linear();
		var moved = BrushHandleLayout.Move(brush, "Start", 60, 45, 10, 20, 100, 50)!;
		Assert.Equal((0.5d, 0.5d), (moved.StartX, moved.StartY));
		Assert.Equal((0d, 0d), (brush.StartX, brush.StartY));
		Assert.Equal(brush.Stops.Select(stop => stop.Offset), moved.Stops.Select(stop => stop.Offset));
		Assert.Equal(new BrushHandle("Start", 60, 45), BrushHandleLayout.Handles(moved, 10, 20, 100, 50)[0]);
	}

	[Fact]
	public void RadialHasCenterOriginAndTwoRadiusHandlesAndNeverGoesNegative()
	{
		var brush = new DesignerBrushInfo { Kind = BrushEditModel.RadialKind, CenterX = 0.5, CenterY = 0.5, OriginX = 0.25, OriginY = 0.25, RadiusX = 0.5, RadiusY = 0.25 };
		var handles = BrushHandleLayout.Handles(brush, 0, 0, 200, 100);
		Assert.Equal(new[] { "Center", "Origin", "RadiusX", "RadiusY" }, handles.Select(handle => handle.Name));
		Assert.Equal(new BrushHandle("RadiusX", 200, 50), handles[2]);
		Assert.Equal(new BrushHandle("RadiusY", 100, 75), handles[3]);

		Assert.Equal(0.25, BrushHandleLayout.Move(brush, "RadiusX", 150, 50, 0, 0, 200, 100)!.RadiusX);
		Assert.Equal(0d, BrushHandleLayout.Move(brush, "RadiusY", 100, 10, 0, 0, 200, 100)!.RadiusY);
		var center = BrushHandleLayout.Move(brush, "Center", 100, 100, 0, 0, 200, 100)!;
		Assert.Equal((0.5d, 1d, 0.25d, 0.25d), (center.CenterX, center.CenterY, center.OriginX, center.OriginY));
	}

	[Fact]
	public void NonGradientsUnknownHandlesAndZeroSizeYieldNothing()
	{
		Assert.Empty(BrushHandleLayout.Handles(new DesignerBrushInfo { Kind = BrushEditModel.SolidKind }, 0, 0, 10, 10));
		Assert.Empty(BrushHandleLayout.Handles(Linear(), 0, 0, 0, 10));
		Assert.Null(BrushHandleLayout.Move(Linear(), "Center", 1, 1, 0, 0, 10, 10));
		Assert.Null(BrushHandleLayout.Move(Linear(), "Start", 1, 1, 0, 0, 0, 10));
		Assert.False(BrushHandleLayout.HasHandles(null));
	}
}
