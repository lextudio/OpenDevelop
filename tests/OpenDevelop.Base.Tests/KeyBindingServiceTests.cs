using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.Core.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// KeyBindingService: one gesture per command (the override, else the manifest's), installed on owners and kept up to date so that a change
/// applies at once. The owners are real WPF elements; the commands count their executions. doc/technotes/keyboard-shortcuts.md.
/// </summary>
public sealed class KeyBindingServiceTests : IDisposable
{
	readonly string directory = Path.Combine(Path.GetTempPath(), "od-keybinding-service-" + Guid.NewGuid().ToString("N"));
	readonly List<string> problems = new List<string>();
	string FilePath => Path.Combine(directory, "KeyBindings.xml");

	public KeyBindingServiceTests() => Directory.CreateDirectory(directory);
	public void Dispose() => Directory.Delete(directory, true);

	sealed class CountingCommand : ICommand
	{
		public int Executed;
		public bool Can = true;
		public event EventHandler? CanExecuteChanged { add { } remove { } }
		public bool CanExecute(object? parameter) => Can;
		public void Execute(object? parameter) => Executed++;
	}

	KeyBindingService NewService() => new KeyBindingService(FilePath, problems.Add);

	static List<InputBinding> Bindings(Border owner) => owner.InputBindings.OfType<InputBinding>().ToList();

	static string Text(KeyGesture gesture) => gesture.Key + "+" + gesture.Modifiers;

	[Fact]
	public void TheManifestShortcut_IsBound_AndItsDisplayTextReturned()
	{
		var service = NewService();
		var owner = new Border();
		var command = new CountingCommand();

		var text = service.Register(owner, "class:Foo.Build", "codon1", "F8", command, null, _ => { });

		Assert.Equal(MenuService.GetDisplayStringForShortcut(new KeyGesture(Key.F8)), text);
		var binding = Assert.Single(Bindings(owner));
		var gesture = Assert.IsType<KeyGesture>(binding.Gesture);
		Assert.Equal(Key.F8, gesture.Key);
		binding.Command.Execute(null);
		Assert.Equal(1, command.Executed);
	}

	[Fact]
	public void AnOverride_ReplacesTheGesture_AtOnce_AndTheDisplayTextFollows()
	{
		var service = NewService();
		var owner = new Border();
		var command = new CountingCommand();
		string shown = "";
		service.Register(owner, "class:Foo.Build", "codon1", "F8", command, null, t => shown = t);

		service.SetOverride("class:Foo.Build", "Ctrl+B");

		var binding = Assert.Single(Bindings(owner));
		var gesture = Assert.IsType<KeyGesture>(binding.Gesture);
		Assert.Equal(Key.B, gesture.Key);
		Assert.Equal(ModifierKeys.Control, gesture.Modifiers);
		Assert.Equal(MenuService.GetDisplayStringForShortcut(gesture), shown);
		binding.Command.Execute(null);
		Assert.Equal(1, command.Executed);
	}

	[Fact]
	public void AnOverrideToNone_RemovesTheBinding_AndClearsTheText_EvenWhenTheManifestHasAShortcut()
	{
		var service = NewService();
		var owner = new Border();
		string shown = "unset";
		service.Register(owner, "class:Foo.Save", "codon1", "Control|S", new CountingCommand(), null, t => shown = t);
		Assert.Single(Bindings(owner));

		service.SetOverride("class:Foo.Save", "");

		Assert.Empty(Bindings(owner));
		Assert.Equal("", shown);
	}

	[Fact]
	public void AnItemWithoutAShortcut_CanBeGivenOne()
	{
		var service = NewService();
		var owner = new Border();
		var command = new CountingCommand();
		var text = service.Register(owner, "class:Foo.Clean", "codon1", null, command, null, _ => { });
		Assert.Equal("", text);
		Assert.Empty(Bindings(owner));

		service.SetOverride("class:Foo.Clean", "Ctrl+Shift+F8");

		var binding = Assert.Single(Bindings(owner));
		binding.Command.Execute(null);
		Assert.Equal(1, command.Executed);
	}

	[Fact]
	public void TheOverrideAppliesToEverySiteOfTheCommand_OnEveryOwner()
	{
		var service = NewService();
		var mainWindow = new Border();
		var contextMenuOwner = new Border();
		var texts = new Dictionary<string, string>();
		service.Register(mainWindow, "class:Foo.Save", "main-menu", "Control|S", new CountingCommand(), null, t => texts["main-menu"] = t);
		service.Register(mainWindow, "class:Foo.Save", "toolbar", null, new CountingCommand(), null, t => texts["toolbar"] = t);
		service.Register(contextMenuOwner, "class:Foo.Save", "context", "Control|S", new CountingCommand(), null, t => texts["context"] = t);

		service.SetOverride("class:Foo.Save", "Ctrl+Alt+S");

		foreach (var owner in new[] { mainWindow, contextMenuOwner })
			Assert.All(Bindings(owner), b => Assert.Equal(Key.S, ((KeyGesture)b.Gesture).Key));
		Assert.All(Bindings(mainWindow), b => Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, ((KeyGesture)b.Gesture).Modifiers));
		// All three sites show the same new gesture, including the toolbar button that has no shortcut of its own
		var expected = MenuService.GetDisplayStringForShortcut(new KeyGesture(Key.S, ModifierKeys.Control | ModifierKeys.Alt));
		Assert.Equal(new[] { "context", "main-menu", "toolbar" }, texts.Keys.OrderBy(k => k));
		Assert.All(texts.Values, t => Assert.Equal(expected, t));
	}

	[Fact]
	public void ResetOverride_RestoresTheManifestShortcut_AndResetAllRestoresEverything()
	{
		var service = NewService();
		var owner = new Border();
		service.Register(owner, "class:A", "a", "F5", new CountingCommand(), null, _ => { });
		service.Register(owner, "class:B", "b", "F6", new CountingCommand(), null, _ => { });
		service.SetOverride("class:A", "F9");
		service.SetOverride("class:B", "");
		Assert.Equal(new[] { Key.F9 }, Bindings(owner).Select(b => ((KeyGesture)b.Gesture).Key));

		service.ResetOverride("class:A");
		Assert.Equal(new[] { Key.F5 }, Bindings(owner).Select(b => ((KeyGesture)b.Gesture).Key));
		service.ResetAll();
		Assert.Equal(new[] { Key.F5, Key.F6 }, Bindings(owner).Select(b => ((KeyGesture)b.Gesture).Key).OrderBy(k => k));
		Assert.Empty(service.Overrides);
	}

	[Fact]
	public void TheSameItemRegisteringAgain_ReplacesItsRegistration_SoNothingPilesUp()
	{
		var service = NewService();
		var owner = new Border();
		CountingCommand latest = null!;
		// A context menu registers its items every time it opens
		for (int i = 0; i < 25; i++) {
			latest = new CountingCommand();
			service.Register(owner, "class:Foo.Rename", "the-codon", "F2", latest, null, _ => { });
		}

		var binding = Assert.Single(Bindings(owner));
		binding.Command.Execute(null);
		Assert.Equal(1, latest.Executed);
	}

	[Fact]
	public void TwoItemsOnTheSameGesture_RunTheFirstThatCanExecute()
	{
		var service = NewService();
		var owner = new Border();
		var first = new CountingCommand { Can = false };
		var second = new CountingCommand();
		service.Register(owner, "class:First", "a", "F4", first, null, _ => { });
		service.Register(owner, "class:Second", "b", "F4", second, null, _ => { });

		var binding = Assert.Single(Bindings(owner));
		Assert.True(binding.Command.CanExecute(null));
		binding.Command.Execute(null);
		Assert.Equal(0, first.Executed);
		Assert.Equal(1, second.Executed);

		first.Can = true;
		binding.Command.Execute(null);
		Assert.Equal(1, first.Executed);
		Assert.Equal(1, second.Executed);
	}

	[Fact]
	public void BindingsThatOtherCodeAdded_AreLeftAlone()
	{
		var service = NewService();
		var owner = new Border();
		var foreign = new KeyBinding(new CountingCommand(), Key.Q, ModifierKeys.Control);
		owner.InputBindings.Add(foreign);
		service.Register(owner, "class:A", "a", "F5", new CountingCommand(), null, _ => { });

		service.SetOverride("class:A", "F6");
		service.SetOverride("class:A", "");

		Assert.Same(foreign, Assert.Single(Bindings(owner)));
	}

	[Fact]
	public void AnOverride_IsStillThereForTheNextService_AsAfterARestart()
	{
		NewService().SetOverride("class:Foo.Clean", "Ctrl+Shift+F8");

		var restarted = NewService();
		var owner = new Border();
		var text = restarted.Register(owner, "class:Foo.Clean", "c", null, new CountingCommand(), null, _ => { });

		var gesture = Assert.IsType<KeyGesture>(Assert.Single(Bindings(owner)).Gesture);
		Assert.Equal(Key.F8, gesture.Key);
		Assert.Equal(MenuService.GetDisplayStringForShortcut(gesture), text);
	}

	[Fact]
	public void ANonsenseOverrideIsRefused_AndOneInTheFileIsReportedAndIgnored()
	{
		var service = NewService();
		Assert.Throws<FormatException>(() => service.SetOverride("class:A", "Ctrl+NotAKey"));
		Assert.Empty(service.Overrides);
		Assert.False(File.Exists(FilePath));

		File.WriteAllText(FilePath, "<KeyBindings version='1'><Command key='class:B' gesture='Ctrl+NotAKey'/></KeyBindings>");
		var fromFile = NewService();
		var owner = new Border();
		fromFile.Register(owner, "class:B", "b", "F7", new CountingCommand(), null, _ => { });

		// The entry is ignored, so the manifest shortcut applies
		Assert.Equal(Key.F7, ((KeyGesture)Assert.Single(Bindings(owner)).Gesture).Key);
		Assert.Contains(problems, p => p.Contains("class:B") && p.Contains("not valid"));
	}

	[Fact]
	public void Changed_IsRaised_AndReloadPicksUpAnEditFromOutside()
	{
		var service = NewService();
		var owner = new Border();
		service.Register(owner, "class:A", "a", "F5", new CountingCommand(), null, _ => { });
		int raised = 0;
		service.Changed += (_, _) => raised++;

		service.SetOverride("class:A", "F6");
		Assert.Equal(1, raised);

		File.WriteAllText(FilePath, "<KeyBindings version='1'><Command key='class:A' gesture='F7'/></KeyBindings>");
		service.Reload();
		Assert.Equal(2, raised);
		Assert.Equal(Key.F7, ((KeyGesture)Assert.Single(Bindings(owner)).Gesture).Key);
	}

	[Fact]
	public void WithoutAnOwner_OnlyTheTextIsComputed()
	{
		var service = NewService();
		service.SetOverride("class:A", "Ctrl+K");

		var text = service.Register(null, "class:A", "a", "F5", new CountingCommand(), null, _ => { });

		Assert.Equal(MenuService.GetDisplayStringForShortcut(new KeyGesture(Key.K, ModifierKeys.Control)), text);
	}
}
