using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// CommandCatalog: the commands of the AddIn tree, found from the codons, and the command-wide default shortcut an item without a
/// <c>shortcut</c> of its own takes from another item of the same command. doc/technotes/keyboard-shortcuts.md.
/// </summary>
public sealed class CommandCatalogTests
{
	const string Manifest = @"
<AddIn name='Catalog.Test'>
  <Manifest><Identity name='Catalog.Test'/></Manifest>
  <Path name='/Test/MainMenu'>
    <MenuItem id='Build' label='_Build' class='Foo.BuildCommand' shortcut='Control|F8'/>
    <MenuItem id='Clean' label='&amp;Clean' class='Foo.CleanCommand'/>
    <MenuItem id='Undo' label='_Undo' command='Undo'/>
    <MenuItem id='Help' label='Topics' link='http://example.com' shortcut='Alt|F1'/>
    <MenuItem id='Sub' label='Sub' type='Menu'><MenuItem id='X' label='X' class='Foo.InSubmenu'/></MenuItem>
    <MenuItem id='Dyn' type='Builder' class='Foo.ABuilder'/>
    <Separator id='Sep'/>
  </Path>
  <Path name='/SharpDevelop/Workbench/MainMenu'>
    <MenuItem id='Build' label='&amp;Build' type='Menu'/>
    <MenuItem id='Edit' label='_Edit' type='Menu'/>
    <Condition name='Ownership' owner='nobody'><MenuItem id='Xml' label='_XML' type='Menu'/></Condition>
  </Path>
  <Path name='/Test/Toolbar'>
    <ToolbarItem id='BuildButton' label='Build' class='Foo.BuildCommand'/>
    <ToolbarItem id='Combo' type='Custom' class='Foo.CustomControl'/>
  </Path>
  <Path name='/Test/Pads'>
    <Pad id='Output' label='Output' class='Foo.OutputPad' shortcut='Control|Alt|O'/>
  </Path>
</AddIn>";

	static AddIn Load(bool enabled = true, bool suspended = false)
	{
		var addIn = AddIn.Load(new AddInTreeImpl(null), new StringReader(Manifest));
		addIn.Enabled = enabled;
		addIn.Suspended = suspended;
		return addIn;
	}

	static CommandCatalogEntry Entry(IReadOnlyList<CommandCatalogEntry> catalog, string key) => catalog.Single(e => e.Key == key);

	[Fact]
	public void TheCatalog_GroupsItemsByCommand_AndSkipsWhatIsNotACommand()
	{
		var catalog = CommandCatalog.Build(new[] { Load() });

		Assert.Equal(
			new[] { "class:Foo.BuildCommand", "class:Foo.CleanCommand", "class:Foo.InSubmenu", "pad:Output", "routed:Undo" }.OrderBy(k => k),
			catalog.Select(e => e.Key).Where(k => !k.StartsWith("item:")).OrderBy(k => k));
		Assert.Equal(2, Entry(catalog, "class:Foo.BuildCommand").Items.Count);
		// no Builder, no Custom control, no separator, no plain Menu
		Assert.DoesNotContain(catalog, e => e.Key.Contains("ABuilder") || e.Key.Contains("CustomControl"));
	}

	[Fact]
	public void AnEntry_HasANameWithoutMnemonics_AndTheManifestShortcutOfAnyItem()
	{
		var catalog = CommandCatalog.Build(new[] { Load() });

		var build = Entry(catalog, "class:Foo.BuildCommand");
		Assert.Equal("Build", build.DisplayName);
		Assert.Equal("Control|F8", build.DefaultGesture);
		Assert.Equal("Clean", Entry(catalog, "class:Foo.CleanCommand").DisplayName);
		Assert.Null(Entry(catalog, "class:Foo.CleanCommand").DefaultGesture);
		Assert.True(Entry(catalog, "routed:Undo").IsRouted);
		Assert.False(build.IsRouted);
	}

	[Fact]
	public void ALinkItem_HasAnItemKey_AndAPad_APadKey()
	{
		var catalog = CommandCatalog.Build(new[] { Load() });
		Assert.Equal("Alt|F1", Entry(catalog, "item:Catalog.Test/Help").DefaultGesture);
		Assert.Equal("Control|Alt|O", Entry(catalog, "pad:Output").DefaultGesture);
	}

	[Fact]
	public void ADisabledOrSuspendedAddIn_ContributesNothing()
	{
		Assert.Empty(CommandCatalog.Build(new[] { Load(enabled: false) }));
		Assert.Empty(CommandCatalog.Build(new[] { Load(suspended: true) }));
	}

	[Fact]
	public void AnItemWithoutAShortcut_TakesTheDefaultOfItsCommand()
	{
		var directory = Path.Combine(Path.GetTempPath(), "od-catalog-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			var service = new KeyBindingService(Path.Combine(directory, "KeyBindings.xml"), _ => { });
			var defaults = CommandCatalog.DefaultGestures(new[] { Load() });
			service.CommandDefaultProvider = key => defaults.TryGetValue(key, out var text) ? text : null;
			var toolbar = new Border();
			var menu = new Border();
			var command = new CountingCommand();

			// the toolbar button has no shortcut attribute; the menu item of the same command carries Control|F8
			service.Register(toolbar, "class:Foo.BuildCommand", "button", null, command, null, _ => { });
			service.Register(menu, "class:Foo.BuildCommand", "item", "Control|F8", command, null, _ => { });

			Assert.Equal(Key.F8, Assert.IsType<KeyGesture>(Assert.Single(toolbar.InputBindings.OfType<InputBinding>()).Gesture).Key);
			Assert.Equal(Key.F8, Assert.IsType<KeyGesture>(Assert.Single(menu.InputBindings.OfType<InputBinding>()).Gesture).Key);

			// an explicit "none" override still wins over the command-wide default
			service.SetOverride("class:Foo.BuildCommand", "");
			Assert.Empty(toolbar.InputBindings);
			Assert.Equal("", service.ResolveText("class:Foo.BuildCommand", null) ?? "");
		} finally {
			Directory.Delete(directory, true);
		}
	}

	[Fact]
	public void TheMainMenuLabels_NameTheTopLevelMenusWithoutMnemonics()
	{
		var labels = CommandCatalog.MainMenuLabels(new[] { Load() });
		Assert.Equal("Build", labels["Build"]);
		Assert.Equal("Edit", labels["Edit"]);
		Assert.Equal("XML", labels["Xml"]); // a menu declared inside a Condition is a codon of the same path
		Assert.Empty(CommandCatalog.MainMenuLabels(new[] { Load(suspended: true) }));
	}

	sealed class CountingCommand : ICommand
	{
		public event EventHandler? CanExecuteChanged { add { } remove { } }
		public bool CanExecute(object? parameter) => true;
		public void Execute(object? parameter) { }
	}

}
