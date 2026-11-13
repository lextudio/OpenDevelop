using System.Windows.Input;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Designer.Surface;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>ShortcutMatcher and the design surface's shortcuts built on it (the terminal's are in TerminalShortcutsTests).</summary>
public sealed class ShortcutMatcherTests
{
	static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

	[Theory]
	[InlineData(Key.Z, ModifierKeys.Control, DesignerShortcut.Undo)]
	[InlineData(Key.Y, ModifierKeys.Control, DesignerShortcut.Redo)]
	[InlineData(Key.Z, ModifierKeys.Control | ModifierKeys.Shift, DesignerShortcut.Redo)]
	[InlineData(Key.Delete, ModifierKeys.None, DesignerShortcut.Delete)]
	[InlineData(Key.F2, ModifierKeys.None, DesignerShortcut.Rename)]
	public void TheDesignerDefaults_AreWhatTheSurfaceHasAlwaysDone(Key key, ModifierKeys modifiers, DesignerShortcut expected)
	{
		Assert.Equal(expected, DesignerShortcuts.Match(key, modifiers, None));
	}

	[Theory]
	[InlineData(Key.Z, ModifierKeys.None)]
	[InlineData(Key.Left, ModifierKeys.None)] // arrow nudging is not a configurable command
	[InlineData(Key.Tab, ModifierKeys.None)]
	public void AnythingElse_IsNotADesignerCommand(Key key, ModifierKeys modifiers)
	{
		Assert.Null(DesignerShortcuts.Match(key, modifiers, None));
	}

	[Fact]
	public void AnOverrideOfTheDesignerDelete_ReplacesTheDefault()
	{
		var overrides = new Dictionary<string, string> { ["designer:Delete"] = "Ctrl+Backspace" };
		Assert.Equal(DesignerShortcut.Delete, DesignerShortcuts.Match(Key.Back, ModifierKeys.Control, overrides));
		Assert.Null(DesignerShortcuts.Match(Key.Delete, ModifierKeys.None, overrides));
		Assert.Equal(DesignerShortcut.Rename, DesignerShortcuts.Match(Key.F2, ModifierKeys.None, overrides));
	}

	[Fact]
	public void TheDesignerCommands_HaveADesignerScopeAndGroup()
	{
		var commands = DesignerShortcuts.Describe().ToList();
		Assert.Equal(new[] { "designer:Undo", "designer:Redo", "designer:Delete", "designer:Rename" }, commands.Select(c => c.Key));
		Assert.All(commands, c => Assert.Equal("Designer", c.Scope));
	}

	[Fact]
	public void TryMatch_PrefersAnExplicitOverride_ToAnotherCommandsDefault_AndTreatsNoneAndNonsenseAsNoMatch()
	{
		var commands = new[] { "a", "b" };
		bool Try(IReadOnlyDictionary<string, string> o, out string match) =>
			ShortcutMatcher.TryMatch(commands, c => "k:" + c, c => c == "a", Key.F5, ModifierKeys.None, o, out match!);

		Assert.True(Try(None, out var m0)); Assert.Equal("a", m0); // b has no override, a matches its default
		Assert.True(Try(new Dictionary<string, string> { ["k:b"] = "F5" }, out var m1)); Assert.Equal("b", m1); // the override wins over a's default
		Assert.False(Try(new Dictionary<string, string> { ["k:a"] = "" }, out _)); // a was set to none: it no longer matches by default
		Assert.False(Try(new Dictionary<string, string> { ["k:a"] = "Banana" }, out _)); // nonsense matches nothing
	}
}
