using System.Globalization;

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

    public static double Fraction(long part, long whole) => whole <= 0 ? 0 : (double)part / whole;
}
