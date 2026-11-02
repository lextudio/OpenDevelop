using ICSharpCode.MewUIDesigner;
using Xunit;

// The alignment/Grid-unit/move/wrap vocabulary is tested once, in OpenDevelop.Base.Tests (DesignerStandardCommandsTests).
public sealed class MewUIContextCommandTests
{
	[Fact]
	public void OnlyNamedIdsAreAddressable()
	{
		Assert.True(MewUIContextCommands.IsNamedId("saveButton"));
		Assert.False(MewUIContextCommands.IsNamedId("#0,1"));
		Assert.False(MewUIContextCommands.IsNamedId(""));
	}
}
