using System.Xml.Linq;
using ICSharpCode.WinUIXamlDesigner;
using Xunit;

namespace OpenDevelop.WinUIXamlDocument.Tests;

/// <summary>An edit must change the edited element and leave the author's layout of everything else alone.</summary>
public class EditPreservationTests
{
	const string Source =
		"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
		"<Page\n" +
		"    xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
		"    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
		"    <StackPanel>\n" +
		"        <Button\n" +
		"            x:Name=\"One\"\n" +
		"            Content='Hello' />\n" +
		"        <TextBlock Text=\"a\"/>\n" +
		"        <Button x:Name=\"Two\" Content=\"Two\" />\n" +
		"    </StackPanel>\n" +
		"</Page>\n";

	static (WinUIXamlDocumentEditor Editor, XElement Two) Load()
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(Source, out _));
		return (editor, editor.FindElement("Two"));
	}

	[Fact]
	public void An_attribute_edit_leaves_every_other_tag_declaration_and_line_ending_as_written()
	{
		var (editor, two) = Load();
		editor.SetAttribute(two, "Content", "Changed");

		string expected = Source.Replace("<Button x:Name=\"Two\" Content=\"Two\" />", "<Button x:Name=\"Two\" Content=\"Changed\" />");
		Assert.Equal(expected, editor.Text);
	}

	[Fact]
	public void The_edited_tag_alone_is_reflowed_when_it_was_laid_out_over_several_lines()
	{
		var (editor, _) = Load();
		editor.SetAttribute(editor.FindElement("One"), "Content", "Changed");

		Assert.Contains("<TextBlock Text=\"a\"/>", editor.Text);
		Assert.Contains("<?xml version=\"1.0\" encoding=\"utf-8\"?>", editor.Text);
		Assert.Contains("<Page\n    xmlns=", editor.Text);
		Assert.Contains("Content=\"Changed\"", editor.Text);
		Assert.DoesNotContain("Hello", editor.Text);
	}

	[Fact]
	public void Crlf_sources_stay_crlf_and_lf_sources_stay_lf()
	{
		foreach (var ending in new[] { "\n", "\r\n" }) {
			var editor = new WinUIXamlDocumentEditor();
			Assert.True(editor.Reset(Source.Replace("\n", ending), out _));
			editor.SetAttribute(editor.FindElement("Two"), "Content", "X");
			Assert.Equal(Source.Replace("\n", ending).Replace("Content=\"Two\"", "Content=\"X\""), editor.Text);
		}
	}

	[Fact]
	public void An_inserted_element_leaves_the_existing_tags_alone_and_undo_restores_the_source_exactly()
	{
		var (editor, _) = Load();
		editor.Insert("Button", editor.FindElement("Two").Parent);

		Assert.Contains("<TextBlock Text=\"a\"/>", editor.Text);
		Assert.Contains("Content='Hello' />", editor.Text);
		Assert.True(editor.Undo());
		Assert.Equal(Source, editor.Text);
	}

	[Fact]
	public void The_result_still_parses_to_the_same_document_as_the_plain_serialization()
	{
		var (editor, two) = Load();
		editor.SetAttribute(two, "Content", "Changed");

		var plain = WinUIXamlDocumentEditor.Serialize(editor.Document);
		Assert.True(XNode.DeepEquals(XDocument.Parse(plain, LoadOptions.PreserveWhitespace).Root, XDocument.Parse(editor.Text, LoadOptions.PreserveWhitespace).Root));
	}
}
