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
		static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IOutputCategory> channels
			= new System.Collections.Concurrent.ConcurrentDictionary<string, IOutputCategory>(System.StringComparer.Ordinal);

		/// <summary>The channel for one designer, named as the user knows it ("WinUI Designer").
		/// Created on first use.
		///
		/// Cached on purpose: <c>OutputPad.GetOrCreateCategory</c> marshals synchronously to the UI
		/// thread even when the category already exists, and the child-process output pump calls this
		/// from a thread-pool thread for every line the host writes. When the UI thread is itself
		/// blocked waiting for that host (a synchronous RPC, e.g. WpfViewContent.FlushCurrentXaml), the
		/// pump stops reading, the host's stderr pipe fills and the host blocks in Console.Error.WriteLine:
		/// a three-way deadlock that left Microsoft WPF Gallery pages unable to load for 60 s. A designer
		/// therefore has to ask for its channel once on the UI thread before its host exists (the WPF
		/// designer does, with its first "Opening ..." line; the WinUI host does at the start of
		/// ConnectAsync), so that the pump only ever finds it cached. The first call from any other thread
		/// still marshals once.
		///
		/// Not removed by this: the Output pad queues appends to the UI thread and, past a couple of
		/// thousand queued appends, makes the calling (pump) thread wait for the UI thread to drain them,
		/// so a UI thread parked on a synchronous host RPC can still stall the pump after a very large
		/// burst of host output.</summary>
		public static IOutputCategory Channel(string designerDisplayName)
		{
			if (channels.TryGetValue(designerDisplayName, out var cached))
				return cached;
			return channels.GetOrAdd(designerDisplayName, name => SD.OutputPad.GetOrCreateCategory(name));
		}

		/// <summary>The shared channel for messages that belong to no single designer - host process
		/// lifecycle, and anything else at IDE level.</summary>
		public static IOutputCategory Ide => Channel("IDE");

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
