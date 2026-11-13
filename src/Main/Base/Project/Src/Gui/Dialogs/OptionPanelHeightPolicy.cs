using System.Windows.Controls;

namespace ICSharpCode.SharpDevelop.Gui
{
	/// <summary>
	/// How the Options dialog lays out a page that fills the height (<see cref="IFillsOptionPanelHeight"/>): scrolled by its own list while it fits,
	/// or scrolled as a whole at a fixed height when it does not. The decision is separate from the dialog so that it can be tested.
	/// </summary>
	public static class OptionPanelHeightPolicy
	{
		/// <summary>The decision: the dialog's vertical scroll bar, and the height to give the page (NaN: the viewer's).</summary>
		public struct Decision
		{
			public ScrollBarVisibility VerticalScrollBar;
			public double ContentHeight;
		}

		/// <param name="availableHeight">The height the viewer has for the page (its own height minus the page title).</param>
		/// <param name="minimumContentHeight">The page's <see cref="IFillsOptionPanelHeight.MinimumContentHeight"/>; null for an ordinary page.</param>
		public static Decision Decide(double availableHeight, double? minimumContentHeight)
		{
			if (minimumContentHeight == null)
				return new Decision { VerticalScrollBar = ScrollBarVisibility.Auto, ContentHeight = double.NaN };
			if (availableHeight >= minimumContentHeight.Value)
				return new Decision { VerticalScrollBar = ScrollBarVisibility.Disabled, ContentHeight = double.NaN };
			// A scrolling viewer measures its content with no height limit, so a page whose list takes "the rest" would grow to the length of the list:
			// give it its minimum height, the viewer then scrolls the page and the list inside keeps a normal size.
			return new Decision { VerticalScrollBar = ScrollBarVisibility.Auto, ContentHeight = minimumContentHeight.Value };
		}
	}
}
