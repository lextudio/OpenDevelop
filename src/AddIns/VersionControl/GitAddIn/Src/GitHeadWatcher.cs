// Copyright (c) 2025 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.IO;
using System.Threading;
using ICSharpCode.Core;

namespace ICSharpCode.GitAddIn
{
	/// <summary>
	/// Watches one working copy's <c>.git/HEAD</c> and <c>.git/index</c> for changes - GitAddIn had
	/// no HEAD/branch-change/post-commit signal at all before this (doc/technotes/openlens.md §10.4
	/// lists "HEAD change, index/worktree change, ... branch switch" as required refresh triggers for
	/// the Git lens). A checkout/commit/branch-switch touches at least one of these two files, so
	/// watching them (rather than hooking every git-mutating code path individually) catches changes
	/// made outside this process too (an external `git commit`, a terminal `git checkout`), not just
	/// ones this AddIn itself initiated.
	///
	/// Debounced (400ms) because a single `git commit` touches both files, sometimes with more than
	/// one write each (locking temp files, then the rename) - without debouncing that would fire
	/// several redundant refreshes per commit.
	/// </summary>
	public sealed class GitHeadWatcher : IDisposable
	{
		readonly FileSystemWatcher watcher;
		readonly System.Threading.Timer debounceTimer;

		public event EventHandler Changed;

		public GitHeadWatcher(string workingCopyRoot)
		{
			string gitDir = Path.Combine(workingCopyRoot, ".git");
			debounceTimer = new System.Threading.Timer(_ => Changed?.Invoke(this, EventArgs.Empty), null, Timeout.Infinite, Timeout.Infinite);

			watcher = new FileSystemWatcher(gitDir) {
				// A plain ".git" directory contains HEAD/index directly; a submodule/worktree's
				// ".git" is a *file*, not a directory - the FileSystemWatcher constructor above
				// would already have thrown for that case, so this class simply isn't usable for
				// submodules/linked worktrees (a known, narrower-than-Git.FindWorkingCopyRoot
				// limitation, not attempted here).
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
			};
			watcher.Changed += OnFileSystemEvent;
			watcher.Created += OnFileSystemEvent;
			watcher.Renamed += OnFileSystemEvent;
			watcher.EnableRaisingEvents = true;
		}

		void OnFileSystemEvent(object sender, FileSystemEventArgs e)
		{
			if (!string.Equals(e.Name, "HEAD", StringComparison.OrdinalIgnoreCase)
				&& !string.Equals(e.Name, "index", StringComparison.OrdinalIgnoreCase))
				return;
			debounceTimer.Change(400, Timeout.Infinite);
		}

		public void Dispose()
		{
			watcher.EnableRaisingEvents = false;
			watcher.Changed -= OnFileSystemEvent;
			watcher.Created -= OnFileSystemEvent;
			watcher.Renamed -= OnFileSystemEvent;
			watcher.Dispose();
			debounceTimer.Dispose();
		}
	}
}
