using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("30 Add-ins and specialized fixtures")]
public sealed class FormsMenuEditingTests(OpenDevelopAppFixture app)
{
	[Fact]
	public async Task ComponentTray_ContainsStripLevelObjects_NotToolStripItems()
	{
		if (!OperatingSystem.IsWindows()) return;
		await OpenFixture();
		JsonElement tray = default;
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			tray = await app.InvokeAsync("od.forms-designer.component-tray-status");
			return tray.TryGetProperty("items", out _);
		}, TimeSpan.FromSeconds(10)), tray.ToString());
		var names = tray.GetProperty("items").EnumerateArray().Select(item => item.GetString()).ToArray();
		Assert.Contains("contextMenu", names);
		Assert.Contains("menuStrip", names);
		Assert.Contains("statusStrip", names);
		Assert.DoesNotContain("contextItem", names);
		Assert.DoesNotContain("fileItem", names);
		Assert.DoesNotContain("menuItem", names);
		await app.InvokeAsync("od.close-active-view");
	}

    // The two popup kinds must exercise the same pointer -> focused editor -> key ->
    // property transaction path. Calling set-property here would miss the original bug.
    [Theory]
    [InlineData("contextMenu", "contextItem")]
    [InlineData("fileItem", "menuItem")]
    public async Task PopupItem_Click_Edit_Cancel_Commit_Undo(string owner, string item)
    {
        if (!OperatingSystem.IsWindows()) return;
        await OpenFixture();
        Assert.True((await app.InvokeAsync("od.forms-designer.select", owner)).GetProperty("success").GetBoolean());
        await ClickItem(item);
        await AssertEditing(item, "Original");
        Assert.True((await app.InvokeAsync("od.forms-designer.item-editor-input", "Discarded", true)).GetProperty("success").GetBoolean());
        Assert.Equal("Original", (await Editor()).GetProperty("selectedText").GetString());
        await ClickItem(item);
        await AssertEditing(item, "Original");
        Assert.True((await app.InvokeAsync("od.forms-designer.item-editor-input", "Edited", false)).GetProperty("success").GetBoolean());
        Assert.Equal("Edited", (await Editor()).GetProperty("selectedText").GetString());
        Assert.True((await app.InvokeAsync("od.forms-designer.status")).GetProperty("canUndo").GetBoolean());
        await app.InvokeAsync("od.forms-designer.undo");
        await app.InvokeAsync("od.forms-designer.select", owner);
        await ClickItem(item);
        await AssertEditing(item, "Original");
        await app.InvokeAsync("od.forms-designer.item-editor-input", "Original", true);
        await app.InvokeAsync("od.close-active-view");
    }

    [Theory]
    [InlineData("contextMenu")]
    [InlineData("fileItem")]
    public async Task PopupTypeHere_Click_CreateItem_AndUndoRemovesIt(string owner)
    {
        if (!OperatingSystem.IsWindows()) return;
        await OpenFixture();
        var beforeNames = (await app.InvokeAsync("od.forms-designer.status")).GetProperty("controlNames")
            .EnumerateArray().Select(name => name.GetString()).ToHashSet(StringComparer.Ordinal);
        await app.InvokeAsync("od.forms-designer.select", owner);
        JsonElement status = default;
        Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
            status = await app.InvokeAsync("od.forms-designer.popup-type-here-status");
            return status.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("ownerId").GetString() == owner);
        }, TimeSpan.FromSeconds(10)), status.ToString());
        var cell = status.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("ownerId").GetString() == owner);
        await Click(cell);
        Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () =>
            (await app.InvokeAsync("od.forms-designer.popup-type-here-status")).GetProperty("items").EnumerateArray()
                .Any(item => item.GetProperty("ownerId").GetString() == owner && item.GetProperty("editing").GetBoolean() && item.GetProperty("focused").GetBoolean()),
            TimeSpan.FromSeconds(10)));
        Assert.True((await app.InvokeAsync("od.forms-designer.popup-type-here-input", "Created", false)).GetProperty("success").GetBoolean());
        JsonElement afterAdd = default;
        string createdName = null;
        Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
            afterAdd = await app.InvokeAsync("od.forms-designer.status");
            createdName = afterAdd.GetProperty("controlNames").EnumerateArray().Select(name => name.GetString())
                .FirstOrDefault(name => !beforeNames.Contains(name) && name.StartsWith("toolStripMenuItem", StringComparison.Ordinal));
            return createdName != null;
        }, TimeSpan.FromSeconds(10)), afterAdd.ToString());
        await app.InvokeAsync("od.forms-designer.select", createdName);
        Assert.Equal("Created", (await Editor()).GetProperty("selectedText").GetString());
        await app.InvokeAsync("od.forms-designer.undo");
        JsonElement afterUndo = default;
        Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
            afterUndo = await app.InvokeAsync("od.forms-designer.status");
            return !afterUndo.GetProperty("controlNames").EnumerateArray().Any(name => name.GetString() == createdName);
        }, TimeSpan.FromSeconds(20)), afterUndo.ToString());
        await app.InvokeAsync("od.close-active-view");
    }

    [Fact]
    public async Task StatusStrip_InsertionNode_Click_AddsItem_AndUndoRemovesIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        await OpenFixture();
        await app.InvokeAsync("od.forms-designer.select", "statusStrip");
        var state = await Editor();
        var bounds = state.GetProperty("insertion");
        Assert.Equal(JsonValueKind.Object, bounds.ValueKind);
        await Click(bounds);
        JsonElement menu = default;
        Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
            menu = await app.InvokeAsync("od.forms-designer.item-insertion-popup-status");
            return menu.GetProperty("open").GetBoolean() && menu.GetProperty("items").GetArrayLength() == 4;
        }, TimeSpan.FromSeconds(10)), menu.ToString());
        var statusLabel = menu.GetProperty("items").EnumerateArray().Single(item =>
            item.GetProperty("typeName").GetString() == "ToolStripStatusLabel");
        await Click(statusLabel);
        JsonElement afterAdd = default;
        Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
            afterAdd = await app.InvokeAsync("od.forms-designer.status");
            return afterAdd.GetProperty("controlNames").EnumerateArray()
                .Any(name => name.GetString() == "toolStripStatusLabel");
        }, TimeSpan.FromSeconds(10)), afterAdd.ToString());
        Assert.True(afterAdd.GetProperty("canUndo").GetBoolean());
        await app.InvokeAsync("od.forms-designer.undo");
        JsonElement afterUndo = default;
        Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
            afterUndo = await app.InvokeAsync("od.forms-designer.status");
            return !afterUndo.GetProperty("controlNames").EnumerateArray()
                .Any(name => name.GetString() == "toolStripStatusLabel");
        }, TimeSpan.FromSeconds(20)), afterUndo.ToString());
        await app.InvokeAsync("od.close-active-view");
    }

    Task<JsonElement> Editor() => app.InvokeAsync("od.forms-designer.item-editor-status");

    async Task AssertEditing(string name, string text)
    {
        JsonElement state = default;
        Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
            state = await Editor();
            return state.GetProperty("editing").GetBoolean() && state.GetProperty("visible").GetBoolean()
                && state.GetProperty("focused").GetBoolean();
        }, TimeSpan.FromSeconds(10)), state.ToString());
        Assert.Equal(name, state.GetProperty("componentName").GetString());
        Assert.Equal(text, state.GetProperty("text").GetString());
        Assert.NotEmpty(state.GetProperty("popupOwners").EnumerateArray());
        // A second read catches selection-refresh cancellation after the click completed.
        Assert.True((await Editor()).GetProperty("focused").GetBoolean());
    }

    async Task ClickItem(string item) => await Click(await app.InvokeAsync("od.forms-designer.query-control-screen-bounds", item));

    async Task Click(JsonElement bounds)
    {
        var x = bounds.GetProperty("x").GetDouble() + bounds.GetProperty("width").GetDouble() / 2;
        var y = bounds.GetProperty("y").GetDouble() + bounds.GetProperty("height").GetDouble() / 2;
        Assert.True((await app.PressPointerAsync(x, y)).GetProperty("ok").GetBoolean());
        try { }
        finally { Assert.True((await app.ReleasePointerAsync(x, y)).GetProperty("ok").GetBoolean()); }
    }

    async Task OpenFixture()
    {
        // Never edit the user's Jexus solution or the tracked sample. Keep a unique temporary
        // fixture for failure inspection; assembly fixture shutdown releases all its handles.
        var directory = Directory.CreateTempSubdirectory("od-menu-edit-").FullName;
        var sample = Path.GetDirectoryName(app.WinFormsSampleSolutionPath)!;
        foreach (var file in Directory.GetFiles(sample))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        await File.WriteAllTextAsync(Path.Combine(directory, "Form1.Designer.cs"), """
            namespace WinFormsSample {
            partial class Form1 {
                private System.ComponentModel.IContainer components;
                private System.Windows.Forms.ContextMenuStrip contextMenu;
                private System.Windows.Forms.ToolStripMenuItem contextItem, fileItem, menuItem;
                private System.Windows.Forms.MenuStrip menuStrip;
                private System.Windows.Forms.StatusStrip statusStrip;
                private void InitializeComponent() {
                    this.components = new System.ComponentModel.Container();
                    this.contextMenu = new System.Windows.Forms.ContextMenuStrip(this.components);
                    this.contextItem = new System.Windows.Forms.ToolStripMenuItem();
                    this.contextItem.Text = "Original";
                    this.contextMenu.Items.Add(this.contextItem);
                    this.menuStrip = new System.Windows.Forms.MenuStrip();
                    this.fileItem = new System.Windows.Forms.ToolStripMenuItem();
                    this.fileItem.Text = "File";
                    this.menuItem = new System.Windows.Forms.ToolStripMenuItem();
                    this.menuItem.Text = "Original";
                    this.fileItem.DropDownItems.Add(this.menuItem);
                    this.menuStrip.Items.Add(this.fileItem);
                    this.statusStrip = new System.Windows.Forms.StatusStrip();
                    this.Controls.Add(this.menuStrip);
                    this.Controls.Add(this.statusStrip);
                    this.ClientSize = new System.Drawing.Size(600, 400);
                }
            }}
            """);
        using var restore = Process.Start(new ProcessStartInfo("dotnet") {
            ArgumentList = { "restore", Path.Combine(directory, "WinFormsSample.csproj"), "--verbosity", "quiet" },
            UseShellExecute = false, CreateNoWindow = true
        })!;
        await restore.WaitForExitAsync();
        Assert.Equal(0, restore.ExitCode);
        Assert.True((await app.ReopenSolutionAsync(Path.Combine(directory, "WinFormsSample.sln"))).GetProperty("success").GetBoolean());
        await app.InvokeAsync("od.open-file", Path.Combine(directory, "Form1.cs"));
        JsonElement status = default;
        Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
            status = await app.InvokeAsync("od.forms-designer.status");
            return status.GetProperty("designerLoaded").GetBoolean();
        }, TimeSpan.FromSeconds(120)), status.ToString());
    }
}
