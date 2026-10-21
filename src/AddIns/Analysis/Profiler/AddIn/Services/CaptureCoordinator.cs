using System.Diagnostics;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.UnitTesting;
using OpenDevelop.Profiler.AddIn.Dialogs;
using OpenDevelop.Profiler.Collection;
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

    public static void AttachCpu() => Run(AttachCpuAsync);

    public static void CaptureHeap() => Run(CaptureHeapAsync);

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
    internal static async Task<string> ProfileProjectAsync(TimeSpan? duration)
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

        return await LaunchAndCaptureCpuAsync(() => startable.CreateStartInfo(), project.Name, duration);
    }

    /// <summary>
    /// Starts a program and samples it from its first instruction, then opens the report. The runtime is held at
    /// startup until the capture is recording; a runtime that cannot do that (older than .NET 5) falls back to
    /// attaching once the process is up, which misses the start. The factory is called once per attempt because
    /// a start info is not reusable after the first. Returns the capture file.
    /// </summary>
    internal static async Task<string> LaunchAndCaptureCpuAsync(
        Func<ProcessStartInfo> startInfo, string displayName, TimeSpan? duration = null)
    {
        var launch = await TryStartSuspendedAsync(startInfo);
        if (launch is not null)
        {
            await using (launch)
                return await CaptureCpuAsync(displayName, launch.Process, ownsProcess: true, duration, launch);
        }

        var info = startInfo();
        info.UseShellExecute = false;
        using var process = await Task.Run(() => ProcessTargets.StartAndWaitForDiagnostics(info, StartTimeout));
        return await CaptureCpuAsync(displayName, process, ownsProcess: true, duration);
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

    static async Task AttachCpuAsync()
    {
        var chosen = ProcessPickerWindow.Pick(SD.Workbench.MainWindow, ProcessPickerPurpose.Cpu);
        if (chosen is null)
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
    internal static async Task<string> CaptureCpuAsync(
        string displayName, Process process, bool ownsProcess, TimeSpan? duration = null, SuspendedLaunch? launch = null)
    {
        var store = ProfilerWorkspace.OpenStore();
        var session = store.Create(SessionKind.Cpu, displayName, process.Id);
        CpuCapture capture;
        try
        {
            var options = new CpuCaptureOptions { Duration = duration };
            capture = launch is null
                ? CpuCapture.Start(process.Id, session.ArtifactPath, options)
                : CpuCapture.Start(launch.Client, process.Id, session.ArtifactPath, options);
            // The capture is recording; only now may the held program run.
            launch?.Resume();
        }
        catch (ProfilingException exception)
        {
            store.Finish(session, SessionStatus.Failed, exception.Message);
            if (ownsProcess)
                TryKill(process);
            throw;
        }

        var window = new CaptureControlWindow(
            displayName,
            StringParser.Parse("${res:AddIns.Profiler.Capture.Sampling}"),
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
        if (ownsProcess && !result.TargetExited)
            TryKill(process);
        store.Finish(session, SessionStatus.Completed);
        if (result.TargetExited)
            SD.StatusBar.SetMessage(StringParser.Format("${res:AddIns.Profiler.Capture.TargetExited}", displayName));
        OpenReport(result.Path);
        return result.Path;
    }

    static async Task CaptureHeapAsync()
    {
        var chosen = ProcessPickerWindow.Pick(SD.Workbench.MainWindow, ProcessPickerPurpose.HeapSnapshot);
        if (chosen is not null)
            await TakeHeapSnapshotAsync(chosen);
    }

    /// <summary>Takes a managed-heap snapshot and opens the report. Returns the capture file, or <c>null</c> if the user cancelled.</summary>
    internal static async Task<string?> TakeHeapSnapshotAsync(ManagedProcess chosen)
    {
        var store = ProfilerWorkspace.OpenStore();
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
