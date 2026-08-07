// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

// Consolidated workbench-level integration tests (editor, solutions, projects, search,
// error list, XML editing, SDK/slnx handling, startup). Originally split across BuildTests,
// SdkTests, SlnxLoadingTests, SolutionExplorerAndEditorTests, SolutionExplorerCrudTests,
// SearchAndReplaceTests, SaveAndDirtyStateTests, XmlEditorTests, ErrorListTests,
// ProjectBrowserTests and StartupTests.

using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("OpenDevelop app")]
public sealed class WorkbenchTests
{
    readonly OpenDevelopAppFixture _app;

    const string ProjectName = "SampleApp";
    public WorkbenchTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task BuildSolution_FixtureProjectBuildsSuccessfully()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);

        var result = await _app.InvokeAsync("od.build-solution");

        Assert.True(result.GetProperty("success").GetBoolean(), "od.build-solution reported an infrastructure failure, not a build failure");
        Assert.Equal("Success", result.GetProperty("result").GetString());
        Assert.Equal(0, result.GetProperty("errorCount").GetInt32());
        Assert.Equal(0, result.GetProperty("warningCount").GetInt32());
        Assert.Empty(result.GetProperty("diagnostics").EnumerateArray());
    }

    [Fact]
    public async Task BuildSolution_OutputPadCapturesRealBuildLog()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);
        await _app.InvokeAsync("od.build-solution");

        var output = await _app.InvokeAsync("od.output-text");

        Assert.Equal("Build", output.GetProperty("category").GetString());
        string text = output.GetProperty("text").GetString()!;
        Assert.Contains("Build started.", text);
        Assert.Contains("Build succeeded.", text);
        Assert.Contains("SampleApp", text);
    }

    // Regression test for the Output pad being tab-docked behind an unrelated pane on its first
    // show: this fixture wipes ~/Library/Application Support/.../layouts/ before every launch (see
    // OpenDevelopAppFixture.DeleteStaleViewStateMemento), so the Output pad is never part of the
    // persisted layout when this runs - exactly the "never shown before" scenario
    // DockWorkspace.BeforeInsertAnchorable's ToolPaneModel.PreferredDockSide handling exists for.
    // Without that handling, AvalonDock's own AttachAnchorablesSource import falls back to
    // whatever pane hosts the active content (or the first pane it finds), so the Output pad ends
    // up as a background tab: od.output-text still returns the right build log (a different code
    // path - MessageViewCategory.Text), but the user never actually sees the pad pop up. isVisible
    // alone can't catch that regression (AvalonDock sets it even for a background tab); isSelected
    // is the front-most-tab-in-its-group flag that BuildService.BuildAsync's
    // SD.OutputPad.BuildCategory.Activate(bringPadToFront: true) call is supposed to guarantee.
    [Fact]
    public async Task BuildSolution_OutputPadIsActuallyShownNotJustPopulated()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);
        await _app.InvokeAsync("od.build-solution");

        var status = await _app.InvokeAsync("od.output-pad.status");

        Assert.True(status.GetProperty("isVisible").GetBoolean(),
            "Output pad should be docked into the layout after a build");
        Assert.True(status.GetProperty("isSelected").GetBoolean(),
            "Output pad should be the front-most tab in its dock group after a build - if this is " +
            "false, the pad exists but is hidden behind another tab (the bug this test guards against)");
    }

    [Fact]
    public async Task BuildSolution_UnknownProjectNameReturnsError()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);

        var result = await _app.InvokeAsync("od.build-solution", "NoSuchProject");

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("NoSuchProject", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SdkList_ReturnsDiscoveredSdksAndEffectiveSdk()
    {
        var result = await _app.InvokeAsync("od.sdk.list");

        // The DevFlow action serializes the anonymous type with its implicit PascalCase member
        // names (Label/RootPath/HighestSdkVersion), only "origin" and "selectedRootPath" are
        // explicitly cased.
        var effective = result.GetProperty("effective");
        Assert.False(string.IsNullOrEmpty(effective.GetProperty("Label").GetString()),
            "Expected an effective SDK label to be resolved");
        Assert.False(string.IsNullOrEmpty(effective.GetProperty("RootPath").GetString()),
            "Expected the effective SDK to have a root path");
        Assert.False(string.IsNullOrEmpty(effective.GetProperty("HighestSdkVersion").GetString()),
            "Expected the effective SDK to report its highest version");

        var sdks = result.GetProperty("sdks").EnumerateArray().ToList();
        Assert.True(sdks.Count > 0, "Expected at least one discovered .NET SDK");
        Assert.Contains(sdks, s => s.GetProperty("RootPath").GetString() == effective.GetProperty("RootPath").GetString());
    }

    [Fact]
    public async Task SdkSelect_RoundTripsBetweenExplicitSdkAndSystemDefault()
    {
        var list = await _app.InvokeAsync("od.sdk.list");
        var sdks = list.GetProperty("sdks").EnumerateArray().ToList();
        Assert.True(sdks.Count > 0, "Expected at least one discovered .NET SDK");
        var target = sdks[0].GetProperty("RootPath").GetString()!;

        try
        {
            var selected = await _app.InvokeAsync("od.sdk.select", target);
            Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
            Assert.Equal(target, selected.GetProperty("effective").GetProperty("RootPath").GetString());
        }
        finally
        {
            // Restore the system default so the shared app instance keeps behaving normally
            // for every later test in this collection.
            var restored = await _app.InvokeAsync("od.sdk.select", "");
            Assert.True(restored.GetProperty("success").GetBoolean(), restored.ToString());

            var after = await _app.InvokeAsync("od.sdk.list");
            var effectiveRoot = after.GetProperty("effective").GetProperty("RootPath").GetString();
            Assert.False(string.IsNullOrEmpty(effectiveRoot));
        }
    }

    [Fact]
    public async Task OpenSlnx_LoadsSolutionExplorerFixture()
    {
        var result = await _app.ReopenSolutionAsync(_app.SlnxFixturePath);

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(_app.SlnxFixturePath, result.GetProperty("currentSolution").GetString());
    }

    [Fact]
    public async Task SolutionTree_ListsAllProjects()
    {
        await _app.EnsureSolutionOpenAsync(_app.SlnxFixturePath);

        var tree = await _app.InvokeAsync("od.solution-tree");

        Assert.Equal(_app.SlnxFixturePath, tree.GetProperty("solutionFile").GetString());

        var projects = tree.GetProperty("projects").EnumerateArray().ToList();
        Assert.Equal(2, projects.Count);

        var lib = projects.Single(p => p.GetProperty("name").GetString() == "Lib");
        var appProj = projects.Single(p => p.GetProperty("name").GetString() == "App");

        var libFiles = lib.GetProperty("files").EnumerateArray().Select(f => f.GetString()!.Replace('\\', '/')).ToList();
        Assert.Contains(libFiles, f => f.EndsWith("Class1.cs"));

        var appFiles = appProj.GetProperty("files").EnumerateArray().Select(f => f.GetString()!.Replace('\\', '/')).ToList();
        Assert.Contains(appFiles, f => f.EndsWith("Program.cs"));
        Assert.Contains(appFiles, f => f.EndsWith("Utils/Helper.cs"));
    }

    [Fact]
    public async Task OpenSlnxFile_DisplaysInAvalonEdit()
    {
        await _app.EnsureSolutionOpenAsync(_app.SlnxFixturePath);

        var programPath = Path.Combine(Path.GetDirectoryName(_app.SlnxFixturePath)!, "App", "Program.cs");
        var openResult = await _app.InvokeAsync("od.open-file", programPath);
        Assert.True(openResult.GetProperty("opened").GetBoolean());

        var activeView = await _app.InvokeAsync("od.active-view");
        Assert.True(activeView.GetProperty("active").GetBoolean());
        Assert.True(activeView.GetProperty("isAvalonEdit").GetBoolean());

        var textPreview = activeView.GetProperty("textPreview").GetString();
        Assert.Contains("class Program", textPreview);
    }

    [Fact]
    public async Task OpenSolution_LoadsSolutionExplorerFixture()
    {
        var result = await _app.ReopenSolutionAsync(_app.SolutionExplorerFixturePath);

        Assert.True(result.GetProperty("success").GetBoolean(), $"OpenSolutionOrProject returned false for {_app.SolutionExplorerFixturePath}");
        Assert.Equal(_app.SolutionExplorerFixturePath, result.GetProperty("currentSolution").GetString());
    }

    [Fact]
    public async Task SolutionTree_MatchesFixtureProjectStructure()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);

        var tree = await _app.InvokeAsync("od.solution-tree");

        Assert.Equal(_app.SolutionExplorerFixturePath, tree.GetProperty("solutionFile").GetString());

        var projects = tree.GetProperty("projects").EnumerateArray().ToList();
        Assert.Single(projects);

        var sampleApp = projects[0];
        Assert.Equal("SampleApp", sampleApp.GetProperty("name").GetString());

        var files = sampleApp.GetProperty("files").EnumerateArray()
            .Select(f => f.GetString())
            .Select(f => f!.Replace('\\', '/'))
            .ToList();

        Assert.Contains(files, f => f.EndsWith("Program.cs"));
        Assert.Contains(files, f => f.EndsWith("Models/Widget.cs"));
        Assert.Contains(files, f => f.EndsWith("Services/WidgetService.cs"));
    }

    [Fact]
    public async Task OpenFile_DisplaysInAvalonEdit()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);
        var widgetPath = Path.Combine(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, "SampleApp", "Models", "Widget.cs");

        var openResult = await _app.InvokeAsync("od.open-file", widgetPath);
        Assert.True(openResult.GetProperty("opened").GetBoolean(), $"Failed to open {widgetPath}");

        var activeView = await _app.InvokeAsync("od.active-view");

        Assert.True(activeView.GetProperty("active").GetBoolean());
        Assert.True(activeView.GetProperty("isAvalonEdit").GetBoolean(),
            $"Expected AvalonEditViewContent, got {activeView.GetProperty("typeName").GetString()}");

        var fileName = activeView.GetProperty("fileName").GetString()!.Replace('\\', '/');
        Assert.EndsWith("Models/Widget.cs", fileName);

        // Confirm AvalonEdit actually loaded the real file content, not a blank/error view
        // (this is exactly the crash this session's fixes were about: AutoDetectDisplayBinding
        // throwing when no display binding was found, and the null FormattingStrategy crash).
        var textPreview = activeView.GetProperty("textPreview").GetString();
        Assert.Contains("class Widget", textPreview);
        Assert.Contains("namespace SampleApp.Models", textPreview);
    }


    // Folding indicators depend on a real Roslyn parse completing after the file opens
    // (CodeEditor.ParseInformationUpdated -> CodeEditorView.UpdateParseInformationForFolding ->
    // ParserFoldingStrategy.UpdateFoldings), which is async - so this polls od.file.foldings
    // instead of asserting immediately after od.open-file returns.
    [Fact]
    public async Task OpenCSharpFile_ShowsFoldingIndicators()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);
        var programPath = Path.Combine(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, "SampleApp", "Program.cs");

        var openResult = await _app.InvokeAsync("od.open-file", programPath);
        Assert.True(openResult.GetProperty("opened").GetBoolean(), $"Failed to open {programPath}");

        JsonElement foldings = default;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            foldings = await _app.InvokeAsync("od.file.foldings", programPath);
            if (foldings.GetProperty("success").GetBoolean() && foldings.GetProperty("foldingCount").GetInt32() > 0)
                break;
            await Task.Delay(500);
        }

        Assert.True(foldings.GetProperty("success").GetBoolean());
        Assert.True(foldings.GetProperty("hasFoldingMargin").GetBoolean(),
            "The FoldingMargin (the gutter's +/- indicator strip) should be installed in the editor's left margins");
        Assert.True(foldings.GetProperty("foldingManagerInstalled").GetBoolean(),
            "A FoldingManager should be registered on the editor's TextView");
        Assert.True(foldings.GetProperty("foldingCount").GetInt32() > 0,
            "Program.cs has a namespace, a class and a method body - expected at least one foldable region");
    }

    // The Project Browser pad renders its own tree in the real WPF visual tree (ProjectBrowserView.xaml's
    // HierarchicalDataTemplate -> TextBlock per node). od.solution-tree covers the backing model; this
    // locks in that opening a plain (non-git) solution actually displays the project, root file and
    // folder nodes as visible UI. Folder nodes render even when collapsed; files nested under a folder
    // (Widget.cs under Models/) are only realized once the folder is expanded, which has no DevFlow hook.
    [Fact]
    public async Task OpenSolution_ProjectBrowserPadRendersRealNodes()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);

        // The pad's TreeView content is only realized by AvalonDock once the pad is shown/activated
        // (same pattern as GitAddInTests).
        var showPadResult = await _app.InvokeAsync("od.show-pad", "ProjectBrowserPad");
        Assert.True(showPadResult.GetProperty("found").GetBoolean(), "Could not find the ProjectBrowser pad");

        var tree = await _app.GetUITreeAsync();
        var texts = FlattenElements(tree)
            .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
                && e.TryGetProperty("text", out var txt) && !string.IsNullOrEmpty(txt.GetString()))
            .Select(e => e.GetProperty("text").GetString())
            .ToList();

        Assert.Contains("SampleApp", texts);
        Assert.Contains("Program.cs", texts);
        Assert.Contains("Models", texts);
        Assert.Contains("Services", texts);
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

    string SampleAppDirectory => Path.Combine(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, "SampleApp");
    string ProjectFilePath => Path.Combine(SampleAppDirectory, "SampleApp.csproj");

    [Fact]
    public async Task AddFileToProject_CreatesFileAndAppearsInSolutionTree()
    {
        var originalProjectFile = File.ReadAllText(ProjectFilePath);
        var newFilePath = Path.Combine(SampleAppDirectory, "Models", "ScratchWidgetPart.txt");
        try
        {
            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);

            // "None" (not "Compile") - SDK-style projects implicitly glob *.cs as Compile items, so
            // adding an explicit <Compile Include> for a file that glob would already pick up
            // creates a duplicate-item build error (NETSDK1022). "None" avoids that entirely.
            var addResult = await _app.InvokeAsync("od.solution.add-file", ProjectName, newFilePath, "None");
            Assert.True(addResult.GetProperty("success").GetBoolean());
            Assert.True(File.Exists(newFilePath));

            var files = addResult.GetProperty("files").EnumerateArray()
                .Select(f => f.GetString()!.Replace('\\', '/')).ToList();
            Assert.Contains(files, f => f.EndsWith("Models/ScratchWidgetPart.txt"));

            var tree = await _app.InvokeAsync("od.solution-tree");
            var treeFiles = tree.GetProperty("projects").EnumerateArray().Single()
                .GetProperty("files").EnumerateArray()
                .Select(f => f.GetString()!.Replace('\\', '/')).ToList();
            Assert.Contains(treeFiles, f => f.EndsWith("Models/ScratchWidgetPart.txt"));
        }
        finally
        {
            File.WriteAllText(ProjectFilePath, originalProjectFile);
            TryDelete(newFilePath);
        }
    }

    // A file added *outside* the project directory (SDK-style implicit item globs, e.g.
    // Microsoft.NET.Sdk.DefaultItems.props' `<None Include="**">`, are rooted at the project
    // directory) is a genuinely explicit ProjectItem, not glob-derived - so it's actually
    // removable via the normal item-collection API. See the sibling test below for what happens
    // to a file *inside* the project directory, which the glob covers regardless of item removal.
    [Fact]
    public async Task RemoveExplicitFileFromProject_DropsItFromTreeButKeepsFileOnDisk()
    {
        var originalProjectFile = File.ReadAllText(ProjectFilePath);
        var sharedDirectory = Path.Combine(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, "Shared");
        Directory.CreateDirectory(sharedDirectory);
        var filePath = Path.Combine(sharedDirectory, "ScratchExplicit.txt");
        try
        {
            File.WriteAllText(filePath, "scratch content");
            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.solution.add-file", ProjectName, filePath, "None");

            var removeResult = await _app.InvokeAsync("od.solution.remove-file", ProjectName, filePath);
            Assert.True(removeResult.GetProperty("success").GetBoolean());

            var files = removeResult.GetProperty("files").EnumerateArray()
                .Select(f => f.GetString()!.Replace('\\', '/')).ToList();
            Assert.DoesNotContain(files, f => f.EndsWith("Shared/ScratchExplicit.txt"));

            // Removing the ProjectItem must not touch the file on disk.
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            File.WriteAllText(ProjectFilePath, originalProjectFile);
            TryDelete(filePath);
            try { Directory.Delete(sharedDirectory); } catch { }
        }
    }

    // A file physically inside the project directory is always covered by the SDK's own implicit
    // item glob, independent of any explicit ProjectItem add/remove - od.solution.remove-file
    // detects this (item.IsAddedToProject == false) and reports failure honestly instead of
    // silently no-op'ing while claiming success.
    [Fact]
    public async Task RemoveGlobCoveredFile_ReportsUnsupportedInsteadOfSilentlyNoOp()
    {
        var originalProjectFile = File.ReadAllText(ProjectFilePath);
        var filePath = Path.Combine(SampleAppDirectory, "Models", "ScratchToRemove.txt");
        try
        {
            File.WriteAllText(filePath, "scratch content");
            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.solution.add-file", ProjectName, filePath, "None");

            var removeResult = await _app.InvokeAsync("od.solution.remove-file", ProjectName, filePath);
            Assert.False(removeResult.GetProperty("success").GetBoolean());
            Assert.Contains("implicit item glob", removeResult.GetProperty("error").GetString());

            var files = removeResult.GetProperty("files").EnumerateArray()
                .Select(f => f.GetString()!.Replace('\\', '/')).ToList();
            Assert.Contains(files, f => f.EndsWith("Models/ScratchToRemove.txt"));
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            File.WriteAllText(ProjectFilePath, originalProjectFile);
            TryDelete(filePath);
        }
    }

    [Fact]
    public async Task RenameProjectFile_MovesFileAndUpdatesTree()
    {
        var originalProjectFile = File.ReadAllText(ProjectFilePath);
        var oldPath = Path.Combine(SampleAppDirectory, "Models", "ScratchOldName.txt");
        var newPath = Path.Combine(SampleAppDirectory, "Models", "ScratchNewName.txt");
        try
        {
            File.WriteAllText(oldPath, "scratch content");
            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.solution.add-file", ProjectName, oldPath, "None");

            var renameResult = await _app.InvokeAsync("od.solution.rename-file", ProjectName, oldPath, newPath);
            Assert.True(renameResult.GetProperty("success").GetBoolean());

            Assert.False(File.Exists(oldPath));
            Assert.True(File.Exists(newPath));

            var files = renameResult.GetProperty("files").EnumerateArray()
                .Select(f => f.GetString()!.Replace('\\', '/')).ToList();
            Assert.DoesNotContain(files, f => f.EndsWith("Models/ScratchOldName.txt"));
            Assert.Contains(files, f => f.EndsWith("Models/ScratchNewName.txt"));
        }
        finally
        {
            File.WriteAllText(ProjectFilePath, originalProjectFile);
            TryDelete(oldPath);
            TryDelete(newPath);
        }
    }

    [Fact]
    public async Task AddProjectReference_AddsReferenceProjectItem()
    {
        var originalProjectFile = File.ReadAllText(ProjectFilePath);
        try
        {
            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);

            var addResult = await _app.InvokeAsync("od.solution.add-reference", ProjectName, "System.Xml");
            Assert.True(addResult.GetProperty("success").GetBoolean());

            var references = addResult.GetProperty("references").EnumerateArray()
                .Select(r => r.GetString()).ToList();
            Assert.Contains("System.Xml", references);

            Assert.Contains("System.Xml", File.ReadAllText(ProjectFilePath));
        }
        finally
        {
            File.WriteAllText(ProjectFilePath, originalProjectFile);
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [Fact]
    public async Task Find_InSolution_FindsTermAcrossMultipleFiles()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);

        // "Widget" appears in both Models/Widget.cs (class declaration) and Services/WidgetService.cs
        // (usage) in the real fixture files - a genuine cross-file plain-text match, not a rename.
        var result = await _app.InvokeAsync("od.search.find", "Widget", "solution");

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.True(result.GetProperty("matchCount").GetInt32() > 1);
        Assert.True(result.GetProperty("fileCount").GetInt32() >= 2);

        var files = result.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("file").GetString()!.Replace('\\', '/')).ToList();
        Assert.Contains(files, f => f.EndsWith("Models/Widget.cs"));
        Assert.Contains(files, f => f.EndsWith("Services/WidgetService.cs"));

        var widgetFileMatches = result.GetProperty("files").EnumerateArray()
            .First(f => f.GetProperty("file").GetString()!.Replace('\\', '/').EndsWith("Models/Widget.cs"))
            .GetProperty("matches").EnumerateArray().ToList();
        Assert.NotEmpty(widgetFileMatches);
        Assert.True(widgetFileMatches[0].GetProperty("line").GetInt32() > 0);
    }

    [Fact]
    public async Task Find_MatchCase_RespectsCaseSensitivity()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);

        var caseInsensitive = await _app.InvokeAsync("od.search.find", "WIDGET", "solution", false, false, false);
        Assert.True(caseInsensitive.GetProperty("matchCount").GetInt32() > 0);

        var caseSensitive = await _app.InvokeAsync("od.search.find", "WIDGET", "solution", true, false, false);
        Assert.Equal(0, caseSensitive.GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public async Task Find_UseRegex_MatchesPattern()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);

        var result = await _app.InvokeAsync("od.search.find", @"Widget\w*", "solution", false, false, true);

        Assert.True(result.GetProperty("success").GetBoolean());
        var files = result.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("file").GetString()!.Replace('\\', '/')).ToList();
        Assert.Contains(files, f => f.EndsWith("Services/WidgetService.cs"));
    }

    [Fact]
    public async Task ShowResults_PopulatesSearchResultsPadUiTree()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);

        // od.search.find is headless; od.search.show-results goes through the same real
        // SearchManager.FindAllParallel engine but also feeds SearchManager.ShowSearchResults ->
        // SearchResultsPad, which the Find-in-Files dialog path never does. The pad's ResultsTreeView
        // renders each SearchNode's Text via a ContentPresenter - the TextBlocks are built from
        // Inlines (which TextBlock.Text does not expose), so the nodes carry AutomationIds
        // (SearchRootNode/SearchFileNode/SearchResultNode, see the Gui/SearchNode*.cs CreateText
        // methods) for the UI tree to identify. Poll for the async results to arrive.
        var showResult = await _app.InvokeAsync("od.search.show-results", "Widget", "solution");
        Assert.True(showResult.GetProperty("success").GetBoolean());

        // The pad is registered with defaultPosition "Bottom, Hidden" (auto-hide), whose content is
        // not realized until the pad is actually activated - the same pattern as the other pad tests.
        var showPadResult = await _app.InvokeAsync("od.show-pad", "Search Results");
        Assert.True(showPadResult.GetProperty("found").GetBoolean(), "Could not find the Search Results pad");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement tree = default;
        List<JsonElement> elements = new();
        while (DateTime.UtcNow < deadline)
        {
            tree = await _app.GetUITreeAsync();
            elements = FlattenElements(tree).ToList();
            if (elements.Count(e =>
                e.TryGetProperty("automationId", out var a) && a.GetString() == "SearchResultNode"
                && e.TryGetProperty("isVisible", out var v) && v.GetBoolean()) >= 2)
                break;
            await Task.Delay(500);
        }

        Assert.True(elements.Any(e =>
            e.TryGetProperty("automationId", out var a) && a.GetString() == "SearchRootNode"
            && e.TryGetProperty("isVisible", out var v) && v.GetBoolean()),
            "Expected the Search Results pad root node to be rendered and visible");

        // The default grouping is Flat (no per-file nodes); the match rows themselves are the
        // rendered content. "Widget" matches in both Models/Widget.cs and Services/WidgetService.cs
        // (see Find_InSolution_FindsTermAcrossMultipleFiles), so at least two real match rows must
        // be visible.
        Assert.True(elements.Count(e =>
            e.TryGetProperty("automationId", out var a) && a.GetString() == "SearchResultNode"
            && e.TryGetProperty("isVisible", out var v) && v.GetBoolean()) >= 2,
            "Expected at least two match nodes to be rendered and visible");
    }





    [Fact]
    public async Task Replace_InOpenFile_UpdatesEditorButNotDiskUntilSaved()
    {
        var scratchPath = Path.Combine(SampleAppDirectory, "ScratchReplaceTarget.cs");
        try
        {
            File.WriteAllText(scratchPath, "namespace SampleApp { class ScratchReplaceTarget { string Value = \"NeedleValue\"; } }");

            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.open-file", scratchPath);

            var replaceResult = await _app.InvokeAsync("od.search.replace", "NeedleValue", "ReplacedValue", "current-document");
            Assert.True(replaceResult.GetProperty("success").GetBoolean());
            Assert.Equal(1, replaceResult.GetProperty("replacedCount").GetInt32());

            // Replaced in the open editor buffer...
            var activeView = await _app.InvokeAsync("od.active-view");
            Assert.Contains("ReplacedValue", activeView.GetProperty("textPreview").GetString());

            var dirtyStatus = await _app.InvokeAsync("od.file.is-dirty", scratchPath);
            Assert.True(dirtyStatus.GetProperty("isDirty").GetBoolean());

            // ...but SearchManager.ReplaceAll only edits the in-memory document - disk is untouched
            // until an explicit save.
            Assert.Contains("NeedleValue", File.ReadAllText(scratchPath));

            await _app.InvokeAsync("od.file.save", scratchPath);
            Assert.Contains("ReplacedValue", File.ReadAllText(scratchPath));
            Assert.DoesNotContain("NeedleValue", File.ReadAllText(scratchPath));
        }
        finally
        {
            TryDelete(scratchPath);
        }
    }

    string ScratchDirectory => Path.Combine(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, "SampleApp");

    [Fact]
    public async Task OpenFile_IsNotDirtyInitially()
    {
        var path = Path.Combine(ScratchDirectory, "ScratchNotDirty.cs");
        File.WriteAllText(path, "namespace SampleApp { class ScratchNotDirty { } }");
        try
        {
            await _app.InvokeAsync("od.open-file", path);

            var status = await _app.InvokeAsync("od.file.is-dirty", path);
            Assert.True(status.GetProperty("isOpen").GetBoolean());
            Assert.False(status.GetProperty("isDirty").GetBoolean());
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task EditFile_MarksDirty()
    {
        var path = Path.Combine(ScratchDirectory, "ScratchEditDirty.cs");
        File.WriteAllText(path, "namespace SampleApp { class ScratchEditDirty { } }");
        try
        {
            await _app.InvokeAsync("od.open-file", path);

            var editResult = await _app.InvokeAsync("od.file.edit-text", path, "\n// edited by test\n");
            Assert.True(editResult.GetProperty("success").GetBoolean());
            Assert.True(editResult.GetProperty("isDirty").GetBoolean());

            var status = await _app.InvokeAsync("od.file.is-dirty", path);
            Assert.True(status.GetProperty("isDirty").GetBoolean());
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task SaveFile_ClearsDirtyFlagAndPersistsContent()
    {
        var path = Path.Combine(ScratchDirectory, "ScratchSave.cs");
        File.WriteAllText(path, "namespace SampleApp { class ScratchSave { } }");
        try
        {
            await _app.InvokeAsync("od.open-file", path);
            await _app.InvokeAsync("od.file.edit-text", path, "\n// saved by test\n");

            var saveResult = await _app.InvokeAsync("od.file.save", path);
            Assert.True(saveResult.GetProperty("success").GetBoolean());
            Assert.False(saveResult.GetProperty("isDirty").GetBoolean());

            var status = await _app.InvokeAsync("od.file.is-dirty", path);
            Assert.False(status.GetProperty("isDirty").GetBoolean());

            var diskContent = File.ReadAllText(path);
            Assert.Contains("// saved by test", diskContent);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task SaveAllOpenFiles_SavesEveryDirtyFile()
    {
        var pathA = Path.Combine(ScratchDirectory, "ScratchSaveAllA.cs");
        var pathB = Path.Combine(ScratchDirectory, "ScratchSaveAllB.cs");
        File.WriteAllText(pathA, "namespace SampleApp { class ScratchSaveAllA { } }");
        File.WriteAllText(pathB, "namespace SampleApp { class ScratchSaveAllB { } }");
        try
        {
            await _app.InvokeAsync("od.open-file", pathA);
            await _app.InvokeAsync("od.open-file", pathB);
            await _app.InvokeAsync("od.file.edit-text", pathA, "\n// A dirty\n");
            await _app.InvokeAsync("od.file.edit-text", pathB, "\n// B dirty\n");

            var result = await _app.InvokeAsync("od.file.save-all");
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.Empty(result.GetProperty("stillDirtyFiles").EnumerateArray());

            Assert.False((await _app.InvokeAsync("od.file.is-dirty", pathA)).GetProperty("isDirty").GetBoolean());
            Assert.False((await _app.InvokeAsync("od.file.is-dirty", pathB)).GetProperty("isDirty").GetBoolean());
            Assert.Contains("// A dirty", File.ReadAllText(pathA));
            Assert.Contains("// B dirty", File.ReadAllText(pathB));
        }
        finally
        {
            TryDelete(pathA);
            TryDelete(pathB);
        }
    }

    async Task OpenSolutionAndFile()
    {
        await _app.EnsureSolutionOpenAsync(_app.FixtureSolutionPath);
    }

    [Fact]
    public async Task OpenXmlFile_AttachesXmlTreeView()
    {
        await OpenSolutionAndFile();

        var open = await _app.InvokeAsync("od.open-file", _app.XmlFixtureFilePath);
        Assert.True(open.GetProperty("opened").GetBoolean(), $"Failed to open {_app.XmlFixtureFilePath}");

        var status = await _app.InvokeAsync("od.xml-tree-status");
        Assert.True(status.GetProperty("found").GetBoolean(), status.ToString());
        Assert.Equal("ICSharpCode.XmlEditor.XmlTreeView", status.GetProperty("viewType").GetString());
    }

    [Fact]
    public async Task OpenXmlFile_XmlTreeViewTabTitleIsNotEmpty()
    {
        await OpenSolutionAndFile();

        var open = await _app.InvokeAsync("od.open-file", _app.XmlFixtureFilePath);
        Assert.True(open.GetProperty("opened").GetBoolean());

        var status = await _app.InvokeAsync("od.xml-tree-status");
        Assert.True(status.GetProperty("found").GetBoolean(), status.ToString());
        Assert.False(string.IsNullOrEmpty(status.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task OpenNonXmlFile_DoesNotAttachXmlTreeView()
    {
        await OpenSolutionAndFile();

        var csFile = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(_app.FixtureSolutionPath)!, "PassTests.cs");
        var open = await _app.InvokeAsync("od.open-file", csFile);
        Assert.True(open.GetProperty("opened").GetBoolean());

        var status = await _app.InvokeAsync("od.xml-tree-status");
        // If an XmlTreeView from a previously opened .xml file lingers in the window, found
        // will still be true — check that it's *not* associated with the .cs file.
        if (status.GetProperty("found").GetBoolean())
        {
            var primaryFile = status.GetProperty("primaryFile").GetString();
            Assert.False(primaryFile!.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),
                $"Expected XmlTreeView NOT attached to .cs file, but found primaryFile={primaryFile}");
        }
    }

    [Fact]
    public async Task ErrorList_IsEmptyAfterCleanBuild()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);
        await _app.InvokeAsync("od.error-list.clear");
        await _app.InvokeAsync("od.build-solution");

        var errorList = await _app.InvokeAsync("od.error-list");
        Assert.Equal(0, errorList.GetProperty("errorCount").GetInt32());
    }

    // MinimalMSBuildEngine (a real `dotnet build` child process, per BuildTests.cs) parses its own
    // stdout/stderr for standard MSBuild diagnostic lines via a regex (DiagnosticLine in
    // MinimalMSBuildEngine.cs), reporting one BuildError per match with real file/line/column - and
    // only falls back to a single generic "Build failed (exit code non-zero)" entry if nothing
    // matched. That regex used to only handle the classic 2-number "(line,column):" shape; this
    // repo's SDK/Roslyn version instead emits a 4-number span shape - "(line,col,endLine,endCol):" -
    // for CS1002 and similar diagnostics, which silently fell through the old regex, always hitting
    // the generic fallback. Fixed by making the trailing ",endLine,endColumn" group optional. This
    // test locks in the real per-line diagnostics now reaching both od.build-solution's own
    // BuildResults.Errors and the Error List pad (TaskService, a separate code path - see
    // UIBuildFeedbackSink.ReportError).
    [Fact]
    public async Task ErrorList_OnBuildFailure_CapturesRealPerLineCompileErrors()
    {
        var brokenFilePath = Path.Combine(SampleAppDirectory, "ScratchBroken.cs");
        try
        {
            File.WriteAllText(brokenFilePath,
                "namespace SampleApp {\n" +
                "    class ScratchBroken {\n" +
                "        void Method() { this is not valid csharp syntax at all }\n" +
                "    }\n" +
                "}\n");

            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.error-list.clear");

            var buildResult = await _app.InvokeAsync("od.build-solution");
            Assert.False(buildResult.GetProperty("errorCount").GetInt32() == 0, "Expected the broken scratch file to produce build errors");

            // od.build-solution's own diagnostics now have real per-line detail, not just the one
            // generic summary.
            var buildDiagnostics = buildResult.GetProperty("diagnostics").EnumerateArray().ToList();
            Assert.Contains(buildDiagnostics, d =>
                (d.GetProperty("fileName").GetString() ?? "").Replace('\\', '/').EndsWith("ScratchBroken.cs")
                && d.GetProperty("line").GetInt32() == 3
                && d.GetProperty("errorCode").GetString() == "CS1002");

            // ...and so does the separately-populated Error List pad.
            var errorList = await _app.InvokeAsync("od.error-list");
            var tasks = errorList.GetProperty("tasks").EnumerateArray().ToList();
            var brokenFileTasks = tasks.Where(t =>
                (t.GetProperty("file").GetString() ?? "").Replace('\\', '/').EndsWith("ScratchBroken.cs")).ToList();

            Assert.NotEmpty(brokenFileTasks);
            Assert.Contains(brokenFileTasks, t => t.GetProperty("type").GetString() == "Error");
            Assert.Contains(brokenFileTasks, t => t.GetProperty("line").GetInt32() == 3);
            Assert.Contains(brokenFileTasks, t => !string.IsNullOrWhiteSpace(t.GetProperty("description").GetString()));

            // The pad's own UI must render the error as real ListView rows (the JSON above is
            // TaskService's data; the ListView rows are TaskViewResources.xaml's GridView cells),
            // which only happens once AvalonDock actually shows the pad.
            var showPadResult = await _app.InvokeAsync("od.show-pad", "ErrorListPad");
            Assert.True(showPadResult.GetProperty("found").GetBoolean(), "Could not find the ErrorList pad");

            var tree = await _app.GetUITreeAsync();
            var elements = FlattenElements(tree).ToList();

            // File column (DisplayMemberBinding="{Binding File}"): an auto-generated TextBlock
            // carrying the real file path.
            Assert.Contains(elements, e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
                && e.TryGetProperty("text", out var txt) && (txt.GetString() ?? "").Replace('\\', '/').EndsWith("ScratchBroken.cs"));

            // Description column (explicit TextBlock bound to Description): the same text the
            // pad's TaskService reports, rendered in the visible row.
            var taskDescriptions = brokenFileTasks
                .Select(t => t.GetProperty("description").GetString())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct().ToList();
            Assert.NotEmpty(taskDescriptions);
            Assert.Contains(elements, e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
                && e.TryGetProperty("text", out var txt) && taskDescriptions.Contains(txt.GetString()));
        }
        finally
        {
            TryDelete(brokenFilePath);
            // Leave the app's own error-list state clean for whichever test runs next in this
            // shared app instance, rather than letting od.build-solution's non-clearing behavior
            // leak this test's induced error into later tests.
            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.error-list.clear");
            await _app.InvokeAsync("od.build-solution");
        }
    }

    [Fact]
    public async Task ErrorList_WithoutExplicitClear_StaleEntriesSurviveANewCleanBuild()
    {
        // Documents a real characteristic (not a test bug): od.build-solution calls
        // SD.BuildService.BuildAsync directly, bypassing the Build menu command's
        // TaskService.ClearExceptCommentTasks() - so unlike using the actual Build menu/toolbar
        // button, driving builds through this API can leave a previous build's errors in the
        // Error List pad even after a subsequent build of now-fixed code succeeds.
        var brokenFilePath = Path.Combine(SampleAppDirectory, "ScratchStale.cs");
        try
        {
            File.WriteAllText(brokenFilePath, "this is not valid csharp syntax at all");

            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.error-list.clear");
            await _app.InvokeAsync("od.build-solution");

            var afterBrokenBuild = await _app.InvokeAsync("od.error-list");
            Assert.True(afterBrokenBuild.GetProperty("errorCount").GetInt32() > 0);

            // Fix the code and build again, WITHOUT calling od.error-list.clear first.
            TryDelete(brokenFilePath);
            var secondBuildResult = await _app.InvokeAsync("od.build-solution");
            Assert.Equal(0, secondBuildResult.GetProperty("errorCount").GetInt32());

            var afterFixedBuild = await _app.InvokeAsync("od.error-list");
            Assert.True(afterFixedBuild.GetProperty("errorCount").GetInt32() > 0,
                "Expected the stale error from the earlier broken build to still be present, since od.build-solution never clears the Error List pad on its own");
        }
        finally
        {
            TryDelete(brokenFilePath);
            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.error-list.clear");
            await _app.InvokeAsync("od.build-solution");
        }
    }

    [Fact]
    public async Task WpfCodeBehindIsNestedAndTreeScrollsVertically()
    {
        var opened = await _app.ReopenSolutionAsync(_app.WpfSampleSolutionPath);
        Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

        var state = await _app.InvokeAsync("od.project-browser-state", "sample");
        Assert.True(state.GetProperty("success").GetBoolean(), state.ToString());
        Assert.Equal("Auto", state.GetProperty("verticalScrollBarVisibility").GetString());

        var project = state.GetProperty("project");
        var mainWindow = FindNode(project, "MainWindow.xaml");
        Assert.NotEqual(default, mainWindow.ValueKind);
        Assert.Contains(mainWindow.GetProperty("children").EnumerateArray(), child =>
            child.GetProperty("name").GetString() == "MainWindow.xaml.cs");
    }

    static JsonElement FindNode(JsonElement node, string name)
    {
        if (node.GetProperty("name").GetString() == name)
            return node;
        foreach (var child in node.GetProperty("children").EnumerateArray()) {
            var match = FindNode(child, name);
            if (match.ValueKind != JsonValueKind.Undefined)
                return match;
        }
        return default;
    }
}

// Empty-startup behavior can only be asserted against a *fresh* app instance: in the shared
// "OpenDevelop app" collection a previous test has already opened a solution, so the active view
// is a document, not the Start Page. This collection gets its own OpenDevelopAppFixture (a
// second, freshly launched app process on the same DevFlow port - safe because the whole test
// assembly runs with parallelization disabled, so collections execute one after another and the
// previous fixture's DisposeAsync has already killed its app).
[Collection("OpenDevelop startup")]
public sealed class StartupTests
{
    readonly OpenDevelopAppFixture _app;

    public StartupTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task EmptyStartup_LoadsStartPageAddInAndShowsStartPage()
    {
        var addInsResult = await _app.InvokeAsync("od.addins");
        var addins = addInsResult.GetProperty("addins").EnumerateArray().ToList();

        Assert.Contains(addins, a => a.GetProperty("fileName").GetString()!.Contains("StartPage.addin"));

        var activeView = await _app.InvokeAsync("od.active-view");

        Assert.True(activeView.GetProperty("active").GetBoolean(), "Expected an active Start Page view.");
        Assert.Equal("ICSharpCode.StartPage.StartPageViewContent", activeView.GetProperty("typeName").GetString());
    }
}

[CollectionDefinition("OpenDevelop startup")]
public sealed class OpenDevelopStartupCollection : ICollectionFixture<OpenDevelopAppFixture> { }
