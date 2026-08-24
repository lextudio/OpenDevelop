using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("30 Add-ins and specialized fixtures")]
public sealed class MewUIDesignerTests : IAsyncDisposable
{
	readonly OpenDevelopAppFixture app;
	readonly string workDir;
	readonly string projectPath;
	readonly string sourcePath;
	readonly string designerPath;
	readonly string settingsSourcePath;

	public MewUIDesignerTests(OpenDevelopAppFixture app)
	{
		this.app = app;
		var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(app.OpenDevelopProjectPath)!, "..", "..", ".."));
		var fixture = Path.Combine(repo, "tests", "fixtures", "MewUIFixture");
		workDir = Path.Combine(Path.GetTempPath(), "MewUIDesignerTests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(workDir);
		foreach (var directory in Directory.EnumerateDirectories(fixture, "*", SearchOption.AllDirectories)) {
			if (directory.Contains(Path.DirectorySeparatorChar + "bin") || directory.Contains(Path.DirectorySeparatorChar + "obj")) continue;
			Directory.CreateDirectory(Path.Combine(workDir, Path.GetRelativePath(fixture, directory)));
		}
		foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)) {
			var relative = Path.GetRelativePath(fixture, file);
			if (relative.StartsWith("bin" + Path.DirectorySeparatorChar) || relative.StartsWith("obj" + Path.DirectorySeparatorChar)) continue;
			var destination = Path.Combine(workDir, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			File.Copy(file, destination);
		}
		projectPath = Path.Combine(workDir, "MewUIFixture.csproj");
		sourcePath = Path.Combine(workDir, "Windows", "MainWindow.mxaml.cs");
		designerPath = Path.Combine(workDir, "Windows", "MainWindow.mxaml");
		settingsSourcePath = Path.Combine(workDir, "Windows", "SettingsWindow.cs");
	}

	[Fact]
	public async Task MewUIDesigner_SourceBackedEditUndoRedoAndSave()
	{
		var openedProject = await app.ReopenSolutionAsync(projectPath);
		Assert.True(openedProject.GetProperty("success").GetBoolean(), openedProject.ToString());
		var opened = await app.InvokeAsync("od.open-file", designerPath);
		Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());

		var status = await WaitForDesignerAsync();
		Assert.True(status.GetProperty("active").GetBoolean(), status.ToString());
		Assert.True(status.GetProperty("hostProcessId").GetInt32() > 0, "MewUI designer did not start its isolated host: " + status);
		Assert.True(status.GetProperty("toolboxHosted").GetBoolean(), "The real Tools pad did not host the MewUI toolbox: " + status);
		Assert.True(status.GetProperty("outlineHosted").GetBoolean(), "The real Outline pad did not host the MewUI tree: " + status);
		Assert.Equal(status.GetProperty("elementCount").GetInt32(), status.GetProperty("outlineItemCount").GetInt32());
		Assert.True(status.GetProperty("toolboxItemCount").GetInt32() >= 16, status.ToString());
		Assert.Equal(3, status.GetProperty("toolbarItemCount").GetInt32());
		Assert.Equal(new[] { "Zoom", "Fit", "Gridlines" }, status.GetProperty("toolbarItems").EnumerateArray().Select(x => x.GetString()).ToArray());
		var zoomed = await app.InvokeAsync("od.mewui-designer.zoom", 1.25); Assert.Equal(1.25, zoomed.GetProperty("zoom").GetDouble());
		var fitted = await app.InvokeAsync("od.mewui-designer.fit"); Assert.True(fitted.GetProperty("measured").GetBoolean(), fitted.ToString()); Assert.InRange(fitted.GetProperty("zoom").GetDouble(), .25, 2);
		var gridOn = await app.InvokeAsync("od.mewui-designer.gridlines", true); Assert.True(gridOn.GetProperty("gridlines").GetBoolean());
		status = await app.InvokeAsync("od.mewui-designer.status"); Assert.True(status.GetProperty("gridlines").GetBoolean());
		var gridOff = await app.InvokeAsync("od.mewui-designer.gridlines", false); Assert.False(gridOff.GetProperty("gridlines").GetBoolean());
		// Window + rootPanel + heading + toolRow + 3 toolbar buttons + nameBox + notificationsCheck
		// + statusList + statusBar + statusText.
		Assert.True(status.GetProperty("elementCount").GetInt32() == 12, "elementCount=" + status.GetProperty("elementCount") + " status=" + status);

		var selected = await app.InvokeAsync("od.mewui-designer.select", "rootPanel");
		Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
		var propertySelection = await app.InvokeAsync("od.mewui-designer.select", "heading");
		Assert.True(propertySelection.GetProperty("success").GetBoolean(), propertySelection.ToString());
		Assert.Contains("MewUIPropertyAdapter", propertySelection.GetProperty("propertyPadSelectedType").GetString());
		status = await app.InvokeAsync("od.mewui-designer.status"); Assert.True(status.GetProperty("propertyPadPropertyCount").GetInt32() > 0, status.ToString());
		var changedProperty = await app.InvokeAsync("od.mewui-designer.set-property", "Text", "Configured");
		Assert.True(changedProperty.GetProperty("success").GetBoolean(), changedProperty.ToString());

		// Insert into a NESTED container (toolRow inside rootPanel), not the root - the fixture is
		// deep enough that "which container receives the child" is part of the contract.
		var selectToolRow = await app.InvokeAsync("od.mewui-designer.select", "toolRow");
		Assert.True(selectToolRow.GetProperty("success").GetBoolean(), selectToolRow.ToString());
		var inserted = await app.InvokeAsync("od.mewui-designer.toolbox.insert", "TextBox");
		Assert.True(inserted.GetProperty("success").GetBoolean(), inserted.ToString());
		Assert.Equal(13, inserted.GetProperty("elementCount").GetInt32());

		var undo = await app.InvokeAsync("od.mewui-designer.undo");
		Assert.Equal(12, undo.GetProperty("elementCount").GetInt32());
		var redo = await app.InvokeAsync("od.mewui-designer.redo");
		Assert.Equal(13, redo.GetProperty("elementCount").GetInt32());
		var reordered = await app.InvokeAsync("od.mewui-designer.reorder", -1);
		Assert.True(reordered.GetProperty("success").GetBoolean(), reordered.ToString());
		var deleted = await app.InvokeAsync("od.mewui-designer.delete"); Assert.True(deleted.GetProperty("success").GetBoolean(), deleted.ToString()); Assert.Equal(12, deleted.GetProperty("elementCount").GetInt32());
		var undoDelete = await app.InvokeAsync("od.mewui-designer.undo"); Assert.Equal(13, undoDelete.GetProperty("elementCount").GetInt32());
		var redoDelete = await app.InvokeAsync("od.mewui-designer.redo"); Assert.Equal(12, redoDelete.GetProperty("elementCount").GetInt32());
		var restoreDeleted = await app.InvokeAsync("od.mewui-designer.undo"); Assert.Equal(13, restoreDeleted.GetProperty("elementCount").GetInt32());

		var saved = await app.InvokeAsync("od.file.save", designerPath);
		Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
		var mxamlContent = await File.ReadAllTextAsync(designerPath, TestContext.Current.CancellationToken);
		Assert.Contains("Name=\"textBox1\"", mxamlContent);
		Assert.Contains("Text=\"Configured\"", mxamlContent);
		// The pre-existing nested status bar must survive edits untouched.
		Assert.Contains("Name=\"statusBar\"", mxamlContent);
				// The behavior (user-owned) file must keep its handlers and gain none of the designer's
		// generated construction code.
		var behavior = await File.ReadAllTextAsync(sourcePath, TestContext.Current.CancellationToken);
		Assert.Contains("SaveButton_Click", behavior);
		Assert.Contains("PreferencesButton_Click", behavior);
		Assert.DoesNotContain("new TextBox", behavior);

		var closed = await app.InvokeAsync("od.close-active-view"); Assert.True(closed.GetProperty("success").GetBoolean(), closed.ToString());
		var reopened = await app.InvokeAsync("od.open-file", designerPath); Assert.True(reopened.GetProperty("opened").GetBoolean(), reopened.ToString());
		var reopenedStatus = await WaitForDesignerAsync(); Assert.Equal(13, reopenedStatus.GetProperty("elementCount").GetInt32());
		var reselectedAfterOpen = await app.InvokeAsync("od.mewui-designer.select", "textBox1"); Assert.True(reselectedAfterOpen.GetProperty("success").GetBoolean(), reselectedAfterOpen.ToString());

		var mainHostProcessId = status.GetProperty("hostProcessId").GetInt32();
		var openedSettings = await app.InvokeAsync("od.open-file", settingsSourcePath);
		Assert.True(openedSettings.GetProperty("opened").GetBoolean(), openedSettings.ToString());
		var settingsStatus = await WaitForDesignerAsync("SettingsWindow");
		Assert.True(settingsStatus.GetProperty("active").GetBoolean(), settingsStatus.ToString());
		Assert.Equal(mainHostProcessId, settingsStatus.GetProperty("hostProcessId").GetInt32());
		Assert.NotEqual(reopenedStatus.GetProperty("hostDocumentId").GetString(), settingsStatus.GetProperty("hostDocumentId").GetString());
		Assert.Equal(2, settingsStatus.GetProperty("activeHostLeases").GetInt32());
		// Settings window: preferences form with GroupBox-nested fields (3 levels deep).
		Assert.True(settingsStatus.GetProperty("elementCount").GetInt32() == 11,
			"elementCount=" + settingsStatus.GetProperty("elementCount") + " status=" + settingsStatus);
		var selectNameBox = await app.InvokeAsync("od.mewui-designer.select", "nameBox");
		Assert.True(selectNameBox.GetProperty("success").GetBoolean(), selectNameBox.ToString());
		var renamed = await app.InvokeAsync("od.mewui-designer.set-property", "$name", "userNameBox");
		Assert.True(renamed.GetProperty("success").GetBoolean(), renamed.ToString());
		var reselected = await app.InvokeAsync("od.mewui-designer.select", "userNameBox");
		Assert.True(reselected.GetProperty("success").GetBoolean(), reselected.ToString());
		var terminated = await app.InvokeAsync("od.mewui-designer.terminate-host");
		Assert.True(terminated.GetProperty("success").GetBoolean(), terminated.ToString());
		settingsStatus = await WaitForHostChangeAsync(mainHostProcessId, "SettingsWindow");
		Assert.True(settingsStatus.GetProperty("hostRecoveryCount").GetInt32() > 0, settingsStatus.ToString());
		var recoveredHostProcessId = settingsStatus.GetProperty("hostProcessId").GetInt32();
		Assert.NotEqual(mainHostProcessId, recoveredHostProcessId);
		var reopenedMain = await app.InvokeAsync("od.open-file", sourcePath);
		Assert.True(reopenedMain.GetProperty("opened").GetBoolean(), reopenedMain.ToString());
		var recoveredMainStatus = await WaitForDesignerAsync("MainWindow");
		Assert.Equal(recoveredHostProcessId, recoveredMainStatus.GetProperty("hostProcessId").GetInt32());
		Assert.True(recoveredMainStatus.GetProperty("hostRecoveryCount").GetInt32() > 0, recoveredMainStatus.ToString());
		Assert.Equal(13, recoveredMainStatus.GetProperty("elementCount").GetInt32());
	}

	[Fact]
	public async Task MewUIDesigner_DragToolboxItemOntoPreviewSurface_InsertsAndPersistsControl()
	{
		// Companion to WPF's/WinUI's/GTK's DragToolboxItem_On*_InsertsAndPersistsControl tests
		// (AddInTests.cs, GtkDesignerTests.cs): drives a REAL synthetic mouse drag from the
		// shared Tools pad onto the MewUI preview surface, exercising the DragDrop.DoDragDrop
		// wiring on MewUIDesignerViewContent's toolbox/Preview() (added alongside this test -
		// previously only click-to-select existed on the preview, and od.mewui-designer.toolbox.insert
		// only covered the API shortcut).
		var openedProject = await app.ReopenSolutionAsync(projectPath);
		Assert.True(openedProject.GetProperty("success").GetBoolean(), openedProject.ToString());
		var opened = await app.InvokeAsync("od.open-file", designerPath);
		Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitForDesignerAsync();
		Assert.True(status.GetProperty("active").GetBoolean(), status.ToString());
		var elementCountBefore = status.GetProperty("elementCount").GetInt32();

		await app.InvokeAsync("od.show-pad", "Tools");
		await app.InvokeAsync("od.activate");

		var toolboxBounds = await app.InvokeAsync("od.mewui-designer.toolbox.query-item-bounds", "CheckBox");
		Assert.True(toolboxBounds.GetProperty("success").GetBoolean(), toolboxBounds.ToString());
		var fromX = toolboxBounds.GetProperty("centerX").GetDouble();
		var fromY = toolboxBounds.GetProperty("centerY").GetDouble();

		// toolRow is a nested StackPanel (inside rootPanel) already holding three buttons - drop
		// onto its own empty trailing space, not dead center, so the hit resolves to toolRow
		// itself rather than one of its existing button children.
		var targetBounds = await app.InvokeAsync("od.mewui-designer.query-element-screen-bounds", "toolRow");
		Assert.True(targetBounds.GetProperty("success").GetBoolean(), targetBounds.ToString());
		var toX = targetBounds.GetProperty("x").GetDouble() + targetBounds.GetProperty("width").GetDouble() - 4;
		var toY = targetBounds.GetProperty("y").GetDouble() + targetBounds.GetProperty("height").GetDouble() / 2;

		JsonElement statusAfterDrop = default;
		var grew = false;
		for (int attempt = 1; attempt <= 4 && !grew; attempt++) {
			await app.InvokeAsync("od.activate");
			var pressed = await app.PressPointerAsync(fromX, fromY); Assert.True(pressed.GetProperty("ok").GetBoolean(), pressed.ToString());
			for (int step = 1; step <= 6; step++) {
				var t = step / 6.0;
				var moved = await app.DragMovePointerAsync(fromX + (toX - fromX) * t, fromY + (toY - fromY) * t);
				Assert.True(moved.GetProperty("ok").GetBoolean(), moved.ToString());
				await Task.Delay(150, TestContext.Current.CancellationToken);
			}
			var released = await app.ReleasePointerAsync(toX, toY); Assert.True(released.GetProperty("ok").GetBoolean(), released.ToString());

			grew = await OpenDevelopAppFixture.PollUntilAsync(async () => {
				statusAfterDrop = await app.InvokeAsync("od.mewui-designer.status");
				return statusAfterDrop.GetProperty("elementCount").GetInt32() > elementCountBefore;
			}, TimeSpan.FromSeconds(8), initialDelayMs: 50, maxDelayMs: 250);
		}
		Assert.True(grew, "Expected elementCount to grow after the drag-drop, even after retries.\nBefore: " + elementCountBefore + "\nAfter: " + statusAfterDrop);

		var saved = await app.InvokeAsync("od.file.save", designerPath); Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
		var mxamlContent = await File.ReadAllTextAsync(designerPath, TestContext.Current.CancellationToken);
		Assert.Contains("<CheckBox ", mxamlContent);
	}

	async Task<JsonElement> WaitForHostChangeAsync(int previousPid, string windowClassName)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30); JsonElement last = default;
		while (DateTime.UtcNow < deadline) { last = await app.InvokeAsync("od.mewui-designer.status"); if (last.TryGetProperty("active", out var active) && active.GetBoolean() && last.GetProperty("hostProcessId").GetInt32() != previousPid && last.GetProperty("windowClassName").GetString() == windowClassName) return last; await Task.Delay(100, TestContext.Current.CancellationToken); }
		return last;
	}

	async Task<JsonElement> WaitForDesignerAsync(string? windowClassName = null)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
		JsonElement last = default;
		while (DateTime.UtcNow < deadline) {
			last = await app.InvokeAsync("od.mewui-designer.status");
			if (last.TryGetProperty("active", out var active) && active.GetBoolean()
				&& last.TryGetProperty("toolboxHosted", out var tools) && tools.GetBoolean()
				&& last.TryGetProperty("outlineHosted", out var outline) && outline.GetBoolean()
				&& (windowClassName is null || last.TryGetProperty("windowClassName", out var className) && className.GetString() == windowClassName)) return last;
			await Task.Delay(100, TestContext.Current.CancellationToken);
		}
		return last;
	}

	public ValueTask DisposeAsync() { try { Directory.Delete(workDir, true); } catch { } return ValueTask.CompletedTask; }
}
