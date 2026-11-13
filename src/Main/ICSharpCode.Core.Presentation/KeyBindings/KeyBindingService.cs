using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace ICSharpCode.Core.Presentation
{
	/// <summary>
	/// Keyboard shortcuts, with the user's overrides: one gesture per COMMAND (<see cref="CommandKey"/>), which is the override from
	/// <c>KeyBindings.xml</c> if there is one and otherwise the <c>shortcut</c> of the manifest. Every place that installs a shortcut
	/// (menu items, toolbar buttons, the pad menu) registers here instead of adding an <see cref="InputBinding"/> itself, so that a change
	/// applies at once, without a restart. doc/technotes/keyboard-shortcuts.md.
	/// </summary>
	/// <remarks>
	/// For every owner (the element that holds the <see cref="UIElement.InputBindings"/>) the service keeps the sites registered on it and DERIVES
	/// the bindings it adds from them: a change removes the bindings it added and adds the new ones, so nothing is left behind. A site is replaced when
	/// the same item (the same <c>codon</c>) registers again, which is what a context menu does every time it opens, so the table does not grow.
	/// Two sites with the same gesture on one owner are combined, the first that can execute runs, as before. Bindings that other code added to
	/// an owner are not touched.
	/// Everything that touches an owner must run on the owner's thread (the UI thread).
	/// </remarks>
	public sealed class KeyBindingService
	{
		static readonly object defaultLock = new object();
		static KeyBindingService defaultInstance;

		/// <summary>The service of the running application, reading and writing <c>KeyBindings.xml</c> in the user configuration directory.</summary>
		public static KeyBindingService Default {
			get {
				lock (defaultLock) {
					if (defaultInstance == null) {
						defaultInstance = new KeyBindingService(FindDefaultFile(), LoggingService.Warn);
						defaultInstance.CommandDefaultProvider = CreateAddInTreeDefaults();
					}
					return defaultInstance;
				}
			}
		}

		/// <summary>Replaces <see cref="Default"/> (tests); null restores the lazily created one.</summary>
		internal static void SetDefault(KeyBindingService service)
		{
			lock (defaultLock)
				defaultInstance = service;
		}

		/// <summary>The command-wide defaults of the AddIn tree, collected on first use (the tree is complete by the time the first menu is built).</summary>
		static Func<string, string> CreateAddInTreeDefaults()
		{
			IReadOnlyDictionary<string, string> defaults = null;
			return key => {
				try {
					if (defaults == null)
						defaults = CommandCatalog.DefaultGestures(ServiceSingleton.GetRequiredService<IAddInTree>().AddIns);
					string text;
					return defaults.TryGetValue(key, out text) ? text : null;
				} catch (Exception ex) {
					LoggingService.Warn("The command defaults of the AddIn tree are not available.", ex);
					defaults = new Dictionary<string, string>();
					return null;
				}
			};
		}

		static string FindDefaultFile()
		{
			try {
				DirectoryName directory = PropertyService.ConfigDirectory;
				return directory == null ? null : Path.Combine(directory.ToString(), "KeyBindings.xml");
			} catch (Exception ex) {
				// The property service is not running (a test, a tool): overrides then live in memory only
				LoggingService.Warn("Keyboard shortcut overrides will not be saved: no configuration directory.", ex);
				return null;
			}
		}

		sealed class Site
		{
			public string Key;
			public object Identity;
			public string DefaultGesture;
			public System.Windows.Input.ICommand Command;
			public string FeatureName;
			public Action<string> DisplayTextChanged;
			public ShortcutGesture Gesture;
		}

		sealed class OwnerState
		{
			public readonly List<Site> Sites = new List<Site>();
			public readonly List<InputBinding> Added = new List<InputBinding>();
			/// <summary>The key chords of the sites on this owner; the owner's PreviewKeyDown feeds it.</summary>
			public readonly ChordRouter Chords = new ChordRouter();
			public bool ChordHandlerAttached;
		}

		readonly string filePath;
		readonly Action<string> report;
		readonly ConditionalWeakTable<UIElement, OwnerState> owners = new ConditionalWeakTable<UIElement, OwnerState>();
		readonly List<WeakReference<UIElement>> liveOwners = new List<WeakReference<UIElement>>();
		KeyBindingsFile file;

		/// <param name="filePath">Where the overrides are kept; null keeps them in memory only.</param>
		/// <param name="report">Receives problems with the file or with a gesture (an unreadable file, a gesture that does not parse).</param>
		public KeyBindingService(string filePath, Action<string> report = null)
		{
			this.filePath = filePath;
			this.report = report ?? (message => { });
		}

		/// <summary>Raised after the overrides changed and the registered shortcuts have been updated.</summary>
		public event EventHandler Changed;

		KeyBindingsFile Data {
			get {
				if (file == null)
					file = KeyBindingsFile.Load(filePath, report);
				return file;
			}
		}

		/// <summary>
		/// The manifest shortcut of a command, for an item that has none of its own (an item of the command carries it elsewhere); null for none.
		/// Set it, then call <see cref="Reload"/> to apply it to the sites already registered.
		/// </summary>
		public Func<string, string> CommandDefaultProvider { get; set; }

		string EffectiveDefault(string commandKey, string defaultGesture)
		{
			if (!string.IsNullOrWhiteSpace(defaultGesture) || CommandDefaultProvider == null)
				return defaultGesture;
			return CommandDefaultProvider(commandKey);
		}

		/// <summary>The overrides: command key -> gesture text, an empty text meaning "no shortcut".</summary>
		public IReadOnlyDictionary<string, string> Overrides {
			get { return Data.Overrides; }
		}

		/// <summary>The gesture text of a command as it is written down, not validated: its override if there is one, else <paramref name="defaultGesture"/>; null for none.</summary>
		public string ResolveText(string commandKey, string defaultGesture)
		{
			string overridden;
			string text = Data.Overrides.TryGetValue(commandKey, out overridden) ? overridden : EffectiveDefault(commandKey, defaultGesture);
			return string.IsNullOrWhiteSpace(text) ? null : text;
		}

		/// <summary>
		/// The single-stroke gesture a command effectively has: its override (an empty one is the explicit "none"), else <paramref name="defaultGesture"/>.
		/// An override that does not parse is reported and ignored, so the default applies; null when there is no gesture, or when the shortcut is a key chord
		/// (see <see cref="ResolveShortcut"/>: the controls that match single strokes themselves do not take chords).
		/// </summary>
		public KeyGesture Resolve(string commandKey, string defaultGesture)
		{
			ShortcutGesture shortcut = ResolveShortcut(commandKey, defaultGesture);
			return shortcut != null && !shortcut.IsChord ? shortcut.First : null;
		}

		/// <summary>
		/// The shortcut a command effectively has (one key stroke or a chord of two): its override (an empty one is the explicit "none"), else the default.
		/// An override that does not parse is reported and ignored, so the default applies; null when there is none.
		/// </summary>
		public ShortcutGesture ResolveShortcut(string commandKey, string defaultGesture)
		{
			string overridden;
			if (Data.Overrides.TryGetValue(commandKey, out overridden)) {
				if (string.IsNullOrWhiteSpace(overridden))
					return null;
				ShortcutGesture gesture = TryParse(overridden, commandKey, "shortcut");
				if (gesture != null)
					return gesture;
			}
			defaultGesture = EffectiveDefault(commandKey, defaultGesture);
			return string.IsNullOrWhiteSpace(defaultGesture) ? null : TryParse(defaultGesture, commandKey, "default shortcut");
		}

		ShortcutGesture TryParse(string text, string commandKey, string what)
		{
			ShortcutGesture gesture;
			if (ShortcutGesture.TryParse(text, out gesture))
				return gesture;
			report("The " + what + " \"" + text + "\" of " + commandKey + " is not valid and is ignored.");
			return null;
		}

		/// <summary>Gives a command an override; <paramref name="gestureText"/> empty means "no shortcut", whatever the manifest says.</summary>
		/// <exception cref="FormatException">The text is not a gesture.</exception>
		public void SetOverride(string commandKey, string gestureText)
		{
			ShortcutGesture ignored;
			if (!string.IsNullOrWhiteSpace(gestureText) && !ShortcutGesture.TryParse(gestureText, out ignored))
				throw new FormatException("\"" + gestureText + "\" is not a keyboard shortcut.");
			Data.Set(commandKey, gestureText == null ? "" : gestureText.Trim());
			SaveAndApply();
		}

		/// <summary>Removes the override of a command: its manifest shortcut, if any, applies again.</summary>
		public void ResetOverride(string commandKey)
		{
			if (Data.Remove(commandKey))
				SaveAndApply();
		}

		/// <summary>Removes every override.</summary>
		/// <summary>
		/// Replaces every override at once (the Settings page applies its working copy this way): one write, one rebuild.
		/// Nothing is changed when a CHANGED gesture does not parse; an entry that is already in the file and is passed back unchanged is kept as it is.
		/// </summary>
		/// <exception cref="FormatException">A gesture is not valid.</exception>
		public void ReplaceOverrides(IEnumerable<KeyValuePair<string, string>> overrides)
		{
			if (overrides == null)
				throw new ArgumentNullException("overrides");
			var list = overrides.ToList();
			foreach (var pair in list) {
				string current;
				// an entry that is not changed is kept as it is, even when it is not a gesture (the file was edited by hand): it is ignored when applied
				if (string.IsNullOrWhiteSpace(pair.Value) || (Data.Overrides.TryGetValue(pair.Key, out current) && current == (pair.Value ?? "").Trim()))
					continue;
				ShortcutGesture ignored;
				if (!ShortcutGesture.TryParse(pair.Value, out ignored))
					throw new FormatException("\"" + pair.Value + "\" is not a keyboard shortcut.");
			}
			Data.Clear();
			foreach (var pair in list)
				Data.Set(pair.Key, pair.Value == null ? "" : pair.Value.Trim());
			SaveAndApply();
		}

		public void ResetAll()
		{
			if (Data.Overrides.Count == 0)
				return;
			Data.Clear();
			SaveAndApply();
		}

		/// <summary>Reads the file again (it was changed from outside) and applies it.</summary>
		public void Reload()
		{
			file = null;
			ApplyAll();
			Changed?.Invoke(this, EventArgs.Empty);
		}

		void SaveAndApply()
		{
			if (filePath != null)
				Data.Save(filePath);
			ApplyAll();
			Changed?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>
		/// Registers an item that runs a command: its shortcut (the override, else <paramref name="defaultGesture"/>) is bound on
		/// <paramref name="owner"/> and kept up to date from now on. Items without a shortcut register too, because an override can give them one.
		/// </summary>
		/// <param name="owner">The element that holds the binding; null only computes the text.</param>
		/// <param name="commandKey">See <see cref="CommandKey"/>.</param>
		/// <param name="siteIdentity">Identifies the item (its codon): registering the same one again replaces the earlier registration.</param>
		/// <param name="defaultGesture">The <c>shortcut</c> of the manifest, or null.</param>
		/// <param name="command">What the key runs.</param>
		/// <param name="featureName">When not empty, a shortcut use is reported for this feature.</param>
		/// <param name="displayTextChanged">Called with the display text of the gesture (empty for none) when an override changes it.</param>
		/// <returns>The display text of the gesture the item has now, empty for none.</returns>
		public string Register(UIElement owner, string commandKey, object siteIdentity, string defaultGesture,
		                       System.Windows.Input.ICommand command, string featureName, Action<string> displayTextChanged)
		{
			if (commandKey == null)
				throw new ArgumentNullException("commandKey");
			ShortcutGesture gesture = ResolveShortcut(commandKey, defaultGesture);
			string text = DisplayText(gesture);
			if (owner == null || command == null)
				return text;

			OwnerState state;
			if (!owners.TryGetValue(owner, out state)) {
				state = new OwnerState();
				owners.Add(owner, state);
				liveOwners.Add(new WeakReference<UIElement>(owner));
			}
			var site = new Site {
				Key = commandKey, Identity = siteIdentity ?? command, DefaultGesture = defaultGesture, Command = command,
				FeatureName = featureName, DisplayTextChanged = displayTextChanged, Gesture = gesture
			};
			int index = state.Sites.FindIndex(s => ReferenceEquals(s.Identity, site.Identity));
			bool hadGesture = false;
			if (index >= 0) {
				hadGesture = state.Sites[index].Gesture != null;
				state.Sites[index] = site;
			} else {
				state.Sites.Add(site);
			}
			// Most items have no shortcut: nothing to bind, nothing to rebuild
			if (gesture != null || hadGesture)
				Rebuild(owner, state);
			return text;
		}

		static string DisplayText(ShortcutGesture gesture)
		{
			return gesture == null ? "" : gesture.ToDisplayText();
		}

		void ApplyAll()
		{
			foreach (UIElement owner in LiveOwners()) {
				OwnerState state;
				if (!owners.TryGetValue(owner, out state))
					continue;
				foreach (Site site in state.Sites) {
					site.Gesture = ResolveShortcut(site.Key, site.DefaultGesture);
					if (site.DisplayTextChanged != null)
						site.DisplayTextChanged(DisplayText(site.Gesture));
				}
				Rebuild(owner, state);
			}
		}

		List<UIElement> LiveOwners()
		{
			var result = new List<UIElement>();
			liveOwners.RemoveAll(reference => {
				UIElement owner;
				if (!reference.TryGetTarget(out owner))
					return true;
				result.Add(owner);
				return false;
			});
			return result;
		}

		/// <summary>Removes the bindings this service added to the owner and adds the ones its sites call for: input bindings for single strokes, the owner's chord router for chords.</summary>
		static void Rebuild(UIElement owner, OwnerState state)
		{
			foreach (InputBinding binding in state.Added)
				owner.InputBindings.Remove(binding);
			state.Added.Clear();
			state.Chords.Clear();

			var groups = new List<KeyValuePair<KeyGesture, List<Site>>>();
			foreach (Site site in state.Sites) {
				if (site.Gesture == null)
					continue;
				if (site.Gesture.IsChord) {
					state.Chords.Add(site.Gesture, Wrap(site));
					continue;
				}
				KeyGesture stroke = site.Gesture.First;
				var group = groups.FirstOrDefault(g => g.Key.Key == stroke.Key && g.Key.Modifiers == stroke.Modifiers);
				if (group.Value == null) {
					group = new KeyValuePair<KeyGesture, List<Site>>(stroke, new List<Site>());
					groups.Add(group);
				}
				group.Value.Add(site);
			}
			foreach (var group in groups) {
				System.Windows.Input.ICommand combined = null;
				foreach (Site site in group.Value) {
					System.Windows.Input.ICommand command = Wrap(site);
					combined = combined == null ? command : new AmbiguousCommandWrapper(combined, command);
				}
				var binding = new InputBinding(combined, group.Key);
				owner.InputBindings.Add(binding);
				state.Added.Add(binding);
			}
			if (state.Chords.HasChords && !state.ChordHandlerAttached) {
				state.ChordHandlerAttached = true;
				ChordRouter router = state.Chords;
				// Preview, so the first stroke is taken before a control inside the owner (an editor) sees it
				owner.PreviewKeyDown += (sender, e) => OnPreviewKeyDown(router, e);
			}
		}

		/// <summary>Reports a shortcut use for the feature, as it was before, then runs the site's command.</summary>
		static System.Windows.Input.ICommand Wrap(Site site)
		{
			System.Windows.Input.ICommand command = site.Command;
			if (!string.IsNullOrEmpty(site.FeatureName))
				command = new ShortcutCommandWrapper(command, site.FeatureName);
			return command;
		}

		static void OnPreviewKeyDown(ChordRouter router, System.Windows.Input.KeyEventArgs e)
		{
			if (e.Handled)
				return;
			System.Windows.Input.Key key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
			System.Windows.Input.ICommand command;
			switch (router.Press(key, System.Windows.Input.Keyboard.Modifiers, DateTime.UtcNow, out command)) {
				case ChordOutcome.Started:
					e.Handled = true;
					break;
				case ChordOutcome.Completed:
					if (command.CanExecute(null))
						command.Execute(null);
					e.Handled = true;
					break;
			}
		}

		/// <summary>
		/// Runs a key chord on an owner as its key presses would (the first stroke, then the second): for tests and DevFlow, because a real key press needs the
		/// key window. Returns whether the first stroke was taken and whether the second completed a chord whose command ran.
		/// </summary>
		public bool TryRunChord(UIElement owner, string firstStroke, string secondStroke, out bool executed)
		{
			executed = false;
			OwnerState state;
			ShortcutGesture chord;
			if (owner == null || !owners.TryGetValue(owner, out state) || !ShortcutGesture.TryParse(firstStroke + ", " + secondStroke, out chord) || !chord.IsChord)
				return false;
			System.Windows.Input.ICommand command;
			if (state.Chords.Press(chord.First.Key, chord.First.Modifiers, DateTime.UtcNow, out command) != ChordOutcome.Started)
				return false;
			if (state.Chords.Press(chord.Second.Key, chord.Second.Modifiers, DateTime.UtcNow, out command) != ChordOutcome.Completed)
				return true;
			if (command.CanExecute(null)) {
				command.Execute(null);
				executed = true;
			}
			return true;
		}
	}
}
