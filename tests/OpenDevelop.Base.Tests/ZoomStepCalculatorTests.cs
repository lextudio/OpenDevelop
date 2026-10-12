using ICSharpCode.SharpDevelop.Designer.Presentation;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class ZoomStepCalculatorTests
{
	[Fact]
	public void WheelStepsInAndOutReturnToExactlyOneHundredPercent()
	{
		// 1.1 is the shared canvas's Ctrl+wheel factor. Without snapping, ten steps in and ten out
		// end a rounding error away from 1.0, so 100% would never again render the frame 1:1.
		var raw = 1.0;
		var stepped = 1.0;
		for (var i = 0; i < 10; i++) {
			raw *= 1.1;
			stepped = ZoomStepCalculator.Step(stepped, 1.1, 0.1, 8);
		}
		// Zooming out multiplies by the reciprocal, exactly as DesignSurface does; that (unlike a
		// division by 1.1) does not round-trip in IEEE doubles: 1.0000000000000004.
		for (var i = 0; i < 10; i++) {
			raw *= 1 / 1.1;
			stepped = ZoomStepCalculator.Step(stepped, 1 / 1.1, 0.1, 8);
		}
		Assert.NotEqual(1.0, raw);
		Assert.Equal(1.0, stepped);
	}

	[Theory]
	[InlineData(0.9995, 1.0)]
	[InlineData(1.0009, 1.0)]
	[InlineData(0.998, 0.998)]
	[InlineData(1.1, 1.1)]
	public void OnlyScalesWithinTheToleranceSnapToOne(double scale, double expected)
		=> Assert.Equal(expected, ZoomStepCalculator.RoundToOneIfClose(scale));

	[Fact]
	public void StepClampsToTheZoomRange()
	{
		Assert.Equal(8, ZoomStepCalculator.Step(7.5, 1.1, 0.1, 8));
		Assert.Equal(0.1, ZoomStepCalculator.Step(0.105, 1 / 1.1, 0.1, 8));
	}
}
