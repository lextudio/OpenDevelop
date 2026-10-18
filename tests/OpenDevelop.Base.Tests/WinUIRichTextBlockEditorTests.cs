using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WinUIXamlDesigner;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class WinUIRichTextBlockEditorTests
{
	const string Source = """
		<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
		  <RichTextBlock x:Name="Description"><Paragraph><Run FontWeight="Bold">Hello</Run><Run Foreground="Red"> world</Run></Paragraph></RichTextBlock>
		</Grid>
		""";

	[Fact]
	public void RichTextBlockRuns_RoundTripAndUndoWithoutTouchingTheContainingXaml()
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(Source, out var error), error);
		var block = editor.FindElement("Description")!;
		Assert.True(editor.TryGetRichTextRuns(block, out var original));
		Assert.Collection(original,
			run => { Assert.Equal("Hello", run.Text); Assert.True(run.Bold); },
			run => { Assert.Equal(" world", run.Text); Assert.Equal("Red", run.Foreground); });

		editor.SetRichTextRuns(block, new[] {
			new DesignerRichTextRun { Text = "New", Italic = true, FontSize = 18 },
			new DesignerRichTextRun { Text = " text", Underline = true, Foreground = "Blue" }
		});
		Assert.Contains("FontStyle=\"Italic\"", editor.Text, StringComparison.Ordinal);
		Assert.Contains("TextDecorations=\"Underline\"", editor.Text, StringComparison.Ordinal);
		Assert.True(editor.Undo());
		Assert.Equal(Source, editor.Text);
	}

	[Fact]
	public void RichTextBlockRuns_RejectsContentTheEditorCannotPreserve()
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset("""<RichTextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Paragraph><Hyperlink>link</Hyperlink></Paragraph></RichTextBlock>""", out var error), error);
		Assert.False(editor.TryGetRichTextRuns(editor.Document.Root!, out _));
	}

	[Theory]
	[InlineData("Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\"")]
	[InlineData("FontWeight=\"SemiBold\"")]
	[InlineData("FontStyle=\"Oblique\"")]
	[InlineData("TextDecorations=\"None\"")]
	public void RichTextBlockRuns_RejectsValuesThatCannotRoundTrip(string attribute)
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset($"""<RichTextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Paragraph><Run {attribute}>text</Run></Paragraph></RichTextBlock>""", out var error), error);
		Assert.False(editor.TryGetRichTextRuns(editor.Document.Root!, out _));
	}

	[Fact]
	public void RichTextBlockRuns_EmptyBlockCanBeEditedAndCleared()
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset("""<RichTextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""", out var error), error);
		var block = editor.Document.Root!;
		Assert.True(editor.TryGetRichTextRuns(block, out var runs));
		Assert.Single(runs);
		editor.SetRichTextRuns(block, Array.Empty<DesignerRichTextRun>());
		Assert.Contains("<Paragraph><Run></Run></Paragraph>", editor.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void RichTextBlockRuns_RoundTripsLineBreakTextAsAPortableRun()
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset("""<RichTextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Paragraph><Run>first</Run></Paragraph></RichTextBlock>""", out var error), error);
		var block = editor.Document.Root!;
		editor.SetRichTextRuns(block, new[] { new DesignerRichTextRun { Text = "first" }, new DesignerRichTextRun { Text = Environment.NewLine }, new DesignerRichTextRun { Text = "second" } });
		Assert.True(editor.TryGetRichTextRuns(block, out var runs));
		Assert.Collection(runs,
			run => Assert.Equal("first", run.Text),
			run => Assert.Equal(Environment.NewLine, run.Text),
			run => Assert.Equal("second", run.Text));
	}

	[Theory]
	[InlineData("<Paragraph FontSize=\"20\"><Run>Hello</Run></Paragraph>")]
	[InlineData("<Paragraph TextAlignment=\"Center\"><Run>Hello</Run></Paragraph>")]
	[InlineData("<Paragraph>Hello</Paragraph>")]
	public void RichTextBlockRuns_RejectsParagraphContentItCannotPreserve(string paragraph)
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset($"""<RichTextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">{paragraph}</RichTextBlock>""", out var error), error);
		Assert.False(editor.TryGetRichTextRuns(editor.Document.Root!, out _));
	}

	[Fact]
	public void RichTextBlockRuns_RejectsCommentsThatARewriteWouldDelete()
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset("""<RichTextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Paragraph><Run>text</Run><!-- preserved author note --></Paragraph></RichTextBlock>""", out var error), error);
		Assert.False(editor.TryGetRichTextRuns(editor.Document.Root!, out _));
	}

	[Fact]
	public void RichTextBlockRuns_RejectsControlsOutsideTheBuiltInXamlNamespace()
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset("""<custom:RichTextBlock xmlns:custom="urn:test"><custom:Paragraph><custom:Run>text</custom:Run></custom:Paragraph></custom:RichTextBlock>""", out var error), error);
		Assert.False(editor.TryGetRichTextRuns(editor.Document.Root!, out _));
	}

	[Theory]
	[InlineData("<custom:Paragraph><custom:Run>text</custom:Run></custom:Paragraph>")]
	[InlineData("<Paragraph><custom:Run>text</custom:Run></Paragraph>")]
	[InlineData("<Paragraph><Run custom:FontWeight=\"Bold\">text</Run></Paragraph>")]
	public void RichTextBlockRuns_RejectsForeignNamespaceContentInsideBuiltInBlock(string content)
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset($"""<RichTextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:custom="urn:test">{content}</RichTextBlock>""", out var error), error);
		Assert.False(editor.TryGetRichTextRuns(editor.Document.Root!, out _));
	}
}
