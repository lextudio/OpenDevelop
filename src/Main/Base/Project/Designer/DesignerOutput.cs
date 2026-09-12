// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Designer
{
	/// <summary>
	/// Where designer diagnostics go in the Output pad.
	///
	/// Two levels, deliberately: each designer writes to a channel named after itself, so everything
	/// about one designer - the IDE-side decisions AND its isolated child's own output - reads as a
	/// single story; while anything not tied to a particular designer goes to the shared IDE channel.
	///
	/// This exists because the interesting lines were previously invisible. An isolated designer host
	/// reports what it had to repair, substitute or give up on ("could not resolve XAML type ...",
	/// "substituted a panel for ...", "serving app resources from ..."), and all of it went to the
	/// child's stderr where only a DevFlow action could reach it. The user saw one parser error on
	/// the design surface - routinely the misleading one - with no way to see what led to it.
	/// </summary>
	public static class DesignerOutput
	{
		/// <summary>The channel for one designer, named as the user knows it ("WinUI Designer").
		/// Created on first use.</summary>
		public static IOutputCategory Channel(string designerDisplayName)
			=> SD.OutputPad.GetOrCreateCategory(designerDisplayName);

		/// <summary>The shared channel for messages that belong to no single designer - host process
		/// lifecycle, and anything else at IDE level.</summary>
		public static IOutputCategory Ide => SD.OutputPad.GetOrCreateCategory("IDE");

		/// <summary>Appends one line. Never throws and never steals focus: these are diagnostics the
		/// user consults after noticing something, not alerts worth interrupting an edit for.</summary>
		public static void AppendLine(IOutputCategory category, string text)
		{
			try
			{
				category.AppendLine(text);
			}
			catch (System.Exception)
			{
				// The Output pad is a convenience here; losing a diagnostic line must never take down
				// the designer or the host that produced it.
			}
		}
	}
}
