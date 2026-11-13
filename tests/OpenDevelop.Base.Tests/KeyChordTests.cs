using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.Core.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>Key chords such as Ctrl+K, Ctrl+C: parsing (ShortcutGesture), the state machine (ChordRouter), the service on a real element, and the Keyboard page's rules.</summary>
public sealed class KeyChordTests : IDisposable
{
	readonly string directory = Path.Combine(Path.GetTempPath(), "od-key-chord-" + Guid.NewGuid().ToString("N"));
	public KeyChordTests() => Directory.CreateDirectory(directory);
	public void Dispose() => Directory.Delete(directory, true);

	sealed class CountingCommand : ICommand
	{
		public int Executed;
		public bool Can = true;
		public event EventHandler? CanExecuteChanged { add { } remove { } }
		public bool CanExecute(object? parameter) => Can;
		public void Execute(object? parameter) => Executed++;
	}

	static ShortcutGesture Parse(string text)
	{
		Assert.True(ShortcutGesture.TryParse(text, out var g), text);
		return g!;
	}

	[Fact]
	public void AShortcut_IsOneStrokeOrAChordOfTwo_InAnySpelling()
	{
		Assert.False(Parse("Ctrl+Shift+F8").IsChord);
		var chord = Parse("Control|K,  ctrl+c");
		Assert.True(chord.IsChord);
		Assert.Equal("Ctrl+K, Ctrl+C", chord.ToInvariantText());
		Assert.Equal((Key.K, ModifierKeys.Control), (chord.First.Key, chord.First.Modifiers));
		Assert.Equal((Key.C, ModifierKeys.Control), (chord.Second!.Key, chord.Second.Modifiers));
	}

	[Theory]
	[InlineData("")]
	[InlineData("Banana")]
	[InlineData("Ctrl+K, Banana")]
	[InlineData("Ctrl+K, Ctrl+C, Ctrl+D")] // three strokes
	[InlineData("Ctrl+K,")]
	public void WhatIsNotAShortcut_IsRefused(string text)
	{
		Assert.False(ShortcutGesture.TryParse(text, out _));
	}

	[Fact]
	public void TwoShortcutsOverlap_WhenOneTakesTheOthersFirstStroke()
	{
		Assert.True(Parse("Ctrl+K").Overlaps(Parse("Ctrl+K"))); // the same stroke
		Assert.True(Parse("Ctrl+K").Overlaps(Parse("Ctrl+K, Ctrl+C"))); // the stroke starts a chord
		Assert.True(Parse("Ctrl+K, Ctrl+C").Overlaps(Parse("Ctrl+K"))); // and the other way round
		Assert.True(Parse("Ctrl+K, Ctrl+C").Overlaps(Parse("Ctrl+K, Ctrl+C"))); // the same two strokes
		Assert.False(Parse("Ctrl+K, Ctrl+C").Overlaps(Parse("Ctrl+K, Ctrl+U"))); // chords sharing the first stroke can live together
		Assert.False(Parse("Ctrl+K").Overlaps(Parse("Ctrl+L")));
	}

	[Fact]
	public void TheRouter_TakesTheFirstStroke_AndRunsTheCommandOnTheSecond()
	{
		var router = new ChordRouter();
		var command = new CountingCommand();
		router.Add(Parse("Ctrl+K, Ctrl+C"), command);
		var t = DateTime.UtcNow;

		Assert.Equal(ChordOutcome.None, router.Press(Key.A, ModifierKeys.Control, t, out _)); // not a first stroke
		Assert.Equal(ChordOutcome.Started, router.Press(Key.K, ModifierKeys.Control, t, out _));
		Assert.Equal(Key.K, router.Pending!.Key);
		Assert.Equal(ChordOutcome.None, router.Press(Key.LeftCtrl, ModifierKeys.Control, t, out _)); // pressing Ctrl for the second stroke changes nothing
		Assert.NotNull(router.Pending);
		Assert.Equal(ChordOutcome.Completed, router.Press(Key.C, ModifierKeys.Control, t, out var run));
		Assert.Same(command, run);
		Assert.Null(router.Pending);
	}

	[Fact]
	public void AnotherKeyAfterTheFirstStroke_CancelsTheChord_AndIsNotTaken()
	{
		var router = new ChordRouter();
		router.Add(Parse("Ctrl+K, Ctrl+C"), new CountingCommand());
		var t = DateTime.UtcNow;
		router.Press(Key.K, ModifierKeys.Control, t, out _);

		Assert.Equal(ChordOutcome.Cancelled, router.Press(Key.X, ModifierKeys.None, t, out _));
		Assert.Null(router.Pending);
		// and the second stroke alone no longer does anything
		Assert.Equal(ChordOutcome.None, router.Press(Key.C, ModifierKeys.Control, t, out _));
	}

	[Fact]
	public void TheFirstStroke_ExpiresAfterTheTimeout()
	{
		var router = new ChordRouter(TimeSpan.FromSeconds(2));
		router.Add(Parse("Ctrl+K, Ctrl+C"), new CountingCommand());
		var t = DateTime.UtcNow;
		router.Press(Key.K, ModifierKeys.Control, t, out _);

		Assert.Equal(ChordOutcome.None, router.Press(Key.C, ModifierKeys.Control, t.AddSeconds(3), out _)); // too late: this is just Ctrl+C
	}

	[Fact]
	public void TheFirstStrokeAgain_StartsTheChordAgain_AndTwoChordsOnTheSameKeysRunTheFirstThatCanExecute()
	{
		var router = new ChordRouter();
		var blocked = new CountingCommand { Can = false };
		var free = new CountingCommand();
		router.Add(Parse("Ctrl+K, Ctrl+C"), blocked);
		router.Add(Parse("Ctrl+K, Ctrl+C"), free); // two items with the same chord
		var t = DateTime.UtcNow;

		router.Press(Key.K, ModifierKeys.Control, t, out _);
		Assert.Equal(ChordOutcome.Started, router.Press(Key.K, ModifierKeys.Control, t, out _)); // Ctrl+K twice: starts over
		router.Press(Key.C, ModifierKeys.Control, t, out var run);
		run!.Execute(null);

		Assert.Equal(0, blocked.Executed);
		Assert.Equal(1, free.Executed); // the combined command skips the one that cannot execute
	}

	[Fact]
	public void TheService_RunsAChordOverride_OnTheOwner_AndDropsItOnAReset()
	{
		var service = new KeyBindingService(Path.Combine(directory, "KeyBindings.xml"), _ => { });
		var owner = new Border();
		var command = new CountingCommand();
		string shown = "";
		service.Register(owner, "class:Foo.Comment", "codon1", null, command, null, t => shown = t);

		service.SetOverride("class:Foo.Comment", "Ctrl+K, Ctrl+C");
		Assert.Contains(", ", shown); // the menu text shows both strokes
		Assert.Empty(owner.InputBindings); // a chord is not an input binding

		Assert.True(service.TryRunChord(owner, "Ctrl+K", "Ctrl+C", out bool executed));
		Assert.True(executed);
		Assert.Equal(1, command.Executed);

		service.ResetOverride("class:Foo.Comment");
		Assert.False(service.TryRunChord(owner, "Ctrl+K", "Ctrl+C", out executed)); // nothing starts any more
		Assert.Equal(1, command.Executed);
	}

	[Fact]
	public void TheService_RefusesAChordOverrideThatIsNotAShortcut()
	{
		var service = new KeyBindingService(Path.Combine(directory, "KeyBindings.xml"), _ => { });
		Assert.Throws<FormatException>(() => service.SetOverride("class:A", "Ctrl+K, Banana"));
		Assert.Throws<FormatException>(() => service.ReplaceOverrides(new Dictionary<string, string> { ["class:A"] = "Ctrl+K, Ctrl+C, Ctrl+D" }));
		Assert.Empty(service.Overrides);
	}

	[Fact]
	public void ASingleStrokeCaller_SeesAChordAsNoGesture_WhileResolveShortcutKnowsIt()
	{
		var service = new KeyBindingService(Path.Combine(directory, "KeyBindings.xml"), _ => { });
		service.SetOverride("class:A", "Ctrl+K, Ctrl+C");
		Assert.Null(service.Resolve("class:A", "F5")); // the editor, terminal and designer matchers take single strokes only
		Assert.True(service.ResolveShortcut("class:A", "F5")!.IsChord);
	}

	[Fact]
	public void TheCaptureBox_AddsTheSecondStroke_ThenStartsOver()
	{
		Assert.Equal("Ctrl+K", KeyboardShortcutsModel.AppendStroke("", "Ctrl+K"));
		Assert.Equal("Ctrl+K, Ctrl+C", KeyboardShortcutsModel.AppendStroke("Ctrl+K", "Ctrl+C"));
		Assert.Equal("Ctrl+D", KeyboardShortcutsModel.AppendStroke("Ctrl+K, Ctrl+C", "Ctrl+D")); // a third starts over
		Assert.Equal("F5", KeyboardShortcutsModel.AppendStroke("Banana", "F5"));
	}
}
