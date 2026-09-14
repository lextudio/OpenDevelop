using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("30 Add-ins and specialized fixtures")]
public sealed class WpfStripEditingTests(OpenDevelopAppFixture app)
{
    [Fact]
    public async Task MenuItem_DoubleClick_InlineEditsHeader_AndUndoRestoresIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Strip edit">
              <DockPanel>
                <Menu DockPanel.Dock="Top"><MenuItem x:Name="fileItem" Header="File" /></Menu>
                <Button x:Name="owner" Content="Open">
                  <Button.ContextMenu><ContextMenu x:Name="actions"><MenuItem x:Name="rename" Header="Rename" /></ContextMenu></Button.ContextMenu>
                </Button>
                <ToolBarTray DockPanel.Dock="Top"><ToolBar><Button x:Name="toolButton" Content="Run" /></ToolBar></ToolBarTray>
                <StatusBar DockPanel.Dock="Bottom"><StatusBarItem x:Name="statusItem" Content="Ready" /></StatusBar>
              </DockPanel>
            </Window>
            """);
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());

            var bounds = await app.InvokeAsync("od.wpf-designer.query-element-screen-bounds", "fileItem");
            Assert.True(bounds.GetProperty("success").GetBoolean(), bounds.ToString());
            var x = bounds.GetProperty("centerX").GetDouble();
            var y = bounds.GetProperty("centerY").GetDouble();
            await app.InvokeAsync("od.activate");
            Assert.True((await app.ClickPointerAsync(x, y, clickCount: 2)).GetProperty("ok").GetBoolean());

            JsonElement editor = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                editor = await app.InvokeAsync("od.wpf-designer.inline-editor-status");
                return editor.GetProperty("editing").GetBoolean()
                    && editor.GetProperty("visible").GetBoolean()
                    && editor.GetProperty("focused").GetBoolean();
            }, TimeSpan.FromSeconds(10)), editor.ToString());
            Assert.Equal("Header", editor.GetProperty("propertyName").GetString());
            Assert.Equal("File", editor.GetProperty("text").GetString());

            Assert.True((await app.InvokeAsync("od.wpf-designer.inline-editor-input", "Project", false))
                .GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.Contains("Header=\"Project\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            var undo = await app.InvokeAsync("od.wpf-designer.undo");
            Assert.True(undo.GetProperty("success").GetBoolean(), undo.ToString());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.Contains("Header=\"File\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "actions"))
                .GetProperty("success").GetBoolean());
            JsonElement tray = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                tray = await app.InvokeAsync("od.wpf-designer.context-menu-tray-status");
                // 2, not 1: the real "rename" item plus the trailing "Type Here" insertion slot
                // (see MenuItem_TypeHere_ClicksAddNewSiblings_AndUndoRemovesThem below).
                return tray.GetProperty("visible").GetBoolean()
                    && tray.GetProperty("items").GetArrayLength() == 2;
            }, TimeSpan.FromSeconds(10)), tray.ToString());
            var trayItem = tray.GetProperty("items")[0];
            Assert.True((await app.ClickPointerAsync(trayItem.GetProperty("centerX").GetDouble(),
                trayItem.GetProperty("centerY").GetDouble(), clickCount: 2)).GetProperty("ok").GetBoolean());

            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                editor = await app.InvokeAsync("od.wpf-designer.inline-editor-status");
                return editor.GetProperty("editing").GetBoolean()
                    && editor.GetProperty("focused").GetBoolean()
                    && editor.GetProperty("propertyName").GetString() == "Header";
            }, TimeSpan.FromSeconds(10)), editor.ToString());
            Assert.Equal("Rename", editor.GetProperty("text").GetString());
            Assert.True((await app.InvokeAsync("od.wpf-designer.inline-editor-input", "Delete", false))
                .GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.Contains("Header=\"Delete\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);
            await app.InvokeAsync("od.close-active-view");
        } finally {
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    /// <summary>Mirrors WinForms' own
    /// <c>FormsMenuEditingTests.PopupTypeHere_Click_CreateItem_AndUndoRemovesIt</c>: click the
    /// trailing "Type Here" insertion slot (the ContextMenu tray's own, and the top-level Menu's
    /// on-canvas hotspot), type a new sibling's Header, commit, and confirm undo removes it again.
    /// Also exercises the commit-via-Enter re-arm (typing two items back-to-back with no re-click
    /// between them) on the ContextMenu tray, matching CommitTypeHere's own re-arm behavior.</summary>
    [Fact]
    public async Task MenuItem_TypeHere_ClicksAddNewSiblings_AndUndoRemovesThem()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Type Here">
              <DockPanel>
                <Menu x:Name="mainMenu" DockPanel.Dock="Top"><MenuItem x:Name="fileItem" Header="File" /></Menu>
                <Button x:Name="owner" Content="Open">
                  <Button.ContextMenu><ContextMenu x:Name="actions"><MenuItem x:Name="rename" Header="Rename" /></ContextMenu></Button.ContextMenu>
                </Button>
              </DockPanel>
            </Window>
            """);
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());
            await app.InvokeAsync("od.activate");

            // --- ContextMenu tray: add two sibling MenuItems back-to-back via commit-on-Enter re-arm ---
            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "actions")).GetProperty("success").GetBoolean());
            JsonElement tray = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                tray = await app.InvokeAsync("od.wpf-designer.context-menu-tray-status");
                return tray.GetProperty("visible").GetBoolean() && tray.GetProperty("items").GetArrayLength() == 2;
            }, TimeSpan.FromSeconds(10)), tray.ToString());
            var typeHere = tray.GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("elementId").GetString() == "@type-here");
            Assert.True((await app.ClickPointerAsync(typeHere.GetProperty("centerX").GetDouble(),
                typeHere.GetProperty("centerY").GetDouble())).GetProperty("ok").GetBoolean());

            JsonElement editor = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                editor = await app.InvokeAsync("od.wpf-designer.inline-editor-status");
                return editor.GetProperty("editing").GetBoolean() && editor.GetProperty("focused").GetBoolean();
            }, TimeSpan.FromSeconds(10)), editor.ToString());
            // The editor must open ON the "Type Here" slot it was clicked at, not jump to the
            // design surface's own origin - a real regression once caught only functionally (typed
            // text still landed correctly) while visually landing at the top of the surface.
            AssertNear(typeHere.GetProperty("centerX").GetDouble(), editor.GetProperty("screenCenterX").GetDouble());
            AssertNear(typeHere.GetProperty("centerY").GetDouble(), editor.GetProperty("screenCenterY").GetDouble());
            // Commit via Enter (cancel: false), which re-arms the same slot for another item.
            Assert.True((await app.InvokeAsync("od.wpf-designer.inline-editor-input", "Delete", false))
                .GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.Contains("Header=\"Delete\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                editor = await app.InvokeAsync("od.wpf-designer.inline-editor-status");
                return editor.GetProperty("editing").GetBoolean() && editor.GetProperty("focused").GetBoolean();
            }, TimeSpan.FromSeconds(10)), editor.ToString());
            // Re-armed via Enter with no re-click - re-query the tray for its now-rebuilt trailing
            // slot and assert the auto-reopened editor followed it there too.
            var rearmedTray = await app.InvokeAsync("od.wpf-designer.context-menu-tray-status");
            var rearmedTypeHere = rearmedTray.GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("elementId").GetString() == "@type-here");
            AssertNear(rearmedTypeHere.GetProperty("centerX").GetDouble(), editor.GetProperty("screenCenterX").GetDouble());
            AssertNear(rearmedTypeHere.GetProperty("centerY").GetDouble(), editor.GetProperty("screenCenterY").GetDouble());
            Assert.True((await app.InvokeAsync("od.wpf-designer.inline-editor-input", "Cut", false))
                .GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            var afterBothAdds = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("Header=\"Rename\"", afterBothAdds, StringComparison.Ordinal);
            Assert.Contains("Header=\"Delete\"", afterBothAdds, StringComparison.Ordinal);
            Assert.Contains("Header=\"Cut\"", afterBothAdds, StringComparison.Ordinal);

            // Undo twice removes both newly-added items, restoring the original single "Rename".
            Assert.True((await app.InvokeAsync("od.wpf-designer.undo")).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.wpf-designer.undo")).GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            var afterUndo = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("Header=\"Rename\"", afterUndo, StringComparison.Ordinal);
            Assert.DoesNotContain("Header=\"Delete\"", afterUndo, StringComparison.Ordinal);
            Assert.DoesNotContain("Header=\"Cut\"", afterUndo, StringComparison.Ordinal);

            // --- Top-level Menu: the same "Type Here" flow via its on-canvas hotspot ---
            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "fileItem")).GetProperty("success").GetBoolean());
            JsonElement hotspot = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                hotspot = await app.InvokeAsync("od.wpf-designer.menu-type-here-status");
                return hotspot.GetProperty("visible").GetBoolean();
            }, TimeSpan.FromSeconds(10)), hotspot.ToString());
            // The very first synthetic click after a select immediately following two undos
            // occasionally lands with no WPF element receiving it at all (a narrower race than
            // ClickPointerAsync's own retry-activate loop covers - the window reports itself
            // foregrounded before the click is actually deliverable) - a second click at the exact
            // same position then succeeds, confirmed live. Retry once rather than treat it as the
            // hotspot-position bug this fact otherwise guards (see wpf-designer.md, Bug 3).
            var opened = false;
            for (var attempt = 0; attempt < 2 && !opened; attempt++)
            {
                Assert.True((await app.ClickPointerAsync(hotspot.GetProperty("centerX").GetDouble(),
                    hotspot.GetProperty("centerY").GetDouble())).GetProperty("ok").GetBoolean());
                opened = await OpenDevelopAppFixture.PollUntilAsync(async () => {
                    editor = await app.InvokeAsync("od.wpf-designer.inline-editor-status");
                    return editor.GetProperty("editing").GetBoolean() && editor.GetProperty("focused").GetBoolean();
                }, TimeSpan.FromSeconds(attempt == 0 ? 3 : 10));
            }
            Assert.True(opened, editor.ToString());
            AssertNear(hotspot.GetProperty("centerX").GetDouble(), editor.GetProperty("screenCenterX").GetDouble());
            AssertNear(hotspot.GetProperty("centerY").GetDouble(), editor.GetProperty("screenCenterY").GetDouble());
            Assert.True((await app.InvokeAsync("od.wpf-designer.inline-editor-input", "Edit", false))
                .GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.Contains("Header=\"Edit\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            Assert.True((await app.InvokeAsync("od.wpf-designer.undo")).GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.DoesNotContain("Header=\"Edit\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            await app.InvokeAsync("od.close-active-view");
        } finally {
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    /// <summary>The full "build a menu from scratch" journey the other two Type-Here facts each
    /// only cover one level of: create the Menu's very FIRST top-level item via its on-canvas
    /// hotspot (starting from a Menu with zero items, not one pre-seeded in the fixture XAML),
    /// then immediately use that freshly-created item's own tray to give IT a first nested child -
    /// proving an item just created via Type-Here is instantly usable as a container for further
    /// Type-Here editing, not just as a stepping stone for adding more siblings next to it.</summary>
    [Fact]
    public async Task MenuItem_CreateTopLevelThenNestChild_ViaTypeHereBothLevels_AndUndoRestoresBoth()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);
        try {
            // mainMenu starts genuinely empty - no MenuItems at all - unlike every other
            // Type-Here fact in this file, which all start from a Menu/ContextMenu that already
            // has at least one item.
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Build Menu From Scratch">
              <Menu x:Name="mainMenu" />
            </Window>
            """);
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());
            await app.InvokeAsync("od.activate");

            // --- Level 1: create the Menu's first top-level item via its on-canvas hotspot ---
            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "mainMenu")).GetProperty("success").GetBoolean());
            JsonElement hotspot = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                hotspot = await app.InvokeAsync("od.wpf-designer.menu-type-here-status");
                return hotspot.GetProperty("visible").GetBoolean();
            }, TimeSpan.FromSeconds(10)), hotspot.ToString());
            Assert.True((await app.ClickPointerAsync(hotspot.GetProperty("centerX").GetDouble(),
                hotspot.GetProperty("centerY").GetDouble())).GetProperty("ok").GetBoolean());
            JsonElement editor = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                editor = await app.InvokeAsync("od.wpf-designer.inline-editor-status");
                return editor.GetProperty("editing").GetBoolean() && editor.GetProperty("focused").GetBoolean();
            }, TimeSpan.FromSeconds(10)), editor.ToString());
            Assert.True((await app.InvokeAsync("od.wpf-designer.inline-editor-input", "File", false))
                .GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.Contains("Header=\"File\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            // --- Level 2: select that SAME freshly-created item (Type-Here assigns no x:Name, so
            // it is the only unnamed MenuItem in the tree and resolvable by type), then use ITS
            // OWN tray to create its first nested child. ---
            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "MenuItem")).GetProperty("success").GetBoolean());
            JsonElement tray = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                tray = await app.InvokeAsync("od.wpf-designer.context-menu-tray-status");
                // The item has zero children of its own yet - just the trailing "Type Here" slot.
                return tray.GetProperty("visible").GetBoolean() && tray.GetProperty("items").GetArrayLength() == 1;
            }, TimeSpan.FromSeconds(10)), tray.ToString());
            var typeHere = tray.GetProperty("items")[0];
            Assert.Equal("@type-here", typeHere.GetProperty("elementId").GetString());
            Assert.True((await app.ClickPointerAsync(typeHere.GetProperty("centerX").GetDouble(),
                typeHere.GetProperty("centerY").GetDouble())).GetProperty("ok").GetBoolean());
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                editor = await app.InvokeAsync("od.wpf-designer.inline-editor-status");
                return editor.GetProperty("editing").GetBoolean() && editor.GetProperty("focused").GetBoolean();
            }, TimeSpan.FromSeconds(10)), editor.ToString());
            Assert.True((await app.InvokeAsync("od.wpf-designer.inline-editor-input", "Open", false))
                .GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            var afterBothLevels = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("Header=\"Open\"", afterBothLevels, StringComparison.Ordinal);
            // "Open" is nested INSIDE the "File" MenuItem, not a sibling next to it at the Menu's
            // own top level - the whole point of this fact.
            Assert.Matches(new System.Text.RegularExpressions.Regex(
                "MenuItem[^>]*Header=\"File\"[^>]*>.*Header=\"Open\"", System.Text.RegularExpressions.RegexOptions.Singleline), afterBothLevels);

            // Undo twice: first removes "Open" (level 2), then removes "File" itself (level 1),
            // restoring the Menu to its original, genuinely empty state.
            Assert.True((await app.InvokeAsync("od.wpf-designer.undo")).GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            var afterFirstUndo = await File.ReadAllTextAsync(xamlPath);
            Assert.DoesNotContain("Header=\"Open\"", afterFirstUndo, StringComparison.Ordinal);
            Assert.Contains("Header=\"File\"", afterFirstUndo, StringComparison.Ordinal);

            Assert.True((await app.InvokeAsync("od.wpf-designer.undo")).GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.DoesNotContain("Header=\"File\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            await app.InvokeAsync("od.close-active-view");
        } finally {
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    /// <summary>Selecting a childless top-level MenuItem shows BOTH the on-canvas "Type Here"
    /// hotspot (adds a SIBLING at the Menu bar's own level) and the tray (showing that same item's
    /// own, currently-empty, submenu) at once - these are two different, simultaneously available
    /// operations on one selection. Using the tray's "Type Here" slot creates the item's first
    /// nested child, turning a plain leaf item into one with a real dropdown submenu.</summary>
    [Fact]
    public async Task MenuItem_TypeHere_InTray_CreatesFirstNestedSubmenuItem_AndUndoRemovesIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Nested Submenu">
              <Menu x:Name="mainMenu"><MenuItem x:Name="fileItem" Header="File" /></Menu>
            </Window>
            """);
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());
            await app.InvokeAsync("od.activate");

            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "fileItem")).GetProperty("success").GetBoolean());

            // Both affordances show up together for the same selection.
            JsonElement hotspot = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                hotspot = await app.InvokeAsync("od.wpf-designer.menu-type-here-status");
                return hotspot.GetProperty("visible").GetBoolean();
            }, TimeSpan.FromSeconds(10)), hotspot.ToString());
            JsonElement tray = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                tray = await app.InvokeAsync("od.wpf-designer.context-menu-tray-status");
                // fileItem currently has zero children of its own - just the trailing slot.
                return tray.GetProperty("visible").GetBoolean() && tray.GetProperty("items").GetArrayLength() == 1;
            }, TimeSpan.FromSeconds(10)), tray.ToString());

            var typeHere = tray.GetProperty("items")[0];
            Assert.Equal("@type-here", typeHere.GetProperty("elementId").GetString());
            Assert.True((await app.ClickPointerAsync(typeHere.GetProperty("centerX").GetDouble(),
                typeHere.GetProperty("centerY").GetDouble())).GetProperty("ok").GetBoolean());

            JsonElement editor = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                editor = await app.InvokeAsync("od.wpf-designer.inline-editor-status");
                return editor.GetProperty("editing").GetBoolean() && editor.GetProperty("focused").GetBoolean();
            }, TimeSpan.FromSeconds(10)), editor.ToString());
            AssertNear(typeHere.GetProperty("centerX").GetDouble(), editor.GetProperty("screenCenterX").GetDouble());
            AssertNear(typeHere.GetProperty("centerY").GetDouble(), editor.GetProperty("screenCenterY").GetDouble());
            Assert.True((await app.InvokeAsync("od.wpf-designer.inline-editor-input", "Open", false))
                .GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            var afterAdd = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("Header=\"Open\"", afterAdd, StringComparison.Ordinal);
            // The new item is nested INSIDE fileItem, not a sibling next to it.
            Assert.Matches(new System.Text.RegularExpressions.Regex(
                "MenuItem[^>]*x:Name=\"fileItem\"[^>]*>.*Header=\"Open\"", System.Text.RegularExpressions.RegexOptions.Singleline), afterAdd);

            Assert.True((await app.InvokeAsync("od.wpf-designer.undo")).GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.DoesNotContain("Header=\"Open\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            await app.InvokeAsync("od.close-active-view");
        } finally {
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    /// <summary>The tray's reorder arrows (WinForms drag-reorder parity's simpler stand-in - see
    /// design/move-element's own doc comment) and Delete-key removal of a selected MenuItem, driven
    /// through the real tray UI and the existing generic od.wpf-designer.delete action.</summary>
    [Fact]
    public async Task ContextMenuTray_ReorderArrowsAndDelete_MutateAndUndoRestore()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Reorder and delete">
              <Button x:Name="owner" Content="Open">
                <Button.ContextMenu>
                  <ContextMenu x:Name="actions">
                    <MenuItem x:Name="renameItem" Header="Rename" />
                    <MenuItem x:Name="deleteItem" Header="Delete" />
                    <MenuItem x:Name="propsItem" Header="Properties" />
                  </ContextMenu>
                </Button.ContextMenu>
              </Button>
            </Window>
            """);
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());
            await app.InvokeAsync("od.activate");

            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "actions")).GetProperty("success").GetBoolean());
            JsonElement tray = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                tray = await app.InvokeAsync("od.wpf-designer.context-menu-tray-status");
                // 4: the 3 real items plus the trailing "Type Here" slot.
                return tray.GetProperty("visible").GetBoolean() && tray.GetProperty("items").GetArrayLength() == 4;
            }, TimeSpan.FromSeconds(10)), tray.ToString());

            // Move "renameItem" down one step by dragging its reorder grip onto "deleteItem"'s row:
            // Rename, Delete, Properties -> Delete, Rename, Properties.
            var rename = tray.GetProperty("items")[0];
            var deleteRowBeforeMove = tray.GetProperty("items")[1];
            await app.InvokeAsync("od.activate");
            var gripX = rename.GetProperty("reorderGripCenterX").GetDouble();
            var gripY = rename.GetProperty("reorderGripCenterY").GetDouble();
            var targetY = deleteRowBeforeMove.GetProperty("centerY").GetDouble();
            Assert.True((await app.PressPointerAsync(gripX, gripY)).GetProperty("ok").GetBoolean());
            for (int step = 1; step <= 4; step++)
            {
                var t = step / 4.0;
                Assert.True((await app.DragMovePointerAsync(gripX, gripY + (targetY - gripY) * t)).GetProperty("ok").GetBoolean());
            }
            Assert.True((await app.ReleasePointerAsync(gripX, targetY)).GetProperty("ok").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            var afterMove = await File.ReadAllTextAsync(xamlPath);
            Assert.True(afterMove.IndexOf("Header=\"Delete\"", StringComparison.Ordinal)
                < afterMove.IndexOf("Header=\"Rename\"", StringComparison.Ordinal));

            Assert.True((await app.InvokeAsync("od.wpf-designer.undo")).GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            var afterMoveUndo = await File.ReadAllTextAsync(xamlPath);
            Assert.True(afterMoveUndo.IndexOf("Header=\"Rename\"", StringComparison.Ordinal)
                < afterMoveUndo.IndexOf("Header=\"Delete\"", StringComparison.Ordinal));

            // Delete "deleteItem" outright via the existing generic delete action, selecting it
            // through the tray first (the tray must have been rebuilt after undo, so re-fetch it).
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                tray = await app.InvokeAsync("od.wpf-designer.context-menu-tray-status");
                return tray.GetProperty("visible").GetBoolean() && tray.GetProperty("items").GetArrayLength() == 4;
            }, TimeSpan.FromSeconds(10)), tray.ToString());
            var deleteRow = tray.GetProperty("items")[1];
            Assert.True((await app.ClickPointerAsync(deleteRow.GetProperty("centerX").GetDouble(),
                deleteRow.GetProperty("centerY").GetDouble())).GetProperty("ok").GetBoolean());
            var deleteResult = await app.InvokeAsync("od.wpf-designer.delete");
            Assert.True(deleteResult.GetProperty("success").GetBoolean(), deleteResult.ToString());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.DoesNotContain("x:Name=\"deleteItem\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            Assert.True((await app.InvokeAsync("od.wpf-designer.undo")).GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.Contains("x:Name=\"deleteItem\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            await app.InvokeAsync("od.close-active-view");
        } finally {
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    /// <summary>The StatusBar/ToolBar equivalent of the Menu bar's own on-canvas "Type Here" hotspot
    /// (od.wpf-designer.menu-type-here-status is reused for both - see UpdateMenuTypeHereHotspot's
    /// own generalization). Both containers render their items inline (no detached popup), so one
    /// hotspot per container, positioned past its last item, is enough - no tray needed.</summary>
    [Fact]
    public async Task StatusBarAndToolBar_TypeHere_AddsAnItem_AndUndoRemovesIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Strip bars">
              <DockPanel>
                <ToolBarTray DockPanel.Dock="Top"><ToolBar x:Name="mainToolBar"><Button x:Name="saveButton" Content="Save" /></ToolBar></ToolBarTray>
                <StatusBar x:Name="mainStatusBar" DockPanel.Dock="Bottom"><StatusBarItem x:Name="readyItem" Content="Ready" /></StatusBar>
              </DockPanel>
            </Window>
            """);
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());
            await app.InvokeAsync("od.activate");

            // --- ToolBar: add a Button via its Type-Here hotspot ---
            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "mainToolBar")).GetProperty("success").GetBoolean());
            JsonElement hotspot = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                hotspot = await app.InvokeAsync("od.wpf-designer.menu-type-here-status");
                return hotspot.GetProperty("visible").GetBoolean();
            }, TimeSpan.FromSeconds(10)), hotspot.ToString());
            Assert.True((await app.ClickPointerAsync(hotspot.GetProperty("centerX").GetDouble(),
                hotspot.GetProperty("centerY").GetDouble())).GetProperty("ok").GetBoolean());
            JsonElement editor = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                editor = await app.InvokeAsync("od.wpf-designer.inline-editor-status");
                return editor.GetProperty("editing").GetBoolean() && editor.GetProperty("focused").GetBoolean();
            }, TimeSpan.FromSeconds(10)), editor.ToString());
            AssertNear(hotspot.GetProperty("centerX").GetDouble(), editor.GetProperty("screenCenterX").GetDouble());
            AssertNear(hotspot.GetProperty("centerY").GetDouble(), editor.GetProperty("screenCenterY").GetDouble());
            Assert.True((await app.InvokeAsync("od.wpf-designer.inline-editor-input", "Open", false))
                .GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            var afterToolBarAdd = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("Content=\"Open\"", afterToolBarAdd, StringComparison.Ordinal);
            Assert.Contains("Content=\"Save\"", afterToolBarAdd, StringComparison.Ordinal);

            Assert.True((await app.InvokeAsync("od.wpf-designer.undo")).GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.DoesNotContain("Content=\"Open\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            // --- StatusBar: same flow, a StatusBarItem instead of a Button ---
            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "mainStatusBar")).GetProperty("success").GetBoolean());
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                hotspot = await app.InvokeAsync("od.wpf-designer.menu-type-here-status");
                return hotspot.GetProperty("visible").GetBoolean();
            }, TimeSpan.FromSeconds(10)), hotspot.ToString());
            Assert.True((await app.ClickPointerAsync(hotspot.GetProperty("centerX").GetDouble(),
                hotspot.GetProperty("centerY").GetDouble())).GetProperty("ok").GetBoolean());
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                editor = await app.InvokeAsync("od.wpf-designer.inline-editor-status");
                return editor.GetProperty("editing").GetBoolean() && editor.GetProperty("focused").GetBoolean();
            }, TimeSpan.FromSeconds(10)), editor.ToString());
            Assert.True((await app.InvokeAsync("od.wpf-designer.inline-editor-input", "Line 1, Col 1", false))
                .GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            var afterStatusBarAdd = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("Content=\"Line 1, Col 1\"", afterStatusBarAdd, StringComparison.Ordinal);
            Assert.Contains("Content=\"Ready\"", afterStatusBarAdd, StringComparison.Ordinal);

            Assert.True((await app.InvokeAsync("od.wpf-designer.undo")).GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.DoesNotContain("Content=\"Line 1, Col 1\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            await app.InvokeAsync("od.close-active-view");
        } finally {
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    /// <summary>The editor's reported top-left and the anchor's reported center are never expected
    /// to match exactly (the anchor's own padding/border), but a real mispositioning bug (jumping
    /// to the design surface's origin) is off by tens to hundreds of pixels - a generous tolerance
    /// still catches that class of bug without being sensitive to exact padding math.</summary>
    static void AssertNear(double expected, double actual, double tolerance = 30)
        => Assert.True(Math.Abs(expected - actual) <= tolerance,
            $"expected {expected} within {tolerance} of {actual}");
}
