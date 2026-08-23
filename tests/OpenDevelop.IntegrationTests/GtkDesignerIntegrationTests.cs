using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// End-to-end coverage for the GTK 4 out-of-process designer against a realistic GtkBuilder
/// document (nested panes, multiple top-level objects, id-referencing properties). Follows the
/// stability rules in doc/technotes/integration-testing.md: temp-copy fixture, semantic DevFlow
/// actions, persisted-disk assertions.
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class GtkDesignerIntegrationTests : IAsyncDisposable
{
	readonly OpenDevelopAppFixture app;
	readonly string workDir;
	readonly string projectPath;
	readonly string uiPath;

	public GtkDesignerIntegrationTests(OpenDevelopAppFixture app)
	{
		this.app = app;
		var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(app.OpenDevelopProjectPath)!, "..", "..", ".."));
		var fixture = Path.Combine(repo, "tests", "fixtures", "GtkFixture");
		workDir = Path.Combine(Path.GetTempPath(), "GtkDesignerTests-" + Guid.NewGuid().ToString("N"));
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
		projectPath = Path.Combine(workDir, "GtkFixture.slnx");
		uiPath = Path.Combine(workDir, "ui", "mainWindow.ui");
	}

	[Fact]
	public async Task GtkDesigner_NestedSelectEditInsertUndoSave()
	{
		var openedProject = await app.ReopenSolutionAsync(projectPath);
		Assert.True(openedProject.GetProperty("success").GetBoolean(), openedProject.ToString());
		var opened = await app.InvokeAsync("od.open-file", uiPath);
		Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());

		var status = await WaitForDesignerAsync();
		Assert.True(status.GetProperty("active").GetBoolean(), status.ToString());
		Assert.True(status.GetProperty("elementCount").GetInt32() == 17, "GTK host did not render the full document: " + status);
		// Two top-level objects: mainWindow subtree (window+rootBox+heading+mainPane+
		// sidebarScroller+documentList+detailBox+detailTitle+detailEntry+actionBar+cancelButton+
		// applyButton = 12) + settingsDialog subtree (dialog+body+label+entry+check = 5).
		Assert.Equal(17, status.GetProperty("elementCount").GetInt32());

		// Select an element nested three panes deep and edit through the real Properties pad.
		var selectEntry = await app.InvokeAsync("od.gtk-designer.select", "detailEntry");
		Assert.True(selectEntry.GetProperty("success").GetBoolean(), selectEntry.ToString());
		var edited = await app.InvokeAsync("od.gtk-designer.properties.edit", "PlaceholderText", "Type to rename…");
		Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());

		// Insert into the nested action bar container: select it first - insertion targets the
		// selected element (falling back to the root container when nothing is selected).
		var selectBar = await app.InvokeAsync("od.gtk-designer.select", "actionBar");
		Assert.True(selectBar.GetProperty("success").GetBoolean(), selectBar.ToString());
		var insert = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkSwitch");
		if (!insert.TryGetProperty("success", out _))
			throw new Xunit.Sdk.XunitException("insert envelope: " + insert.ToString());
		Assert.True(insert.GetProperty("success").GetBoolean(), insert.ToString());

		// undo/redo responses are Status() payloads (no success field); assert via counts.
		var undo = await app.InvokeAsync("od.gtk-designer.undo");
		Assert.Equal(17, undo.GetProperty("elementCount").GetInt32()); // switch removed
		var redo = await app.InvokeAsync("od.gtk-designer.redo");
		Assert.Equal(18, redo.GetProperty("elementCount").GetInt32()); // switch restored

		var saved = await app.InvokeAsync("od.file.save", uiPath);
		Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
		var savedUi = await File.ReadAllTextAsync(uiPath, TestContext.Current.CancellationToken);
		// The property edit round-trips to disk with its decoded value intact...
		Assert.Contains(">Type to rename…</property>", savedUi);
		// ...and so does the inserted switch (wherever the host placed it under actionBar's subtree).
		Assert.Contains("GtkSwitch", savedUi);

	}

	async Task<JsonElement> WaitForDesignerAsync()
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		JsonElement last = default;
		while (DateTime.UtcNow < deadline) {
			last = await app.InvokeAsync("od.gtk-designer.status");
			if (last.TryGetProperty("active", out var active) && active.GetBoolean()
				&& last.TryGetProperty("elementCount", out var count) && count.GetInt32() > 0
				&& last.TryGetProperty("toolboxHosted", out var hosted) && hosted.GetBoolean()) return last;
			await Task.Delay(150, TestContext.Current.CancellationToken);
		}
		return last;
	}

	public ValueTask DisposeAsync() { try { Directory.Delete(workDir, true); } catch { } return ValueTask.CompletedTask; }
}
