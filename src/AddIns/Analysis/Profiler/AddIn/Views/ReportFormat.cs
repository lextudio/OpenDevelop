using System.Globalization;
using ICSharpCode.Core;
using OpenDevelop.Profiler.Cpu;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>Number formatting shared by the report views.</summary>
internal static class ReportFormat
{
	public static string Bytes(long bytes)
	{
		string[] units = ["B", "KB", "MB", "GB", "TB"];
		var value = (double)Math.Abs(bytes);
		var unit = 0;
		while (value >= 1024 && unit < units.Length - 1)
		{
			value /= 1024;
			unit++;
		}
		var text = unit == 0
			? value.ToString("N0", CultureInfo.CurrentCulture)
			: value.ToString("N1", CultureInfo.CurrentCulture);
		return (bytes < 0 ? "-" : "") + text + " " + units[unit];
	}

	/// <summary>Column header for the inclusive weight: milliseconds for a CPU profile, kilobytes for an allocation profile.</summary>
	public static string TotalHeader(ProfileUnit unit) => StringParser.Parse(
		unit == ProfileUnit.Kilobytes ? "${res:AddIns.Profiler.Report.TotalSize}" : "${res:AddIns.Profiler.Report.TotalMs}");

	public static string SelfHeader(ProfileUnit unit) => StringParser.Parse(
		unit == ProfileUnit.Kilobytes ? "${res:AddIns.Profiler.Report.SelfSize}" : "${res:AddIns.Profiler.Report.SelfMs}");

	/// <summary>A counter value in its own unit; bytes are scaled (KB, MB, GB) so a rate of billions reads as a size.</summary>
	public static string CounterValue(double value, string units) => units == "B"
		? Bytes((long)value)
		: value.ToString("N1", CultureInfo.CurrentCulture) + (units.Length == 0 ? "" : " " + units);

	/// <summary>A weight with its unit, e.g. "1,234 ms" or "1,234 KB".</summary>
	public static string Quantity(double weight, ProfileUnit unit) =>
		weight.ToString("N0", CultureInfo.CurrentCulture) + (unit == ProfileUnit.Kilobytes ? " KB" : " ms");

	public static double Fraction(long part, long whole) => whole <= 0 ? 0 : (double)part / whole;

	/// <summary>A share change in percentage points with an explicit sign, e.g. +3.2 pp or -0.4 pp; 0 shows as 0.0 pp.</summary>
	public static string Signed(double fractionChange)
	{
		var points = fractionChange * 100;
		return (points > 0.05 ? "+" : "") + points.ToString("0.0", CultureInfo.CurrentCulture) + " pp";
	}
}
