using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop;

namespace ICSharpCode.UnitTesting.Simple;

public record TestInfo(
    string DisplayName,
    string FullyQualifiedName,
    string ProjectName,
    string? ProjectPath,
    string? TargetFramework = null,
    string? TestKey = null,
    string? Uid = null,
    string? TypeFullName = null,
    string? MethodName = null,
    int? ParameterCount = null)
{
    public string EffectiveKey => string.IsNullOrWhiteSpace(TestKey) ? FullyQualifiedName : TestKey;

    public static string BuildKey(string? projectPath, string? targetFramework, string fullyQualifiedName)
        => string.Concat(
            projectPath ?? string.Empty,
            "|",
            targetFramework ?? string.Empty,
            "|",
            fullyQualifiedName);
}

public enum TestResultType { None, Passing, Failing, Skipped, Running }

public record TestResultInfo(
    string FullyQualifiedName,
    TestResultType Result,
    string? Message,
    string? StackTrace,
    string? TargetFramework = null,
    string? TestKey = null)
{
    public string EffectiveKey => string.IsNullOrWhiteSpace(TestKey) ? FullyQualifiedName : TestKey;
}

public interface ITestService
{
    bool IsRunning { get; }

    event Action? TestRunStarted;
    event Action<TestResultInfo>? TestResultUpdated;
    event Action? TestRunCompleted;

    // Fires when a project's GetTests() entries that were approximate (Roslyn-scanned, no MTP Uid
    // yet) are replaced by the authoritative MTP-confirmed ones. A caller that already displayed
    // the approximate list from an earlier GetTests() call should re-fetch and merge in the
    // confirmed data now, rather than wait for it before showing anything at all.
    event Action? TestsConfirmed;

    IReadOnlyList<TestInfo> GetTests(IProgressMonitor? progressMonitor = null);
    IReadOnlyDictionary<string, TestResultInfo> GetLastResults();
    void RefreshTests();

    Task RunTestsAsync(IReadOnlyList<string> fullyQualifiedNames);
    Task RunAllTestsAsync();

    void Stop();
}
