// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// Puts a project whose MSBuild reference resolution fails into the Error List, once per
	/// project, instead of only into the log. A failed resolution leaves the project in the Roslyn
	/// workspace with no package or framework references, so completion, navigation and
	/// diagnostics quietly degrade for it; 69 of OpenDevelop.Mvp's 87 projects were in that state
	/// with nothing visible saying so (doc/technotes/fast-mode.md, after C# Dev Kit's "C# Doctor").
	/// </summary>
	static class ReferenceResolutionDiagnostics
	{
		static readonly object gate = new object();
		static readonly Dictionary<string, SDTask> reported = new Dictionary<string, SDTask>(StringComparer.OrdinalIgnoreCase);

		static readonly Regex FirstError = new Regex(@"error (?<code>[A-Z]+[0-9]+):\s*(?<text>[^\[\r\n]*)", RegexOptions.CultureInvariant);

		/// <summary>Reports a failed resolution; <paramref name="output"/> is the child's output.</summary>
		public static void Failed(string projectFileName, string output)
		{
			var match = FirstError.Match(output ?? string.Empty);
			string reason = match.Success
				? match.Groups["code"].Value + ": " + match.Groups["text"].Value.Trim()
				: "MSBuild's ResolveReferences failed";
			string description = "Reference resolution failed (" + reason + "). Code completion, navigation and "
				+ "diagnostics for this project see no package or framework references until it succeeds.";
			SD.MainThread.InvokeAsyncAndForget(() => {
				lock (gate) {
					if (reported.ContainsKey(projectFileName))
						return;
					var task = new SDTask(FileName.Create(projectFileName), description, 0, 0, TaskType.Warning);
					reported[projectFileName] = task;
					TaskService.Add(task);
				}
			});
		}

		/// <summary>Withdraws the project's entry once its resolution succeeds.</summary>
		public static void Succeeded(string projectFileName)
		{
			lock (gate) {
				if (!reported.ContainsKey(projectFileName))
					return;
			}
			SD.MainThread.InvokeAsyncAndForget(() => {
				lock (gate) {
					if (reported.TryGetValue(projectFileName, out var task)) {
						reported.Remove(projectFileName);
						TaskService.Remove(task);
					}
				}
			});
		}

		/// <summary>Clears every entry; called when a solution opens, which resolves again.</summary>
		public static void Reset()
		{
			lock (gate) {
				foreach (var task in reported.Values)
					TaskService.Remove(task);
				reported.Clear();
			}
		}
	}
}
