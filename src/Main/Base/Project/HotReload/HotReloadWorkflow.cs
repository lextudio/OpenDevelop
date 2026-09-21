using System;

namespace ICSharpCode.SharpDevelop.Project.HotReload
{
	/// <summary>What the Apply gesture actually has to do for the current adapter.</summary>
	public enum HotReloadApplyAction
	{
		/// <summary>No live session, or the document cannot be applied - the command is disabled.</summary>
		None,

		/// <summary>Send the document to the framework and report what it answered.</summary>
		Apply,

		/// <summary>
		/// Save the document and let the framework notice. The only honest action for an adapter
		/// whose framework watches the filesystem: the IDE has nothing to transmit.
		/// </summary>
		Save,
	}

	/// <summary>
	/// The capability-driven decisions behind the Hot Reload commands, kept free of workbench
	/// statics so they can be tested directly. The UI must never offer an action the resolved
	/// adapter cannot perform, which is the whole reason this is a decision and not a fixed command.
	/// </summary>
	public static class HotReloadWorkflow
	{
		public static HotReloadApplyAction DecideApplyAction(IHotReloadSession session)
		{
			if (session == null)
				return HotReloadApplyAction.None;

			switch (session.State) {
				case HotReloadSessionState.Disconnected:
				case HotReloadSessionState.Stopped:
				case HotReloadSessionState.Unsupported:
				case HotReloadSessionState.NotStarted:
					return HotReloadApplyAction.None;
			}

			return session.Capabilities.CanApplyFromIde ? HotReloadApplyAction.Apply : HotReloadApplyAction.Save;
		}

		/// <summary>
		/// The label the command shows. "Apply" would be a lie for a framework-driven adapter, and
		/// a user who clicked it and saw nothing happen would have no way to know why.
		/// </summary>
		public static string DescribeApplyAction(HotReloadApplyAction action)
		{
			switch (action) {
				case HotReloadApplyAction.Apply:
					return "${res:XML.MainMenu.DebugMenu.ApplyHotReload}";
				case HotReloadApplyAction.Save:
					return "${res:XML.MainMenu.DebugMenu.SaveForHotReload}";
				default:
					return "${res:XML.MainMenu.DebugMenu.ApplyHotReload}";
			}
		}

		/// <summary>
		/// Whether an unsaved editor buffer may be sent as-is. When the framework reads the file
		/// from disk, applying buffer text would report success for something the framework never
		/// saw, so the document has to be saved first.
		/// </summary>
		public static bool MustSaveBeforeApply(IHotReloadSession session)
			=> session != null && session.Capabilities.RequiresSavedFile;

		/// <summary>
		/// A conservative classification. The adapters treat this as a hint about the smallest safe
		/// update and are free to ignore it, so guessing narrowly is a correctness risk while
		/// guessing broadly only costs efficiency - hence FullDocument whenever anything structural
		/// might have changed.
		/// </summary>
		public static HotReloadChangeKind Classify(string previousText, string currentText)
		{
			if (previousText == null || currentText == null)
				return HotReloadChangeKind.FullDocument;
			if (string.Equals(previousText, currentText, StringComparison.Ordinal))
				return HotReloadChangeKind.FullDocument;

			// Same element structure, different attribute text: treat as a property edit. Counting
			// tags is deliberately crude - it only has to be right about "nothing was added or
			// removed", because everything else falls back to the full document.
			return CountElements(previousText) == CountElements(currentText)
				? HotReloadChangeKind.Property
				: HotReloadChangeKind.Subtree;
		}

		static int CountElements(string xaml)
		{
			var count = 0;
			for (var i = 0; i < xaml.Length; i++) {
				if (xaml[i] != '<')
					continue;
				if (i + 1 < xaml.Length && (xaml[i + 1] == '/' || xaml[i + 1] == '!' || xaml[i + 1] == '?'))
					continue;
				count++;
			}
			return count;
		}
	}
}
