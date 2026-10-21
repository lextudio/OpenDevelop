using System.Diagnostics;
using OpenDevelop.Profiler.Collection;
using OpenDevelop.Profiler.Cpu;
using OpenDevelop.Profiler.Dump;
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

    Process StartFixture(string mode, int seconds, params string[] extra)
    {
        Assert.True(File.Exists(FixtureDll), $"fixture not built: {FixtureDll}");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add(FixtureDll);
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(seconds.ToString());
        foreach (var argument in extra)
            info.ArgumentList.Add(argument);
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
    public async Task AllocationCapture_NamesTheTypeThatTakesTheMemoryAndTheMethodThatAllocatesIt()
    {
        var fixture = StartFixture("alloc", 30);
        var path = Path.Combine(workDirectory, "allocation.nettrace");

        await using (var capture = CpuCapture.Start(fixture.Id, path, new CpuCaptureOptions { Mode = CaptureMode.Allocation, Duration = TimeSpan.FromSeconds(3) }))
            await capture.Completion;

        Assert.Equal(CaptureKind.Allocation, ReportLoader.Detect(path));
        var profile = NettraceAllocationReader.Read(path);
        Assert.Equal(ProfileUnit.Kilobytes, profile.Unit);
        Assert.True(profile.Samples.Count > 20, $"only {profile.Samples.Count} allocation samples");
        Assert.InRange(profile.SampleIntervalMs, 20, 400); // about 100 KB per tick

        var filter = ProfileFilter.Default;
        var functions = profile.BuildFunctions(filter);
        var top = functions[0];
        Assert.Equal("System.Byte[]", top.Name);
        Assert.Equal(NettraceAllocationReader.AllocatedTypeModule, top.Module);
        var callers = profile.BuildCallerCallee(top.FrameId, filter)!.Callers;
        Assert.Contains("Allocator.Buffers", callers[0].Name);
        var shareOfBytes = (double)top.Self / profile.CountSamples(filter);
        Assert.True(shareOfBytes > 0.8, $"byte arrays were {shareOfBytes:P0} of the allocations");
    }

    [Theory]
    [InlineData(CaptureMode.Cpu, "cpu")]
    [InlineData(CaptureMode.Allocation, "alloc")]
    public async Task Capture_CarriesTheRuntimeCountersAlignedWithTheSamples(CaptureMode mode, string fixtureMode)
    {
        var fixture = StartFixture(fixtureMode, 30);
        var path = Path.Combine(workDirectory, "counters.nettrace");

        await using (var capture = CpuCapture.Start(fixture.Id, path, new CpuCaptureOptions { Mode = mode, Duration = TimeSpan.FromSeconds(4) }))
            await capture.Completion;

        var counters = NettraceCounterReader.Read(path);
        var heap = counters.Single(c => c.Name == "gc-heap-size");
        Assert.True(heap.Values.Count >= 2, $"{heap.Values.Count} heap size points");
        Assert.True(heap.Last > 0);
        Assert.Contains(counters, c => c.Name == "cpu-usage");
        Assert.Contains(counters, c => c.Name == "gen-0-gc-count");
        Assert.All(counters, c => Assert.Equal(c.TimesMs.Count, c.Values.Count));

        // Both are measured on the session clock, so the first counter point falls within the sampled span, not far outside it.
        var profile = mode == CaptureMode.Cpu ? NettraceCpuReader.Read(path) : NettraceAllocationReader.Read(path);
        var firstPoint = heap.TimesMs[0] - profile.OriginMs;
        Assert.InRange(firstPoint, -1500, profile.DurationMs + 1500);
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

    [Theory]
    [InlineData(",suspend")]
    [InlineData(",nosuspend")]
    public async Task DiagnosticPortListener_ProfilesAProgramStartedElsewhereThatConnectsToTheAddress(string mode)
    {
        Assert.True(File.Exists(FixtureDll), $"fixture not built: {FixtureDll}");
        var address = SuspendedLaunch.CreatePortAddress(OperatingSystem.IsWindows(), Path.GetTempPath());
        var path = Path.Combine(workDirectory, "remote.nettrace");

        // Listen first, then start the program the way an operator would elsewhere: told where to connect, held at startup.
        var listening = DiagnosticPortListener.ListenAsync(address, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add(FixtureDll);
        info.ArgumentList.Add("startup");
        info.ArgumentList.Add("60");
        info.Environment["DOTNET_DiagnosticPorts"] = address + mode;
        using var program = Process.Start(info)!;
        KillOnDispose(program.Id);

        await using var listener = await listening;
        await using var capture = CpuCapture.Start(listener.Client, program.Id, path);
        listener.Resume();
        var ready = await program.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal("READY", ready);
        await capture.StopAsync();

        Assert.False(string.IsNullOrEmpty(listener.DisplayName));
        var functions = NettraceCpuReader.Read(path).BuildFunctions(ProfileFilter.Default);
        // Held at startup, the capture sees the startup work from its first instruction; otherwise the program was already running.
        if (mode == ",suspend")
            Assert.Contains("HotLeaf", functions[0].Name);
        else
            Assert.NotEmpty(functions);
    }

    [Fact]
    public async Task DiagnosticPortListener_SaysHowToConnectWhenNothingDoes()
    {
        var address = SuspendedLaunch.CreatePortAddress(OperatingSystem.IsWindows(), Path.GetTempPath());

        var error = await Assert.ThrowsAsync<ProfilingException>(
            () => DiagnosticPortListener.ListenAsync(address, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        Assert.Contains("DOTNET_DiagnosticPorts=" + address, error.Message);
    }

    [Fact]
    public async Task DiagnosticPortListener_RefusesAnEmptyAddress() =>
        await Assert.ThrowsAsync<ProfilingException>(() => DiagnosticPortListener.ListenAsync("  ", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

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
    public async Task HeapCapture_RefusesASnapshotWhoseTargetDiedWhileItWasBeingWritten()
    {
        // Millions of objects, so the dump streams for long enough to end the process in the middle of it.
        var fixture = StartFixture("heap", 120, "3000000");
        var path = Path.Combine(workDirectory, "heap.nettrace");
        var capture = HeapCapture.CaptureAsync(fixture.Id, path, TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);

        // Wait until a good part of the dump is on disk, then take the process away.
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(90) && (!File.Exists(path) || new FileInfo(path).Length < 4_000_000))
            await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(path) && new FileInfo(path).Length >= 4_000_000, "the snapshot never started streaming");
        fixture.Kill(entireProcessTree: true);

        var error = await Assert.ThrowsAsync<ProfilingException>(() => capture).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Contains("incomplete", error.Message);
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
    public async Task Capture_WorksInAFolderWhoseNameHasSpacesAndNonAsciiCharacters()
    {
        // F-13: the path goes to the runtime, the file system and the readers; none of them may mangle it.
        var folder = Path.Combine(workDirectory, "捕获 文件夹 with spaces ü");
        var store = new OpenDevelop.Profiler.Sessions.ProfilingSessionStore(folder);
        var session = store.Create(OpenDevelop.Profiler.Sessions.SessionKind.Cpu, "应用", 1);
        var fixture = StartFixture("cpu", 30);

        await using (var capture = CpuCapture.Start(fixture.Id, session.ArtifactPath, new CpuCaptureOptions { Duration = TimeSpan.FromSeconds(2) }))
            await capture.Completion;
        store.Finish(session, OpenDevelop.Profiler.Sessions.SessionStatus.Completed);

        Assert.Equal(CaptureKind.Cpu, ReportLoader.Detect(session.ArtifactPath));
        Assert.Contains("HotLeaf", ReportLoader.Load(session.ArtifactPath).Cpu!.BuildFunctions(ProfileFilter.Default)[0].Name);
        Assert.Equal("应用", Assert.Single(store.List()).Info.DisplayName);
    }

    [Fact]
    public async Task HeapContentAnalyzer_FindsDuplicateStringsAndSparseArraysInARealProcess()
    {
        var fixture = StartFixture("contents", 90);

        var insights = await HeapContentAnalyzer.AnalyzeProcessAsync(fixture.Id, TestContext.Current.CancellationToken);

        Assert.True(insights.ObjectsScanned > 1000, $"{insights.ObjectsScanned} objects");
        var duplicate = insights.DuplicateStrings.Single(s => s.Preview.StartsWith("duplicate-string-held-by-the-fixture"));
        Assert.True(duplicate.Copies >= 2000, $"{duplicate.Copies} copies");
        Assert.Equal("duplicate-string-held-by-the-fixture-0123456789".Length, duplicate.Length);
        Assert.True(duplicate.WastedBytes >= 1999 * 100, $"{duplicate.WastedBytes} bytes wasted");
        var array = insights.SparseArrays.Single(a => a.Type == "System.Int32[]" && a.Length == 200_000);
        Assert.Equal(199_997, array.EmptyElements);
        Assert.True(array.WastedBytes >= 199_997 * 4);
        Assert.True(insights.TotalWastedByDuplicateStrings >= duplicate.WastedBytes);

        // It is kept beside the capture and reopens with it.
        var path = Path.Combine(workDirectory, ContentInsights.FileName);
        insights.Save(path);
        var loaded = ContentInsights.TryLoad(path)!;
        Assert.Equal(insights.ObjectsScanned, loaded.ObjectsScanned);
        Assert.Equal(duplicate.Copies, loaded.DuplicateStrings.Single(s => s.Preview == duplicate.Preview).Copies);
        Assert.Null(ContentInsights.TryLoad(Path.Combine(workDirectory, "missing.json")));
    }

    [Fact]
    public async Task DumpCapture_ThenDumpReader_GivesTheHeapTheContentsAndTheThreadsOfTheProcess()
    {
        var fixture = StartFixture("contents", 120);
        var path = Path.Combine(workDirectory, "process.dmp");

        var result = await DumpCapture.CaptureAsync(fixture.Id, path, TestContext.Current.CancellationToken);

        Assert.False(result.TargetExited);
        Assert.False(fixture.HasExited); // a dump holds the process still while it is written, it does not end it
        Assert.True(DumpReader.IsDumpFile(path));
        Assert.False(DumpReader.IsDumpFile(Path.Combine(AppContext.BaseDirectory, "ProfilerFixture", "ProfilerFixture.dll")));
        Assert.Equal(CaptureKind.Dump, ReportLoader.Detect(path));

        var report = ReportLoader.Load(path).Dump!;
        Assert.False(string.IsNullOrEmpty(report.Info.RuntimeVersion));
        Assert.True(report.Info.FileBytes > 1_000_000);

        // The heap, as a snapshot: the held strings are counted, and the graph answers who holds them alive.
        var strings = report.Heap.Types.Single(t => t.Name == "System.String");
        Assert.True(strings.Count >= 2000, $"{strings.Count} strings");
        Assert.NotNull(report.Heap.Graph);
        var path2 = report.Heap.Graph!.PathToRoot("System.Int32[]");
        Assert.NotNull(path2);
        Assert.Contains(path2.Types, t => t.StartsWith("System.Int32[]"));

        // What the contents hold, from the same pass.
        Assert.Contains(report.Contents.DuplicateStrings, s => s.Preview.StartsWith("duplicate-string-held-by-the-fixture") && s.Copies >= 2000);
        Assert.Contains(report.Contents.SparseArrays, a => a.Type == "System.Int32[]" && a.Length == 200_000);

        // And the threads: the program's main thread is sleeping in its Main.
        Assert.Contains(report.Threads, t => t.Frames.Any(f => f.Contains("Main", StringComparison.Ordinal)));
    }

    [Fact]
    public void DumpReader_ExplainsAFileThatIsNotADumpAndOneThatHasNoRuntime()
    {
        var text = Path.Combine(workDirectory, "notes.dmp");
        File.WriteAllText(text, "this is not a memory dump");

        Assert.False(DumpReader.IsDumpFile(text));
        Assert.False(DumpReader.IsDumpFile(Path.Combine(workDirectory, "missing.dmp")));
        Assert.Throws<ProfilingException>(() => DumpReader.Read(text, TestContext.Current.CancellationToken));
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
    public async Task SuspendedLaunch_ExplainsAProgramThatCannotBeStarted()
    {
        var info = new ProcessStartInfo(Path.Combine(workDirectory, "does-not-exist.exe"));

        var error = await Assert.ThrowsAsync<ProfilingException>(() => SuspendedLaunch.StartAsync(info, TimeSpan.FromSeconds(5)));

        Assert.Contains("Could not start", error.Message);
    }

    [Fact]
    public async Task ReportLoader_ReportsAFileItCannotParseAsProfilingException()
    {
        // A real capture cut short in the middle: recognisable at its start, unreadable as a whole.
        var fixture = StartFixture("cpu", 30);
        var path = Path.Combine(workDirectory, "cpu.nettrace");
        await using (var capture = CpuCapture.Start(fixture.Id, path, new CpuCaptureOptions { Duration = TimeSpan.FromSeconds(2) }))
            await capture.Completion;
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var cut = Path.Combine(workDirectory, "cut.nettrace");
        await File.WriteAllBytesAsync(cut, bytes.AsMemory(0, bytes.Length / 2).ToArray(), TestContext.Current.CancellationToken);

        // Whatever the trace reader makes of it, a caller must only ever see a ProfilingException.
        try
        {
            ReportLoader.Load(cut);
        }
        catch (Exception exception)
        {
            Assert.IsType<ProfilingException>(exception);
        }
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
