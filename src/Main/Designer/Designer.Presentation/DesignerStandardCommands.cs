using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation
{
	/// <summary>
	/// The context-menu commands every designer's surface offers, as data and pure rules: which entries, the command
	/// strings they send, how a command string is read back, and when an entry is enabled. A designer composes its menu
	/// from these pieces plus its own entries and maps each command to its host call; it does not re-spell them. Command
	/// strings are an open vocabulary on the shared canvas (<c>SetContextCommands</c> takes any string), so nothing here
	/// constrains a designer that adds a command of its own.
	/// </summary>
	public static class DesignerStandardCommands
	{
		/// <summary>Cut, Copy, Paste, [Paste Here], Duplicate, separator, Delete. Paste Here needs a design point under
		/// the pointer, which only designers with a free-form surface report.</summary>
		public static IReadOnlyList<(string Header, string Command)> EditMenu(bool pasteHere)
		{
			var entries = new List<(string, string)> { ("Cut", "cut"), ("Copy", "copy"), ("Paste", "paste") };
			if (pasteHere)
				entries.Add(("Paste Here", "paste-here"));
			entries.Add(("Duplicate", "duplicate"));
			entries.Add(("-", ""));
			entries.Add(("Delete", "delete"));
			return entries;
		}

		/// <summary>Move First/Earlier/Later/Last among siblings: for a box or stack the child order is the layout
		/// order, so these are "move", not z-order "bring to front".</summary>
		public static readonly IReadOnlyList<(string Header, string Command)> MoveMenu = new[] {
			("Order/Move First", "move-first"), ("Order/Move Earlier", "move-earlier"), ("Order/Move Later", "move-later"), ("Order/Move Last", "move-last")
		};

		/// <summary>"Wrap/X" entries (command <c>wrap-X</c>) and Unwrap.</summary>
		public static IReadOnlyList<(string Header, string Command)> WrapMenu(params string[] containers)
			=> containers.Select(container => ("Wrap/" + container, "wrap-" + container)).Append(("Unwrap", "unwrap")).ToArray();

		/// <summary>The container a <c>wrap-X</c> command names, matched case-insensitively against
		/// <paramref name="containers"/> and returned as spelled there; null when it is not a wrap of one of them.</summary>
		public static string? WrapContainer(string? command, IEnumerable<string> containers)
		{
			const string prefix = "wrap-";
			if (command == null || !command.StartsWith(prefix, StringComparison.Ordinal))
				return null;
			var name = command.Substring(prefix.Length);
			return containers.FirstOrDefault(container => string.Equals(container, name, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>Horizontal/Vertical Alignment entries, command <c>set:HorizontalAlignment:Left</c> and so on.</summary>
		public static readonly IReadOnlyList<(string Header, string Command)> AlignmentMenu = new[] {
			("Horizontal Alignment/Left", "set:HorizontalAlignment:Left"), ("Horizontal Alignment/Center", "set:HorizontalAlignment:Center"),
			("Horizontal Alignment/Right", "set:HorizontalAlignment:Right"), ("Horizontal Alignment/Stretch", "set:HorizontalAlignment:Stretch"),
			("Vertical Alignment/Top", "set:VerticalAlignment:Top"), ("Vertical Alignment/Center", "set:VerticalAlignment:Center"),
			("Vertical Alignment/Bottom", "set:VerticalAlignment:Bottom"), ("Vertical Alignment/Stretch", "set:VerticalAlignment:Stretch"),
		};

		/// <summary>The property and value of an <see cref="AlignmentMenu"/> command, or null for anything else.</summary>
		public static (string Property, string Value)? Alignment(string? command)
		{
			var parts = (command ?? "").Split(':');
			if (parts.Length != 3 || parts[0] != "set")
				return null;
			var allowed = parts[1] switch {
				"HorizontalAlignment" => new[] { "Left", "Center", "Right", "Stretch" },
				"VerticalAlignment" => new[] { "Top", "Center", "Bottom", "Stretch" },
				_ => null
			};
			return allowed != null && allowed.Contains(parts[2]) ? (parts[1], parts[2]) : null;
		}

		/// <summary>Grid Row/Column entries that set the size unit of the track under the pointer (Fixed, Star, Auto).</summary>
		public static readonly IReadOnlyList<(string Header, string Command)> GridUnitMenu = new[] {
			("Grid Row/Fixed", "grid-row-Pixel"), ("Grid Row/Star", "grid-row-Star"), ("Grid Row/Auto", "grid-row-Auto"),
			("Grid Column/Fixed", "grid-column-Pixel"), ("Grid Column/Star", "grid-column-Star"), ("Grid Column/Auto", "grid-column-Auto"),
		};

		/// <summary>Whether a <see cref="GridUnitMenu"/> command names the row or column, and the wire unit (Auto, Star, Pixel).</summary>
		public static (bool IsRow, string Unit)? GridUnit(string? command)
		{
			var parts = (command ?? "").Split('-');
			if (parts.Length != 3 || parts[0] != "grid" || parts[1] is not ("row" or "column") || parts[2] is not ("Auto" or "Star" or "Pixel"))
				return null;
			return (parts[1] == "row", parts[2]);
		}

		/// <summary>How far a move command shifts a child that is at <paramref name="index"/> among <paramref name="count"/>
		/// siblings; zero when it would not move (or the command is not a move).</summary>
		public static int MoveDelta(string? command, int index, int count) => command switch {
			"move-first" => -index,
			"move-earlier" => index > 0 ? -1 : 0,
			"move-later" => index < count - 1 ? 1 : 0,
			"move-last" => count - 1 - index,
			_ => 0
		};

		public static bool IsMoveCommand(string? command) => command is "move-first" or "move-earlier" or "move-later" or "move-last";

		/// <summary>Whether a move command can do anything for a child at <paramref name="index"/> of <paramref name="count"/>.</summary>
		public static bool CanMove(string? command, int index, int count) => MoveDelta(command, index, count) != 0;

		/// <summary>Unwrap acts on exactly one container that has children and sits among siblings of one parent.</summary>
		public static bool CanUnwrap(int selectedCount, bool hasChildren, bool sharesOneParent) => selectedCount == 1 && hasChildren && sharesOneParent;

		/// <summary>Wrap acts on siblings of one parent; a single-child container takes exactly one element.</summary>
		public static bool CanWrap(int selectedCount, bool sharesOneParent, bool containerHoldsOneChild)
			=> selectedCount > 0 && sharesOneParent && (!containerHoldsOneChild || selectedCount == 1);

		/// <summary>The nodes of a tree, parents before children.</summary>
		public static IEnumerable<DesignerElementNode> Flatten(DesignerElementNode root)
		{
			yield return root;
			foreach (var child in root.Children)
				foreach (var node in Flatten(child))
					yield return node;
		}

		/// <summary>The parent of the node with <paramref name="id"/>, or null for the root or an unknown id.</summary>
		public static DesignerElementNode? ParentOf(DesignerElementNode root, string id)
			=> Flatten(root).FirstOrDefault(node => node.Children.Any(child => child.Id == id));

		/// <summary>The position of the single selected node among its siblings, or null when the selection is not one
		/// non-root node.</summary>
		public static (int Index, int Count)? SiblingPosition(DesignerElementNode? root, IReadOnlyList<string> selectedIds)
		{
			if (root == null || selectedIds.Count != 1)
				return null;
			var parent = ParentOf(root, selectedIds[0]);
			return parent == null ? null : (parent.Children.FindIndex(child => child.Id == selectedIds[0]), parent.Children.Count);
		}

		/// <summary>Whether every selected node has the same (non-null) parent.</summary>
		public static bool SharesOneParent(DesignerElementNode? root, IReadOnlyList<string> selectedIds)
		{
			if (root == null || selectedIds.Count == 0)
				return false;
			var parent = ParentOf(root, selectedIds[0]);
			return parent != null && selectedIds.All(id => ParentOf(root, id) == parent);
		}
	}
}
