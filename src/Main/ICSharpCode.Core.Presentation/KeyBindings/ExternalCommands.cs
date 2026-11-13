using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.Core.Presentation
{
	/// <summary>
	/// A command that is not an AddIn-tree item but still has a keyboard shortcut a user may want to change: the text editor's own editing and caret
	/// commands, for example. The component that owns it describes it here and applies the override itself. doc/technotes/keyboard-shortcuts.md.
	/// </summary>
	public sealed class ExternalCommand
	{
		public ExternalCommand(string key, string name, string group, string scope, string defaultGesture)
		{
			if (string.IsNullOrEmpty(key))
				throw new ArgumentException("A command needs a key.", "key");
			Key = key;
			Name = name ?? key;
			Group = group;
			Scope = scope;
			DefaultGesture = defaultGesture;
		}

		/// <summary>The command key, for example <c>editor:EditingCommands.Backspace</c>; it is what KeyBindings.xml stores.</summary>
		public string Key { get; private set; }

		/// <summary>The name shown on the Keyboard page.</summary>
		public string Name { get; private set; }

		/// <summary>The heading the page lists the command under.</summary>
		public string Group { get; private set; }

		/// <summary>The scope it works in (the text editor, say): the same gesture in another scope is shadowing, not a conflict.</summary>
		public string Scope { get; private set; }

		/// <summary>The gesture it has until the user changes it, in the text form of KeyGestureConverter; null for none.</summary>
		public string DefaultGesture { get; private set; }
	}

	/// <summary>Where the owners of <see cref="ExternalCommand"/>s tell the Keyboard page about them.</summary>
	public static class ExternalCommands
	{
		static readonly object syncRoot = new object();
		static readonly List<Func<IEnumerable<ExternalCommand>>> providers = new List<Func<IEnumerable<ExternalCommand>>>();

		/// <summary>Adds a source of commands; it is asked each time the page opens, on the UI thread.</summary>
		public static void Register(Func<IEnumerable<ExternalCommand>> provider)
		{
			if (provider == null)
				throw new ArgumentNullException("provider");
			lock (syncRoot)
				providers.Add(provider);
		}

		/// <summary>The commands of every provider; a provider that throws is skipped (and logged), not fatal.</summary>
		public static IReadOnlyList<ExternalCommand> All()
		{
			Func<IEnumerable<ExternalCommand>>[] snapshot;
			lock (syncRoot)
				snapshot = providers.ToArray();
			var result = new List<ExternalCommand>();
			foreach (var provider in snapshot) {
				try {
					result.AddRange(provider());
				} catch (Exception ex) {
					LoggingService.Warn("A source of keyboard shortcut commands failed and is skipped.", ex);
				}
			}
			return result;
		}
	}
}
