using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("40 New project dialog fixture")]
public sealed class NewProjectDialogIntegrationTests
{
    readonly OpenDevelopAppFixture _app;
    public NewProjectDialogIntegrationTests(OpenDevelopAppFixture app) => _app = app;

    [Fact]
    public async Task BlazorFrameworkChoice_HasVisibleFallbackLabel()
    {
        var template = await _app.InvokeAsync("od.template.inspect", "Blazor Web App");
        var framework = template.GetProperty("Parameters")
            .EnumerateArray()
            .Single(parameter => parameter.GetProperty("Name").GetString() == "Framework");

        var choices = framework.GetProperty("Choices");
        Assert.Equal("net10.0", choices.GetProperty("net10.0").GetString());
    }

    [Fact]
    public async Task NewFileDialog_ConstructsAndExposesTemplatePicker()
    {
        var targetDirectory = Path.Combine(Path.GetTempPath(), "opendevelop-new-item-dialog");
        Directory.CreateDirectory(targetDirectory);
        var opened = await _app.InvokeAsync("od.new-item-dialog", targetDirectory);
        Assert.True(opened.GetProperty("started").GetBoolean());
        try
        {
            JsonElement tree = default;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(250);
                tree = await _app.GetUITreeAsync();
                var text = tree.ToString();
                if (text.Contains("NewItemWindow") && text.Contains("Item name:") && text.Contains("Template options:"))
                    return;
            }
            Assert.Fail("New File dialog did not expose its item-name and template-option controls. UI tree: " + tree);
        }
        finally
        {
            var closed = await _app.InvokeAsync("od.new-item-dialog.close");
            Assert.True(closed.GetProperty("closed").GetBoolean());
        }
    }

    [Fact]
    public Task NewSolutionDialog_CreatesProjectAndSolutionFiles() => CreateConsoleSolutionAndBuildAsync();

    // Issue #19: the machine there evaluated the new project under SDK 9.0.316, where the Roslyn
    // targets path comes out empty. Whatever SDK the user's setting points at, the IDE must keep
    // using a .NET 10+ SDK and the project it creates must still load and build:
    //  - dotnetup-sdk9: a REAL SDK 9, installed with dotnetup (see DotnetupSdkProvider), registered
    //    as a custom root so the IDE discovers it, then selected;
    //  - fake-sdk9-root: the same shape with an empty SDK 9 folder, without the download;
    //  - 10.0: the bare version string the reporter had selected, which is not a root at all.
    [Theory]
    [InlineData("dotnetup-sdk9")]
    [InlineData("fake-sdk9-root")]
    [InlineData("10.0")]
    public async Task NewSolutionDialog_StillCreatesAWorkingProject_WhenTheSelectedSdkIsUnusable(string selection)
    {
        var fakeRoot = Path.Combine(Path.GetTempPath(), "opendevelop-fake-sdk9-" + Guid.NewGuid().ToString("N"));
        string? customRoot = null;
        JsonElement removed = default;
        try
        {
            string selected = selection;
            if (selection == "fake-sdk9-root")
            {
                Directory.CreateDirectory(Path.Combine(fakeRoot, "sdk", "9.0.316"));
                await File.WriteAllTextAsync(Path.Combine(fakeRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), "");
                selected = customRoot = fakeRoot;
            }
            else if (selection == "dotnetup-sdk9")
            {
                selected = customRoot = await DotnetupSdkProvider.EnsureSdkAsync("9.0");
            }

            if (customRoot is not null)
            {
                var added = await _app.InvokeAsync("od.sdk.add-custom-root", customRoot);
                Assert.True(added.GetProperty("success").GetBoolean(), added.ToString());

                // Discovered, so the selection below really is "an SDK the IDE knows but must not use".
                var list = await _app.InvokeAsync("od.sdk.list");
                Assert.Contains(list.GetProperty("sdks").EnumerateArray(), s =>
                    string.Equals(TrimRoot(s.GetProperty("RootPath").GetString()), TrimRoot(customRoot), StringComparison.OrdinalIgnoreCase)
                    && s.GetProperty("HighestSdkVersion").GetString()!.StartsWith("9."));
            }

            var select = await _app.InvokeAsync("od.sdk.select", selected);
            Assert.True(select.GetProperty("success").GetBoolean(), select.ToString());

            // The unusable selection must really be stored; otherwise "effective is SDK 10" below
            // would hold trivially and nothing would have been tested.
            var afterSelect = await _app.InvokeAsync("od.sdk.list");
            Assert.Equal(selected, afterSelect.GetProperty("selectedRootPath").GetString());

            // Tell "this machine has no .NET 10+ SDK" (environment) apart from "the IDE returned none
            // although one is discovered" (product regression).
            var discovered = (await _app.InvokeAsync("od.sdk.list")).GetProperty("sdks").EnumerateArray().ToList();
            Assert.True(discovered.Any(s => int.TryParse(s.GetProperty("HighestSdkVersion").GetString()?.Split('.')[0], out var v) && v >= 10),
                "This machine has no discovered .NET SDK 10 or newer, which the IDE (net10.0) needs.");

            var effective = select.GetProperty("effective");
            Assert.True(effective.ValueKind == JsonValueKind.Object,
                "A .NET 10+ SDK is discovered, but no effective SDK was returned after selecting an unusable one.");
            var effectiveVersion = effective.GetProperty("HighestSdkVersion").GetString();
            Assert.True(int.TryParse(effectiveVersion?.Split('.')[0], out var major) && major >= 10,
                $"Selecting '{selected}' made SDK {effectiveVersion} effective; it must never be older than .NET 10.");
            if (customRoot is not null)
                Assert.NotEqual(TrimRoot(customRoot), TrimRoot(effective.GetProperty("RootPath").GetString()));

            await CreateConsoleSolutionAndBuildAsync();
        }
        finally
        {
            await _app.InvokeAsync("od.sdk.select", "");
            if (customRoot is not null)
                // A different spelling than the one added: removal must match by directory, so a
                // regression to raw string comparison leaves the root behind and fails below.
                removed = await _app.InvokeAsync("od.sdk.remove-custom-root", customRoot + Path.DirectorySeparatorChar);
            if (Directory.Exists(fakeRoot)) Directory.Delete(fakeRoot, recursive: true);
        }

        // Checked after the cleanup so a failure above is not masked, and a leaked root is not silent.
        if (customRoot is not null)
            Assert.True(removed.GetProperty("removed").GetInt32() >= 1,
                $"The custom SDK root '{customRoot}' was not removed again: {removed}");
    }

    static string TrimRoot(string? path) => (path ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    async Task CreateConsoleSolutionAndBuildAsync()
    {
        var root =Path.Combine(Path.GetTempPath(), "opendevelop-new-solution-" + Guid.NewGuid().ToString("N"));
        const string solutionName = "CreatedSolution";
        const string projectName = "CreatedConsole";
        try
        {
            Assert.True((await _app.InvokeAsync("od.new-solution-dialog")).GetProperty("started").GetBoolean());
            await WaitForWindowAsync("NewProjectWindow");
            await WaitForTemplatesAsync("project");
            var submitted = await _app.InvokeAsync("od.new-solution-dialog.create", "Console App", projectName, root, solutionName);
            Assert.True(submitted.GetProperty("submitted").GetBoolean(), submitted.ToString());

            var solutionRoot = Path.Combine(root, solutionName);
            var projectDirectory = Path.Combine(solutionRoot, projectName);
            var projectFile = Path.Combine(projectDirectory, projectName + ".csproj");
            var programFile = Path.Combine(projectDirectory, "Program.cs");
            await WaitForFileAsync(projectFile);
            await WaitForFileAsync(Path.Combine(solutionRoot, solutionName + ".slnx"));
            Assert.Contains("Microsoft.NET.Sdk", await File.ReadAllTextAsync(projectFile));
            Assert.Contains("<TargetFramework>net10.0</TargetFramework>", await File.ReadAllTextAsync(projectFile));
            Assert.Contains("Hello, World!", await File.ReadAllTextAsync(programFile));

            // Files alone are not sufficient: issue #19 wrote the template then failed while the IDE
            // loaded its SDK-style project. Verify the created project reached the live solution model
            // and can pass through the same in-process evaluation/build path a user sees.
            var solution = await WaitForProjectInSolutionAsync(Path.Combine(solutionRoot, solutionName + ".slnx"), projectFile);
            Assert.Equal(Path.Combine(solutionRoot, solutionName + ".slnx"), solution.GetProperty("solutionFile").GetString());
            Assert.Contains(solution.GetProperty("projects").EnumerateArray(), p =>
                p.GetProperty("name").GetString() == projectName
                && p.GetProperty("fileName").GetString() == projectFile);

            var build = await _app.InvokeAsync("od.build-solution", projectName);
            Assert.True(build.GetProperty("success").GetBoolean(), build.ToString());
            Assert.Equal("Success", build.GetProperty("result").GetString());
        }
        finally
        {
            await _app.InvokeAsync("od.new-solution-dialog.close");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NewFileDialog_CreatesTemplateOutput()
    {
        var targetDirectory = Path.Combine(Path.GetTempPath(), "opendevelop-new-file-" + Guid.NewGuid().ToString("N"));
        const string itemName = "GeneratedTemplate";
        try
        {
            Directory.CreateDirectory(targetDirectory);
            Assert.True((await _app.InvokeAsync("od.new-item-dialog", targetDirectory)).GetProperty("started").GetBoolean());
            await WaitForWindowAsync("NewItemWindow");
            await WaitForTemplatesAsync("item");
            var submitted = await _app.InvokeAsync("od.new-item-dialog.create", "Text Template (.tt)", itemName);
            Assert.True(submitted.GetProperty("submitted").GetBoolean(), submitted.ToString());

            var output = Path.Combine(targetDirectory, itemName + ".tt");
            await WaitForFileAsync(output);
            var text = await File.ReadAllTextAsync(output);
            Assert.Contains("namespace GeneratedFromT4", text);
            Assert.Contains("class GeneratedTemplate", text);
        }
        finally
        {
            await _app.InvokeAsync("od.new-item-dialog.close");
            if (Directory.Exists(targetDirectory)) Directory.Delete(targetDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task NewSolutionDialog_ConstructsAndExposesTemplatePicker()
    {
        var opened = await _app.InvokeAsync("od.new-solution-dialog");
        Assert.True(opened.GetProperty("started").GetBoolean());
        try
        {
            JsonElement tree = default;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(250);
                tree = await _app.GetUITreeAsync();
                var text = tree.ToString();
                if (text.Contains("NewProjectWindow") && text.Contains("Project name:") && text.Contains("Template options:"))
                    return;
            }
            Assert.Fail("New Solution dialog did not expose its project-name and template-option controls. UI tree: " + tree);
        }
        finally
        {
            var closed = await _app.InvokeAsync("od.new-solution-dialog.close");
            Assert.True(closed.GetProperty("closed").GetBoolean());
        }
    }

    async Task WaitForWindowAsync(string windowType)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if ((await _app.GetUITreeAsync()).ToString().Contains(windowType)) return;
            await Task.Delay(250);
        }
        Assert.Fail($"Expected {windowType} to appear.");
    }

    async Task WaitForTemplatesAsync(string kind)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var status = await _app.InvokeAsync("od.new-template-dialog.status");
            if (status.GetProperty("open").GetBoolean()
                && status.GetProperty("kind").GetString() == kind
                && status.GetProperty("templateCount").GetInt32() > 0)
                return;
            await Task.Delay(250);
        }
        Assert.Fail($"The {kind} template dialog did not finish loading its templates.");
    }

    static async Task WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (File.Exists(path)) return;
            await Task.Delay(250);
        }
        Assert.Fail($"Expected generated file was not created: {path}");
    }

    async Task<JsonElement> WaitForProjectInSolutionAsync(string solutionFile, string projectFile)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var solution = await _app.InvokeAsync("od.solution-tree");
            if (solution.GetProperty("solutionFile").GetString() == solutionFile
                && solution.GetProperty("projects").EnumerateArray().Any(p => p.GetProperty("fileName").GetString() == projectFile))
                return solution;
            await Task.Delay(250);
        }
        Assert.Fail($"Timed out waiting for '{projectFile}' to load into '{solutionFile}'.");
        return default;
    }
}
