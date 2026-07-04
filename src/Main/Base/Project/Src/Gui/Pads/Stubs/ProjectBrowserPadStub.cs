// MVP stub: the real ProjectBrowserPad (WinForms ExtTreeView-based Solution Explorer) is out of
// MVP scope per docs/opendevelop.md - Solution Explorer will eventually be rebuilt WPF+CPS-style (R6).
// This minimal stand-in keeps the handful of call sites elsewhere in Base (which only ever call the
// static RefreshViewAsync() to ask for a repaint after a project change) compiling without pulling in
// the excluded WinForms/ExtTreeView tree UI.
using System;

namespace ICSharpCode.SharpDevelop.Project
{
	public static class ProjectBrowserPad
	{
		public static void RefreshViewAsync()
		{
			// no-op: Solution Explorer UI is not present in this MVP build.
		}
	}
}
