using System.Xml.Linq;
using ICSharpCode.WinUIXamlDesigner;
using Xunit;

namespace OpenDevelop.WinUIXamlDocument.Tests;

/// <summary>
/// The Margin the designer writes for a moved element (<see cref="WinUIXamlMarginEditor"/>): the
/// side the element's alignment anchors, and - the part the real-pointer resize journey found - no
/// edit at all when the position delta rounds to zero. A pure resize therefore stays one undo
/// entry instead of leaving a second, empty Undo step behind.
/// </summary>
public class MarginEditorTests
{
	const string Source =
		"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
		"<Page\n" +
		"    xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
		"    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
		"    <StackPanel>\n" +
		"        <Button x:Name=\"Button1\" Content=\"One\" />\n" +
		"    </StackPanel>\n" +
		"</Page>\n";

	static (WinUIXamlDocumentEditor Editor, XElement Button) Load()
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(Source, out _));
		return (editor, editor.FindElement("Button1"));
	}

	[Fact]
	public void A_zero_delta_asks_for_no_edit()
	{
		var (_, button) = Load();

		Assert.False(WinUIXamlMarginEditor.TryDelta(button, 0, 0, out var margin));
		Assert.Null(margin);
		// A fraction of a unit that the commit would round away is still no edit.
		Assert.False(WinUIXamlMarginEditor.TryDelta(button, 0.4, -0.49, out margin));
		Assert.Null(margin);
	}

	[Fact]
	public void A_move_lands_on_the_side_the_alignment_anchors()
	{
		var (_, button) = Load();

		Assert.True(WinUIXamlMarginEditor.TryDelta(button, 12, 8, out var margin));
		Assert.Equal("12,8,0,0", margin);

		button.SetAttributeValue("HorizontalAlignment", "Right");
		button.SetAttributeValue("VerticalAlignment", "Bottom");
		Assert.True(WinUIXamlMarginEditor.TryDelta(button, 12, 8, out margin));
		// A right/bottom anchor moves by giving up margin on that side.
		Assert.Equal("0,0,-12,-8", margin);

		button.SetAttributeValue("HorizontalAlignment", "Center");
		button.SetAttributeValue("VerticalAlignment", "Center");
		Assert.True(WinUIXamlMarginEditor.TryDelta(button, 12, 8, out margin));
		Assert.Equal("12,8,-12,-8", margin);
	}

	[Fact]
	public void Returning_to_no_offset_removes_the_Margin_instead_of_writing_zeros()
	{
		var (_, button) = Load();
		button.SetAttributeValue("Margin", "12,8,0,0");

		Assert.True(WinUIXamlMarginEditor.TryDelta(button, -12, -8, out var margin));
		Assert.Null(margin);
	}

	[Fact]
	public void A_move_is_one_undo_entry_that_restores_the_tag()
	{
		var (editor, button) = Load();

		Assert.True(WinUIXamlMarginEditor.TryDelta(button, 20, 10, out var margin));
		editor.SetAttribute(button, "Margin", margin);
		Assert.True(editor.CanUndo);
		Assert.Contains("Margin=\"20,10,0,0\"", editor.Text);

		Assert.True(editor.Undo());
		Assert.Equal(Source, editor.Text);
		Assert.False(editor.CanUndo);
	}

	[Fact]
	public void A_pure_resize_pushes_no_undo_entry()
	{
		var (editor, button) = Load();

		// What the resize-handle commit does: the position delta is zero, so nothing is written.
		if (WinUIXamlMarginEditor.TryDelta(button, 0, 0, out var margin))
			editor.SetAttribute(button, "Margin", margin);

		Assert.False(editor.CanUndo);
		Assert.Equal(Source, editor.Text);
	}
}
