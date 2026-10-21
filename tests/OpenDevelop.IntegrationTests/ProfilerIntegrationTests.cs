using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// The Profiler end to end inside the real IDE: a capture of a real process through the add-in's capture
/// coordinator, the report it opens, and reopening that report from its file. The probes
/// (<c>od.profiler.*</c>) run the same coordinator and read the same views the menu commands and a user's
/// clicks do; only the process picker, which needs a person, is skipped.
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class ProfilerIntegrationTests : IAsyncDisposable
{
	const int HeldObjectCount = 12345;

	readonly OpenDevelopAppFixture app;
	readonly List<Process> started = [];

	public ProfilerIntegrationTests(OpenDevelopAppFixture app) => this.app = app;

	public ValueTask DisposeAsync()
	{
		foreach (var process in started)
		{
			// Kill throws Win32Exception as well as InvalidOperationException when the process is
			// already exiting, and either one here would skip the remaining disposals and hide the
			// real test failure.
			try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
			process.Dispose();
		}
		return ValueTask.CompletedTask;
	}

	[Fact]
	public async Task CpuCapture_OpensAReportWhoseCallTreeNamesTheHotMethod()
	{
		var fixture = await StartFixtureAsync("cpu", 60);

		var capture = await app.InvokeAsync("od.profiler.capture-cpu", fixture.Id, 3);
		Assert.True(capture.GetProperty("success").GetBoolean(), capture.ToString());
		Assert.True(File.Exists(capture.GetProperty("path").GetString()));

		var report = await app.InvokeAsync("od.profiler.report");
		Assert.Equal("cpu", report.GetProperty("kind").GetString());
		var state = report.GetProperty("state");
		Assert.Equal("CallTree", state.GetProperty("mode").GetString());
		Assert.True(state.GetProperty("samples").GetInt32() > 100);
		var rows = state.GetProperty("tree").EnumerateArray().ToList();
		var hot = rows.Single(row => row.GetProperty("name").GetString()!.Contains("HotLeaf"));
		Assert.True(hot.GetProperty("selfMs").GetDouble() > 0);
		// The hot path opens expanded, so the caller sits right above it in the tree.
		var hotIndex = rows.IndexOf(hot);
		Assert.Contains("Outer", rows[hotIndex - 1].GetProperty("name").GetString());
		Assert.True(rows[hotIndex].GetProperty("depth").GetInt32() > rows[hotIndex - 1].GetProperty("depth").GetInt32());
	}

	[Fact]
	public async Task CpuReport_FunctionsCallerCalleeFindAndRange()
	{
		var fixture = await StartFixtureAsync("cpu", 60);
		var capture = await app.InvokeAsync("od.profiler.capture-cpu", fixture.Id, 3);
		Assert.True(capture.GetProperty("success").GetBoolean(), capture.ToString());
		await app.InvokeAsync("od.profiler.report");

		// Functions: ordered by self time, so the hot method leads.
		Assert.True((await app.InvokeAsync("od.profiler.cpu.set-mode", "Functions")).GetProperty("success").GetBoolean());
		var functions = (await app.InvokeAsync("od.profiler.report")).GetProperty("state").GetProperty("functions");
		Assert.Contains("HotLeaf", functions[0].GetProperty("name").GetString());

		// Caller/Callee of the hot method: called by Outer, calls nothing, all of its time is its own body.
		Assert.True((await app.InvokeAsync("od.profiler.cpu.show-caller-callee", "HotLeaf")).GetProperty("result").GetProperty("found").GetBoolean());
		var neighbours = (await app.InvokeAsync("od.profiler.report")).GetProperty("state");
		Assert.Equal("CallerCallee", neighbours.GetProperty("mode").GetString());
		var callerCallee = neighbours.GetProperty("callerCallee");
		Assert.Contains("HotLeaf", callerCallee.GetProperty("function").GetString());
		Assert.Contains("Outer", callerCallee.GetProperty("callers")[0].GetString());
		Assert.Empty(callerCallee.GetProperty("callees").EnumerateArray());

		// Ring Diagram: the same tree as concentric rings, focused on the root, one ring per call depth.
		Assert.True((await app.InvokeAsync("od.profiler.cpu.set-mode", "RingDiagram")).GetProperty("success").GetBoolean());
		var ring = (await app.InvokeAsync("od.profiler.report")).GetProperty("state").GetProperty("ring");
		Assert.Equal("Root", ring.GetProperty("focus").GetString());
		Assert.True(ring.GetProperty("slices").GetInt32() >= 3, ring.ToString());
		Assert.True(ring.GetProperty("levels").GetInt32() >= 3, ring.ToString());

		// Find: the floating find box reports its match count, and a miss reports none.
		var found = (await app.InvokeAsync("od.profiler.cpu.find", "HotLeaf")).GetProperty("result");
		Assert.Equal((1, 1), (found.GetProperty("current").GetInt32(), found.GetProperty("total").GetInt32()));
		Assert.Equal(0, (await app.InvokeAsync("od.profiler.cpu.find", "no-such-function")).GetProperty("result").GetProperty("total").GetInt32());

		// Range: selecting part of the timeline recomputes every view for it; clearing restores the capture.
		var whole = (await app.InvokeAsync("od.profiler.report")).GetProperty("state");
		var duration = whole.GetProperty("durationMs").GetDouble();
		await app.InvokeAsync("od.profiler.cpu.select-range", duration * 0.25, duration * 0.75);
		var ranged = (await app.InvokeAsync("od.profiler.report")).GetProperty("state");
		Assert.True(ranged.GetProperty("hasRange").GetBoolean());
		Assert.Contains(" to ", ranged.GetProperty("status").GetString());
		await app.InvokeAsync("od.profiler.cpu.clear-range");
		Assert.False((await app.InvokeAsync("od.profiler.report")).GetProperty("state").GetProperty("hasRange").GetBoolean());
	}

	[Fact]
	public async Task HeapSnapshot_CountsTheObjectsTheFixtureHolds_AndComparesTwoSnapshots()
	{
		var fixture = await StartFixtureAsync("heap", 120);

		var first = await app.InvokeAsync("od.profiler.capture-heap", fixture.Id);
		Assert.True(first.GetProperty("success").GetBoolean(), first.ToString());
		var report = await app.InvokeAsync("od.profiler.report");
		Assert.Equal("heap", report.GetProperty("kind").GetString());
		var held = report.GetProperty("state").GetProperty("rows").EnumerateArray()
			.Single(row => row.GetProperty("name").GetString() == "HeldObject");
		Assert.Equal(HeldObjectCount, held.GetProperty("count").GetInt32());

		// Selecting a type shows who holds it alive (path to a GC root) and who references it.
		var selected = await app.InvokeAsync("od.profiler.heap.select-type", "HeldObject");
		Assert.True(selected.GetProperty("result").GetProperty("selected").GetBoolean(), selected.ToString());
		var details = selected.GetProperty("result").GetProperty("details");
		var path = details.GetProperty("path").EnumerateArray().Select(item => item.GetString()).ToList();
		Assert.StartsWith("GC root", path[0]);
		Assert.Contains(path, line => line!.EndsWith("→ HeldObject[]"));
		Assert.EndsWith("→ HeldObject", path[^1]);
		Assert.Equal("HeldObject[]", details.GetProperty("referencedBy")[0].GetProperty("name").GetString());
		Assert.Contains(details.GetProperty("references").EnumerateArray(), link => link.GetProperty("name").GetString() == "System.String");

		// Insights: the fixture's 200 listeners are kept alive by handlers on a static event, and the array holding the
		// held objects is among the objects keeping the most memory alive.
		var insights = details.GetProperty("insights");
		var listener = insights.GetProperty("handlerTargets").EnumerateArray().Single(t => t.GetProperty("type").GetString() == "Listener");
		Assert.True(listener.GetProperty("delegates").GetInt64() >= 200, listener.ToString());
		Assert.Contains(insights.GetProperty("retainers").EnumerateArray(), r => r.GetProperty("type").GetString() == "HeldObject[]");

		// Retained size: the held objects keep their names and payloads alive, so a type's retained size exceeds its own,
		// and the largest instance of the array that holds them all retains what the whole list does.
		var shallow = report.GetProperty("state").GetProperty("rows").EnumerateArray()
			.Single(row => row.GetProperty("name").GetString() == "HeldObject").GetProperty("size").GetInt64();
		Assert.True(details.GetProperty("retained").GetInt64() > shallow, details.ToString());
		var instances = details.GetProperty("instances").EnumerateArray().ToList();
		Assert.NotEmpty(instances);
		Assert.True(instances[0].GetProperty("retained").GetInt64() >= instances[0].GetProperty("size").GetInt64());

		var array = (await app.InvokeAsync("od.profiler.heap.select-type", "HeldObject[]")).GetProperty("result").GetProperty("details");
		Assert.True(array.GetProperty("instances")[0].GetProperty("retained").GetInt64() > shallow);
		var instancePath = (await app.InvokeAsync("od.profiler.heap.select-instance", 0)).GetProperty("result").GetProperty("details")
			.GetProperty("path").EnumerateArray().Select(item => item.GetString()).ToList();
		Assert.StartsWith("GC root", instancePath[0]);
		Assert.EndsWith("→ HeldObject[]", instancePath[^1]);

		// The type filter narrows the table.
		await app.InvokeAsync("od.profiler.heap.filter", "HeldObject");
		var filtered = (await app.InvokeAsync("od.profiler.report")).GetProperty("state").GetProperty("rows").EnumerateArray()
			.Select(row => row.GetProperty("name").GetString()).ToList();
		Assert.All(filtered, name => Assert.Contains("HeldObject", name));
		await app.InvokeAsync("od.profiler.heap.filter", "");

		// A second snapshot of the same process: the held type did not grow, so it is absent from the diff.
		var second = await app.InvokeAsync("od.profiler.capture-heap", fixture.Id);
		Assert.True(second.GetProperty("success").GetBoolean(), second.ToString());
		var compared = await app.InvokeAsync("od.profiler.heap.compare", first.GetProperty("path").GetString()!);
		Assert.True(compared.GetProperty("success").GetBoolean(), compared.ToString());
		var diff = (await app.InvokeAsync("od.profiler.report")).GetProperty("state");
		Assert.True(diff.GetProperty("comparing").GetBoolean());
		Assert.DoesNotContain(diff.GetProperty("rows").EnumerateArray(), row => row.GetProperty("name").GetString() == "HeldObject");
		await app.InvokeAsync("od.profiler.heap.stop-compare");
		Assert.False((await app.InvokeAsync("od.profiler.report")).GetProperty("state").GetProperty("comparing").GetBoolean());
	}

	[Fact]
	public async Task Sessions_AreRecordedAndTheirCapturesReopenAsReports()
	{
		var fixture = await StartFixtureAsync("cpu", 60);
		var capture = await app.InvokeAsync("od.profiler.capture-cpu", fixture.Id, 2);
		var path = capture.GetProperty("path").GetString()!;

		var sessions = await app.InvokeAsync("od.profiler.sessions");
		var recorded = sessions.GetProperty("sessions").EnumerateArray().First(session => session.GetProperty("processId").GetInt32() == fixture.Id);
		Assert.Equal("Cpu", recorded.GetProperty("kind").GetString());
		Assert.Equal("Completed", recorded.GetProperty("status").GetString());
		Assert.True(recorded.GetProperty("artifactExists").GetBoolean());

		// Reopening needs nothing but the file: close everything, open the raw capture again.
		var reopened = await app.InvokeAsync("od.open-file", path);
		Assert.True(reopened.GetProperty("opened").GetBoolean(), reopened.ToString());
		Assert.EndsWith("ProfilerReportViewContent", reopened.GetProperty("viewContentType").GetString());
		var report = await app.InvokeAsync("od.profiler.report");
		Assert.Equal("cpu", report.GetProperty("kind").GetString());

		// The Profiling Sessions window lists the session and can delete it, capture file included.
		var id = recorded.GetProperty("id").GetString()!;
		var window = await app.InvokeAsync("od.profiler.sessions.window");
		var row = window.GetProperty("sessions").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
		Assert.Equal("CPU", row.GetProperty("kind").GetString());
		Assert.True(row.GetProperty("size").GetInt64() > 0);
		var deleted = await app.InvokeAsync("od.profiler.sessions.delete", id);
		Assert.True(deleted.GetProperty("success").GetBoolean(), deleted.ToString());
		Assert.DoesNotContain(deleted.GetProperty("sessions").EnumerateArray(), r => r.GetProperty("id").GetString() == id);
		Assert.False(File.Exists(path));
	}

	[Fact]
	public async Task LaunchedProgram_IsCapturedFromItsFirstInstruction()
	{
		// "startup" does its hot work in the first 0.6 s, before it prints READY. A capture that attached to the
		// running process could only ever see it asleep; this one holds the runtime until recording has begun.
		var launched = await app.InvokeAsync("od.profiler.launch-cpu", LocateFixtureDll(), "startup 60", 3);
		Assert.True(launched.GetProperty("success").GetBoolean(), launched.ToString());

		var report = await app.InvokeAsync("od.profiler.report");
		Assert.Equal("cpu", report.GetProperty("kind").GetString());
		var state = report.GetProperty("state");
		Assert.True(state.GetProperty("samples").GetInt32() > 40, state.ToString());
		await app.InvokeAsync("od.profiler.cpu.set-mode", "Functions");
		var functions = (await app.InvokeAsync("od.profiler.report")).GetProperty("state").GetProperty("functions");
		Assert.Contains("HotLeaf", functions[0].GetProperty("name").GetString());
	}

	[Fact]
	public async Task ProfileCurrentProject_OpensTheProjectBuildsItLaunchesItAndAnalysesTheRun()
	{
		// The whole user journey, with nothing handed to the profiler but the open project: open the solution, build it
		// with the IDE's own build, then profile the current project the way Run would start it.
		await app.InvokeAsync("od.open-solution", LocateFixtureFile("ProfilerFixture.slnx"));
		var build = await app.InvokeAsync("od.build-solution");
		Assert.True(build.GetProperty("success").GetBoolean(), build.ToString());
		Assert.Equal(0, build.GetProperty("errorCount").GetInt32());

		var profiled = await app.InvokeAsync("od.profiler.profile-project", 3);
		Assert.True(profiled.GetProperty("success").GetBoolean(), profiled.ToString());

		// The report is the project's own run: the hot method it was written to spin in, called from its caller.
		var report = await app.InvokeAsync("od.profiler.report");
		Assert.Equal("cpu", report.GetProperty("kind").GetString());
		Assert.True(report.GetProperty("state").GetProperty("samples").GetInt32() > 100);
		await app.InvokeAsync("od.profiler.cpu.set-mode", "Functions");
		var functions = (await app.InvokeAsync("od.profiler.report")).GetProperty("state").GetProperty("functions");
		Assert.Contains("HotLeaf", functions[0].GetProperty("name").GetString());
		Assert.True((await app.InvokeAsync("od.profiler.cpu.show-caller-callee", "HotLeaf")).GetProperty("result").GetProperty("found").GetBoolean());
		var callers = (await app.InvokeAsync("od.profiler.report")).GetProperty("state").GetProperty("callerCallee").GetProperty("callers");
		Assert.Contains("Outer", callers[0].GetString());

		// The session is recorded against the project, and the program the profiler started does not outlive the capture.
		var sessions = (await app.InvokeAsync("od.profiler.sessions")).GetProperty("sessions").EnumerateArray().ToList();
		Assert.Contains(sessions, s => s.GetProperty("name").GetString() == "ProfilerFixture" && s.GetProperty("status").GetString() == "Completed");
		// The profiler stops the program when the capture ends, but that exit is asynchronous: poll for
		// it rather than assuming a fixed delay.
		var exited = await OpenDevelopAppFixture.PollUntilAsync(
			() => Task.FromResult(Process.GetProcessesByName("ProfilerFixture").Length == 0), TimeSpan.FromSeconds(10));
		Assert.True(exited, "The profiled program outlived its capture.");
	}

	[Fact]
	public async Task ProfileSelectedTests_SamplesTheTestHostAndOpensTheReport()
	{
		await app.EnsureSolutionOpenAsync(app.FixtureSolutionPath);
		var discovered = await OpenDevelopAppFixture.PollUntilAsync(async () =>
		{
			var tree = await app.InvokeAsync("od.unit-test.tree");
			return tree.GetProperty("tests").GetArrayLength() > 0 && tree.ToString().Contains("AlwaysPasses");
		}, TimeSpan.FromSeconds(60));
		Assert.True(discovered, "The sample tests were not discovered.");

		// The test host is started held at startup, sampled while the test runs, and the report opens when it exits.
		var profiled = await app.InvokeAsync("od.profiler.profile-test", "AlwaysPasses");
		Assert.True(profiled.GetProperty("success").GetBoolean(), profiled.ToString());
		Assert.EndsWith("cpu.nettrace", profiled.GetProperty("path").GetString());

		var report = await app.InvokeAsync("od.profiler.report");
		Assert.Equal("cpu", report.GetProperty("kind").GetString());
		Assert.True(report.GetProperty("state").GetProperty("durationMs").GetDouble() >= 0);
		var sessions = (await app.InvokeAsync("od.profiler.sessions")).GetProperty("sessions").EnumerateArray();
		Assert.Contains(sessions, session => session.GetProperty("name").GetString()!.Contains("SampleTestProject")
			&& session.GetProperty("status").GetString() == "Completed");
	}

	[Fact]
	public async Task GoToSource_FindsTheDeclarationOfTheHotMethodInTheOpenSolution()
	{
		await app.InvokeAsync("od.open-solution", LocateFixtureFile("ProfilerFixture.slnx"));
		var fixture = await StartFixtureAsync("cpu", 90);
		var capture = await app.InvokeAsync("od.profiler.capture-cpu", fixture.Id, 2);
		Assert.True(capture.GetProperty("success").GetBoolean(), capture.ToString());
		await app.InvokeAsync("od.profiler.report");

		// The language service loads the project in the background, so allow it time to answer.
		JsonElement source = default;
		for (var attempt = 0; attempt < 45; attempt++)
		{
			source = await app.InvokeAsync("od.profiler.cpu.find-source", "HotLeaf");
			if (source.GetProperty("found").GetBoolean())
				break;
			await Task.Delay(2000);
		}

		Assert.True(source.GetProperty("found").GetBoolean(), "Go to Source never found HotLeaf: " + source);
		Assert.EndsWith("Program.cs", source.GetProperty("file").GetString());
		Assert.True(source.GetProperty("line").GetInt32() > 1);
	}

	[Fact]
	public async Task AspNetCoreService_UnderRequestLoad_ShowsTheHandlerUnderTheFrameworkPipeline()
	{
		var (service, ready) = await StartProgramAsync("ProfilerWebFixture");
		var url = ready["READY ".Length..].Split(',')[0];
		using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) };

		// Keep four requests in flight for longer than the capture runs, so the sampler always finds work.
		using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
		var load = Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
		{
			while (!stop.IsCancellationRequested)
			{
				try { await http.GetStringAsync("/work", stop.Token); }
				catch (OperationCanceledException) { }
			}
		}));
		var capture = await app.InvokeAsync("od.profiler.capture-cpu", service.Id, 3);
		await load;
		Assert.True(capture.GetProperty("success").GetBoolean(), capture.ToString());

		var report = await app.InvokeAsync("od.profiler.report");
		Assert.Equal("cpu", report.GetProperty("kind").GetString());
		Assert.True(report.GetProperty("state").GetProperty("samples").GetInt32() > 100, report.ToString());
		// Requests are served on several threads, not just the one that started the host.
		Assert.True(report.GetProperty("state").GetProperty("threadChoices").GetInt32() >= 3, report.ToString());
		await app.InvokeAsync("od.profiler.cpu.set-mode", "Functions");
		var functions = (await app.InvokeAsync("od.profiler.report")).GetProperty("state").GetProperty("functions");
		// The handler's loop is among the three functions doing the most work; the rest is the framework folded into
		// [External Code], which legitimately takes a share of a service this small.
		Assert.Contains(functions.EnumerateArray().Take(3), f => f.GetProperty("name").GetString()!.Contains("WebWork.Spin"));
		Assert.True((await app.InvokeAsync("od.profiler.cpu.show-caller-callee", "WebWork.Spin")).GetProperty("result").GetProperty("found").GetBoolean());
		var spinCallers = (await app.InvokeAsync("od.profiler.report")).GetProperty("state").GetProperty("callerCallee").GetProperty("callers");
		Assert.Contains("WebWork.Handle", spinCallers[0].GetString());

		// Just My Code: the handler is shown, the Kestrel and routing frames above it are folded away...
		Assert.True((await app.InvokeAsync("od.profiler.cpu.find", "WebWork.Handle")).GetProperty("result").GetProperty("total").GetInt32() >= 1);
		Assert.Equal(0, (await app.InvokeAsync("od.profiler.cpu.find", "Microsoft.AspNetCore")).GetProperty("result").GetProperty("total").GetInt32());
		Assert.True((await app.InvokeAsync("od.profiler.cpu.find", "[External Code]")).GetProperty("result").GetProperty("total").GetInt32() >= 1);
		// ...and turning it off reveals them.
		await app.InvokeAsync("od.profiler.cpu.just-my-code", false);
		Assert.True((await app.InvokeAsync("od.profiler.cpu.find", "Microsoft.AspNetCore")).GetProperty("result").GetProperty("total").GetInt32() >= 1);
	}

	[Fact]
	public async Task WpfApplication_ShowsTheBusyUiThreadWithTheDispatcherAboveIt()
	{
		Assert.SkipUnless(OperatingSystem.IsWindows(), "The WPF fixture needs the Windows Desktop runtime.");
		var (wpf, _) = await StartProgramAsync("ProfilerWpfFixture");

		var capture = await app.InvokeAsync("od.profiler.capture-cpu", wpf.Id, 3);
		Assert.True(capture.GetProperty("success").GetBoolean(), capture.ToString());

		var report = await app.InvokeAsync("od.profiler.report");
		Assert.Equal("cpu", report.GetProperty("kind").GetString());
		var state = report.GetProperty("state");
		Assert.True(state.GetProperty("samples").GetInt32() > 100, report.ToString());
		// The UI thread and the runtime's own threads: more than one thread to filter by.
		Assert.True(state.GetProperty("threadChoices").GetInt32() >= 2, report.ToString());
		await app.InvokeAsync("od.profiler.cpu.set-mode", "Functions");
		var functions = (await app.InvokeAsync("od.profiler.report")).GetProperty("state").GetProperty("functions");
		Assert.Contains("UiWork.Spin", functions[0].GetProperty("name").GetString());

		// Just My Code hides WPF's dispatcher frames; turning it off shows the message loop the timer runs on.
		Assert.True((await app.InvokeAsync("od.profiler.cpu.find", "UiWork.Spin")).GetProperty("result").GetProperty("total").GetInt32() >= 1);
		Assert.Equal(0, (await app.InvokeAsync("od.profiler.cpu.find", "System.Windows.Threading")).GetProperty("result").GetProperty("total").GetInt32());
		await app.InvokeAsync("od.profiler.cpu.just-my-code", false);
		Assert.True((await app.InvokeAsync("od.profiler.cpu.find", "System.Windows.Threading.Dispatcher")).GetProperty("result").GetProperty("total").GetInt32() >= 1);
	}

	[Fact]
	public async Task AFileThatIsNotACapture_ShowsTheReasonInsteadOfAReport()
	{
		var path = Path.Combine(Path.GetTempPath(), "od-not-a-capture-" + Guid.NewGuid().ToString("N") + ".nettrace");
		await File.WriteAllTextAsync(path, "this is not an EventPipe file");
		try
		{
			await app.InvokeAsync("od.open-file", path);
			var report = await app.InvokeAsync("od.profiler.report");
			Assert.Equal("message", report.GetProperty("kind").GetString());
			Assert.Contains("is not a CPU or heap capture", report.GetProperty("text").GetString());
		}
		finally
		{
			File.Delete(path);
		}
	}

	/// <summary>Starts a fixture program (<c>dotnet exec</c>) and waits for its first output line, which says it is ready.</summary>
	async Task<(Process Process, string ReadyLine)> StartProgramAsync(string fixture, params string[] arguments)
	{
		var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
		info.ArgumentList.Add("exec");
		info.ArgumentList.Add(LocateFixtureDll(fixture));
		foreach (var argument in arguments)
			info.ArgumentList.Add(argument);
		var process = Process.Start(info)!;
		started.Add(process);
		var output = new List<string>();
		var ready = await Task.Run(async () =>
		{
			// A framework may print its own lines (ASP.NET Core logs) before the fixture says it is ready.
			while (await process.StandardOutput.ReadLineAsync() is { } line)
			{
				if (line.StartsWith("READY", StringComparison.Ordinal))
					return line;
				output.Add(line);
			}
			return null;
		}).WaitAsync(TimeSpan.FromSeconds(90));
		Assert.True(ready is not null, $"{fixture} ended without printing READY (exit code {(process.HasExited ? process.ExitCode : null)}). Output:\n{string.Join('\n', output)}");

		// Keep reading what the program writes. A redirected pipe nobody empties fills up, and a program that logs
		// to the console (ASP.NET Core logs every request) then blocks on its next write: the service would stall
		// instead of serving the load the test puts on it.
		_ = Task.Run(async () =>
		{
			while (await process.StandardOutput.ReadLineAsync() is not null)
			{
			}
		});
		return (process, ready!);
	}

	async Task<Process> StartFixtureAsync(string mode, int seconds)
	{
		var dll = LocateFixtureDll();
		var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
		info.ArgumentList.Add("exec");
		info.ArgumentList.Add(dll);
		info.ArgumentList.Add(mode);
		info.ArgumentList.Add(seconds.ToString());
		var process = Process.Start(info)!;
		started.Add(process);
		// The fixture prints READY once it holds its objects (heap) or is about to spin (cpu).
		var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
		Assert.Equal("READY", ready);
		return process;
	}

	static string LocateFixtureFile(string name)
	{
		var dir = AppContext.BaseDirectory;
		while (dir is not null)
		{
			var candidate = Path.Combine(dir, "tests", "fixtures", "ProfilerFixture", name);
			if (File.Exists(candidate))
				return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException($"Could not find tests/fixtures/ProfilerFixture/{name}.");
	}

	static string LocateFixtureDll(string fixture = "ProfilerFixture")
	{
		var dir = AppContext.BaseDirectory;
		while (dir is not null)
		{
			var project = Path.Combine(dir, "tests", "fixtures", fixture);
			foreach (var configuration in new[] { "Debug", "Release" })
			{
				var dll = Path.Combine(project, "bin", configuration, fixture == "ProfilerWpfFixture" ? "net10.0-windows" : "net10.0", fixture + ".dll");
				if (File.Exists(dll))
					return dll;
			}
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException(fixture + " was not built; build tests/fixtures/" + fixture + " first.");
	}
}
