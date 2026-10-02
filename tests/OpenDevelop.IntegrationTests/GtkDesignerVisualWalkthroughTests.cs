using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Walks the designer's own steps and captures a PNG after every one that changes what is on the
/// canvas. This exists to be looked at rather than to assert: the GTK host's render frame carries only
/// the design content, so nothing in the test suite can see how the shared canvas placed, scaled or
/// chromed that content - which is where a distorted surface appears. Running inside the existing
/// fixture also means the capture happens in exactly the environment the assertions run in, instead of
/// a hand-launched app whose add-in timing differs.
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class GtkDesignerVisualWalkthroughTests : IAsyncLifetime, IAsyncDisposable
{
	readonly OpenDevelopAppFixture app; readonly string workDir; readonly string projectPath; readonly string uiPath;

	public GtkDesignerVisualWalkthroughTests(OpenDevelopAppFixture app)
	{
		this.app = app;
		var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(app.OpenDevelopProjectPath)!, "..", "..", ".."));
		var fixture = Path.Combine(repo, "tests", "fixtures", "GtkDesignerFixture");
		workDir = Path.Combine(Path.GetTempPath(), "GtkDesignerWalk-" + Guid.NewGuid().ToString("N"));
		CopyDirectory(fixture, workDir);
		projectPath = Path.Combine(workDir, "GtkDesignerFixture.csproj");
		uiPath = Path.Combine(workDir, "Windows", "MainWindow.ui");
		OutputDirectory = Path.Combine(repo, "tests", "OpenDevelop.IntegrationTests", "bin", "gtk-walkthrough");
		Directory.CreateDirectory(OutputDirectory);
	}

	/// <summary>Where the walkthrough leaves its PNGs, so a failing run can be inspected by eye.</summary>
	public string OutputDirectory { get; }

	public async ValueTask InitializeAsync()
	{
		var start = new System.Diagnostics.ProcessStartInfo("dotnet") {
			RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = workDir
		};
		start.ArgumentList.Add("restore");
		start.ArgumentList.Add(projectPath);
		start.ArgumentList.Add("--verbosity");
		start.ArgumentList.Add("quiet");
		using var process = System.Diagnostics.Process.Start(start)!;
		var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		Assert.True(process.ExitCode == 0, "fixture restore failed:\n" + await output + await error);
	}

	public ValueTask DisposeAsync() { Cleanup(); return default; }

	void Cleanup() { try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch (IOException) { } }

	static void CopyDirectory(string source, string target)
	{
		Directory.CreateDirectory(target);
		foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
		foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
	}

	int step;

	/// <summary>Captures the designer and records the step's name, so the PNGs read as a sequence.</summary>
	async Task ShootAsync(string name)
	{
		var path = Path.Combine(OutputDirectory, $"{++step:D2}-{name}.png");
		var result = await app.InvokeAsync("od.gtk-designer.screenshot", path);
		Assert.True(result.GetProperty("success").GetBoolean(), "screenshot " + name + " failed: " + result);
	}

	[Fact]
	public async Task Walkthrough_CapturesTheDesignSurfaceAfterEveryKeyChange()
	{
		await app.InvokeAsync("od.activate");
		var opened = await app.InvokeAsync("od.open-file", uiPath);
		Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		await app.InvokeAsync("od.gtk-designer.refresh");
		await ShootAsync("opened-fit");

		// Zoom is where a mis-scaled frame would first show: the bitmap is stretched by the canvas, not
		// by GTK, so 100% and 200% are the two states worth comparing against each other.
		await app.InvokeAsync("od.gtk-designer.zoom", "100");
		await ShootAsync("zoom-100");
		await app.InvokeAsync("od.gtk-designer.zoom", "200");
		await ShootAsync("zoom-200");
		await app.InvokeAsync("od.gtk-designer.fit");
		await ShootAsync("fit-again");

		await app.InvokeAsync("od.gtk-designer.gridlines", "true");
		await ShootAsync("gridlines-on");
		await app.InvokeAsync("od.gtk-designer.gridlines", "false");

		await app.InvokeAsync("od.gtk-designer.toolbox.filter", "button");
		await ShootAsync("toolbox-filtered");
		await app.InvokeAsync("od.gtk-designer.toolbox.filter", "");

		// Inserting grows the document, so the frame, the caption and the canvas margin all have to
		// re-lay-out together; this is the step most likely to leave something stale.
		var inserted = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkButton");
		Assert.True(inserted.GetProperty("success").GetBoolean(), inserted.ToString());
		await ShootAsync("after-insert");

		await app.InvokeAsync("od.gtk-designer.select", "heading");
		await ShootAsync("selected-heading");

		await app.InvokeAsync("od.gtk-designer.select", "runButton");
		await ShootAsync("selected-button");

		await app.InvokeAsync("od.gtk-designer.delete", "heading");
		await ShootAsync("after-delete");

		await app.InvokeAsync("od.gtk-designer.undo");
		await ShootAsync("after-undo");

		// A second insert on top of the first is the state where a stale chrome offset would compound.
		await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkLabel");
		await ShootAsync("second-insert");

		await app.InvokeAsync("od.gtk-designer.zoom", "400");
		await ShootAsync("zoom-400-detail");

		TestContext.Current.TestOutputHelper?.WriteLine("GTK walkthrough PNGs: " + OutputDirectory);
		Assert.True(Directory.GetFiles(OutputDirectory, "*.png").Length >= 12, "expected a screenshot per step in " + OutputDirectory);
	}
}
