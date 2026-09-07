// Copyright (c) 2026 Christoph Wille
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
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ICSharpCode.ILSpy.Processes;

namespace ICSharpCode.ILSpy.ViewModels
{
	public sealed partial class OpenFromProcessDialogViewModel : ViewModelBase
	{
		readonly IProcessExplorer explorer;
		readonly List<ProcessRowViewModel> allProcesses = new();

		CancellationTokenSource refreshCts;
		CancellationTokenSource modulesCts;
		int modulesGeneration;

		string entryAssemblyPath;

		[ObservableProperty]
		string filterText = string.Empty;

		[ObservableProperty]
		[NotifyCanExecuteChangedFor(nameof(AddEntryAssemblyCommand))]
		ProcessRowViewModel selectedProcess;

		[ObservableProperty]
		bool isLoadingProcesses;

		[ObservableProperty]
		bool isLoadingModules;

		[ObservableProperty]
		string errorMessage;

		public ObservableCollection<ProcessRowViewModel> Processes { get; } = new();

		public ObservableCollection<ProcessModuleRowViewModel> Modules { get; } = new();

		public ObservableCollection<ProcessModuleRowViewModel> SelectedModules { get; } = new();

		public event Action<string[]> CloseRequested;

		public OpenFromProcessDialogViewModel(IProcessExplorer explorer)
		{
			this.explorer = explorer;
			SelectedModules.CollectionChanged += (_, _) => AddSelectedModulesCommand.NotifyCanExecuteChanged();
		}

		partial void OnFilterTextChanged(string value) => ApplyFilter();

		partial void OnSelectedProcessChanged(ProcessRowViewModel value)
			=> LoadModulesAsync(value).HandleExceptions();

		[RelayCommand]
		void Refresh() => RefreshAsync().HandleExceptions();

		bool CanAddSelectedModules => SelectedModules.Any(m => !m.IsInMemory);

		[RelayCommand(CanExecute = nameof(CanAddSelectedModules))]
		void AddSelectedModules()
		{
			var paths = SelectedModules
				.Where(m => !m.IsInMemory && m.Path != null)
				.Select(m => m.Path)
				.ToArray();
			if (paths.Length > 0)
				CloseRequested?.Invoke(paths);
		}

		bool CanAddEntryAssembly => entryAssemblyPath != null;

		[RelayCommand(CanExecute = nameof(CanAddEntryAssembly))]
		void AddEntryAssembly()
		{
			if (entryAssemblyPath != null)
				CloseRequested?.Invoke(new[] { entryAssemblyPath });
		}

		public void CancelAllOperations()
		{
			CancelAndDispose(ref refreshCts);
			CancelAndDispose(ref modulesCts);
		}

		static void CancelAndDispose(ref CancellationTokenSource source)
		{
			var previous = source;
			source = null;
			previous?.Cancel();
			previous?.Dispose();
		}

		async Task RefreshAsync()
		{
			CancelAndDispose(ref refreshCts);
			var cts = refreshCts = new CancellationTokenSource();
			IsLoadingProcesses = true;
			try
			{
				var processes = await explorer.GetProcessesAsync(cts.Token);
				if (cts.IsCancellationRequested)
					return;

				ErrorMessage = null;
				ReplaceProcesses(processes);
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				ErrorMessage = ex.Message;
				ReplaceProcesses(Array.Empty<RunningDotNetProcess>());
			}
			finally
			{
				if (!cts.IsCancellationRequested)
					IsLoadingProcesses = false;
			}
		}

		void ReplaceProcesses(IReadOnlyList<RunningDotNetProcess> processes)
		{
			SelectedProcess = null;
			allProcesses.Clear();
			allProcesses.AddRange(processes.Select(p => new ProcessRowViewModel(p)));
			ApplyFilter();
		}

		void ApplyFilter()
		{
			var matching = allProcesses.Where(p => p.Matches(FilterText)).ToList();
			var admitted = new HashSet<ProcessRowViewModel>(matching);
			if (SelectedProcess != null && !admitted.Contains(SelectedProcess))
				SelectedProcess = null;
			for (int i = Processes.Count - 1; i >= 0; i--)
			{
				if (!admitted.Contains(Processes[i]))
					Processes.RemoveAt(i);
			}
			for (int i = 0; i < matching.Count; i++)
			{
				if (i >= Processes.Count)
					Processes.Add(matching[i]);
				else if (!ReferenceEquals(Processes[i], matching[i]))
					Processes.Insert(i, matching[i]);
			}
		}

		async Task LoadModulesAsync(ProcessRowViewModel process)
		{
			CancelAndDispose(ref modulesCts);
			var cts = modulesCts = new CancellationTokenSource();
			int generation = ++modulesGeneration;

			Modules.Clear();
			SelectedModules.Clear();
			SetEntryAssemblyPath(null);
			if (process == null)
			{
				IsLoadingModules = false;
				return;
			}

			IsLoadingModules = true;
			try
			{
				var modules = await explorer.GetModulesAsync(process.Process, cts.Token);
				if (generation != modulesGeneration)
					return;

				ErrorMessage = null;
				foreach (var module in modules)
					Modules.Add(new ProcessModuleRowViewModel(module));
				SetEntryAssemblyPath(process.Process.ResolveEntryAssemblyPath(modules));
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				if (generation != modulesGeneration)
					return;
				ErrorMessage = ex.Message;
				SetEntryAssemblyPath(process.Process.ResolveEntryAssemblyPath(Array.Empty<ProcessModuleInfo>()));
			}
			finally
			{
				if (generation == modulesGeneration)
					IsLoadingModules = false;
			}
		}

		void SetEntryAssemblyPath(string path)
		{
			entryAssemblyPath = path;
			AddEntryAssemblyCommand.NotifyCanExecuteChanged();
		}
	}
}
