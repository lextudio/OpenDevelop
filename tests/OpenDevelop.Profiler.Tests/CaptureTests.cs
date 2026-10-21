using System.Diagnostics;
using OpenDevelop.Profiler.Collection;
using OpenDevelop.Profiler.Cpu;
using OpenDevelop.Profiler.Heap;
using Xunit;

namespace OpenDevelop.Profiler.Tests;

/// <summary>Attaches to the real ProfilerFixture process: collection, the files it writes, and parsing them back.</summary>
public sealed class CaptureTests : IDisposable
{
    static readonly string FixtureDll = Path.Combine(AppContext.BaseDirectory, "ProfilerFixture", "ProfilerFixture.dll");

    readonly string workDirectory = Path.Combine(Path.GetTempPath(), "od-profiler-capture-" + Guid.NewGuid().ToString("N"));
    readonly List<Process> started = [];
    readonly List<int> launched = [];

    // A launch disposes its Process object before the test ends, so the process is remembered by id and found again.
    void KillOnDispose(int processId) => launched.Add(processId);

    public CaptureTests() => Directory.CreateDirectory(workDirectory);

    public void Dispose()
    {
        foreach (var id in launched)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
        foreach (var process in started)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            process.Dispose();
        }
        Directory.Delete(workDirectory, recursive: true);
    }

    Process StartFixture(string mode, int seconds)
    {
        Assert.True(File.Exists(FixtureDll), $"fixture not built: {FixtureDll}");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add(FixtureDll);
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(seconds.ToString());
        var process = ProcessTargets.StartAndWaitForDiagnostics(info, TimeSpan.FromSeconds(30));
        started.Add(process);
        var ready = process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        Assert.Equal("READY", ready);
        return process;
    }

    [Fact]
    public async Task CpuCapture_RecordsTheHotMethodAndItsCaller()
    {
        var fixture = StartFixture("cpu", 30);
        var path = Path.Combine(workDirectory, "cpu.nettrace");

        await using (var capture = CpuCapture.Start(fixture.Id, path, new CpuCaptureOptions { Duration = TimeSpan.FromSeconds(3) }))
        {
            var result = await capture.Completion;
            Assert.False(result.TargetExited);
        }

        var profile = NettraceCpuReader.Read(path);
        Assert.True(profile.Samples.Count > 100, $"only {profile.Samples.Count} samples");

        var functions = profile.BuildFunctions(ProfileFilter.Default);
        Assert.Contains("HotLeaf", functions[0].Name);
        Assert.Contains("ProfilerFixture", functions[0].Module, StringComparison.OrdinalIgnoreCase);
        var hotShare = (double)functions[0].Self / profile.CountSamples(ProfileFilter.Default);
        Assert.True(hotShare > 0.7, $"HotLeaf self share was {hotShare:P0}");

        var view = profile.BuildCallerCallee(functions[0].FrameId, ProfileFilter.Default);
        Assert.NotNull(view);
        Assert.Contains("Outer", view.Callers[0].Name);
    }

    [Fact]
    public async Task SuspendedLaunch_CapturesTheWorkDoneDuringStartup()
    {
        Assert.True(File.Exists(FixtureDll), $"fixture not built: {FixtureDll}");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add(FixtureDll);
        info.ArgumentList.Add("startup");
        info.ArgumentList.Add("60");
        var path = Path.Combine(workDirectory, "startup.nettrace");

        await using var launch = await SuspendedLaunch.StartAsync(info, TimeSpan.FromSeconds(30));
        KillOnDispose(launch.Process.Id);
        await using var capture = CpuCapture.Start(launch.Client, launch.Process.Id, path);
        launch.Resume();

        // The program does its 0.6 s of work first and prints READY after it: a capture that began once the
        // process was already running would find it asleep.
        var ready = await launch.Process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal("READY", ready);
        await capture.StopAsync();

        var profile = NettraceCpuReader.Read(path);
        var functions = profile.BuildFunctions(ProfileFilter.Default);
        Assert.Contains("HotLeaf", functions[0].Name);
        Assert.True(profile.CountSamples(ProfileFilter.Default) > 40, $"only {profile.CountSamples(ProfileFilter.Default)} samples");
    }

    [Fact]
    public async Task SuspendedLaunch_SurvivesTheProgramExitingAfterTheLaunchReturned()
    {
        // Regression: the Exited handler used a cancellation source that StartAsync had already disposed, so a program
        // that ended soon after launch raised an unhandled exception on a pool thread and took the whole process down.
        Assert.True(File.Exists(FixtureDll), $"fixture not built: {FixtureDll}");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add(FixtureDll);
        info.ArgumentList.Add("startup");
        info.ArgumentList.Add("1");

        await using var launch = await SuspendedLaunch.StartAsync(info, TimeSpan.FromSeconds(30));
        KillOnDispose(launch.Process.Id);
        launch.Resume();
        await launch.Process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken); // let any late Exited callback run

        Assert.True(launch.Process.HasExited);
    }

    [Fact]
    public async Task SuspendedLaunch_ExplainsAProgramThatIsNotDotNet()
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd", "/c exit 3")
            : new ProcessStartInfo("sh", "-c \"exit 3\"");
        info.CreateNoWindow = true;

        var error = await Assert.ThrowsAsync<ProfilingException>(() => SuspendedLaunch.StartAsync(info, TimeSpan.FromSeconds(30)));

        Assert.Contains("exited with code 3", error.Message);
    }

    [Fact]
    public async Task SuspendedLaunch_ExplainsAProgramThatNeverConnectsAndEndsIt()
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 60 127.0.0.1")
            : new ProcessStartInfo("sleep", "60");
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;

        var error = await Assert.ThrowsAsync<ProfilingException>(() => SuspendedLaunch.StartAsync(info, TimeSpan.FromSeconds(2)));

        Assert.Contains("did not connect", error.Message);
    }

    [Fact]
    public async Task HeapCapture_GivesUpOnATargetThatIsHeldAtStartup()
    {
        // The runtime is suspended and never collects, so the snapshot cannot finish: the capture must still return.
        Assert.True(File.Exists(FixtureDll), $"fixture not built: {FixtureDll}");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add(FixtureDll);
        info.ArgumentList.Add("startup");
        info.ArgumentList.Add("60");
        await using var launch = await SuspendedLaunch.StartAsync(info, TimeSpan.FromSeconds(30));
        KillOnDispose(launch.Process.Id);
        var path = Path.Combine(workDirectory, "held.nettrace");

        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAsync<ProfilingException>(() => HeapCapture.CaptureAsync(
            launch.Client, launch.Process.Id, path, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(40), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task CpuCapture_StopAsyncEndsTheCaptureWithACompleteFile()
    {
        var fixture = StartFixture("cpu", 30);
        var path = Path.Combine(workDirectory, "cpu.nettrace");
        await using var capture = CpuCapture.Start(fixture.Id, path);

        await Task.Delay(1500, TestContext.Current.CancellationToken);
        var result = await capture.StopAsync();

        Assert.False(result.TargetExited);
        Assert.True(new FileInfo(path).Length > 0);
        Assert.NotEmpty(NettraceCpuReader.Read(path).Samples);
        Assert.Same(result, await capture.StopAsync()); // idempotent
    }

    [Fact]
    public async Task CpuCapture_KeepsTheFileWhenTheTargetExits()
    {
        var fixture = StartFixture("cpu", 3);
        var path = Path.Combine(workDirectory, "cpu.nettrace");
        await using var capture = CpuCapture.Start(fixture.Id, path);

        var result = await capture.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(result.TargetExited);
        Assert.NotEmpty(NettraceCpuReader.Read(path).Samples);
    }

    [Fact]
    public async Task HeapCapture_CountsTheLiveObjectsOfTheFixture()
    {
        var fixture = StartFixture("heap", 60);
        var path = Path.Combine(workDirectory, "heap.nettrace");

        var result = await HeapCapture.CaptureAsync(fixture.Id, path, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.False(result.TargetExited);
        var snapshot = NettraceHeapReader.Read(path);
        var held = snapshot.Types.Single(t => t.Name == "HeldObject");
        Assert.Equal(12345, held.Count);
        Assert.True(snapshot.Types.Single(t => t.Name == "System.Byte[]").Count >= 12345);
        Assert.True(snapshot.HeapSize > held.Size);
        Assert.False(fixture.HasExited); // taking a snapshot must not kill or stop the target
    }

    [Fact]
    public async Task HeapCapture_GraphShowsWhoHoldsTheObjectsAndTheirPathToARoot()
    {
        var fixture = StartFixture("heap", 60);
        var path = Path.Combine(workDirectory, "heap.nettrace");
        await HeapCapture.CaptureAsync(fixture.Id, path, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var graph = NettraceHeapReader.Read(path).Graph;

        Assert.NotNull(graph);
        Assert.True(graph.RootCount > 0);
        // The fixture keeps its objects in a List<HeldObject>, so the array backing it holds all of them.
        var holders = graph.ReferencedBy("HeldObject");
        Assert.Equal("HeldObject[]", holders[0].TypeName);
        Assert.Equal(12345, holders[0].References);
        Assert.Contains(graph.References("HeldObject"), link => link.TypeName == "System.String");
        Assert.Contains(graph.References("HeldObject"), link => link.TypeName == "System.Byte[]");
        // And something rooted keeps that array alive: the path ends at the type and passes through the array.
        var route = graph.PathToRoot("HeldObject");
        Assert.NotNull(route);
        Assert.Equal("HeldObject", route.Types[^1]);
        Assert.Contains("HeldObject[]", route.Types);
        Assert.False(string.IsNullOrEmpty(route.RootKind));

        // The array keeps every held object, their names and payloads alive, so it retains far more than the
        // objects' own shallow size; the instance list leads with the largest retainer of the type.
        var snapshot = NettraceHeapReader.Read(path);
        var shallowHeld = snapshot.Types.Single(t => t.Name == "HeldObject").Size;
        var retained = graph.RetainedByType().ToDictionary(t => t.TypeName, t => t.RetainedSize);
        Assert.True(retained["HeldObject[]"] > shallowHeld, $"retained {retained["HeldObject[]"]} vs shallow {shallowHeld}");
        Assert.True(retained["HeldObject"] > shallowHeld);
        var instance = graph.Instances("HeldObject[]", 3)[0];
        Assert.True(instance.RetainedSize > shallowHeld);
        Assert.True(instance.RetainedSize <= retained["HeldObject[]"]);
        Assert.NotNull(graph.PathToRoot(instance.Node));

        // The fixture subscribes 200 listeners to a static event and never unsubscribes: the delegates that hold them
        // are the lead to follow, and they are what keeps the listeners reachable.
        var handlerTarget = graph.DelegateTargets(10).Single(t => t.TypeName == "Listener");
        Assert.True(handlerTarget.Delegates >= 200, handlerTarget.ToString());
        Assert.True(handlerTarget.RetainedSize > 0);
        Assert.Contains(graph.LargestRetainers(10), o => o.TypeName == "HeldObject[]");
    }

    [Fact]
    public async Task HeapCapture_ComparingTwoSnapshotsOfOneProcessShowsNoGrowthInTheHeldType()
    {
        var fixture = StartFixture("heap", 60);
        var first = Path.Combine(workDirectory, "heap1.nettrace");
        var second = Path.Combine(workDirectory, "heap2.nettrace");

        await HeapCapture.CaptureAsync(fixture.Id, first, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await HeapCapture.CaptureAsync(fixture.Id, second, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var diff = NettraceHeapReader.Read(second).Compare(NettraceHeapReader.Read(first));
        Assert.DoesNotContain(diff, d => d.Name == "HeldObject");
    }

    [Fact]
    public void ListManagedProcesses_IncludesAStartedDotNetProcessButNotTheCaller()
    {
        var fixture = StartFixture("cpu", 30);

        var processes = ProcessTargets.ListManagedProcesses();

        Assert.Contains(processes, p => p.Id == fixture.Id);
        Assert.DoesNotContain(processes, p => p.Id == Environment.ProcessId);
    }

    [Fact]
    public void StartAndWaitForDiagnostics_ExplainsANativeProcessThatHasNoDiagnosticPort()
    {
        // Not a .NET process, so it never publishes a diagnostic port; it just exits.
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd", "/c exit 3")
            : new ProcessStartInfo("sh", "-c \"exit 3\"");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;

        var error = Assert.Throws<ProfilingException>(() => ProcessTargets.StartAndWaitForDiagnostics(info, TimeSpan.FromSeconds(30)));

        Assert.Contains("exited with code 3", error.Message);
    }

    [Fact]
    public void StartAndWaitForDiagnostics_ExplainsAProgramThatCannotBeStarted()
    {
        var info = new ProcessStartInfo(Path.Combine(workDirectory, "does-not-exist.exe")) { UseShellExecute = false };

        var error = Assert.Throws<ProfilingException>(() => ProcessTargets.StartAndWaitForDiagnostics(info, TimeSpan.FromSeconds(5)));

        Assert.Contains("Could not start", error.Message);
    }

    [Fact]
    public async Task ReportLoader_RecognisesBothKindsOfCaptureAndRejectsOtherFiles()
    {
        var cpuFixture = StartFixture("cpu", 30);
        var cpuPath = Path.Combine(workDirectory, "cpu.nettrace");
        await using (var capture = CpuCapture.Start(cpuFixture.Id, cpuPath, new CpuCaptureOptions { Duration = TimeSpan.FromSeconds(1) }))
            await capture.Completion;
        var heapFixture = StartFixture("heap", 60);
        var heapPath = Path.Combine(workDirectory, "heap.nettrace");
        await HeapCapture.CaptureAsync(heapFixture.Id, heapPath, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var textPath = Path.Combine(workDirectory, "notes.nettrace");
        await File.WriteAllTextAsync(textPath, "this is not a trace", TestContext.Current.CancellationToken);

        Assert.Equal(CaptureKind.Cpu, ReportLoader.Detect(cpuPath));
        Assert.Equal(CaptureKind.Heap, ReportLoader.Detect(heapPath));
        Assert.Equal(CaptureKind.Unknown, ReportLoader.Detect(textPath));
        Assert.Equal(CaptureKind.Unknown, ReportLoader.Detect(Path.Combine(workDirectory, "missing.nettrace")));

        Assert.NotEmpty(ReportLoader.Load(cpuPath).Cpu!.Samples);
        Assert.Equal(12345, ReportLoader.Load(heapPath).Heap!.Types.Single(t => t.Name == "HeldObject").Count);
        Assert.Throws<ProfilingException>(() => ReportLoader.Load(textPath));
    }

    [Fact]
    public void CpuCapture_RejectsAProcessThatDoesNotExist()
    {
        var path = Path.Combine(workDirectory, "none.nettrace");

        Assert.Throws<ProfilingException>(() => CpuCapture.Start(int.MaxValue - 1, path));
    }
}
