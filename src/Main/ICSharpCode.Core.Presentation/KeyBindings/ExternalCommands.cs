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
			// Without a scope the command would count as a Workbench command and could take a menu command's shortcut away instead of shadowing it
			if (string.IsNullOrEmpty(scope))
				throw new ArgumentException("A command needs a scope.", "scope");
			if (string.IsNullOrEmpty(group))
				throw new ArgumentException("A command needs a group.", "group");
			Key = key;
			Name = name ?? key;
			Group = group;
			Scope = scope;
			DefaultGesture = defaultGesture;
		}

		/// <summary>"BoxSelectDownByLine" as "Box Select Down By Line" (a framework command's name made readable); an acronym such as "IME" stays together.</summary>
		public static string Humanize(string pascalCase)
		{
			if (string.IsNullOrEmpty(pascalCase))
				return pascalCase;
			var result = new System.Text.StringBuilder();
			for (int i = 0; i < pascalCase.Length; i++) {
				char c = pascalCase[i];
				bool boundary = i > 0 && char.IsUpper(c) && (char.IsLower(pascalCase[i - 1]) || (i + 1 < pascalCase.Length && char.IsLower(pascalCase[i + 1]) && char.IsUpper(pascalCase[i - 1])));
				if (boundary)
					result.Append(' ');
				result.Append(c);
			}
			return result.ToString();
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

	/// <summary>A list of sources of <see cref="ExternalCommand"/>s.</summary>
	public sealed class ExternalCommandRegistry
	{
		readonly object syncRoot = new object();
		readonly List<Func<IEnumerable<ExternalCommand>>> providers = new List<Func<IEnumerable<ExternalCommand>>>();

		/// <summary>Adds a source of commands; it is asked each time <see cref="All"/> is called (the page opens), on the UI thread.</summary>
		public void Register(Func<IEnumerable<ExternalCommand>> provider)
		{
			if (provider == null)
				throw new ArgumentNullException("provider");
			lock (syncRoot)
				providers.Add(provider);
		}

		/// <summary>The commands of every provider, in registration order; a provider that throws is skipped (and logged), not fatal.</summary>
		public IReadOnlyList<ExternalCommand> All()
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

	/// <summary>Where the owners of <see cref="ExternalCommand"/>s tell the Keyboard page about them (the registry of the running application).</summary>
	public static class ExternalCommands
	{
		static readonly ExternalCommandRegistry registry = new ExternalCommandRegistry();

		public static void Register(Func<IEnumerable<ExternalCommand>> provider)
		{
			registry.Register(provider);
		}

		public static IReadOnlyList<ExternalCommand> All()
		{
			return registry.All();
		}
	}
}
