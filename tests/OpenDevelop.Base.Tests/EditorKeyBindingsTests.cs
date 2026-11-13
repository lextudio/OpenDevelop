using System.Windows.Input;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Editor;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>EditorKeyBindings on a real AvalonEdit TextArea: the commands, applying an override, resetting, and attaching twice.</summary>
[Collection("KeyBindingServiceDefault")]
public sealed class EditorKeyBindingsTests : IDisposable
{
	const string DeleteNextWord = "editor:EditingCommands.DeleteNextWord";

	public EditorKeyBindingsTests() => KeyBindingService.Default.ResetAll();
	public void Dispose() => KeyBindingService.Default.ResetAll();

	static List<(Key Key, ModifierKeys Modifiers)> Gestures(TextArea textArea, string commandKey) =>
		new[] { textArea.DefaultInputHandler.CaretNavigation.InputBindings, textArea.DefaultInputHandler.Editing.InputBindings }
			.SelectMany(l => l).OfType<KeyBinding>()
			.Where(b => EditorKeyBindings.KeyFor(b.Command) == commandKey)
			.Select(b => (b.Key, b.Modifiers)).ToList();

	[Fact]
	public void TheDescribedCommands_ExcludeTheTextEntryOnes_AndHaveReadableNamesAndDefaults()
	{
		var commands = EditorKeyBindings.Describe().ToList();
		Assert.DoesNotContain(commands, c => c.Key.Contains("EnterParagraphBreak") || c.Key.Contains("EnterLineBreak") || c.Key.Contains("TabForward") || c.Key.Contains("TabBackward"));
		var delete = commands.Single(c => c.Key == DeleteNextWord);
		Assert.Equal("Delete Next Word", delete.Name);
		Assert.Equal("Ctrl+Delete", delete.DefaultGesture);
		Assert.All(commands, c => Assert.Equal("Text editor", c.Scope));
	}

	[Fact]
	public void AnOverride_IsAppliedAtOnce_AndAResetBringsTheOriginalBack()
	{
		var textArea = new TextArea();
		EditorKeyBindings.Attach(textArea);
		Assert.Equal(new[] { (Key.Delete, ModifierKeys.Control) }, Gestures(textArea, DeleteNextWord));

		KeyBindingService.Default.SetOverride(DeleteNextWord, "Ctrl+Shift+D");
		Assert.Equal(new[] { (Key.D, ModifierKeys.Control | ModifierKeys.Shift) }, Gestures(textArea, DeleteNextWord));

		KeyBindingService.Default.SetOverride(DeleteNextWord, "");
		Assert.Empty(Gestures(textArea, DeleteNextWord));

		KeyBindingService.Default.ResetOverride(DeleteNextWord);
		Assert.Equal(new[] { (Key.Delete, ModifierKeys.Control) }, Gestures(textArea, DeleteNextWord));
	}

	[Fact]
	public void AttachingTwice_KeepsTheOriginals_SoAResetStillWorks()
	{
		var textArea = new TextArea();
		EditorKeyBindings.Attach(textArea);
		KeyBindingService.Default.SetOverride(DeleteNextWord, "Ctrl+Shift+D");

		EditorKeyBindings.Attach(textArea); // a second attach must not take the overridden state for the originals

		KeyBindingService.Default.ResetOverride(DeleteNextWord);
		Assert.Equal(new[] { (Key.Delete, ModifierKeys.Control) }, Gestures(textArea, DeleteNextWord));
	}

	[Fact]
	public void TheTextEntryKeys_AreNeverTouched()
	{
		var textArea = new TextArea();
		EditorKeyBindings.Attach(textArea);
		KeyBindingService.Default.SetOverride("editor:EditingCommands.EnterParagraphBreak", "F2"); // not a command of the page: ignored here
		Assert.Contains((Key.Enter, ModifierKeys.None), Gestures(textArea, "editor:EditingCommands.EnterParagraphBreak"));
	}
}

[CollectionDefinition("KeyBindingServiceDefault", DisableParallelization = true)]
public sealed class KeyBindingServiceDefaultCollection { }
