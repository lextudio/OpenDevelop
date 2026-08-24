using System;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Workbench;

/// <summary>
/// One-line convenience wrapper so any addin can write diagnostic output to a named
/// Output pad channel without holding references or managing categories.
///
/// Usage:
/// <code>
///   OutputChannel.Write("MewUI", "Host started on port {0}", port);
///   OutputChannel.Write("GtkDesigner", "Host process exited unexpectedly");
/// </code>
///
/// Each category is created on first use and persists in the Output pad's combo box,
/// giving users a permanent, filterable diagnostic surface per addin. Without this,
/// addins tend to log nothing at all - background operations (host lifecycle, parser
/// activity, file resolution) are invisible when they fail.
/// </summary>
public static class OutputChannel
{
	/// <summary>Writes a line to the named output category, creating it if needed.</summary>
	public static void Write(string categoryName, string message)
	{
		var pad = SD.Services.GetService(typeof(IOutputPad)) as IOutputPad;
		if (pad == null) return;
		var category = pad.GetOrCreateCategory(categoryName);
		if (category == null) return;
		category.AppendText(message + Environment.NewLine);
	}

	/// <summary>Formats and writes a line to the named output category.</summary>
	public static void Write(string categoryName, string format, params object[] args)
	{
		Write(categoryName, string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));
	}
}
