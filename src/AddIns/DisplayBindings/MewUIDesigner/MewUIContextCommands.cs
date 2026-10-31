using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.MewUIDesigner;

/// <summary>The MewUI design surface's alignment and Grid-track context commands and their command-string mapping, kept
/// free of view types so a test can check every menu entry maps to the right wire values.</summary>
public static class MewUIContextCommands
{
	public static readonly (string Header, string Command)[] LayoutMenu = {
		("Horizontal Alignment/Left", "set:HorizontalAlignment:Left"), ("Horizontal Alignment/Center", "set:HorizontalAlignment:Center"),
		("Horizontal Alignment/Right", "set:HorizontalAlignment:Right"), ("Horizontal Alignment/Stretch", "set:HorizontalAlignment:Stretch"),
		("Vertical Alignment/Top", "set:VerticalAlignment:Top"), ("Vertical Alignment/Center", "set:VerticalAlignment:Center"),
		("Vertical Alignment/Bottom", "set:VerticalAlignment:Bottom"), ("Vertical Alignment/Stretch", "set:VerticalAlignment:Stretch"),
		("Grid Row/Auto", "grid-row-Auto"), ("Grid Row/Star", "grid-row-Star"), ("Grid Row/Fixed", "grid-row-Pixel"),
		("Grid Column/Auto", "grid-column-Auto"), ("Grid Column/Star", "grid-column-Star"), ("Grid Column/Fixed", "grid-column-Pixel")
	};

	/// <summary>The property and value of "set:HorizontalAlignment:Left", or null for anything else.</summary>
	public static (string Property, string Value)? Alignment(string command)
	{
		var parts = (command ?? "").Split(':');
		if (parts.Length != 3 || parts[0] != "set") return null;
		var allowed = parts[1] switch {
			"HorizontalAlignment" => new[] { "Left", "Center", "Right", "Stretch" },
			"VerticalAlignment" => new[] { "Top", "Center", "Bottom", "Stretch" },
			_ => null
		};
		return allowed != null && allowed.Contains(parts[2]) ? (parts[1], parts[2]) : null;
	}

	/// <summary>Whether "grid-row-Auto" / "grid-column-Pixel" names the row or column, and the wire unit (Auto, Star, Pixel).</summary>
	public static (bool Row, string Unit)? GridTrack(string command)
	{
		var parts = (command ?? "").Split('-');
		if (parts.Length != 3 || parts[0] != "grid" || parts[1] is not ("row" or "column") || parts[2] is not ("Auto" or "Star" or "Pixel")) return null;
		return (parts[1] == "row", parts[2]);
	}

	/// <summary>An element the document can address: unnamed elements get "#path" ids and cannot be edited by name.</summary>
	public static bool IsNamedId(string id) => !string.IsNullOrEmpty(id) && id[0] != '#';
}
