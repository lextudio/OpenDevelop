using System.Diagnostics;
using System.Text.Json;
using ICSharpCode.SharpDevelop;
using ICSharpCode.UnitTesting;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;
using OpenDevelop.Profiler.AddIn.Services;
using OpenDevelop.Profiler.AddIn.Views;
using OpenDevelop.Profiler.Collection;

namespace OpenDevelop.Profiler.AddIn;

/// <summary>
/// Narrow integration-test probes for the Profiler. They run the same capture coordinator the menu
/// commands use (minus the pickers, which need a person) and read the report views through the same
/// code paths a user's clicks take, so a test exercises the loaded add-in rather than a stand-in.
/// </summary>
[DevFlowUIThread]
public static class ProfilerDevFlowActions
{
	static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

	[DevFlowAction("od.profiler.capture-cpu", Description = "Sample a running .NET process for a number of seconds through the Profiler's capture coordinator, then open the report")]
	public static async Task<string> CaptureCpuAsync(int processId, int seconds)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			var path = await CaptureCoordinator.CaptureCpuAsync(
				process.ProcessName, process, ownsProcess: false, TimeSpan.FromSeconds(seconds));
			return Serialize(new { success = true, path });
		}
		catch (Exception exception) when (exception is ProfilingException or ArgumentException)
		{
			return Serialize(new { success = false, error = exception.Message });
		}
	}

	[DevFlowAction("od.profiler.capture-allocation", Description = "Record the allocations of a running .NET process for a number of seconds through the Profiler's capture coordinator, then open the report")]
	public static async Task<string> CaptureAllocationAsync(int processId, int seconds)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			var path = await CaptureCoordinator.CaptureCpuAsync(
				process.ProcessName, process, ownsProcess: false, TimeSpan.FromSeconds(seconds), mode: Collection.CaptureMode.Allocation);
			return Serialize(new { success = true, path });
		}
		catch (Exception exception) when (exception is ProfilingException or ArgumentException)
		{
			return Serialize(new { success = false, error = exception.Message });
		}
	}

	[DevFlowAction("od.profiler.listen-port", Description = "Listen at a diagnostic port address until a program connects, record it for a number of seconds (Cpu or Allocation), then open the report")]
	public static async Task<string> ListenPortAsync(string address, string mode, int seconds, int waitSeconds)
	{
		try
		{
			var path = await CaptureCoordinator.CaptureFromPortAsync(
				address, Enum.Parse<Collection.CaptureMode>(mode, ignoreCase: true), TimeSpan.FromSeconds(waitSeconds), TimeSpan.FromSeconds(seconds));
			return Serialize(new { success = path is not null, path });
		}
		catch (Exception exception) when (exception is ProfilingException or ArgumentException)
		{
			return Serialize(new { success = false, error = exception.Message });
		}
	}

	[DevFlowAction("od.profiler.settings", Description = "Read the Profiler settings, or change them: retained sessions, large-capture megabytes and store folder (pass 0, 0 and the current folder to only read)")]
	public static string Settings(int retainedSessions, int largeCaptureMegabytes, string storeFolder)
	{
		if (retainedSessions > 0)
			ProfilerSettings.RetainedSessions = retainedSessions;
		if (largeCaptureMegabytes > 0)
			ProfilerSettings.LargeCaptureMegabytes = largeCaptureMegabytes;
		ProfilerSettings.StoreFolder = storeFolder;
		return Serialize(new
		{
			success = true,
			retainedSessions = ProfilerSettings.RetainedSessions,
			largeCaptureMegabytes = ProfilerSettings.LargeCaptureMegabytes,
			storeFolder = ProfilerSettings.StoreFolder,
			sessions = ProfilerWorkspace.OpenStore().List().Count,
		});
	}

	// Windows opened by the probe below, so the next call can close them.
	static readonly List<System.Windows.Window> OpenedDialogs = [];

	[DevFlowAction("od.profiler.show-dialog", Description = "Open one of the Profiler's dialogs without blocking (settings, port), so its layout and text can be checked; returns its title")]
	public static string ShowDialog(string name)
	{
		System.Windows.Window window = name switch
		{
			"settings" => new Dialogs.SettingsWindow(),
			"port" => new Dialogs.DiagnosticPortWindow(),
			_ => throw new ArgumentException("Unknown dialog: " + name),
		};
		window.Owner = SD.Workbench.MainWindow;
		window.Show();
		OpenedDialogs.Add(window);
		return Serialize(new { success = true, title = window.Title });
	}

	[DevFlowAction("od.profiler.close-dialogs", Description = "Close the dialogs opened by od.profiler.show-dialog")]
	public static string CloseDialogs()
	{
		foreach (var window in OpenedDialogs)
			window.Close();
		var closed = OpenedDialogs.Count;
		OpenedDialogs.Clear();
		return Serialize(new { success = true, closed });
	}

	[DevFlowAction("od.profiler.debugged-process", Description = "The id of the process the debugger is attached to, or null; what Attach CPU Profiler to Debugged Process samples")]
	public static string DebuggedProcess() => Serialize(new { success = true, processId = SD.Debugger.DebuggedProcessId });

	[DevFlowAction("od.profiler.scan-contents", Description = "Turn the heap contents scan (duplicate strings, sparse arrays) after a snapshot on or off")]
	public static string ScanContents(bool enabled)
	{
		ProfilerSettings.ScanHeapContents = enabled;
		return Serialize(new { success = true, enabled = ProfilerSettings.ScanHeapContents });
	}

	[DevFlowAction("od.profiler.capture-dump", Description = "Write a memory dump (with the heap) of a running .NET process through the Profiler's capture coordinator, then open it as a report")]
	public static async Task<string> CaptureDumpAsync(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			var path = await CaptureCoordinator.WriteDumpAsync(new ManagedProcess(processId, process.ProcessName));
			return Serialize(new { success = path is not null, path });
		}
		catch (Exception exception) when (exception is ProfilingException or ArgumentException)
		{
			return Serialize(new { success = false, error = exception.Message });
		}
	}

	[DevFlowAction("od.profiler.dump.select-thread", Description = "Select a thread by managed id in the open dump report's Threads tab")]
	public static Task<string> SelectDumpThread(int managedId) => WithDump(dump => new { found = dump.SelectThread(managedId) });

	[DevFlowAction("od.profiler.dump.show-tab", Description = "Show the Heap or the Threads tab of the open dump report")]
	public static Task<string> ShowDumpTab(string name) => WithDump(dump =>
	{
		dump.ShowTab(name);
		return new { applied = true };
	});

	[DevFlowAction("od.profiler.launch-cpu", Description = "Start a .NET program (dotnet exec <dll> <arguments>) held at startup, sample it from its first instruction for a number of seconds, then open the report")]
	public static Task<string> LaunchCpuAsync(string dllPath, string arguments, int seconds) =>
		LaunchAsync(dllPath, arguments, seconds, Collection.CaptureMode.Cpu);

	[DevFlowAction("od.profiler.launch-allocation", Description = "Start a .NET program (dotnet exec <dll> <arguments>) held at startup, record its allocations from its first instruction for a number of seconds, then open the report")]
	public static Task<string> LaunchAllocationAsync(string dllPath, string arguments, int seconds) =>
		LaunchAsync(dllPath, arguments, seconds, Collection.CaptureMode.Allocation);

	static async Task<string> LaunchAsync(string dllPath, string arguments, int seconds, Collection.CaptureMode mode)
	{
		ProcessStartInfo StartInfo()
		{
			var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
			info.ArgumentList.Add("exec");
			info.ArgumentList.Add(dllPath);
			foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
				info.ArgumentList.Add(argument);
			return info;
		}

		try
		{
			var path = await CaptureCoordinator.LaunchAndCaptureCpuAsync(StartInfo, Path.GetFileNameWithoutExtension(dllPath), TimeSpan.FromSeconds(seconds), mode);
			return Serialize(new { success = true, path });
		}
		catch (ProfilingException exception)
		{
			return Serialize(new { success = false, error = exception.Message });
		}
	}

	[DevFlowAction("od.profiler.profile-test", Description = "Run the first discovered unit test whose display name matches or ends with the given name in a profiled test host, then open the report")]
	public static async Task<string> ProfileTestAsync(string displayName)
	{
		var solution = SD.GetRequiredService<ITestService>().OpenSolution;
		var test = solution is null ? null : FindTest(solution.NestedTests, displayName);
		if (test is null)
			return Serialize(new { success = false, error = "Test not found: " + displayName });
		try
		{
			var path = await CaptureCoordinator.ProfileTestsAsync([test]);
			return Serialize(new { success = true, path });
		}
		catch (ProfilingException exception)
		{
			return Serialize(new { success = false, error = exception.Message });
		}
	}

	static ITest? FindTest(IEnumerable<ITest> tests, string displayName)
	{
		foreach (var test in tests)
		{
			if (test.DisplayName == displayName || test.DisplayName.EndsWith(displayName, StringComparison.Ordinal))
				return test;
			if (FindTest(test.NestedTests, displayName) is { } nested)
				return nested;
		}
		return null;
	}

	[DevFlowAction("od.profiler.profile-project", Description = "Profile the current project exactly as Analysis > Profiler > Profile Current Project does (its launch settings, held at startup), for a number of seconds, then open the report")]
	public static async Task<string> ProfileProjectAsync(int seconds)
	{
		try
		{
			var path = await CaptureCoordinator.ProfileProjectAsync(TimeSpan.FromSeconds(seconds));
			return Serialize(new { success = true, path });
		}
		catch (ProfilingException exception)
		{
			return Serialize(new { success = false, error = exception.Message });
		}
	}

	[DevFlowAction("od.profiler.capture-heap", Description = "Take a managed-heap snapshot of a running .NET process through the Profiler's capture coordinator, then open the report")]
	public static async Task<string> CaptureHeapAsync(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			var path = await CaptureCoordinator.TakeHeapSnapshotAsync(new ManagedProcess(processId, process.ProcessName));
			return Serialize(new { success = path is not null, path });
		}
		catch (Exception exception) when (exception is ProfilingException or ArgumentException)
		{
			return Serialize(new { success = false, error = exception.Message });
		}
	}

	[DevFlowAction("od.profiler.sessions", Description = "List the sessions recorded in the Profiler's session store")]
	public static string Sessions()
	{
		var sessions = ProfilerWorkspace.OpenStore().List().Select(s => new
		{
			id = s.Info.Id,
			kind = s.Info.Kind.ToString(),
			status = s.Info.Status.ToString(),
			name = s.Info.DisplayName,
			processId = s.Info.ProcessId,
			artifactExists = File.Exists(s.ArtifactPath),
		});
		return Serialize(new { success = true, sessions });
	}

	[DevFlowAction("od.profiler.sessions.window", Description = "Open the Profiling Sessions window and list the rows it shows")]
	public static string SessionsWindow()
	{
		var window = Dialogs.SessionsWindow.ShowWindow();
		return Serialize(new { success = true, sessions = window.Describe() });
	}

	[DevFlowAction("od.profiler.sessions.delete", Description = "Delete a session through the Profiling Sessions window (without the confirmation question) and return the remaining rows")]
	public static string DeleteSession(string id)
	{
		var window = Dialogs.SessionsWindow.ShowWindow();
		var deleted = window.DeleteWithoutConfirmation(id);
		return Serialize(new { success = deleted, sessions = window.Describe() });
	}

	[DevFlowAction("od.profiler.report", Description = "Describe the active Profiler report (kind, mode, status text, visible rows), waiting until the capture file has been parsed")]
	public static async Task<string> ReportAsync()
	{
		if (SD.Workbench.ActiveViewContent is not ProfilerReportViewContent content)
			return Serialize(new { success = false, error = "The active view is not a Profiler report." });
		await content.ReportControl.Ready;
		if (content.ReportControl.ReportView is CpuReportView settling)
			await settling.Settled;
		return content.ReportControl.ReportView switch
		{
			CpuReportView cpu => Serialize(new { success = true, kind = "cpu", state = cpu.Describe() }),
			HeapReportView heap => Serialize(new { success = true, kind = "heap", state = heap.Snapshot(40) }),
			DumpReportView dump => Serialize(new { success = true, kind = "dump", state = dump.Describe() }),
			System.Windows.Controls.TextBlock message => Serialize(new { success = true, kind = "message", text = message.Text }),
			_ => Serialize(new { success = false, error = "Unknown report content." }),
		};
	}

	[DevFlowAction("od.profiler.cpu.background-threshold", Description = "Set how many samples a capture needs before its views are recomputed off the UI thread (1 makes every capture do so)")]
	public static string BackgroundThreshold(int samples)
	{
		CpuReportView.BackgroundSampleThreshold = Math.Max(1, samples);
		return Serialize(new { success = true, samples = CpuReportView.BackgroundSampleThreshold });
	}

	[DevFlowAction("od.profiler.cpu.set-mode", Description = "Switch the CPU report view: CallTree, Functions, CallerCallee or FlameGraph")]
	public static Task<string> SetMode(string mode) => WithCpu(cpu => new { applied = cpu.SetMode(mode) });

	[DevFlowAction("od.profiler.cpu.just-my-code", Description = "Turn Show Just My Code on or off in the CPU report")]
	public static Task<string> JustMyCode(bool enabled) => WithCpu(cpu =>
	{
		cpu.SetJustMyCode(enabled);
		return new { applied = true };
	});

	[DevFlowAction("od.profiler.cpu.select-range", Description = "Select a time range (milliseconds from the first sample) on the CPU report's timeline")]
	public static Task<string> SelectRange(double startMs, double endMs) => WithCpu(cpu =>
	{
		cpu.SelectRange(startMs, endMs);
		return new { applied = true };
	});

	[DevFlowAction("od.profiler.cpu.clear-range", Description = "Clear the CPU report's selected time range")]
	public static Task<string> ClearRange() => WithCpu(cpu =>
	{
		cpu.ClearRange();
		return new { applied = true };
	});

	[DevFlowAction("od.profiler.export", Description = "Export the open report (call tree or function list, or the heap type table) as CSV to a path, as the toolbar button does")]
	public static async Task<string> Export(string path)
	{
		switch (await ActiveReportViewAsync<object>())
		{
			case CpuReportView cpu:
				cpu.ExportTo(path);
				return Serialize(new { success = true, path });
			case HeapReportView heap:
				heap.ExportTo(path);
				return Serialize(new { success = true, path });
			default:
				return Serialize(new { success = false, error = "The active view is not a loaded report." });
		}
	}

	[DevFlowAction("od.profiler.cpu.compare", Description = "Compare the CPU report's Functions view with another CPU capture file")]
	public static async Task<string> CompareCpu(string path)
	{
		if (await ActiveReportViewAsync<CpuReportView>() is not { } cpu)
			return Serialize(new { success = false, error = "The active view is not a loaded CPU report." });
		await cpu.CompareWithAsync(path);
		return Serialize(new { success = true, result = new { applied = true } });
	}

	[DevFlowAction("od.profiler.cpu.stop-compare", Description = "Stop comparing the CPU report with another capture")]
	public static Task<string> StopCompareCpu() => WithCpu(cpu =>
	{
		cpu.StopComparing();
		return new { applied = true };
	});

	[DevFlowAction("od.profiler.cpu.find", Description = "Find the next call tree node whose name contains the text")]
	public static Task<string> Find(string text) => WithCpu(cpu =>
	{
		var (current, total) = cpu.FindNode(text);
		return new { current, total };
	});

	[DevFlowAction("od.profiler.cpu.show-caller-callee", Description = "Show the caller/callee view of the first function whose name contains the text")]
	public static Task<string> ShowCallerCallee(string nameFragment) => WithCpu(cpu => new { found = cpu.ShowCallerCalleeOf(nameFragment) });

	[DevFlowAction("od.profiler.cpu.find-source", Description = "Look up where the first function whose name contains the text is declared in the open solution, as Go to Source would")]
	public static async Task<string> FindSourceAsync(string nameFragment)
	{
		if (await ActiveReportViewAsync<CpuReportView>() is not { } cpu)
			return Serialize(new { success = false, error = "The active view is not a loaded CPU report." });
		var location = await cpu.FindSourceOfAsync(nameFragment);
		return Serialize(new
		{
			success = true,
			found = location is not null,
			file = location?.FileName,
			line = location?.Line,
		});
	}

	[DevFlowAction("od.profiler.heap.filter", Description = "Filter the heap report's Types view by type name")]
	public static Task<string> FilterHeap(string text) => WithHeap(heap =>
	{
		heap.SetFilter(text);
		return Task.FromResult<object>(new { applied = true });
	});

	[DevFlowAction("od.profiler.heap.select-type", Description = "Select a type in the heap report and describe its path to a GC root and its referrers")]
	public static Task<string> SelectType(string typeName) => WithHeap(async heap =>
	{
		await heap.WaitForRetainedSizesAsync();
		var selected = await heap.SelectTypeAsync(typeName);
		return (object)new { selected, details = heap.Details() };
	});

	[DevFlowAction("od.profiler.heap.select-instance", Description = "Select the n-th listed instance of the selected heap type and describe the path to a GC root it shows")]
	public static Task<string> SelectInstance(int index) => WithHeap(heap =>
	{
		var selected = heap.SelectInstance(index);
		return Task.FromResult<object>(new { selected, details = heap.Details() });
	});

	[DevFlowAction("od.profiler.heap.compare", Description = "Compare the heap report with another heap capture file")]
	public static Task<string> CompareHeap(string path) => WithHeap(async heap =>
	{
		await heap.CompareWithAsync(path);
		return new { applied = true };
	});

	[DevFlowAction("od.profiler.heap.stop-compare", Description = "Stop comparing the heap report with another capture")]
	public static Task<string> StopCompareHeap() => WithHeap(heap =>
	{
		heap.StopComparing();
		return Task.FromResult<object>(new { applied = true });
	});

	// A report parses its capture file off the UI thread and keeps a loading message as its content until
	// the parse finishes, so ReportControl.ReportView is a TextBlock for that whole window. Every action
	// that needs a loaded view waits for the parse first, the way od.profiler.report already does: a
	// capture opens its report straight away, and an action that matched the placeholder instead reported
	// "not a loaded report" for a report the user could already see.
	static async Task<T?> ActiveReportViewAsync<T>() where T : class
	{
		if (SD.Workbench.ActiveViewContent is not ProfilerReportViewContent content)
			return null;
		await content.ReportControl.Ready;
		// Ready completes on a pool thread (its source runs continuations asynchronously), so come back to
		// the UI thread before the caller touches the view.
		return SD.MainThread.InvokeIfRequired(() => content.ReportControl.ReportView as T);
	}

	static async Task<string> WithCpu(Func<CpuReportView, object> action)
	{
		if (await ActiveReportViewAsync<CpuReportView>() is { } cpu)
			return Serialize(new { success = true, result = action(cpu) });
		return Serialize(new { success = false, error = "The active view is not a loaded CPU report." });
	}

	static async Task<string> WithDump(Func<DumpReportView, object> action)
	{
		if (await ActiveReportViewAsync<DumpReportView>() is { } dump)
			return Serialize(new { success = true, result = action(dump) });
		return Serialize(new { success = false, error = "The active view is not a loaded dump report." });
	}

	static async Task<string> WithHeap(Func<HeapReportView, Task<object>> action)
	{
		if (await ActiveReportViewAsync<HeapReportView>() is { } heap)
			return Serialize(new { success = true, result = await action(heap) });
		return Serialize(new { success = false, error = "The active view is not a loaded heap report." });
	}

	static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
}
