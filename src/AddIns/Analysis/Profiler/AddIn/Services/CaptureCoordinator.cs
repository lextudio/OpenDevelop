using System.Diagnostics;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.UnitTesting;
using OpenDevelop.Profiler.AddIn.Dialogs;
using OpenDevelop.Profiler.Collection;
using OpenDevelop.Profiler.Heap;
using OpenDevelop.Profiler.Sessions;

namespace OpenDevelop.Profiler.AddIn.Services;

/// <summary>
/// Runs a capture from a menu command to an open report: picks or starts the target, shows the capture
/// window, records the session on disk, and opens the raw file in the Profiler's report view. A failed
/// capture is recorded as failed and reported; a completed one is never discarded because a later step
/// (opening the report) failed.
/// </summary>
internal static class CaptureCoordinator
{
	static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
	static readonly TimeSpan HeapTimeout = TimeSpan.FromMinutes(2);
	static readonly TimeSpan SuspendedStartTimeout = TimeSpan.FromSeconds(15);

	public static void ProfileCurrentProject() => Run(ProfileCurrentProjectAsync);

	public static void ProfileCurrentProjectAllocations() => Run(() => ProfileProjectAsync(duration: null, CaptureMode.Allocation));

	public static void AttachCpu() => Run(AttachCpuAsync);

	public static void AttachAllocation() => Run(AttachAllocationAsync);

	public static void ProfileDebuggedProcess() => Run(ProfileDebuggedProcessAsync);

	public static void AttachDiagnosticPort() => Run(AttachDiagnosticPortAsync);

	public static void CaptureHeap() => Run(CaptureHeapAsync);

	public static void CaptureDump() => Run(CaptureDumpAsync);

	public static void ProfileTests(IReadOnlyList<ITest> tests) => Run(() => ProfileTestsAsync(tests));

	// The command boundary: a menu command cannot await, and every failure has to reach the user
	// instead of vanishing with an unobserved task.
	static async void Run(Func<Task> operation)
	{
		try
		{
			await operation();
		}
		catch (ProfilingException exception)
		{
			MessageService.ShowError(StringParser.Format("${res:AddIns.Profiler.Capture.Failed}", exception.Message));
		}
		catch (Exception exception)
		{
			MessageService.ShowException(exception, StringParser.Parse("${res:AddIns.Profiler.Capture.UnexpectedFailure}"));
		}
	}

	static Task ProfileCurrentProjectAsync() => ProfileProjectAsync(duration: null);

	/// <summary>
	/// Profiles the current project the way its Run command would start it: its launch settings, working directory
	/// and arguments, held at startup, sampled until it exits, stops, or <paramref name="duration"/> passes. Returns
	/// the capture file. Validation failures are reported as <see cref="ProfilingException"/>.
	/// </summary>
	internal static async Task<string> ProfileProjectAsync(TimeSpan? duration, CaptureMode mode = CaptureMode.Cpu)
	{
		// The project selected in the Projects pad, else the solution's startup project (what Visual Studio's profiler
		// profiles), so the command also works with nothing selected.
		var project = ProjectService.CurrentProject
			?? ProjectService.OpenSolution?.StartupProject
			?? throw new ProfilingException(StringParser.Parse("${res:AddIns.Profiler.Messages.NoStartableProjectFound}"));
		// Only projects built on AbstractProject can describe how they start; every project type in the
		// product does, and a library or other non-startable project is reported like the Run command does.
		if (project is not AbstractProject startable || !project.IsStartable)
			throw new ProfilingException(StringParser.Parse("${res:BackendBindings.ExecutionManager.CantExecuteDLLError}"));
		if (!File.Exists(project.OutputAssemblyFullPath))
			throw new ProfilingException(StringParser.Format("${res:AddIns.Profiler.Messages.BuildFirst}", project.Name));

		return await LaunchAndCaptureCpuAsync(() => startable.CreateStartInfo(), project.Name, duration, mode);
	}

	/// <summary>
	/// Starts a program and samples it from its first instruction, then opens the report. The runtime is held at
	/// startup until the capture is recording; a runtime that cannot do that (older than .NET 5) falls back to
	/// attaching once the process is up, which misses the start. The factory is called once per attempt because
	/// a start info is not reusable after the first. Returns the capture file.
	/// </summary>
	internal static async Task<string> LaunchAndCaptureCpuAsync(
		Func<ProcessStartInfo> startInfo, string displayName, TimeSpan? duration = null, CaptureMode mode = CaptureMode.Cpu)
	{
		var launch = await TryStartSuspendedAsync(startInfo);
		if (launch is not null)
		{
			await using (launch)
			{
				try { return await CaptureCpuAsync(displayName, launch.Process, ownsProcess: true, duration, launch, mode); }
				finally { TryKill(launch.Process); }
			}
		}

		var info = startInfo();
		info.UseShellExecute = false;
		using var process = await Task.Run(() => ProcessTargets.StartAndWaitForDiagnostics(info, StartTimeout));
		try { return await CaptureCpuAsync(displayName, process, ownsProcess: true, duration, mode: mode); }
		finally { TryKill(process); }
	}

	/// <summary>
	/// Samples the test host while the selected tests run, from the host's first instruction, and opens the report when
	/// it exits. Returns the capture file.
	/// </summary>
	internal static async Task<string> ProfileTestsAsync(IReadOnlyList<ITest> tests)
	{
		if (tests.Count == 0)
			throw new ProfilingException(StringParser.Parse("${res:AddIns.Profiler.Messages.NoTestsSelected}"));
		var projects = tests.Select(test => test.ParentProject).Distinct().ToList();
		if (projects.Count != 1)
			throw new ProfilingException(StringParser.Parse("${res:AddIns.Profiler.Messages.TestsFromOneProject}"));
		if (projects[0] is not MtpTestProject project)
			throw new ProfilingException(StringParser.Parse("${res:AddIns.Profiler.Messages.TestsNotSupported}"));

		return await LaunchAndCaptureCpuAsync(() => RunnableHost(project.CreateTestHostStartInfo(tests)), project.Project.Name);
	}

	// The test host comes back as the built assembly (the debugger hands that to its adapter, which starts it
	// with dotnet). A process of our own has to be started the way dotnet would: "dotnet exec <dll> <args>".
	static ProcessStartInfo RunnableHost(ProcessStartInfo host)
	{
		if (!host.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
			return host;

		var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet ? dotnet : "dotnet")
		{
			WorkingDirectory = host.WorkingDirectory,
			UseShellExecute = false,
		};
		info.ArgumentList.Add("exec");
		info.ArgumentList.Add(host.FileName);
		foreach (var argument in host.ArgumentList)
			info.ArgumentList.Add(argument);
		return info;
	}

	static readonly TimeSpan PortWaitTimeout = TimeSpan.FromMinutes(10);

	/// <summary>Samples the program the debugger is attached to: no picker, because the debugger knows which one it is.</summary>
	static async Task ProfileDebuggedProcessAsync()
	{
		if (SD.Debugger.DebuggedProcessId is not { } id)
			throw new ProfilingException(StringParser.Parse("${res:AddIns.Profiler.Messages.NotDebugging}"));
		if (!ProceedWhileDebuggerIsStopped(id))
			return;
		using var process = OpenProcess(new ManagedProcess(id, string.Empty));
		await CaptureCpuAsync(process.ProcessName, process, ownsProcess: false);
	}

	// A program stopped at a breakpoint has its runtime paused: sampling records nothing and a heap snapshot waits for it to run.
	// When the debugger says which process it is debugging, only that one is warned about; otherwise the warning is generic.
	static bool ProceedWhileDebuggerIsStopped(int processId)
	{
		var debugger = SD.Debugger;
		if (!debugger.IsDebugging || debugger.IsProcessRunning)
			return true;
		// When the debugger says which process it has, only that one is paused; when it does not, any may be.
		return debugger.DebuggedProcessId switch
		{
			{ } debugged when debugged != processId => true,
			{ } => MessageService.AskQuestion(StringParser.Format("${res:AddIns.Profiler.Capture.DebuggedProcessStopped}", processId)),
			null => MessageService.AskQuestion(StringParser.Parse("${res:AddIns.Profiler.Capture.DebuggerStopped}")),
		};
	}

	static async Task AttachDiagnosticPortAsync()
	{
		var request = DiagnosticPortWindow.Ask(SD.Workbench.MainWindow);
		if (request is not null)
			await CaptureFromPortAsync(request.Address, request.Mode, PortWaitTimeout, duration: null);
	}

	/// <summary>
	/// Listens at a diagnostic port address until a program started elsewhere connects to it, then records it and opens the
	/// report. Returns the capture file, or <c>null</c> if the user gave up waiting.
	/// </summary>
	internal static async Task<string?> CaptureFromPortAsync(string address, CaptureMode mode, TimeSpan waitTimeout, TimeSpan? duration)
	{
		using var cancellation = new CancellationTokenSource();
		var waiting = new CaptureControlWindow(
			address,
			StringParser.Format("${res:AddIns.Profiler.Capture.Waiting}", address),
			hint: null,
			StringParser.Parse("${res:Global.Cancel}"))
		{
			Owner = SD.Workbench.MainWindow,
		};
		waiting.ActionRequested += (_, _) =>
		{
			waiting.SetBusy(StringParser.Parse("${res:AddIns.Profiler.Capture.Stopping}"));
			cancellation.Cancel();
		};
		waiting.Show();

		DiagnosticPortListener listener;
		try
		{
			listener = await DiagnosticPortListener.ListenAsync(address, waitTimeout, cancellation.Token);
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		finally
		{
			waiting.CloseFromCode();
		}

		await using (listener)
		{
			// The program is not ours to end, and it has no local process id.
			return await CaptureAsync(
				listener.DisplayName, 0, (path, options) => CpuCapture.Start(listener.Client, 0, path, options),
				listener.Resume, stopTarget: null, duration, mode);
		}
	}

	static async Task AttachAllocationAsync()
	{
		var chosen = ProcessPickerWindow.Pick(SD.Workbench.MainWindow, ProcessPickerPurpose.Allocation);
		if (chosen is null || !ProceedWhileDebuggerIsStopped(chosen.Id))
			return;
		using var process = OpenProcess(chosen);
		await CaptureCpuAsync(chosen.Name, process, ownsProcess: false, mode: CaptureMode.Allocation);
	}

	static async Task AttachCpuAsync()
	{
		var chosen = ProcessPickerWindow.Pick(SD.Workbench.MainWindow, ProcessPickerPurpose.Cpu);
		if (chosen is null || !ProceedWhileDebuggerIsStopped(chosen.Id))
			return;
		using var process = OpenProcess(chosen);
		await CaptureCpuAsync(chosen.Name, process, ownsProcess: false);
	}

	static async Task<SuspendedLaunch?> TryStartSuspendedAsync(Func<ProcessStartInfo> startInfo)
	{
		try
		{
			return await SuspendedLaunch.StartAsync(startInfo(), SuspendedStartTimeout);
		}
		catch (ProfilingException)
		{
			return null;
		}
	}

	static Process OpenProcess(ManagedProcess chosen)
	{
		try
		{
			return Process.GetProcessById(chosen.Id);
		}
		catch (ArgumentException)
		{
			throw new ProfilingException($"Process {chosen.Id} ({chosen.Name}) has already exited.");
		}
	}

	/// <summary>Samples a running process until it is stopped (or <paramref name="duration"/> passes) and opens the report. Returns the capture file.</summary>
	internal static Task<string> CaptureCpuAsync(
		string displayName, Process process, bool ownsProcess, TimeSpan? duration = null, SuspendedLaunch? launch = null,
		CaptureMode mode = CaptureMode.Cpu) =>
		CaptureAsync(
			displayName, process.Id,
			(path, options) => launch is null
				? CpuCapture.Start(process.Id, path, options)
				: CpuCapture.Start(launch.Client, process.Id, path, options),
			launch is null ? null : launch.Resume,
			ownsProcess ? () => TryKill(process) : null,
			duration, mode);

	/// <summary>
	/// Runs a capture of any target the caller can start a session on: a local process, a held launch, or a program that
	/// connected to a diagnostic port. <paramref name="stopTarget"/> ends a program this IDE started once the capture is
	/// over; it is null for one that belongs to someone else. Opens the report and returns the capture file.
	/// </summary>
	static async Task<string> CaptureAsync(
		string displayName, int processId, Func<string, CpuCaptureOptions, CpuCapture> start, Action? resume, Action? stopTarget,
		TimeSpan? duration, CaptureMode mode)
	{
		var store = ProfilerWorkspace.OpenStore();
		store.Prune(ProfilerSettings.RetainedSessions);
		var session = store.Create(mode == CaptureMode.Allocation ? SessionKind.Allocation : SessionKind.Cpu, displayName, processId);
		CpuCapture capture;
		try
		{
			capture = start(session.ArtifactPath, new CpuCaptureOptions { Duration = duration, Mode = mode });
			// The capture is recording; only now may the held program run.
			resume?.Invoke();
		}
		catch (ProfilingException exception)
		{
			store.Finish(session, SessionStatus.Failed, exception.Message);
			stopTarget?.Invoke();
			throw;
		}

		var window = new CaptureControlWindow(
			displayName,
			StringParser.Parse(mode == CaptureMode.Allocation
				? "${res:AddIns.Profiler.Capture.RecordingAllocations}"
				: "${res:AddIns.Profiler.Capture.Sampling}"),
			StringParser.Parse("${res:AddIns.Profiler.Capture.Hint}"),
			StringParser.Parse("${res:AddIns.Profiler.Capture.Stop}"))
		{
			Owner = SD.Workbench.MainWindow,
		};
		window.ActionRequested += async (_, _) =>
		{
			window.SetBusy(StringParser.Parse("${res:AddIns.Profiler.Capture.Stopping}"));
			try
			{
				await capture.StopAsync();
			}
			catch (ProfilingException)
			{
				// Reported through Completion below.
			}
		};
		window.Show();

		CaptureResult result;
		try
		{
			result = await capture.Completion;
		}
		catch (Exception exception) when (exception is IOException or ProfilingException)
		{
			store.Finish(session, SessionStatus.Failed, exception.Message);
			throw new ProfilingException(exception.Message, exception);
		}
		finally
		{
			window.CloseFromCode();
		}

		// A project this command launched belongs to the profiling run; one the user attached to does not.
		if (!result.TargetExited)
			stopTarget?.Invoke();
		store.Finish(session, SessionStatus.Completed);
		if (result.TargetExited)
			SD.StatusBar.SetMessage(StringParser.Format("${res:AddIns.Profiler.Capture.TargetExited}", displayName));
		OpenReport(result.Path);
		return result.Path;
	}

	static async Task CaptureHeapAsync()
	{
		var chosen = ProcessPickerWindow.Pick(SD.Workbench.MainWindow, ProcessPickerPurpose.HeapSnapshot);
		if (chosen is not null && ProceedWhileDebuggerIsStopped(chosen.Id))
			await TakeHeapSnapshotAsync(chosen);
	}

	/// <summary>Takes a managed-heap snapshot and opens the report. Returns the capture file, or <c>null</c> if the user cancelled.</summary>
	internal static async Task<string?> TakeHeapSnapshotAsync(ManagedProcess chosen)
	{
		var store = ProfilerWorkspace.OpenStore();
		store.Prune(ProfilerSettings.RetainedSessions);
		var session = store.Create(SessionKind.Heap, chosen.Name, chosen.Id);
		using var cancellation = new CancellationTokenSource();
		var window = new CaptureControlWindow(
			chosen.Name,
			StringParser.Parse("${res:AddIns.Profiler.Capture.Snapshotting}"),
			hint: null,
			StringParser.Parse("${res:Global.Cancel}"))
		{
			Owner = SD.Workbench.MainWindow,
		};
		window.ActionRequested += (_, _) =>
		{
			window.SetBusy(StringParser.Parse("${res:AddIns.Profiler.Capture.Stopping}"));
			cancellation.Cancel();
		};
		window.Show();

		try
		{
			await HeapCapture.CaptureAsync(chosen.Id, session.ArtifactPath, HeapTimeout, cancellation.Token);
		}
		catch (OperationCanceledException)
		{
			store.Finish(session, SessionStatus.Canceled);
			return null;
		}
		catch (ProfilingException exception)
		{
			store.Finish(session, SessionStatus.Failed, exception.Message);
			throw;
		}
		finally
		{
			window.CloseFromCode();
		}

		if (ProfilerSettings.ScanHeapContents)
			await ScanContentsAsync(chosen, session.Directory);
		store.Finish(session, SessionStatus.Completed);
		OpenReport(session.ArtifactPath);
		return session.ArtifactPath;
	}

	// A second pause of the process and a dump of its memory, so it is opt-in. A scan that fails does not fail the snapshot,
	// which is already complete and opens without it.
	static async Task ScanContentsAsync(ManagedProcess chosen, string sessionDirectory)
	{
		SD.StatusBar.SetMessage(StringParser.Format("${res:AddIns.Profiler.Capture.ReadingContents}", chosen.Name));
		try
		{
			var insights = await HeapContentAnalyzer.AnalyzeProcessAsync(chosen.Id);
			insights.Save(Path.Combine(sessionDirectory, ContentInsights.FileName));
		}
		catch (Exception exception) when (exception is ProfilingException or IOException or UnauthorizedAccessException)
		{
			SD.StatusBar.SetMessage(StringParser.Format("${res:AddIns.Profiler.Capture.ContentsFailed}", exception.Message));
		}
	}

	static async Task CaptureDumpAsync()
	{
		var chosen = ProcessPickerWindow.Pick(SD.Workbench.MainWindow, ProcessPickerPurpose.Dump);
		if (chosen is not null && ProceedWhileDebuggerIsStopped(chosen.Id))
			await WriteDumpAsync(chosen);
	}

	/// <summary>
	/// Writes a memory dump of a process (with its heap) into a new session and opens it as a report. The process is held still
	/// while the file is written. Returns the dump file, or <c>null</c> if the user cancelled.
	/// </summary>
	internal static async Task<string?> WriteDumpAsync(ManagedProcess chosen)
	{
		var store = ProfilerWorkspace.OpenStore();
		store.Prune(ProfilerSettings.RetainedSessions);
		var session = store.Create(SessionKind.Dump, chosen.Name, chosen.Id);
		using var cancellation = new CancellationTokenSource();
		var window = new CaptureControlWindow(
			chosen.Name,
			StringParser.Parse("${res:AddIns.Profiler.Capture.WritingDump}"),
			hint: null,
			StringParser.Parse("${res:Global.Cancel}"))
		{
			Owner = SD.Workbench.MainWindow,
		};
		window.ActionRequested += (_, _) =>
		{
			window.SetBusy(StringParser.Parse("${res:AddIns.Profiler.Capture.Stopping}"));
			cancellation.Cancel();
		};
		window.Show();

		try
		{
			await DumpCapture.CaptureAsync(chosen.Id, session.ArtifactPath, cancellation.Token);
		}
		catch (OperationCanceledException)
		{
			store.Finish(session, SessionStatus.Canceled);
			return null;
		}
		catch (Exception exception) when (exception is ProfilingException or IOException or UnauthorizedAccessException)
		{
			store.Finish(session, SessionStatus.Failed, exception.Message);
			throw;
		}
		finally
		{
			window.CloseFromCode();
		}

		store.Finish(session, SessionStatus.Completed);
		OpenReport(session.ArtifactPath);
		return session.ArtifactPath;
	}

	public static void OpenReport(string path) => SD.FileService.OpenFile(FileName.Create(path));

	static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// Exited while we were looking.
		}
	}
}
