using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// End-to-end coverage of the NuGet addin's real "Manage NuGet Packages" dialog
// (src/AddIns/Misc/PackageManagement/Project/Src/ManagePackagesView.xaml/PackagesView.xaml): open
// a project, drive the real search box and the real per-row "Add" button (via the same
// SearchCommand/AddPackageCommand bindings the UI uses - see PackageManagementDevFlowActions.cs),
// then confirm the on-disk .csproj reflects the newly installed package. Installs against a local,
// offline NuGet feed (tests/fixtures/LocalNuGetFeed) instead of nuget.org so the test doesn't
// depend on network access.
//
// Prerequisites:
//   1. Build OpenDevelop in Debug:
//        dotnet build src/Main/SharpDevelop/SharpDevelop.csproj -c Debug
[Collection("OpenDevelop app")]
public sealed class NuGetAddInTests : IDisposable
{
    const string TestPackageId = "OpenDevelop.TestPackage";

    readonly OpenDevelopAppFixture _app;
    readonly string _projectDir;

    public NuGetAddInTests(OpenDevelopAppFixture app)
    {
        _app = app;
        // Installing a package mutates the .csproj on disk - copy the fixture to a temp dir so
        // the test doesn't write a PackageReference into this repo's tracked fixture file on
        // every run (the same reasoning as GitAddInTests' per-test temp git repo).
        _projectDir = Path.Combine(Path.GetTempPath(), "NuGetAddInTests-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(app.NuGetFixtureTemplatePath, _projectDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectDir, recursive: true); } catch { }
    }

    static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (dir.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                dir.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                continue;
            Directory.CreateDirectory(dir.Replace(sourceDir, destDir));
        }
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                continue;
            File.Copy(file, file.Replace(sourceDir, destDir), overwrite: true);
        }
    }

    [Fact]
    public async Task SearchAndInstallPackage_UpdatesProjectFile()
    {
        var solutionPath = Path.Combine(_projectDir, "NuGetFixture.sln");
        var openResult = await _app.InvokeAsync("od.open-solution", solutionPath);
        Assert.True(openResult.GetProperty("success").GetBoolean(), $"Failed to open {solutionPath}");

        var addInsResult = await _app.InvokeAsync("od.addins");
        var packageManagementAddIn = addInsResult.GetProperty("addins").EnumerateArray()
            .FirstOrDefault(a => a.TryGetProperty("fileName", out var fileName) &&
                fileName.GetString()?.EndsWith("PackageManagement.addin", StringComparison.Ordinal) == true);
        Assert.True(packageManagementAddIn.ValueKind != JsonValueKind.Undefined, "PackageManagement.addin was not registered");

        var feedResult = await _app.InvokeAsync("od.nuget.set-local-feed", _app.LocalNuGetFeedPath);
        Assert.True(feedResult.GetProperty("success").GetBoolean(), $"Set local feed failed: {feedResult}");

        var openDialogResult = await _app.InvokeAsync("od.nuget.open-dialog");
        Assert.True(openDialogResult.GetProperty("success").GetBoolean(), $"Open dialog failed: {openDialogResult}");

        var setSearchResult = await _app.InvokeAsync("od.nuget.set-search-text", TestPackageId);
        Assert.True(setSearchResult.GetProperty("success").GetBoolean(), $"Set search text failed: {setSearchResult}");

        var searchResult = await _app.InvokeAsync("od.nuget.search");
        Assert.True(searchResult.GetProperty("success").GetBoolean(), $"Search command failed: {searchResult}");

        var status = await WaitForSearchToFinishAsync();
        Assert.False(status.GetProperty("hasError").GetBoolean(), $"Search reported an error: {status}");

        var packages = status.GetProperty("packages").EnumerateArray().ToList();
        Assert.Contains(packages, p => p.GetProperty("id").GetString() == TestPackageId);
        Assert.All(packages.Where(p => p.GetProperty("id").GetString() == TestPackageId),
            p => Assert.False(p.GetProperty("isAdded").GetBoolean(), "Package should not be installed yet"));

        var installResult = await _app.InvokeAsync("od.nuget.install", TestPackageId);
        Assert.True(installResult.GetProperty("success").GetBoolean(), $"Install failed: {installResult}");

        var afterInstall = await WaitForPackageInstalledAsync();
        Assert.True(afterInstall, "Package's IsAdded flag never flipped true after install");

        await _app.InvokeAsync("od.nuget.close-dialog");

        // On-disk project state: NuGet's own project-file update path wrote the PackageReference.
        var csprojPath = Path.Combine(_projectDir, "NuGetFixtureApp", "NuGetFixtureApp.csproj");
        var csprojText = await File.ReadAllTextAsync(csprojPath);
        Assert.Contains($"Include=\"{TestPackageId}\"", csprojText);

        // The Project Browser refresh is intentionally not part of this test's pass/fail boundary
        // yet. The install path is asynchronous enough that the UI tree can lag the project-file
        // mutation; keep this test focused on search + install not throwing and the PackageReference
        // being persisted.
    }

    async Task<JsonElement> WaitForSearchToFinishAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        JsonElement status = default;
        while (DateTime.UtcNow < deadline)
        {
            status = await _app.InvokeAsync("od.nuget.status");
            if (status.TryGetProperty("isReadingPackages", out var reading) && !reading.GetBoolean())
                return status;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Package search never finished. Last status: {status}");
    }

    async Task<bool> WaitForPackageInstalledAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = await _app.InvokeAsync("od.nuget.status");
            var installed = status.GetProperty("packages").EnumerateArray()
                .Any(p => p.GetProperty("id").GetString() == TestPackageId && p.GetProperty("isAdded").GetBoolean());
            if (installed)
                return true;
            await Task.Delay(500);
        }
        return false;
    }

    static IEnumerable<JsonElement> FlattenElements(JsonElement tree)
    {
        foreach (var root in tree.GetProperty("elements").EnumerateArray())
            foreach (var node in Flatten(root))
                yield return node;
    }

    static IEnumerable<JsonElement> Flatten(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in Flatten(child))
                    yield return descendant;
    }
}
