using System;
using System.IO;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Project
{
	public sealed class ProjectFileChangeWatcher : IProjectChangeWatcher
	{
		readonly IMessageLoop messageLoop;
		FileSystemWatcher watcher;
		string fileName;
		bool enabled = true;
		DateTime lastWriteTimeUtc;

		public ProjectFileChangeWatcher(string fileName)
		{
			this.messageLoop = SD.Services.GetService<IMessageLoop>();
			this.fileName = fileName;
			UpdateLastWriteTime();
			SetWatcher();
		}

		public event EventHandler<FileRenameEventArgs> ChangedExternally;

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
			UpdateLastWriteTime();
			SetWatcher();
		}

		public void Dispose()
		{
			if (watcher == null)
				return;

			watcher.EnableRaisingEvents = false;
			watcher.Dispose();
			watcher = null;
		}

		void SetWatcher()
		{
			if (watcher != null)
				watcher.EnableRaisingEvents = false;

			if (!enabled || string.IsNullOrWhiteSpace(fileName) || !Path.IsPathRooted(fileName))
				return;

			string directory = Path.GetDirectoryName(fileName);
			string filter = Path.GetFileName(fileName);
			if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(filter) || !Directory.Exists(directory))
				return;

			try {
				if (watcher == null)
					watcher = new FileSystemWatcher();

				if (messageLoop?.SynchronizingObject != null)
					watcher.SynchronizingObject = messageLoop.SynchronizingObject;

				watcher.Path = directory;
				watcher.Filter = filter;
				watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName;
				watcher.Changed -= OnWatcherChanged;
				watcher.Created -= OnWatcherChanged;
				watcher.Deleted -= OnWatcherChanged;
				watcher.Renamed -= OnWatcherRenamed;
				watcher.Changed += OnWatcherChanged;
				watcher.Created += OnWatcherChanged;
				watcher.Deleted += OnWatcherChanged;
				watcher.Renamed += OnWatcherRenamed;
				watcher.EnableRaisingEvents = true;
			} catch (PlatformNotSupportedException) {
				DisposeWatcher();
			} catch (FileNotFoundException) {
				DisposeWatcher();
			} catch (ArgumentException) {
				DisposeWatcher();
			}
		}

		void DisposeWatcher()
		{
			if (watcher == null)
				return;

			watcher.Dispose();
			watcher = null;
		}

		void OnWatcherChanged(object sender, FileSystemEventArgs e)
		{
			if (!HasMeaningfulWriteTimeChange() && e.ChangeType == WatcherChangeTypes.Changed)
				return;

			LoggingService.DebugFormatted("Project file watcher noticed external change for {0}: {1}", e.FullPath, e.ChangeType);
			UpdateLastWriteTime();
			ChangedExternally?.Invoke(this, new FileRenameEventArgs(fileName, fileName, isDirectory: false));
		}

		void OnWatcherRenamed(object sender, RenamedEventArgs e)
		{
			LoggingService.DebugFormatted("Project file watcher noticed external rename for {0}: {1}", e.OldFullPath, e.FullPath);
			fileName = e.FullPath;
			UpdateLastWriteTime();
			ChangedExternally?.Invoke(this, new FileRenameEventArgs(e.OldFullPath, e.FullPath, isDirectory: false));
			SetWatcher();
		}

		void UpdateLastWriteTime()
		{
			lastWriteTimeUtc = File.Exists(fileName)
				? File.GetLastWriteTimeUtc(fileName)
				: DateTime.MinValue;
		}

		bool HasMeaningfulWriteTimeChange()
		{
			if (!File.Exists(fileName))
				return true;

			return File.GetLastWriteTimeUtc(fileName) != lastWriteTimeUtc;
		}
	}
}
