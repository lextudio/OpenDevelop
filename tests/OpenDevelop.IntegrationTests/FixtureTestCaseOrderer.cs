using Xunit.Sdk;
using Xunit.v3;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Keeps integration tests in a stable, fixture-oriented order. The order is also applied when a
/// runner selects a class, method, or arbitrary filtered subset of the suite.
/// </summary>
public sealed class FixtureTestCaseOrderer : ITestCaseOrderer
{
    public IReadOnlyCollection<TTestCase> OrderTestCases<TTestCase>(
        IReadOnlyCollection<TTestCase> testCases)
        where TTestCase : notnull, ITestCase
        => testCases
            // xUnit executes a class as an indivisible group. Sort classes first so an assembly
            // run and every filtered subset choose the same group order, then sort the mixed
            // Workbench/AddIn classes by their fixture project.
            .OrderBy(testCase => GetClassOrder(GetName(testCase)))
            .ThenBy(testCase => GetFixtureOrder(GetName(testCase)))
            .ThenBy(testCase => GetScenarioOrder(GetName(testCase)))
            .ThenBy(GetName, StringComparer.Ordinal)
            .ToArray();

    static string GetName<TTestCase>(TTestCase testCase)
        where TTestCase : notnull, ITestCase
        => $"{testCase.TestClassName}.{testCase.TestMethodName}";

    static int GetClassOrder(string testName)
    {
        if (testName.Contains("DevFlowTests", StringComparison.Ordinal)) return 0;
        if (testName.Contains("StartupTests", StringComparison.Ordinal)) return 10;
        if (testName.Contains("WorkbenchTests", StringComparison.Ordinal)) return 20;
        if (testName.Contains("AddInTests", StringComparison.Ordinal)) return 30;
        if (testName.Contains("CodeCoverageTests", StringComparison.Ordinal)) return 40;
        if (testName.Contains("DebuggerIntegrationTests", StringComparison.Ordinal)) return 50;
        if (testName.Contains("RuntimeUpgradeIntegrationTests", StringComparison.Ordinal)) return 60;
        return 1000;
    }

    internal static int GetFixtureOrder(string testName)
    {
        // Application state that does not require a solution.
        if (ContainsAny(testName, "DevFlowTests", "StartupTests", "AddIn_IsLoaded",
                "AddInsList_", "UpdateCheck_", "Service_Is", "SdkList_", "SdkSelect_"))
            return 0;

        if (ContainsAny(testName, "SolutionExplorerFixture", "BuildSolution_", "SolutionTree_",
                "OpenSolution_", "OpenFile_", "EditFile_", "SaveFile_", "SaveAllOpenFiles_",
                "AddFileToProject_", "RemoveExplicitFile", "RemoveGlobCoveredFile",
                "RenameProjectFile_", "AddProjectReference_", "Replace_InOpenFile_",
                "ErrorList_", "OpenSln", "Roslyn", "FindReferences_", "RenameSymbol_",
                "ExtractInterface_", "ProjectContextMenu_"))
            return 10;

        if (ContainsAny(testName, "SampleTestProject", "UnitTest", "FixtureSolutionPath_Xml"))
            return 20;

        if (testName.Contains("CodeCoverage", StringComparison.Ordinal))
            return 30;

        if (testName.Contains("Debugger", StringComparison.Ordinal)
            || testName.Contains("DebugStart_", StringComparison.Ordinal)
            || testName.Contains("BreakpointHit_", StringComparison.Ordinal)
            || testName.Contains("ContinueDebug_", StringComparison.Ordinal)
            || testName.Contains("StepInto", StringComparison.Ordinal))
            return 40;

        if (testName.Contains("FSharp", StringComparison.Ordinal))
            return 50;
        if (testName.Contains("VBFixture", StringComparison.Ordinal)
            || testName.Contains("VBAddIn", StringComparison.Ordinal))
            return 60;
        if (ContainsAny(testName, "Wpf", "Xaml", "DesignSurface", "SamplePane"))
            return 70;
        if (testName.Contains("WinForms", StringComparison.Ordinal))
            return 80;
        if (testName.Contains("Git", StringComparison.Ordinal))
            return 90;
        if (testName.Contains("Package", StringComparison.Ordinal))
            return 100;
        if (testName.Contains("RuntimeUpgrade", StringComparison.Ordinal)
            || testName.Contains("Retargeting", StringComparison.Ordinal))
            return 110;
        if (testName.Contains("IlSpy", StringComparison.Ordinal)
            || testName.Contains("Assembly", StringComparison.Ordinal))
            return 120;

        return 1000;
    }

    static int GetScenarioOrder(string testName)
    {
        if (ContainsAny(testName, "IsAvailable", "IsLoaded", "Service_Is", "AddInsList_"))
            return 0;
        if (ContainsAny(testName, "Open", "Loads", "Tree_", "Shows"))
            return 10;
        if (ContainsAny(testName, "Build", "Run", "Debug"))
            return 20;
        if (ContainsAny(testName, "Clear", "Remove", "Missing", "FailsCleanly"))
            return 90;
        return 50;
    }

    static bool ContainsAny(string value, params string[] fragments)
        => fragments.Any(fragment => value.Contains(fragment, StringComparison.Ordinal));
}

/// <summary>Orders the one-class collections before xUnit creates their class runners.</summary>
public sealed class FixtureTestCollectionOrderer : ITestCollectionOrderer
{
    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(
        IReadOnlyCollection<TTestCollection> testCollections)
        where TTestCollection : notnull, ITestCollection
        => testCollections
            .OrderBy(collection => collection.TestCollectionDisplayName, StringComparer.Ordinal)
            .ToArray();
}
