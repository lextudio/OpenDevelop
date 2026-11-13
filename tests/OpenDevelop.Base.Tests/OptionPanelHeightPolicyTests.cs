using System.Windows.Controls;
using ICSharpCode.SharpDevelop.Gui;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>The Options dialog's decision for a page that fills the height: its own list scrolls while it fits, the page scrolls at a fixed height when it does not.</summary>
public sealed class OptionPanelHeightPolicyTests
{
	[Fact]
	public void AnOrdinaryPage_KeepsTheDialogsScrollingAndHeight()
	{
		var decision = OptionPanelHeightPolicy.Decide(300, null);
		Assert.Equal(ScrollBarVisibility.Auto, decision.VerticalScrollBar);
		Assert.True(double.IsNaN(decision.ContentHeight));
	}

	[Theory]
	[InlineData(320)]
	[InlineData(500)]
	public void APageThatFits_ScrollsOnlyItsOwnList(double available)
	{
		var decision = OptionPanelHeightPolicy.Decide(available, 320);
		Assert.Equal(ScrollBarVisibility.Disabled, decision.VerticalScrollBar);
		Assert.True(double.IsNaN(decision.ContentHeight));
	}

	[Fact]
	public void APageThatDoesNotFit_ScrollsAsAWhole_AtItsMinimumHeight()
	{
		// 336 px available for a page that needs 426 (the open "not loaded" section): the viewer scrolls the page, the list does not grow to its full length
		var decision = OptionPanelHeightPolicy.Decide(336, 426);
		Assert.Equal(ScrollBarVisibility.Auto, decision.VerticalScrollBar);
		Assert.Equal(426, decision.ContentHeight);
	}

	[Fact]
	public void TheDecisionFollowsTheMinimum_WhenASectionOpensOrCloses()
	{
		const double available = 336; // the viewer at the dialog's minimum height
		Assert.Equal(ScrollBarVisibility.Disabled, OptionPanelHeightPolicy.Decide(available, 320).VerticalScrollBar);
		var shown = OptionPanelHeightPolicy.Decide(available, 320 + 26); // the section is shown: 346 px needed
		Assert.Equal(ScrollBarVisibility.Auto, shown.VerticalScrollBar);
		Assert.Equal(346, shown.ContentHeight);
		Assert.Equal(ScrollBarVisibility.Disabled, OptionPanelHeightPolicy.Decide(available + 100, 320 + 26 + 80).VerticalScrollBar); // a taller dialog fits it open
	}
}
