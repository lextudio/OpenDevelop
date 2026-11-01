using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>One entry of a designer-edited menu model on the wire (design host &lt;-&gt; IDE): a leaf
	/// item, a section (a group the toolkit separates visually) or a submenu. Framework-neutral so a
	/// designer whose menu is data rather than a control tree (GTK's <c>&lt;menu&gt;</c>) shares one editor
	/// model, text form and round-trip rules with the rest.</summary>
	public sealed class DesignerMenuNode
	{
		public const string ItemKind = "item";
		public const string SectionKind = "section";
		public const string SubmenuKind = "submenu";

		/// <summary>The entry's index among its parent's entries in the source, or -1 for a new one. It
		/// lets a rewrite keep the attributes (icons, hints) the editor does not model.</summary>
		[JsonPropertyName("sourceIndex")] public int SourceIndex { get; set; } = -1;
		[JsonPropertyName("kind")] public string Kind { get; set; } = ItemKind;
		[JsonPropertyName("label")] public string Label { get; set; } = "";
		[JsonPropertyName("action")] public string Action { get; set; } = "";
		[JsonPropertyName("children")] public List<DesignerMenuNode> Children { get; set; } = new List<DesignerMenuNode>();

		public bool IsContainer => Kind == SectionKind || Kind == SubmenuKind;
		public static bool IsKnownKind(string? kind) => kind == ItemKind || kind == SectionKind || kind == SubmenuKind;
	}

	/// <summary>The indented text form of a menu tree, one entry per line: <c>Label = action</c> for an
	/// item, <c>[section] Label</c> and <c>[submenu] Label</c> for a container whose entries follow one
	/// level deeper (a tab or two spaces per level). Parsing keeps each entry's source index by matching
	/// it positionally against the menu being edited, so unmodelled attributes survive an edit.</summary>
	public static class DesignerMenuOutline
	{
		const string SectionTag = "[section]";
		const string SubmenuTag = "[submenu]";

		public static string Format(IEnumerable<DesignerMenuNode> nodes)
		{
			var builder = new StringBuilder();
			Append(builder, nodes, 0);
			return builder.ToString().TrimEnd('\r', '\n');
		}

		static void Append(StringBuilder builder, IEnumerable<DesignerMenuNode> nodes, int depth)
		{
			foreach (var node in nodes) {
				builder.Append(' ', depth * 2);
				if (node.IsContainer) {
					builder.Append(node.Kind == DesignerMenuNode.SectionKind ? SectionTag : SubmenuTag);
					if (node.Label.Length > 0) builder.Append(' ').Append(node.Label);
					builder.Append('\n');
					Append(builder, node.Children, depth + 1);
				} else {
					builder.Append(node.Action.Length == 0 ? node.Label : node.Label + " = " + node.Action).Append('\n');
				}
			}
		}

		/// <summary>Parses <paramref name="text"/> against <paramref name="existing"/>; null with
		/// <paramref name="error"/> set when an item has indented entries below it or a line is indented
		/// deeper than its parent allows.</summary>
		public static List<DesignerMenuNode>? Parse(string? text, IReadOnlyList<DesignerMenuNode> existing, out string? error)
		{
			error = null;
			var lines = new List<(int Depth, string Text, int Number)>();
			var number = 0;
			foreach (var raw in (text ?? "").Replace("\r", "").Split('\n')) {
				number++;
				if (string.IsNullOrWhiteSpace(raw)) continue;
				var depth = DepthOf(raw);
				if (depth < 0) { error = "Line " + number + " is indented by an odd number of spaces; indent each level with two spaces or a tab."; return null; }
				lines.Add((depth, raw.Trim(), number));
			}
			var position = 0;
			var result = ParseLevel(lines, ref position, 0, ref error);
			if (error == null && position < lines.Count)
				error = "Line " + lines[position].Number + " is indented deeper than its parent entry allows.";
			if (error != null) return null;
			AssignSourceIndexes(result, existing);
			return result;
		}

		static List<DesignerMenuNode> ParseLevel(List<(int Depth, string Text, int Number)> lines, ref int position, int depth, ref string? error)
		{
			var result = new List<DesignerMenuNode>();
			while (error == null && position < lines.Count && lines[position].Depth >= depth) {
				var line = lines[position];
				if (line.Depth > depth) { error = "Line " + line.Number + " is indented deeper than its parent entry allows."; break; }
				position++;
				var node = ParseLine(line.Text);
				if (node.IsContainer) {
					node.Children = ParseLevel(lines, ref position, depth + 1, ref error);
				} else if (position < lines.Count && lines[position].Depth > depth) {
					error = "Line " + lines[position].Number + " is indented below the item '" + node.Label + "'; only a section or submenu holds entries.";
				}
				result.Add(node);
			}
			return result;
		}

		/// <summary>Gives each parsed entry the source index of the existing entry it stands for, so the attributes
		/// the text form does not show survive an edit. Two passes per level: an unclaimed entry of the same kind
		/// with the same label and action wins wherever it now sits (so inserting a line above does not shift every
		/// later entry), then an entry whose text changed keeps the unclaimed entry at its own position.</summary>
		static void AssignSourceIndexes(List<DesignerMenuNode> nodes, IReadOnlyList<DesignerMenuNode> existing)
		{
			var matches = new int[nodes.Count];
			var claimed = new bool[existing.Count];
			for (var i = 0; i < nodes.Count; i++) {
				matches[i] = -1;
				for (var j = 0; j < existing.Count; j++) {
					if (claimed[j] || existing[j].Kind != nodes[i].Kind || existing[j].Label != nodes[i].Label || existing[j].Action != nodes[i].Action) continue;
					matches[i] = j; claimed[j] = true;
					break;
				}
			}
			for (var i = 0; i < nodes.Count; i++) {
				if (matches[i] >= 0 || i >= existing.Count || claimed[i] || existing[i].Kind != nodes[i].Kind) continue;
				matches[i] = i; claimed[i] = true;
			}
			for (var i = 0; i < nodes.Count; i++) {
				nodes[i].SourceIndex = matches[i];
				if (nodes[i].IsContainer)
					AssignSourceIndexes(nodes[i].Children, matches[i] >= 0 ? existing[matches[i]].Children : (IReadOnlyList<DesignerMenuNode>)Array.Empty<DesignerMenuNode>());
			}
		}

		static DesignerMenuNode ParseLine(string text)
		{
			if (text.StartsWith(SectionTag, StringComparison.OrdinalIgnoreCase))
				return new DesignerMenuNode { Kind = DesignerMenuNode.SectionKind, Label = text.Substring(SectionTag.Length).Trim() };
			if (text.StartsWith(SubmenuTag, StringComparison.OrdinalIgnoreCase))
				return new DesignerMenuNode { Kind = DesignerMenuNode.SubmenuKind, Label = text.Substring(SubmenuTag.Length).Trim() };
			var at = text.IndexOf('=');
			return new DesignerMenuNode {
				Label = (at < 0 ? text : text.Substring(0, at)).Trim(),
				Action = at < 0 ? "" : text.Substring(at + 1).Trim()
			};
		}

		static int DepthOf(string line)
		{
			var width = 0;
			foreach (var c in line) {
				if (c == '\t') width += 2;
				else if (c == ' ') width++;
				else break;
			}
			return width % 2 == 0 ? width / 2 : -1;
		}
	}
}
