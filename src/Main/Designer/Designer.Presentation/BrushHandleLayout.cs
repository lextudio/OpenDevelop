using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>One on-canvas handle of a gradient brush, in design coordinates.</summary>
public readonly record struct BrushHandle(string Name, double X, double Y);

/// <summary>
/// Where a gradient's draggable points sit over its element, and the brush a drag of one of them
/// produces. A linear gradient has Start and End; a radial one Center, Origin and the RadiusX /
/// RadiusY handles at the right and bottom of its ellipse. Coordinates are the brush's own: fractions
/// of the element's bounds for RelativeToBoundingBox, element-local units for Absolute. Pure
/// geometry, so the canvas overlay and its tests share one definition.
/// </summary>
public static class BrushHandleLayout
{
	public const string Start = "Start";
	public const string End = "End";
	public const string Center = "Center";
	public const string Origin = "Origin";
	public const string RadiusX = "RadiusX";
	public const string RadiusY = "RadiusY";

	/// <summary>Whether the brush is one this layout draws handles for.</summary>
	public static bool HasHandles(DesignerBrushInfo? brush)
		=> brush is { Kind: BrushEditModel.LinearKind or BrushEditModel.RadialKind };

	public static IReadOnlyList<BrushHandle> Handles(DesignerBrushInfo brush, double x, double y, double width, double height)
	{
		if (!HasHandles(brush) || !TryScale(brush, width, height, out var sx, out var sy))
			return Array.Empty<BrushHandle>();
		BrushHandle At(string name, double vx, double vy) => new(name, x + vx * sx, y + vy * sy);
		if (brush.Kind == BrushEditModel.LinearKind)
			return new[] { At(Start, brush.StartX, brush.StartY), At(End, brush.EndX, brush.EndY) };
		return new[] {
			At(Center, brush.CenterX, brush.CenterY),
			At(Origin, brush.OriginX, brush.OriginY),
			At(RadiusX, brush.CenterX + brush.RadiusX, brush.CenterY),
			At(RadiusY, brush.CenterX, brush.CenterY + brush.RadiusY)
		};
	}

	/// <summary>The point of each gradient stop along a linear gradient's line, for the stop markers.</summary>
	public static IReadOnlyList<(double X, double Y, string Color)> StopMarkers(DesignerBrushInfo brush, double x, double y, double width, double height)
	{
		if (brush.Kind != BrushEditModel.LinearKind || !TryScale(brush, width, height, out var sx, out var sy))
			return Array.Empty<(double, double, string)>();
		return brush.Stops.Select(stop => (
			x + (brush.StartX + (brush.EndX - brush.StartX) * stop.Offset) * sx,
			y + (brush.StartY + (brush.EndY - brush.StartY) * stop.Offset) * sy,
			stop.Color)).ToArray();
	}

	/// <summary>A copy of <paramref name="brush"/> with <paramref name="handle"/> dragged to the design point
	/// (<paramref name="px"/>, <paramref name="py"/>), or null for an unknown handle or a zero-size element.
	/// A radius never goes negative; values are rounded to three decimals so the written XAML stays tidy.</summary>
	public static DesignerBrushInfo? Move(DesignerBrushInfo brush, string handle, double px, double py, double x, double y, double width, double height)
	{
		if (!HasHandles(brush) || !TryScale(brush, width, height, out var sx, out var sy))
			return null;
		double Fx(double design) => Math.Round((design - x) / sx, 3);
		double Fy(double design) => Math.Round((design - y) / sy, 3);
		var moved = Clone(brush);
		switch (handle)
		{
			case Start when brush.Kind == BrushEditModel.LinearKind: (moved.StartX, moved.StartY) = (Fx(px), Fy(py)); break;
			case End when brush.Kind == BrushEditModel.LinearKind: (moved.EndX, moved.EndY) = (Fx(px), Fy(py)); break;
			case Center when brush.Kind == BrushEditModel.RadialKind: (moved.CenterX, moved.CenterY) = (Fx(px), Fy(py)); break;
			case Origin when brush.Kind == BrushEditModel.RadialKind: (moved.OriginX, moved.OriginY) = (Fx(px), Fy(py)); break;
			case RadiusX when brush.Kind == BrushEditModel.RadialKind: moved.RadiusX = Math.Max(0, Math.Round(Fx(px) - brush.CenterX, 3)); break;
			case RadiusY when brush.Kind == BrushEditModel.RadialKind: moved.RadiusY = Math.Max(0, Math.Round(Fy(py) - brush.CenterY, 3)); break;
			default: return null;
		}
		return moved;
	}

	static bool TryScale(DesignerBrushInfo brush, double width, double height, out double sx, out double sy)
	{
		if (string.Equals(brush.MappingMode, "Absolute", StringComparison.Ordinal))
			(sx, sy) = (1, 1);
		else
			(sx, sy) = (width, height);
		return sx > 0 && sy > 0 && !double.IsNaN(sx + sy);
	}

	static DesignerBrushInfo Clone(DesignerBrushInfo brush) => new() {
		Kind = brush.Kind, ResourceKey = brush.ResourceKey, ImageSource = brush.ImageSource, Stretch = brush.Stretch,
		TileMode = brush.TileMode, AlignmentX = brush.AlignmentX, AlignmentY = brush.AlignmentY,
		Viewport = brush.Viewport, ViewportUnits = brush.ViewportUnits, Viewbox = brush.Viewbox, ViewboxUnits = brush.ViewboxUnits,
		Color = brush.Color, Opacity = brush.Opacity,
		StartX = brush.StartX, StartY = brush.StartY, EndX = brush.EndX, EndY = brush.EndY,
		CenterX = brush.CenterX, CenterY = brush.CenterY, OriginX = brush.OriginX, OriginY = brush.OriginY,
		RadiusX = brush.RadiusX, RadiusY = brush.RadiusY,
		MappingMode = brush.MappingMode, SpreadMethod = brush.SpreadMethod,
		Stops = brush.Stops.Select(stop => new DesignerGradientStopInfo { Color = stop.Color, Offset = stop.Offset }).ToList()
	};
}
