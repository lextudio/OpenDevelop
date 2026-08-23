using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("30 Add-ins and specialized fixtures")]
public sealed class GtkDesignerTests : IAsyncDisposable
{
	readonly OpenDevelopAppFixture app; readonly string workDir; readonly string projectPath; readonly string uiPath;
	public GtkDesignerTests(OpenDevelopAppFixture app)
	{
		this.app = app;
		var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(app.OpenDevelopProjectPath)!, "..", "..", ".."));
		var fixture = Path.Combine(repo, "tests", "fixtures", "GtkDesignerFixture");
		workDir = Path.Combine(Path.GetTempPath(), "GtkDesignerTests-" + Guid.NewGuid().ToString("N"));
		CopyDirectory(fixture, workDir); projectPath = Path.Combine(workDir, "GtkDesignerFixture.csproj"); uiPath = Path.Combine(workDir, "Windows", "MainWindow.ui");
	}

	[Fact]
	public async Task GtkDesigner_RealPadsPropertyEditToolboxHistoryAndSave()
	{
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", uiPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync();
		Assert.True(status.GetProperty("active").GetBoolean(), status.ToString());
		Assert.True(status.GetProperty("hostProcessId").GetInt32() > 0, "GTK designer did not start its isolated host: " + status);
		Assert.True(status.GetProperty("toolboxHosted").GetBoolean(), "The real Tools pad did not host the GTK toolbox: " + status);
		Assert.True(status.GetProperty("outlineHosted").GetBoolean(), "The real Outline pad did not host the GTK tree: " + status);
		Assert.Equal(status.GetProperty("elementCount").GetInt32(), status.GetProperty("outlineItemCount").GetInt32());
		Assert.True(status.GetProperty("toolboxItemCount").GetInt32() >= 15, status.ToString());
		Assert.Equal(3, status.GetProperty("toolbarItemCount").GetInt32());
		Assert.Equal(new[] { "Zoom", "Fit", "Gridlines" }, status.GetProperty("toolbarItems").EnumerateArray().Select(x => x.GetString()).ToArray());
		var zoomed = await app.InvokeAsync("od.gtk-designer.zoom", 1.5); Assert.Equal(1.5, zoomed.GetProperty("zoom").GetDouble());
		var fitted = await app.InvokeAsync("od.gtk-designer.fit"); Assert.Equal(1, fitted.GetProperty("zoom").GetDouble());
		var gridOn = await app.InvokeAsync("od.gtk-designer.gridlines", true); Assert.True(gridOn.GetProperty("gridlines").GetBoolean());
		status = await app.InvokeAsync("od.gtk-designer.status"); Assert.True(status.GetProperty("gridlines").GetBoolean());
		var gridOff = await app.InvokeAsync("od.gtk-designer.gridlines", false); Assert.False(gridOff.GetProperty("gridlines").GetBoolean());

		var selected = await app.InvokeAsync("od.gtk-designer.select", "runButton"); Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
		Assert.Contains("GtkPropertyAdapter", selected.GetProperty("propertyPadSelectedType").GetString());
		status = await app.InvokeAsync("od.gtk-designer.status"); Assert.True(status.GetProperty("propertyPadPropertyCount").GetInt32() > 0, status.ToString());
		var edited = await app.InvokeAsync("od.gtk-designer.properties.edit", "Label", "Execute"); Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
		var restarted = await app.InvokeAsync("od.gtk-designer.restart-host");
		Assert.True(restarted.GetProperty("success").GetBoolean(), restarted.ToString());
		Assert.NotEqual(restarted.GetProperty("oldHostProcessId").GetInt32(), restarted.GetProperty("hostProcessId").GetInt32());
		var refreshed = await app.InvokeAsync("od.gtk-designer.refresh"); Assert.True(refreshed.GetProperty("success").GetBoolean(), refreshed.ToString());

		await app.InvokeAsync("od.gtk-designer.select", "contentBox");
		var inserted = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkEntry"); Assert.True(inserted.GetProperty("success").GetBoolean(), inserted.ToString());
		Assert.Equal("entry1", inserted.GetProperty("selectedId").GetString());
		var undo = await app.InvokeAsync("od.gtk-designer.undo"); Assert.Equal(4, undo.GetProperty("elementCount").GetInt32());
		var redo = await app.InvokeAsync("od.gtk-designer.redo"); Assert.Equal(5, redo.GetProperty("elementCount").GetInt32());
		var saved = await app.InvokeAsync("od.file.save", uiPath); Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
		var xml = await File.ReadAllTextAsync(uiPath, TestContext.Current.CancellationToken);
		Assert.Contains(">Execute</property>", xml); Assert.Contains("class=\"GtkEntry\"", xml); Assert.Contains("id=\"entry1\"", xml);
	}

	async Task<JsonElement> WaitAsync()
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20); JsonElement last = default;
		while (DateTime.UtcNow < deadline) { last = await app.InvokeAsync("od.gtk-designer.status"); if (last.TryGetProperty("active", out var active) && active.GetBoolean() && last.GetProperty("toolboxHosted").GetBoolean() && last.GetProperty("outlineHosted").GetBoolean()) return last; await Task.Delay(100, TestContext.Current.CancellationToken); }
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
	public ValueTask DisposeAsync() { try { Directory.Delete(workDir, true); } catch { } return ValueTask.CompletedTask; }
}
