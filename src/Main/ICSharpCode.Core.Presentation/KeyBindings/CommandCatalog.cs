using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.Core.Presentation
{
	/// <summary>One command a user can give a keyboard shortcut: every AddIn-tree item that runs it is one of its <see cref="Items"/>.</summary>
	public sealed class CommandCatalogEntry
	{
		public string Key { get; internal set; }

		/// <summary>The label of the first item that runs the command, mnemonics removed; the key when no item has a label.</summary>
		public string DisplayName { get; internal set; }

		/// <summary>The manifest shortcut of the command (null for none): the one any item carries; the first when items disagree.</summary>
		public string DefaultGesture { get; internal set; }

		/// <summary>The command is a WPF routed command: its keys come from the framework, so it is shown but not changed.</summary>
		public bool IsRouted { get { return Key.StartsWith(CommandKey.RoutedPrefix, StringComparison.Ordinal); } }

		/// <summary>The codons (menu items, toolbar items, pads) that run the command.</summary>
		public IReadOnlyList<Codon> Items { get; internal set; }

		/// <summary>The AddIn-tree path each item of <see cref="Items"/> sits in, in the same order.</summary>
		public IReadOnlyList<string> ItemPaths { get; internal set; }
	}

	/// <summary>
	/// The commands of the AddIn tree, found by walking the codons of the loaded AddIns: nothing is instantiated, so it also covers sub-menus
	/// and context menus, which are built only when opened. doc/technotes/keyboard-shortcuts.md.
	/// </summary>
	public static class CommandCatalog
	{
		/// <summary>The codon kinds that run a command; Menu, Separator and the like do not.</summary>
		static readonly HashSet<string> CommandCodons = new HashSet<string> { "MenuItem", "ToolbarItem", "Pad" };

		/// <summary>An item <c>type</c> that is not one plain command: a Builder makes items, a Custom or DropDownButton item builds its own control.</summary>
		static readonly HashSet<string> NonCommandTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Builder", "Menu", "Separator", "Custom", "DropDownButton" };

		/// <summary>Whether the codon is an item that runs a command (it has a class, a routed command or a link, or is a pad).</summary>
		public static bool IsCommand(Codon codon)
		{
			if (codon == null || !CommandCodons.Contains(codon.Name))
				return false;
			if (codon.Name == "Pad")
				return !string.IsNullOrEmpty(codon.Id) || !string.IsNullOrEmpty(codon.Properties["class"]);
			string type = codon.Properties["type"];
			if (!string.IsNullOrEmpty(type) && NonCommandTypes.Contains(type))
				return false;
			return !string.IsNullOrEmpty(codon.Properties["class"]) || !string.IsNullOrEmpty(codon.Properties["command"])
				|| !string.IsNullOrEmpty(codon.Properties["link"]);
		}

		/// <summary>The commands of these AddIns (the loaded ones), ordered by name.</summary>
		public static IReadOnlyList<CommandCatalogEntry> Build(IEnumerable<AddIn> addIns)
		{
			if (addIns == null)
				throw new ArgumentNullException("addIns");
			var groups = new Dictionary<string, List<KeyValuePair<string, Codon>>>();
			var order = new List<string>();
			foreach (AddIn addIn in addIns) {
				if (addIn.Action != AddInAction.Enable || addIn.Suspended)
					continue;
				foreach (ExtensionPath path in addIn.Paths.Values) {
					foreach (Codon codon in path.Codons) {
						if (!IsCommand(codon))
							continue;
						string key = CommandKey.FromCodon(codon);
						List<KeyValuePair<string, Codon>> list;
						if (!groups.TryGetValue(key, out list)) {
							list = new List<KeyValuePair<string, Codon>>();
							groups.Add(key, list);
							order.Add(key);
						}
						list.Add(new KeyValuePair<string, Codon>(path.Name, codon));
					}
				}
			}
			return order.Select(key => Create(key, groups[key]))
				.OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
				.ThenBy(entry => entry.Key, StringComparer.Ordinal)
				.ToList();
		}

		static CommandCatalogEntry Create(string key, List<KeyValuePair<string, Codon>> pairs)
		{
			List<Codon> items = pairs.Select(p => p.Value).ToList();
			string label = items.Select(c => c.Properties["label"]).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
			string shortcut = items.Select(c => c.Properties["shortcut"]).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
			return new CommandCatalogEntry {
				Key = key,
				DisplayName = label != null ? StripMnemonic(StringParser.Parse(label)) : key,
				DefaultGesture = shortcut,
				Items = items,
				ItemPaths = pairs.Select(p => p.Key).ToList()
			};
		}

		/// <summary>The labels of the main menu's top-level menus (id -> label, mnemonics removed), for naming the groups of the Keyboard page.</summary>
		public static IReadOnlyDictionary<string, string> MainMenuLabels(IEnumerable<AddIn> addIns)
		{
			var result = new Dictionary<string, string>();
			foreach (AddIn addIn in addIns) {
				if (addIn.Action != AddInAction.Enable || addIn.Suspended)
					continue;
				ExtensionPath path;
				if (!addIn.Paths.TryGetValue(MainMenuPath, out path))
					continue;
				foreach (Codon codon in path.Codons) {
					string label = codon.Properties["label"];
					if (codon.Name == "MenuItem" && !string.IsNullOrEmpty(codon.Id) && !string.IsNullOrWhiteSpace(label) && !result.ContainsKey(codon.Id))
						result[codon.Id] = StripMnemonic(StringParser.Parse(label));
				}
			}
			return result;
		}

		public const string MainMenuPath = "/SharpDevelop/Workbench/MainMenu";

		/// <summary>The shortcut a command has in the manifests: what an item without a <c>shortcut</c> of its own takes.</summary>
		public static IReadOnlyDictionary<string, string> DefaultGestures(IEnumerable<AddIn> addIns)
		{
			var result = new Dictionary<string, string>();
			foreach (CommandCatalogEntry entry in Build(addIns))
				if (entry.DefaultGesture != null)
					result[entry.Key] = entry.DefaultGesture;
			return result;
		}

		/// <summary>"_Build" or "&amp;Build" as shown without the access key marker.</summary>
		static string StripMnemonic(string label)
		{
			return label.Replace("&&", "\u0001").Replace("&", "").Replace("_", "").Replace("\u0001", "&");
		}
	}
}
