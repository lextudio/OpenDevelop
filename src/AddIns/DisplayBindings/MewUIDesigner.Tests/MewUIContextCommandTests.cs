using System.Linq;
using ICSharpCode.MewUIDesigner;
using Xunit;

public sealed class MewUIContextCommandTests
{
	[Fact]
	public void EveryLayoutMenuEntryMapsToWireValues()
	{
		foreach (var (header, command) in MewUIContextCommands.LayoutMenu) {
			var alignment = MewUIContextCommands.Alignment(command);
			var track = MewUIContextCommands.GridTrack(command);
			Assert.True(alignment != null ^ track != null, header + " -> " + command);
		}
		Assert.Equal(MewUIContextCommands.LayoutMenu.Length, MewUIContextCommands.LayoutMenu.Select(entry => entry.Command).Distinct().Count());
	}

	[Fact]
	public void AlignmentAndGridCommandsMapToTheRightProperties()
	{
		Assert.Equal(("HorizontalAlignment", "Center"), MewUIContextCommands.Alignment("set:HorizontalAlignment:Center"));
		Assert.Equal(("VerticalAlignment", "Bottom"), MewUIContextCommands.Alignment("set:VerticalAlignment:Bottom"));
		Assert.Null(MewUIContextCommands.Alignment("set:VerticalAlignment:Left"));
		Assert.Null(MewUIContextCommands.Alignment("set:Width:10"));
		Assert.Equal((true, "Pixel"), MewUIContextCommands.GridTrack("grid-row-Pixel"));
		Assert.Equal((false, "Star"), MewUIContextCommands.GridTrack("grid-column-Star"));
		Assert.Equal((true, "Auto"), MewUIContextCommands.GridTrack("grid-row-Auto"));
		Assert.Null(MewUIContextCommands.GridTrack("grid-row-Fixed"));
		Assert.Null(MewUIContextCommands.GridTrack("grid-cell-Auto"));
	}

	[Fact]
	public void OnlyNamedIdsAreAddressable()
	{
		Assert.True(MewUIContextCommands.IsNamedId("saveButton"));
		Assert.False(MewUIContextCommands.IsNamedId("#0,1"));
		Assert.False(MewUIContextCommands.IsNamedId(""));
	}
}
