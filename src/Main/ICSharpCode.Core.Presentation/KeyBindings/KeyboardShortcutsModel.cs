using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace ICSharpCode.Core.Presentation
{
	/// <summary>One row of the Keyboard page: a command and the shortcut it has in the working copy.</summary>
	public sealed class ShortcutRow
	{
		internal ShortcutRow(CommandCatalogEntry entry, IReadOnlyDictionary<string, string> groupLabels)
		{
			Entry = entry;
			Key = entry.Key;
			Name = entry.DisplayName;
			IsRouted = entry.IsRouted;
			DefaultGesture = KeyboardShortcutsModel.Normalize(entry.DefaultGesture);
			Locations = entry.ItemPaths.Distinct().ToList();
			Scope = entry.ExplicitScope ?? KeyboardShortcutsModel.ScopeOf(entry.ItemPaths);
			Group = entry.ExplicitGroup ?? KeyboardShortcutsModel.GroupOf(entry.ItemPaths);
			string label;
			GroupName = groupLabels != null && groupLabels.TryGetValue(Group, out label) ? label : Group;
		}

		internal CommandCatalogEntry Entry { get; private set; }
		public string Key { get; private set; }
		public string Name { get; private set; }
		public bool IsRouted { get; private set; }

		/// <summary>The manifest shortcut in the normal form (Ctrl+Shift+F8), null for none.</summary>
		public string DefaultGesture { get; private set; }

		/// <summary>The shortcut in the working copy in the normal form, null for none.</summary>
		public string Gesture { get; internal set; }

		/// <summary>The working copy differs from the manifest default.</summary>
		public bool IsCustomized { get { return !string.Equals(Gesture, DefaultGesture, StringComparison.OrdinalIgnoreCase); } }

		/// <summary>The AddIn-tree paths of the items that run the command.</summary>
		public IReadOnlyList<string> Locations { get; private set; }

		/// <summary>The scope the shortcut works in: <see cref="KeyboardShortcutsModel.WorkbenchScope"/>, or the control (a context menu's owner).</summary>
		public string Scope { get; private set; }

		/// <summary>The heading the page groups the command under (a main menu, "Toolbars", "Pads", a control's name).</summary>
		public string Group { get; private set; }

		/// <summary>The heading to show: the label of the main menu when the model knows it (translated), else <see cref="Group"/>.</summary>
		public string GroupName { get; private set; }
	}

	/// <summary>An override in KeyBindings.xml for a command the catalog does not have now (its AddIn is disabled, suspended or gone).</summary>
	public sealed class UnknownOverride
	{
		internal UnknownOverride(string key, string gesture) { Key = key; Gesture = gesture; }

		public string Key { get; private set; }

		/// <summary>The gesture as written in the file; empty means "no shortcut".</summary>
		public string Gesture { get; private set; }
	}

	/// <summary>One shortcut an import would change.</summary>
	public sealed class ImportChange
	{
		internal ImportChange(ShortcutRow row, string oldGesture, string newGesture) { Row = row; OldGesture = oldGesture; NewGesture = newGesture; }

		public ShortcutRow Row { get; private set; }
		public string OldGesture { get; private set; }

		/// <summary>The gesture the file gives the command; null for "no shortcut".</summary>
		public string NewGesture { get; private set; }
	}

	/// <summary>What importing a file would do, before anything is changed.</summary>
	public sealed class ImportPreview
	{
		internal ImportPreview(IReadOnlyList<ImportChange> changes, int skipped, IReadOnlyList<string> invalid, IReadOnlyList<ShortcutRow> conflicts)
		{
			Changes = changes; Skipped = skipped; Invalid = invalid; Conflicts = conflicts;
		}

		/// <summary>The commands whose shortcut would change.</summary>
		public IReadOnlyList<ImportChange> Changes { get; private set; }

		/// <summary>Entries for commands this installation does not have, and built-in (routed) commands: not imported.</summary>
		public int Skipped { get; private set; }

		/// <summary>Command keys whose gesture in the file the page would refuse to assign (not a keyboard shortcut, reserved by the system, held by a built-in command): not imported.</summary>
		public IReadOnlyList<string> Invalid { get; private set; }

		/// <summary>Commands that would share a gesture with another command of the same scope after the import (they are not refused: the page shows them under Conflicts).</summary>
		public IReadOnlyList<ShortcutRow> Conflicts { get; private set; }
	}

	/// <summary>What a capture box does with a key press.</summary>
	public enum CaptureKeyAction
	{
		/// <summary>Not a shortcut here: Tab leaves the box and Esc closes the dialog (alone), so the page stays usable from the keyboard.</summary>
		PassThrough,
		/// <summary>Backspace or Delete (alone): empty the box.</summary>
		Clear,
		/// <summary>Anything else: it is swallowed, and shown as a chord once it is one (see <see cref="KeyboardShortcutsModel.FormatChord"/>).</summary>
		Capture
	}

	/// <summary>Why a gesture cannot be assigned, and who already has it.</summary>
	public sealed class AssignCheck
	{
		internal AssignCheck(string gesture, string error, IReadOnlyList<ShortcutRow> conflicts, IReadOnlyList<ShortcutRow> shadows)
		{
			Gesture = gesture;
			Error = error;
			Conflicts = conflicts;
			Shadows = shadows;
		}

		/// <summary>The gesture in the normal form; null for "none" or when it is invalid.</summary>
		public string Gesture { get; private set; }

		/// <summary>Null when the gesture can be assigned (possibly after reassigning); else the reason it cannot.</summary>
		public string Error { get; private set; }

		/// <summary>Commands in the same scope that have the gesture: assigning needs them to give it up.</summary>
		public IReadOnlyList<ShortcutRow> Conflicts { get; private set; }

		/// <summary>Commands of the other kind of scope (Workbench against a control) with the gesture: the control wins while it has focus. A note, not a conflict.</summary>
		public IReadOnlyList<ShortcutRow> Shadows { get; private set; }

		public bool IsValid { get { return Error == null; } }
	}

	/// <summary>
	/// The working copy behind the Keyboard page: the commands of the <see cref="CommandCatalog"/> with the shortcut each has once the pending
	/// changes are applied. Nothing reaches the <see cref="KeyBindingService"/> until <see cref="Commit"/>. doc/technotes/keyboard-shortcuts.md.
	/// </summary>
	public sealed class KeyboardShortcutsModel
	{
		public const string WorkbenchScope = "Workbench";

		/// <summary>Gestures that cannot work as a command shortcut, in the normal form. Data, not code.</summary>
		static readonly HashSet<string> ReservedGestures = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alt+F4" };

		static readonly string[] WorkbenchPrefixes = {
			"/SharpDevelop/Workbench/MainMenu", "/SharpDevelop/Workbench/ToolBar", "/SharpDevelop/Workbench/Pads", "/SharpDevelop/Workbench/Tools"
		};

		readonly List<ShortcutRow> rows;
		readonly Dictionary<string, ShortcutRow> byKey;
		readonly Dictionary<string, string> initialOverrides;
		readonly HashSet<string> removedUnknown = new HashSet<string>();

		/// <param name="catalog">The commands.</param>
		/// <param name="groupLabels">Main menu id -> its label, to name the groups; null shows the ids.</param>
		/// <param name="overrides">The overrides on disk now (command key -> gesture, empty = none); keys the catalog does not know are kept untouched.</param>
		public KeyboardShortcutsModel(IReadOnlyList<CommandCatalogEntry> catalog, IReadOnlyDictionary<string, string> overrides, IReadOnlyDictionary<string, string> groupLabels = null)
		{
			if (catalog == null)
				throw new ArgumentNullException("catalog");
			rows = catalog.Select(e => new ShortcutRow(e, groupLabels)).ToList();
			byKey = rows.ToDictionary(r => r.Key);
			initialOverrides = overrides == null ? new Dictionary<string, string>() : overrides.ToDictionary(p => p.Key, p => p.Value);
			foreach (ShortcutRow row in rows) {
				string overridden;
				row.Gesture = initialOverrides.TryGetValue(row.Key, out overridden) ? Normalize(overridden) ?? OverrideFallback(overridden, row) : row.DefaultGesture;
			}
		}

		// an override that does not parse is ignored by the service, so the page shows the default
		static string OverrideFallback(string overridden, ShortcutRow row)
		{
			return string.IsNullOrWhiteSpace(overridden) ? null : row.DefaultGesture;
		}

		public IReadOnlyList<ShortcutRow> Rows { get { return rows; } }

		/// <summary>Overrides of commands that are not in the catalog, which the page cannot show as rows; the user can remove them. Kept (not erased) otherwise.</summary>
		public IReadOnlyList<UnknownOverride> UnknownOverrides {
			get {
				return initialOverrides.Where(p => !byKey.ContainsKey(p.Key) && !removedUnknown.Contains(p.Key))
					.OrderBy(p => p.Key, StringComparer.Ordinal)
					.Select(p => new UnknownOverride(p.Key, p.Value)).ToList();
			}
		}

		/// <summary>Drops the override of a command that is not loaded (it would apply again if its AddIn came back).</summary>
		public void RemoveUnknown(string commandKey)
		{
			if (commandKey != null && !byKey.ContainsKey(commandKey) && initialOverrides.ContainsKey(commandKey))
				removedUnknown.Add(commandKey);
		}

		public ShortcutRow Find(string commandKey)
		{
			ShortcutRow row;
			return byKey.TryGetValue(commandKey, out row) ? row : null;
		}

		/// <summary>The gesture in the normal form (Ctrl+Shift+F8); null for none or for text that is not a gesture.</summary>
		public static string Normalize(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
				return null;
			try {
				return new KeyGestureConverter().ConvertToInvariantString(MenuService.ParseShortcut(text));
			} catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException || ex is FormatException) {
				return null;
			}
		}

		/// <summary>What a capture box (the shortcut box or the shortcut search) does with this key and the modifiers held.</summary>
		public static CaptureKeyAction ClassifyCaptureKey(Key key, ModifierKeys modifiers)
		{
			if (modifiers == ModifierKeys.None) {
				if (key == Key.Tab || key == Key.Escape)
					return CaptureKeyAction.PassThrough;
				if (key == Key.Back || key == Key.Delete)
					return CaptureKeyAction.Clear;
			}
			return CaptureKeyAction.Capture;
		}

		/// <summary>Whether two texts are the same gesture (Control|F8, Ctrl+F8 and ctrl+f8 are); false when either is not a gesture. What the shortcut search compares.</summary>
		public static bool SameGesture(string a, string b)
		{
			string x = Normalize(a);
			return x != null && string.Equals(x, Normalize(b), StringComparison.OrdinalIgnoreCase);
		}

		public static string ScopeOf(IEnumerable<string> paths)
		{
			var list = paths.ToList();
			if (list.Any(IsWorkbenchPath))
				return WorkbenchScope;
			return ControlScope(list.FirstOrDefault());
		}

		static bool IsWorkbenchPath(string path)
		{
			return path != null && WorkbenchPrefixes.Any(p => path.StartsWith(p, StringComparison.Ordinal));
		}

		/// <summary>The owner a context menu or a pad toolbar belongs to: the path up to its menu segment.</summary>
		static string ControlScope(string path)
		{
			if (string.IsNullOrEmpty(path))
				return WorkbenchScope;
			foreach (string marker in new[] { "/ContextMenu", "/ContextMenus", "/Toolbar", "/ToolBar", "/EntityContextMenu" }) {
				int index = path.IndexOf(marker, StringComparison.Ordinal);
				if (index > 0)
					return path.Substring(0, index);
			}
			return path;
		}

		public static string GroupOf(IEnumerable<string> paths)
		{
			var list = paths.ToList();
			const string mainMenu = "/SharpDevelop/Workbench/MainMenu";
			string menu = list.FirstOrDefault(p => p != null && p.StartsWith(mainMenu, StringComparison.Ordinal));
			if (menu != null) {
				string rest = menu.Substring(mainMenu.Length).Trim('/');
				return rest.Length == 0 ? "Main menu" : rest.Split('/')[0];
			}
			if (list.Any(p => p != null && p.StartsWith("/SharpDevelop/Workbench/ToolBar", StringComparison.Ordinal)))
				return "Toolbars";
			if (list.Any(p => p != null && p.StartsWith("/SharpDevelop/Workbench/Pads", StringComparison.Ordinal)))
				return "Pads";
			if (list.Any(p => p != null && p.StartsWith("/SharpDevelop/Workbench/Tools", StringComparison.Ordinal)))
				return "Tools";
			string scope = ControlScope(list.FirstOrDefault());
			return scope.Substring(scope.LastIndexOf('/') + 1);
		}

		/// <summary>Whether the gesture can be given to the command, and who has it already. <paramref name="gestureText"/> empty means "none" and always passes.</summary>
		public AssignCheck Check(string commandKey, string gestureText)
		{
			ShortcutRow row = Find(commandKey);
			if (row == null)
				return Fail("The command is not known.");
			if (row.IsRouted)
				return Fail("This command is built in: its keys come from the framework and cannot be changed here.");
			if (string.IsNullOrWhiteSpace(gestureText))
				return new AssignCheck(null, null, new ShortcutRow[0], new ShortcutRow[0]);
			string gesture = Normalize(gestureText);
			if (gesture == null)
				return Fail("\"" + gestureText.Trim() + "\" is not a keyboard shortcut.");
			if (ReservedGestures.Contains(gesture))
				return Fail(gesture + " is reserved by the system.");
			var others = rows.Where(r => r != row && string.Equals(r.Gesture, gesture, StringComparison.OrdinalIgnoreCase)).ToList();
			var conflicts = others.Where(r => r.Scope == row.Scope).ToList();
			ShortcutRow builtIn = conflicts.FirstOrDefault(r => r.IsRouted);
			if (builtIn != null)
				return Fail(gesture + " belongs to the built-in command \"" + builtIn.Name + "\", which cannot give it up.");
			var shadows = others.Where(r => r.Scope != row.Scope && (r.Scope == WorkbenchScope || row.Scope == WorkbenchScope)).ToList();
			return new AssignCheck(gesture, null, conflicts, shadows);
		}

		static AssignCheck Fail(string error)
		{
			return new AssignCheck(null, error, new ShortcutRow[0], new ShortcutRow[0]);
		}

		/// <summary>
		/// Gives the command the gesture ("" or null: none). With conflicts, nothing happens unless <paramref name="reassign"/> is set, which takes the
		/// gesture away from the other commands (they get "none").
		/// </summary>
		/// <returns>The check; assigned when <see cref="AssignCheck.IsValid"/> and no unresolved conflict remained.</returns>
		public AssignCheck Assign(string commandKey, string gestureText, bool reassign = false)
		{
			AssignCheck check = Check(commandKey, gestureText);
			if (!check.IsValid)
				return check;
			if (check.Conflicts.Count > 0) {
				if (!reassign)
					return check;
				foreach (ShortcutRow other in check.Conflicts)
					other.Gesture = null;
			}
			Find(commandKey).Gesture = check.Gesture;
			return check;
		}

		/// <summary>Back to the manifest default.</summary>
		public void Reset(string commandKey)
		{
			ShortcutRow row = Find(commandKey);
			if (row != null && !row.IsRouted)
				row.Gesture = row.DefaultGesture;
		}

		public void ResetAll()
		{
			foreach (ShortcutRow row in rows)
				if (!row.IsRouted)
					row.Gesture = row.DefaultGesture;
			foreach (UnknownOverride unknown in UnknownOverrides)
				removedUnknown.Add(unknown.Key);
		}

		/// <summary>The overrides the working copy stands for: a row that differs from its default (an empty text when it was emptied), plus the keys the catalog does not know.</summary>
		public IReadOnlyDictionary<string, string> BuildOverrides()
		{
			var result = new Dictionary<string, string>();
			foreach (var pair in initialOverrides)
				if (!byKey.ContainsKey(pair.Key) && !removedUnknown.Contains(pair.Key))
					result[pair.Key] = pair.Value;
			foreach (ShortcutRow row in rows) {
				if (row.IsRouted) {
					string kept;
					if (initialOverrides.TryGetValue(row.Key, out kept))
						result[row.Key] = kept;
				} else if (row.IsCustomized) {
					result[row.Key] = row.Gesture ?? "";
				} else {
					// an override that is not a gesture is ignored by the service; the page does not erase it behind the user's back
					string unreadable;
					if (initialOverrides.TryGetValue(row.Key, out unreadable) && !string.IsNullOrWhiteSpace(unreadable) && Normalize(unreadable) == null)
						result[row.Key] = unreadable;
				}
			}
			return result;
		}

		/// <summary>The commands in the same scope that have the same gesture as this row (a conflict that is already in the copy, e.g. two manifests that agree).</summary>
		public IReadOnlyList<ShortcutRow> ConflictsOf(ShortcutRow row)
		{
			if (row == null || row.Gesture == null)
				return new ShortcutRow[0];
			return rows.Where(r => r != row && r.Scope == row.Scope && string.Equals(r.Gesture, row.Gesture, StringComparison.OrdinalIgnoreCase)).ToList();
		}

		/// <summary>
		/// The gesture a key press stands for, in the normal form; null while only modifiers are down or when the chord is not a valid shortcut
		/// (a bare letter, for instance). What the capture box shows.
		/// </summary>
		public static string FormatChord(Key key, ModifierKeys modifiers)
		{
			switch (key) {
				case Key.None: case Key.LeftCtrl: case Key.RightCtrl: case Key.LeftAlt: case Key.RightAlt:
				case Key.LeftShift: case Key.RightShift: case Key.LWin: case Key.RWin: case Key.System: case Key.DeadCharProcessed:
					return null;
			}
			try {
				return new KeyGestureConverter().ConvertToInvariantString(new KeyGesture(key, modifiers));
			} catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException || ex is FormatException) {
				return null;
			}
		}

		/// <summary>
		/// What importing these overrides (command key -> gesture, empty = none) would change in the working copy. The file is MERGED: commands it does not mention keep
		/// what they have. Nothing is changed here.
		/// </summary>
		public ImportPreview PreviewImport(IReadOnlyDictionary<string, string> imported)
		{
			if (imported == null)
				throw new ArgumentNullException("imported");
			var changes = new List<ImportChange>();
			var invalid = new List<string>();
			var planned = new Dictionary<ShortcutRow, string>();
			var changed = new HashSet<ShortcutRow>();
			int skipped = 0;
			foreach (var pair in imported) {
				ShortcutRow row = Find(pair.Key);
				if (row == null || row.IsRouted) {
					skipped++;
					continue;
				}
				// the rules of an assignment on the page: a gesture it would refuse (not a shortcut, reserved by the system, held by a built-in command) is not imported
				if (!Check(pair.Key, pair.Value).IsValid) {
					invalid.Add(pair.Key);
					continue;
				}
				string gesture = string.IsNullOrWhiteSpace(pair.Value) ? null : Normalize(pair.Value);
				planned[row] = gesture;
				if (!string.Equals(row.Gesture, gesture, StringComparison.OrdinalIgnoreCase)) {
					changes.Add(new ImportChange(row, row.Gesture, gesture));
					changed.Add(row);
				}
			}
			// conflicts as they would be after the import
			Func<ShortcutRow, string> after = r => { string g; return planned.TryGetValue(r, out g) ? g : r.Gesture; };
			var conflicts = rows.Where(r => after(r) != null && rows.Any(o => o != r && o.Scope == r.Scope && string.Equals(after(o), after(r), StringComparison.OrdinalIgnoreCase)
			                                                              && (changed.Contains(r) || changed.Contains(o)))).ToList();
			return new ImportPreview(changes, skipped, invalid, conflicts);
		}

		/// <summary>Applies a preview to the working copy (OK writes it, Cancel drops it).</summary>
		public void ApplyImport(ImportPreview preview)
		{
			if (preview == null)
				throw new ArgumentNullException("preview");
			foreach (ImportChange change in preview.Changes)
				change.Row.Gesture = change.NewGesture;
		}

		/// <summary>Writes the overrides of the working copy to a file in the format of KeyBindings.xml.</summary>
		public void Export(string path)
		{
			var file = new KeyBindingsFile();
			foreach (var pair in BuildOverrides())
				file.Set(pair.Key, pair.Value);
			file.Save(path);
		}

		/// <summary>Whether the working copy differs from what is on disk.</summary>
		public bool IsDirty {
			get {
				var now = BuildOverrides();
				return now.Count != initialOverrides.Count
					|| now.Any(p => { string old; return !initialOverrides.TryGetValue(p.Key, out old) || !SameText(old, p.Value); });
			}
		}

		static bool SameText(string a, string b)
		{
			return string.Equals(Normalize(a) ?? "", Normalize(b) ?? "", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>Writes the working copy through the service (one write); the menus and toolbars show the new keys at once.</summary>
		public void Commit(KeyBindingService service)
		{
			if (service == null)
				throw new ArgumentNullException("service");
			service.ReplaceOverrides(BuildOverrides());
		}
	}
}
