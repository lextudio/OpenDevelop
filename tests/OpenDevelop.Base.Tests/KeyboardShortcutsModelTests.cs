using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// KeyboardShortcutsModel: the working copy behind the Keyboard page (rows, validation, conflicts per scope, reassign, reset, commit).
/// doc/technotes/keyboard-shortcuts.md.
/// </summary>
public sealed class KeyboardShortcutsModelTests : IDisposable
{
	const string Manifest = @"
<AddIn name='Model.Test'>
  <Manifest><Identity name='Model.Test'/></Manifest>
  <Path name='/SharpDevelop/Workbench/MainMenu/Build'>
    <MenuItem id='Build' label='_Build' class='Foo.Build' shortcut='Control|F8'/>
    <MenuItem id='Rebuild' label='_Rebuild' class='Foo.Rebuild' shortcut='Alt|F8'/>
    <MenuItem id='Clean' label='_Clean' class='Foo.Clean'/>
  </Path>
  <Path name='/SharpDevelop/Workbench/MainMenu/Edit'>
    <MenuItem id='Undo' label='_Undo' command='Undo'/>
    <MenuItem id='Print' label='_Print' command='Print' shortcut='Control|P'/>
  </Path>
  <Path name='/SharpDevelop/Pads/ProjectBrowser/ContextMenu/Common/Edit'>
    <MenuItem id='Rename' label='Rename' class='Foo.Rename' shortcut='F2'/>
    <MenuItem id='Open' label='Open' class='Foo.OpenNode' shortcut='Control|F8'/>
  </Path>
  <Path name='/SharpDevelop/Pads/ProjectBrowser/ContextMenu/FileNode'>
    <MenuItem id='Rename' label='Rename' class='Foo.Rename' shortcut='F2'/>
  </Path>
  <Path name='/SharpDevelop/ViewContent/TextEditor/ContextMenu'>
    <MenuItem id='Fold' label='Fold' class='Foo.Fold'/>
  </Path>
</AddIn>";

	readonly string directory = Path.Combine(Path.GetTempPath(), "od-shortcut-model-" + Guid.NewGuid().ToString("N"));
	public KeyboardShortcutsModelTests() => Directory.CreateDirectory(directory);
	public void Dispose() => Directory.Delete(directory, true);

	static IReadOnlyList<CommandCatalogEntry> Catalog()
	{
		var addIn = AddIn.Load(new AddInTreeImpl(null), new StringReader(Manifest));
		addIn.Enabled = true;
		return CommandCatalog.Build(new[] { addIn });
	}

	static KeyboardShortcutsModel Model(Dictionary<string, string>? overrides = null) =>
		new KeyboardShortcutsModel(Catalog(), overrides ?? new Dictionary<string, string>());

	[Fact]
	public void Rows_CarryTheEffectiveShortcut_TheScope_AndTheGroup()
	{
		var model = Model(new() { ["class:Foo.Clean"] = "Control+Shift+F9", ["class:Foo.Rebuild"] = "" });

		Assert.Equal("Ctrl+F8", model.Find("class:Foo.Build")!.Gesture);
		Assert.Equal("Ctrl+Shift+F9", model.Find("class:Foo.Clean")!.Gesture);
		Assert.True(model.Find("class:Foo.Clean")!.IsCustomized);
		Assert.Null(model.Find("class:Foo.Rebuild")!.Gesture);
		Assert.Equal("Alt+F8", model.Find("class:Foo.Rebuild")!.DefaultGesture);
		Assert.Equal("Build", model.Find("class:Foo.Build")!.Group);
		Assert.Equal(KeyboardShortcutsModel.WorkbenchScope, model.Find("class:Foo.Build")!.Scope);
		Assert.Equal("/SharpDevelop/Pads/ProjectBrowser", model.Find("class:Foo.Rename")!.Scope);
		Assert.Equal("ProjectBrowser", model.Find("class:Foo.Rename")!.Group);
		Assert.Equal(2, model.Find("class:Foo.Rename")!.Locations.Count);
	}

	[Fact]
	public void AGestureAnotherCommandHasInTheSameScope_IsAConflict_UntilReassigned()
	{
		var model = Model();

		var refused = model.Assign("class:Foo.Clean", "Ctrl+F8");
		Assert.True(refused.IsValid);
		Assert.Equal("class:Foo.Build", Assert.Single(refused.Conflicts).Key);
		Assert.Null(model.Find("class:Foo.Clean")!.Gesture); // nothing changed

		model.Assign("class:Foo.Clean", "Control|F8", reassign: true);
		Assert.Equal("Ctrl+F8", model.Find("class:Foo.Clean")!.Gesture);
		Assert.Null(model.Find("class:Foo.Build")!.Gesture); // gave it up: an explicit none
		Assert.Equal("", model.BuildOverrides()["class:Foo.Build"]);
		Assert.Equal("Ctrl+F8", model.BuildOverrides()["class:Foo.Clean"]);
	}

	[Fact]
	public void AWorkbenchGestureOnAControlCommand_IsShadowing_NotAConflict()
	{
		var model = Model();
		// Open (project browser) has Ctrl+F8 as Build (workbench) does
		var check = model.Check("class:Foo.OpenNode", "Ctrl+F8");
		Assert.Empty(check.Conflicts);
		Assert.Equal("class:Foo.Build", Assert.Single(check.Shadows).Key);
		// a command in another control scope is not shadowing either
		Assert.Empty(model.Check("class:Foo.Fold", "F2").Shadows);
		Assert.Empty(model.Check("class:Foo.Fold", "F2").Conflicts);
	}

	[Fact]
	public void ACommandInSeveralMenus_IsOneRow_AndItsOwnGestureIsNoConflict()
	{
		var model = Model();
		Assert.Single(model.Rows, r => r.Key == "class:Foo.Rename");
		Assert.Empty(model.Check("class:Foo.Rename", "F2").Conflicts);
	}

	[Fact]
	public void NonsenseTheSystemKeysAndRoutedCommands_AreRefused()
	{
		var model = Model();
		Assert.False(model.Check("class:Foo.Clean", "Banana").IsValid);
		Assert.False(model.Check("class:Foo.Clean", "Alt+F4").IsValid);
		Assert.False(model.Check("class:Foo.Clean", "A").IsValid); // a bare letter types text
		Assert.False(model.Check("routed:Undo", "Ctrl+Z").IsValid);
		Assert.False(model.Check("class:Nobody", "F7").IsValid);
		Assert.True(model.Check("class:Foo.Clean", "").IsValid);
	}

	[Fact]
	public void RemoveAndReset_WorkOnTheCopy_AndTheModelKnowsWhetherItIsDirty()
	{
		var model = Model();
		Assert.False(model.IsDirty);

		model.Assign("class:Foo.Build", "");
		Assert.True(model.IsDirty);
		Assert.Equal("", model.BuildOverrides()["class:Foo.Build"]);

		model.Reset("class:Foo.Build");
		Assert.False(model.IsDirty);
		Assert.Empty(model.BuildOverrides());

		model.Assign("class:Foo.Clean", "Ctrl+Shift+F9");
		model.ResetAll();
		Assert.False(model.IsDirty);
	}

	[Fact]
	public void Commit_WritesTheWorkingCopyOnce_AndKeepsWhatTheCatalogDoesNotKnow()
	{
		string file = Path.Combine(directory, "KeyBindings.xml");
		var service = new KeyBindingService(file, _ => { });
		service.SetOverride("class:Some.Unloaded.Command", "Ctrl+Alt+Q");
		var model = new KeyboardShortcutsModel(Catalog(), service.Overrides);

		model.Assign("class:Foo.Clean", "Ctrl+Shift+F9");
		model.Assign("class:Foo.Rebuild", "");
		int changes = 0;
		service.Changed += (_, _) => changes++;
		model.Commit(service);

		Assert.Equal(1, changes);
		Assert.Equal("Ctrl+Shift+F9", service.Overrides["class:Foo.Clean"]);
		Assert.Equal("", service.Overrides["class:Foo.Rebuild"]);
		Assert.Equal("Ctrl+Alt+Q", service.Overrides["class:Some.Unloaded.Command"]);
		Assert.Equal(3, service.Overrides.Count);
		// and it is on disk
		Assert.Equal(3, new KeyBindingService(file, _ => { }).Overrides.Count);
	}

	[Fact]
	public void ReplaceOverrides_ChangesNothing_WhenOneGestureIsNotValid()
	{
		var service = new KeyBindingService(Path.Combine(directory, "KeyBindings.xml"), _ => { });
		service.SetOverride("class:A", "F5");

		Assert.Throws<FormatException>(() => service.ReplaceOverrides(new Dictionary<string, string> { ["class:B"] = "F6", ["class:C"] = "Banana" }));

		Assert.Equal("F5", Assert.Single(service.Overrides).Value);
	}

	[Fact]
	public void AGestureOfABuiltInCommand_CannotBeTaken_EvenWithReassign()
	{
		var model = Model();
		var check = model.Assign("class:Foo.Clean", "Ctrl+P", reassign: true);
		Assert.False(check.IsValid);
		Assert.Null(model.Find("class:Foo.Clean")!.Gesture);
		Assert.Equal("Ctrl+P", model.Find("routed:Print")!.Gesture);
	}

	[Fact]
	public void AnUnreadableEntryInTheFile_DoesNotStopTheCommit_AndIsKept()
	{
		string file = Path.Combine(directory, "KeyBindings.xml");
		File.WriteAllText(file, "<KeyBindings version=\"1\"><Command key=\"class:Foo.Clean\" gesture=\"Banana\"/><Command key=\"class:Gone\" gesture=\"Nope\"/></KeyBindings>");
		var service = new KeyBindingService(file, _ => { });
		var model = new KeyboardShortcutsModel(Catalog(), service.Overrides);
		Assert.Null(model.Find("class:Foo.Clean")!.Gesture); // ignored: it has no default

		model.Assign("class:Foo.Rebuild", "Ctrl+Shift+F9");
		model.Commit(service);

		Assert.Equal("Banana", service.Overrides["class:Foo.Clean"]);
		Assert.Equal("Nope", service.Overrides["class:Gone"]);
		Assert.Equal("Ctrl+Shift+F9", service.Overrides["class:Foo.Rebuild"]);
	}

	[Fact]
	public void TheCaptureBox_ShowsAChordOnlyOnceAKeyBesidesModifiersIsDown()
	{
		Assert.Equal("Ctrl+Shift+F8", KeyboardShortcutsModel.FormatChord(System.Windows.Input.Key.F8, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift));
		Assert.Equal("F5", KeyboardShortcutsModel.FormatChord(System.Windows.Input.Key.F5, System.Windows.Input.ModifierKeys.None));
		Assert.Null(KeyboardShortcutsModel.FormatChord(System.Windows.Input.Key.LeftCtrl, System.Windows.Input.ModifierKeys.Control));
		Assert.Null(KeyboardShortcutsModel.FormatChord(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.None)); // a bare letter types text
	}

	[Fact]
	public void ConflictsOf_FindsTwoCommandsThatAlreadyShareAGestureInOneScope()
	{
		var model = Model(new() { ["class:Foo.Clean"] = "Alt+F8" }); // Rebuild has Alt+F8 by default
		Assert.Equal("class:Foo.Rebuild", Assert.Single(model.ConflictsOf(model.Find("class:Foo.Clean")!)).Key);
		Assert.Empty(model.ConflictsOf(model.Find("class:Foo.Build")!));
	}

	[Fact]
	public void AMainMenuGroup_IsNamedByTheMenusLabel_WhenTheModelKnowsIt()
	{
		var model = new KeyboardShortcutsModel(Catalog(), new Dictionary<string, string>(), new Dictionary<string, string> { ["Build"] = "Generate" });
		Assert.Equal("Build", model.Find("class:Foo.Build")!.Group);
		Assert.Equal("Generate", model.Find("class:Foo.Build")!.GroupName);
		Assert.Equal("ProjectBrowser", model.Find("class:Foo.Rename")!.GroupName); // not a main menu: the name stays
		Assert.Equal("Build", Model().Find("class:Foo.Build")!.GroupName); // no labels: the id
	}

	[Fact]
	public void TheShortcutSearch_MatchesTheSameGestureWhateverItsSpelling()
	{
		Assert.True(KeyboardShortcutsModel.SameGesture("Control|F8", "Ctrl+F8"));
		Assert.True(KeyboardShortcutsModel.SameGesture("ctrl+f8", "Ctrl+F8"));
		Assert.False(KeyboardShortcutsModel.SameGesture("Ctrl+F8", "Ctrl+Shift+F8"));
		Assert.False(KeyboardShortcutsModel.SameGesture("Banana", "Banana"));
		Assert.False(KeyboardShortcutsModel.SameGesture("Ctrl+F8", null));
		// the chain of the search box: a key chord -> its text -> the rows that have exactly it
		var model = Model();
		string chord = KeyboardShortcutsModel.FormatChord(System.Windows.Input.Key.F8, System.Windows.Input.ModifierKeys.Control)!;
		Assert.Equal("class:Foo.Build", Assert.Single(model.Rows, r => KeyboardShortcutsModel.SameGesture(chord, r.Gesture) && r.Scope == KeyboardShortcutsModel.WorkbenchScope).Key);
	}

	[Theory]
	[InlineData(System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.None, CaptureKeyAction.PassThrough)]
	[InlineData(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None, CaptureKeyAction.PassThrough)]
	[InlineData(System.Windows.Input.Key.Back, System.Windows.Input.ModifierKeys.None, CaptureKeyAction.Clear)]
	[InlineData(System.Windows.Input.Key.Delete, System.Windows.Input.ModifierKeys.None, CaptureKeyAction.Clear)]
	[InlineData(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.Control, CaptureKeyAction.Capture)]
	[InlineData(System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.Shift, CaptureKeyAction.Capture)]
	[InlineData(System.Windows.Input.Key.Delete, System.Windows.Input.ModifierKeys.Alt, CaptureKeyAction.Capture)]
	[InlineData(System.Windows.Input.Key.F8, System.Windows.Input.ModifierKeys.None, CaptureKeyAction.Capture)]
	public void TheCaptureBox_LeavesTabAndEscAlone_ClearsOnBackspace_AndCapturesTheRest(
		System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers, CaptureKeyAction expected)
	{
		Assert.Equal(expected, KeyboardShortcutsModel.ClassifyCaptureKey(key, modifiers));
		// Esc with a modifier is a real chord
		if (key == System.Windows.Input.Key.Escape && modifiers != System.Windows.Input.ModifierKeys.None)
			Assert.Equal("Ctrl+Esc", KeyboardShortcutsModel.FormatChord(key, modifiers));
	}

	[Fact]
	public void OverridesOfCommandsThatAreNotLoaded_AreListed_AndCanBeRemovedOrKept()
	{
		var model = Model(new() { ["class:Gone.One"] = "Ctrl+Alt+Q", ["class:Gone.Two"] = "", ["class:Foo.Clean"] = "Ctrl+Shift+F9" });

		Assert.Equal(new[] { "class:Gone.One", "class:Gone.Two" }, model.UnknownOverrides.Select(u => u.Key));
		Assert.Equal("Ctrl+Alt+Q", model.UnknownOverrides[0].Gesture);
		Assert.False(model.IsDirty);
		Assert.Contains("class:Gone.One", model.BuildOverrides().Keys); // kept unless removed

		model.RemoveUnknown("class:Gone.One");
		model.RemoveUnknown("class:Foo.Clean"); // a known command is not an unknown override: ignored here
		Assert.Equal("class:Gone.Two", Assert.Single(model.UnknownOverrides).Key);
		Assert.True(model.IsDirty);
		Assert.DoesNotContain("class:Gone.One", model.BuildOverrides().Keys);
		Assert.Equal("Ctrl+Shift+F9", model.BuildOverrides()["class:Foo.Clean"]);

		model.ResetAll(); // everything goes back to the manifests, the leftovers included
		Assert.Empty(model.UnknownOverrides);
		Assert.Empty(model.BuildOverrides());
	}

	[Fact]
	public void RemovedUnknownOverrides_AreNotWrittenBack_ByCommit()
	{
		string file = Path.Combine(directory, "KeyBindings.xml");
		var service = new KeyBindingService(file, _ => { });
		service.SetOverride("class:Gone.One", "Ctrl+Alt+Q");
		service.SetOverride("class:Gone.Two", "F11");
		var model = new KeyboardShortcutsModel(Catalog(), service.Overrides);

		model.RemoveUnknown("class:Gone.One");
		model.Commit(service);

		Assert.Equal("class:Gone.Two", Assert.Single(service.Overrides).Key);
		Assert.Equal("class:Gone.Two", Assert.Single(new KeyBindingService(file, _ => { }).Overrides).Key);
	}

	[Fact]
	public void Import_ShowsWhatWouldChange_AndAppliesItOnlyWhenAsked()
	{
		var model = Model();
		var preview = model.PreviewImport(new Dictionary<string, string> {
			["class:Foo.Clean"] = "Ctrl+Shift+F9",   // a new shortcut
			["class:Foo.Rebuild"] = "",              // removed
			["class:Foo.Build"] = "Control|F8",      // the same as it is: no change
			["class:Foo.Fold"] = "Banana",           // not a gesture
			["class:Gone.One"] = "F11",              // a command that is not here
			["routed:Print"] = "Ctrl+Q"              // built in
		});

		Assert.Equal(new[] { "class:Foo.Clean", "class:Foo.Rebuild" }, preview.Changes.Select(c => c.Row.Key));
		Assert.Equal("class:Foo.Fold", Assert.Single(preview.Invalid));
		Assert.Equal(2, preview.Skipped);
		Assert.Empty(preview.Conflicts);
		Assert.Null(model.Find("class:Foo.Clean")!.Gesture); // nothing changed yet

		model.ApplyImport(preview);
		Assert.Equal("Ctrl+Shift+F9", model.Find("class:Foo.Clean")!.Gesture);
		Assert.Null(model.Find("class:Foo.Rebuild")!.Gesture);
		Assert.Equal("Ctrl+F8", model.Find("class:Foo.Build")!.Gesture); // untouched: the file is merged
	}

	[Fact]
	public void Import_WarnsAboutConflictsItWouldCreate()
	{
		var model = Model();
		var preview = model.PreviewImport(new Dictionary<string, string> { ["class:Foo.Clean"] = "Ctrl+F8" }); // Build has it
		Assert.Equal(new[] { "class:Foo.Build", "class:Foo.Clean" }, preview.Conflicts.Select(r => r.Key).OrderBy(k => k));
	}

	[Fact]
	public void Export_WritesTheWorkingCopysOverrides_ThatAnotherCopyCanImport()
	{
		string file = Path.Combine(directory, "export.xml");
		var model = Model();
		model.Assign("class:Foo.Clean", "Ctrl+Shift+F9");
		model.Assign("class:Foo.Rebuild", "");
		model.Export(file);

		var read = KeyBindingsFile.Load(file);
		Assert.Equal("Ctrl+Shift+F9", read.Overrides["class:Foo.Clean"]);
		Assert.Equal("", read.Overrides["class:Foo.Rebuild"]);

		var other = Model();
		other.ApplyImport(other.PreviewImport(read.Overrides));
		Assert.Equal("Ctrl+Shift+F9", other.Find("class:Foo.Clean")!.Gesture);
		Assert.Null(other.Find("class:Foo.Rebuild")!.Gesture);
	}

	[Fact]
	public void Import_DoesNotCountAConflictThatIsAlreadyThere()
	{
		var model = Model(new() { ["class:Foo.Clean"] = "Alt+F8" }); // Clean and Rebuild already share Alt+F8
		var preview = model.PreviewImport(new Dictionary<string, string> { ["class:Foo.Clean"] = "Alt+F8", ["class:Foo.Rebuild"] = "Alt|F8" }); // the file only restates it
		Assert.Empty(preview.Changes);
		Assert.Empty(preview.Conflicts);
	}

	[Fact]
	public void Import_RefusesWhatTheAssignmentRefuses()
	{
		var model = Model();
		var preview = model.PreviewImport(new Dictionary<string, string> {
			["class:Foo.Clean"] = "Alt+F4",   // reserved by the system
			["class:Foo.Rebuild"] = "Ctrl+P",  // held by the built-in Print
			["class:Foo.Fold"] = "F7"          // fine
		});
		Assert.Equal("class:Foo.Fold", Assert.Single(preview.Changes).Row.Key);
		Assert.Equal(new[] { "class:Foo.Clean", "class:Foo.Rebuild" }, preview.Invalid.OrderBy(k => k));
	}
}
