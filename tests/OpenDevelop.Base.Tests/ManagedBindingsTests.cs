using System.Windows.Input;
using ICSharpCode.Core.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>ManagedBindings.Rebuild: the per-editor key binding list rebuilt from the original bindings and the user's overrides (the logic behind editor shortcuts).</summary>
public sealed class ManagedBindingsTests
{
	static readonly RoutedUICommand DeleteWord = new RoutedUICommand("Delete word", "DeleteWord", typeof(ManagedBindingsTests));
	static readonly RoutedUICommand LineBreak = new RoutedUICommand("Line break", "LineBreak", typeof(ManagedBindingsTests));
	static readonly RoutedUICommand Other = new RoutedUICommand("Other", "Other", typeof(ManagedBindingsTests));
	sealed class PlainCommand : ICommand
	{
		public event EventHandler? CanExecuteChanged { add { } remove { } }
		public bool CanExecute(object? parameter) => true;
		public void Execute(object? parameter) { }
	}

	static string? KeyOf(ICommand c) => c is RoutedUICommand r ? "editor:" + r.Name : null;

	readonly KeyBinding deleteWord = new KeyBinding(DeleteWord, Key.Delete, ModifierKeys.Control);
	readonly KeyBinding enter = new KeyBinding(LineBreak, Key.Enter, ModifierKeys.None);
	readonly KeyBinding returnKey = new KeyBinding(LineBreak, Key.Return, ModifierKeys.None);
	readonly KeyBinding other = new KeyBinding(Other, Key.F9, ModifierKeys.None);
	readonly KeyBinding plain = new KeyBinding(new PlainCommand(), Key.Tab, ModifierKeys.None); // not a routed command: unmanaged
	readonly List<InputBinding> live = new List<InputBinding>();
	readonly List<InputBinding> added = new List<InputBinding>();
	List<InputBinding> Originals => new List<InputBinding> { deleteWord, enter, returnKey, other, plain };

	public ManagedBindingsTests() => live.AddRange(Originals);

	void Rebuild(Func<string, bool> hasOverride, Func<string, KeyGesture?> resolve) =>
		ManagedBindings.Rebuild(live, Originals, added, KeyOf, hasOverride, k => resolve(k)!);

	[Fact]
	public void WithoutOverrides_EveryOriginalIsThere_UntouchedAndNothingIsAdded()
	{
		Rebuild(_ => false, _ => null);
		Assert.Equal(Originals, live);
		Assert.Empty(added);
		Assert.Equal(Key.Delete, deleteWord.Key); // the originals are never changed
	}

	[Fact]
	public void AnOverride_ReplacesTheOriginal_WithABindingOnTheNewGesture_ThatRunsTheSameCommand()
	{
		Rebuild(k => k == "editor:DeleteWord", _ => new KeyGesture(Key.D, ModifierKeys.Control | ModifierKeys.Shift));

		Assert.DoesNotContain(deleteWord, live);
		var binding = Assert.IsType<KeyBinding>(Assert.Single(added));
		Assert.Contains(binding, live);
		Assert.Equal((Key.D, ModifierKeys.Control | ModifierKeys.Shift), (binding.Key, binding.Modifiers));
		Assert.Same(DeleteWord, binding.Command);
		Assert.Contains(other, live); // the others are untouched
		Assert.Contains(plain, live);
	}

	[Fact]
	public void None_RemovesTheCommandsKey_AndNothingTakesItsPlace()
	{
		Rebuild(k => k == "editor:DeleteWord", _ => null);
		Assert.DoesNotContain(deleteWord, live);
		Assert.Empty(added);
	}

	[Fact]
	public void ResettingBringsTheOriginalBack_AndDropsTheBindingAddedBefore()
	{
		Rebuild(k => k == "editor:DeleteWord", _ => new KeyGesture(Key.D, ModifierKeys.Control));
		var first = added.Single();

		Rebuild(_ => false, _ => null);

		Assert.Contains(deleteWord, live);
		Assert.DoesNotContain(first, live);
		Assert.Empty(added);
		Assert.Equal(5, live.Count);
	}

	[Fact]
	public void ChangingTheOverrideAgain_ReplacesTheBindingAddedBefore_NotAddsAnother()
	{
		Rebuild(k => k == "editor:DeleteWord", _ => new KeyGesture(Key.D, ModifierKeys.Control));
		Rebuild(k => k == "editor:DeleteWord", _ => new KeyGesture(Key.E, ModifierKeys.Control));

		Assert.Equal(Key.E, Assert.IsType<KeyBinding>(Assert.Single(added)).Key);
		Assert.Single(live, b => b is KeyBinding k && k.Command == DeleteWord);
	}

	[Fact]
	public void ACommandWithTwoKeys_BecomesOneBinding_OnAnOverride()
	{
		Rebuild(k => k == "editor:LineBreak", _ => new KeyGesture(Key.F2));

		Assert.DoesNotContain(enter, live);
		Assert.DoesNotContain(returnKey, live);
		Assert.Equal(Key.F2, Assert.IsType<KeyBinding>(Assert.Single(added)).Key);
	}

	[Fact]
	public void ABindingThatIsNotManaged_IsNeverTouched_EvenWhenEverythingIsOverridden()
	{
		Rebuild(_ => true, _ => null);
		Assert.Contains(plain, live); // keyOf gives null for it: the Tab snippet binding of the editor stays
	}

	[Fact]
	public void ABindingTheOwnerRemovedAfterwards_IsNotPutBackByARebuild()
	{
		// like the decompiler view removing Tab editing after the editor was created: the list's owner decides about bindings that are not managed
		Rebuild(_ => false, _ => null);
		live.Remove(plain);

		Rebuild(k => k == "editor:DeleteWord", _ => new KeyGesture(Key.D, ModifierKeys.Control));

		Assert.DoesNotContain(plain, live);
		Assert.Contains(other, live);
	}
}
