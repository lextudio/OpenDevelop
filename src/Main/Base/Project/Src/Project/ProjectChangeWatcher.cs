// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
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
using System.Collections.Generic;
using System.IO;

#if !HAS_UNO
using System.Windows.Threading;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project.Commands;
using ICSharpCode.SharpDevelop.Workbench;
#endif

namespace ICSharpCode.SharpDevelop.Project
{
#if HAS_UNO
	sealed class ProjectChangeWatcher : IProjectChangeWatcher
	{
		public static IDisposable DeferEnabling() => null;

		public ProjectChangeWatcher(string fileName)
		{
		}
		
		public void Enable()
		{
		}
		
		public void Disable()
		{
		}
		
		public void Rename(string newFileName)
		{
		}
		
		public void Dispose()
		{
		}
	}
#else
	public sealed class ProjectChangeWatcher : IProjectChangeWatcher
	{
		/// <summary>Watchers whose start is postponed by <see cref="DeferEnabling"/>; null when not deferring.</summary>
		static List<ProjectChangeWatcher> deferredEnables;

		/// <summary>
		/// Postpones starting every watcher created or re-enabled until the returned scope is
		/// disposed, then starts them one per idle dispatcher turn. Starting a FileSystemWatcher is
		/// synchronous and slow on macOS (each one starts an FSEvents stream); opening a solution
		/// started one per project on the UI thread, which was about half the time of that freeze
		/// (doc/technotes/fast-mode.md step 3). A file changed while its watcher was postponed is
		/// still reported: <see cref="CompleteDeferredStart"/> compares the write time recorded when the
		/// watcher was created.
		/// </summary>
		public static IDisposable DeferEnabling()
		{
			SD.MainThread.VerifyAccess();
			if (deferredEnables != null)
				return null; // nested: the outer scope starts them
			deferredEnables = new List<ProjectChangeWatcher>();
			return new DeferralScope();
		}

		sealed class DeferralScope : IDisposable
		{
			bool disposed;
			public void Dispose()
			{
				if (disposed)
					return;
				disposed = true;
				var pending = new Queue<ProjectChangeWatcher>(deferredEnables);
				deferredEnables = null;
				StartNextDeferred(pending);
			}
		}

		static long deferredStartTicks;
		static int deferredStartCount;

		/// <summary>
		/// Starts the postponed watchers one after another. Starting a FileSystemWatcher costs ~75 ms
		/// on macOS (6.5 s for OpenDevelop.Mvp's 88 projects); only the state checks around it run on
		/// the UI thread, the start itself on the thread pool. While a watcher is starting in the
		/// background, SetWatcher and Dispose leave its FileSystemWatcher alone (it is not thread-safe)
		/// and CompleteDeferredStart reconciles afterwards.
		/// </summary>
		static void StartNextDeferred(Queue<ProjectChangeWatcher> pending)
		{
			while (pending.Count > 0) {
				var next = pending.Dequeue();
				var starting = System.Diagnostics.Stopwatch.StartNew();
				var fileSystemWatcher = next.BeginDeferredStart();
				deferredStartTicks += starting.Elapsed.Ticks;
				if (fileSystemWatcher == null)
					continue;
				System.Threading.Tasks.Task.Run(() => {
					try {
						fileSystemWatcher.EnableRaisingEvents = true;
						return (Exception)null;
					} catch (Exception ex) {
						return ex;
					}
				}).ContinueWith(start => SD.MainThread.InvokeAsyncAndForget(() => {
					var completing = System.Diagnostics.Stopwatch.StartNew();
					next.CompleteDeferredStart(fileSystemWatcher, start.Result);
					deferredStartTicks += completing.Elapsed.Ticks;
					deferredStartCount++;
					StartNextDeferred(pending);
				}, DispatcherPriority.Background));
				return;
			}
			PerfTimeline.Mark(PerfTimeline.SolutionOpen, "project-watchers-started",
				deferredStartCount + " watcher(s), " + TimeSpan.FromTicks(deferredStartTicks).TotalMilliseconds.ToString("0") + " ms on the UI thread");
			deferredStartTicks = 0;
			deferredStartCount = 0;
		}

		/// <summary>True while this watcher's FileSystemWatcher is being started on the thread pool.</summary>
		bool startingInBackground;
		/// <summary>A SetWatcher arrived while starting in the background; redo it afterwards.</summary>
		bool resyncAfterStart;

		/// <summary>The FileSystemWatcher to start in the background, or null if there is none to start.</summary>
		FileSystemWatcher BeginDeferredStart()
		{
			// Disposed, disabled, or already started again by a later SetWatcher in the meantime.
			if (disposed || !enabled || watcher == null || watcher.EnableRaisingEvents)
				return null;
			// Unlike the immediate start in SetWatcher, this runs later, when the project's directory
			// may already be gone (a solution closed and deleted, a temporary copy cleaned up):
			// starting then throws DirectoryNotFoundException, an IOException but not a
			// FileNotFoundException, which surfaced as an unhandled-exception dialog.
			if (!Directory.Exists(watcher.Path)) {
				watcher.Dispose();
				watcher = null;
				return null;
			}
			startingInBackground = true;
			return watcher;
		}

		void CompleteDeferredStart(FileSystemWatcher started, Exception error)
		{
			startingInBackground = false;
			if (disposed) {
				started.Dispose();
				if (watcher == started)
					watcher = null;
				return;
			}
			if (error != null) {
				if (!(error is PlatformNotSupportedException || error is IOException || error is ArgumentException || error is ObjectDisposedException))
					LoggingService.Warn("Starting the project file watcher for " + fileName + " failed: " + error.Message);
				started.Dispose();
				if (watcher == started)
					watcher = null;
				return;
			}
			if (resyncAfterStart) {
				resyncAfterStart = false;
				SetWatcher();
			}
			// A file changed while its watcher was postponed is still reported.
			if (enabled && LastWriteTimeHasChanged())
				OnFileChangedEvent(this, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(fileName), Path.GetFileName(fileName)));
		}

		static readonly HashSet<ProjectChangeWatcher> activeWatchers = new HashSet<ProjectChangeWatcher>();
		
		internal static void OnAllChangeWatchersDisabledChanged()
		{
			foreach (ProjectChangeWatcher watcher in activeWatchers)
				watcher.SetWatcher();
		}
		
		FileSystemWatcher watcher;
		string fileName;
		bool enabled = true;
		DateTime lastWriteTime;

		public ProjectChangeWatcher(string fileName)
		{
			this.fileName = fileName;
			
			SD.MainThread.VerifyAccess();
			activeWatchers.Add(this);
			UpdateLastWriteTime();
			
			SD.Workbench.MainWindow.Activated += MainFormActivated;
		}
		
		public void Enable()
		{
			enabled = true;
			SetWatcher();
		}

		public void Disable()
		{
			enabled = false;
			SetWatcher();
		}

		public void Rename(string newFileName)
		{
			fileName = newFileName;
		}
		
		void UpdateLastWriteTime()
		{
			// Save current last write time attribute
			FileInfo fileInfo = new FileInfo(fileName);
			if (fileInfo.Exists) {
				lastWriteTime = fileInfo.LastWriteTimeUtc;
			}
		}
		
		bool LastWriteTimeHasChanged()
		{
			// Save current last write time attribute
			FileInfo fileInfo = new FileInfo(fileName);
			if (fileInfo.Exists) {
				return lastWriteTime != fileInfo.LastWriteTimeUtc;
			}

			return true; // File might have been renamed/deleted?
		}

		void SetWatcher()
		{
			SD.MainThread.VerifyAccess();
			if (startingInBackground) {
				resyncAfterStart = true;
				return;
			}

			if (watcher != null) {
				watcher.EnableRaisingEvents = false;
			}

			if (!enabled || FileChangeWatcher.AllChangeWatchersDisabled)
				return;
			if (!FileChangeWatcher.DetectExternalChangesOption)
				return;
			if (string.IsNullOrEmpty(fileName))
				return;
			if (FileUtility.IsUrl(fileName))
				return;
			if (!Path.IsPathRooted(fileName))
				return;

			try {
				if (watcher == null) {
					watcher = new FileSystemWatcher();
					watcher.SynchronizingObject = SD.MainThread.SynchronizingObject;
					watcher.Changed += OnFileChangedEvent;
					watcher.Created += OnFileChangedEvent;
					watcher.Renamed += OnFileChangedEvent;
				}
				watcher.Path = Path.GetDirectoryName(fileName);
				watcher.Filter = Path.GetFileName(fileName);
				if (deferredEnables != null) {
					if (!deferredEnables.Contains(this))
						deferredEnables.Add(this);
				} else {
					watcher.EnableRaisingEvents = true;
				}
			} catch (PlatformNotSupportedException) {
				if (watcher != null) {
					watcher.Dispose();
				}
				watcher = null;
			} catch (IOException) {
				// can occur if the directory was deleted externally: FileNotFoundException, and on
				// macOS DirectoryNotFoundException from the FSEvents stream. The latter became
				// reachable once SetWatcher also runs after a background start
				// (CompleteDeferredStart), when the project's directory may already be gone.
				if (watcher != null) {
					watcher.Dispose();
				}
				watcher = null;
			} catch (ArgumentException) {
				// can occur if parent directory was deleted externally
				if (watcher != null) {
					watcher.Dispose();
				}
				watcher = null;
			}
		}

		static bool wasChangedExternally;

		void OnFileChangedEvent(object sender, FileSystemEventArgs e)
		{
			// Ignore this event, if LastWriteTime has changed to same value as before (= no real change in file)
			if ((e.ChangeType == WatcherChangeTypes.Changed) && !LastWriteTimeHasChanged()) {
				LoggingService.DebugFormatted("Attributes of project file {0} have been set externally ({1}), but no relevant changes detected.", e.Name, e.ChangeType);
				return;
			}
			
			LoggingService.DebugFormatted("Project file {0} was changed externally: {1}", e.Name, e.ChangeType);
			UpdateLastWriteTime();

			// SDK-style projects are routinely hand-edited - retargeting, package references - and the
			// file is the project, with no designer state that a reload could lose. Asking "the solution
			// was altered externally, reload?" every time turns a normal edit into a dialog, so apply the
			// change instead. Legacy projects keep the prompt: their in-memory model can hold state that
			// is not in the file, so silently dropping it would not be safe.
			if (IsSdkStyleProjectFile()) {
				if (!reloadQueued) {
					reloadQueued = true;
					// Same delay as the prompt below, and for the same reason: a writer may still be
					// mid-save, and editors often touch the file twice in quick succession.
					SD.MainThread.CallLater(TimeSpan.FromSeconds(0.5), ReloadAfterSdkStyleProjectChange);
				}
				return;
			}

			if (!wasChangedExternally) {
				wasChangedExternally = true;
				if (SD.Workbench.IsActiveWindow) {
					// delay reloading message a bit, prevents showing two messages
					// when the file changes twice in quick succession; and prevents
					// trying to reload the file while it is still being written
					SD.MainThread.CallLater(TimeSpan.FromSeconds(0.5), delegate { MainFormActivated(); });
				}
			}
		}

		bool reloadQueued;

		/// <summary>
		/// Re-reads the solution so an edited SDK-style project file takes effect, without prompting.
		/// </summary>
		void ReloadAfterSdkStyleProjectChange()
		{
			reloadQueued = false;
			var solution = ProjectService.OpenSolution;
			if (solution == null)
				return;
			// Re-reading the whole solution is what the reload prompt does too; there is no
			// single-project reload to call here.
			LoggingService.Info("Reloading solution after " + fileName + " changed.");
			SD.ProjectService.OpenSolutionOrProject(solution.FileName);
		}

		/// <summary>
		/// Whether the watched file is an SDK-style project (<c>&lt;Project Sdk="..."&gt;</c>).
		/// </summary>
		/// <remarks>
		/// Read as text rather than parsed as XML on purpose: this runs while another process may still
		/// be writing the file, and a partial write must not throw. Anything unreadable or unrecognised
		/// simply falls through to the existing prompt.
		/// </remarks>
		bool IsSdkStyleProjectFile()
		{
			if (string.IsNullOrEmpty(fileName))
				return false;
			string extension = Path.GetExtension(fileName);
			if (!".csproj".Equals(extension, StringComparison.OrdinalIgnoreCase)
			    && !".vbproj".Equals(extension, StringComparison.OrdinalIgnoreCase)
			    && !".fsproj".Equals(extension, StringComparison.OrdinalIgnoreCase))
				return false;

			try {
				using (var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				using (var reader = new StreamReader(stream)) {
					char[] buffer = new char[4096];
					int read = reader.Read(buffer, 0, buffer.Length);
					if (read <= 0)
						return false;
					string head = new string(buffer, 0, read);
					// Either the attribute form on the root element or the nested <Sdk .../> element.
					return head.IndexOf("Sdk=\"", StringComparison.OrdinalIgnoreCase) >= 0
						|| head.IndexOf("<Sdk ", StringComparison.OrdinalIgnoreCase) >= 0;
				}
			} catch (IOException) {
				return false;
			} catch (UnauthorizedAccessException) {
				return false;
			}
		}

		static bool showingMessageBox;
		
		static void MainFormActivated(object sender, EventArgs e)
		{
			// delay the event so that we don't interrupt the user if he's trying to close SharpDevelop
			SD.MainThread.InvokeAsyncAndForget(MainFormActivated, DispatcherPriority.Background);
		}
		
		static void MainFormActivated()
		{
			if (wasChangedExternally) {
				if (!showingMessageBox) {
					if (ProjectService.OpenSolution != null) {
						// Set wasChangedExternally=false only after the dialog is closed,
						// so that additional changes to the project while the dialog is open
						// don't cause it to appear twice.
						
						// The MainFormActivated event occurs when the dialog is closed before
						// we get a change to set wasChangedExternally=false, so we use 'showingMessageBox'
						// to prevent the dialog from appearing infititely.
						showingMessageBox = true;
						int result = MessageService.ShowCustomDialog(MessageService.DefaultMessageBoxTitle, "${res:ICSharpCode.SharpDevelop.Project.SolutionAlteredExternallyMessage}", 0, 1, "${res:ICSharpCode.SharpDevelop.Project.ReloadSolution}", "${res:ICSharpCode.SharpDevelop.Project.KeepOldSolution}", "${res:ICSharpCode.SharpDevelop.Project.CloseSolution}");
						showingMessageBox = false;
						wasChangedExternally = false;
						if (result == 1) {
							FileChangeWatcher.AskForReload();
						} else {
							FileChangeWatcher.CancelReloadQueue();
							if (result == 0) {
								SD.ProjectService.OpenSolutionOrProject(ProjectService.OpenSolution.FileName);
							} else {
								new CloseSolution().Run();
							}
						}
					} else {
						wasChangedExternally = false;
					}
				}
			} else {
				FileChangeWatcher.AskForReload();
			}
		}

		bool disposed;

		public void Dispose()
		{
			SD.MainThread.VerifyAccess();
			if (!disposed) {
				SD.Workbench.MainWindow.Activated -= MainFormActivated;
				activeWatchers.Remove(this);
			}
			// A background start owns the FileSystemWatcher until CompleteDeferredStart, which
			// disposes it once it sees `disposed`.
			if (watcher != null && !startingInBackground) {
				watcher.Dispose();
				watcher = null;
			}
			disposed = true;
		}
	}
#endif
}
