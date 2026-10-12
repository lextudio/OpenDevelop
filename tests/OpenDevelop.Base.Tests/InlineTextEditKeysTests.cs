using System.Windows.Input;

using ICSharpCode.SharpDevelop.Designer.Presentation;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class InlineTextEditKeysTests
{
	[Theory]
	[InlineData(Key.Enter, ModifierKeys.None, true, InlineTextEditKeyAction.Commit)]
	[InlineData(Key.Enter, ModifierKeys.Shift, true, InlineTextEditKeyAction.InsertNewline)]
	[InlineData(Key.Enter, ModifierKeys.Shift | ModifierKeys.Control, true, InlineTextEditKeyAction.InsertNewline)]
	[InlineData(Key.Enter, ModifierKeys.Shift, false, InlineTextEditKeyAction.Commit)]
	[InlineData(Key.Enter, ModifierKeys.Control, true, InlineTextEditKeyAction.Commit)]
	[InlineData(Key.Escape, ModifierKeys.None, true, InlineTextEditKeyAction.Cancel)]
	[InlineData(Key.Escape, ModifierKeys.Shift, false, InlineTextEditKeyAction.Cancel)]
	[InlineData(Key.A, ModifierKeys.Shift, true, InlineTextEditKeyAction.None)]
	public void FollowsTheInPlaceEditorKeyContract(Key key, ModifierKeys modifiers, bool acceptsNewlines, InlineTextEditKeyAction expected)
		=> Assert.Equal(expected, InlineTextEditKeys.Resolve(key, modifiers, acceptsNewlines));
}
