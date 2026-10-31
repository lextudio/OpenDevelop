using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.WinUIXamlDesigner;

/// <summary>The WinUI/Uno design surface's context menu and the pure parts of its command mapping, kept apart from the
/// view so a test can check that every menu entry is handled.</summary>
public static class WinUIXamlContextCommands
{
	/// <summary>Commands the view handles by name (everything else is a wrap, an alignment or a grid unit).</summary>
	public static readonly IReadOnlyList<string> NamedCommands = new[] {
		"cut", "copy", "paste", "duplicate", "delete", "bring-to-front", "bring-forward", "send-backward", "send-to-back",
		"unwrap", "create-empty-style", "create-based-on-style"
	};

	public static readonly (string Header, string Command)[] Menu = {
		("Cut", "cut"), ("Copy", "copy"), ("Paste", "paste"), ("Duplicate", "duplicate"), ("-", ""), ("Delete", "delete"), ("-", ""),
		("Bring to Front", "bring-to-front"), ("Bring Forward", "bring-forward"), ("Send Backward", "send-backward"), ("Send to Back", "send-to-back"), ("-", ""),
		("Wrap in Grid", "wrap-grid"), ("Wrap in StackPanel", "wrap-stackpanel"), ("Wrap in Canvas", "wrap-canvas"), ("Wrap in Viewbox", "wrap-viewbox"), ("Unwrap", "unwrap"), ("-", ""),
		("Edit Style/Create Empty", "create-empty-style"), ("Edit Style/Create Based On Current", "create-based-on-style"), ("-", ""),
		("Horizontal Alignment/Left", "set:HorizontalAlignment:Left"), ("Horizontal Alignment/Center", "set:HorizontalAlignment:Center"),
		("Horizontal Alignment/Right", "set:HorizontalAlignment:Right"), ("Horizontal Alignment/Stretch", "set:HorizontalAlignment:Stretch"),
		("Vertical Alignment/Top", "set:VerticalAlignment:Top"), ("Vertical Alignment/Center", "set:VerticalAlignment:Center"),
		("Vertical Alignment/Bottom", "set:VerticalAlignment:Bottom"), ("Vertical Alignment/Stretch", "set:VerticalAlignment:Stretch"), ("-", ""),
		("Grid Row/Fixed", "grid-row-Pixel"), ("Grid Row/Star", "grid-row-Star"), ("Grid Row/Auto", "grid-row-Auto"),
		("Grid Column/Fixed", "grid-column-Pixel"), ("Grid Column/Star", "grid-column-Star"), ("Grid Column/Auto", "grid-column-Auto")
	};

	/// <summary>The container type a "wrap-*" command wraps in, or null.</summary>
	public static string WrapContainer(string command) => command switch {
		"wrap-grid" => "Grid", "wrap-stackpanel" => "StackPanel", "wrap-canvas" => "Canvas", "wrap-viewbox" => "Viewbox", _ => null
	};

	/// <summary>The property and value of a "set:HorizontalAlignment:Left" command, or null when the command is not an
	/// alignment the designer offers.</summary>
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

	/// <summary>Whether the view has a handler for <paramref name="command"/>.</summary>
	public static bool IsHandled(string command)
		=> NamedCommands.Contains(command) || WrapContainer(command) != null || Alignment(command) != null
			|| command.StartsWith("grid-row-", StringComparison.Ordinal) || command.StartsWith("grid-column-", StringComparison.Ordinal);
}
