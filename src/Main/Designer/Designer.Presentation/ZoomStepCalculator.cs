using System;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>
/// One incremental zoom step (Ctrl+wheel, zoom buttons). Ported from the in-process WPF
/// designer's <c>ZoomScrollViewer.RoundToOneIfClose</c>: repeated multiply/divide steps leave the
/// scale at 0.99999... instead of exactly 100%, where the design frame should render 1:1.
/// </summary>
public static class ZoomStepCalculator
{
	/// <summary>Distance from 100% within which a stepped scale snaps to exactly 1.0.</summary>
	public const double OneTolerance = 0.001;

	/// <summary>Applies <paramref name="factor"/> to <paramref name="scale"/>, snaps a result
	/// near 100% to exactly 1.0, then clamps it to [<paramref name="min"/>, <paramref name="max"/>].</summary>
	public static double Step(double scale, double factor, double min, double max)
		=> Math.Clamp(RoundToOneIfClose(scale * factor), min, max);

	public static double RoundToOneIfClose(double scale)
		=> Math.Abs(scale - 1.0) < OneTolerance ? 1.0 : scale;
}
