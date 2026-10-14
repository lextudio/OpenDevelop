using System.Xml.Linq;
using ICSharpCode.WinUIXamlDesigner;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class WinUIXamlPasteTests
{
	const string Page = """
		<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
		  <StackPanel x:Name="RootPanel">
		    <StackPanel x:Name="Card"><TextBlock x:Name="Title1" Text="A"/><Button x:Name="Go"/></StackPanel>
		    <Button x:Name="Last"/>
		  </StackPanel>
		</Page>
		""";

	[Fact]
	public void DuplicateRenamesNestedNamesAndLandsAfterTheOriginal()
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(Page, out var error), error);
		var card = editor.FindElement("Card")!;
		var pasted = editor.Paste(card.ToString(SaveOptions.DisableFormatting), card.Parent!, card);

		var names = editor.ElementNames();
		Assert.Equal(names.Count, names.Distinct().Count());
		Assert.Contains("Title2", names);
		Assert.Contains("Go1", names);
		Assert.Equal(new[] { "Card", (string)pasted.Attribute(WinUIXamlDocumentEditor.NameDirective)!, "Last" },
			editor.FindElement("RootPanel")!.Elements().Select(e => (string)e.Attribute(WinUIXamlDocumentEditor.NameDirective)!));
		Assert.True(editor.CanUndo);
	}
}
