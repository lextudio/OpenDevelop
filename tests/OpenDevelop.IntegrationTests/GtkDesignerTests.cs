using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("30 Add-ins and specialized fixtures")]
public sealed class GtkDesignerTests : IAsyncLifetime, IAsyncDisposable
{
	readonly OpenDevelopAppFixture app; readonly string workDir; readonly string projectPath; readonly string uiPath; readonly string settingsUiPath; readonly string resourceUiPath;
	public GtkDesignerTests(OpenDevelopAppFixture app)
	{
		this.app = app;
		var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(app.OpenDevelopProjectPath)!, "..", "..", ".."));
		var fixture = Path.Combine(repo, "tests", "fixtures", "GtkDesignerFixture");
		// The real path, not Path.GetTempPath()'s: on macOS that is /var/..., a symlink to /private/var.
		// MSBuild given the /var path of the project resolves its items under /private/var, sees them as
		// outside the project, and copies Windows/MainWindow.ui flat into bin/ - where the app cannot find it.
		workDir = Path.Combine(RealPath(Path.GetTempPath()), "GtkDesignerTests-" + Guid.NewGuid().ToString("N"));
		CopyDirectory(fixture, workDir); projectPath = Path.Combine(workDir, "GtkDesignerFixture.csproj"); uiPath = Path.Combine(workDir, "Windows", "MainWindow.ui"); settingsUiPath = Path.Combine(workDir, "Windows", "SettingsWindow.ui"); resourceUiPath = Path.Combine(workDir, "Windows", "ResourceWindow.ui");
	}

	public async ValueTask InitializeAsync()
	{
		var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(app.OpenDevelopProjectPath)!, "..", "..", ".."));
		var start = new System.Diagnostics.ProcessStartInfo("dotnet") {
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false,
			WorkingDirectory = workDir
		};
		start.ArgumentList.Add("restore");
		start.ArgumentList.Add(projectPath);
		start.ArgumentList.Add("--configfile");
		start.ArgumentList.Add(Path.Combine(repo, "NuGet.config"));
		start.ArgumentList.Add("--verbosity");
		start.ArgumentList.Add("quiet");
		using var process = System.Diagnostics.Process.Start(start)!;
		var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		var output = await outputTask;
		var error = await errorTask;
		Assert.True(process.ExitCode == 0, "GTK designer fixture restore failed:\n" + output + error);
	}

	[Fact]
	public async Task GtkDesigner_PreviewsGResourceUriFromSiblingManifest()
	{
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", resourceUiPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("resourceWindow");
		Assert.True(status.GetProperty("nativeFrame").GetBoolean(), "GTK resource document did not return a native frame: " + status);
		status = await WaitForCanvasFrameAsync(status);
		Assert.True(status.GetProperty("canvasFrameShown").GetBoolean(), status.GetProperty("canvasDisplay").GetString());
		Assert.Equal(2, status.GetProperty("elementCount").GetInt32());
		Assert.DoesNotContain(status.GetProperty("diagnostics").EnumerateArray(), item => (item.GetProperty("message").GetString() ?? "").Contains("resource", StringComparison.OrdinalIgnoreCase));
		var image = await app.InvokeAsync("od.gtk-designer.select", "resourceImage"); Assert.True(image.GetProperty("success").GetBoolean(), image.ToString());
	}

	[Fact]
	public async Task GtkDesigner_RealPadsPropertyEditToolboxHistoryAndSave()
	{
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var browser = await WaitForProjectBrowserAsync();
		Assert.True(browser.GetProperty("success").GetBoolean(), browser.ToString());
		var behaviorNode = FindProjectBrowserNode(browser.GetProperty("project"), "MainWindow.cs");
		Assert.True(behaviorNode.ValueKind != JsonValueKind.Undefined, browser.ToString());
		var companions = behaviorNode.GetProperty("children").EnumerateArray().Select(child => child.GetProperty("name").GetString()).ToArray();
		Assert.Contains("MainWindow.ui", companions);
		Assert.Contains("MainWindow.ui.cs", companions);
		Assert.Contains("MainWindow.css", companions);
		var sized = await app.InvokeAsync("od.window.maximize"); Assert.True(sized.GetProperty("success").GetBoolean(), sized.ToString());
		var opened = await app.InvokeAsync("od.open-file", uiPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync();
		Assert.True(status.GetProperty("active").GetBoolean(), status.ToString());
		Assert.False(status.GetProperty("canUndo").GetBoolean());
		Assert.False(status.GetProperty("canRedo").GetBoolean());
		Assert.True(status.GetProperty("hostProcessId").GetInt32() > 0, "GTK designer did not start its isolated host: " + status);
		Assert.True(status.GetProperty("nativeFrame").GetBoolean(), "GTK host did not return a native GTK frame: " + status);
		Assert.Equal("in-process GSK/Cairo", status.GetProperty("nativeRenderer").GetString());
		status = await WaitForCanvasFrameAsync(status);
		Assert.True(status.GetProperty("canvasFrameShown").GetBoolean(), "The design canvas is not displaying the GTK frame: " + status.GetProperty("canvasDisplay").GetString());
		Assert.DoesNotContain("GtkRenderHelper", status.GetProperty("hostLog").GetString() ?? "", StringComparison.Ordinal);
		Assert.DoesNotContain("gtk4-builder-tool", status.GetProperty("hostLog").GetString() ?? "", StringComparison.Ordinal);
		await AssertNoRenderChildrenAsync(status.GetProperty("hostProcessId").GetInt32());
		Assert.True(status.GetProperty("nativeFrameWidth").GetInt32() > 0 && status.GetProperty("nativeFrameHeight").GetInt32() > 0, status.ToString());
		var originalFrame = status.GetProperty("nativeFrameFingerprint").GetString(); Assert.False(string.IsNullOrEmpty(originalFrame));
		var darkTheme = await app.InvokeAsync("od.gtk-designer.theme", "Dark"); Assert.True(darkTheme.GetProperty("success").GetBoolean(), darkTheme.ToString());
		status = await WaitForFrameChangeAsync(originalFrame);
		var darkFrame = status.GetProperty("nativeFrameFingerprint").GetString(); Assert.NotEqual(originalFrame, darkFrame);
		Assert.False(status.GetProperty("canUndo").GetBoolean(), "Preview theme switching must not create a source-model undo entry.");
		var lightTheme = await app.InvokeAsync("od.gtk-designer.theme", "Light"); Assert.True(lightTheme.GetProperty("success").GetBoolean(), lightTheme.ToString());
		status = await WaitForFrameChangeAsync(darkFrame);
		Assert.NotEqual(darkFrame, status.GetProperty("nativeFrameFingerprint").GetString());
		Assert.False(status.GetProperty("canUndo").GetBoolean(), "Restoring the preview theme must not create a source-model undo entry.");
		// Application CSS is deliberately outside GtkBuilder. The preview host discovers the
		// sibling stylesheet, reads it in the child process, and a filesystem update must re-render
		// without changing the .ui document, reopening the designer, or requiring manual Refresh.
		var cssPath = Path.Combine(workDir, "Windows", "MainWindow.css");
		Assert.True(File.Exists(cssPath), "GTK fixture CSS was not copied beside its .ui document.");
		await File.WriteAllTextAsync(cssPath, ".designer-accent { background: #dc2626; color: white; }", TestContext.Current.CancellationToken);
		status = await WaitForFrameChangeAsync(originalFrame);
		Assert.DoesNotContain(status.GetProperty("diagnostics").EnumerateArray(), item => (item.GetProperty("message").GetString() ?? "").Contains("preview CSS", StringComparison.OrdinalIgnoreCase));
		var cssFrame = status.GetProperty("nativeFrameFingerprint").GetString(); Assert.NotEqual(originalFrame, cssFrame);
		File.Delete(cssPath);
		status = await WaitForFrameChangeAsync(cssFrame);
		Assert.NotEqual(cssFrame, status.GetProperty("nativeFrameFingerprint").GetString());
		// A project-wide style.css is the documented fallback when no same-stem stylesheet exists.
		var noCssFrame = status.GetProperty("nativeFrameFingerprint").GetString();
		var fallbackCssPath = Path.Combine(workDir, "Windows", "style.css");
		await File.WriteAllTextAsync(fallbackCssPath, ".designer-accent { background: #2563eb; color: white; }", TestContext.Current.CancellationToken);
		status = await WaitForFrameChangeAsync(noCssFrame);
		var fallbackCssFrame = status.GetProperty("nativeFrameFingerprint").GetString();
		Assert.NotEqual(noCssFrame, fallbackCssFrame);
		File.Delete(fallbackCssPath);
		status = await WaitForFrameChangeAsync(fallbackCssFrame);
		Assert.NotEqual(fallbackCssFrame, status.GetProperty("nativeFrameFingerprint").GetString());
		// Editors often save a stylesheet by moving a temporary file into place instead of writing
		// the destination. Verify both legs of that Rename event: an incoming stylesheet is applied
		// without reopening the .ui, and moving it away removes the provider again.
		noCssFrame = status.GetProperty("nativeFrameFingerprint").GetString();
		var incomingCssPath = Path.Combine(workDir, "Windows", "MainWindow.incoming.css");
		await File.WriteAllTextAsync(incomingCssPath, ".designer-accent { background: #7c3aed; color: white; }", TestContext.Current.CancellationToken);
		File.Move(incomingCssPath, cssPath);
		status = await WaitForFrameChangeAsync(noCssFrame);
		var renamedCssFrame = status.GetProperty("nativeFrameFingerprint").GetString();
		Assert.NotEqual(noCssFrame, renamedCssFrame);
		File.Move(cssPath, incomingCssPath);
		status = await WaitForFrameChangeAsync(renamedCssFrame);
		Assert.NotEqual(renamedCssFrame, status.GetProperty("nativeFrameFingerprint").GetString());
		File.Delete(incomingCssPath);
		Assert.Equal(status.GetProperty("elementCount").GetInt32(), status.GetProperty("nativeBoundsCount").GetInt32());
		var runBounds = await app.InvokeAsync("od.gtk-designer.bounds", "runButton"); Assert.True(runBounds.GetProperty("success").GetBoolean(), runBounds.ToString());
		var nativeHit = await app.InvokeAsync("od.gtk-designer.hit-test", runBounds.GetProperty("x").GetDouble() + runBounds.GetProperty("width").GetDouble() / 2, runBounds.GetProperty("y").GetDouble() + runBounds.GetProperty("height").GetDouble() / 2);
		Assert.True(nativeHit.GetProperty("success").GetBoolean(), nativeHit.ToString()); Assert.Equal("runButton", nativeHit.GetProperty("selectedId").GetString());
			Assert.True(status.GetProperty("toolboxHosted").GetBoolean(), "The real Tools pad did not host the GTK toolbox: " + status);
			Assert.True(status.GetProperty("toolboxSearchHosted").GetBoolean(), "The visible Toolbox search UI was not mounted: " + status);
		Assert.True(status.GetProperty("outlineHosted").GetBoolean(), "The real Outline pad did not host the GTK tree: " + status);
		Assert.Equal(status.GetProperty("elementCount").GetInt32(), status.GetProperty("outlineItemCount").GetInt32());
		Assert.True(status.GetProperty("toolboxItemCount").GetInt32() >= 15, status.ToString());
		var filteredTools = await app.InvokeAsync("od.gtk-designer.toolbox.filter", "switch"); Assert.Equal(1, filteredTools.GetProperty("itemCount").GetInt32());
		var allTools = await app.InvokeAsync("od.gtk-designer.toolbox.filter", ""); Assert.True(allTools.GetProperty("itemCount").GetInt32() >= 15, allTools.ToString());
		Assert.Equal(5, status.GetProperty("toolbarItemCount").GetInt32());
		Assert.Equal(new[] { "Zoom", "Fit", "Gridlines", "Theme", "Show Names" }, status.GetProperty("toolbarItems").EnumerateArray().Select(x => x.GetString()).ToArray());
		var zoomed = await app.InvokeAsync("od.gtk-designer.zoom", 1.5); Assert.Equal(1.5, zoomed.GetProperty("zoom").GetDouble());
		var fitted = await app.InvokeAsync("od.gtk-designer.fit"); Assert.True(fitted.GetProperty("measured").GetBoolean(), fitted.ToString()); // The fit scale is the viewport divided by the frame, so it moves whenever the frame does -
		// adding the host-composed title bar made it 630 tall instead of 600 and took the scale just
		// under a hard .25. Assert what fit means (a sensible scale that shows the design), not a bound
		// that the frame height can push it past.
		var fitZoom = fitted.GetProperty("zoom").GetDouble();
		Assert.True(fitZoom is >= .1 and <= 2, $"Fit zoom {fitZoom} is outside 0.1..2; maximize reported {sized}; fit reported {fitted}");
		// The shared DesignerCanvas toolbar's Fit button/action bypasses the Zoom combo entirely -
		// without syncing the combo back, it kept showing the last manually-picked percentage while
		// the canvas visibly rendered at Fit scale (observed live: combo stuck on "100%").
		status = await app.InvokeAsync("od.gtk-designer.status"); Assert.Equal(0, status.GetProperty("zoomComboSelectedIndex").GetInt32());
		var gridOn = await app.InvokeAsync("od.gtk-designer.gridlines", true); Assert.True(gridOn.GetProperty("gridlines").GetBoolean());
		status = await app.InvokeAsync("od.gtk-designer.status"); Assert.True(status.GetProperty("gridlines").GetBoolean());
		var gridOff = await app.InvokeAsync("od.gtk-designer.gridlines", false); Assert.False(gridOff.GetProperty("gridlines").GetBoolean());

		var selected = await app.InvokeAsync("od.gtk-designer.select", "runButton"); Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
		status = await WaitForPropertyAdapterAsync();
		Assert.Contains("GtkPropertyAdapter", status.GetProperty("propertyPadSelectedType").GetString());
		var multi = await app.InvokeAsync("od.gtk-designer.multi-select", "runButton,heading"); Assert.True(multi.GetProperty("success").GetBoolean(), multi.ToString());
		Assert.Equal(new[] { "runButton", "heading" }, multi.GetProperty("selectedIds").EnumerateArray().Select(item => item.GetString()).ToArray());
		status = await app.InvokeAsync("od.gtk-designer.status"); Assert.Equal(2, status.GetProperty("selectedIds").GetArrayLength()); Assert.True(status.GetProperty("propertyPadPropertyCount").GetInt32() > 0, status.ToString());
		var batchEdit = await app.InvokeAsync("od.gtk-designer.properties.edit", "Label", "Shared heading"); Assert.True(batchEdit.GetProperty("success").GetBoolean(), batchEdit.ToString());
		await app.InvokeAsync("od.gtk-designer.select", "runButton");
		status = await app.InvokeAsync("od.gtk-designer.status"); Assert.True(status.GetProperty("propertyPadPropertyCount").GetInt32() > 0, status.ToString());
		var edited = await app.InvokeAsync("od.gtk-designer.properties.edit", "Label", "Execute"); Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
		status = await WaitForFrameChangeAsync(originalFrame); Assert.NotEqual(originalFrame, status.GetProperty("nativeFrameFingerprint").GetString());
		var signal = await app.InvokeAsync("od.gtk-designer.properties.event.bind", "clicked"); Assert.True(signal.GetProperty("success").GetBoolean(), signal.ToString());
		var reordered = await app.InvokeAsync("od.gtk-designer.pointer-reorder", "runButton", "heading"); Assert.True(reordered.GetProperty("success").GetBoolean(), reordered.ToString());
		var darkBeforeRestart = await app.InvokeAsync("od.gtk-designer.theme", "Dark"); Assert.True(darkBeforeRestart.GetProperty("success").GetBoolean(), darkBeforeRestart.ToString());
		var restarted = await app.InvokeAsync("od.gtk-designer.restart-host");
		Assert.True(restarted.GetProperty("success").GetBoolean(), restarted.ToString());
		Assert.NotEqual(restarted.GetProperty("oldHostProcessId").GetInt32(), restarted.GetProperty("hostProcessId").GetInt32());
		status = await app.InvokeAsync("od.gtk-designer.status"); Assert.Equal("Dark", status.GetProperty("previewTheme").GetString()); Assert.Equal("Dark", status.GetProperty("canvasTheme").GetString());
		var refreshed = await app.InvokeAsync("od.gtk-designer.refresh"); Assert.True(refreshed.GetProperty("success").GetBoolean(), refreshed.ToString());

		await app.InvokeAsync("od.gtk-designer.select", "contentBox");
		var inserted = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkEntry"); Assert.True(inserted.GetProperty("success").GetBoolean(), inserted.ToString());
		Assert.Equal("entry1", inserted.GetProperty("selectedId").GetString());
		status = await app.InvokeAsync("od.gtk-designer.status"); Assert.True(status.GetProperty("canUndo").GetBoolean(), status.ToString()); Assert.False(status.GetProperty("canRedo").GetBoolean(), status.ToString());
		// A CSS-only refresh must not reopen the source model and silently clear this history.
		var historyFrame = status.GetProperty("nativeFrameFingerprint").GetString();
		await File.WriteAllTextAsync(cssPath, ".designer-accent { background: #0f766e; color: white; }", TestContext.Current.CancellationToken);
		status = await WaitForFrameChangeAsync(historyFrame);
		Assert.True(status.GetProperty("canUndo").GetBoolean(), "A CSS preview refresh must preserve source-model undo history.");
		File.Delete(cssPath);
		status = await WaitForFrameChangeAsync(status.GetProperty("nativeFrameFingerprint").GetString());
		Assert.True(status.GetProperty("canUndo").GetBoolean(), "Removing preview CSS must preserve source-model undo history.");
		var refreshPreview = await app.InvokeAsync("od.gtk-designer.refresh");
		Assert.True(refreshPreview.GetProperty("success").GetBoolean(), refreshPreview.ToString());
		status = await app.InvokeAsync("od.gtk-designer.status");
		Assert.True(status.GetProperty("canUndo").GetBoolean(), "Manual preview refresh must preserve source-model undo history.");
		var undo = await app.InvokeAsync("od.gtk-designer.undo"); Assert.Equal(4, undo.GetProperty("elementCount").GetInt32());
		Assert.True(undo.GetProperty("canRedo").GetBoolean());
		var redo = await app.InvokeAsync("od.gtk-designer.redo"); Assert.Equal(5, redo.GetProperty("elementCount").GetInt32());
		Assert.False(redo.GetProperty("canRedo").GetBoolean());
		var deleted = await app.InvokeAsync("od.gtk-designer.delete"); Assert.True(deleted.GetProperty("success").GetBoolean(), deleted.ToString()); Assert.Equal(4, deleted.GetProperty("elementCount").GetInt32());
		var undoDelete = await app.InvokeAsync("od.gtk-designer.undo"); Assert.Equal(5, undoDelete.GetProperty("elementCount").GetInt32());
		var redoDelete = await app.InvokeAsync("od.gtk-designer.redo"); Assert.Equal(4, redoDelete.GetProperty("elementCount").GetInt32());
		var restoreDeleted = await app.InvokeAsync("od.gtk-designer.undo"); Assert.Equal(5, restoreDeleted.GetProperty("elementCount").GetInt32());
		// Duplicate and copy/paste: the copy gets a unique id, becomes the selection, and undoes as one step.
		await app.InvokeAsync("od.gtk-designer.select", "runButton");
		var duplicated = await app.InvokeAsync("od.gtk-designer.clipboard", "duplicate"); Assert.True(duplicated.GetProperty("success").GetBoolean(), duplicated.ToString());
		Assert.Equal(6, duplicated.GetProperty("elementCount").GetInt32()); Assert.Equal(new[] { "runButton1" }, duplicated.GetProperty("selectedIds").EnumerateArray().Select(item => item.GetString()).ToArray());
		var undoDuplicate = await app.InvokeAsync("od.gtk-designer.undo"); Assert.Equal(5, undoDuplicate.GetProperty("elementCount").GetInt32());
		await app.InvokeAsync("od.gtk-designer.select", "runButton");
		var copied = await app.InvokeAsync("od.gtk-designer.clipboard", "copy"); Assert.True(copied.GetProperty("success").GetBoolean(), copied.ToString());
		await app.InvokeAsync("od.gtk-designer.select", "contentBox");
		var pasted = await app.InvokeAsync("od.gtk-designer.clipboard", "paste"); Assert.True(pasted.GetProperty("success").GetBoolean(), pasted.ToString());
		Assert.Equal(6, pasted.GetProperty("elementCount").GetInt32()); Assert.Equal(new[] { "runButton1" }, pasted.GetProperty("selectedIds").EnumerateArray().Select(item => item.GetString()).ToArray());
		var undoPaste = await app.InvokeAsync("od.gtk-designer.undo"); Assert.Equal(5, undoPaste.GetProperty("elementCount").GetInt32());
		// Align sets halign on the selection as one undo step; an invalid command does nothing.
		await app.InvokeAsync("od.gtk-designer.select", "runButton");
		var aligned = await app.InvokeAsync("od.gtk-designer.clipboard", "align-h-center"); Assert.True(aligned.GetProperty("success").GetBoolean(), aligned.ToString());
		Assert.False((await app.InvokeAsync("od.gtk-designer.clipboard", "align-h-bogus")).GetProperty("success").GetBoolean());
		var alignedStatus = await app.InvokeAsync("od.gtk-designer.status"); Assert.True(alignedStatus.GetProperty("canUndo").GetBoolean());
		var undoAlign = await app.InvokeAsync("od.gtk-designer.undo"); Assert.Equal(5, undoAlign.GetProperty("elementCount").GetInt32());
		// Move First/Last keep the element selected; the first child cannot move earlier.
		await app.InvokeAsync("od.gtk-designer.select", "heading");
		var movedFirst = await app.InvokeAsync("od.gtk-designer.clipboard", "move-first"); Assert.True(movedFirst.GetProperty("success").GetBoolean(), movedFirst.ToString());
		Assert.Equal(new[] { "heading" }, movedFirst.GetProperty("selectedIds").EnumerateArray().Select(item => item.GetString()).ToArray());
		Assert.False((await app.InvokeAsync("od.gtk-designer.clipboard", "move-earlier")).GetProperty("success").GetBoolean());
		var movedLast = await app.InvokeAsync("od.gtk-designer.clipboard", "move-last"); Assert.True(movedLast.GetProperty("success").GetBoolean(), movedLast.ToString());
		Assert.False((await app.InvokeAsync("od.gtk-designer.clipboard", "move-later")).GetProperty("success").GetBoolean());
		await app.InvokeAsync("od.gtk-designer.undo"); await app.InvokeAsync("od.gtk-designer.undo");
		var saved = await app.InvokeAsync("od.file.save", uiPath); Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
		var xml = await File.ReadAllTextAsync(uiPath, TestContext.Current.CancellationToken);
		Assert.Contains(">Execute</property>", xml); Assert.Contains(">Shared heading</property>", xml); Assert.Contains("<signal name=\"clicked\" handler=\"runButton_clicked\"", xml); Assert.Contains("class=\"GtkEntry\"", xml); Assert.Contains("id=\"entry1\"", xml);
		Assert.True(xml.IndexOf("id=\"runButton\"", StringComparison.Ordinal) < xml.IndexOf("id=\"heading\"", StringComparison.Ordinal), "GTK reorder was not persisted: " + xml);
		await ValidateGtkBuilderAsync(uiPath);

		var closed = await app.InvokeAsync("od.close-active-view"); Assert.True(closed.GetProperty("success").GetBoolean(), closed.ToString());
		var reopened = await app.InvokeAsync("od.open-file", uiPath); Assert.True(reopened.GetProperty("opened").GetBoolean(), reopened.ToString());
		var reopenedStatus = await WaitAsync(); Assert.Equal(5, reopenedStatus.GetProperty("elementCount").GetInt32()); Assert.True(reopenedStatus.GetProperty("nativeFrame").GetBoolean(), reopenedStatus.ToString());
		var reselected = await app.InvokeAsync("od.gtk-designer.select", "entry1"); Assert.True(reselected.GetProperty("success").GetBoolean(), reselected.ToString()); Assert.Contains("GtkPropertyAdapter", reselected.GetProperty("propertyPadSelectedType").GetString());

		var mainHostProcessId = reopenedStatus.GetProperty("hostProcessId").GetInt32();
		var mainDocumentId = reopenedStatus.GetProperty("hostDocumentId").GetString(); Assert.False(string.IsNullOrEmpty(mainDocumentId));
		var openedSettings = await app.InvokeAsync("od.open-file", settingsUiPath); Assert.True(openedSettings.GetProperty("opened").GetBoolean(), openedSettings.ToString());
		var settingsStatus = await WaitAsync("settingsWindow"); Assert.True(mainHostProcessId == settingsStatus.GetProperty("hostProcessId").GetInt32(), $"expected host {mainHostProcessId}: {settingsStatus}"); Assert.Equal(4, settingsStatus.GetProperty("elementCount").GetInt32());
		Assert.NotEqual(mainDocumentId, settingsStatus.GetProperty("hostDocumentId").GetString()); Assert.Equal(2, settingsStatus.GetProperty("activeHostLeases").GetInt32());
		var selectedSettings = await app.InvokeAsync("od.gtk-designer.select", "settingsHeading"); Assert.True(selectedSettings.GetProperty("success").GetBoolean(), selectedSettings.ToString()); Assert.Contains("GtkPropertyAdapter", selectedSettings.GetProperty("propertyPadSelectedType").GetString());
		var editedSettings = await app.InvokeAsync("od.gtk-designer.properties.edit", "Label", "Advanced Preferences"); Assert.True(editedSettings.GetProperty("success").GetBoolean(), editedSettings.ToString());
		var terminated = await app.InvokeAsync("od.gtk-designer.terminate-host"); Assert.True(terminated.GetProperty("success").GetBoolean(), terminated.ToString());
		settingsStatus = await WaitForHostChangeAsync(mainHostProcessId, "settingsWindow"); Assert.True(settingsStatus.GetProperty("hostRecoveryCount").GetInt32() > 0, settingsStatus.ToString());
		var recoveredHostProcessId = settingsStatus.GetProperty("hostProcessId").GetInt32(); Assert.NotEqual(mainHostProcessId, recoveredHostProcessId);
		var savedSettings = await app.InvokeAsync("od.file.save", settingsUiPath); Assert.True(savedSettings.GetProperty("success").GetBoolean(), savedSettings.ToString());
		Assert.Contains("Advanced Preferences", await File.ReadAllTextAsync(settingsUiPath, TestContext.Current.CancellationToken));
		Assert.DoesNotContain("Advanced Preferences", await File.ReadAllTextAsync(uiPath, TestContext.Current.CancellationToken));
		var closedSettings = await app.InvokeAsync("od.close-active-view"); Assert.True(closedSettings.GetProperty("success").GetBoolean(), closedSettings.ToString());
		var reactivateMain = await app.InvokeAsync("od.open-file", uiPath); Assert.True(reactivateMain.GetProperty("opened").GetBoolean(), reactivateMain.ToString());
		var mainAgain = await WaitAsync("mainWindow"); Assert.Equal(recoveredHostProcessId, mainAgain.GetProperty("hostProcessId").GetInt32()); Assert.True(mainAgain.GetProperty("hostRecoveryCount").GetInt32() > 0, mainAgain.ToString()); Assert.Equal(5, mainAgain.GetProperty("elementCount").GetInt32());
			var mainSelectionAgain = await app.InvokeAsync("od.gtk-designer.select", "entry1"); Assert.True(mainSelectionAgain.GetProperty("success").GetBoolean(), mainSelectionAgain + " status=" + mainAgain);

		// Binding `clicked` created a typed handler in the user's behavior class and connected it in
		// the designer-owned companion: GtkBuilder itself cannot resolve the <signal> (it aborts).
		var behavior = await File.ReadAllTextAsync(Path.Combine(workDir, "Windows", "MainWindow.cs"), TestContext.Current.CancellationToken);
		Assert.Contains("void runButton_clicked(Gtk.Button sender, System.EventArgs args)", behavior);
		var companion = await File.ReadAllTextAsync(Path.Combine(workDir, "Windows", "MainWindow.ui.cs"), TestContext.Current.CancellationToken);
		Assert.Contains(".OnClicked += (_, args) => runButton_clicked(", companion);
		await ValidateFixtureBuildAsync(projectPath);
		// The mutated app must actually start: before the generated wiring, a bound signal made
		// GtkBuilder abort with "No function named runButton_clicked".
		await RunFixtureSmokeTestAsync(projectPath);
	}

	[Fact]
	public async Task GtkDesigner_PropertiesPad_IsGirTyped_ValidatesEdits_AndResets()
	{
		// The Properties pad is driven by the installed GTK introspection data, not a hand-written
		// list: every writable property of the class and its ancestors/interfaces, typed (enum
		// drop-down, bool, integer, double), grouped by declaring class, validated before it is
		// written, translatable when new user-visible text, and resettable to GTK's default.
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", uiPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync(); Assert.True(status.GetProperty("active").GetBoolean(), status.ToString());
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "runButton")).GetProperty("success").GetBoolean());

		async Task<Dictionary<string, JsonElement>> Describe()
		{
			var described = await app.InvokeAsync("od.gtk-designer.properties.describe");
			Assert.True(described.GetProperty("success").GetBoolean(), described.ToString());
			return described.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!, i => i);
		}
		var items = await Describe();
		Assert.True(items.Count > 30, "expected the GIR property set, got " + items.Count + ": " + string.Join(", ", items.Keys));
		// Everyday properties lead in "Common" (the curated groups); the rest stay under their class.
		Assert.Equal("Common", items["Label"].GetProperty("category").GetString());
		Assert.Equal("GtkWidget", items["Opacity"].GetProperty("category").GetString());
		var halign = items["Halign"];
		Assert.Equal("Common", halign.GetProperty("category").GetString());
		Assert.True(halign.GetProperty("exclusive").GetBoolean(), halign.ToString());
		Assert.Contains("center", halign.GetProperty("choices").EnumerateArray().Select(c => c.GetString()));
		Assert.Equal("fill", halign.GetProperty("value").GetString());   // GTK's default, not written in the file
		Assert.False(halign.GetProperty("canReset").GetBoolean());
		Assert.Equal("Boolean", items["Sensitive"].GetProperty("type").GetString());
		Assert.Equal("Int64", items["MarginStart"].GetProperty("type").GetString());
		Assert.Equal("Double", items["Opacity"].GetProperty("type").GetString());
		// GTK's own limits (GParamSpec - not in GIR) are shown and enforced.
		Assert.Contains("Range: 0 to 32767.", items["MarginStart"].GetProperty("description").GetString());

		var edited = await app.InvokeAsync("od.gtk-designer.properties.edit", "Halign", "center"); Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
		// Not an enum member: the host rejects it before the document changes.
		try { await app.InvokeAsync("od.gtk-designer.properties.edit", "Halign", "middle"); } catch (InvalidOperationException) { }
		try { await app.InvokeAsync("od.gtk-designer.properties.edit", "MarginStart", "40000"); } catch (InvalidOperationException) { }
		var tooltip = await app.InvokeAsync("od.gtk-designer.properties.edit", "TooltipText", "Runs the task"); Assert.True(tooltip.GetProperty("success").GetBoolean(), tooltip.ToString());
		Assert.True((await app.InvokeAsync("od.file.save", uiPath)).GetProperty("success").GetBoolean());
		var xml = await File.ReadAllTextAsync(uiPath, TestContext.Current.CancellationToken);
		Assert.Contains("<property name=\"halign\">center</property>", xml);
		Assert.DoesNotContain("middle", xml);
		Assert.DoesNotContain("40000", xml);
		Assert.Contains("<property name=\"tooltip-text\" translatable=\"yes\">Runs the task</property>", xml);
		await ValidateGtkBuilderAsync(uiPath);

		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "runButton")).GetProperty("success").GetBoolean());
		var reset = await app.InvokeAsync("od.gtk-designer.properties.reset", "Halign"); Assert.True(reset.GetProperty("success").GetBoolean(), reset.ToString());
		Assert.True((await app.InvokeAsync("od.file.save", uiPath)).GetProperty("success").GetBoolean());
		Assert.DoesNotContain("name=\"halign\"", await File.ReadAllTextAsync(uiPath, TestContext.Current.CancellationToken));
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "runButton")).GetProperty("success").GetBoolean());
		var afterReset = (await Describe())["Halign"];
		Assert.Equal("fill", afterReset.GetProperty("value").GetString());
		Assert.False(afterReset.GetProperty("canReset").GetBoolean());

		// Conditional enabling (Stetic's disabled-if): a label's wrap-mode only applies while wrap is on.
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "heading")).GetProperty("success").GetBoolean());
		var wrapMode = (await Describe())["WrapMode"];
		Assert.True(wrapMode.GetProperty("readOnly").GetBoolean(), wrapMode.ToString());
		Assert.Contains("Applies when wrap is True.", wrapMode.GetProperty("description").GetString());
		Assert.True((await app.InvokeAsync("od.gtk-designer.properties.edit", "Wrap", "True")).GetProperty("success").GetBoolean());
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "heading")).GetProperty("success").GetBoolean());
		Assert.False((await Describe())["WrapMode"].GetProperty("readOnly").GetBoolean());
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_GridChild_LayoutProperties_AreEditable_AndGtkAppliesThem()
	{
		// GTK 4 child properties live in <layout> and belong to the PARENT's layout manager. A
		// GtkGrid child must offer column/row/spans (typed from GtkGridLayoutChild), the edit must
		// land in <layout>, and real GTK must lay the child out accordingly - checked against the
		// host's natively measured bounds, not just the XML.
		var gridPath = Path.Combine(workDir, "Windows", "GridWindow.ui");
		await File.WriteAllTextAsync(gridPath, """
			<?xml version="1.0" encoding="UTF-8"?>
			<interface>
			  <requires lib="gtk" version="4.0" />
			  <object class="GtkWindow" id="gridWindow">
			    <property name="default-width">400</property>
			    <property name="default-height">200</property>
			    <child>
			      <object class="GtkGrid" id="grid">
			        <property name="column-spacing">8</property>
			        <child>
			          <object class="GtkLabel" id="first">
			            <property name="label">First</property>
			            <layout><property name="column">0</property><property name="row">0</property></layout>
			          </object>
			        </child>
			        <child>
			          <object class="GtkLabel" id="second">
			            <property name="label">Second</property>
			            <layout><property name="column">0</property><property name="row">1</property></layout>
			          </object>
			        </child>
			      </object>
			    </child>
			  </object>
			</interface>
			""", TestContext.Current.CancellationToken);
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", gridPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("gridWindow"); Assert.True(status.GetProperty("active").GetBoolean(), status.ToString());

		async Task<(double X, double Y)> Bounds(string id)
		{
			var b = await app.InvokeAsync("od.gtk-designer.bounds", id); Assert.True(b.GetProperty("success").GetBoolean(), id + ": " + b);
			return (b.GetProperty("x").GetDouble(), b.GetProperty("y").GetDouble());
		}
		var first = await Bounds("first"); var second = await Bounds("second");
		Assert.True(second.Y > first.Y, $"row 1 must be below row 0: {first} {second}");

		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "second")).GetProperty("success").GetBoolean());
		var described = await app.InvokeAsync("od.gtk-designer.properties.describe");
		var items = described.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!, i => i);
		foreach (var name in new[] { "LayoutColumn", "LayoutRow", "LayoutColumnSpan", "LayoutRowSpan" })
			Assert.True(items.ContainsKey(name), name + " missing: " + string.Join(", ", items.Keys.Where(k => k.StartsWith("Layout", StringComparison.Ordinal))));
		Assert.Equal("Layout (GtkGrid)", items["LayoutColumn"].GetProperty("category").GetString());
		Assert.Equal("Int64", items["LayoutColumn"].GetProperty("type").GetString());
		Assert.Equal("1", items["LayoutRow"].GetProperty("value").GetString());

		// Move the second label to column 1, row 0: beside the first instead of below it.
		Assert.True((await app.InvokeAsync("od.gtk-designer.properties.edit", "LayoutColumn", "1")).GetProperty("success").GetBoolean());
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "second")).GetProperty("success").GetBoolean());
		Assert.True((await app.InvokeAsync("od.gtk-designer.properties.edit", "LayoutRow", "0")).GetProperty("success").GetBoolean());
		JsonElement moved = default;
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			moved = await app.InvokeAsync("od.gtk-designer.bounds", "second");
			return moved.GetProperty("x").GetDouble() > first.X && Math.Abs(moved.GetProperty("y").GetDouble() - first.Y) < 1;
		}, TimeSpan.FromSeconds(10)), "GTK did not lay the child out in column 1, row 0: " + moved);

		Assert.True((await app.InvokeAsync("od.file.save", gridPath)).GetProperty("success").GetBoolean());
		var xml = (await File.ReadAllTextAsync(gridPath, TestContext.Current.CancellationToken)).Replace("\r", "");
		Assert.Matches(@"id=""second"">[\s\S]*<layout>[\s\S]*<property name=""column"">1</property>[\s\S]*<property name=""row"">0</property>", xml);
		await ValidateGtkBuilderAsync(gridPath);

		// A toolbox drop is placed by the grid's child policy: below "second" (now at column 1,
		// row 0) is free column 1, row 1 - the drop planner picks it from GTK's measured bounds, and
		// GTK lays the new button out there.
		var secondNow = await Bounds("second");
		var below = await app.InvokeAsync("od.gtk-designer.bounds", "second");
		var dropX = secondNow.X + below.GetProperty("width").GetDouble() / 2;
		var dropY = secondNow.Y + below.GetProperty("height").GetDouble() + 20;
		var plan = await app.InvokeAsync("od.gtk-designer.drop-plan", dropX, dropY);
		Assert.True(plan.GetProperty("success").GetBoolean(), plan.ToString());
		Assert.Equal("grid", plan.GetProperty("containerId").GetString());
		Assert.Equal(1, plan.GetProperty("column").GetInt32()); Assert.Equal(1, plan.GetProperty("row").GetInt32());
		var dropped = await app.InvokeAsync("od.gtk-designer.toolbox.drop-at", "GtkButton", dropX, dropY);
		Assert.True(dropped.GetProperty("success").GetBoolean(), dropped.ToString());
		var newId = dropped.GetProperty("selectedId").GetString()!;
		JsonElement placed = default;
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			placed = await app.InvokeAsync("od.gtk-designer.bounds", newId);
			return placed.GetProperty("success").GetBoolean() && Math.Abs(placed.GetProperty("x").GetDouble() - secondNow.X) < 1 && placed.GetProperty("y").GetDouble() > secondNow.Y;
		}, TimeSpan.FromSeconds(10)), "the dropped button is not under 'second' in column 1: " + placed);

		// Reset returns the child to GtkGrid's default column (0).
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "second")).GetProperty("success").GetBoolean());
		Assert.True((await app.InvokeAsync("od.gtk-designer.properties.reset", "LayoutColumn")).GetProperty("success").GetBoolean());
		Assert.True((await app.InvokeAsync("od.file.save", gridPath)).GetProperty("success").GetBoolean());
		Assert.DoesNotMatch(@"id=""second"">((?!</object>)[\s\S])*name=""column""", (await File.ReadAllTextAsync(gridPath, TestContext.Current.CancellationToken)).Replace("\r", ""));

		// Grid context commands: span, insert and delete row each edit the document as one undo step.
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "first")).GetProperty("success").GetBoolean());
		var countBefore = (await app.InvokeAsync("od.gtk-designer.status")).GetProperty("elementCount").GetInt32();
		Assert.True((await app.InvokeAsync("od.gtk-designer.clipboard", "span-column-more")).GetProperty("success").GetBoolean());
		Assert.False((await app.InvokeAsync("od.gtk-designer.clipboard", "span-row-less")).GetProperty("success").GetBoolean());
		Assert.True((await app.InvokeAsync("od.file.save", gridPath)).GetProperty("success").GetBoolean());
		Assert.Matches(@"id=""first"">[\s\S]*?name=""column-span"">2</property>", (await File.ReadAllTextAsync(gridPath, TestContext.Current.CancellationToken)).Replace("\r", ""));
		await app.InvokeAsync("od.gtk-designer.undo");
		Assert.True((await app.InvokeAsync("od.gtk-designer.clipboard", "grid-insert-row")).GetProperty("success").GetBoolean());
		Assert.True((await app.InvokeAsync("od.file.save", gridPath)).GetProperty("success").GetBoolean());
		Assert.Matches(@"id=""first"">[\s\S]*?name=""row"">1</property>", (await File.ReadAllTextAsync(gridPath, TestContext.Current.CancellationToken)).Replace("\r", ""));
		var deleted = await app.InvokeAsync("od.gtk-designer.clipboard", "grid-delete-row"); Assert.True(deleted.GetProperty("success").GetBoolean(), deleted.ToString());
		Assert.True(deleted.GetProperty("elementCount").GetInt32() < countBefore, deleted.ToString());
		await app.InvokeAsync("od.gtk-designer.undo");
		Assert.Equal(countBefore, (await app.InvokeAsync("od.gtk-designer.status")).GetProperty("elementCount").GetInt32());
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_StackPages_UseGtkStackPageObjects_AndTheNotebookUsesAPlainChild()
	{
		// A GTK 4 stack page is an explicit GtkStackPage object holding the widget as its "child"
		// property: GtkStack's GtkBuilder form takes no child properties, so a bare <child> would be
		// an unnamed, untitled page. GtkNotebook is the opposite: its bare <child> IS the page and GTK
		// supplies the "Page N" tab. A drop must write each container's own shape, and the designer
		// must select the widget the user asked for rather than the GtkStackPage wrapper.
		var stackPath = Path.Combine(workDir, "Windows", "StackWindow.ui");
		await File.WriteAllTextAsync(stackPath, """
			<?xml version="1.0" encoding="UTF-8"?>
			<interface>
			  <requires lib="gtk" version="4.0" />
			  <object class="GtkWindow" id="stackWindow">
			    <property name="default-width">400</property>
			    <property name="default-height">300</property>
			    <child>
			      <object class="GtkStack" id="stack" />
			    </child>
			  </object>
			</interface>
			""", TestContext.Current.CancellationToken);
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", stackPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("stackWindow"); Assert.True(status.GetProperty("nativeFrame").GetBoolean(), status.ToString());

		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "stack")).GetProperty("success").GetBoolean());
		var first = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkButton");
		Assert.True(first.GetProperty("success").GetBoolean(), first.ToString());
		Assert.Equal("button1", first.GetProperty("selectedId").GetString());
		var firstBounds = await app.InvokeAsync("od.gtk-designer.bounds", "button1");
		// The insert response carries the previous layout pass's bounds; the native render that
		// measures the new page arrives asynchronously.
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			firstBounds = await app.InvokeAsync("od.gtk-designer.bounds", "button1");
			return firstBounds.GetProperty("success").GetBoolean() && firstBounds.GetProperty("width").GetDouble() > 0 && firstBounds.GetProperty("height").GetDouble() > 0;
		}, TimeSpan.FromSeconds(10)), "the stack page content was not rendered: " + firstBounds);
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "stack")).GetProperty("success").GetBoolean());
		var second = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkLabel");
		Assert.True(second.GetProperty("success").GetBoolean(), second.ToString());
		Assert.Equal("label1", second.GetProperty("selectedId").GetString());
		Assert.True((await app.InvokeAsync("od.file.save", stackPath)).GetProperty("success").GetBoolean());
		var stackText = (await File.ReadAllTextAsync(stackPath, TestContext.Current.CancellationToken)).Replace("\r", "");
		var stackXml = System.Xml.Linq.XDocument.Parse(stackText);
		var stack = stackXml.Descendants("object").Single(x => (string?)x.Attribute("id") == "stack");
		var pages = stack.Elements("child").Select(c => c.Element("object")!).ToList();
		Assert.Equal(new[] { "GtkStackPage", "GtkStackPage" }, pages.Select(p => (string?)p.Attribute("class")));
		Assert.Equal(new[] { "page1", "page2" }, pages.Select(p => (string?)p.Attribute("id")));
		Assert.Equal(new[] { "page1", "page2" }, pages.Select(p => p.Elements("property").Single(x => (string?)x.Attribute("name") == "name").Value));
		Assert.Equal(new[] { "Page 1", "Page 2" }, pages.Select(p => p.Elements("property").Single(x => (string?)x.Attribute("name") == "title").Value));
		Assert.All(pages, p => Assert.Equal("yes", (string?)p.Elements("property").Single(x => (string?)x.Attribute("name") == "title").Attribute("translatable")));
		Assert.Equal(new[] { "button1", "label1" }, pages.Select(p => (string?)p.Elements("property").Single(x => (string?)x.Attribute("name") == "child").Element("object")?.Attribute("id")));
		await ValidateGtkBuilderAsync(stackPath);
		await app.InvokeAsync("od.close-active-view");

		var notebookPath = Path.Combine(workDir, "Windows", "NotebookWindow.ui");
		await File.WriteAllTextAsync(notebookPath, """
			<?xml version="1.0" encoding="UTF-8"?>
			<interface>
			  <requires lib="gtk" version="4.0" />
			  <object class="GtkWindow" id="notebookWindow">
			    <property name="default-width">400</property>
			    <property name="default-height">300</property>
			    <child>
			      <object class="GtkNotebook" id="notebook" />
			    </child>
			  </object>
			</interface>
			""", TestContext.Current.CancellationToken);
		var openedNotebook = await app.InvokeAsync("od.open-file", notebookPath); Assert.True(openedNotebook.GetProperty("opened").GetBoolean(), openedNotebook.ToString());
		var notebookStatus = await WaitAsync("notebookWindow"); Assert.True(notebookStatus.GetProperty("nativeFrame").GetBoolean(), notebookStatus.ToString());
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "notebook")).GetProperty("success").GetBoolean());
		var page = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkButton");
		Assert.True(page.GetProperty("success").GetBoolean(), page.ToString());
		Assert.Equal("button1", page.GetProperty("selectedId").GetString());
		Assert.True((await app.InvokeAsync("od.file.save", notebookPath)).GetProperty("success").GetBoolean());
		var notebookText = (await File.ReadAllTextAsync(notebookPath, TestContext.Current.CancellationToken)).Replace("\r", "");
		var notebook = System.Xml.Linq.XDocument.Parse(notebookText).Descendants("object").Single(x => (string?)x.Attribute("id") == "notebook");
		// A page is a bare <child>; it is followed by its own editable <child type="tab"> label.
		var notebookChildren = notebook.Elements("child").ToList();
		Assert.Equal(2, notebookChildren.Count);
		var notebookChild = notebookChildren[0];
		Assert.Equal("GtkButton", (string?)notebookChild.Element("object")?.Attribute("class"));
		Assert.Equal("button1", (string?)notebookChild.Element("object")?.Attribute("id"));
		Assert.Null((string?)notebookChild.Attribute("type"));
		Assert.Equal("tab", (string?)notebookChildren[1].Attribute("type"));
		Assert.Equal("GtkLabel", (string?)notebookChildren[1].Element("object")?.Attribute("class"));
		Assert.DoesNotContain("GtkNotebookPage", notebookText);
		await ValidateGtkBuilderAsync(notebookPath);
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_SingleChildContainers_AreNotDropTargets_AndRefuseASecondChild()
	{
		// GtkFrame (like GtkScrolledWindow, a window and AdwClamp) takes exactly one child. GtkBuilder
		// accepts a second <child> but GTK keeps only one, so the document the designer writes would
		// not match what GTK shows: a full one is not a drop target and an insertion is refused.
		var framePath = Path.Combine(workDir, "Windows", "FrameWindow.ui");
		await File.WriteAllTextAsync(framePath, """
			<?xml version="1.0" encoding="UTF-8"?>
			<interface>
			  <requires lib="gtk" version="4.0" />
			  <object class="GtkWindow" id="frameWindow">
			    <property name="default-width">400</property>
			    <property name="default-height">200</property>
			    <child>
			      <object class="GtkFrame" id="frame">
			        <child>
			          <object class="GtkLabel" id="only">
			            <property name="label">Only</property>
			          </object>
			        </child>
			      </object>
			    </child>
			  </object>
			</interface>
			""", TestContext.Current.CancellationToken);
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", framePath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("frameWindow"); Assert.True(status.GetProperty("nativeFrame").GetBoolean(), status.ToString());
		var count = status.GetProperty("elementCount").GetInt32();

		var child = await app.InvokeAsync("od.gtk-designer.bounds", "only");
		Assert.True(child.GetProperty("success").GetBoolean(), child.ToString());
		var x = child.GetProperty("x").GetDouble() + child.GetProperty("width").GetDouble() / 2;
		var y = child.GetProperty("y").GetDouble() + child.GetProperty("height").GetDouble() / 2;
		var plan = await app.InvokeAsync("od.gtk-designer.drop-plan", x, y);
		Assert.False(plan.GetProperty("success").GetBoolean(), "the full frame must not be a drop target: " + plan);
		var dropped = await app.InvokeAsync("od.gtk-designer.toolbox.drop-at", "GtkButton", x, y);
		Assert.False(dropped.GetProperty("success").GetBoolean(), dropped.ToString());

		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "frame")).GetProperty("success").GetBoolean());
		var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkButton"));
		Assert.True(refused.Message.Contains("A GtkFrame holds a single child."), refused.Message);
		status = await app.InvokeAsync("od.gtk-designer.status");
		Assert.Equal(count, status.GetProperty("elementCount").GetInt32());
		Assert.True((await app.InvokeAsync("od.file.save", framePath)).GetProperty("success").GetBoolean());
		var frame = System.Xml.Linq.XDocument.Parse((await File.ReadAllTextAsync(framePath, TestContext.Current.CancellationToken)).Replace("\r", "")).Descendants("object").Single(o => (string?)o.Attribute("id") == "frame");
		Assert.Single(frame.Elements("child"));
		await ValidateGtkBuilderAsync(framePath);
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_PanedJourney_PlacesChildrenInTheStartAndEndSlots()
	{
		// GtkPaned is a container with two named slots. A drop plans them from GTK's measured bounds
		// (index 0 = start, 1 = end), the host writes the child in that order, and once both are filled
		// the paned is skipped so a third drop lands in the box around it.
		var path = Path.Combine(workDir, "Windows", "PanedWindow.ui");
		await File.WriteAllTextAsync(path, """
			<?xml version="1.0" encoding="UTF-8"?>
			<interface>
			  <requires lib="gtk" version="4.0" />
			  <object class="GtkWindow" id="panedWindow">
			    <property name="default-width">400</property>
			    <property name="default-height">200</property>
			    <child>
			      <object class="GtkBox" id="box">
			        <property name="orientation">vertical</property>
			        <child>
			          <object class="GtkPaned" id="paned">
			            <property name="hexpand">True</property>
			            <property name="vexpand">True</property>
			            <child>
			              <object class="GtkLabel" id="left">
			                <property name="label">Left</property>
			              </object>
			            </child>
			          </object>
			        </child>
			      </object>
			    </child>
			  </object>
			</interface>
			""", TestContext.Current.CancellationToken);
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", path); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("panedWindow"); Assert.True(status.GetProperty("nativeFrame").GetBoolean(), status.ToString());

		async Task<(double X, double Y, double Width, double Height)> Bounds(string id)
		{
			var b = await app.InvokeAsync("od.gtk-designer.bounds", id); Assert.True(b.GetProperty("success").GetBoolean(), id + ": " + b);
			return (b.GetProperty("x").GetDouble(), b.GetProperty("y").GetDouble(), b.GetProperty("width").GetDouble(), b.GetProperty("height").GetDouble());
		}
		var paned = await Bounds("paned");
		Assert.True(paned.Width > 100 && paned.Height > 50, "the paned was not laid out: " + paned);

		// After the start child's middle: the drop becomes the end child, to the right of it.
		var endX = paned.X + paned.Width * 0.8; var endY = paned.Y + paned.Height / 2;
		var endPlan = await app.InvokeAsync("od.gtk-designer.drop-plan", endX, endY);
		Assert.True(endPlan.GetProperty("success").GetBoolean(), endPlan.ToString());
		Assert.Equal("paned", endPlan.GetProperty("containerId").GetString());
		Assert.Equal(1, endPlan.GetProperty("index").GetInt32());
		var endDrop = await app.InvokeAsync("od.gtk-designer.toolbox.drop-at", "GtkButton", endX, endY);
		Assert.True(endDrop.GetProperty("success").GetBoolean(), endDrop.ToString());
		Assert.Equal("button1", endDrop.GetProperty("selectedId").GetString());
		// GTK puts the second child after the first one's natural width (the paned's default
		// position), so the new end child starts at the start child's right edge.
		JsonElement endBounds = default, startNow = default;
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			endBounds = await app.InvokeAsync("od.gtk-designer.bounds", "button1");
			startNow = await app.InvokeAsync("od.gtk-designer.bounds", "left");
			return endBounds.GetProperty("success").GetBoolean() && startNow.GetProperty("success").GetBoolean()
				&& endBounds.GetProperty("x").GetDouble() >= startNow.GetProperty("x").GetDouble() + startNow.GetProperty("width").GetDouble();
		}, TimeSpan.FromSeconds(10)), "the dropped button did not become the paned's end child: " + endBounds + " start=" + startNow);

		// A full paned is skipped: the next drop lands in the box, below the paned.
		var belowX = paned.X + paned.Width * 0.8; var belowY = paned.Y + paned.Height * 0.8;
		var boxPlan = await app.InvokeAsync("od.gtk-designer.drop-plan", belowX, belowY);
		Assert.True(boxPlan.GetProperty("success").GetBoolean(), boxPlan.ToString());
		Assert.Equal("box", boxPlan.GetProperty("containerId").GetString());
		Assert.Equal(1, boxPlan.GetProperty("index").GetInt32());
		var boxDrop = await app.InvokeAsync("od.gtk-designer.toolbox.drop-at", "GtkButton", belowX, belowY);
		Assert.True(boxDrop.GetProperty("success").GetBoolean(), boxDrop.ToString());
		Assert.Equal("button2", boxDrop.GetProperty("selectedId").GetString());
		JsonElement belowBounds = default, panedNow = default;
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			belowBounds = await app.InvokeAsync("od.gtk-designer.bounds", "button2");
			panedNow = await app.InvokeAsync("od.gtk-designer.bounds", "paned");
			return belowBounds.GetProperty("success").GetBoolean() && panedNow.GetProperty("success").GetBoolean()
				&& belowBounds.GetProperty("y").GetDouble() >= panedNow.GetProperty("y").GetDouble() + panedNow.GetProperty("height").GetDouble();
		}, TimeSpan.FromSeconds(10)), "the third drop is not below the paned in the box: " + belowBounds + " paned=" + panedNow);

		// An insertion addressed at the full paned is refused with its own reason.
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "paned")).GetProperty("success").GetBoolean());
		var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkButton"));
		Assert.True(refused.Message.Contains("A GtkPaned holds only a start and an end child."), refused.Message);

		Assert.True((await app.InvokeAsync("od.file.save", path)).GetProperty("success").GetBoolean());
		var xml = System.Xml.Linq.XDocument.Parse((await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Replace("\r", ""));
		var panedXml = xml.Descendants("object").Single(o => (string?)o.Attribute("id") == "paned");
		Assert.Equal(new[] { "left", "button1" }, panedXml.Elements("child").Select(c => (string?)c.Element("object")?.Attribute("id")));
		var boxXml = xml.Descendants("object").Single(o => (string?)o.Attribute("id") == "box");
		Assert.Equal(new[] { "paned", "button2" }, boxXml.Elements("child").Select(c => (string?)c.Element("object")?.Attribute("id")));
		await ValidateGtkBuilderAsync(path);
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_MalformedSourceEdit_IsReported_AndTheDesignerRecovers()
	{
		// Editing the XML pane into an invalid document must not lose the design or crash the host:
		// the designer reports the error, and fixing the text brings the design back.
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", uiPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("mainWindow"); Assert.Equal(4, status.GetProperty("elementCount").GetInt32());
		var hostProcessId = status.GetProperty("hostProcessId").GetInt32();

		// Type into the XML pane the way a user does: the source editor becomes the active view, so
		// returning to the designer hands it the edited text.
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => (await app.InvokeAsync("od.file.query-text-area-screen-bounds", uiPath)).GetProperty("success").GetBoolean(), TimeSpan.FromSeconds(10)), "the XML source view never became visible");
		var broken = await app.InvokeAsync("od.file.replace-text", uiPath, "<property name=\"label\">Run</property>", "<property name=\"label\">Run</propertyX>");
		Assert.True(broken.GetProperty("success").GetBoolean(), broken.ToString());
		JsonElement reported = default;
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			reported = await app.InvokeAsync("od.gtk-designer.status");
			var text = reported.ToString();
			return text.Contains("propertyX", StringComparison.Ordinal) || text.Contains("does not match", StringComparison.OrdinalIgnoreCase) || text.Contains("unexpected end tag", StringComparison.OrdinalIgnoreCase);
		}, TimeSpan.FromSeconds(15)), "the malformed source was not reported: " + reported);

		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => (await app.InvokeAsync("od.file.query-text-area-screen-bounds", uiPath)).GetProperty("success").GetBoolean(), TimeSpan.FromSeconds(10)), "the XML source view never became visible");
		var fixedText = await app.InvokeAsync("od.file.replace-text", uiPath, "<property name=\"label\">Run</propertyX>", "<property name=\"label\">Run again</property>");
		Assert.True(fixedText.GetProperty("success").GetBoolean(), fixedText.ToString());
		JsonElement recovered = default;
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			recovered = await app.InvokeAsync("od.gtk-designer.status");
			return recovered.GetProperty("elementCount").GetInt32() == 4 && recovered.GetProperty("nativeFrame").GetBoolean()
				&& recovered.GetProperty("hostProcessId").GetInt32() == hostProcessId;
		}, TimeSpan.FromSeconds(15)), "the designer did not recover on the same host after the fix: " + recovered);
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "runButton")).GetProperty("success").GetBoolean());
		var label = (await app.InvokeAsync("od.gtk-designer.properties.describe")).GetProperty("items").EnumerateArray().First(i => i.GetProperty("name").GetString() == "Label");
		Assert.Equal("Run again", label.GetProperty("value").GetString());
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_ItemsCollectionEditor_UsesThePropertiesPadAndPersistsOneUndoableEdit()
	{
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", uiPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		await WaitAsync();
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "contentBox")).GetProperty("success").GetBoolean());
		var inserted = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkDropDown"); Assert.True(inserted.GetProperty("success").GetBoolean(), inserted.ToString());
		Assert.Equal("dropDown1", inserted.GetProperty("selectedId").GetString());

		// Exercise the actual Properties-pad Items button rather than the RPC directly. GtkBuilder's
		// shorthand list is source-owned, so the new entry must be serialized below the DropDown.
		var edited = await app.InvokeAsync("od.gtk-designer.properties.edit-items", "$items", "add:-1:First;add:0:Second;up:1");
		Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
		Assert.True(edited.GetProperty("committed").GetBoolean(), edited.ToString());
		Assert.Equal(new[] { "Second", "First" }, edited.GetProperty("after").EnumerateArray().Select(item => item.GetString()));

		var saved = await app.InvokeAsync("od.file.save", uiPath); Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
		var ui = await File.ReadAllTextAsync(uiPath, TestContext.Current.CancellationToken);
		Assert.Matches("<object class=\"GtkDropDown\" id=\"dropDown1\">\\s*<items>\\s*<item translatable=\"yes\">Second</item>\\s*<item translatable=\"yes\">First</item>\\s*</items>\\s*</object>", ui);
		var undone = await app.InvokeAsync("od.gtk-designer.undo"); Assert.True(undone.GetProperty("canRedo").GetBoolean(), undone.ToString());
		var afterUndo = await app.InvokeAsync("od.gtk-designer.properties.edit-items", "$items", "cancel");
		Assert.Empty(afterUndo.GetProperty("before").EnumerateArray());
		var redone = await app.InvokeAsync("od.gtk-designer.redo"); Assert.False(redone.GetProperty("canRedo").GetBoolean(), redone.ToString());
		var afterRedo = await app.InvokeAsync("od.gtk-designer.properties.edit-items", "$items", "cancel");
		Assert.Equal(new[] { "Second", "First" }, afterRedo.GetProperty("before").EnumerateArray().Select(item => item.GetString()));
	}

	[Fact]
	public async Task GtkDesigner_ListViewModel_EditsItsStringItems()
	{
		// A list/grid/column view gets its strings from an inline GtkStringList model, so the
		// Properties pad edits that model's <items> from the view - there is no list-view property
		// to edit. A model reference (a property holding an id) is not editable this way.
		var path = Path.Combine(workDir, "Windows", "ListViewWindow.ui");
		await File.WriteAllTextAsync(path, """
			<?xml version="1.0" encoding="UTF-8"?>
			<interface>
			  <requires lib="gtk" version="4.0" />
			  <object class="GtkWindow" id="listWindow">
			    <property name="default-width">320</property>
			    <property name="default-height">200</property>
			    <child>
			      <object class="GtkListView" id="listView">
			        <property name="model">
			          <object class="GtkStringList" id="listModel">
			            <items><item translatable="yes">One</item><item>Two</item></items>
			          </object>
			        </property>
			      </object>
			    </child>
			  </object>
			</interface>
			""", TestContext.Current.CancellationToken);
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", path); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("listWindow"); Assert.True(status.GetProperty("nativeFrame").GetBoolean(), status.ToString());
		Assert.Contains("listView", status.GetProperty("elementIds").EnumerateArray().Select(id => id.GetString()));
		Assert.Contains("listModel", status.GetProperty("elementIds").EnumerateArray().Select(id => id.GetString()));

		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "listView")).GetProperty("success").GetBoolean());
		var items = (await app.InvokeAsync("od.gtk-designer.properties.describe")).GetProperty("items").EnumerateArray()
			.FirstOrDefault(item => item.GetProperty("name").GetString() == "$items");
		Assert.Equal("GtkStringList", items.GetProperty("category").GetString());
		var edited = await app.InvokeAsync("od.gtk-designer.properties.edit-items", "$items", "add:-1:Three");
		Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
		Assert.Equal(new[] { "One", "Two", "Three" }, edited.GetProperty("after").EnumerateArray().Select(item => item.GetString()));

		Assert.True((await app.InvokeAsync("od.file.save", path)).GetProperty("success").GetBoolean());
		var xml = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
		Assert.Matches("<object class=\"GtkStringList\" id=\"listModel\">\\s*<items>\\s*<item translatable=\"yes\">One</item>\\s*<item>Two</item>\\s*<item translatable=\"yes\">Three</item>\\s*</items>", xml);
		await ValidateGtkBuilderAsync(path);
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_ToolboxListViews_StartWithAModelAndFactory()
	{
		// A list/grid/column view dropped from the toolbox starts with an inline string list and a
		// label factory, so it renders and its Items row edits real data instead of an empty shell.
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", uiPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		await WaitAsync("mainWindow");
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "contentBox")).GetProperty("success").GetBoolean());
		var list = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkListView");
		Assert.True(list.GetProperty("success").GetBoolean(), list.ToString());
		Assert.Equal("listView1", list.GetProperty("selectedId").GetString());
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "listView1")).GetProperty("success").GetBoolean());
		var edited = await app.InvokeAsync("od.gtk-designer.properties.edit-items", "$items", "add:-1:Extra");
		Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());

		var columns = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkColumnView");
		Assert.True(columns.GetProperty("success").GetBoolean(), columns.ToString());
		Assert.Equal("columnView1", columns.GetProperty("selectedId").GetString());
		// A column view takes columns, not widgets; a further column can be added and deleted.
		var column = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkColumnViewColumn");
		Assert.True(column.GetProperty("success").GetBoolean(), column.ToString());
		Assert.Equal("column2", column.GetProperty("selectedId").GetString());
		var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkButton"));
		Assert.Contains("holds columns", refused.Message);
		Assert.True((await app.InvokeAsync("od.gtk-designer.delete")).GetProperty("success").GetBoolean());

		// The cell template is editable as XML; invalid XML is refused and keeps the old template.
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "listView1")).GetProperty("success").GetBoolean());
		var factory = await app.InvokeAsync("od.gtk-designer.factory-template");
		Assert.True(factory.GetProperty("success").GetBoolean(), factory.ToString());
		var template = factory.GetProperty("template").GetString()!;
		Assert.Contains("GtkListItem", template);
		var updated = await app.InvokeAsync("od.gtk-designer.factory-template.set", template.Replace("GtkLabel", "GtkButton"));
		Assert.True(updated.GetProperty("success").GetBoolean(), updated.ToString());
		Assert.Contains("GtkButton", updated.GetProperty("template").GetString());
		var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() => app.InvokeAsync("od.gtk-designer.factory-template.set", "<not xml"));
		Assert.Contains("not valid XML", invalid.Message);

		Assert.True((await app.InvokeAsync("od.file.save", uiPath)).GetProperty("success").GetBoolean());
		var xml = await File.ReadAllTextAsync(uiPath, TestContext.Current.CancellationToken);
		Assert.Matches("<object class=\"GtkListView\" id=\"listView1\">\\s*<property name=\"model\">\\s*<object class=\"GtkStringList\" id=\"listModel1\">[\\s\\S]*?<item translatable=\"yes\">Extra</item>", xml);
		Assert.Contains("<object class=\"GtkBuilderListItemFactory\">", xml);
		Assert.Contains("<property name=\"bytes\">&lt;interface&gt;", xml);
		Assert.Contains("GtkButton", xml);
		Assert.Matches("<object class=\"GtkColumnView\" id=\"columnView1\">\\s*<property name=\"model\">[\\s\\S]*?<child>\\s*<object class=\"GtkColumnViewColumn\" id=\"column1\">", xml);
		Assert.Contains("<property name=\"title\" translatable=\"yes\">Column 1</property>", xml);
		Assert.DoesNotContain("id=\"column2\"", xml);
		await ValidateGtkBuilderAsync(uiPath);
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_MenuItems_EditThePopoverMenu()
	{
		// GtkBuilder parses menus as <menu> tags, not a Gio.Menu object; the designer edits the
		// top-level label/action pairs of the selected popover and leaves sections to the source.
		var path = Path.Combine(workDir, "Windows", "MenuWindow.ui");
		await File.WriteAllTextAsync(path, """
			<?xml version="1.0" encoding="UTF-8"?>
			<interface>
			  <requires lib="gtk" version="4.0" />
			  <object class="GtkWindow" id="menuWindow">
			    <property name="default-width">320</property>
			    <property name="default-height">200</property>
			    <child>
			      <object class="GtkMenuButton" id="menuButton">
			        <property name="label">Menu</property>
			        <property name="popover">
			          <object class="GtkPopoverMenu" id="menuPopover">
			            <property name="menu-model">
			              <menu>
			                <item><attribute name="label">New</attribute><attribute name="action">app.new</attribute></item>
			                <item><attribute name="label">Open</attribute><attribute name="action">app.open</attribute></item>
			              </menu>
			            </property>
			          </object>
			        </property>
			      </object>
			    </child>
			  </object>
			</interface>
			""", TestContext.Current.CancellationToken);
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", path); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("menuWindow"); Assert.True(status.GetProperty("nativeFrame").GetBoolean(), status.ToString());
		Assert.Contains("menuPopover", status.GetProperty("elementIds").EnumerateArray().Select(id => id.GetString()));

		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "menuPopover")).GetProperty("success").GetBoolean());
		var before = await app.InvokeAsync("od.gtk-designer.menu-items");
		Assert.True(before.GetProperty("success").GetBoolean(), before.ToString());
		Assert.Equal(new[] { "New", "Open" }, before.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("label").GetString()));
		var edited = await app.InvokeAsync("od.gtk-designer.menu-items.set", "New=app.new;Open=app.open;Quit=app.quit");
		Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
		Assert.Equal(new[] { "New", "Open", "Quit" }, edited.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("label").GetString()));

		Assert.True((await app.InvokeAsync("od.file.save", path)).GetProperty("success").GetBoolean());
		var xml = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
		Assert.Contains("<attribute name=\"label\">Quit</attribute>", xml);
		Assert.Contains("<attribute name=\"action\">app.quit</attribute>", xml);
		await ValidateGtkBuilderAsync(path);
		Assert.True((await app.InvokeAsync("od.gtk-designer.undo")).GetProperty("canRedo").GetBoolean());
		Assert.Equal(2, (await app.InvokeAsync("od.gtk-designer.menu-items")).GetProperty("items").GetArrayLength());

		// Sections and submenus go through the same shared outline and stay a valid GtkBuilder menu.
		var nested = await app.InvokeAsync("od.gtk-designer.menu-items.set", "[section] File;  New=app.new;  Open=app.open;[submenu] Recent;  A=app.a");
		Assert.True(nested.GetProperty("success").GetBoolean(), nested.ToString());
		var tree = nested.GetProperty("items").EnumerateArray().ToList();
		Assert.Equal(new[] { "section", "submenu" }, tree.Select(item => item.GetProperty("kind").GetString()));
		Assert.Equal(2, tree[0].GetProperty("children").GetArrayLength());
		Assert.True((await app.InvokeAsync("od.file.save", path)).GetProperty("success").GetBoolean());
		xml = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
		Assert.Contains("<section>", xml);
		Assert.Contains("<submenu>", xml);
		await ValidateGtkBuilderAsync(path);

		// Window actions ("win.save") get a handler stub in MenuWindow.cs and an AddMenuActions in the
		// companion; an application action ("app.quit") belongs to application code and gets nothing.
		var actions = await app.InvokeAsync("od.gtk-designer.menu-items.set", "Save=win.save;Save as=win.save-as;Save dotted=win.save.as;Odd=win.under_score;Quit=app.quit");
		Assert.True(actions.GetProperty("success").GetBoolean(), actions.ToString());
		Assert.True((await app.InvokeAsync("od.file.save", path)).GetProperty("success").GetBoolean());
		var behaviorPath = Path.ChangeExtension(path, ".cs");
		// Neither file is open in an editor, so the designer wrote them to disk directly.
		var companionPath = path + ".cs";
		var behavior = await File.ReadAllTextAsync(behaviorPath, TestContext.Current.CancellationToken);
		Assert.Contains("void save_action(Gio.SimpleAction sender, Gio.SimpleAction.ActivateSignalArgs args)", behavior);
		Assert.Contains("void save_as_action(Gio.SimpleAction sender, Gio.SimpleAction.ActivateSignalArgs args)", behavior);
		Assert.Contains("void save__as_action(Gio.SimpleAction sender, Gio.SimpleAction.ActivateSignalArgs args)", behavior); // "save.as" is not "save-as"
		Assert.DoesNotContain("under_score", behavior); // not a GAction name (g_simple_action_new would return NULL)
		Assert.DoesNotContain("quit_action", behavior);
		var companion = await File.ReadAllTextAsync(companionPath, TestContext.Current.CancellationToken);
		Assert.Contains("public void AddMenuActions(global::Gio.ActionMap map)", companion);
		Assert.Contains("global::Gio.SimpleAction.New(\"save-as\", null)", companion);
		Assert.DoesNotContain("\"quit\"", companion);
		// The generated stubs and registration must compile against Gir.Core.
		await ValidateFixtureBuildAsync(projectPath);
		// Dropping the last window action drops AddMenuActions; the user's call site is told, not left to a compile error.
		var removed = await app.InvokeAsync("od.gtk-designer.menu-items.set", "Quit=app.quit");
		Assert.True(removed.GetProperty("success").GetBoolean(), removed.ToString());
		Assert.DoesNotContain("AddMenuActions", await File.ReadAllTextAsync(companionPath, TestContext.Current.CancellationToken));
		Assert.Contains("AddMenuActions", (await app.InvokeAsync("od.gtk-designer.status")).GetProperty("diagnosticText").GetString());
		// Undo brings the actions back, and the companion follows (a history step is not a blind spot).
		await app.InvokeAsync("od.gtk-designer.undo");
		Assert.Contains("AddMenuActions", await File.ReadAllTextAsync(companionPath, TestContext.Current.CancellationToken));
		await ValidateFixtureBuildAsync(projectPath);
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_SourceView_SharesTheDesignerToolbox_AndPlacesDroppedItems()
	{
		// A .ui shown Design and Source side by side has one toolbox. With the Source half focused the
		// Tools pad still shows the GTK list (the plain text editor has none of its own), and a GTK
		// control dropped onto the markup becomes a real GtkBuilder child - <child><object class id/> -
		// in the container under the drop point, not "<GtkSwitch />" or the dragged type as plain text.
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", uiPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("mainWindow"); Assert.Equal(4, status.GetProperty("elementCount").GetInt32());

		var original = await File.ReadAllTextAsync(uiPath, TestContext.Current.CancellationToken);
		// On the heading's label text: the new control goes after the heading, inside contentBox.
		var onHeading = original.IndexOf("GTK 4 Designer", StringComparison.Ordinal) + 3;
		var dropped = await app.InvokeAsync("od.file.drop-toolbox-item", uiPath, onHeading, "GtkSwitch");
		Assert.True(dropped.GetProperty("success").GetBoolean() && dropped.GetProperty("inserted").GetBoolean(), dropped.ToString());
		var text = dropped.GetProperty("text").GetString()!;
		var heading = text.IndexOf("id=\"heading\"", StringComparison.Ordinal);
		var inserted = text.IndexOf("class=\"GtkSwitch\"", StringComparison.Ordinal);
		var run = text.IndexOf("id=\"runButton\"", StringComparison.Ordinal);
		Assert.True(heading < inserted && inserted < run, "the switch did not land between the heading and the button:\n" + text);
		Assert.Contains("<child>", text[heading..run]);
		Assert.DoesNotContain("<GtkSwitch", text);

		var pad = await app.InvokeAsync("od.tools-pad.status");
		Assert.Equal("AvalonEditViewContent", pad.GetProperty("activeView").GetString());
		Assert.True(pad.GetProperty("hostsSharedToolbox").GetBoolean(), "the Source half lost the GTK toolbox: " + pad);
		Assert.Equal("GtkDesignerViewContent", pad.GetProperty("sharedToolboxOwner").GetString());

		// A drop with no container under it is refused, and the text is left alone.
		var onRequires = text.IndexOf("<requires", StringComparison.Ordinal) + 2;
		var refused = await app.InvokeAsync("od.file.drop-toolbox-item", uiPath, onRequires, "GtkSwitch");
		Assert.False(refused.GetProperty("inserted").GetBoolean(), refused.ToString());
		Assert.Equal(text, refused.GetProperty("text").GetString());

		// Back on the design surface, the designer has the control the Source half added.
		JsonElement designed = default;
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			designed = await app.InvokeAsync("od.gtk-designer.status");
			return designed.GetProperty("elementIds").EnumerateArray().Any(e => e.GetString() == "switch1");
		}, TimeSpan.FromSeconds(15)), "the designer did not pick up the dropped switch: " + designed);
		Assert.True(designed.GetProperty("nativeFrame").GetBoolean(), designed.ToString());
		await app.InvokeAsync("od.file.revert-all-dirty");
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_ExternalChangeOnDisk_ReloadsTheDesign()
	{
		// Another tool (Cambalache, git checkout, a text editor) rewrites the .ui while it is open
		// and unmodified in the IDE: the file watcher reloads it, and the designer must show the
		// new document - same host, no restart.
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", uiPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("mainWindow"); Assert.Equal(4, status.GetProperty("elementCount").GetInt32());
		var hostProcessId = status.GetProperty("hostProcessId").GetInt32();

		var text = await File.ReadAllTextAsync(uiPath, TestContext.Current.CancellationToken);
		text = text.Replace("<property name=\"label\">Run</property>", "<property name=\"label\">Run externally</property>", StringComparison.Ordinal)
			.Replace("</object>\n        </child>\n      </object>", "</object>\n        </child>\n        <child>\n          <object class=\"GtkSwitch\" id=\"externalSwitch\" />\n        </child>\n      </object>", StringComparison.Ordinal);
		if (!text.Contains("externalSwitch", StringComparison.Ordinal))   // CRLF checkout
			text = text.Replace("</object>\r\n        </child>\r\n      </object>", "</object>\r\n        </child>\r\n        <child>\r\n          <object class=\"GtkSwitch\" id=\"externalSwitch\" />\r\n        </child>\r\n      </object>", StringComparison.Ordinal);
		Assert.Contains("externalSwitch", text);
		await File.WriteAllTextAsync(uiPath, text, TestContext.Current.CancellationToken);

		JsonElement reloaded = default;
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			await app.InvokeAsync("od.activate");   // the watcher reloads when the IDE window is active
			reloaded = await app.InvokeAsync("od.gtk-designer.status");
			return reloaded.GetProperty("elementIds").EnumerateArray().Any(e => e.GetString() == "externalSwitch");
		}, TimeSpan.FromSeconds(20)), "the external change was not reloaded into the designer: " + reloaded);
		Assert.Equal(hostProcessId, reloaded.GetProperty("hostProcessId").GetInt32());
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "runButton")).GetProperty("success").GetBoolean());
		var label = (await app.InvokeAsync("od.gtk-designer.properties.describe")).GetProperty("items").EnumerateArray().First(i => i.GetProperty("name").GetString() == "Label");
		Assert.Equal("Run externally", label.GetProperty("value").GetString());
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_CustomWidget_IsKeptAndReported_RestStaysEditable()
	{
		// An application-defined widget class cannot be instantiated by the designer's GTK. The
		// design must still open, say why, keep the object through edits and saves, and let the
		// rest of the document be edited.
		var customPath = Path.Combine(workDir, "Windows", "CustomWindow.ui");
		var customPreviewPath = Path.Combine(workDir, "Windows", ".gtkdesigner.json");
		await File.WriteAllTextAsync(customPreviewPath, """{"customWidgets":{"MyAppStarRating":{"previewClass":"GtkLabel","propertyMap":{"stars":"label"}}}}""", TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(customPath, """
			<?xml version="1.0" encoding="UTF-8"?>
			<interface>
			  <requires lib="gtk" version="4.0" />
			  <object class="GtkWindow" id="customWindow">
			    <child>
			      <object class="GtkBox" id="box">
			        <property name="orientation">vertical</property>
			        <child>
			          <object class="MyAppStarRating" id="rating">
			          <property name="stars">0.75</property>
			          </object>
			        </child>
			        <child>
			          <object class="GtkLabel" id="caption">
			            <property name="label">Rated</property>
			          </object>
			        </child>
			      </object>
			    </child>
			  </object>
			</interface>
			""", TestContext.Current.CancellationToken);
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", customPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		JsonElement status = default;
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			status = await app.InvokeAsync("od.gtk-designer.status");
			return status.TryGetProperty("active", out var a) && a.GetBoolean() && status.GetProperty("elementIds").EnumerateArray().Any(e => e.GetString() == "rating");
		}, TimeSpan.FromSeconds(30)), "the design with a custom widget did not open: " + status);
		Assert.True(status.GetProperty("nativeFrame").GetBoolean(), status.ToString());
		Assert.Contains("previewed as GtkLabel", status.GetProperty("diagnostics").ToString());
		Assert.DoesNotContain("placeholder", status.GetProperty("diagnostics").ToString(), StringComparison.OrdinalIgnoreCase);
		var labelFrame = status.GetProperty("nativeFrameFingerprint").GetString();
		// A declaration is preview-only: changing its safe Gtk representation redraws the same
		// source document on the existing host and preserves its generic custom properties.
		await File.WriteAllTextAsync(customPreviewPath, """{"customWidgets":{"MyAppStarRating":{"previewClass":"GtkProgressBar","propertyMap":{"stars":"fraction"}}}}""", TestContext.Current.CancellationToken);
		status = await WaitForFrameChangeAsync(labelFrame);
		Assert.True(status.GetProperty("nativeFrame").GetBoolean(), status.ToString());
		Assert.Contains("previewed as GtkProgressBar", status.GetProperty("diagnostics").ToString());

		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "caption")).GetProperty("success").GetBoolean());
		Assert.True((await app.InvokeAsync("od.gtk-designer.properties.edit", "Label", "Rated by you")).GetProperty("success").GetBoolean());
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "rating")).GetProperty("success").GetBoolean());
		var rating = (await app.InvokeAsync("od.gtk-designer.properties.describe")).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!, i => i);
		Assert.Equal("0.75", rating["Stars"].GetProperty("value").GetString());   // source type remains generic and editable
		Assert.True((await app.InvokeAsync("od.file.save", customPath)).GetProperty("success").GetBoolean());
		var xml = await File.ReadAllTextAsync(customPath, TestContext.Current.CancellationToken);
		Assert.Contains("<object class=\"MyAppStarRating\" id=\"rating\">", xml);
		Assert.Contains("<property name=\"stars\">0.75</property>", xml);
		Assert.Contains("Rated by you", xml);
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_CompositeTemplate_OpensRendersAndEdits()
	{
		// Gir.Core [Template] classes define their UI as <template class="X" parent="GtkWindow">.
		// The designer edits it as an instance of its parent type and previews it natively, though
		// the template's own GType exists only in the application.
		var templatePath = Path.Combine(workDir, "Windows", "TemplateWindow.ui");
		await File.WriteAllTextAsync(templatePath, """
			<?xml version="1.0" encoding="UTF-8"?>
			<interface>
			  <requires lib="gtk" version="4.0" />
			  <template class="TemplateWindow" parent="GtkWindow">
			    <property name="title">Template</property>
			    <child>
			      <object class="GtkLabel" id="templateLabel">
			        <property name="label">Inside a template</property>
			      </object>
			    </child>
			  </template>
			</interface>
			""", TestContext.Current.CancellationToken);
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", templatePath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("TemplateWindow");
		Assert.True(status.GetProperty("nativeFrame").GetBoolean(), "the template was not previewed: " + status);
		Assert.Equal(2, status.GetProperty("elementCount").GetInt32());

		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "TemplateWindow")).GetProperty("success").GetBoolean());
		var items = (await app.InvokeAsync("od.gtk-designer.properties.describe")).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!, i => i);
		Assert.Equal("Template", items["Title"].GetProperty("value").GetString());   // typed as GtkWindow
		Assert.True((await app.InvokeAsync("od.gtk-designer.properties.edit", "Title", "Edited template")).GetProperty("success").GetBoolean());
		Assert.True((await app.InvokeAsync("od.file.save", templatePath)).GetProperty("success").GetBoolean());
		var xml = await File.ReadAllTextAsync(templatePath, TestContext.Current.CancellationToken);
		Assert.Contains("<template class=\"TemplateWindow\" parent=\"GtkWindow\">", xml);
		Assert.Contains("Edited template", xml);
		Assert.DoesNotContain("<object class=\"GtkWindow\"", xml);
		await ValidateGtkBuilderAsync(templatePath);

		// A template must not carry <signal>: Gir.Core sets no managed BuilderScope, so GTK
		// resolves the handler as a native symbol and aborts the app. The designer refuses before
		// writing the entry and points at the [Gtk.Connect] + event-subscription pattern.
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "TemplateWindow")).GetProperty("success").GetBoolean());
		var refused = await app.InvokeAsync("od.gtk-designer.signal.set", "close-request", "TemplateWindow_close_request");
		Assert.False(refused.GetProperty("success").GetBoolean(), refused.ToString());
		Assert.True((await app.InvokeAsync("od.file.save", templatePath)).GetProperty("success").GetBoolean());
		var afterSignal = await File.ReadAllTextAsync(templatePath, TestContext.Current.CancellationToken);
		Assert.DoesNotContain("<signal", afterSignal);
		Assert.Contains("Edited template", afterSignal);
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_CompositeTemplate_WiresSignalsInTheTemplateClass()
	{
		// A Gir.Core template must not carry <signal>: GTK resolves the handler as a native symbol
		// and aborts the app (Gir.Core sets no managed BuilderScope). The designer wires the
		// template's own class instead - [Gtk.Connect] field + handler method + event subscription
		// in Initialize() - and the generated code has to compile with the fixture.
		var templatePath = Path.Combine(workDir, "Windows", "TemplatePanel.ui");
		var classPath = Path.Combine(workDir, "TemplatePanel.cs");
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", templatePath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("TemplatePanel"); Assert.True(status.GetProperty("nativeFrame").GetBoolean(), status.ToString());
		Assert.Contains("runButton", status.GetProperty("elementIds").EnumerateArray().Select(id => id.GetString()));

		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "runButton")).GetProperty("success").GetBoolean());
		var wired = await app.InvokeAsync("od.gtk-designer.signal.set", "clicked", "runButton_clicked");
		Assert.True(wired.GetProperty("success").GetBoolean(), wired.ToString());

		var source = await File.ReadAllTextAsync(classPath, TestContext.Current.CancellationToken);
		Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(source, "Gtk\\.Connect\\(\"runButton\"\\)").Count);
		Assert.Contains("void runButton_clicked(Gtk.Button sender, System.EventArgs args)", source);
		Assert.Contains("_runButton.OnClicked += (_, args) => runButton_clicked(_runButton, args);", source);

		Assert.True((await app.InvokeAsync("od.file.save", templatePath)).GetProperty("success").GetBoolean());
		Assert.DoesNotContain("<signal", await File.ReadAllTextAsync(templatePath, TestContext.Current.CancellationToken));

		// Clearing goes through the Properties pad's own adapter. Identity is the shape
		// `(_, args) => handler(local, args)`, so the first clear removes the designer-wired subscription
		// and the pad then shows the hand-written one of the same shape (indistinguishable, and as
		// hand-wired template handlers always appeared) instead of writing "" over it.
		var cleared = await app.InvokeAsync("od.gtk-designer.properties.event.set", "clicked", "");
		Assert.True(cleared.GetProperty("success").GetBoolean(), cleared.ToString());
		Assert.Equal("Observe", cleared.GetProperty("handler").GetString());
		// The wiring opened the class in the editor, so the edit lands in its buffer: save it.
		Assert.True((await app.InvokeAsync("od.file.save", classPath)).GetProperty("success").GetBoolean());
		source = await File.ReadAllTextAsync(classPath, TestContext.Current.CancellationToken);
		Assert.DoesNotContain("runButton_clicked(_runButton, args)", source);
		Assert.Contains("_runButton.OnClicked += (_, _) => _runButton.Label = \"Running\";", source); // another shape: never shown or touched
		Assert.Contains("_runButton.OnClicked += (_, args) => Observe(_runButton, args);", source);
		Assert.Contains("void runButton_clicked(Gtk.Button sender, System.EventArgs args)", source); // the user-owned method stays
		Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(source, "Gtk\\.Connect\\(\"runButton\"\\)").Count);
		// Clearing what the pad now shows removes that subscription too; the pad then shows nothing.
		var clearedAgain = await app.InvokeAsync("od.gtk-designer.properties.event.set", "clicked", "");
		Assert.True(clearedAgain.GetProperty("success").GetBoolean(), clearedAgain.ToString());
		Assert.Equal("", clearedAgain.GetProperty("handler").GetString());
		Assert.True((await app.InvokeAsync("od.file.save", classPath)).GetProperty("success").GetBoolean());
		source = await File.ReadAllTextAsync(classPath, TestContext.Current.CancellationToken);
		// Only the Initialize() subscription went; the same-shaped one in SubscribeElsewhere() is neither
		// shown (the pad reads ""), nor removed.
		Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(source, "Observe\\(_runButton, args\\)").Count);
		Assert.Contains("void SubscribeElsewhere()", source);
		Assert.Contains("_runButton.OnClicked += (_, _) => _runButton.Label = \"Running\";", source);
		Assert.Contains("private static void Observe(", source); // the method itself is user code and stays
		// Wiring again reuses the field and the method instead of duplicating them.
		Assert.True((await app.InvokeAsync("od.gtk-designer.signal.set", "clicked", "runButton_clicked")).GetProperty("success").GetBoolean());
		Assert.True((await app.InvokeAsync("od.file.save", classPath)).GetProperty("success").GetBoolean());
		source = await File.ReadAllTextAsync(classPath, TestContext.Current.CancellationToken);
		Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(source, "runButton_clicked\\(_runButton, args\\)").Count);
		Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(source, "void runButton_clicked").Count);

		// The generated method and subscription must compile; the fixture also runs the template.
		await ValidateFixtureBuildAsync(projectPath);
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_LibadwaitaNavigation_UsesTypedPagesAndRenders()
	{
		var path = Path.Combine(workDir, "Windows", "NavigationWindow.ui");
		await File.WriteAllTextAsync(path, """
			<interface>
			  <requires lib="gtk" version="4.0" />
			  <requires lib="libadwaita" version="1.0" />
			  <object class="AdwWindow" id="navigationWindow">
			    <property name="content"><object class="AdwNavigationView" id="navigationView" /></property>
			  </object>
			</interface>
			""", TestContext.Current.CancellationToken);
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", path); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("navigationWindow"); Assert.True(status.GetProperty("nativeFrame").GetBoolean(), status.ToString());
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "navigationView")).GetProperty("success").GetBoolean());
		var page = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "AdwNavigationPage"); Assert.True(page.GetProperty("success").GetBoolean(), page.ToString());
		Assert.True((await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkBox")).GetProperty("success").GetBoolean());
		Assert.True((await app.InvokeAsync("od.file.save", path)).GetProperty("success").GetBoolean());
		var xml = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
		Assert.Contains("<child><object class=\"AdwNavigationPage\"", xml);
		Assert.Contains("<property name=\"child\"><object class=\"GtkBox\"", xml);
		// gtk4-builder-tool never loads libadwaita, so it rejects any Adw document outright
		// ("Invalid object type 'AdwWindow'") and cannot validate this one. The saved file is
		// checked the way the product does it instead: reopen it and require a native frame.
		await app.InvokeAsync("od.close-active-view");
		var reopened = await app.InvokeAsync("od.open-file", path); Assert.True(reopened.GetProperty("opened").GetBoolean(), reopened.ToString());
		var saved = await WaitAsync("navigationWindow"); Assert.True(saved.GetProperty("nativeFrame").GetBoolean(), saved.ToString());
		Assert.Equal(4, saved.GetProperty("elementCount").GetInt32());
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_HeaderBar_PacksInsertedControlsAtTheStart()
	{
		// A header bar's children are packed with <child type="start|end|title">; a toolbox click
		// (no drop point) packs at the start, and the saved document must still load in GTK.
		var path = Path.Combine(workDir, "Windows", "HeaderBarWindow.ui");
		await File.WriteAllTextAsync(path, """
			<?xml version="1.0" encoding="UTF-8"?>
			<interface>
			  <requires lib="gtk" version="4.0" />
			  <object class="GtkWindow" id="headerWindow">
			    <property name="default-width">400</property>
			    <property name="default-height">200</property>
			    <property name="titlebar"><object class="GtkHeaderBar" id="headerBar" /></property>
			  </object>
			</interface>
			""", TestContext.Current.CancellationToken);
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", path); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("headerWindow"); Assert.True(status.GetProperty("nativeFrame").GetBoolean(), status.ToString());
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "headerBar")).GetProperty("success").GetBoolean());
		var inserted = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkButton");
		Assert.True(inserted.GetProperty("success").GetBoolean(), inserted.ToString());
		Assert.True((await app.InvokeAsync("od.file.save", path)).GetProperty("success").GetBoolean());
		var xml = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
		Assert.Contains("<child type=\"start\">", xml);
		await ValidateGtkBuilderAsync(path);
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_LibadwaitaTabView_UsesTabPagesAndRenders()
	{
		// An AdwTabView page is an AdwTabPage object holding the widget as its child property, like a
		// GtkStackPage; a drop writes the page, selects the widget the user asked for, and the saved
		// document still renders natively (gtk4-builder-tool never loads libadwaita, so the proof is a frame).
		var path = Path.Combine(workDir, "Windows", "TabViewWindow.ui");
		await File.WriteAllTextAsync(path, """
			<interface>
			  <requires lib="gtk" version="4.0" />
			  <requires lib="libadwaita" version="1.0" />
			  <object class="AdwWindow" id="tabWindow">
			    <property name="content"><object class="AdwTabView" id="tabView" /></property>
			  </object>
			</interface>
			""", TestContext.Current.CancellationToken);
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", path); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("tabWindow"); Assert.True(status.GetProperty("nativeFrame").GetBoolean(), status.ToString());
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "tabView")).GetProperty("success").GetBoolean());
		var inserted = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkLabel"); Assert.True(inserted.GetProperty("success").GetBoolean(), inserted.ToString());
		Assert.Equal("label1", inserted.GetProperty("selectedId").GetString());
		Assert.True((await app.InvokeAsync("od.file.save", path)).GetProperty("success").GetBoolean());
		var xml = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
		Assert.Contains("<object class=\"AdwTabPage\"", xml);
		Assert.Contains("<property name=\"child\"><object class=\"GtkLabel\"", xml);
		await app.InvokeAsync("od.close-active-view");
		var reopened = await app.InvokeAsync("od.open-file", path); Assert.True(reopened.GetProperty("opened").GetBoolean(), reopened.ToString());
		var saved = await WaitAsync("tabWindow"); Assert.True(saved.GetProperty("nativeFrame").GetBoolean(), saved.ToString());
		Assert.Contains("label1", saved.GetProperty("elementIds").EnumerateArray().Select(id => id.GetString()));
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_LibadwaitaToolbarView_PlacesContentAndBars()
	{
		// AdwToolbarView's GtkBuildable form is <child type="top|bottom"> for bars and a single
		// content property; a drop plans the top third as a top bar, the bottom third as a bottom
		// bar and the middle as the content.
		var path = Path.Combine(workDir, "Windows", "ToolbarWindow.ui");
		await File.WriteAllTextAsync(path, """
			<?xml version="1.0" encoding="UTF-8"?>
			<interface>
			  <requires lib="gtk" version="4.0" />
			  <requires lib="libadwaita" version="1.0" />
			  <object class="AdwWindow" id="toolbarWindow">
			    <property name="default-width">400</property>
			    <property name="default-height">300</property>
			    <property name="content"><object class="AdwToolbarView" id="toolbar" /></property>
			  </object>
			</interface>
			""", TestContext.Current.CancellationToken);
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", path); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("toolbarWindow"); Assert.True(status.GetProperty("nativeFrame").GetBoolean(), status.ToString());

		// A toolbox click on the empty toolbar view fills its single content slot.
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "toolbar")).GetProperty("success").GetBoolean());
		var body = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkLabel");
		Assert.True(body.GetProperty("success").GetBoolean(), body.ToString());
		Assert.Equal("label1", body.GetProperty("selectedId").GetString());

		// A canvas drop in the top third becomes a top bar.
		var bounds = await app.InvokeAsync("od.gtk-designer.bounds", "toolbar");
		Assert.True(bounds.GetProperty("success").GetBoolean(), bounds.ToString());
		var x = bounds.GetProperty("x").GetDouble() + bounds.GetProperty("width").GetDouble() / 2;
		var y = bounds.GetProperty("y").GetDouble() + bounds.GetProperty("height").GetDouble() * 0.1;
		var plan = await app.InvokeAsync("od.gtk-designer.drop-plan", x, y);
		Assert.Equal("toolbar", plan.GetProperty("containerId").GetString());
		Assert.Equal(0, plan.GetProperty("index").GetInt32());
		var bar = await app.InvokeAsync("od.gtk-designer.toolbox.drop-at", "GtkButton", x, y);
		Assert.True(bar.GetProperty("success").GetBoolean(), bar.ToString());
		Assert.Equal("button1", bar.GetProperty("selectedId").GetString());

		// Only one content widget: a second middle drop is refused (a toolbox click would add a
		// top bar instead, which is the documented fallback once the content slot is filled).
		var middleY = bounds.GetProperty("y").GetDouble() + bounds.GetProperty("height").GetDouble() * 0.5;
		var middlePlan = await app.InvokeAsync("od.gtk-designer.drop-plan", x, middleY);
		Assert.Equal(2, middlePlan.GetProperty("index").GetInt32());
		var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => app.InvokeAsync("od.gtk-designer.toolbox.drop-at", "GtkLabel", x, middleY));
		Assert.Contains("one content widget", refused.Message);

		Assert.True((await app.InvokeAsync("od.file.save", path)).GetProperty("success").GetBoolean());
		var xml = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
		Assert.Matches("<property name=\"content\">\\s*<object class=\"GtkLabel\" id=\"label1\">", xml);
		Assert.Matches("<child type=\"top\">\\s*<object class=\"GtkButton\" id=\"button1\">", xml);
		// gtk4-builder-tool never loads libadwaita, so reopen and require a native frame instead.
		await app.InvokeAsync("od.close-active-view");
		var reopened = await app.InvokeAsync("od.open-file", path); Assert.True(reopened.GetProperty("opened").GetBoolean(), reopened.ToString());
		var saved = await WaitAsync("toolbarWindow"); Assert.True(saved.GetProperty("nativeFrame").GetBoolean(), saved.ToString());
		Assert.Equal(4, saved.GetProperty("elementCount").GetInt32());
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_Libadwaita_IsUsedOnlyWhenTheDocumentRequiresIt()
	{
		// Libadwaita is a separate capability: a document that declares <requires lib="libadwaita">
		// previews real Adw widgets with typed properties and gets the Libadwaita toolbox; the same
		// widget in a GTK-only document is not silently loaded.
		const string body = """
			  <object class="AdwWindow" id="adwWindow">
			    <property name="content">
			      <object class="AdwStatusPage" id="statusPage">
			        <property name="title">All set</property>
			        <property name="description">Nothing to do</property>
			      </object>
			    </property>
			  </object>
			</interface>
			""";
		var adwPath = Path.Combine(workDir, "Windows", "AdwWindow.ui");
		await File.WriteAllTextAsync(adwPath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<interface>\n  <requires lib=\"gtk\" version=\"4.0\" />\n  <requires lib=\"libadwaita\" version=\"1.0\" />\n" + body, TestContext.Current.CancellationToken);
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", adwPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("adwWindow");
		Assert.True(status.GetProperty("nativeFrame").GetBoolean(), status.ToString());
		Assert.Contains("statusPage", status.GetProperty("elementIds").EnumerateArray().Select(e => e.GetString()));
		Assert.DoesNotContain("placeholder", status.GetProperty("diagnostics").ToString());
		Assert.True(status.GetProperty("toolboxItemCount").GetInt32() > GtkToolCount, "the Libadwaita toolbox items are missing: " + status);
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "statusPage")).GetProperty("success").GetBoolean());
		var items = (await app.InvokeAsync("od.gtk-designer.properties.describe")).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!, i => i);
		Assert.Equal("All set", items["Title"].GetProperty("value").GetString());
		Assert.Equal("AdwStatusPage", items["Description"].GetProperty("category").GetString());
		await app.InvokeAsync("od.close-active-view");

		var gtkOnlyPath = Path.Combine(workDir, "Windows", "GtkOnlyWindow.ui");
		await File.WriteAllTextAsync(gtkOnlyPath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<interface>\n  <requires lib=\"gtk\" version=\"4.0\" />\n" + body.Replace("adwWindow", "gtkOnlyWindow").Replace("AdwWindow", "GtkWindow").Replace("\"content\"", "\"child\""), TestContext.Current.CancellationToken);
		var openedGtk = await app.InvokeAsync("od.open-file", gtkOnlyPath); Assert.True(openedGtk.GetProperty("opened").GetBoolean(), openedGtk.ToString());
		var gtkStatus = await WaitAsync("gtkOnlyWindow");
		Assert.Contains("does not declare", gtkStatus.GetProperty("diagnostics").ToString());
		Assert.Equal(GtkToolCount, gtkStatus.GetProperty("toolboxItemCount").GetInt32());
		await app.InvokeAsync("od.close-active-view");
	}

	[Fact]
	public async Task GtkDesigner_RelativeImageFile_PreviewsFromTheUiFolder()
	{
		// GtkBuilder resolves a relative file against the .ui's folder; the preview must too, or every
		// project image shows as missing. A 43x30 PNG beside the .ui must give the picture its size.
		var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(app.OpenDevelopProjectPath)!, "..", "..", ".."));
		Directory.CreateDirectory(Path.Combine(workDir, "Windows", "images"));
		File.Copy(Path.Combine(repo, "data", "resources", "languages", "brazil.png"), Path.Combine(workDir, "Windows", "images", "flag.png"));
		var picturePath = Path.Combine(workDir, "Windows", "PictureWindow.ui");
		await File.WriteAllTextAsync(picturePath, """
			<?xml version="1.0" encoding="UTF-8"?>
			<interface>
			  <requires lib="gtk" version="4.0" />
			  <object class="GtkWindow" id="pictureWindow">
			    <child>
			      <object class="GtkBox" id="pictures">
			        <child>
			          <object class="GtkPicture" id="flag">
			            <property name="file">images/flag.png</property>
			            <property name="can-shrink">False</property>
			            <property name="halign">start</property>
			            <property name="valign">start</property>
			          </object>
			        </child>
			      </object>
			    </child>
			  </object>
			</interface>
			""", TestContext.Current.CancellationToken);
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", picturePath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync("pictureWindow");
		Assert.DoesNotContain("not previewed", status.GetProperty("diagnostics").ToString());
		var bounds = await app.InvokeAsync("od.gtk-designer.bounds", "flag");
		Assert.True(bounds.GetProperty("width").GetDouble() >= 43 && bounds.GetProperty("height").GetDouble() >= 30, "the relative image was not loaded: " + bounds);
		Assert.Contains("<property name=\"file\">images/flag.png</property>", await File.ReadAllTextAsync(picturePath, TestContext.Current.CancellationToken));
		await app.InvokeAsync("od.close-active-view");
	}

	/// <summary>The GTK 4 toolbox size (GtkDesignerViewContent.ToolNames).</summary>
	const int GtkToolCount = 22;

	[Fact]
	public async Task GtkDesigner_DragToolboxItemOntoNativeDesignSurface_InsertsAndPersistsControl()
	{
		// Companion to WPF's DragToolboxItem_OntoDesignSurface_InsertsAndPersistsControl and
		// WinUI's *_DragToolboxItemOntoDesignSurface_* (AddInTests.cs): drives a REAL synthetic
		// mouse drag (press/drag-move/release) from the shared Tools pad onto the GTK host's
		// rendered native preview, exercising GtkDesignerViewContent's own DragDrop.DoDragDrop
		// wiring (toolbox.PreviewMouseMove -> the "hits" overlay canvas's Drop handler) - the
		// existing od.gtk-designer.toolbox.insert-based test only covers the API shortcut.
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", uiPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync();
		Assert.True(status.GetProperty("nativeFrame").GetBoolean(), status.ToString());
		var elementCountBefore = status.GetProperty("elementCount").GetInt32();

		// See DragToolboxItem_OntoDesignSurface_InsertsAndPersistsControl's own comment on why
		// the Tools pad needs an explicit show first, and why activation is repeated per attempt.
		await app.InvokeAsync("od.show-pad", "Tools");
		await app.InvokeAsync("od.activate");
		await app.InvokeAsync("od.gtk-designer.fit");

		// Target a real LEAF element (runButton), not a container whose own native bounds can
		// tie exactly with an ancestor's (e.g. contentBox vs mainWindow both reporting the full
		// 800x600 frame here) - NativeNodeAt's tie-break on equal area is order-dependent and
		// made an early version of this test flaky at the coordinate-resolution step, independent
		// of the drag/drop mechanics themselves.
		JsonElement statusAfterDrop = default;
		var grew = false; var pointerEvents = "";
		for (int attempt = 1; attempt <= 4 && !grew; attempt++) {
			await app.InvokeAsync("od.activate");
			// Read both points after activation, and only once two samples agree: showing the Tools pad
			// and activating the window re-lay it out, and a toolbox point read before that settles lands
			// on a neighbouring item - the drop then inserts a CheckButton instead of the Switch.
			var (fromX, fromY) = await StableCenterAsync("od.gtk-designer.toolbox.query-item-bounds", "GtkSwitch");
			var (toX, toY) = await StableCenterAsync("od.gtk-designer.query-element-screen-bounds", "runButton");
			var activated = await app.InvokeAsync("od.activate");
			await app.InvokeAsync("od.pointer-events", true);
			var pressed = await app.PressPointerAsync(fromX, fromY); Assert.True(pressed.GetProperty("ok").GetBoolean(), pressed.ToString());
			pointerEvents = (await app.InvokeAsync("od.pointer-events", false)).ToString() + " pressed at " + fromX + "," + fromY + " after " + activated;
			for (int step = 1; step <= 6; step++) {
				var t = step / 6.0;
				var moved = await app.DragMovePointerAsync(fromX + (toX - fromX) * t, fromY + (toY - fromY) * t);
				Assert.True(moved.GetProperty("ok").GetBoolean(), moved.ToString());
				await Task.Delay(150, TestContext.Current.CancellationToken);
			}
			var released = await app.ReleasePointerAsync(toX, toY); Assert.True(released.GetProperty("ok").GetBoolean(), released.ToString());

			grew = await OpenDevelopAppFixture.PollUntilAsync(async () => {
				statusAfterDrop = await app.InvokeAsync("od.gtk-designer.status");
				return statusAfterDrop.GetProperty("elementCount").GetInt32() > elementCountBefore;
			}, TimeSpan.FromSeconds(8), initialDelayMs: 50, maxDelayMs: 250);
		}
		Assert.True(grew, "Expected elementCount to grow after the drag-drop, even after retries.\nBefore: " + elementCountBefore + "\nPointer: " + pointerEvents + "\nAfter: " + statusAfterDrop);

		var saved = await app.InvokeAsync("od.file.save", uiPath); Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
		var xml = await File.ReadAllTextAsync(uiPath, TestContext.Current.CancellationToken);
		Assert.True(xml.Contains("class=\"GtkSwitch\"", StringComparison.Ordinal),
			"Dropped GtkSwitch not persisted.\nstatusAfterDrop=" + statusAfterDrop + "\nsaved xml:\n" + xml);
		await ValidateGtkBuilderAsync(uiPath);
	}

	async Task<(double X, double Y)> StableCenterAsync(string action, string name)
	{
		(double X, double Y)? previous = null;
		for (int sample = 0; sample < 20; sample++) {
			var bounds = await app.InvokeAsync(action, name);
			Assert.True(bounds.GetProperty("success").GetBoolean(), bounds.ToString());
			var current = (bounds.GetProperty("centerX").GetDouble(), bounds.GetProperty("centerY").GetDouble());
			if (previous == current) return current;
			previous = current;
			await Task.Delay(150, TestContext.Current.CancellationToken);
		}
		Assert.Fail(action + " " + name + " never reported the same bounds twice in a row; last " + previous);
		return default;
	}
	async Task<JsonElement> WaitAsync(string? rootId = null)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20); JsonElement last = default;
		while (DateTime.UtcNow < deadline) { last = await app.InvokeAsync("od.gtk-designer.status"); if (last.TryGetProperty("active", out var active) && active.GetBoolean() && last.GetProperty("toolboxHosted").GetBoolean() && last.GetProperty("outlineHosted").GetBoolean() && last.GetProperty("nativeFrame").GetBoolean() && (rootId == null || last.TryGetProperty("rootId", out var actualRoot) && actualRoot.GetString() == rootId)) return last; await Task.Delay(100, TestContext.Current.CancellationToken); }
		return last;
	}
	/// <summary>The host's frame arriving is not the canvas showing it: a frame can sit in the surface while its
	/// tab is still hidden. Waits (bounded) for the canvas itself to display it, so a design that never becomes
	/// visible fails with the canvas's own state instead of passing on backend data alone.</summary>
	async Task<JsonElement> WaitForCanvasFrameAsync(JsonElement last)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
		while (!(last.TryGetProperty("canvasFrameShown", out var shown) && shown.GetBoolean()) && DateTime.UtcNow < deadline) { await Task.Delay(100, TestContext.Current.CancellationToken); last = await app.InvokeAsync("od.gtk-designer.status"); }
		return last;
	}
	async Task<JsonElement> WaitForPropertyAdapterAsync()
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5); JsonElement last = default;
		while (DateTime.UtcNow < deadline) {
			last = await app.InvokeAsync("od.gtk-designer.status");
			if ((last.GetProperty("propertyPadSelectedType").GetString() ?? "").Contains("GtkPropertyAdapter", StringComparison.Ordinal)) return last;
			await Task.Delay(50, TestContext.Current.CancellationToken);
		}
		return last;
	}
	async Task<JsonElement> WaitForProjectBrowserAsync()
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10); JsonElement last = default;
		while (DateTime.UtcNow < deadline) {
			last = await app.InvokeAsync("od.project-browser-state", "GtkDesignerFixture");
			if (last.GetProperty("success").GetBoolean() && FindProjectBrowserNode(last.GetProperty("project"), "MainWindow.cs").ValueKind != JsonValueKind.Undefined) return last;
			await Task.Delay(100, TestContext.Current.CancellationToken);
		}
		return last;
	}
	async Task<JsonElement> WaitForFrameChangeAsync(string? previous)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20); JsonElement last = default;
		while (DateTime.UtcNow < deadline) { last = await app.InvokeAsync("od.gtk-designer.status"); if (last.GetProperty("nativeFrame").GetBoolean() && last.GetProperty("nativeFrameFingerprint").GetString() != previous) return last; await Task.Delay(100, TestContext.Current.CancellationToken); }
		return last;
	}
	async Task<JsonElement> WaitForHostChangeAsync(int previousPid, string rootId)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30); JsonElement last = default;
		while (DateTime.UtcNow < deadline) { last = await app.InvokeAsync("od.gtk-designer.status"); if (last.TryGetProperty("active", out var active) && active.GetBoolean() && last.GetProperty("hostProcessId").GetInt32() != previousPid && last.GetProperty("rootId").GetString() == rootId && last.GetProperty("nativeFrame").GetBoolean()) return last; await Task.Delay(100, TestContext.Current.CancellationToken); }
		return last;
	}
	static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		static bool IsBuildOutput(string relative) => relative.Split(Path.DirectorySeparatorChar)[0] is "bin" or "obj";
		foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) {
			var relative = Path.GetRelativePath(source, directory); if (!IsBuildOutput(relative)) Directory.CreateDirectory(Path.Combine(destination, relative));
		}
		foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
			var relative = Path.GetRelativePath(source, file); if (!IsBuildOutput(relative)) File.Copy(file, Path.Combine(destination, relative));
		}
	}
	static JsonElement FindProjectBrowserNode(JsonElement node, string name)
	{
		if (string.Equals(node.GetProperty("name").GetString(), name, StringComparison.Ordinal)) return node;
		foreach (var child in node.GetProperty("children").EnumerateArray()) {
			var found = FindProjectBrowserNode(child, name);
			if (found.ValueKind != JsonValueKind.Undefined) return found;
		}
		return default;
	}

	static async Task ValidateGtkBuilderAsync(string path)
	{
		var start = new System.Diagnostics.ProcessStartInfo(GtkBuilderTool()) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
		start.ArgumentList.Add("validate"); start.ArgumentList.Add(path);
		using var process = System.Diagnostics.Process.Start(start)!; var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken); await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		Assert.True(process.ExitCode == 0, "gtk4-builder-tool rejected saved UI: " + error);
	}
	/// <summary>gtk4-builder-tool from PATH, or on Windows from the GTK install the IDE itself
	/// uses (GtkRuntimeLocator: GTK4_ROOT, then the MSYS2 environment for this architecture),
	/// which is usually not on PATH.</summary>
	static string GtkBuilderTool()
	{
		if (!OperatingSystem.IsWindows()) return "gtk4-builder-tool";
		var environment = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
			? new[] { "clangarm64" } : new[] { "ucrt64", "mingw64", "clang64" };
		var roots = new[] { Environment.GetEnvironmentVariable("GTK4_ROOT") }
			.Concat(new[] { Environment.GetEnvironmentVariable("MSYS2_ROOT"), @"C:\msys64", @"C:\tools\msys64" }
				.Where(r => !string.IsNullOrEmpty(r)).SelectMany(r => environment.Select(e => Path.Combine(r!, e))))
			.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.GetDirectoryName(p.TrimEnd('\\')) ?? p));
		foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
			foreach (var candidate in new[] { Path.Combine(root!, "bin", "gtk4-builder-tool.exe"), Path.Combine(root!, "gtk4-builder-tool.exe") })
				if (File.Exists(candidate)) return candidate;
		return "gtk4-builder-tool";
	}

	/// <summary>Runs the built fixture with --smoke-test (load the UI, wire signals, quit), with the
	/// GTK runtime the IDE uses on PATH.</summary>
	static async Task RunFixtureSmokeTestAsync(string projectPath)
	{
		var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
		start.ArgumentList.Add("run"); start.ArgumentList.Add("--no-build"); start.ArgumentList.Add("--project"); start.ArgumentList.Add(projectPath); start.ArgumentList.Add("--"); start.ArgumentList.Add("--smoke-test");
		var tool = GtkBuilderTool();
		if (Path.IsPathRooted(tool)) start.Environment["PATH"] = Path.GetDirectoryName(tool) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
		using var process = System.Diagnostics.Process.Start(start)!;
		var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken); var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		var output = await outputTask; var error = await errorTask;
		Assert.True(process.ExitCode == 0 && output.Contains("smoke test passed", StringComparison.Ordinal), "GTK fixture did not start after saved edits:\n" + output + error + "\noutput directory:\n" + DescribeFixtureOutput(projectPath) + "\nproject:\n" + File.ReadAllText(projectPath));
	}

	/// <summary>The path with every symbolic link along it resolved.</summary>
	static string RealPath(string path)
	{
		var full = Path.GetFullPath(path);
		var resolved = Path.GetPathRoot(full)!;
		foreach (var part in full.Substring(resolved.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
			resolved = Path.Combine(resolved, part);
			if (new DirectoryInfo(resolved).LinkTarget is { } target)
				resolved = Path.GetFullPath(target, Path.GetDirectoryName(resolved)!);
		}
		return resolved;
	}
	static string DescribeFixtureOutput(string projectPath)
	{
		var bin = Path.Combine(Path.GetDirectoryName(projectPath)!, "bin");
		return Directory.Exists(bin)
			? string.Join("\n", Directory.EnumerateFiles(bin, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".dll", StringComparison.Ordinal)).Select(f => Path.GetRelativePath(bin, f)))
			: "(no bin directory)";
	}
	static async Task ValidateFixtureBuildAsync(string projectPath)
	{
		var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
		start.ArgumentList.Add("build"); start.ArgumentList.Add(projectPath); start.ArgumentList.Add("--nologo"); start.ArgumentList.Add("-v:q");
		using var process = System.Diagnostics.Process.Start(start)!; var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken); var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken); await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		var output = await outputTask; var error = await errorTask; Assert.True(process.ExitCode == 0, "GTK designer fixture no longer compiles after saved edits:\n" + output + error);
	}
	static async Task AssertNoRenderChildrenAsync(int hostProcessId)
	{
		if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
		var start = new System.Diagnostics.ProcessStartInfo("ps") { RedirectStandardOutput = true, UseShellExecute = false };
		start.ArgumentList.Add("-axo"); start.ArgumentList.Add("ppid=,command=");
		using var process = System.Diagnostics.Process.Start(start)!; var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken); await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		var children = output.Split('\n').Where(line => line.TrimStart().StartsWith(hostProcessId.ToString() + " ", StringComparison.Ordinal)).ToArray();
		Assert.DoesNotContain(children, line => line.Contains("gtk4-builder-tool", StringComparison.Ordinal) || line.Contains("GtkRenderHelper", StringComparison.Ordinal));
	}
	public async ValueTask DisposeAsync()
	{
		// Close the documents first: the app outlives this class, and a tab still open on a deleted
		// .ui fails the next time the workbench initializes its views.
		try { await app.InvokeAsync("od.close-all-document-views"); } catch { }
		try { if (Environment.GetEnvironmentVariable("OD_KEEP_GTK_WORKDIR") != "1") Directory.Delete(workDir, true); } catch { }
	}
}
