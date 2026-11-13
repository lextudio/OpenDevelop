using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace ICSharpCode.Core.Presentation
{
	/// <summary>What a key press did to a <see cref="ChordRouter"/>.</summary>
	public enum ChordOutcome
	{
		/// <summary>Not part of any chord: the key goes on to whatever else handles it.</summary>
		None,
		/// <summary>The first stroke of a chord: the key is taken and the router waits for the second.</summary>
		Started,
		/// <summary>The second stroke: the chord's command is to run (see <see cref="ChordRouter.Press"/>).</summary>
		Completed,
		/// <summary>A chord was pending and this key is not its second stroke: the pending first stroke is dropped (the key itself is looked at afresh and is not taken here).</summary>
		Cancelled
	}

	/// <summary>
	/// The state machine of key chords for one owner: the chords it knows, the first stroke that is pending, and how long it stays pending. It does not know
	/// WPF events or commands' threads; <see cref="KeyBindingService"/> feeds it the key presses of an owner. doc/technotes/keyboard-shortcuts.md.
	/// </summary>
	public sealed class ChordRouter
	{
		/// <summary>How long the second stroke may take.</summary>
		public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

		sealed class Entry
		{
			public KeyGesture First;
			public KeyGesture Second;
			public ICommand Command;
		}

		readonly List<Entry> entries = new List<Entry>();
		readonly TimeSpan timeout;
		KeyGesture pending;
		DateTime pendingSince;

		public ChordRouter() : this(DefaultTimeout) { }

		public ChordRouter(TimeSpan timeout)
		{
			this.timeout = timeout;
		}

		/// <summary>The first stroke waiting for its second, or null.</summary>
		public KeyGesture Pending { get { return pending; } }

		/// <summary>Forgets the chords (a rebuild is about to add them again) and any pending first stroke.</summary>
		public void Clear()
		{
			entries.Clear();
			pending = null;
		}

		/// <summary>Adds a chord; chords with the same two strokes run the first of their commands that can execute.</summary>
		public void Add(ShortcutGesture chord, ICommand command)
		{
			if (chord == null || !chord.IsChord)
				throw new ArgumentException("A chord of two strokes is needed.", "chord");
			if (command == null)
				throw new ArgumentNullException("command");
			Entry existing = entries.FirstOrDefault(e => ShortcutGesture.SameStroke(e.First, chord.First) && ShortcutGesture.SameStroke(e.Second, chord.Second));
			if (existing != null)
				existing.Command = new AmbiguousCommandWrapper(existing.Command, command);
			else
				entries.Add(new Entry { First = chord.First, Second = chord.Second, Command = command });
		}

		public bool HasChords { get { return entries.Count > 0; } }

		/// <summary>True for the keys that are only a modifier going down: they are part of a stroke, not a stroke, so they change nothing.</summary>
		public static bool IsModifierOnly(Key key)
		{
			switch (key) {
				case Key.LeftCtrl: case Key.RightCtrl: case Key.LeftAlt: case Key.RightAlt: case Key.LeftShift: case Key.RightShift:
				case Key.LWin: case Key.RWin: case Key.System: case Key.None: case Key.DeadCharProcessed:
					return true;
				default:
					return false;
			}
		}

		/// <summary>Feeds one key press (the key and the modifiers held).</summary>
		/// <param name="command">For <see cref="ChordOutcome.Completed"/>: the command of the chord.</param>
		public ChordOutcome Press(Key key, ModifierKeys modifiers, DateTime now, out ICommand command)
		{
			command = null;
			if (entries.Count == 0 || IsModifierOnly(key))
				return ChordOutcome.None;
			bool expired = pending != null && now - pendingSince > timeout;
			bool hadPending = pending != null && !expired;
			if (hadPending) {
				KeyGesture first = pending;
				pending = null;
				Entry completed = entries.FirstOrDefault(e => ShortcutGesture.SameStroke(e.First, first) && e.Second.Key == key && e.Second.Modifiers == modifiers);
				if (completed != null) {
					command = completed.Command;
					return ChordOutcome.Completed;
				}
			}
			pending = null;
			if (entries.Any(e => e.First.Key == key && e.First.Modifiers == modifiers)) {
				pending = entries.First(e => e.First.Key == key && e.First.Modifiers == modifiers).First;
				pendingSince = now;
				return ChordOutcome.Started;
			}
			return hadPending ? ChordOutcome.Cancelled : ChordOutcome.None;
		}
	}
}
