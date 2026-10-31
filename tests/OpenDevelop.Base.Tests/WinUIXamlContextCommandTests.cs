using System.Linq;
using System.Xml.Linq;
using ICSharpCode.WinUIXamlDesigner;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class WinUIXamlContextCommandTests
{
	[Fact]
	public void EveryMenuEntryHasAHandlerAndNoCommandRepeats()
	{
		var commands = WinUIXamlContextCommands.Menu.Select(entry => entry.Command).Where(command => command.Length > 0).ToArray();
		Assert.All(commands, command => Assert.True(WinUIXamlContextCommands.IsHandled(command), "no handler for " + command));
		Assert.Equal(commands.Length, commands.Distinct().Count());
		Assert.All(WinUIXamlContextCommands.Menu.Where(entry => entry.Header != "-"), entry => Assert.NotEqual("", entry.Command));
	}

	[Fact]
	public void WrapCommandsMapToContainersAndAlignmentsAreValidated()
	{
		Assert.Equal("Viewbox", WinUIXamlContextCommands.WrapContainer("wrap-viewbox"));
		Assert.Equal("Canvas", WinUIXamlContextCommands.WrapContainer("wrap-canvas"));
		Assert.Null(WinUIXamlContextCommands.WrapContainer("wrap-bogus"));
		Assert.Equal(("VerticalAlignment", "Bottom"), WinUIXamlContextCommands.Alignment("set:VerticalAlignment:Bottom"));
		Assert.Null(WinUIXamlContextCommands.Alignment("set:VerticalAlignment:Left"));
		Assert.Null(WinUIXamlContextCommands.Alignment("set:Width:10"));
	}

	[Fact]
	public void WrapInViewboxCanBeUnwrappedInOneStepEach()
	{
		const string page = """
			<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
			  <StackPanel x:Name="Panel"><Button x:Name="A"/></StackPanel>
			</Page>
			""";
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(page, out var error), error);
		var wrapper = editor.WrapInContainer(editor.FindElement("A")!, "Viewbox");
		Assert.NotNull(wrapper);
		Assert.Equal("Viewbox", wrapper!.Name.LocalName);
		Assert.True(editor.Unwrap(wrapper));
		Assert.Equal("StackPanel", editor.FindElement("A")!.Parent!.Name.LocalName);
		Assert.True(editor.Undo());
		Assert.Equal("Viewbox", editor.FindElement("A")!.Parent!.Name.LocalName);
	}
}
