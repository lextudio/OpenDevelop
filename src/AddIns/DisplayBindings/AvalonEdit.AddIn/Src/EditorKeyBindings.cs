using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;

using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;

namespace ICSharpCode.AvalonEdit.AddIn
{
	/// <summary>
	/// The text editor's own editing and caret commands as keyboard shortcut commands (key <c>editor:EditingCommands.Backspace</c>), and the
	/// application of the user's overrides to every open editor. doc/technotes/keyboard-shortcuts.md.
	/// </summary>
	/// <remarks>
	/// AvalonEdit adds its key bindings as FROZEN <see cref="KeyBinding"/>s shared by all editors, but each editor's handler lists
	/// (<c>DefaultInputHandler.Editing.InputBindings</c>, <c>CaretNavigation.InputBindings</c>) are its own and can be changed: this is what
	/// <c>CodeEditorView</c> already does for Tab. A binding whose command is not one of the framework's routed commands (that Tab one) is not managed here.
	/// </remarks>
	static class EditorKeyBindings
	{
		public const string KeyPrefix = "editor:";
		public const string Scope = "Text editor";

		/// <summary>What an editor had before any override: per handler list its original bindings, and the bindings this class added.</summary>
		sealed class EditorState
		{
			public readonly List<HandlerList> Lists = new List<HandlerList>();
		}

		sealed class HandlerList
		{
			public ICollection<InputBinding> Bindings;
			public List<InputBinding> Originals;
			public readonly List<InputBinding> Added = new List<InputBinding>();
		}

		/// <summary>
		/// Commands that put text into the document or move between its fields: removing their key would leave the editor unable to type a line break or indent,
		/// and the page could not warn about it, so they stay as AvalonEdit has them (like the Tab binding for snippets).
		/// </summary>
		static readonly HashSet<string> Unmanaged = new HashSet<string> {
			KeyPrefix + "EditingCommands.EnterParagraphBreak", KeyPrefix + "EditingCommands.EnterLineBreak",
			KeyPrefix + "EditingCommands.TabForward", KeyPrefix + "EditingCommands.TabBackward"
		};

		static readonly ConditionalWeakTable<TextArea, EditorState> states = new ConditionalWeakTable<TextArea, EditorState>();
		static readonly List<WeakReference<TextArea>> live = new List<WeakReference<TextArea>>();
		static bool subscribed;
		static Dictionary<string, ExternalCommand> described;

		/// <summary>The key of a framework command (<c>editor:EditingCommands.Backspace</c>), null for anything else; what the DevFlow action reports.</summary>
		public static string KeyFor(ICommand command)
		{
			var routed = command as RoutedUICommand;
			if (routed == null || routed.OwnerType == null || string.IsNullOrEmpty(routed.Name))
				return null;
			return KeyPrefix + routed.OwnerType.Name + "." + routed.Name;
		}

		static IEnumerable<ICollection<InputBinding>> HandlerLists(TextArea textArea)
		{
			yield return textArea.DefaultInputHandler.CaretNavigation.InputBindings;
			yield return textArea.DefaultInputHandler.Editing.InputBindings;
		}

		/// <summary>
		/// The commands for the Keyboard page, read from the bindings a fresh editor has (so they follow the AvalonEdit version in use). Creates and caches a real
		/// <see cref="TextArea"/>, so it must run on the UI thread (the page does); a failure is logged by the registry and the page then has no editor commands.
		/// </summary>
		public static IEnumerable<ExternalCommand> Describe()
		{
			return Described().Values;
		}

		static Dictionary<string, ExternalCommand> Described()
		{
			if (described != null)
				return described;
			var result = new Dictionary<string, ExternalCommand>();
			var prototype = new TextArea();
			foreach (ICollection<InputBinding> list in HandlerLists(prototype)) {
				foreach (KeyBinding binding in list.OfType<KeyBinding>()) {
					string key = KeyFor(binding.Command);
					// Enter and Return both exist for one command: the first gesture is the one shown
					if (key == null || Unmanaged.Contains(key) || result.ContainsKey(key))
						continue;
					var routed = (RoutedUICommand)binding.Command;
					string name = string.IsNullOrWhiteSpace(routed.Text) || routed.Text == routed.Name ? ExternalCommand.Humanize(routed.Name) : routed.Text;
					string gesture = new KeyGestureConverter().ConvertToInvariantString(new KeyGesture(binding.Key, binding.Modifiers));
					result[key] = new ExternalCommand(key, name, Scope, Scope, gesture);
				}
			}
			described = result;
			return result;
		}

		/// <summary>Called for each editor when it is created (after its own binding changes): remembers its bindings and applies the overrides.</summary>
		public static void Attach(TextArea textArea)
		{
			if (textArea == null)
				throw new ArgumentNullException("textArea");
			var state = new EditorState();
			foreach (ICollection<InputBinding> list in HandlerLists(textArea))
				state.Lists.Add(new HandlerList { Bindings = list, Originals = list.OfType<InputBinding>().ToList() });
			states.Remove(textArea);
			states.Add(textArea, state);
			live.Add(new WeakReference<TextArea>(textArea));
			if (!subscribed) {
				subscribed = true;
				KeyBindingService.Default.Changed += delegate { ApplyToAll(); };
			}
			Apply(textArea);
		}

		static void ApplyToAll()
		{
			live.RemoveAll(w => { TextArea t; return !w.TryGetTarget(out t); });
			foreach (var weak in live.ToArray()) {
				TextArea textArea;
				if (weak.TryGetTarget(out textArea))
					Apply(textArea);
			}
		}

		/// <summary>Rebuilds the managed bindings of one editor from its original ones and the current overrides.</summary>
		public static void Apply(TextArea textArea)
		{
			EditorState state;
			if (!states.TryGetValue(textArea, out state))
				return;
			var defaults = Described();
			var service = KeyBindingService.Default;
			foreach (HandlerList view in state.Lists) {
				ManagedBindings.Rebuild(view.Bindings, view.Originals, view.Added,
					command => { string key = KeyFor(command); return key != null && defaults.ContainsKey(key) ? key : null; },
					key => service.Overrides.ContainsKey(key),
					key => service.Resolve(key, defaults[key].DefaultGesture));
			}
		}
	}

	/// <summary>Registers the text editor's commands with the Keyboard page at startup.</summary>
	public sealed class RegisterEditorKeyCommandsCommand : AbstractCommand
	{
		public override void Run()
		{
			ExternalCommands.Register(EditorKeyBindings.Describe);
		}
	}
}
