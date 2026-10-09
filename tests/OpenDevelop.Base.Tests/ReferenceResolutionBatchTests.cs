using System.Diagnostics;
using ICSharpCode.SharpDevelop.Project;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Batched reference resolution (ReferenceResolutionBatch, used by MinimalMSBuildEngine's
/// IBatchAssemblyReferenceResolver; doc/technotes/fast-mode.md): one MSBuild process resolves the
/// references of many projects, so its output must come back attributed to the right project, and
/// projects must be batched by the global.json that governs their SDK resolution.
/// </summary>
public class ReferenceResolutionBatchTests
{
    // Shaped like real `-getItem:ODReferencePath` output: each item is labelled with the project it
    // was resolved for (ODProject); a ProjectReference output also carries MSBuildSourceProjectFile,
    // which names the REFERENCED project - grouping by that would hand App's reference to Lib.
    const string LabelledOutput = """
        Restore complete.
        {
          "Items": {
            "ODReferencePath": [
              { "Identity": "/packs/ref/System.Runtime.dll", "ODProject": "/src/Lib/Lib.csproj" },
              { "Identity": "/nuget/newtonsoft.json/13.0.3/lib/net6.0/Newtonsoft.Json.dll", "ODProject": "/src/Lib/Lib.csproj", "NuGetPackageId": "Newtonsoft.Json" },
              { "Identity": "/packs/ref/System.Runtime.dll", "ODProject": "/src/App/App.csproj" },
              { "Identity": "/nuget/humanizer.core/2.14.1/lib/net6.0/Humanizer.dll", "ODProject": "/src/App/App.csproj", "NuGetPackageId": "Humanizer.Core" },
              { "Identity": "/src/Lib/bin/Debug/net10.0/Lib.dll", "ODProject": "/src/App/App.csproj", "MSBuildSourceProjectFile": "/src/Lib/Lib.csproj", "ReferenceSourceTarget": "ProjectReference" }
            ]
          }
        }
        """;

    [Fact]
    public void ParseLabelledReferencePaths_GroupsByTheProjectTheyWereResolvedFor()
    {
        var grouped = ReferenceResolutionBatch.ParseLabelledReferencePaths(LabelledOutput);

        Assert.NotNull(grouped);
        Assert.Equal(new[] { "/packs/ref/System.Runtime.dll", "/nuget/newtonsoft.json/13.0.3/lib/net6.0/Newtonsoft.Json.dll" },
            grouped["/src/Lib/Lib.csproj"]);
        Assert.Equal(new[] { "/packs/ref/System.Runtime.dll", "/nuget/humanizer.core/2.14.1/lib/net6.0/Humanizer.dll", "/src/Lib/bin/Debug/net10.0/Lib.dll" },
            grouped["/src/App/App.csproj"]);
    }

    [Fact]
    public void ParseLabelledReferencePaths_WithoutTheItemList_ReturnsNull()
    {
        Assert.Null(ReferenceResolutionBatch.ParseLabelledReferencePaths("MSBuild version 17\nerror MSB1009: Project file does not exist."));
        Assert.Null(ReferenceResolutionBatch.ParseLabelledReferencePaths("""{ "Items": { "ReferencePath": [] } }"""));
    }

    [Fact]
    public void CreateResolveReferencesTraversal_LabelsEveryProjectsOutput()
    {
        var xml = ReferenceResolutionBatch.CreateResolveReferencesTraversal(new[] { ("/src/Lib/Lib.csproj", (string?)null), ("/src/Multi/Multi.csproj", "net9.0") });

        Assert.Contains("<ODProject Include=\"/src/Lib/Lib.csproj\" Properties=\"BuildingInsideVisualStudio=true\" />", xml);
        Assert.Contains("Properties=\"BuildingInsideVisualStudio=true;TargetFramework=net9.0\"", xml);
        Assert.Contains("<ODReferencePath Include=\"@(_R0)\" ODProject=\"/src/Lib/Lib.csproj\" />", xml);
        Assert.Contains("<ODReferencePath Include=\"@(_R1)\" ODProject=\"/src/Multi/Multi.csproj\" />", xml);
        Assert.Contains("Targets=\"R0;R1\"", xml);
    }

    [Fact]
    public void GroupByGlobalJson_SplitsProjectsByTheNearestGlobalJson()
    {
        using var tree = new TempTree();
        var root = tree.Dir("repo");
        File.WriteAllText(Path.Combine(root, "global.json"), "{}");
        var nested = tree.Dir("repo/nested");
        File.WriteAllText(Path.Combine(nested, "global.json"), "{}");
        var a = tree.File("repo/A/A.csproj");
        var b = tree.File("repo/B/B.csproj");
        var c = tree.File("repo/nested/C/C.csproj");
        var outside = tree.File("outside/D/D.csproj"); // no global.json between it and the temp root

        var groups = ReferenceResolutionBatch.GroupByGlobalJson(new[] { a, b, c, outside }, file => file)
            .ToDictionary(group => group.Key ?? "<none>", group => group.Value.ToArray());

        Assert.Equal(new[] { a, b }, groups[root]);
        Assert.Equal(new[] { c }, groups[nested]);
        Assert.Equal(new[] { outside }, groups[ReferenceResolutionBatch.GlobalJsonDirectory(outside) ?? "<none>"]);
        Assert.Equal(3, groups.Count);
    }

    [Fact]
    public void ResolveInGlobalJsonBatches_AFailedBatchLeavesOnlyItsOwnProjectsUnresolved()
    {
        using var tree = new TempTree();
        File.WriteAllText(Path.Combine(tree.Dir("good"), "global.json"), "{}");
        File.WriteAllText(Path.Combine(tree.Dir("bad"), "global.json"), "{}");
        File.WriteAllText(Path.Combine(tree.Dir("throws"), "global.json"), "{}");
        var good = tree.File("good/G/G.csproj");
        var bad = tree.File("bad/B/B.csproj");
        var throws = tree.File("throws/T/T.csproj");

        var resolved = ReferenceResolutionBatch.ResolveInGlobalJsonBatches(new[] { good, bad, throws }, file => file, (directory, batch) => {
            if (directory.EndsWith("bad", StringComparison.Ordinal))
                return null; // the whole batch failed
            if (directory.EndsWith("throws", StringComparison.Ordinal))
                throw new InvalidOperationException("batch process could not start");
            return batch.ToDictionary(project => project, project => (IReadOnlyList<string>)new[] { project + ".dll" });
        });

        // Missing projects are what the caller resolves one by one.
        Assert.Equal(new[] { good }, resolved.Keys);
        Assert.Equal(new[] { good + ".dll" }, resolved[good]);
    }

    [Fact]
    public void RealMSBuild_ResolvesEachProjectsOwnPackagesAndProjectReferences()
    {
        // Packages from the global package cache, so the restore does not need the network.
        using var tree = new TempTree();
        var lib = tree.File("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Newtonsoft.Json" Version="13.0.3" /></ItemGroup>
            </Project>
            """);
        var app = tree.File("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Humanizer.Core" Version="2.14.1" />
                <ProjectReference Include="../Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        Assert.Equal(0, Dotnet(tree.Root, "restore", app).ExitCode);
        var traversal = tree.File("resolve.proj", ReferenceResolutionBatch.CreateResolveReferencesTraversal(new[] { (lib, (string?)null), (app, (string?)null) }));

        var run = Dotnet(tree.Root, "msbuild", traversal, "-nologo", "-t:Resolve", "-getItem:ODReferencePath");
        var grouped = ReferenceResolutionBatch.ParseLabelledReferencePaths(run.Output);

        Assert.True(grouped != null, run.Output);
        var libReferences = grouped![lib].Select(Path.GetFileName).ToArray();
        var appReferences = grouped[app].Select(Path.GetFileName).ToArray();
        Assert.Contains("Newtonsoft.Json.dll", libReferences);
        Assert.DoesNotContain("Humanizer.dll", libReferences);
        Assert.Contains("Humanizer.dll", appReferences);
        Assert.Contains("Lib.dll", appReferences); // the project reference's output, attributed to App
    }

    static (int ExitCode, string Output) Dotnet(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(300_000);
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    sealed class TempTree : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "RefBatch-" + Guid.NewGuid().ToString("N"))).FullName;

        public string Dir(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

        public string File(string relative, string content = "<Project />")
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
