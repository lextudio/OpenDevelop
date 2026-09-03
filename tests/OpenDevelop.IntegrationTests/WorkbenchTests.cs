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

[Collection("20 General workbench fixture")]
public sealed class WorkbenchTests
{
    readonly OpenDevelopAppFixture _app;

    const string ProjectName = "SampleApp";
    public WorkbenchTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    // Merged: BuildSolution_FixtureProjectBuildsSuccessfully, BuildSolution_OutputPadCapturesRealBuildLog,
    // BuildSolution_OutputPadIsActuallyShownNotJustPopulated, BuildSolution_UnknownProjectNameReturnsError
    // and ErrorList_IsEmptyAfterCleanBuild all open the same SolutionExplorerFixturePath and only read
    // back build/output/error-list state - no project or file mutation - so they share a single open
    // and a single clean build instead of five.
    [Fact]
    public async Task BuildSolution_ChecksResultOutputPadErrorListAndUnknownProject()
    {
        // Project CRUD scenarios may have just completed while their project-system refresh is
        // still draining. A clean-build assertion needs a genuine clean load; reusing that live
        // solution can cause BuildService to observe the previous refresh cancellation token.
        await _app.ReopenSolutionAsync(_app.SolutionExplorerFixturePath);
        await _app.InvokeAsync("od.error-list.clear");

        // --- was: BuildSolution_FixtureProjectBuildsSuccessfully ---
        var result = await _app.InvokeAsync("od.build-solution");
        for (var attempt = 1;
             attempt < 3 && result.GetProperty("result").GetString() == "Cancelled";
             attempt++)
        {
            // Project reload completion and BuildService readiness are signaled on different UI
            // turns. A cancellation here means no compilation ran; wait for the reload queue and
            // retry the same clean build rather than treating that transient as a compiler result.
            await Task.Delay(500);
            result = await _app.InvokeAsync("od.build-solution");
        }

        Assert.True(result.GetProperty("success").GetBoolean(), "od.build-solution reported an infrastructure failure, not a build failure");
        // Report the compiler's own output rather than just "Success vs Error" - see DescribeBuildAsync.
        if (result.GetProperty("result").GetString() != "Success"
            || result.GetProperty("errorCount").GetInt32() != 0
            || result.GetProperty("warningCount").GetInt32() != 0)
        {
            Assert.Fail(await _app.DescribeBuildAsync(result));
        }
        Assert.Empty(result.GetProperty("diagnostics").EnumerateArray());

        // --- was: BuildSolution_OutputPadCapturesRealBuildLog ---
        var output = await _app.InvokeAsync("od.output-text");

        Assert.Equal("Build", output.GetProperty("category").GetString());
        string text = output.GetProperty("text").GetString()!;
        Assert.Contains("Build started.", text);
        Assert.Contains("Build succeeded.", text);
        Assert.Contains("SampleApp", text);

        // --- was: BuildSolution_OutputPadIsActuallyShownNotJustPopulated ---
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
        var status = await _app.InvokeAsync("od.output-pad.status");

        Assert.True(status.GetProperty("isVisible").GetBoolean(),
            "Output pad should be docked into the layout after a build");
        Assert.True(status.GetProperty("isSelected").GetBoolean(),
            "Output pad should be the front-most tab in its dock group after a build - if this is " +
            "false, the pad exists but is hidden behind another tab (the bug this test guards against)");

        // --- was: ErrorList_IsEmptyAfterCleanBuild ---
        var errorList = await _app.InvokeAsync("od.error-list");
        if (errorList.GetProperty("errorCount").GetInt32() != 0)
            Assert.Fail("Error List was not empty after a clean build. " + errorList + Environment.NewLine
                + await _app.DescribeBuildAsync(result));

        // --- was: BuildSolution_UnknownProjectNameReturnsError ---
        var unknownProjectResult = await _app.InvokeAsync("od.build-solution", "NoSuchProject");

        Assert.False(unknownProjectResult.GetProperty("success").GetBoolean());
        Assert.Contains("NoSuchProject", unknownProjectResult.GetProperty("error").GetString());
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
    public async Task OpenSlnx_WithEmptyFolderElement_DoesNotHang()
    {
        // Regression test for a hang reported when opening JexusManager's .slnx: it declares an
        // empty placeholder folder (<Folder Name="/.nuget/" />, self-closing, no children), a
        // common convention for a "Solution Items"-style folder. SlnxSolutionLoader.ReadFolderContents
        // only advanced the XmlReader past a <Folder> when it had children; for a self-closing one it
        // returned without moving the reader, so the caller's while loop kept re-reading the exact
        // same node forever - allocating a brand new SolutionFolder on every iteration, on the UI
        // thread, until the process ran out of memory. Worked on a copy since this rewrites the .slnx.
        var workingDir = Path.Combine(Path.GetTempPath(), "SlnxEmptyFolderTests-" + Guid.NewGuid().ToString("N"));
        CopyFixtureDirectory(Path.GetDirectoryName(_app.SlnxFixturePath)!, workingDir);
        var slnxPath = Path.Combine(workingDir, Path.GetFileName(_app.SlnxFixturePath));

        try
        {
            var original = File.ReadAllText(slnxPath);
            var withEmptyFolder = original.Replace("<Solution>", "<Solution>\n  <Folder Name=\"/.nuget/\" />");
            Assert.Contains("<Folder Name=\"/.nuget/\" />", withEmptyFolder);
            File.WriteAllText(slnxPath, withEmptyFolder);

            var result = await _app.ReopenSolutionAsync(slnxPath);

            Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
            Assert.Equal(slnxPath, result.GetProperty("currentSolution").GetString());

            // The loader must have kept going past the empty folder and loaded the real project(s),
            // not bailed out into an empty shell.
            var tree = await _app.InvokeAsync("od.solution-tree");
            Assert.Equal(slnxPath, tree.GetProperty("solutionFile").GetString());
            Assert.NotEmpty(tree.GetProperty("projects").EnumerateArray().ToList());
        }
        finally
        {
            try { Directory.Delete(workingDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task OpenSln_MigratesToSlnxAndOpensTheSlnx()
    {
        // Opening a classic .sln converts it to the XML .slnx format first and opens that, so the
        // solution the workbench ends up holding is the .slnx sitting next to the original.
        // Worked on a copy: migration writes a new file next to the solution, and this repo's
        // fixture is tracked.
        var workingDir = Path.Combine(Path.GetTempPath(), "SlnMigrateTests-" + Guid.NewGuid().ToString("N"));
        CopyFixtureDirectory(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, workingDir);
        var slnPath = Path.Combine(workingDir, Path.GetFileName(_app.SolutionExplorerFixturePath));
        var slnxPath = Path.ChangeExtension(slnPath, ".slnx");

        try
        {
            // The fixture ships a .slnx beside its .sln (the app's steady state after a first
            // open), so strip it from the copy to test the genuine migrate-from-scratch path.
            if (File.Exists(slnxPath))
                File.Delete(slnxPath);
            Assert.True(File.Exists(slnPath), "Expected the copied fixture to still be a .sln.");
            Assert.False(File.Exists(slnxPath), "The copied fixture should not start with a .slnx.");

            var result = await _app.ReopenSolutionAsync(slnPath);

            Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
            Assert.True(File.Exists(slnxPath), "Expected opening the .sln to produce a .slnx beside it.");
            Assert.Equal(slnxPath, result.GetProperty("currentSolution").GetString());

            // The migrated solution still describes the same projects, not an empty shell.
            var tree = await _app.InvokeAsync("od.solution-tree");
            Assert.Equal(slnxPath, tree.GetProperty("solutionFile").GetString());
        }
        finally
        {
            try { Directory.Delete(workingDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task OpenSln_WhenSlnxAlreadyExists_AdoptsItWithoutRegenerating()
    {
        // A .slnx beside the .sln is opened as-is: regenerating it would discard whatever the user
        // (or an earlier migration) put there.
        var workingDir = Path.Combine(Path.GetTempPath(), "SlnMigrateTests-" + Guid.NewGuid().ToString("N"));
        CopyFixtureDirectory(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, workingDir);
        var slnPath = Path.Combine(workingDir, Path.GetFileName(_app.SolutionExplorerFixturePath));
        var slnxPath = Path.ChangeExtension(slnPath, ".slnx");

        try
        {
            await _app.ReopenSolutionAsync(slnPath);
            Assert.True(File.Exists(slnxPath));
            var generated = File.ReadAllText(slnxPath);

            // Mark the file, reopen the .sln, and check the marker survived.
            var marked = generated.Replace("</Solution>", "  <!-- hand edited -->\n</Solution>");
            File.WriteAllText(slnxPath, marked);

            var result = await _app.ReopenSolutionAsync(slnPath);

            Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
            Assert.Equal(slnxPath, result.GetProperty("currentSolution").GetString());
            Assert.Contains("hand edited", File.ReadAllText(slnxPath));
        }
        finally
        {
            try { Directory.Delete(workingDir, recursive: true); } catch { }
        }
    }

    static void CopyFixtureDirectory(string sourceDir, string destDir)
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

    /// <summary>
    /// Copy-on-write isolation for every scenario that MUTATES the SampleApp fixture: writes a
    /// scratch file into the shared tracked directory, edits SampleApp.csproj, renames/moves
    /// files, or builds with an intentionally broken file. Mutating in place leaked across runs
    /// AND across concurrently-executing collections sharing this one app instance - e.g. a
    /// leftover ScratchBroken.cs turned the next clean-build assertion into CS0246 noise about
    /// missing types, and a search's matchCount shifted under another test's scratch files.
    /// Operate on the returned copy instead; delete <see cref="IsolatedSampleApp.WorkingRoot"/>
    /// in a finally.
    /// </summary>
    async Task<IsolatedSampleApp> OpenIsolatedSampleAppCopyAsync()
    {
        var workingRoot = Path.Combine(Path.GetTempPath(), "WorkbenchTests-" + Guid.NewGuid().ToString("N"));
        CopyFixtureDirectory(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, workingRoot);
        var solutionPath = Path.Combine(workingRoot, Path.GetFileName(_app.SolutionExplorerFixturePath));
        await _app.ReopenSolutionAsync(solutionPath);
        return new IsolatedSampleApp(workingRoot, solutionPath);
    }

    static void DeleteIsolatedSampleApp(IsolatedSampleApp app)
    {
        try { Directory.Delete(app.WorkingRoot, recursive: true); } catch { }
    }

    sealed record IsolatedSampleApp(string WorkingRoot, string SolutionPath)
    {
        public string SampleAppDirectory => Path.Combine(WorkingRoot, "SampleApp");
        public string ProjectFilePath => Path.Combine(SampleAppDirectory, "SampleApp.csproj");
        public string SharedDirectory => WorkingRoot;
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
        // The fixture ships a .slnx beside its .sln, and the app adopts an existing .slnx without
        // regenerating it, so opening the .sln lands on the .slnx.
        Assert.Equal(Path.ChangeExtension(_app.SolutionExplorerFixturePath, ".slnx"), result.GetProperty("currentSolution").GetString());
    }

    // Merged: SolutionTree_MatchesFixtureProjectStructure, OpenFile_DisplaysInAvalonEdit,
    // OpenCSharpFile_ShowsFoldingIndicators and OpenSolution_ProjectBrowserPadRendersRealNodes all open
    // the same SolutionExplorerFixturePath and only read back tree/editor/pad state - no mutation - so
    // they share a single open instead of four.
    [Fact]
    public async Task SolutionExplorerFixture_TreeFileAndProjectBrowserChecks()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);

        // --- was: SolutionTree_MatchesFixtureProjectStructure ---
        var tree = await _app.InvokeAsync("od.solution-tree");

        Assert.Equal(Path.ChangeExtension(_app.SolutionExplorerFixturePath, ".slnx"), tree.GetProperty("solutionFile").GetString());

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

        // --- was: OpenFile_DisplaysInAvalonEdit ---
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

        // --- was: OpenCSharpFile_ShowsFoldingIndicators ---
        // Folding indicators depend on a real Roslyn parse completing after the file opens
        // (CodeEditor.ParseInformationUpdated -> CodeEditorView.UpdateParseInformationForFolding ->
        // ParserFoldingStrategy.UpdateFoldings), which is async - so this polls od.file.foldings
        // instead of asserting immediately after od.open-file returns.
        var programPath = Path.Combine(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, "SampleApp", "Program.cs");

        var openProgramResult = await _app.InvokeAsync("od.open-file", programPath);
        Assert.True(openProgramResult.GetProperty("opened").GetBoolean(), $"Failed to open {programPath}");

        JsonElement foldings = default;
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            foldings = await _app.InvokeAsync("od.file.foldings", programPath);
            return foldings.GetProperty("success").GetBoolean() && foldings.GetProperty("foldingCount").GetInt32() > 0;
        }, TimeSpan.FromSeconds(30));

        Assert.True(foldings.GetProperty("success").GetBoolean());
        Assert.True(foldings.GetProperty("hasFoldingMargin").GetBoolean(),
            "The FoldingMargin (the gutter's +/- indicator strip) should be installed in the editor's left margins");
        Assert.True(foldings.GetProperty("foldingManagerInstalled").GetBoolean(),
            "A FoldingManager should be registered on the editor's TextView");
        Assert.True(foldings.GetProperty("foldingCount").GetInt32() > 0,
            "Program.cs has a namespace, a class and a method body - expected at least one foldable region");

        // --- was: OpenSolution_ProjectBrowserPadRendersRealNodes ---
        // The Project Browser pad renders its own tree in the real WPF visual tree (ProjectBrowserView.xaml's
        // HierarchicalDataTemplate -> TextBlock per node). od.solution-tree covers the backing model; this
        // locks in that opening a plain (non-git) solution actually displays the project, root file and
        // folder nodes as visible UI. Folder nodes render even when collapsed; files nested under a folder
        // (Widget.cs under Models/) are only realized once the folder is expanded, which has no DevFlow hook.
        //
        // The pad's TreeView content is only realized by AvalonDock once the pad is shown/activated
        // (same pattern as GitAddInTests).
        var showPadResult = await _app.InvokeAsync("od.show-pad", "ProjectBrowserPad");
        Assert.True(showPadResult.GetProperty("found").GetBoolean(), "Could not find the ProjectBrowser pad");

        var uiTree = await _app.GetUITreeAsync();
        var texts = FlattenElements(uiTree)
            .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
                && e.TryGetProperty("text", out var txt) && !string.IsNullOrEmpty(txt.GetString()))
            .Select(e => e.GetProperty("text").GetString())
            .ToList();

        Assert.Contains("SampleApp", texts);
        Assert.Contains("Program.cs", texts);
        Assert.Contains("Models", texts);
        Assert.Contains("Services", texts);
    }

    // Regression coverage for the incremental layout-switch semantics (doc/technotes/ilspy.md
    // "Legacy pad migration", 2026-08-09): switching layouts must open and surface the panes the
    // target layout names, but must NOT close panes the user had open. The debugger's automatic
    // switch to the "Debug" layout used to evict every pad not named in Debug.xml (ErrorList,
    // UnitTestsPad, TaskList, ...) from ToolPanes - that's the "pads vanish during debugging"
    // bug. Directly driving LayoutConfiguration.CurrentLayoutName exercises the same
    // Store/LoadConfiguration path the debugger uses, without needing a full debug session.
    [Fact]
    public async Task SwitchLayout_Debug_KeepsOpenPadsDocked()
    {
        try
        {
            await _app.InvokeAsync("od.workbench.switch-layout", "Default");
            var before = await _app.InvokeAsync("od.layout.tool-panes");
            var visibleBefore = VisibleContentIds(before).ToHashSet();
            Assert.Contains("ProjectBrowser", visibleBefore);
            Assert.Contains("OutputPad", visibleBefore);

            var switched = await _app.InvokeAsync("od.workbench.switch-layout", "Debug");
            Assert.Equal("Debug", switched.GetProperty("layoutName").GetString());

            var during = await _app.InvokeAsync("od.layout.tool-panes");
            var visibleDuring = VisibleContentIds(during).ToHashSet();

            // Every pad that was open before the switch must still be open after it - the
            // incremental contract. (The Debug layout file itself only names ProjectBrowser +
            // OutputPad; the rest must survive via the re-dock-at-PreferredDockSide path.)
            foreach (var contentId in visibleBefore)
                Assert.True(visibleDuring.Contains(contentId),
                    $"Pad '{contentId}' was evicted by the switch to the Debug layout.");

            var currentName = await _app.InvokeAsync("od.layout.current-name");
            Assert.Equal("Debug", currentName.GetProperty("layoutName").GetString());
        }
        finally
        {
            await _app.InvokeAsync("od.workbench.switch-layout", "Default");
        }
    }

    // Regression coverage for the "opening a solution must surface the Projects pad" behavior
    // (doc/technotes/ilspy.md "Legacy pad migration", 2026-08-09): WpfWorkbench subscribes
    // SD.ProjectService.SolutionOpened and BringPadToFronts the Project Browser, so a freshly
    // opened solution lands with the Projects pad front-most (selected tab) rather than buried
    // behind whatever else shares its dock strip.
    [Fact]
    public async Task OpenSolution_ActivatesProjectBrowserPad()
    {
        await _app.ReopenSolutionAsync(_app.SolutionExplorerFixturePath);

        var projectBrowser = await _app.InvokeAsync("od.layout.pane-position", "ProjectBrowser");
        Assert.True(projectBrowser.GetProperty("found").GetBoolean());
        Assert.True(projectBrowser.GetProperty("isSelected").GetBoolean(),
            "Project Browser must be the selected tab after opening a solution.");

        // Belt and braces: it must be the FIRST tab of its strip, and the strip must actually
        // have siblings (otherwise "tab 0" would be trivially true for a lone pane).
        Assert.Equal(0, projectBrowser.GetProperty("tabIndex").GetInt32());
        Assert.True(projectBrowser.GetProperty("siblingCount").GetInt32() >= 2,
            "Expected the Project Browser to share its dock strip with at least one other pad.");
    }

    static IEnumerable<string> VisibleContentIds(JsonElement toolPanes)
        => toolPanes.GetProperty("panes").EnumerateArray()
            .Where(p => p.GetProperty("IsVisible").GetBoolean())
            .Select(p => p.GetProperty("ContentId").GetString()!);

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

    // Mutating scenarios must NOT use these shared-fixture paths - see
    // OpenIsolatedSampleAppCopyAsync. Only read-only tests may touch the tracked fixture.

    [Fact]
    public async Task AddFileToProject_CreatesFileAndAppearsInSolutionTree()
    {
        var sample = await OpenIsolatedSampleAppCopyAsync();
        try
        {
            var newFilePath = Path.Combine(sample.SampleAppDirectory, "Models", "ScratchWidgetPart.txt");

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
            DeleteIsolatedSampleApp(sample);
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
        var sample = await OpenIsolatedSampleAppCopyAsync();
        try
        {
            var sharedDirectory = sample.SharedDirectory;
            Directory.CreateDirectory(sharedDirectory);
            var filePath = Path.Combine(sharedDirectory, "ScratchExplicit.txt");
            File.WriteAllText(filePath, "scratch content");
            await _app.InvokeAsync("od.solution.add-file", ProjectName, filePath, "None");

            var removeResult = await _app.InvokeAsync("od.solution.remove-file", ProjectName, filePath);
            Assert.True(removeResult.GetProperty("success").GetBoolean());

            var files = removeResult.GetProperty("files").EnumerateArray()
                .Select(f => f.GetString()!.Replace('\\', '/')).ToList();
            Assert.DoesNotContain(files, f => f.EndsWith("ScratchExplicit.txt"));

            // Removing the ProjectItem must not touch the file on disk.
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            DeleteIsolatedSampleApp(sample);
        }
    }

    // A file physically inside the project directory is always covered by the SDK's own implicit
    // item glob, independent of any explicit ProjectItem add/remove - od.solution.remove-file
    // detects this (item.IsAddedToProject == false) and reports failure honestly instead of
    // silently no-op'ing while claiming success.
    [Fact]
    public async Task RemoveGlobCoveredFile_ReportsUnsupportedInsteadOfSilentlyNoOp()
    {
        var sample = await OpenIsolatedSampleAppCopyAsync();
        try
        {
            var filePath = Path.Combine(sample.SampleAppDirectory, "Models", "ScratchToRemove.txt");
            File.WriteAllText(filePath, "scratch content");
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
            DeleteIsolatedSampleApp(sample);
        }
    }

    [Fact]
    public async Task RenameProjectFile_MovesFileAndUpdatesTree()
    {
        var sample = await OpenIsolatedSampleAppCopyAsync();
        try
        {
            var oldPath = Path.Combine(sample.SampleAppDirectory, "Models", "ScratchOldName.txt");
            var newPath = Path.Combine(sample.SampleAppDirectory, "Models", "ScratchNewName.txt");
            File.WriteAllText(oldPath, "scratch content");
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
            DeleteIsolatedSampleApp(sample);
        }
    }

    [Fact]
    public async Task AddProjectReference_AddsReferenceProjectItem()
    {
        var sample = await OpenIsolatedSampleAppCopyAsync();
        try
        {
            var addResult = await _app.InvokeAsync("od.solution.add-reference", ProjectName, "System.Xml");
            Assert.True(addResult.GetProperty("success").GetBoolean());

            var references = addResult.GetProperty("references").EnumerateArray()
                .Select(r => r.GetString()).ToList();
            Assert.Contains("System.Xml", references);

            Assert.Contains("System.Xml", File.ReadAllText(sample.ProjectFilePath));
        }
        finally
        {
            DeleteIsolatedSampleApp(sample);
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // Merged: Find_InSolution_FindsTermAcrossMultipleFiles, Find_MatchCase_RespectsCaseSensitivity,
    // Find_UseRegex_MatchesPattern and ShowResults_PopulatesSearchResultsPadUiTree all open the same
    // SolutionExplorerFixturePath and only run read-only searches - no mutation - so they share a
    // single open instead of four.
    [Fact]
    public async Task SolutionExplorerFixture_SearchChecks()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);

        // --- was: Find_InSolution_FindsTermAcrossMultipleFiles ---
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

        // --- was: Find_MatchCase_RespectsCaseSensitivity ---
        var caseInsensitive = await _app.InvokeAsync("od.search.find", "WIDGET", "solution", false, false, false);
        Assert.True(caseInsensitive.GetProperty("matchCount").GetInt32() > 0);

        var caseSensitive = await _app.InvokeAsync("od.search.find", "WIDGET", "solution", true, false, false);
        Assert.Equal(0, caseSensitive.GetProperty("matchCount").GetInt32());

        // --- was: Find_UseRegex_MatchesPattern ---
        var regexResult = await _app.InvokeAsync("od.search.find", @"Widget\w*", "solution", false, false, true);

        Assert.True(regexResult.GetProperty("success").GetBoolean());
        var regexFiles = regexResult.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("file").GetString()!.Replace('\\', '/')).ToList();
        Assert.Contains(regexFiles, f => f.EndsWith("Services/WidgetService.cs"));

        // --- was: ShowResults_PopulatesSearchResultsPadUiTree ---
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

        JsonElement tree = default;
        List<JsonElement> elements = new();
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            tree = await _app.GetUITreeAsync();
            elements = FlattenElements(tree).ToList();
            return elements.Count(e =>
                e.TryGetProperty("automationId", out var a) && a.GetString() == "SearchResultNode"
                && e.TryGetProperty("isVisible", out var v) && v.GetBoolean()) >= 2;
        }, TimeSpan.FromSeconds(30));

        Assert.True(elements.Any(e =>
            e.TryGetProperty("automationId", out var a) && a.GetString() == "SearchRootNode"
            && e.TryGetProperty("isVisible", out var v) && v.GetBoolean()),
            "Expected the Search Results pad root node to be rendered and visible");

        // The default grouping is Flat (no per-file nodes); the match rows themselves are the
        // rendered content. "Widget" matches in both Models/Widget.cs and Services/WidgetService.cs
        // (see the Find_InSolution section above), so at least two real match rows must be visible.
        Assert.True(elements.Count(e =>
            e.TryGetProperty("automationId", out var a) && a.GetString() == "SearchResultNode"
            && e.TryGetProperty("isVisible", out var v) && v.GetBoolean()) >= 2,
            "Expected at least two match nodes to be rendered and visible");
    }


    [Fact]
    public async Task Replace_InOpenFile_UpdatesEditorButNotDiskUntilSaved()
    {
        var sample = await OpenIsolatedSampleAppCopyAsync();
        var scratchPath = Path.Combine(sample.SampleAppDirectory, "ScratchReplaceTarget.cs");
        try
        {
            File.WriteAllText(scratchPath, "namespace SampleApp { class ScratchReplaceTarget { string Value = \"NeedleValue\"; } }");

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
            var persisted = await OpenDevelopAppFixture.PollUntilAsync(
                () => Task.FromResult(File.Exists(scratchPath)
                    && File.ReadAllText(scratchPath).Contains("ReplacedValue", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5), initialDelayMs: 25, maxDelayMs: 200);
            Assert.True(persisted, "The saved replacement did not reach disk within 5 seconds.");
            var savedText = File.ReadAllText(scratchPath);
            Assert.Contains("ReplacedValue", savedText);
            Assert.DoesNotContain("NeedleValue", savedText);
        }
        finally
        {
            TryDelete(scratchPath);
            DeleteIsolatedSampleApp(sample);
        }
    }

    // These dirty-flag tests only need SOME writable .cs file - no solution context at all -
    // so use a plain temp dir instead of writing scratch files into the shared fixture.
    static string NewScratchDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WorkbenchDirtyFlagTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task OpenFile_IsNotDirtyInitially()
    {
        var scratchDirectory = NewScratchDirectory();
        var path = Path.Combine(scratchDirectory, "ScratchNotDirty.cs");
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
            try { Directory.Delete(scratchDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EditFile_MarksDirty()
    {
        var scratchDirectory = NewScratchDirectory();
        var path = Path.Combine(scratchDirectory, "ScratchEditDirty.cs");
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
            // The editor still owns the dirty in-memory document. Save it before deleting the
            // scratch file so a later solution close cannot recreate the file from that buffer.
            try { await _app.InvokeAsync("od.file.save", path); } catch { }
            TryDelete(path);
            try { Directory.Delete(scratchDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SaveFile_ClearsDirtyFlagAndPersistsContent()
    {
        var scratchDirectory = NewScratchDirectory();
        var path = Path.Combine(scratchDirectory, "ScratchSave.cs");
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
            try { Directory.Delete(scratchDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SaveAllOpenFiles_SavesEveryDirtyFile()
    {
        var scratchDirectory = NewScratchDirectory();
        var pathA = Path.Combine(scratchDirectory, "ScratchSaveAllA.cs");
        var pathB = Path.Combine(scratchDirectory, "ScratchSaveAllB.cs");
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
            try { Directory.Delete(scratchDirectory, recursive: true); } catch { }
        }
    }

    async Task OpenSolutionAndFile()
    {
        await _app.EnsureSolutionOpenAsync(_app.FixtureSolutionPath);
    }

    // Merged: OpenXmlFile_AttachesXmlTreeView, OpenXmlFile_XmlTreeViewTabTitleIsNotEmpty and
    // OpenNonXmlFile_DoesNotAttachXmlTreeView all open the same FixtureSolutionPath and only read
    // back XmlTreeView state - no mutation - so they share a single open instead of three. Keeping
    // the non-XML check last is actually a *stronger* regression check than before: it now runs
    // with a real, still-open XmlTreeView left behind by the two XML opens above, which is exactly
    // the "lingers in the window" scenario its own comment describes.
    [Fact]
    public async Task FixtureSolutionPath_XmlTreeViewChecks()
    {
        await OpenSolutionAndFile();

        // --- was: OpenXmlFile_AttachesXmlTreeView ---
        var open = await _app.InvokeAsync("od.open-file", _app.XmlFixtureFilePath);
        Assert.True(open.GetProperty("opened").GetBoolean(), $"Failed to open {_app.XmlFixtureFilePath}");

        var status = await _app.InvokeAsync("od.xml-tree-status");
        Assert.True(status.GetProperty("found").GetBoolean(), status.ToString());
        Assert.Equal("ICSharpCode.XmlEditor.XmlTreeView", status.GetProperty("viewType").GetString());

        // --- was: OpenXmlFile_XmlTreeViewTabTitleIsNotEmpty ---
        var reopen = await _app.InvokeAsync("od.open-file", _app.XmlFixtureFilePath);
        Assert.True(reopen.GetProperty("opened").GetBoolean());

        var titleStatus = await _app.InvokeAsync("od.xml-tree-status");
        Assert.True(titleStatus.GetProperty("found").GetBoolean(), titleStatus.ToString());
        Assert.False(string.IsNullOrEmpty(titleStatus.GetProperty("title").GetString()));

        // --- was: OpenNonXmlFile_DoesNotAttachXmlTreeView ---
        var csFile = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(_app.FixtureSolutionPath)!, "PassTests.cs");
        var openCs = await _app.InvokeAsync("od.open-file", csFile);
        Assert.True(openCs.GetProperty("opened").GetBoolean());

        var csStatus = await _app.InvokeAsync("od.xml-tree-status");
        // If an XmlTreeView from a previously opened .xml file lingers in the window, found
        // will still be true — check that it's *not* associated with the .cs file.
        if (csStatus.GetProperty("found").GetBoolean())
        {
            var primaryFile = csStatus.GetProperty("primaryFile").GetString();
            Assert.False(primaryFile!.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),
                $"Expected XmlTreeView NOT attached to .cs file, but found primaryFile={primaryFile}");
        }
    }

    // Merged: ErrorList_OnBuildFailure_CapturesRealPerLineCompileErrors and
    // ErrorList_WithoutExplicitClear_StaleEntriesSurviveANewCleanBuild. Both are mutating (write a
    // broken scratch file, build, restore) and both need a genuine fresh od.open-solution for
    // isolation, but the second test's scenario - "build broken, then rebuild clean WITHOUT calling
    // od.error-list.clear, and confirm the stale entry survives" - is exactly what's left over from
    // the first test's broken build. So instead of paying two real reopens, this fact pays one: it
    // builds ScratchBroken.cs broken (asserting the OnBuildFailure diagnostics), then - deliberately
    // without clearing the Error List - deletes the broken file and rebuilds clean (asserting the
    // WithoutExplicitClear staleness behavior against the same still-populated Error List).
    // (ScratchBroken.cs also fills in for ScratchStale.cs, which was likewise a broken-syntax scratch
    // file whose only job was to make the same build fail.)
    [Fact]
    public async Task ErrorList_BuildFailureCapturesDiagnosticsThenStaleEntriesSurviveCleanRebuild()
    {
        // Isolated copy: this test deliberately breaks the build, and a crashed earlier run
        // leaving ScratchBroken.cs behind used to poison the NEXT run's clean-build assertions
        // (and any concurrently-executing collection's builds). Mutate the copy, not the tracked
        // fixture.
        var sample = await OpenIsolatedSampleAppCopyAsync();
        var brokenFilePath = Path.Combine(sample.SampleAppDirectory, "ScratchBroken.cs");
        try
        {
            File.WriteAllText(brokenFilePath,
                "namespace SampleApp {\n" +
                "    class ScratchBroken {\n" +
                "        void Method() { this is not valid csharp syntax at all }\n" +
                "    }\n" +
                "}\n");

            await _app.InvokeAsync("od.error-list.clear");

            // --- was: ErrorList_OnBuildFailure_CapturesRealPerLineCompileErrors ---
            // MinimalMSBuildEngine (a real `dotnet build` child process, per BuildTests.cs) parses its
            // own stdout/stderr for standard MSBuild diagnostic lines via a regex (DiagnosticLine in
            // MinimalMSBuildEngine.cs), reporting one BuildError per match with real file/line/column -
            // and only falls back to a single generic "Build failed (exit code non-zero)" entry if
            // nothing matched. That regex used to only handle the classic 2-number "(line,column):"
            // shape; this repo's SDK/Roslyn version instead emits a 4-number span shape -
            // "(line,col,endLine,endCol):" - for CS1002 and similar diagnostics, which silently fell
            // through the old regex, always hitting the generic fallback. Fixed by making the trailing
            // ",endLine,endColumn" group optional. This locks in the real per-line diagnostics now
            // reaching both od.build-solution's own BuildResults.Errors and the Error List pad
            // (TaskService, a separate code path - see UIBuildFeedbackSink.ReportError).
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

            var errorListPosition = await _app.InvokeAsync("od.layout.pane-position", "ErrorList");
            Assert.True(errorListPosition.GetProperty("found").GetBoolean(), "Error List has no live AvalonDock anchorable");
            Assert.False(errorListPosition.GetProperty("isFloating").GetBoolean(),
                "Error List fell through AvalonDock's insertion fallback into a floating window");
            Assert.Equal("Bottom", errorListPosition.GetProperty("side").GetString());

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

            // --- was: ErrorList_WithoutExplicitClear_StaleEntriesSurviveANewCleanBuild ---
            // Documents a real characteristic (not a test bug): od.build-solution calls
            // SD.BuildService.BuildAsync directly, bypassing the Build menu command's
            // TaskService.ClearExceptCommentTasks() - so unlike using the actual Build menu/toolbar
            // button, driving builds through this API can leave a previous build's errors in the
            // Error List pad even after a subsequent build of now-fixed code succeeds.
            //
            // Fix the code and build again, WITHOUT calling od.error-list.clear first.
            TryDelete(brokenFilePath);
            var secondBuildResult = await _app.InvokeAsync("od.build-solution");
            if (secondBuildResult.GetProperty("errorCount").GetInt32() != 0)
                Assert.Fail(await _app.DescribeBuildAsync(secondBuildResult));

            var afterFixedBuild = await _app.InvokeAsync("od.error-list");
            Assert.True(afterFixedBuild.GetProperty("errorCount").GetInt32() > 0,
                "Expected the stale error from the earlier broken build to still be present, since od.build-solution never clears the Error List pad on its own");
        }
        finally
        {
            DeleteIsolatedSampleApp(sample);
            // Leave the app's own error-list state clean for whichever test runs next in this
            // shared app instance, rather than letting od.build-solution's non-clearing behavior
            // leak this test's induced error into later tests.
            await _app.ReopenSolutionAsync(_app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.error-list.clear");
        }
    }

    [Fact]
    public async Task WpfCodeBehindIsNestedAndTreeScrollsVertically()
    {
        await _app.EnsureSolutionOpenAsync(_app.WpfSampleSolutionPath);

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

// The collection orderer always runs this class first against the fresh assembly fixture, before
// any test can open a solution or replace the Start Page with a document.
[Collection("00 Fresh startup")]
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
