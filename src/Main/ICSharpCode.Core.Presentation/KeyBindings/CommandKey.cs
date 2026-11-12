using System;

namespace ICSharpCode.Core.Presentation
{
	/// <summary>
	/// The stable identity of a command, as far as keyboard shortcuts are concerned: a shortcut belongs to a COMMAND, not to one menu item,
	/// so every menu item, toolbar button and context-menu entry that runs the same command shares it. doc/technotes/keyboard-shortcuts.md.
	/// </summary>
	/// <remarks>
	/// <list type="bullet">
	/// <item><c>class:&lt;full class name&gt;</c> - an item with a <c>class</c> attribute.</item>
	/// <item><c>routed:&lt;name&gt;</c> - an item with a <c>command</c> attribute: a WPF routed command such as <c>Undo</c>.</item>
	/// <item><c>pad:&lt;pad id&gt;</c> - a pad (its id, or its class when it has none).</item>
	/// <item><c>item:&lt;AddIn identity&gt;/&lt;id&gt;</c> - an item with neither (a <c>link=</c> item); the tree path of an item is not known where its
	/// shortcut is installed, so the AddIn and the id stand in for it.</item>
	/// </list>
	/// </remarks>
	public static class CommandKey
	{
		public const string ClassPrefix = "class:";
		public const string RoutedPrefix = "routed:";
		public const string PadPrefix = "pad:";
		public const string ItemPrefix = "item:";

		/// <summary>The key of the command an AddIn-tree item runs.</summary>
		public static string FromCodon(Codon codon)
		{
			if (codon == null)
				throw new ArgumentNullException("codon");
			if (codon.Name == "Pad")
				return ForPad(codon.Id, codon.Properties["class"]);
			string className = codon.Properties["class"];
			if (!string.IsNullOrEmpty(className))
				return ClassPrefix + className;
			string commandName = codon.Properties["command"];
			if (!string.IsNullOrEmpty(commandName))
				return RoutedPrefix + commandName;
			string identity = null;
			if (codon.AddIn != null && codon.AddIn.Manifest != null)
				identity = codon.AddIn.Manifest.PrimaryIdentity;
			return ItemPrefix + (identity ?? "") + "/" + codon.Id;
		}

		/// <summary>The key of a pad: its id when it has one (a pad declared in an .addin), else its class.</summary>
		public static string ForPad(string padId, string padClass)
		{
			string name = !string.IsNullOrEmpty(padId) ? padId : padClass;
			if (string.IsNullOrEmpty(name))
				throw new ArgumentException("A pad needs an id or a class to have a command key.");
			return PadPrefix + name;
		}
	}
}
