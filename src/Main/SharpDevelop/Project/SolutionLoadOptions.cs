// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// Environment switches for solution loading (doc/technotes/fast-mode.md). Each is on by default;
	/// setting the variable to <c>0</c> restores the previous behavior, for comparison or as a workaround.
	/// </summary>
	static class SolutionLoadOptions
	{
		/// <summary><c>OD_ASYNC_SOLUTION_LOAD</c>: build solutions off the UI thread.</summary>
		public static bool AsyncLoad => IsEnabled("OD_ASYNC_SOLUTION_LOAD");

		/// <summary><c>OD_SHARED_EVALUATION</c>: one MSBuild EvaluationContext for all projects of a load.</summary>
		public static bool SharedEvaluation => IsEnabled("OD_SHARED_EVALUATION");

		/// <summary><c>OD_DEFER_PROJECT_WATCHERS</c>: start project file watchers after the solution-opened handlers.</summary>
		public static bool DeferProjectWatchers => IsEnabled("OD_DEFER_PROJECT_WATCHERS");

		/// <summary>
		/// Test only (set through DevFlow's od.test.solution-publish-delay): how long a finished background build
		/// waits before it is published. It widens the window in which a newer open or a close must invalidate it.
		/// </summary>
		public static TimeSpan TestPublishDelay { get; set; }

		static bool IsEnabled(string variable) => Environment.GetEnvironmentVariable(variable) != "0";
	}
}
