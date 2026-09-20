// Copyright (c) 2026 SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:

using System;

namespace ICSharpCode.SharpDevelop.Workbench
{
	/// <summary>
	/// Why the workbench's effective activation context changed.
	/// </summary>
	public enum WorkbenchContextChangeReason
	{
		DockActivation,
		ViewActivation,
		LayoutAttached
	}

	/// <summary>
	/// Immutable snapshot of the workbench activation state. Published on
	/// <c>MessageBus&lt;WorkbenchContextChangedEventArgs&gt;</c> by the workbench after it has updated
	/// its public active-content properties. <see cref="ActiveDockContent"/> may be a tool-pane
	/// model and is intentionally typed as <see cref="object"/> so this base contract does not take
	/// a dependency on AvalonDock or any AddIn.
	/// </summary>
	public sealed class WorkbenchContextChangedEventArgs : EventArgs
	{
		public WorkbenchContextChangedEventArgs(
			IWorkbenchWindow activeWorkbenchWindow,
			IViewContent activeViewContent,
			IServiceProvider activeContent,
			object activeDockContent,
			WorkbenchContextChangeReason reason,
			long revision)
		{
			ActiveWorkbenchWindow = activeWorkbenchWindow;
			ActiveViewContent = activeViewContent;
			ActiveContent = activeContent;
			ActiveDockContent = activeDockContent;
			Reason = reason;
			Revision = revision;
		}

		public IWorkbenchWindow ActiveWorkbenchWindow { get; }
		public IViewContent ActiveViewContent { get; }
		public IServiceProvider ActiveContent { get; }
		public object ActiveDockContent { get; }
		public WorkbenchContextChangeReason Reason { get; }
		public long Revision { get; }
	}
}
