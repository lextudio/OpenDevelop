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

    IReadOnlyList<TestInfo> GetTests(IProgressMonitor? progressMonitor = null);
    IReadOnlyDictionary<string, TestResultInfo> GetLastResults();
    void RefreshTests();

    Task RunTestsAsync(IReadOnlyList<string> fullyQualifiedNames);
    Task RunAllTestsAsync();

    void Stop();
}
