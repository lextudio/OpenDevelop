using System;
using System.IO;
using System.Linq;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.UnitTesting.Simple;

internal static class TestProjectDetector
{
    private static readonly string[] TestPackageMarkers =
    [
        "Microsoft.NET.Test.Sdk",
        "Microsoft.Testing.Platform",
        "MSTest",
        "NUnit",
        "xunit",
        "TUnit"
    ];

    // MSBuild-evaluated boolean properties that mark a test project. IsTestProject is set explicitly
    // by test projects and implicitly by the test SDK targets; the MTP flag covers the new
    // Microsoft.Testing.Platform runner projects.
    private static readonly string[] TestProjectProperties =
    [
        "IsTestProject",
        "IsTestingPlatformApplication"
    ];

    public static bool IsTestProject(IProject project)
    {
        // Authoritative signal: ask MSBuild for the evaluated property. MSBuildBasedProject
        // .GetEvaluatedProperty opens the real Microsoft.Build.Evaluation project (thread-safe, locks
        // the project SyncRoot). We deliberately do NOT scan project.Items for PackageReferences:
        // the SharpDevelop item model classifies PackageReference as an SDK-internal item and omits
        // it, and Items access requires the UI thread — both make it unreliable here.
        if (project is MSBuildBasedProject msbuildProject)
        {
            try
            {
                foreach (var propertyName in TestProjectProperties)
                {
                    var value = msbuildProject.GetEvaluatedProperty(propertyName);
                    Dbg($"IsTestProject: {project.Name} {propertyName}='{value}'");
                    if (bool.TryParse(value, out var flag) && flag)
                        return true;
                }
            }
            catch (Exception ex)
            {
                Dbg($"IsTestProject: {project.Name} evaluation threw {ex.GetType().Name}: {ex.Message}");
                // Evaluation failed (e.g. SDK not resolvable) — fall through to the output probe.
            }
        }
        else
        {
            Dbg($"IsTestProject: {project.Name} is not MSBuildBasedProject (type={project.GetType().Name})");
        }

        var fallback = HasTestAssemblyNearOutput(project);
        Dbg($"IsTestProject: {project.Name} output-probe={fallback}");
        return fallback;
    }

    private static void Dbg(string msg)
    {
        try { File.AppendAllText("/tmp/ut-debug.log", $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    private static bool IsTestPackageReference(string include)
    {
        foreach (var marker in TestPackageMarkers)
        {
            if (include.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // Last-resort probe when MSBuild evaluation is unavailable: look for a test-framework assembly in
    // the build output. Searches recursively so multi-targeted output (bin/<Config>/<TFM>/) is covered.
    internal static bool HasTestAssemblyNearOutput(IProject project)
    {
        var outputPath = project.OutputAssemblyFullPath;
        return !string.IsNullOrEmpty(outputPath) && ProbeDirectoryForTestFramework(Path.GetDirectoryName(outputPath));
    }

    internal static bool ProbeDirectoryForTestFramework(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return false;

        try
        {
            return Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Any(name => name is not null && IsTestPackageReference(name));
        }
        catch
        {
            return false;
        }
    }
}
