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

    [DevFlowAction("od.profiler.launch-cpu", Description = "Start a .NET program (dotnet exec <dll> <arguments>) held at startup, sample it from its first instruction for a number of seconds, then open the report")]
    public static async Task<string> LaunchCpuAsync(string dllPath, string arguments, int seconds)
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
            var path = await CaptureCoordinator.LaunchAndCaptureCpuAsync(StartInfo, Path.GetFileNameWithoutExtension(dllPath), TimeSpan.FromSeconds(seconds));
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
        return content.ReportControl.ReportView switch
        {
            CpuReportView cpu => Serialize(new { success = true, kind = "cpu", state = cpu.Describe() }),
            HeapReportView heap => Serialize(new { success = true, kind = "heap", state = heap.Snapshot(40) }),
            System.Windows.Controls.TextBlock message => Serialize(new { success = true, kind = "message", text = message.Text }),
            _ => Serialize(new { success = false, error = "Unknown report content." }),
        };
    }

    [DevFlowAction("od.profiler.cpu.set-mode", Description = "Switch the CPU report view: CallTree, Functions, CallerCallee or FlameGraph")]
    public static string SetMode(string mode) => WithCpu(cpu => new { applied = cpu.SetMode(mode) });

    [DevFlowAction("od.profiler.cpu.just-my-code", Description = "Turn Show Just My Code on or off in the CPU report")]
    public static string JustMyCode(bool enabled) => WithCpu(cpu =>
    {
        cpu.SetJustMyCode(enabled);
        return new { applied = true };
    });

    [DevFlowAction("od.profiler.cpu.select-range", Description = "Select a time range (milliseconds from the first sample) on the CPU report's timeline")]
    public static string SelectRange(double startMs, double endMs) => WithCpu(cpu =>
    {
        cpu.SelectRange(startMs, endMs);
        return new { applied = true };
    });

    [DevFlowAction("od.profiler.cpu.clear-range", Description = "Clear the CPU report's selected time range")]
    public static string ClearRange() => WithCpu(cpu =>
    {
        cpu.ClearRange();
        return new { applied = true };
    });

    [DevFlowAction("od.profiler.cpu.find", Description = "Find the next call tree node whose name contains the text")]
    public static string Find(string text) => WithCpu(cpu =>
    {
        var (current, total) = cpu.FindNode(text);
        return new { current, total };
    });

    [DevFlowAction("od.profiler.cpu.show-caller-callee", Description = "Show the caller/callee view of the first function whose name contains the text")]
    public static string ShowCallerCallee(string nameFragment) => WithCpu(cpu => new { found = cpu.ShowCallerCalleeOf(nameFragment) });

    [DevFlowAction("od.profiler.cpu.find-source", Description = "Look up where the first function whose name contains the text is declared in the open solution, as Go to Source would")]
    public static async Task<string> FindSourceAsync(string nameFragment)
    {
        if (SD.Workbench.ActiveViewContent is not ProfilerReportViewContent { ReportControl.ReportView: CpuReportView cpu })
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
    public static string FilterHeap(string text) => WithHeap(heap =>
    {
        heap.SetFilter(text);
        return Task.FromResult<object>(new { applied = true });
    }).GetAwaiter().GetResult();

    [DevFlowAction("od.profiler.heap.select-type", Description = "Select a type in the heap report and describe its path to a GC root and its referrers")]
    public static Task<string> SelectType(string typeName) => WithHeap(async heap =>
    {
        await heap.WaitForRetainedSizesAsync();
        var selected = await heap.SelectTypeAsync(typeName);
        return (object)new { selected, details = heap.Details() };
    });

    [DevFlowAction("od.profiler.heap.select-instance", Description = "Select the n-th listed instance of the selected heap type and describe the path to a GC root it shows")]
    public static string SelectInstance(int index) => WithHeap(heap =>
    {
        var selected = heap.SelectInstance(index);
        return Task.FromResult<object>(new { selected, details = heap.Details() });
    }).GetAwaiter().GetResult();

    [DevFlowAction("od.profiler.heap.compare", Description = "Compare the heap report with another heap capture file")]
    public static Task<string> CompareHeap(string path) => WithHeap(async heap =>
    {
        await heap.CompareWithAsync(path);
        return new { applied = true };
    });

    [DevFlowAction("od.profiler.heap.stop-compare", Description = "Stop comparing the heap report with another capture")]
    public static string StopCompareHeap() => WithHeap(heap =>
    {
        heap.StopComparing();
        return Task.FromResult<object>(new { applied = true });
    }).GetAwaiter().GetResult();

    static string WithCpu(Func<CpuReportView, object> action)
    {
        if (SD.Workbench.ActiveViewContent is ProfilerReportViewContent { ReportControl.ReportView: CpuReportView cpu })
            return Serialize(new { success = true, result = action(cpu) });
        return Serialize(new { success = false, error = "The active view is not a loaded CPU report." });
    }

    static async Task<string> WithHeap(Func<HeapReportView, Task<object>> action)
    {
        if (SD.Workbench.ActiveViewContent is ProfilerReportViewContent { ReportControl.ReportView: HeapReportView heap })
            return Serialize(new { success = true, result = await action(heap) });
        return Serialize(new { success = false, error = "The active view is not a loaded heap report." });
    }

    static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
}
