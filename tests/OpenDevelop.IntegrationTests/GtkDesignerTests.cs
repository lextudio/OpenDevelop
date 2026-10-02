using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("30 Add-ins and specialized fixtures")]
public sealed class GtkDesignerTests : IAsyncLifetime, IAsyncDisposable
{
	readonly OpenDevelopAppFixture app; readonly string workDir; readonly string projectPath; readonly string uiPath; readonly string settingsUiPath;
	public GtkDesignerTests(OpenDevelopAppFixture app)
	{
		this.app = app;
		var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(app.OpenDevelopProjectPath)!, "..", "..", ".."));
		var fixture = Path.Combine(repo, "tests", "fixtures", "GtkDesignerFixture");
		// The real path, not Path.GetTempPath()'s: on macOS that is /var/..., a symlink to /private/var.
		// MSBuild given the /var path of the project resolves its items under /private/var, sees them as
		// outside the project, and copies Windows/MainWindow.ui flat into bin/ - where the app cannot find it.
		workDir = Path.Combine(RealPath(Path.GetTempPath()), "GtkDesignerTests-" + Guid.NewGuid().ToString("N"));
		CopyDirectory(fixture, workDir); projectPath = Path.Combine(workDir, "GtkDesignerFixture.csproj"); uiPath = Path.Combine(workDir, "Windows", "MainWindow.ui"); settingsUiPath = Path.Combine(workDir, "Windows", "SettingsWindow.ui");
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
	public async Task GtkDesigner_RealPadsPropertyEditToolboxHistoryAndSave()
	{
		var project = await app.ReopenSolutionAsync(projectPath); Assert.True(project.GetProperty("success").GetBoolean(), project.ToString());
		var opened = await app.InvokeAsync("od.open-file", uiPath); Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
		var status = await WaitAsync();
		Assert.True(status.GetProperty("active").GetBoolean(), status.ToString());
		Assert.False(status.GetProperty("canUndo").GetBoolean());
		Assert.False(status.GetProperty("canRedo").GetBoolean());
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
			Assert.True(status.GetProperty("toolboxSearchHosted").GetBoolean(), "The visible Toolbox search UI was not mounted: " + status);
		Assert.True(status.GetProperty("outlineHosted").GetBoolean(), "The real Outline pad did not host the GTK tree: " + status);
		Assert.Equal(status.GetProperty("elementCount").GetInt32(), status.GetProperty("outlineItemCount").GetInt32());
		Assert.True(status.GetProperty("toolboxItemCount").GetInt32() >= 15, status.ToString());
		var filteredTools = await app.InvokeAsync("od.gtk-designer.toolbox.filter", "switch"); Assert.Equal(1, filteredTools.GetProperty("itemCount").GetInt32());
		var allTools = await app.InvokeAsync("od.gtk-designer.toolbox.filter", ""); Assert.True(allTools.GetProperty("itemCount").GetInt32() >= 15, allTools.ToString());
		Assert.Equal(3, status.GetProperty("toolbarItemCount").GetInt32());
		Assert.Equal(new[] { "Zoom", "Fit", "Gridlines" }, status.GetProperty("toolbarItems").EnumerateArray().Select(x => x.GetString()).ToArray());
		var zoomed = await app.InvokeAsync("od.gtk-designer.zoom", 1.5); Assert.Equal(1.5, zoomed.GetProperty("zoom").GetDouble());
		var fitted = await app.InvokeAsync("od.gtk-designer.fit"); Assert.True(fitted.GetProperty("measured").GetBoolean(), fitted.ToString()); Assert.InRange(fitted.GetProperty("zoom").GetDouble(), .25, 2);
		// The shared DesignerCanvas toolbar's Fit button/action bypasses the Zoom combo entirely -
		// without syncing the combo back, it kept showing the last manually-picked percentage while
		// the canvas visibly rendered at Fit scale (observed live: combo stuck on "100%").
		status = await app.InvokeAsync("od.gtk-designer.status"); Assert.Equal(0, status.GetProperty("zoomComboSelectedIndex").GetInt32());
		var gridOn = await app.InvokeAsync("od.gtk-designer.gridlines", true); Assert.True(gridOn.GetProperty("gridlines").GetBoolean());
		status = await app.InvokeAsync("od.gtk-designer.status"); Assert.True(status.GetProperty("gridlines").GetBoolean());
		var gridOff = await app.InvokeAsync("od.gtk-designer.gridlines", false); Assert.False(gridOff.GetProperty("gridlines").GetBoolean());

		var selected = await app.InvokeAsync("od.gtk-designer.select", "runButton"); Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
		Assert.Contains("GtkPropertyAdapter", selected.GetProperty("propertyPadSelectedType").GetString());
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
		var restarted = await app.InvokeAsync("od.gtk-designer.restart-host");
		Assert.True(restarted.GetProperty("success").GetBoolean(), restarted.ToString());
		Assert.NotEqual(restarted.GetProperty("oldHostProcessId").GetInt32(), restarted.GetProperty("hostProcessId").GetInt32());
		var refreshed = await app.InvokeAsync("od.gtk-designer.refresh"); Assert.True(refreshed.GetProperty("success").GetBoolean(), refreshed.ToString());

		await app.InvokeAsync("od.gtk-designer.select", "contentBox");
		var inserted = await app.InvokeAsync("od.gtk-designer.toolbox.insert", "GtkEntry"); Assert.True(inserted.GetProperty("success").GetBoolean(), inserted.ToString());
		Assert.Equal("entry1", inserted.GetProperty("selectedId").GetString());
		status = await app.InvokeAsync("od.gtk-designer.status"); Assert.True(status.GetProperty("canUndo").GetBoolean(), status.ToString()); Assert.False(status.GetProperty("canRedo").GetBoolean(), status.ToString());
		var undo = await app.InvokeAsync("od.gtk-designer.undo"); Assert.Equal(4, undo.GetProperty("elementCount").GetInt32());
		Assert.True(undo.GetProperty("canRedo").GetBoolean());
		var redo = await app.InvokeAsync("od.gtk-designer.redo"); Assert.Equal(5, redo.GetProperty("elementCount").GetInt32());
		Assert.False(redo.GetProperty("canRedo").GetBoolean());
		var deleted = await app.InvokeAsync("od.gtk-designer.delete"); Assert.True(deleted.GetProperty("success").GetBoolean(), deleted.ToString()); Assert.Equal(4, deleted.GetProperty("elementCount").GetInt32());
		var undoDelete = await app.InvokeAsync("od.gtk-designer.undo"); Assert.Equal(5, undoDelete.GetProperty("elementCount").GetInt32());
		var redoDelete = await app.InvokeAsync("od.gtk-designer.redo"); Assert.Equal(4, redoDelete.GetProperty("elementCount").GetInt32());
		var restoreDeleted = await app.InvokeAsync("od.gtk-designer.undo"); Assert.Equal(5, restoreDeleted.GetProperty("elementCount").GetInt32());
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
			            <property name="stars">4</property>
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
		Assert.Contains("MyAppStarRating", status.GetProperty("diagnostics").ToString());

		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "caption")).GetProperty("success").GetBoolean());
		Assert.True((await app.InvokeAsync("od.gtk-designer.properties.edit", "Label", "Rated by you")).GetProperty("success").GetBoolean());
		Assert.True((await app.InvokeAsync("od.gtk-designer.select", "rating")).GetProperty("success").GetBoolean());
		var rating = (await app.InvokeAsync("od.gtk-designer.properties.describe")).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!, i => i);
		Assert.Equal("4", rating["Stars"].GetProperty("value").GetString());   // unknown to GIR, still editable text
		Assert.True((await app.InvokeAsync("od.file.save", customPath)).GetProperty("success").GetBoolean());
		var xml = await File.ReadAllTextAsync(customPath, TestContext.Current.CancellationToken);
		Assert.Contains("<object class=\"MyAppStarRating\" id=\"rating\">", xml);
		Assert.Contains("<property name=\"stars\">4</property>", xml);
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
	const int GtkToolCount = 20;

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
		var grew = false;
		for (int attempt = 1; attempt <= 4 && !grew; attempt++) {
			await app.InvokeAsync("od.activate");
			// Read both points after activation, and only once two samples agree: showing the Tools pad
			// and activating the window re-lay it out, and a toolbox point read before that settles lands
			// on a neighbouring item - the drop then inserts a CheckButton instead of the Switch.
			var (fromX, fromY) = await StableCenterAsync("od.gtk-designer.toolbox.query-item-bounds", "GtkSwitch");
			var (toX, toY) = await StableCenterAsync("od.gtk-designer.query-element-screen-bounds", "runButton");
			var pressed = await app.PressPointerAsync(fromX, fromY); Assert.True(pressed.GetProperty("ok").GetBoolean(), pressed.ToString());
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
		Assert.True(grew, "Expected elementCount to grow after the drag-drop, even after retries.\nBefore: " + elementCountBefore + "\nAfter: " + statusAfterDrop);

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
