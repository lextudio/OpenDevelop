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
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices.OpenLens;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.GitAddIn
{
	/// <summary>
	/// Registers <see cref="GitOpenLensProvider"/> against the shared
	/// <see cref="OpenLensProviderRegistry"/>, and wires up the refresh triggers doc
	/// §10.4 calls for: HEAD/index change (<see cref="GitHeadWatcher"/>, re-created per solution
	/// since the working copy root can change) and a file save (<see cref="FileUtility.FileSaved"/>,
	/// already used for the overlay-icon cache elsewhere in this AddIn).
	/// </summary>
	public sealed class RegisterGitOpenLensProviderCommand : AbstractCommand, IDisposable
	{
		OpenLensProviderRegistry registry;
		IDisposable registration;
		GitHeadWatcher headWatcher;

		public override void Run()
		{
			registry = SD.GetRequiredService<OpenLensProviderRegistry>();
			registration = registry.RegisterProvider(new GitOpenLensProvider());

			SD.ProjectService.SolutionOpened += OnSolutionOpened;
			SD.ProjectService.SolutionClosed += OnSolutionClosed;
			FileUtility.FileSaved += OnFileSaved;
			if (SD.ProjectService.CurrentSolution != null)
				CreateWatcherFor(SD.ProjectService.CurrentSolution.Directory);
		}

		void OnSolutionOpened(object sender, SolutionEventArgs e) => CreateWatcherFor(e.Solution.Directory);

		void OnSolutionClosed(object sender, SolutionEventArgs e) => DisposeWatcher();

		void CreateWatcherFor(DirectoryName solutionDirectory)
		{
			DisposeWatcher();
			string wcRoot = Git.FindWorkingCopyRoot(solutionDirectory);
			if (wcRoot == null)
				return;
			try {
				headWatcher = new GitHeadWatcher(wcRoot);
				headWatcher.Changed += OnHeadChanged;
			} catch (Exception ex) {
				// A submodule/linked worktree's ".git" is a file, not a directory
				// (GitHeadWatcher's own doc comment) - FileSystemWatcher throws for that. No HEAD-
				// change refresh in that case; the file-saved trigger below still works.
				LoggingService.Warn("GitOpenLens: couldn't watch '" + wcRoot + "/.git' for HEAD changes. " + ex.Message);
			}
		}

		void DisposeWatcher()
		{
			if (headWatcher != null) {
				headWatcher.Changed -= OnHeadChanged;
				headWatcher.Dispose();
				headWatcher = null;
			}
		}

		void OnHeadChanged(object sender, EventArgs e) =>
			registry.RequestRefresh(new OpenLensRefreshEventArgs("Git"));

		void OnFileSaved(object sender, FileNameEventArgs e) =>
			registry.RequestRefresh(new OpenLensRefreshEventArgs("Git"));

		public void Dispose()
		{
			SD.ProjectService.SolutionOpened -= OnSolutionOpened;
			SD.ProjectService.SolutionClosed -= OnSolutionClosed;
			FileUtility.FileSaved -= OnFileSaved;
			DisposeWatcher();
			registration?.Dispose();
		}
	}
}
