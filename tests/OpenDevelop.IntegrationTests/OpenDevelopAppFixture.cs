using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// Launches OpenDevelop once per test collection, waits for the DevFlow agent (port 9223),
// and exposes helpers to invoke actions. Disposing kills the app.
//
// Prerequisites:
//   1. Build OpenDevelop in Debug:
//        dotnet build src/Main/SharpDevelop/SharpDevelop.csproj -c Debug
//   2. Build the fixture project that OpenDevelop opens:
//        dotnet build tests/fixtures/SampleTestProject/SampleTestProject.csproj
public sealed class OpenDevelopAppFixture : IAsyncLifetime
{
    // SharpDevelop pins its DevFlow agent to 9299 (see DevFlowPort.cs), dedicated to this app so
    // it doesn't collide with unrelated local services on the shared default (9223). Override via
    // env var DEVFLOW_AGENT_PORT if needed.
    static readonly int Port = int.TryParse(
        Environment.GetEnvironmentVariable("DEVFLOW_AGENT_PORT"), out var p) && p > 0 ? p : 9299;
    static readonly string BaseUrl = $"http://localhost:{Port}";

    // Exposed so tests that need a raw HttpClient (rather than InvokeAsync/GetStatusAsync) don't
    // have to hardcode the port -- see the "InvokeActions_ListsRegisteredActions" bug where a
    // hardcoded "localhost:9223" only worked by coincidence while the app's default matched 9223.
    public string DevFlowBaseUrl => BaseUrl;

	// Must exceed the longest per-action `timeoutSeconds` argument used anywhere in this suite
	// (od.code-coverage.run's own 180s budget for a coverage build+instrument+run+collect cycle) -
	// otherwise this client-side timeout can abort a request the server-side action was still
	// legitimately allowed to keep polling for, throwing a misleading "request failed" exception
	// instead of the actual (or timed-out) result.
	readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(240) };
	readonly object _outputLock = new();
	readonly StringBuilder _appOutput = new();
	Process? _app;
	// Set for the lifetime of one launch; see AppLogPath.
	string? _appLogPath;
	DateTime _appStartedUtc;

	// The in-memory _appOutput ring buffer is only ever surfaced through an InvokeAsync exception -
	// which means the one failure mode that matters most is exactly the one it cannot report: the
	// app dying *during startup*, before any action is invoked, takes its whole output with it. Each
	// launch therefore also streams stdout/stderr to its own file that outlives the run, so a silent
	// startup death can be diagnosed after the fact (a fatal stack overflow prints "Stack overflow."
	// plus repeating frames to stderr and bypasses every managed handler, so this file is the only
	// place that evidence ever lands). Override the directory with OD_TEST_LOG_DIR.
	public string? AppLogPath => _appLogPath;

    public string OpenDevelopProjectPath { get; } = LocateOpenDevelopProject();
    public string FixtureSolutionPath { get; } = LocateFixtureProject();
    public string CoverageFixtureSolutionPath { get; } = LocateCoverageFixture();
    public string SolutionExplorerFixturePath { get; } = LocateSolutionExplorerFixture();
    public string DebugTestProjectPath { get; } = LocateDebugTestProject();
    public string SlnxFixturePath { get; } = LocateSlnxFixture();
    public string WpfSampleSolutionPath { get; } = LocateWpfSampleSolution();
    public string GitFixtureTemplatePath { get; } = LocateGitFixtureTemplate();
    public string FSharpFixtureSolutionPath { get; } = LocateFSharpFixture();
    public string VBFixtureSolutionPath { get; } = LocateVBFixture();
    public string NuGetFixtureTemplatePath { get; } = LocateNuGetFixtureTemplate();
    public string LocalNuGetFeedPath { get; } = LocateLocalNuGetFeed();
    public string XmlFixtureFilePath { get; } = LocateXmlFixtureFile();

    // Only one fixture instance may own a live app at a time. This assembly has two collections
    // ("OpenDevelop app" and "OpenDevelop startup"), each with its own OpenDevelopAppFixture, and
    // StopApp() kills SharpDevelop/OpenDevelop *by process name* - so a second fixture coming up (or
    // an old one going down) kills whatever app the other collection is currently driving. That is
    // not hypothetical: a measured full run launched app A at 00:21:34, app B at 00:21:44 (B's
    // InitializeAsync killed A before A had even finished loading its layout), then B was SIGTERM'd
    // mid-suite at 00:24:21, cascading into 73 failures whose only symptom was
    // "request failed / AppProcess=exited exitCode=143". Note how indistinguishable that is from the
    // "intermittent silent startup crash" this suite has been chasing: an app killed by a foreign
    // fixture during startup looks exactly like an app that died on its own.
    //
    // The assembly-level DisableTestParallelization was assumed to serialize the two fixtures'
    // lifetimes. It does not (it orders test *execution*, not collection-fixture construction and
    // disposal), so the mutual exclusion has to be enforced here.
    static readonly SemaphoreSlim FixtureGate = new(1, 1);
    bool _gateHeld;

    public async ValueTask InitializeAsync()
    {
        // Timeout rather than an unbounded wait: if a future xunit version ever defers the previous
        // collection's fixture disposal past the next collection's construction, an unbounded wait
        // would deadlock the whole run with no output. Falling through instead degrades to the old
        // (racy) behavior, and says so in the app log so it is diagnosable rather than silent.
        _gateHeld = await FixtureGate.WaitAsync(TimeSpan.FromSeconds(300));

        StopApp();
        await WaitForPortFreeAsync(TimeSpan.FromSeconds(30));
        DeleteStaleViewStateMemento();
        await StartAsync();

        if (!_gateHeld)
            AppendAppOutput("fixture", "WARNING: fixture gate not acquired within 300s - another "
                + "OpenDevelopAppFixture may still own a live app; cross-fixture kills are possible.");
    }

    // Two separate persistence mechanisms restore previously-open documents on the next startup,
    // both under the user's real ICSharpCode/SharpDevelop5 config directory (shared with the
    // user's own interactive use of the app, not something this test run owns):
    //  - WpfWorkbench's whole-session memento (LastViewStates.xml) - all views open at last exit.
    //  - Per-project preferences (preferences/<project>.<hash>.xml, PropertyService-backed - see
    //    AbstractProject's "openFiles" property) - each project remembers its own open-files list
    //    and reopens (and, per observed behavior, ends up activating the *last* entry of) all of
    //    them as soon as its solution/project loads, entirely independent of LastViewStates.xml.
    // Neither restore is guaranteed to finish before this fixture's own explicit
    // od.open-solution/od.open-file calls run, so a document left open from a *previous* test run
    // (of this suite, or an earlier interactive session against the same sample project) can still
    // be - or become - ActiveViewContent well after a test's own od.open-file call returned
    // "opened: true", making tests that assert on "the currently active document" flaky depending
    // on what was open the last time this exact project was loaded. Deleting both before each
    // launch gives every test run a deterministic, empty-workbench starting point.
    static void DeleteStaleViewStateMemento()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ICSharpCode", "SharpDevelop5");

        try
        {
            var lastViewStates = Path.Combine(configDir, "LastViewStates.xml");
            if (File.Exists(lastViewStates))
                File.Delete(lastViewStates);
        }
        catch
        {
            // Best-effort - a leftover memento only risks test flakiness, not a hard failure.
        }

        try
        {
            var preferencesDir = Path.Combine(configDir, "preferences");
            if (Directory.Exists(preferencesDir))
                Directory.Delete(preferencesDir, recursive: true);
        }
        catch
        {
            // Best-effort - same rationale as above.
        }

        // The hosted ILSpy addin restores its own assembly list and layout from ILSpy.xml at
        // startup (ILSpySettingsFilePathProvider -> ~/.config/ICSharpCode/ILSpy.xml). Leftovers
        // from the user's own interactive ILSpy usage (e.g. an assembly list pointing at a
        // different checkout) would load stale assemblies into the tree, auto-select a dead node,
        // and make the decompiled view / assembly tree UI assertions nondeterministic - same
        // restore-timing rationale as LastViewStates.xml above.
        try
        {
            var ilSpySettings = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ICSharpCode", "ILSpy.xml");
            if (File.Exists(ilSpySettings))
                File.Delete(ilSpySettings);
        }
        catch
        {
            // Best-effort - same rationale as above.
        }

        // Per-user saved layout copies (layouts/*.xml under the SharpDevelop5 config dir) are
        // written at every graceful app exit and restored over the AddIn templates on the next
        // launch - a stale ILSpy layout from an earlier session would override the ILSpyAddIn's
        // own Layouts/ILSpy.xml template and make dock-position assertions nondeterministic.
        try
        {
            var layoutsDir = Path.Combine(configDir, "layouts");
            if (Directory.Exists(layoutsDir))
                Directory.Delete(layoutsDir, recursive: true);
        }
        catch
        {
            // Best-effort - same rationale as above.
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopApp();
        _http.Dispose();
        if (_gateHeld)
        {
            _gateHeld = false;
            FixtureGate.Release();
        }
        await Task.CompletedTask;
    }

    static string ResolveLogDirectory()
    {
        var dir = Environment.GetEnvironmentVariable("OD_TEST_LOG_DIR");
        if (string.IsNullOrEmpty(dir))
            dir = Path.Combine(Path.GetTempPath(), "od-test-logs");
        Directory.CreateDirectory(dir);
        return dir;
    }

    async Task StartAsync()
    {
        _appStartedUtc = DateTime.UtcNow;
        var psi = new ProcessStartInfo(ResolveDotNetHost())
        {
            WorkingDirectory = Path.GetDirectoryName(OpenDevelopProjectPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "run", "--project", OpenDevelopProjectPath, "-f", "net10.0-windows", "--no-build" })
            psi.ArgumentList.Add(a);
        // Tells the app it is being driven by the integration-test agent: the main window shows
        // without activating (ShowActivated=false), so a test run never steals focus from whatever
        // the user is doing on the machine (measured annoyance - the WPF window grabs activation on
        // every fixture launch otherwise).
        psi.Environment["OD_TEST_MODE"] = "1";
        ConfigureDotNetEnvironment(psi);

        _app = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start OpenDevelop");
        try
        {
            _appLogPath = Path.Combine(
                ResolveLogDirectory(),
                $"od-app-{_appStartedUtc:yyyyMMdd-HHmmss}-pid{_app.Id}.log");
            File.WriteAllText(_appLogPath, $"# OpenDevelop test launch {_appStartedUtc:O} pid {_app.Id}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort - losing the log file must not fail the run itself.
            _appLogPath = null;
        }
		_app.OutputDataReceived += (_, e) => AppendAppOutput("stdout", e.Data);
		_app.ErrorDataReceived += (_, e) => AppendAppOutput("stderr", e.Data);
		_app.BeginOutputReadLine();
		_app.BeginErrorReadLine();

        await WaitForAgentAsync(TimeSpan.FromSeconds(120));
    }

    void StopApp()
    {
        try { if (_app is { HasExited: false }) _app.Kill(entireProcessTree: true); } catch { }
        try { foreach (var proc in Process.GetProcessesByName("SharpDevelop")) { try { proc.Kill(true); } catch { } } } catch { }
        // On non-Windows the app exe is named "OpenDevelop" (the csproj AssemblyName), and a
        // manually started instance (e.g. for ad-hoc DevFlow probing) would otherwise survive
        // StopApp and keep holding the DevFlow port - making the next fixture launch bind a
        // second app to a different process while tests talk to the stale one.
        try { foreach (var proc in Process.GetProcessesByName("OpenDevelop")) { try { proc.Kill(true); } catch { } } } catch { }
        try { foreach (var proc in Process.GetProcessesByName("SharpDbg.Cli")) { try { proc.Kill(true); } catch { } } } catch { }
        try { foreach (var proc in Process.GetProcessesByName("DebugTestApp")) { try { proc.Kill(true); } catch { } } } catch { }
        _app = null;
    }

    async Task WaitForAgentAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var resp = await _http.GetAsync($"{BaseUrl}/api/v1/agent/status");
                if (resp.IsSuccessStatusCode) return;
            }
            catch { }

            // A startup death is terminal: the agent can never come up, so waiting out the full
            // timeout only delays the report by ~2 minutes and (worse) used to hide the cause behind
            // a bare TimeoutException. Fail immediately with the process's exit status and log path.
            if (_app is { HasExited: true })
            {
                // Give the async output readers a moment to flush the final lines - the last thing
                // printed before the abort is exactly the interesting part.
                await Task.Delay(500);
                throw new InvalidOperationException(
                    $"OpenDevelop exited during startup before the DevFlow agent came up on {BaseUrl}. "
                    + DescribeAppFailureContext()
                    + $"\nApp output:\n{GetRecentAppOutput()}");
            }

            await Task.Delay(1000);
        }
        throw new TimeoutException(
            $"DevFlow agent did not respond on {BaseUrl} within {timeout}. "
            + DescribeAppFailureContext()
            + $"\nApp output:\n{GetRecentAppOutput()}");
    }

    async Task WaitForPortFreeAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName("SharpDevelop").Length == 0 && !IsPortInUse(Port))
                return;
            await Task.Delay(500);
        }
    }

    static bool IsPortInUse(int port)
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(ep => ep.Port == port);
        }
        catch { return false; }
    }

	// od.open-solution is never a no-op app-side: it unconditionally closes the current solution
	// (force-closing views, canceling builds, saving prefs) and reloads a fresh MSBuild project
	// tree from disk, even when the requested path is already open. That full reload is expensive
	// and, across ~90 facts in this suite, mostly redundant - many consecutive facts re-open the
	// exact same fixture solution just to run another read-only query against it.
	//
	// EnsureSolutionOpenAsync lets read-only facts skip the reopen when this exact path is already
	// open. The source of truth is the app itself, not an in-memory cache: the app is queried for
	// its currently open solution path (od.solution.status), so a fact is never misled by other
	// facts that opened a different solution directly via od.open-solution (which, unlike this
	// helper, does not record anything on the test side). Correctness never depends on xUnit's
	// (unguaranteed) test execution order: whichever fact runs, if the requested path is not the
	// app's current solution, it reopens; if it is, it's safe to skip only because the caller has
	// verified the fact does not depend on a fresh reload for isolation (no dirty documents, no
	// project mutation, no leftover build/debug/test-run state from a previous fact). Facts that
	// mutate solution/project state, run builds/debug sessions/unit test runs, or otherwise rely
	// on od.open-solution's full-reset side effects as their isolation mechanism must keep calling
	// ReopenSolutionAsync (or InvokeAsync("od.open-solution", ...) directly) so they always get a
	// genuine fresh reload.

	public async Task EnsureSolutionOpenAsync(string path)
	{
		var status = await InvokeAsync("od.solution.status");
		string? current = status.TryGetProperty("path", out var p) ? p.GetString() : null;
		if (string.Equals(current, path, StringComparison.Ordinal))
			return;
		await InvokeAsync("od.open-solution", path);
	}

	public async Task<JsonElement> ReopenSolutionAsync(string path)
	{
		var result = await InvokeAsync("od.open-solution", path);
		return result;
	}

	public async Task<JsonElement> InvokeAsync(string action, params object[] args)
	{
		var body = JsonSerializer.Serialize(new { args });
		using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
		HttpResponseMessage resp;
		try
		{
			resp = await _http.PostAsync($"{BaseUrl}/api/v1/invoke/actions/{action}", content);
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException($"Action '{action}' request failed. {DescribeAppFailureContext()}\nRecent app output:\n{GetRecentAppOutput()}", ex);
		}
		using (resp)
		{
			if (!resp.IsSuccessStatusCode)
			{
				var err = await resp.Content.ReadAsStringAsync();
				throw new InvalidOperationException($"Action '{action}' failed ({(int)resp.StatusCode}): {err}\n{DescribeAppFailureContext()}\nRecent app output:\n{GetRecentAppOutput()}");
			}
			var envelope = await resp.Content.ReadFromJsonAsync<JsonElement>();
			var raw = envelope.TryGetProperty("returnValue", out var rv) ? rv.GetString() : null;
			if (string.IsNullOrEmpty(raw))
				throw new InvalidOperationException($"Action '{action}' returned no value: {envelope}\nRecent app output:\n{GetRecentAppOutput()}");
			return JsonDocument.Parse(raw).RootElement.Clone();
		}
	}

    public async Task<JsonElement> GetStatusAsync()
    {
        using var resp = await _http.GetAsync($"{BaseUrl}/api/v1/agent/status");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // System.Text.Json's default MaxDepth (64) is too shallow for a full WPF visual tree - real
    // windows nest well past that (panels within panels within docking containers etc.), so the
    // default-options read threw "The maximum configured depth of 64 has been exceeded" on a real
    // window instead of returning the tree.
    static readonly JsonSerializerOptions DeepJsonOptions = new() { MaxDepth = 256 };

    public async Task<JsonElement> GetUITreeAsync()
    {
        using var resp = await _http.GetAsync($"{BaseUrl}/api/v1/ui/tree");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>(DeepJsonOptions);
    }

    static string LocateOpenDevelopProject()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "src", "Main", "SharpDevelop", "SharpDevelop.csproj");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate src/Main/SharpDevelop/SharpDevelop.csproj by walking up from " + AppContext.BaseDirectory);
    }

    static string LocateFixtureProject()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "SampleTestProject", "SampleTestProject.csproj");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate tests/fixtures/SampleTestProject/SampleTestProject.csproj by walking up from " + AppContext.BaseDirectory);
    }

    static string LocateCoverageFixture()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "CoverageFixture", "CoverageFixture.sln");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate tests/fixtures/CoverageFixture/CoverageFixture.sln by walking up from " + AppContext.BaseDirectory);
    }

    static string LocateSolutionExplorerFixture()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "SolutionExplorerFixture", "SolutionExplorerFixture.sln");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate tests/fixtures/SolutionExplorerFixture/SolutionExplorerFixture.sln by walking up from " + AppContext.BaseDirectory);
    }

    static string LocateSlnxFixture()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "SlnxFixture", "SlnxFixture.slnx");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate tests/fixtures/SlnxFixture/SlnxFixture.slnx by walking up from " + AppContext.BaseDirectory);
    }

    static string LocateDebugTestProject()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "DebugTestApp", "DebugTestApp.csproj");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate tests/fixtures/DebugTestApp/DebugTestApp.csproj by walking up from " + AppContext.BaseDirectory);
    }

    static string LocateGitFixtureTemplate()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "GitFixture");
            if (File.Exists(Path.Combine(candidate, "GitFixture.sln"))) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate tests/fixtures/GitFixture/GitFixture.sln by walking up from " + AppContext.BaseDirectory);
    }

    static string LocateNuGetFixtureTemplate()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "NuGetFixture");
            if (File.Exists(Path.Combine(candidate, "NuGetFixture.sln"))) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate tests/fixtures/NuGetFixture/NuGetFixture.sln by walking up from " + AppContext.BaseDirectory);
    }

    static string LocateFSharpFixture()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "FSharpFixture", "FSharpFixture.sln");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate tests/fixtures/FSharpFixture/FSharpFixture.sln by walking up from " + AppContext.BaseDirectory);
    }

    static string LocateVBFixture()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "VBFixture", "VBFixture.sln");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate tests/fixtures/VBFixture/VBFixture.sln by walking up from " + AppContext.BaseDirectory);
    }

    static string LocateLocalNuGetFeed()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "LocalNuGetFeed");
            if (File.Exists(Path.Combine(candidate, "OpenDevelop.TestPackage.1.0.0.nupkg"))) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate tests/fixtures/LocalNuGetFeed/OpenDevelop.TestPackage.1.0.0.nupkg by walking up from " + AppContext.BaseDirectory);
    }

    static string LocateWpfSampleSolution()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "externals", "vscode-wpf", "sample", "net6.0", "sample.sln");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate externals/vscode-wpf/sample/net6.0/sample.sln by walking up from " + AppContext.BaseDirectory);
    }

    static string LocateXmlFixtureFile()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "SampleTestProject", "Sample.xml");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate tests/fixtures/SampleTestProject/Sample.xml by walking up from " + AppContext.BaseDirectory);
    }

    static string ResolveDotNetHost()
    {
        var envHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(envHost) && File.Exists(envHost) && DotNetHostResolvesSdk(envHost))
            return envHost;

        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "..", "librewpf", ".dotnet", "dotnet");
            candidate = Path.GetFullPath(candidate);
            if (File.Exists(candidate) && DotNetHostResolvesSdk(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        return "dotnet";
    }

    // A dotnet host found on disk (e.g. a sibling "librewpf" checkout's bundled runtime) can carry
    // an SDK version that doesn't satisfy this repo's global.json (rollForward doesn't cross major
    // versions) - in that case "dotnet run" fails instantly with a "compatible SDK not found"
    // error, but StartAsync only ever finds out indirectly, by timing out 120s later waiting for a
    // DevFlow agent that never started. Validate the candidate actually resolves an SDK for this
    // repo before preferring it over the plain "dotnet" already on PATH.
    static bool DotNetHostResolvesSdk(string dotnetPath)
    {
        try
        {
            var psi = new ProcessStartInfo(dotnetPath, "--version")
            {
                WorkingDirectory = FindRepoRoot(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            if (!process.WaitForExit(10000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "global.json"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return AppContext.BaseDirectory;
    }

	static void ConfigureDotNetEnvironment(ProcessStartInfo psi)
    {
        var dotnet = ResolveDotNetHost();
        if (!File.Exists(dotnet))
            return;

        var dotnetRoot = Path.GetDirectoryName(dotnet)!;

        // Homebrew's formula layout splits the package: the "dotnet" binary resolves into
        // <Cellar>/<version>/bin/dotnet, but the actual SDK/runtime tree (with "sdk/", "shared/",
        // etc.) lives in the sibling <Cellar>/<version>/libexec. Using dotnetRoot ("bin") directly
        // means sdkRoot below never exists, so this whole method used to silently no-op past that
        // point - including never setting MSBuildEnableWorkloadResolver=false (see comment below),
        // which let CodeCoverageTests/other early-run solution loads hit the exact MSB4236
        // "WorkloadAutoImportPropsLocator SDK not found" project-load failure this env var exists
        // to avoid. Same fix as DotNetSdkService.ResolvePathDotnetRoot() in the main app.
        var sdkRoot = Path.Combine(dotnetRoot, "sdk");
        if (!Directory.Exists(sdkRoot)) {
            var siblingLibexec = Path.Combine(Path.GetDirectoryName(dotnetRoot) ?? "", "libexec");
            if (Directory.Exists(Path.Combine(siblingLibexec, "sdk")))
                dotnetRoot = siblingLibexec;
        }
        psi.Environment["DOTNET_ROOT"] = dotnetRoot;
        psi.Environment["DOTNET_HOST_PATH"] = dotnet;

        sdkRoot = Path.Combine(dotnetRoot, "sdk");
        if (!Directory.Exists(sdkRoot))
            return;

        var sdkDir = Directory.GetDirectories(sdkRoot)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .LastOrDefault();
        if (sdkDir == null)
            return;

        psi.Environment["MSBuildSDKsPath"] = Path.Combine(sdkDir, "Sdks");
        psi.Environment["MSBuildExtensionsPath"] = sdkDir;
        psi.Environment["MSBuildToolsPath"] = sdkDir;
        psi.Environment["MSBuildToolsVersion"] = "Current";
        psi.Environment["MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET"] = Path.Combine(sdkDir, "SdkResolvers");
        psi.Environment["MSBUILD_NUGET_PATH"] = sdkDir;

        // The bundled preview SDK's workload manifest/resolver setup only works through the
        // `dotnet` CLI muxer (which has its own workload resolution baked in); SharpDevelop's
        // in-process MSBuild hosting (Microsoft.Build.Execution, used to evaluate opened
        // projects) doesn't get that and intermittently fails project loads with
        // "ProjectLoadException: The SDK 'Microsoft.NET.SDK.WorkloadAutoImportPropsLocator'
        // specified could not be found." Not needed for plain console/class-library projects.
        psi.Environment["MSBuildEnableWorkloadResolver"] = "false";
	}

	void AppendAppOutput(string stream, string? line)
	{
		if (line == null)
			return;
		lock (_outputLock)
		{
			_appOutput.Append('[').Append(stream).Append("] ").AppendLine(line);
			if (_appOutput.Length > 100_000)
				_appOutput.Remove(0, _appOutput.Length - 100_000);
			if (_appLogPath != null)
			{
				try { File.AppendAllText(_appLogPath, $"[{stream}] {line}{Environment.NewLine}"); }
				catch { /* Best-effort - see AppLogPath. */ }
			}
		}
	}

	// Whether the app process is gone, and if so how it died - the distinction the fixture used to
	// lose entirely. On macOS a SIGABRT surfaces as exit code 134, and a .NET fatal stack overflow
	// aborts the same way; the code alone narrows a "silent startup death" to a signal kill vs a
	// clean managed exit vs a still-running-but-unresponsive agent.
	string DescribeAppProcess()
	{
		var app = _app;
		if (app == null)
			return "AppProcess=none";
		try
		{
			if (!app.HasExited)
				return $"AppProcess=running pid={app.Id}";
			var code = app.ExitCode;
			var signal = code > 128 ? $" (signal {code - 128})" : "";
			return $"AppProcess=exited pid={app.Id} exitCode={code}{signal}";
		}
		catch (Exception ex)
		{
			return $"AppProcess=unknown ({ex.GetType().Name})";
		}
	}

	// macOS writes a .ips crash report per abnormal termination. Surfacing the paths of reports
	// written since this launch turns "the app vanished with no output" into a concrete artifact -
	// for a stack overflow the report's repeating frame names name the offending call site, which is
	// the only way to identify it (managed handlers never run).
	string DescribeRecentCrashReports()
	{
		try
		{
			var dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				"Library", "Logs", "DiagnosticReports");
			if (!Directory.Exists(dir))
				return "";
			var since = _appStartedUtc.AddSeconds(-5);
			var hits = Directory.EnumerateFiles(dir, "*.ips")
				.Select(f => new FileInfo(f))
				.Where(f => f.LastWriteTimeUtc >= since)
				.Where(f => f.Name.StartsWith("OpenDevelop", StringComparison.OrdinalIgnoreCase)
					|| f.Name.StartsWith("SharpDevelop", StringComparison.OrdinalIgnoreCase)
					|| f.Name.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(f => f.LastWriteTimeUtc)
				.Take(3)
				.Select(f => f.FullName)
				.ToList();
			return hits.Count == 0 ? "" : $"\nCrash reports since launch:\n  {string.Join("\n  ", hits)}";
		}
		catch
		{
			return "";
		}
	}

	// One string carrying every out-of-band diagnostic: how the process is doing, where its full
	// log is, and any crash report it left behind. Attached to every failure path that previously
	// reported only the truncated in-memory output.
	string DescribeAppFailureContext()
	{
		var log = _appLogPath == null ? "" : $"\nApp log: {_appLogPath}";
		return $"{DescribeAppProcess()}{log}{DescribeRecentCrashReports()}";
	}

	string GetRecentAppOutput()
	{
		lock (_outputLock)
		{
			return _appOutput.ToString();
		}
	}
}

[CollectionDefinition("OpenDevelop app")]
public sealed class OpenDevelopAppCollection : ICollectionFixture<OpenDevelopAppFixture> { }
