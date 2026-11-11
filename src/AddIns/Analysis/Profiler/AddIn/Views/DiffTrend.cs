using System.Windows.Media;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>Which way a compared value moved. Memory or time growing is the bad direction, so it is red and a drop is green.</summary>
internal enum DiffTrend
{
	Same,
	Grew,
	Shrank,
}

internal static class DiffColours
{
	static readonly Brush Worse = Frozen(Color.FromRgb(0xD1, 0x34, 0x38));
	static readonly Brush Better = Frozen(Color.FromRgb(0x2E, 0x9D, 0x4F));

	public static DiffTrend TrendOf(double change, double epsilon = 0) =>
		change > epsilon ? DiffTrend.Grew : change < -epsilon ? DiffTrend.Shrank : DiffTrend.Same;

	/// <summary>The text colour of a change; an unchanged value keeps the ordinary text colour.</summary>
	public static Brush BrushOf(DiffTrend trend) => trend switch
	{
		DiffTrend.Grew => Worse,
		DiffTrend.Shrank => Better,
		_ => System.Windows.SystemColors.ControlTextBrush,
	};

	static Brush Frozen(Color colour)
	{
		var brush = new SolidColorBrush(colour);
		brush.Freeze();
		return brush;
	}
}
