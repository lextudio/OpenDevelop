using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace ICSharpCode.Core.Presentation
{
	/// <summary>
	/// Rebuilds the key bindings of one input handler (a list that belongs to one editor) from the bindings it was created with and the user's overrides.
	/// The original bindings may be shared and frozen, so they are only ever removed from, and added back to, the list: never changed. Pure of any application
	/// state, so it can be tested. doc/technotes/keyboard-shortcuts.md.
	/// </summary>
	public static class ManagedBindings
	{
		/// <param name="bindings">The handler's live list; it is changed.</param>
		/// <param name="originals">The bindings the handler had before any override, in order.</param>
		/// <param name="added">The bindings an earlier call added (removed first); it is updated to the ones added now.</param>
		/// <param name="keyOf">The command key of a binding's command; null when the command is not managed (it is then left as it is).</param>
		/// <param name="hasOverride">Whether the user gave the command an override (a gesture, or "none").</param>
		/// <param name="resolve">The gesture the command has now; null for "no shortcut".</param>
		public static void Rebuild(ICollection<InputBinding> bindings, IReadOnlyList<InputBinding> originals, List<InputBinding> added,
		                           Func<ICommand, string> keyOf, Func<string, bool> hasOverride, Func<string, KeyGesture> resolve)
		{
			if (bindings == null) throw new ArgumentNullException("bindings");
			if (originals == null) throw new ArgumentNullException("originals");
			if (added == null) throw new ArgumentNullException("added");
			foreach (InputBinding previous in added)
				bindings.Remove(previous);
			added.Clear();
			var replaced = new HashSet<string>();
			foreach (InputBinding original in originals) {
				string key = original is KeyBinding ? keyOf(original.Command) : null;
				if (key == null || !hasOverride(key)) {
					if (!bindings.Contains(original))
						bindings.Add(original);
					continue;
				}
				bindings.Remove(original);
				if (!replaced.Add(key))
					continue; // one new binding per command, however many it had
				KeyGesture gesture = resolve(key);
				if (gesture == null)
					continue; // "no shortcut"
				var binding = new KeyBinding(original.Command, gesture.Key, gesture.Modifiers);
				bindings.Add(binding);
				added.Add(binding);
			}
		}
	}
}
