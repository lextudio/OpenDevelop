using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Presentation;

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

	public static readonly (string Header, string Command)[] Menu = DesignerStandardCommands.EditMenu(pasteHere: false).Append(("-", ""))
		.Concat(new[] {
			("Bring to Front", "bring-to-front"), ("Bring Forward", "bring-forward"), ("Send Backward", "send-backward"), ("Send to Back", "send-to-back"), ("-", ""),
			("Wrap in Grid", "wrap-grid"), ("Wrap in StackPanel", "wrap-stackpanel"), ("Wrap in Canvas", "wrap-canvas"), ("Wrap in Viewbox", "wrap-viewbox"), ("Unwrap", "unwrap"), ("-", ""),
			("Edit Style/Create Empty", "create-empty-style"), ("Edit Style/Create Based On Current", "create-based-on-style"), ("-", "") })
		.Concat(DesignerStandardCommands.AlignmentMenu).Append(("-", ""))
		.Concat(DesignerStandardCommands.GridUnitMenu).ToArray();

	static readonly string[] WrapContainers = { "Grid", "StackPanel", "Canvas", "Viewbox" };

	/// <summary>The container type a "wrap-*" command wraps in, or null.</summary>
	public static string WrapContainer(string command) => DesignerStandardCommands.WrapContainer(command, WrapContainers);

	/// <summary>The property and value of a "set:HorizontalAlignment:Left" command, or null when the command is not an
	/// alignment the designer offers.</summary>
	public static (string Property, string Value)? Alignment(string command) => DesignerStandardCommands.Alignment(command);

	/// <summary>Whether the view has a handler for <paramref name="command"/>.</summary>
	public static bool IsHandled(string command)
		=> NamedCommands.Contains(command) || WrapContainer(command) != null || Alignment(command) != null
			|| command.StartsWith("grid-row-", StringComparison.Ordinal) || command.StartsWith("grid-column-", StringComparison.Ordinal);
}
