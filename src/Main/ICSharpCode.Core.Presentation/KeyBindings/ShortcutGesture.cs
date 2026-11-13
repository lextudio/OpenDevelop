using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace ICSharpCode.Core.Presentation
{
	/// <summary>
	/// A keyboard shortcut: one key stroke (<c>Ctrl+Shift+F8</c>) or a chord of two (<c>Ctrl+K, Ctrl+C</c>, as in Visual Studio). WPF's
	/// <see cref="KeyGesture"/> is a single stroke, so the chord is this type. doc/technotes/keyboard-shortcuts.md.
	/// </summary>
	public sealed class ShortcutGesture
	{
		public ShortcutGesture(IEnumerable<KeyGesture> strokes)
		{
			if (strokes == null)
				throw new ArgumentNullException("strokes");
			Strokes = strokes.ToList();
			if (Strokes.Count < 1 || Strokes.Count > 2)
				throw new ArgumentException("A shortcut has one or two key strokes.", "strokes");
		}

		public IReadOnlyList<KeyGesture> Strokes { get; private set; }

		public bool IsChord { get { return Strokes.Count == 2; } }

		public KeyGesture First { get { return Strokes[0]; } }

		public KeyGesture Second { get { return Strokes.Count > 1 ? Strokes[1] : null; } }

		/// <summary>Parses <c>Ctrl+K, Ctrl+C</c> or <c>Control|K, Control|C</c> (strokes separated by a comma); false for text that is not a shortcut.</summary>
		public static bool TryParse(string text, out ShortcutGesture gesture)
		{
			gesture = null;
			if (string.IsNullOrWhiteSpace(text))
				return false;
			var strokes = new List<KeyGesture>();
			foreach (string part in text.Split(',')) {
				try {
					// an empty stroke ("Ctrl+K,") converts to null, which is not a stroke
					KeyGesture stroke = string.IsNullOrWhiteSpace(part) ? null : MenuService.ParseShortcut(part.Trim());
					if (stroke == null)
						return false;
					strokes.Add(stroke);
				} catch (Exception ex) when (ex is NotSupportedException || ex is FormatException || ex is ArgumentException) {
					return false;
				}
			}
			if (strokes.Count > 2)
				return false;
			gesture = new ShortcutGesture(strokes);
			return true;
		}

		/// <summary>The text in the normal form (<c>Ctrl+K, Ctrl+C</c>), what KeyBindings.xml stores.</summary>
		public string ToInvariantText()
		{
			return string.Join(", ", Strokes.Select(s => new KeyGestureConverter().ConvertToInvariantString(s)));
		}

		/// <summary>The text for a menu item: each stroke in the platform's display form.</summary>
		public string ToDisplayText()
		{
			return string.Join(", ", Strokes.Select(s => MenuService.GetDisplayStringForShortcut(s)));
		}

		/// <summary>Whether the other shortcut starts with the same stroke: a single stroke and a chord that begins with it cannot both work, and neither can two chords with the same two strokes.</summary>
		public bool Overlaps(ShortcutGesture other)
		{
			if (other == null)
				return false;
			if (!SameStroke(First, other.First))
				return false;
			if (IsChord && other.IsChord)
				return SameStroke(Second, other.Second);
			return true; // the first stroke would be taken by one of them
		}

		public static bool SameStroke(KeyGesture a, KeyGesture b)
		{
			return a != null && b != null && a.Key == b.Key && a.Modifiers == b.Modifiers;
		}
	}
}
