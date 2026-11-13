namespace ICSharpCode.SharpDevelop.Gui
{
	/// <summary>
	/// Marks an options panel that lays itself out to the height the Options dialog gives it (a panel whose main part is a list that scrolls on its own).
	/// The dialog then does not wrap the page in a vertical scroll bar, as long as the page has the height it needs; below that it scrolls the page again.
	/// </summary>
	public interface IFillsOptionPanelHeight
	{
		/// <summary>The height, in device-independent pixels, below which the page no longer fits and the dialog scrolls it as it does any other page.</summary>
		double MinimumContentHeight { get; }
	}
}
