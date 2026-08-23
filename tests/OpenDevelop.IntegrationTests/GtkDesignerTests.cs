using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("30 Add-ins and specialized fixtures")]
public sealed class GtkDesignerTests : IAsyncDisposable
{
	readonly OpenDevelopAppFixture app; readonly string workDir; readonly string projectPath; readonly string uiPath; readonly string settingsUiPath;
	public GtkDesignerTests(OpenDevelopAppFixture app)
	{
		this.app = app;
		var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(app.OpenDevelopProjectPath)!, "..", "..", ".."));
		var fixture = Path.Combine(repo, "tests", "fixtures", "GtkDesignerFixture");
		workDir = Path.Combine(Path.GetTempPath(), "GtkDesignerTests-" + Guid.NewGuid().ToString("N"));
		CopyDirectory(fixture, workDir); projectPath = Path.Combine(workDir, "GtkDesignerFixture.csproj"); uiPath = Path.Combine(workDir, "Windows", "MainWindow.ui"); settingsUiPath = Path.Combine(workDir, "Windows", "SettingsWindow.ui");
	}

	[Fact]
	public async Task GtkDesigner_RealPadsPropertyEditToolboxHistoryAndSave()
	{
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", uiPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync();
		Assert.True(status.GetProperty("active").GetBoolean(), status.ToString());
		Assert.True(status.GetProperty("hostProcessId").GetInt32() > 0, "GTK designer did not start its isolated host: " + status);
		Assert.True(status.GetProperty("nativeFrame").GetBoolean(), "GTK host did not return a native GTK frame: " + status);
		Assert.Equal("in-process GSK/Cairo", status.GetProperty("nativeRenderer").GetString());
		Assert.DoesNotContain("GtkRenderHelper", status.GetProperty("hostLog").GetString() ?? "", StringComparison.Ordinal);
		Assert.DoesNotContain("gtk4-builder-tool", status.GetProperty("hostLog").GetString() ?? "", StringComparison.Ordinal);
		await AssertNoRenderChildrenAsync(status.GetProperty("hostProcessId").GetInt32());
		Assert.True(status.GetProperty("nativeFrameWidth").GetInt32() > 0 && status.GetProperty("nativeFrameHeight").GetInt32() > 0, status.ToString());
		var originalFrame = status.GetProperty("nativeFrameFingerprint").GetString(); Assert.False(string.IsNullOrEmpty(originalFrame));
		Assert.Equal(status.GetProperty("elementCount").GetInt32(), status.GetProperty("nativeBoundsCount").GetInt32());
		var runBounds = await app.InvokeAsync("od.gtk-designer.bounds", "runButton"); Assert.True(runBounds.GetProperty("success").GetBoolean(), runBounds.ToString());
		var nativeHit = await app.InvokeAsync("od.gtk-designer.hit-test", runBounds.GetProperty("x").GetDouble() + runBounds.GetProperty("width").GetDouble() / 2, runBounds.GetProperty("y").GetDouble() + runBounds.GetProperty("height").GetDouble() / 2);
		Assert.True(nativeHit.GetProperty("success").GetBoolean(), nativeHit.ToString()); Assert.Equal("runButton", nativeHit.GetProperty("selectedId").GetString());
		Assert.True(status.GetProperty("toolboxHosted").GetBoolean(), "The real Tools pad did not host the GTK toolbox: " + status);
		Assert.True(status.GetProperty("outlineHosted").GetBoolean(), "The real Outline pad did not host the GTK tree: " + status);
		Assert.Equal(status.GetProperty("elementCount").GetInt32(), status.GetProperty("outlineItemCount").GetInt32());
		Assert.True(status.GetProperty("toolboxItemCount").GetInt32() >= 15, status.ToString());
		Assert.Equal(3, status.GetProperty("toolbarItemCount").GetInt32());
		Assert.Equal(new[] { "Zoom", "Fit", "Gridlines" }, status.GetProperty("toolbarItems").EnumerateArray().Select(x => x.GetString()).ToArray());
		var zoomed = await app.InvokeAsync("od.gtk-designer.zoom", 1.5); Assert.Equal(1.5, zoomed.GetProperty("zoom").GetDouble());
		var fitted = await app.InvokeAsync("od.gtk-designer.fit"); Assert.True(fitted.GetProperty("measured").GetBoolean(), fitted.ToString()); Assert.InRange(fitted.GetProperty("zoom").GetDouble(), .25, 2);
		var gridOn = await app.InvokeAsync("od.gtk-designer.gridlines", true); Assert.True(gridOn.GetProperty("gridlines").GetBoolean());
		status = await app.InvokeAsync("od.gtk-designer.status"); Assert.True(status.GetProperty("gridlines").GetBoolean());
		var gridOff = await app.InvokeAsync("od.gtk-designer.gridlines", false); Assert.False(gridOff.GetProperty("gridlines").GetBoolean());

		var selected = await app.InvokeAsync("od.gtk-designer.select", "runButton"); Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
		Assert.Contains("GtkPropertyAdapter", selected.GetProperty("propertyPadSelectedType").GetString());
		status = await app.InvokeAsync("od.gtk-designer.status"); Assert.True(status.GetProperty("propertyPadPropertyCount").GetInt32() > 0, status.ToString());
		var edited = await app.InvokeAsync("od.gtk-designer.properties.edit", "Label", "Execute"); Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
		status = await WaitForFrameChangeAsync(originalFrame); Assert.NotEqual(originalFrame, status.GetProperty("nativeFrameFingerprint").GetString());
		var signal = await app.InvokeAsync("od.gtk-designer.signal.set", "clicked", "OnRunClicked"); Assert.True(signal.GetProperty("success").GetBoolean(), signal.ToString());
		var reordered = await app.InvokeAsync("od.gtk-designer.pointer-reorder", "runButton", "heading"); Assert.True(reordered.GetProperty("success").GetBoolean(), reordered.ToString());
		var restarted = await app.InvokeAsync("od.gtk-designer.restart-host");
		Assert.True(restarted.GetProperty("success").GetBoolean(), restarted.ToString());
		Assert.NotEqual(restarted.GetProperty("oldHostProcessId").GetInt32(), restarted.GetProperty("hostProcessId").GetInt32());
		var refreshed = await app.InvokeAsync("od.gtk-designer.refresh"); Assert.True(refreshed.GetProperty("success").GetBoolean(), refreshed.ToString());

		await app.InvokeAsync("od.gtk-designer.select", "contentBox");
		var inserted = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkEntry"); Assert.True(inserted.GetProperty("success").GetBoolean(), inserted.ToString());
		Assert.Equal("entry1", inserted.GetProperty("selectedId").GetString());
		var undo = await app.InvokeAsync("od.gtk-designer.undo"); Assert.Equal(4, undo.GetProperty("elementCount").GetInt32());
		var redo = await app.InvokeAsync("od.gtk-designer.redo"); Assert.Equal(5, redo.GetProperty("elementCount").GetInt32());
		var deleted = await app.InvokeAsync("od.gtk-designer.delete"); Assert.True(deleted.GetProperty("success").GetBoolean(), deleted.ToString()); Assert.Equal(4, deleted.GetProperty("elementCount").GetInt32());
		var undoDelete = await app.InvokeAsync("od.gtk-designer.undo"); Assert.Equal(5, undoDelete.GetProperty("elementCount").GetInt32());
		var redoDelete = await app.InvokeAsync("od.gtk-designer.redo"); Assert.Equal(4, redoDelete.GetProperty("elementCount").GetInt32());
		var restoreDeleted = await app.InvokeAsync("od.gtk-designer.undo"); Assert.Equal(5, restoreDeleted.GetProperty("elementCount").GetInt32());
		var saved = await app.InvokeAsync("od.file.save", uiPath); Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
		var xml = await File.ReadAllTextAsync(uiPath, TestContext.Current.CancellationToken);
		Assert.Contains(">Execute</property>", xml); Assert.Contains("<signal name=\"clicked\" handler=\"OnRunClicked\"", xml); Assert.Contains("class=\"GtkEntry\"", xml); Assert.Contains("id=\"entry1\"", xml);
		Assert.True(xml.IndexOf("id=\"runButton\"", StringComparison.Ordinal) < xml.IndexOf("id=\"heading\"", StringComparison.Ordinal), "GTK reorder was not persisted: " + xml);
		await ValidateGtkBuilderAsync(uiPath);

		var closed = await app.InvokeAsync("od.close-active-view"); Assert.True(closed.GetProperty("success").GetBoolean(), closed.ToString());
		var reopened = await app.InvokeAsync("od.open-file", uiPath); Assert.True(reopened.GetProperty("opened").GetBoolean(), reopened.ToString());
		var reopenedStatus = await WaitAsync(); Assert.Equal(5, reopenedStatus.GetProperty("elementCount").GetInt32()); Assert.True(reopenedStatus.GetProperty("nativeFrame").GetBoolean(), reopenedStatus.ToString());
		var reselected = await app.InvokeAsync("od.gtk-designer.select", "entry1"); Assert.True(reselected.GetProperty("success").GetBoolean(), reselected.ToString()); Assert.Contains("GtkPropertyAdapter", reselected.GetProperty("propertyPadSelectedType").GetString());

		var mainHostProcessId = reopenedStatus.GetProperty("hostProcessId").GetInt32();
		var mainDocumentId = reopenedStatus.GetProperty("hostDocumentId").GetString(); Assert.False(string.IsNullOrEmpty(mainDocumentId));
		var openedSettings = await app.InvokeAsync("od.open-file", settingsUiPath); Assert.True(openedSettings.GetProperty("opened").GetBoolean(), openedSettings.ToString());
		var settingsStatus = await WaitAsync("settingsWindow"); Assert.Equal(mainHostProcessId, settingsStatus.GetProperty("hostProcessId").GetInt32()); Assert.Equal(4, settingsStatus.GetProperty("elementCount").GetInt32());
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
		var mainSelectionAgain = await app.InvokeAsync("od.gtk-designer.select", "entry1"); Assert.True(mainSelectionAgain.GetProperty("success").GetBoolean(), mainSelectionAgain.ToString());
		await ValidateFixtureBuildAsync(projectPath);
	}

	async Task<JsonElement> WaitAsync(string? rootId = null)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20); JsonElement last = default;
		while (DateTime.UtcNow < deadline) { last = await app.InvokeAsync("od.gtk-designer.status"); if (last.TryGetProperty("active", out var active) && active.GetBoolean() && last.GetProperty("toolboxHosted").GetBoolean() && last.GetProperty("outlineHosted").GetBoolean() && last.GetProperty("nativeFrame").GetBoolean() && (rootId == null || last.TryGetProperty("rootId", out var actualRoot) && actualRoot.GetString() == rootId)) return last; await Task.Delay(100, TestContext.Current.CancellationToken); }
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
	static async Task ValidateGtkBuilderAsync(string path)
	{
		var start = new System.Diagnostics.ProcessStartInfo("gtk4-builder-tool") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
		start.ArgumentList.Add("validate"); start.ArgumentList.Add(path);
		using var process = System.Diagnostics.Process.Start(start)!; var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken); await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		Assert.True(process.ExitCode == 0, "gtk4-builder-tool rejected saved UI: " + error);
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
	public ValueTask DisposeAsync() { try { Directory.Delete(workDir, true); } catch { } return ValueTask.CompletedTask; }
}
