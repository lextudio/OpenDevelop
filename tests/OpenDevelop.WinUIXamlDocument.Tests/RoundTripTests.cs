using ICSharpCode.WinUIXamlDesigner;
using Xunit;

namespace OpenDevelop.WinUIXamlDocument.Tests;

/// <summary>
/// The WinUI designer edits through an XDocument over the XAML text and re-serializes the whole document on every change
/// (<c>CommitChange: Text = Serialize(Document)</c>). XDocument keeps element structure, text whitespace, comments, CDATA and the
/// choice between <c>&lt;a&gt;&lt;/a&gt;</c> and an empty element, but not every lexical choice XML allows. These cases pin what is lost today
/// (measured, not assumed). A case that starts round-tripping fails its test until its expectation is updated, so the list of losses
/// can only shrink. Line endings are measured separately: they follow the platform, not the source (see
/// <see cref="Line_endings_follow_the_platform_not_the_source"/>).
/// </summary>
public class RoundTripTests
{
	const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

	/// <summary>A snippet, the category an edit-free save is expected to land in, and one attribute edit to measure.</summary>
	public sealed record Case(string Name, string Xaml, RoundTripCategory Expected, string? EditFrom = null, string? EditTo = null)
	{
		public override string ToString() => Name;
	}

	public static IEnumerable<object[]> Cases() => All.Select(c => new object[] { c });

	static readonly Case[] All = {
		new("single-line attributes, spaced self-closing tags",
			$"<Page {Ns}>\n  <StackPanel>\n    <Button x:Name=\"B\" Content=\"Hi\" />\n    <TextBlock Text=\"x\" />\n  </StackPanel>\n</Page>",
			RoundTripCategory.Identical, "Content=\"Hi\"", "Content=\"Edited\""),
		new("self-closing without a space",
			$"<Page {Ns}>\n  <Button x:Name=\"B\" Content=\"Hi\"/>\n</Page>",
			RoundTripCategory.EmptyElementForm, "Content=\"Hi\"", "Content=\"Edited\""),
		new("an empty element written as a start tag and an end tag (kept as written)",
			$"<Page {Ns}>\n  <Button x:Name=\"B\" Content=\"Hi\"></Button>\n</Page>",
			RoundTripCategory.Identical, "Content=\"Hi\"", "Content=\"Edited\""),
		new("attributes on separate lines (the usual Visual Studio layout)",
			$"<Page\n    xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n  <Button\n      x:Name=\"B\"\n      Content=\"Hi\"\n      Margin=\"4\" />\n</Page>",
			RoundTripCategory.AttributeWhitespace, "Content=\"Hi\"", "Content=\"Edited\""),
		new("several spaces between attributes",
			$"<Page {Ns}>\n  <Button x:Name=\"B\"   Content=\"Hi\"   Margin=\"4\" />\n</Page>",
			RoundTripCategory.AttributeWhitespace, "Content=\"Hi\"", "Content=\"Edited\""),
		new("CRLF line endings",
			$"<Page {Ns}>\r\n  <Button x:Name=\"B\" Content=\"Hi\" />\r\n</Page>",
			RoundTripCategory.Identical, "Content=\"Hi\"", "Content=\"Edited\""),
		new("an XML declaration (dropped, see The_xml_declaration_is_dropped)",
			$"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Page {Ns}>\n  <Button x:Name=\"B\" Content=\"Hi\" />\n</Page>",
			RoundTripCategory.Identical, "Content=\"Hi\"", "Content=\"Edited\""),
		new("single-quoted attribute values",
			$"<Page {Ns}>\n  <Button x:Name='B' Content='Hi' />\n</Page>",
			RoundTripCategory.QuoteStyle, "Content='Hi'", "Content=\"Edited\""),
		new("escaped characters the writer re-emits the same way",
			$"<Page {Ns}>\n  <TextBlock x:Name=\"T\" Text=\"a &amp; b &lt; c &quot;d&quot;\" />\n</Page>",
			RoundTripCategory.Identical, "Text=\"a &amp; b &lt; c &quot;d&quot;\"", "Text=\"Edited\""),
		new("a greater-than sign written as &gt; in an attribute",
			$"<Page {Ns}>\n  <TextBlock x:Name=\"T\" Text=\"a &gt; b\" />\n</Page>",
			RoundTripCategory.Identical, "Text=\"a &gt; b\"", "Text=\"Edited\""),
		new("a character reference for a newline in an attribute (rewritten)",
			$"<Page {Ns}>\n  <TextBlock x:Name=\"T\" Text=\"one&#10;two\" />\n</Page>",
			RoundTripCategory.Other, "Text=\"one&#10;two\"", "Text=\"Edited\""),
		new("markup extensions, x:Bind and design-time attributes on one line",
			$"<Page {Ns} xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"d\">\n  <TextBlock x:Name=\"T\" Text=\"{{x:Bind Name, Mode=OneWay}}\" d:Text=\"Sample\" Foreground=\"{{ThemeResource TextFillColorPrimaryBrush}}\" />\n</Page>",
			RoundTripCategory.Identical, "Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\"", "Foreground=\"Edited\""),
		new("comments before the root and inside",
			$"<!-- header -->\n<Page {Ns}>\n  <!-- body -->\n  <Button x:Name=\"B\" Content=\"Hi\" />\n</Page>",
			RoundTripCategory.Identical, "Content=\"Hi\"", "Content=\"Edited\""),
		new("property elements and attached properties",
			$"<Page {Ns}>\n  <Grid x:Name=\"G\">\n    <Grid.RowDefinitions>\n      <RowDefinition Height=\"Auto\" />\n      <RowDefinition />\n    </Grid.RowDefinitions>\n    <Button x:Name=\"B\" Grid.Row=\"1\" Content=\"Hi\" />\n  </Grid>\n</Page>",
			RoundTripCategory.Identical, "Content=\"Hi\"", "Content=\"Edited\""),
		new("text content with inner whitespace and newlines",
			$"<Page {Ns}>\n  <TextBlock x:Name=\"T\">  hello \n    world  </TextBlock>\n</Page>",
			RoundTripCategory.Identical),
		new("a CDATA section",
			$"<Page {Ns}>\n  <TextBlock x:Name=\"T\"><![CDATA[a < b]]></TextBlock>\n</Page>",
			RoundTripCategory.Identical),
		new("a newline after the closing root tag (what editors save)",
			$"<Page {Ns}>\n  <Button x:Name=\"B\" Content=\"Hi\" />\n</Page>\n",
			RoundTripCategory.Identical, "Content=\"Hi\"", "Content=\"Edited\""),
		new("a ControlTemplate with a VisualStateManager, attributes on separate lines",
			$"<Page {Ns}>\n  <Page.Resources>\n    <ControlTemplate x:Key=\"T\" TargetType=\"Button\">\n      <Grid x:Name=\"Root\"\n            Background=\"{{TemplateBinding Background}}\">\n        <VisualStateManager.VisualStateGroups>\n          <VisualStateGroup x:Name=\"CommonStates\">\n            <VisualState x:Name=\"Normal\" />\n            <VisualState x:Name=\"PointerOver\">\n              <Storyboard>\n                <ObjectAnimationUsingKeyFrames Storyboard.TargetName=\"Root\"\n                                               Storyboard.TargetProperty=\"Background\">\n                  <DiscreteObjectKeyFrame KeyTime=\"0\" Value=\"Red\" />\n                </ObjectAnimationUsingKeyFrames>\n              </Storyboard>\n            </VisualState>\n          </VisualStateGroup>\n        </VisualStateManager.VisualStateGroups>\n      </Grid>\n    </ControlTemplate>\n  </Page.Resources>\n  <Button x:Name=\"B\" Content=\"Hi\" />\n</Page>",
			RoundTripCategory.AttributeWhitespace, "Content=\"Hi\"", "Content=\"Edited\""),
	};

	[Theory]
	[MemberData(nameof(Cases))]
	public void An_edit_free_save_lands_in_the_expected_category(Case c)
	{
		var actual = RoundTrip.Classify(c.Xaml);

		Assert.True(c.Expected == actual,
			$"'{c.Name}': expected {c.Expected} but an edit-free save is now {actual}. If this case now round-trips better, update its expectation: "
			+ "the list of losses only shrinks.");
	}

	/// <summary>
	/// One attribute edit. The minimal result changes only that attribute's text (compared ignoring line endings, which are measured
	/// separately). The editor re-serializes the document, so wherever the layout is lost the edit also rewrites lines that were not touched.
	/// </summary>
	[Theory]
	[MemberData(nameof(Cases))]
	public void One_attribute_edit_rewrites_other_lines_exactly_where_the_layout_is_lost(Case c)
	{
		if (c.EditFrom == null)
			return;
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(c.Xaml, out var error), error);
		var element = editor.FindElement("B") ?? editor.FindElement("T");
		Assert.NotNull(element);
		string attribute = c.EditFrom.Split('=')[0];

		editor.SetAttribute(element, attribute, "Edited");

		// The editor keeps the layout of every tag the edit did not touch (WinUIXamlDocumentEditor.PreserveLayout): apart from the
		// edited element's own start tag, which the serializer lays out, the text is the source with the one value changed.
		string minimal = c.Xaml.Replace(c.EditFrom, attribute + "=\"Edited\"");
		static string WithoutEditedTag(string text)
			=> System.Text.RegularExpressions.Regex.Replace(RoundTrip.Lf(text), "<[A-Za-z][^<>]*x:Name=[\"'](B|T)[\"'][^<>]*>", "@@");
		Assert.Equal(WithoutEditedTag(minimal), WithoutEditedTag(editor.Text));
	}

	/// <summary>
	/// Line endings are the platform's, not the source's: <c>XmlWriterSettings.NewLineHandling</c> defaults to Replace, so saving converts every
	/// newline to <see cref="Environment.NewLine"/>. On Windows a LF file becomes CRLF on its first edit; on macOS and Linux a CRLF file becomes LF.
	/// </summary>
	[Fact]
	public void Line_endings_follow_the_platform_not_the_source()
	{
		bool windows = Environment.NewLine == "\r\n";
		var lf = RoundTrip.Analyze(All[0].Xaml);
		var crlf = RoundTrip.Analyze(All.Single(c => c.Name == "CRLF line endings").Xaml);

		Assert.Equal(windows, lf.LineEndingsChanged);    // an LF source is converted on Windows only
		Assert.Equal(!windows, crlf.LineEndingsChanged); // a CRLF source is converted everywhere else
	}

	[Fact]
	public void The_xml_declaration_is_dropped()
	{
		var withDeclaration = All.Single(c => c.Name.StartsWith("an XML declaration", StringComparison.Ordinal));

		var analysis = RoundTrip.Analyze(withDeclaration.Xaml);
		var saved = RoundTrip.SaveWithoutEdit(withDeclaration.Xaml)!;

		Assert.True(analysis.DeclarationDropped);
		Assert.DoesNotContain("<?xml", saved);
		Assert.False(RoundTrip.Analyze(All[0].Xaml).DeclarationDropped);   // a file without one is not changed in this respect
	}

	[Fact]
	public void The_corpus_covers_every_loss_the_editor_has_and_the_things_it_keeps()
	{
		var seen = All.Select(c => c.Expected).ToHashSet();
		foreach (var category in new[] { RoundTripCategory.Identical, RoundTripCategory.AttributeWhitespace,
			RoundTripCategory.EmptyElementForm, RoundTripCategory.QuoteStyle, RoundTripCategory.Other })
			Assert.Contains(category, seen);
	}

	[Fact]
	public void Malformed_xaml_is_not_loadable()
		=> Assert.Equal(RoundTripCategory.NotLoadable, RoundTrip.Classify("<Page><Button></Page>"));
}
