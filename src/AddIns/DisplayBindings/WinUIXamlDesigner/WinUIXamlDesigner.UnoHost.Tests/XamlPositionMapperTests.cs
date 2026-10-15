using System.Xml.Linq;
using ICSharpCode.WinUIXamlDesigner.UnoHost;
using Xunit;

namespace WinUIXamlDesigner.UnoHost.Tests;

public sealed class XamlPositionMapperTests
{
	const string Source =
		"<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
		"      xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
		"  <Grid>\n" +
		"    <Button x:Name=\"First\" />\n" +
		"    <Button x:Name=\"Second\" Content=\"Broken\" />\n" +
		"  </Grid>\n" +
		"</Page>";

	// The host reserializes the document without formatting and injects a resources element,
	// so every element ends up on one line.
	static string Loaded(string injectedResources = "") =>
		XDocument.Parse(Source).Root!.ToString(SaveOptions.DisableFormatting)
			.Replace("<Grid>", "<Grid>" + injectedResources);

	static (int Line, int Column) PositionOf(string text, string needle)
	{
		var index = text.IndexOf(needle, System.StringComparison.Ordinal);
		var line = 1 + text.Take(index).Count(c => c == '\n');
		var column = index - (text.LastIndexOf('\n', index) + 1) + 1;
		return (line, column);
	}

	[Fact]
	public void IdenticalText_MapsToItself()
	{
		var mapped = XamlPositionMapper.MapToSource(Source, Source, 5, 6);
		Assert.Equal((5, 6, 7), mapped);
	}

	[Fact]
	public void ReserializedText_MapsToTheNamedElementInTheSource()
	{
		var loaded = Loaded();
		Assert.DoesNotContain('\n', loaded);
		var (line, column) = PositionOf(loaded, "<Button x:Name=\"Second\"");

		var mapped = XamlPositionMapper.MapToSource(Source, loaded, line, column + 1);

		// <Button x:Name="Second" ...> starts on line 5 of the source, after 4 spaces and '<'.
		Assert.Equal((5, 6, 6 + "Button".Length), mapped);
	}

	[Fact]
	public void InjectedSiblingsAheadOfTheElement_DoNotShiftIdentifiedElements()
	{
		var loaded = Loaded("<Button x:Name=\"Injected\" />");
		var (line, column) = PositionOf(loaded, "<Button x:Name=\"Second\"");

		var mapped = XamlPositionMapper.MapToSource(Source, loaded, line, column + 1);

		Assert.Equal(5, mapped!.Value.Line);
	}

	[Fact]
	public void ElementThatExistsOnlyInTheLoadedText_ResolvesToItsNearestSourceAncestor()
	{
		var loaded = Loaded("<Grid.Resources><Style x:Key=\"Injected\" /></Grid.Resources>");
		var (line, column) = PositionOf(loaded, "<Style x:Key=\"Injected\"");

		var mapped = XamlPositionMapper.MapToSource(Source, loaded, line, column + 1);

		// Grid.Resources is not in the source, so the error lands on <Grid> (line 3).
		Assert.Equal(3, mapped!.Value.Line);
	}

	[Fact]
	public void DocumentWithoutARootElement_HasNoMapping()
	{
		Assert.Null(XamlPositionMapper.MapToSource("<!-- only a comment -->", Loaded(), 1, 5));
		Assert.Null(XamlPositionMapper.MapToSource(Source, "<!-- only a comment -->", 1, 5));
	}

	[Fact]
	public void MalformedSource_HasNoTrustworthyMapping()
	{
		Assert.Null(XamlPositionMapper.MapToSource("<Page><Grid></Page>", Loaded(), 1, 10));
	}
}
