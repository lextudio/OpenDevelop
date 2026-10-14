using System.Windows.Media;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class BrushHsbTests
{
	[Fact]
	public void ParsesHsbAndRoundTripsThroughTheColorEditorsConversion()
	{
		Assert.True(BrushEditorDialog.TryParseHsb("120", "100", "50", out var h, out var s, out var b));
		Assert.Equal((120d, 1d, 0.5d), (h, s, b));
		Assert.Equal(Color.FromArgb(255, 0, 128, 0), BrushEditorDialog.FromHsv(h, s, b, 255));
		var (hue, saturation, value) = BrushEditorDialog.ToHsv(Color.FromArgb(255, 0, 128, 0));
		Assert.Equal(120, Math.Round(hue));
		Assert.Equal(1, saturation, 3);
		Assert.Equal(0.502, value, 3);
		// 360 is the same hue as 0.
		Assert.True(BrushEditorDialog.TryParseHsb("360", "0", "0", out h, out _, out _));
		Assert.Equal(0, h);
	}

	[Fact]
	public void UneditedHsbTextIsNotCommitted()
	{
		// Leaving a box without typing must not re-apply the rounded display.
		Assert.False(BrushEditorDialog.IsHsbEdit(("0", "100", "0"), ("0", "100", "0")));
		Assert.True(BrushEditorDialog.IsHsbEdit(("0", "100", "1"), ("0", "100", "0")));
	}

	[Theory]
	[InlineData("-1", "50", "50")]
	[InlineData("361", "50", "50")]
	[InlineData("10", "101", "50")]
	[InlineData("10", "50", "x")]
	public void RejectsOutOfRangeOrNonNumericHsb(string h, string s, string b)
		=> Assert.False(BrushEditorDialog.TryParseHsb(h, s, b, out _, out _, out _));
}
