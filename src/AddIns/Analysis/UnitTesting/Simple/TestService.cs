using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.UnitTesting.Simple;

public sealed class TestService : ITestService
{
    private readonly DotNetTestRunner _runner = new();
    private List<TestInfo> _cachedTests = new();
    // Tracks whether a discovery pass has actually completed, separately from whether it found
    // anything - a project with zero (real, legitimately discovered) tests must stay cached too,
    // otherwise every GetTests() call re-runs the full (MTP-host-spinup-cost, ~30s-per-project)
    // discovery from scratch forever. Previously guarded by `_cachedTests.Count > 0`, which never
    // let an empty result "stick".
    private bool _hasDiscovered;
    private Dictionary<string, TestResultInfo> _lastResults = new();
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private static readonly string _dbgLog = "/tmp/ut-debug.log";
    // Bumped by RefreshTests(); a background MTP confirmation task (see ConfirmProjectAsync)
    // captures the generation it was launched under and drops its result if a refresh happened
    // in the meantime, so a slow confirmation from a stale discovery pass can't clobber a fresh
    // one's cache.
    private int _generation;

    public TestService()
    {
        _runner.OutputLine += AppendOutputLine;
    }

    public bool IsRunning { get; private set; }

    public event Action? TestRunStarted;
    public event Action<TestResultInfo>? TestResultUpdated;
    public event Action? TestRunCompleted;

    // Fires once a project's approximate (Roslyn-scanned) test list has been replaced by the
    // authoritative MTP-discovered one - see GetTests()/ConfirmProjectAsync. Not part of
    // ITestService: existing consumers (DevFlow actions, TestResultsPad) already re-poll
    // GetTests() on their own cadence and don't need to be broken by a new required member; this
    // is here for callers (tests, a future pad) that want to react as soon as confirmation lands.
    public event Action? TestsConfirmed;

    public IReadOnlyList<TestInfo> GetTests(IProgressMonitor? progressMonitor = null)
    {
        lock (_lock)
        {
            if (_hasDiscovered)
                return _cachedTests.ToList();

            var projectService = ServiceSingleton.ServiceProvider.GetService(typeof(IProjectService)) as IProjectService;
            var solution = projectService?.CurrentSolution;
            if (solution?.Projects is null)
                return Array.Empty<TestInfo>();

            Dbg($"GetTests: {solution.Projects.Count} project(s) in solution");
            var output = PrepareOutputCategory(clear: true);

            var testProjects = solution.Projects
                .Where(TestProjectDetector.IsTestProject)
                .ToList();
            var total = testProjects.Count;
            var completed = 0;

            if (progressMonitor is not null)
                progressMonitor.TaskName = $"Refreshing tests for solution '{solution.Name}'...";

            output?.AppendLine($"Refreshing tests for solution '{solution.Name}'...");
            var generation = _generation;
            var all = new List<TestInfo>();
            foreach (var project in testProjects)
            {
                Dbg($"GetTests: scanning {project.Name} (Roslyn, approximate)");
                if (progressMonitor is not null)
                {
                    progressMonitor.TaskName = $"Scanning {project.Name} for tests...";
                    progressMonitor.Progress = (double)completed / total;
                }
                // Roslyn-only pass first: fast (no build, no test-host spin-up), so a caller isn't
                // stuck looking at "no tests yet" for the ~30-60s an MTP round trip can take. It's
                // approximate (can't expand parameterized tests, has no MTP Uid to run just one
                // test by) - see doc/technotes/unit-testing.md. The real MTP-confirmed list
                // replaces these entries in the background via ConfirmProjectAsync below.
                var approxTests = DiscoverTestsForProjectApprox(project);
                Dbg($"GetTests: {project.Name} -> {approxTests.Count} candidate test(s) (unconfirmed)");
                output?.AppendLine($"Found {approxTests.Count} candidate test(s) in {project.Name} (confirming via Microsoft.Testing.Platform...)");
                all.AddRange(approxTests);
                completed++;

                var project1 = project;
                _ = Task.Run(() => ConfirmProjectAsync(project1, generation));
            }

            _cachedTests = all;
            _hasDiscovered = true;
            if (progressMonitor is not null)
            {
                progressMonitor.TaskName = $"Test refresh completed: {all.Count} test(s).";
                progressMonitor.Progress = 1;
            }
            output?.AppendLine($"Test refresh completed: {all.Count} test(s).");
            return all.ToList();
        }
    }

    public IReadOnlyDictionary<string, TestResultInfo> GetLastResults()
    {
        lock (_lock)
        {
            return new Dictionary<string, TestResultInfo>(_lastResults);
        }
    }

    public void RefreshTests()
    {
        lock (_lock)
        {
            _cachedTests.Clear();
            _hasDiscovered = false;
            _generation++;
        }
    }

    public async Task RunTestsAsync(IReadOnlyList<string> fullyQualifiedNames)
    {
        if (IsRunning || fullyQualifiedNames.Count == 0)
            return;

        var tests = GetTests();
        var testsByKey = tests.ToDictionary(t => t.EffectiveKey, StringComparer.Ordinal);
        var byProject = fullyQualifiedNames
            .Select(key => testsByKey.TryGetValue(key, out var test) ? test : null)
            .Where(t => t is not null)
            .Where(t => !string.IsNullOrEmpty(t!.ProjectPath))
            .GroupBy(t => new
            {
                ProjectPath = t!.ProjectPath!,
                t.TargetFramework,
            })
            .ToList();

        if (byProject.Count == 0)
            return;

        _cts = new CancellationTokenSource();
        IsRunning = true;
        TestRunStarted?.Invoke();

        try
        {
            foreach (var group in byProject)
            {
                if (_cts.IsCancellationRequested) break;

                var projectPath = group.Key.ProjectPath;
                var targetFramework = group.Key.TargetFramework;
                var testUids = group
                    .Select(t => t.Uid)
                    .Where(uid => !string.IsNullOrEmpty(uid))
                    .Select(uid => uid!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var keysByName = group
                    .GroupBy(t => t.FullyQualifiedName, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(t => t.EffectiveKey).First(), StringComparer.Ordinal);
                await RunTestsInProjectAsync(projectPath, targetFramework, testUids, keysByName, _cts.Token);
            }
        }
        finally
        {
            IsRunning = false;
            _cts = null;
            TestRunCompleted?.Invoke();
        }
    }

    public async Task RunAllTestsAsync()
    {
        var tests = GetTests();
        if (tests.Count == 0) return;
        await RunTestsAsync(tests.Select(t => t.EffectiveKey).ToList());
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    // Fast, approximate pass: a syntax-only Roslyn scan of the project's own source, no build and
    // no MTP test-host round trip. See RoslynTestScanner and doc/technotes/unit-testing.md. Uid is
    // always null here - RunTestsAsync treats "no Uid for any test in this project" as "run
    // everything in this project", which is a safe (if imprecise) fallback for a test that hasn't
    // been MTP-confirmed yet, never a silent skip.
    private static List<TestInfo> DiscoverTestsForProjectApprox(IProject project)
    {
        var result = new List<TestInfo>();
        var projectPath = project.FileName?.ToString();
        if (string.IsNullOrEmpty(projectPath))
            return result;

        var candidates = RoslynTestScanner.ScanProject(Path.GetDirectoryName(projectPath));
        if (candidates.Count == 0)
            return result;

        var targetFrameworks = ResolveTargetFrameworks(project, projectPath);
        foreach (var targetFramework in targetFrameworks)
        {
            foreach (var candidate in candidates)
            {
                result.Add(new TestInfo(
                    candidate.DisplayName,
                    candidate.DisplayName,
                    project.Name,
                    projectPath,
                    targetFramework,
                    TestInfo.BuildKey(projectPath, targetFramework, candidate.DisplayName),
                    Uid: null,
                    candidate.TypeFullName,
                    candidate.MethodName,
                    ParameterCount: null));
            }
        }

        return result;
    }

    // Authoritative pass: runs in the background (see GetTests()'s Task.Run) and replaces this
    // project's approximate entries in _cachedTests once the real MTP test host confirms them.
    private async Task ConfirmProjectAsync(IProject project, int generation)
    {
        var confirmed = await DiscoverTestsForProjectViaMtpAsync(project);

        lock (_lock)
        {
            // A RefreshTests() happened since this confirmation was launched - its result belongs
            // to a discovery pass that's already been thrown away; applying it now would resurrect
            // stale data (or worse, data for a project the new pass no longer even considers a
            // test project) into the current cache.
            if (generation != _generation)
            {
                Dbg($"ConfirmProjectAsync: discarding stale confirmation for {project.Name} (generation {generation} != {_generation})");
                return;
            }

            var projectPath = project.FileName?.ToString();
            _cachedTests.RemoveAll(t => string.Equals(t.ProjectPath, projectPath, StringComparison.Ordinal));
            _cachedTests.AddRange(confirmed);
        }

        Dbg($"ConfirmProjectAsync: {project.Name} -> {confirmed.Count} confirmed test(s)");
        TestsConfirmed?.Invoke();
    }

    private async Task<List<TestInfo>> DiscoverTestsForProjectViaMtpAsync(IProject project)
    {
        var result = new List<TestInfo>();
        var projectPath = project.FileName?.ToString();
        if (string.IsNullOrEmpty(projectPath))
            return result;

        var targetFrameworks = ResolveTargetFrameworks(project, projectPath);
        foreach (var targetFramework in targetFrameworks)
        {
            try
            {
                // A bounded timeout, not CancellationToken.None: if the launched MTP test host
                // connects but then never responds to initialize/discoverTests (hung static
                // ctor, deadlock in the host, ...), MtpServerProcess's RPC call has no timeout of
                // its own and would otherwise hang this call - and by extension every caller of
                // GetTests()/DiscoverTestsForProject - forever. The "await using" in
                // DotNetTestRunner.ListTestsAsync still disposes (and kills) the host process once
                // this token fires.
                using var discoverCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var tests = await _runner.ListTestsAsync(projectPath, targetFramework, discoverCts.Token);

                foreach (var test in tests)
                {
                    result.Add(new TestInfo(
                        test.DisplayName,
                        test.DisplayName,
                        project.Name,
                        projectPath,
                        targetFramework,
                        TestInfo.BuildKey(projectPath, targetFramework, test.DisplayName),
                        test.Uid,
                        test.TypeFullName,
                        test.MethodName,
                        test.ParameterCount));
                }
            }
            catch (OperationCanceledException)
            {
                // This 60s timeout firing is a reliable signal, not just a slow project: TestService
                // only speaks Microsoft.Testing.Platform's server-mode protocol, so a classic
                // VSTest-based test project (Microsoft.NET.Test.Sdk + xunit.runner.visualstudio /
                // NUnit3TestAdapter / MSTest.TestAdapter, no UseMicrosoftTestingPlatformRunner /
                // EnableMSTestRunner / EnableNUnitRunner) will NEVER connect back and will hit this
                // every time - silently reporting "0 tests" otherwise, with no clue why.
                Dbg($"DiscoverTests timed out for {project.Name} ({targetFramework ?? "(default target)"}) - MTP host did not respond within 60s");
                PrepareOutputCategory(clear: false)?.AppendLine(
                    $"WARNING: '{project.Name}'"
                    + (string.IsNullOrWhiteSpace(targetFramework) ? "" : $" [{targetFramework}]")
                    + " did not respond to Microsoft.Testing.Platform (MTP) test discovery within 60s. "
                    + "UnoDevelop's test runner only supports MTP-based test hosts - if this project still "
                    + "uses classic VSTest (Microsoft.NET.Test.Sdk + xunit.runner.visualstudio / "
                    + "NUnit3TestAdapter / MSTest.TestAdapter), it will never be discovered. Upgrade to "
                    + "xunit.v3 (UseMicrosoftTestingPlatformRunner), MSTest (EnableMSTestRunner), or "
                    + "NUnit (EnableNUnitRunner) to enable test discovery and running.");
            }
            catch (Exception ex)
            {
                Dbg($"DiscoverTests failed for {project.Name} ({targetFramework ?? "(default target)"}): {ex.Message}");
            }
        }

        return result;
    }

    private async Task RunTestsInProjectAsync(
        string projectPath,
        string? targetFramework,
        IReadOnlyList<string> testUids,
        IReadOnlyDictionary<string, string> testKeysByName,
        CancellationToken ct)
    {
        Dbg($"Running tests in {projectPath}" + (string.IsNullOrWhiteSpace(targetFramework) ? string.Empty : $" ({targetFramework})"));
        PrepareOutputCategory(clear: false)?.AppendLine(
            $"Running {testKeysByName.Count} test(s) in {Path.GetFileName(projectPath)}"
            + (string.IsNullOrWhiteSpace(targetFramework) ? "..." : $" [{targetFramework}]..."));

        // Mark all requested tests as running (keyed by display name - testKeysByName enumerates
        // exactly the tests this group is about to run).
        foreach (var fqn in testKeysByName.Keys)
        {
            var key = testKeysByName[fqn];
            TestResultUpdated?.Invoke(new TestResultInfo(fqn, TestResultType.Running, null, null, targetFramework, key));
        }

        try
        {
            var results = await _runner.RunTestsAsync(projectPath, targetFramework, testUids, ct);
            foreach (var result in results)
            {
                var key = testKeysByName.TryGetValue(result.FullyQualifiedName, out var mapped)
                    ? mapped
                    : TestInfo.BuildKey(projectPath, targetFramework, result.FullyQualifiedName);
                var enriched = result with { TargetFramework = targetFramework, TestKey = key };
                lock (_lock)
                    _lastResults[enriched.EffectiveKey] = enriched;
                TestResultUpdated?.Invoke(enriched);
            }
        }
        catch (OperationCanceledException)
        {
            Dbg("MTP run canceled");
        }
        catch (Exception ex)
        {
            Dbg($"MTP run failed: {ex.Message}");
            PrepareOutputCategory(clear: false)?.AppendLine("ERROR: test run failed: " + ex.Message);
            foreach (var fqn in testKeysByName.Keys)
            {
                var key = testKeysByName[fqn];
                TestResultUpdated?.Invoke(new TestResultInfo(fqn, TestResultType.Failing, ex.Message, null, targetFramework, key));
            }
        }
    }

    private static IReadOnlyList<string?> ResolveTargetFrameworks(IProject project, string projectPath)
    {
        if (project is MSBuildBasedProject msbuildProject)
        {
            try
            {
                var frameworks = SplitTargetFrameworks(msbuildProject.GetEvaluatedProperty("TargetFrameworks"));
                if (frameworks.Count > 0)
                    return frameworks.Cast<string?>().ToArray();

                var targetFramework = msbuildProject.GetEvaluatedProperty("TargetFramework");
                if (!string.IsNullOrWhiteSpace(targetFramework))
                    return [targetFramework.Trim()];
            }
            catch
            {
                // Fallback to project-file scan below.
            }
        }

        return ResolveTargetFrameworksFromProjectFile(projectPath);
    }

    private static IReadOnlyList<string?> ResolveTargetFrameworksFromProjectFile(string projectPath)
    {
        try
        {
            var document = XDocument.Load(projectPath, LoadOptions.None);
            var frameworks = new List<string>();

            foreach (var targetFrameworks in document.Descendants().Where(element => element.Name.LocalName == "TargetFrameworks"))
                frameworks.AddRange(SplitTargetFrameworks(targetFrameworks.Value));

            if (frameworks.Count > 0)
                return frameworks.Cast<string?>().Distinct(StringComparer.Ordinal).ToArray();

            var targetFramework = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "TargetFramework")
                ?.Value;
            if (!string.IsNullOrWhiteSpace(targetFramework))
                return [targetFramework.Trim()];
        }
        catch
        {
            // Best-effort parser; ignore malformed/unreadable project files.
        }

        return [null];
    }

    private static IReadOnlyList<string> SplitTargetFrameworks(string? value)
        => (value ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static void Dbg(string msg)
    {
        try { File.AppendAllText(_dbgLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    private static IOutputCategory? PrepareOutputCategory(bool clear)
    {
        var outputPad = ServiceSingleton.ServiceProvider.GetService(typeof(IOutputPad)) as IOutputPad;
        if (outputPad is null) return null;
        var category = outputPad.GetOrCreateCategory("Tests");
        if (category is null)
            return null;

        if (clear)
            category.Clear();
        outputPad.CurrentCategory = category;
        return category;
    }

    private static void AppendOutputLine(string line)
    {
        PrepareOutputCategory(clear: false)?.AppendLine(line);
    }
}
