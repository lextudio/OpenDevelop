using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("30 Add-ins and specialized fixtures")]
public sealed class MewUIDesignerTests : IAsyncLifetime, IAsyncDisposable
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
		settingsSourcePath = Path.Combine(workDir, "Windows", "SettingsWindow.mxaml");
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
		Assert.True(process.ExitCode == 0, "MewUI designer fixture restore failed:\n" + output + error);
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
		Assert.False(status.GetProperty("canUndo").GetBoolean());
		Assert.False(status.GetProperty("canRedo").GetBoolean());
		Assert.True(status.GetProperty("hostProcessId").GetInt32() > 0, "MewUI designer did not start its isolated host: " + status);
		Assert.True(status.GetProperty("toolboxHosted").GetBoolean(), "The real Tools pad did not host the MewUI toolbox: " + status);
		Assert.True(status.GetProperty("toolboxSearchHosted").GetBoolean(), "The visible Toolbox search UI was not mounted: " + status);
		Assert.True(status.GetProperty("outlineHosted").GetBoolean(), "The real Outline pad did not host the MewUI tree: " + status);
		Assert.Equal(status.GetProperty("elementCount").GetInt32(), status.GetProperty("outlineItemCount").GetInt32());
		Assert.True(status.GetProperty("toolboxItemCount").GetInt32() >= 16, status.ToString());
		var filteredTools = await app.InvokeAsync("od.mewui-designer.toolbox.filter", "textbox"); Assert.Equal(1, filteredTools.GetProperty("itemCount").GetInt32());
		var allTools = await app.InvokeAsync("od.mewui-designer.toolbox.filter", ""); Assert.True(allTools.GetProperty("itemCount").GetInt32() >= 16, allTools.ToString());
		Assert.Equal(3, status.GetProperty("toolbarItemCount").GetInt32());
		Assert.Equal(new[] { "Zoom", "Fit", "Gridlines" }, status.GetProperty("toolbarItems").EnumerateArray().Select(x => x.GetString()).ToArray());
		var zoomed = await app.InvokeAsync("od.mewui-designer.zoom", 1.25); Assert.Equal(1.25, zoomed.GetProperty("zoom").GetDouble());
		var fitted = await app.InvokeAsync("od.mewui-designer.fit"); Assert.True(fitted.GetProperty("measured").GetBoolean(), fitted.ToString()); Assert.InRange(fitted.GetProperty("zoom").GetDouble(), .25, 2);
		// The shared DesignerCanvas toolbar's Fit action bypasses the Zoom combo - without syncing
		// it back, the combo kept showing the last manually-picked percentage while the canvas
		// visibly rendered at Fit scale (observed live: combo stuck on "100%").
		status = await app.InvokeAsync("od.mewui-designer.status"); Assert.Equal(0, status.GetProperty("zoomComboSelectedIndex").GetInt32());
		var gridOn = await app.InvokeAsync("od.mewui-designer.gridlines", true); Assert.True(gridOn.GetProperty("gridlines").GetBoolean());
		status = await app.InvokeAsync("od.mewui-designer.status"); Assert.True(status.GetProperty("gridlines").GetBoolean());
		var gridOff = await app.InvokeAsync("od.mewui-designer.gridlines", false); Assert.False(gridOff.GetProperty("gridlines").GetBoolean());
		// Window + rootPanel + heading + toolRow + 3 toolbar buttons + nameBox + notificationsCheck
		// + statusList + statusBar + statusText.
		Assert.True(status.GetProperty("elementCount").GetInt32() == 12, "elementCount=" + status.GetProperty("elementCount") + " status=" + status);

		var selected = await app.InvokeAsync("od.mewui-designer.select", "rootPanel");
		Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
		// Orientation is absent in the fixture. The StackPanel fallback must surface the catalogue's
		// finite enum choices through the real Properties pad and write the source token.
		var orientation = await app.InvokeAsync("od.mewui-designer.properties.edit", "Orientation", "Horizontal");
		Assert.True(orientation.GetProperty("success").GetBoolean(), orientation.ToString());
		var rejectedOrientation = await app.InvokeAsync("od.mewui-designer.properties.edit", "Orientation", "Diagonal");
		Assert.False(rejectedOrientation.GetProperty("success").GetBoolean(), rejectedOrientation.ToString());
		var propertySelection = await app.InvokeAsync("od.mewui-designer.select", "heading");
		Assert.True(propertySelection.GetProperty("success").GetBoolean(), propertySelection.ToString());
		Assert.Contains("MewUIPropertyAdapter", propertySelection.GetProperty("propertyPadSelectedType").GetString());
		// Margin is absent from the fixture's Label markup. The MewUI snapshot only publishes
		// authored attributes, so this exercises the adapter's fundamental-property fallback via
		// the real shared Properties pad rather than the direct source mutation action.
		var addedMargin = await app.InvokeAsync("od.mewui-designer.properties.edit", "Margin", "4");
		Assert.True(addedMargin.GetProperty("success").GetBoolean(), addedMargin.ToString());
		var multi = await app.InvokeAsync("od.mewui-designer.multi-select", "heading,nameBox"); Assert.True(multi.GetProperty("success").GetBoolean(), multi.ToString());
		Assert.Equal(2, multi.GetProperty("selectedIds").GetArrayLength());
		status = await app.InvokeAsync("od.mewui-designer.status"); Assert.Equal(2, status.GetProperty("selectedIds").GetArrayLength()); Assert.True(status.GetProperty("propertyPadPropertyCount").GetInt32() > 0, status.ToString());
		var batchEdit = await app.InvokeAsync("od.mewui-designer.properties.edit", "Text", "Shared note"); Assert.True(batchEdit.GetProperty("success").GetBoolean(), batchEdit.ToString());
		await app.InvokeAsync("od.mewui-designer.select", "heading");
		status = await app.InvokeAsync("od.mewui-designer.status"); Assert.True(status.GetProperty("propertyPadPropertyCount").GetInt32() > 0, status.ToString());
		var changedProperty = await app.InvokeAsync("od.mewui-designer.set-property", "Text", "Configured");
		Assert.True(changedProperty.GetProperty("success").GetBoolean(), changedProperty.ToString());
		var boundEvent = await app.InvokeAsync("od.mewui-designer.properties.event.bind", "Loaded");
		Assert.True(boundEvent.GetProperty("success").GetBoolean(), boundEvent.ToString());

		// Insert into a NESTED container (toolRow inside rootPanel), not the root - the fixture is
		// deep enough that "which container receives the child" is part of the contract.
		var selectToolRow = await app.InvokeAsync("od.mewui-designer.select", "toolRow");
		Assert.True(selectToolRow.GetProperty("success").GetBoolean(), selectToolRow.ToString());
		var inserted = await app.InvokeAsync("od.mewui-designer.toolbox.insert", "TextBox");
		Assert.True(inserted.GetProperty("success").GetBoolean(), inserted.ToString());
		Assert.Equal(13, inserted.GetProperty("elementCount").GetInt32());
		status = await app.InvokeAsync("od.mewui-designer.status"); Assert.True(status.GetProperty("canUndo").GetBoolean()); Assert.False(status.GetProperty("canRedo").GetBoolean());

		var undo = await app.InvokeAsync("od.mewui-designer.undo");
		Assert.Equal(12, undo.GetProperty("elementCount").GetInt32());
		Assert.True(undo.GetProperty("canRedo").GetBoolean());
		var redo = await app.InvokeAsync("od.mewui-designer.redo");
		Assert.Equal(13, redo.GetProperty("elementCount").GetInt32());
		Assert.False(redo.GetProperty("canRedo").GetBoolean());
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
		Assert.Contains("Name=\"rootPanel\" Spacing=\"8\" Orientation=\"Horizontal\"", mxamlContent);
		Assert.Contains("Text=\"Configured\"", mxamlContent);
		Assert.Contains("Name=\"heading\" Text=\"Configured\" Margin=\"4\"", mxamlContent);
		Assert.Contains("Loaded=\"heading_Loaded\"", mxamlContent);
		Assert.Contains("Name=\"nameBox\" Text=\"Shared note\"", mxamlContent);
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
		var reopenedMain = await app.InvokeAsync("od.open-file", designerPath);
		Assert.True(reopenedMain.GetProperty("opened").GetBoolean(), reopenedMain.ToString());
		var recoveredMainStatus = await WaitForDesignerAsync("MainWindow");
		Assert.Equal(recoveredHostProcessId, recoveredMainStatus.GetProperty("hostProcessId").GetInt32());
		Assert.True(recoveredMainStatus.GetProperty("hostRecoveryCount").GetInt32() > 0, recoveredMainStatus.ToString());
		Assert.Equal(13, recoveredMainStatus.GetProperty("elementCount").GetInt32());
	}

	[Fact]
	public async Task MewUIDesigner_RendersRealMewUIControlsWithLayoutBounds()
	{
		Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "The MewUI host only wires up the macOS and Windows render backends so far.");
		var openedProject = await app.ReopenSolutionAsync(projectPath);
		Assert.True(openedProject.GetProperty("success").GetBoolean(), openedProject.ToString());
		var opened = await app.InvokeAsync("od.open-file", designerPath);
		Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitForDesignerAsync();
		Assert.True(status.GetProperty("hasNativeFrame").GetBoolean(), "No MewUI frame: " + status);
		// Every element of the fixture got a real arranged rect from MewUI's own layout.
		Assert.Equal(status.GetProperty("elementCount").GetInt32(), status.GetProperty("nativeBoundsCount").GetInt32());

		// Compare independent elements: bounds derived from one shared wrong source would still agree
		// with themselves, but not order siblings left-to-right or nest a child inside its panel.
		async Task<(double X, double Y, double W, double H)> Bounds(string id)
		{
			var b = await app.InvokeAsync("od.mewui-designer.query-element-screen-bounds", id);
			Assert.True(b.GetProperty("success").GetBoolean(), id + ": " + b);
			return (b.GetProperty("x").GetDouble(), b.GetProperty("y").GetDouble(), b.GetProperty("width").GetDouble(), b.GetProperty("height").GetDouble());
		}
		var row = await Bounds("toolRow"); var first = await Bounds("newButton"); var last = await Bounds("saveButton"); var heading = await Bounds("heading");
		Assert.True(first.X + first.W <= last.X, $"Horizontal StackPanel children overlap: {first} vs {last}");
		Assert.True(first.X >= row.X && first.Y >= row.Y && last.X + last.W <= row.X + row.W + 0.5, $"Buttons are not inside toolRow: {row} {first} {last}");
		Assert.True(heading.Y + heading.H <= row.Y, $"heading is not above toolRow: {heading} vs {row}");
		var diagnostics = status.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetString()).ToArray();
		Assert.DoesNotContain(diagnostics, d => d!.Contains("render failed") || d.Contains("Unknown MewUI control"));

		// Attached properties are static Owner.SetX(Element, value) methods in MewUI, not instance
		// properties: a known one applies silently, an unknown one is named in a diagnostic.
		Assert.True((await app.InvokeAsync("od.mewui-designer.select", "heading")).GetProperty("success").GetBoolean());
		var docked = await app.InvokeAsync("od.mewui-designer.set-property", "DockPanel.Dock", "Top");
		Assert.True(docked.GetProperty("success").GetBoolean(), docked.ToString());
		var bogus = await app.InvokeAsync("od.mewui-designer.set-property", "Grid.Bogus", "1");
		Assert.True(bogus.GetProperty("success").GetBoolean(), bogus.ToString());
		status = await app.InvokeAsync("od.mewui-designer.status");
		diagnostics = status.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetString()).ToArray();
		Assert.DoesNotContain(diagnostics, d => d!.Contains("DockPanel.Dock"));
		Assert.Contains(diagnostics, d => d!.Contains("Grid.Bogus"));
		Assert.True(status.GetProperty("hasNativeFrame").GetBoolean(), status.ToString());
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
		// Fit first: the preview surface can be wider than the canvas viewport, and an element's
		// PointToScreen still reports the off-screen part - which put an edge-relative drop point
		// outside the designer entirely (observed landing on the Properties pad).
		await app.InvokeAsync("od.mewui-designer.fit");

		// Drop on toolRow's centre. Landing on one of its child Buttons is fine and intended:
		// ResolveDropTarget walks up from the hit, and a Button only has AllowDrop by inheritance
		// (which the portable resolver deliberately ignores), so the first EXPLICIT AllowDrop
		// ancestor - toolRow's own panel - is what receives the drop.
		JsonElement statusAfterDrop = default;
		var grew = false; var pointerEvents = "";
		for (int attempt = 1; attempt <= 4 && !grew; attempt++) {
			await app.InvokeAsync("od.activate");
			var (fromX, fromY) = await StableCenterAsync("od.mewui-designer.toolbox.query-item-bounds", "CheckBox");
			var (toX, toY) = await StableCenterAsync("od.mewui-designer.query-element-screen-bounds", "toolRow");
			await app.InvokeAsync("od.pointer-events", true);
			var pressed = await app.PressPointerAsync(fromX, fromY); Assert.True(pressed.GetProperty("ok").GetBoolean(), pressed.ToString());
			pointerEvents = (await app.InvokeAsync("od.pointer-events", false)).ToString();
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
		Assert.True(grew, "Expected elementCount to grow after the drag-drop, even after retries.\nBefore: " + elementCountBefore + "\nPointer: " + pointerEvents + "\nAfter: " + statusAfterDrop);

		var saved = await app.InvokeAsync("od.file.save", designerPath); Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
		var mxamlContent = await File.ReadAllTextAsync(designerPath, TestContext.Current.CancellationToken);
		Assert.Contains("<CheckBox ", mxamlContent);
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
		Assert.Fail(action + " " + name + " never reported stable bounds; last " + previous);
		return default;
	}

	[Fact]
	public async Task MewUIDesigner_SourceView_SharesTheDesignerToolbox_AndPlacesDroppedItems()
	{
		// The .mxaml Source half of a Design/Source document: the Tools pad keeps the MewUI list, and
		// a dropped control becomes an element on its own line in the panel under the drop point.
		var openedProject = await app.ReopenSolutionAsync(projectPath); Assert.True(openedProject.GetProperty("success").GetBoolean(), openedProject.ToString());
		var opened = await app.InvokeAsync("od.open-file", designerPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitForDesignerAsync();
		var countBefore = status.GetProperty("elementCount").GetInt32();

		// On the "New" button inside the horizontal toolRow: the new control goes after it, in toolRow.
		// The designer hands the Source view its canonical text on the switch, so plan against that.
		var current = (await app.InvokeAsync("od.file.query-vs-text-buffer", designerPath)).GetProperty("text").GetString()!;
		var onNewButton = current.IndexOf("Name=\"newButton\"", StringComparison.Ordinal);
		var dropped = await app.InvokeAsync("od.file.drop-toolbox-item", designerPath, onNewButton, "CheckBox");
		Assert.True(dropped.GetProperty("success").GetBoolean() && dropped.GetProperty("inserted").GetBoolean(), dropped.ToString());
		var text = dropped.GetProperty("text").GetString()!;
		Assert.True(NextLineIsSibling(text, "Name=\"newButton\"", "<CheckBox />"), "the CheckBox is not on its own line after newButton:\n" + text);

		var pad = await app.InvokeAsync("od.tools-pad.status");
		Assert.Equal("AvalonEditViewContent", pad.GetProperty("activeView").GetString());
		Assert.True(pad.GetProperty("hostsSharedToolbox").GetBoolean(), "the Source half lost the MewUI toolbox: " + pad);
		Assert.Equal("MewUIDesignerViewContent", pad.GetProperty("sharedToolboxOwner").GetString());

		// A Label is not a container, and neither is a full content control: dropping into the
		// QuickNotes heading's line goes to the root panel instead, never inside the Label.
		var onHeading = text.IndexOf("Text=\"QuickNotes\"", StringComparison.Ordinal);
		var second = await app.InvokeAsync("od.file.drop-toolbox-item", designerPath, onHeading, "Button");
		Assert.True(second.GetProperty("inserted").GetBoolean(), second.ToString());
		var afterSecond = second.GetProperty("text").GetString()!;
		Assert.True(NextLineIsSibling(afterSecond, "Name=\"heading\"", "<Button />"), "the Button is not on its own line after the heading:\n" + afterSecond);

		JsonElement designed = default;
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			designed = await app.InvokeAsync("od.mewui-designer.status");
			return designed.GetProperty("elementCount").GetInt32() == countBefore + 2;
		}, TimeSpan.FromSeconds(15)), "the designer did not pick up the two dropped controls: " + designed);
		await app.InvokeAsync("od.file.revert-all-dirty");
		await app.InvokeAsync("od.close-active-view");
	}

	/// <summary>Whether the line after the one holding <paramref name="marker"/> is exactly
	/// <paramref name="markup"/>, at that line's indentation.</summary>
	static bool NextLineIsSibling(string text, string marker, string markup)
	{
		var lines = text.Replace("\r\n", "\n").Split('\n');
		var at = Array.FindIndex(lines, line => line.Contains(marker, StringComparison.Ordinal));
		if (at < 0 || at + 1 >= lines.Length) return false;
		var indent = lines[at][..(lines[at].Length - lines[at].TrimStart().Length)];
		return lines[at + 1] == indent + markup;
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

	public async ValueTask DisposeAsync()
	{
		// Close the documents first: the app outlives this class, and a tab still open on a deleted
		// file fails the next time the workbench initializes its views.
		try { await app.InvokeAsync("od.close-all-document-views"); } catch { }
		try { Directory.Delete(workDir, true); } catch { }
	}
}
