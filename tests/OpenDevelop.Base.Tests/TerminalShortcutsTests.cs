using System.Windows.Input;
using OpenDevelop.Terminal;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>TerminalShortcuts: which terminal command a key press is, by default and with the user's overrides from the Keyboard page.</summary>
public sealed class TerminalShortcutsTests
{
	static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

	[Theory]
	[InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Shift, TerminalShortcut.Copy)]
	[InlineData(Key.V, ModifierKeys.Control | ModifierKeys.Shift, TerminalShortcut.Paste)]
	[InlineData(Key.F, ModifierKeys.Control | ModifierKeys.Shift, TerminalShortcut.Find)]
	[InlineData(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift, TerminalShortcut.ZoomIn)]
	[InlineData(Key.Subtract, ModifierKeys.Control | ModifierKeys.Shift, TerminalShortcut.ZoomOut)]
	[InlineData(Key.NumPad0, ModifierKeys.Control | ModifierKeys.Shift, TerminalShortcut.ZoomReset)]
	[InlineData(Key.C, ModifierKeys.Windows, TerminalShortcut.Copy)]
	[InlineData(Key.V, ModifierKeys.Windows | ModifierKeys.Shift, TerminalShortcut.Paste)]
	[InlineData(Key.PageUp, ModifierKeys.Shift, TerminalShortcut.ScrollPageUp)]
	[InlineData(Key.PageDown, ModifierKeys.Shift, TerminalShortcut.ScrollPageDown)]
	[InlineData(Key.Home, ModifierKeys.Shift, TerminalShortcut.ScrollToTop)]
	[InlineData(Key.End, ModifierKeys.Shift, TerminalShortcut.ScrollToBottom)]
	public void TheDefaults_AreWhatTheTerminalHasAlwaysDone(Key key, ModifierKeys modifiers, TerminalShortcut expected)
	{
		Assert.Equal(expected, TerminalShortcuts.Match(key, modifiers, isMac: true, None));
	}

	[Theory]
	[InlineData(Key.C, ModifierKeys.Control)] // Ctrl+C is the shell's interrupt
	[InlineData(Key.C, ModifierKeys.None)]
	[InlineData(Key.PageUp, ModifierKeys.None)]
	[InlineData(Key.A, ModifierKeys.Control | ModifierKeys.Shift)]
	public void AnythingElse_IsNotATerminalCommand_AndGoesToTheShell(Key key, ModifierKeys modifiers)
	{
		Assert.Null(TerminalShortcuts.Match(key, modifiers, isMac: false, None));
	}

	[Fact]
	public void SelectAllAndClear_ExistOnMacOnly()
	{
		Assert.Equal(TerminalShortcut.SelectAll, TerminalShortcuts.Match(Key.A, ModifierKeys.Windows, isMac: true, None));
		Assert.Equal(TerminalShortcut.Clear, TerminalShortcuts.Match(Key.K, ModifierKeys.Windows, isMac: true, None));
		Assert.Null(TerminalShortcuts.Match(Key.A, ModifierKeys.Windows, isMac: false, None));
		Assert.DoesNotContain(TerminalShortcuts.Describe(isMac: false), c => c.Key == "terminal:SelectAll" || c.Key == "terminal:Clear");
		Assert.Contains(TerminalShortcuts.Describe(isMac: true), c => c.Key == "terminal:Clear");
	}

	[Fact]
	public void AnOverride_ReplacesTheDefault_OfThatCommandOnly()
	{
		var overrides = new Dictionary<string, string> { ["terminal:Copy"] = "Ctrl+Alt+C" };

		Assert.Equal(TerminalShortcut.Copy, TerminalShortcuts.Match(Key.C, ModifierKeys.Control | ModifierKeys.Alt, isMac: false, overrides));
		Assert.Null(TerminalShortcuts.Match(Key.C, ModifierKeys.Control | ModifierKeys.Shift, isMac: false, overrides)); // the old key is free: the shell gets it
		Assert.Equal(TerminalShortcut.Paste, TerminalShortcuts.Match(Key.V, ModifierKeys.Control | ModifierKeys.Shift, isMac: false, overrides)); // others keep theirs
	}

	[Fact]
	public void NoneRemovesTheShortcut()
	{
		var overrides = new Dictionary<string, string> { ["terminal:Find"] = "" };
		Assert.Null(TerminalShortcuts.Match(Key.F, ModifierKeys.Control | ModifierKeys.Shift, isMac: false, overrides));
	}

	[Fact]
	public void AGestureGivenToAnotherCommand_WinsOverTheDefaultOfTheFirst()
	{
		// the user put Zoom In on Ctrl+Shift+C (the page warns of the conflict); the explicit override is matched before any default
		var overrides = new Dictionary<string, string> { ["terminal:ZoomIn"] = "Ctrl+Shift+C" };
		Assert.Equal(TerminalShortcut.ZoomIn, TerminalShortcuts.Match(Key.C, ModifierKeys.Control | ModifierKeys.Shift, isMac: false, overrides));
	}

	[Fact]
	public void ANonsenseOverride_MatchesNothing_ButDoesNotRestoreTheDefault()
	{
		var overrides = new Dictionary<string, string> { ["terminal:Paste"] = "Banana" };
		Assert.Null(TerminalShortcuts.Match(Key.V, ModifierKeys.Control | ModifierKeys.Shift, isMac: false, overrides));
	}

	[Fact]
	public void TheCommandsHaveATerminalScopeAndGroup()
	{
		var commands = TerminalShortcuts.Describe(isMac: false).ToList();
		Assert.All(commands, c => { Assert.Equal("Terminal", c.Scope); Assert.Equal("Terminal", c.Group); Assert.StartsWith("terminal:", c.Key); });
		Assert.Equal("Ctrl+Shift+C", commands.Single(c => c.Key == "terminal:Copy").DefaultGesture);
	}
}
