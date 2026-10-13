using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("30 Add-ins and specialized fixtures")]
public sealed class WpfStripEditingTests(OpenDevelopAppFixture app)
{
	[Fact]
	public async Task CtrlPointerGestures_DistinguishClickCollapseFromGroupDrag()
	{
		// The packaged native key holder currently backs keydown/keyup on macOS. Windows has
		// native mouse injection but does not yet implement the matching held-key endpoint.
		if (!OperatingSystem.IsMacOS()) return;

		// Exercise the actual native modifier state. The shared canvas delays selection until
		// mouse-up: a Ctrl-click on a selected member collapses to it, while a Ctrl-drag takes
		// the group-drag path and must keep the whole selection.
		var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
		var xamlPath = Path.Combine(sample, "MainWindow.xaml");
		var originalXaml = await File.ReadAllTextAsync(xamlPath);
		try {
			await File.WriteAllTextAsync(xamlPath, """
			<Window x:Class="sample.MainWindow"
			        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
			        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
			        Width="420" Height="220" Title="Ctrl pointer gestures">
			  <Canvas>
			    <Button x:Name="first" Canvas.Left="30" Canvas.Top="30" Width="100" Height="40" Content="First" />
			    <Button x:Name="second" Canvas.Left="220" Canvas.Top="30" Width="100" Height="40" Content="Second" />
			  </Canvas>
			</Window>
			""");
			Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
			Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
			JsonElement status = default;
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				status = await app.InvokeAsync("od.wpf-designer.status");
				return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
			}, TimeSpan.FromSeconds(90)), status.ToString());

			// The canvas reads Keyboard.Modifiers when the click completes. A native keydown sent
			// while the window is still becoming active can be missed, turning the Ctrl-click into
			// a plain click; confirm the IDE sees Ctrl before pressing, and retry the hold if not.
			async Task HoldCtrlAsync()
			{
				JsonElement modifiers = default;
				for (var attempt = 1; attempt <= 3; attempt++) {
					Assert.True((await app.KeyDownAsync("ctrl")).GetProperty("ok").GetBoolean());
					if (await OpenDevelopAppFixture.PollUntilAsync(async () => {
						modifiers = await app.InvokeAsync("od.keyboard.modifiers");
						return modifiers.GetProperty("control").GetBoolean();
					}, TimeSpan.FromSeconds(2), initialDelayMs: 50, maxDelayMs: 200))
						return;
					await app.KeyUpAsync("ctrl");
				}
				Assert.Fail("The IDE never observed the held Ctrl key: " + modifiers);
			}

			var initial = await app.InvokeAsync("od.wpf-designer.multi-select", "first,second");
			var initialIds = initial.GetProperty("selectedIds").EnumerateArray().Select(id => id.GetString()).ToArray();
			Assert.Equal(2, initialIds.Length);
			var second = await app.InvokeAsync("od.wpf-designer.query-element-screen-bounds", "second");
			Assert.True(second.GetProperty("success").GetBoolean(), second.ToString());
			var secondX = second.GetProperty("centerX").GetDouble();
			var secondY = second.GetProperty("centerY").GetDouble();

			await HoldCtrlAsync();
			Exception? clickFailure = null;
			try {
				Assert.True((await app.PressPointerAsync(secondX, secondY, ensureForeground: false)).GetProperty("ok").GetBoolean());
				Assert.True((await app.ReleasePointerAsync(secondX, secondY)).GetProperty("ok").GetBoolean());
			}
			catch (Exception ex) {
				clickFailure = ex;
				throw;
			}
			finally {
				var released = await app.KeyUpAsync("ctrl");
				if (clickFailure is null)
					Assert.True(released.GetProperty("ok").GetBoolean(), released.ToString());
			}

			// This catches the former state where second was reported as primary although only
			// first remained selected, without adding a document mutation just to inspect it.
			var collapsed = await app.InvokeAsync("od.wpf-designer.status");
			Assert.Equal(new[] { initialIds[1] }, collapsed.GetProperty("selectedIds").EnumerateArray().Select(id => id.GetString()).ToArray());

			// A lone selection keeps the established Ctrl-toggle semantics. Poll because the
			// selection owner refreshes the exposed status on the next UI dispatch turn.
			// The surface also recognizes a double-click within 500 ms for inline edit, so make
			// this an independent Ctrl-click rather than accidentally taking that gesture path.
			await Task.Delay(550);
			await HoldCtrlAsync();
			Exception? deselectFailure = null;
			try {
				Assert.True((await app.PressPointerAsync(secondX, secondY, ensureForeground: false)).GetProperty("ok").GetBoolean());
				Assert.True((await app.ReleasePointerAsync(secondX, secondY)).GetProperty("ok").GetBoolean());
			}
			catch (Exception ex) {
				deselectFailure = ex;
				throw;
			}
			finally {
				var released = await app.KeyUpAsync("ctrl");
				if (deselectFailure is null)
					Assert.True(released.GetProperty("ok").GetBoolean(), released.ToString());
			}
			JsonElement deselected = default;
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				deselected = await app.InvokeAsync("od.wpf-designer.status");
				return deselected.GetProperty("selectedIds").GetArrayLength() == 0;
			}, TimeSpan.FromSeconds(5)), deselected.ToString());

			var regrouped = await app.InvokeAsync("od.wpf-designer.multi-select", "first,second");
			Assert.Equal(2, regrouped.GetProperty("selectionCount").GetInt32());
			var first = await app.InvokeAsync("od.wpf-designer.query-element-screen-bounds", "first");
			Assert.True(first.GetProperty("success").GetBoolean(), first.ToString());
			var firstX = first.GetProperty("centerX").GetDouble();
			var firstY = first.GetProperty("centerY").GetDouble();

			await HoldCtrlAsync();
			Exception? dragFailure = null;
			try {
				Assert.True((await app.PressPointerAsync(firstX, firstY, ensureForeground: false)).GetProperty("ok").GetBoolean());
				await app.DragPointerInStepsAsync(firstX, firstY, firstX + 24, firstY + 12, steps: 4);
			}
			catch (Exception ex) {
				dragFailure = ex;
				throw;
			}
			finally {
				var released = await app.KeyUpAsync("ctrl");
				if (dragFailure is null)
					Assert.True(released.GetProperty("ok").GetBoolean(), released.ToString());
			}

			var retained = await app.InvokeAsync("od.wpf-designer.status");
			Assert.Equal(initialIds, retained.GetProperty("selectedIds").EnumerateArray().Select(id => id.GetString()).ToArray());
		} finally {
			try {
				await app.InvokeAsync("od.close-active-view");
			}
			catch {
				// Preserve the original test failure; the source restoration below is still required.
			}
			await File.WriteAllTextAsync(xamlPath, originalXaml);
		}
	}

    [Fact]
    public async Task InlineEditor_ShiftEnterInsertsATextBlockLineBreak()
    {
        // Native held keys are implemented for macOS; the Windows strip journeys cover Enter.
        if (!OperatingSystem.IsMacOS()) return;

        // The in-process WPF InPlaceEditor contract: Enter commits, Shift+Enter inserts a newline
        // in a TextBlock's text, Escape cancels.
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Inline line break">
              <Canvas><TextBlock x:Name="caption" Canvas.Left="30" Canvas.Top="30" Width="160" Height="60" Text="Line1" /></Canvas>
            </Window>
            """);
            await app.InvokeAsync("od.close-all-document-views");
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());

            // A view restored from an earlier journey on this file can leave the TextBlock panned
            // outside the visible canvas, where a native double-click lands on nothing. Fit first,
            // then require the target to be inside the visible viewport.
            Assert.True((await app.InvokeAsync("od.designer-canvas.fit")).GetProperty("success").GetBoolean());
            JsonElement bounds = default;
            JsonElement geometry = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                bounds = await app.InvokeAsync("od.wpf-designer.query-element-screen-bounds", "caption");
                geometry = await app.InvokeAsync("od.wpf-designer.surface-geometry");
                if (!bounds.GetProperty("success").GetBoolean() || !geometry.TryGetProperty("viewport", out var viewport) || viewport.ValueKind != JsonValueKind.Object)
                    return false;
                var cx = bounds.GetProperty("centerX").GetDouble();
                var cy = bounds.GetProperty("centerY").GetDouble();
                var vx = viewport.GetProperty("x").GetDouble();
                var vy = viewport.GetProperty("y").GetDouble();
                return cx > vx && cy > vy && cx < vx + viewport.GetProperty("width").GetDouble() && cy < vy + viewport.GetProperty("height").GetDouble();
            }, TimeSpan.FromSeconds(5)), "The TextBlock is outside the visible canvas: " + bounds + " " + geometry);
            // A native double-click can be lost while the window is still becoming active, or the
            // editor can lose focus (which commits and closes it); re-activate and retry, as the
            // other native pointer journeys do.
            JsonElement editor = default;
            var opened = false;
            for (var attempt = 1; attempt <= 3 && !opened; attempt++) {
                await app.InvokeAsync("od.activate");
                Assert.True((await app.ClickPointerAsync(bounds.GetProperty("centerX").GetDouble(), bounds.GetProperty("centerY").GetDouble(), clickCount: 2))
                    .GetProperty("ok").GetBoolean());
                opened = await OpenDevelopAppFixture.PollUntilAsync(async () => {
                    editor = await app.InvokeAsync("od.wpf-designer.inline-editor-status");
                    return editor.GetProperty("editing").GetBoolean() && editor.GetProperty("focused").GetBoolean();
                }, TimeSpan.FromSeconds(3));
                if (!opened)
                    await Task.Delay(600); // keep the next press out of the 500 ms double-click window
            }
            Assert.True(opened, editor.ToString());
            Assert.Equal("Text", editor.GetProperty("propertyName").GetString());

            // Shift+Enter through the editor's real key handler keeps the editor open with a newline.
            Assert.True((await app.KeyDownAsync("shift")).GetProperty("ok").GetBoolean());
            JsonElement shiftUp = default;
            try {
                Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () =>
                    (await app.InvokeAsync("od.keyboard.modifiers")).GetProperty("shift").GetBoolean(),
                    TimeSpan.FromSeconds(2), initialDelayMs: 50, maxDelayMs: 200), "The IDE never observed the held Shift key.");
                Assert.True((await app.InvokeAsync("od.wpf-designer.inline-editor-input", "Line1", false)).GetProperty("success").GetBoolean());
            } finally {
                shiftUp = await app.KeyUpAsync("shift");
            }
            // The next Enter must be a plain Enter: wait until the IDE has seen the key-up.
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () =>
                !(await app.InvokeAsync("od.keyboard.modifiers")).GetProperty("shift").GetBoolean(),
                TimeSpan.FromSeconds(5), initialDelayMs: 50, maxDelayMs: 250), "The IDE still reports Shift held after its key-up: " + shiftUp);
            editor = await app.InvokeAsync("od.wpf-designer.inline-editor-status");
            Assert.True(editor.GetProperty("editing").GetBoolean(), editor.ToString());
            Assert.Equal("Line1\n", editor.GetProperty("text").GetString());

            // A plain Enter then commits the multi-line text to the TextBlock.
            Assert.True((await app.InvokeAsync("od.wpf-designer.inline-editor-input", "Line1\nLine2", false)).GetProperty("success").GetBoolean());
            editor = await app.InvokeAsync("od.wpf-designer.inline-editor-status");
            Assert.False(editor.GetProperty("editing").GetBoolean(), editor.ToString());
            var saved = await app.InvokeAsync("od.file.save", xamlPath);
            Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
            Assert.Contains("Text=\"Line1&#xA;Line2\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);
        } finally {
            await app.InvokeAsync("od.close-active-view");
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    public async Task ContextMenu_QuickPropertiesSetAlignmentOrientationAndDock()
    {
        // The in-process QuickOperationMenu: alignment for any element, Orientation for a StackPanel
        // and Dock for a DockPanel child, each as one property edit.
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllBytesAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Quick properties">
              <DockPanel x:Name="dock">
                <StackPanel x:Name="stack"><TextBlock Text="Item" /></StackPanel>
              </DockPanel>
            </Window>
            """);
            await app.InvokeAsync("od.close-all-document-views");
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());
            Dictionary<string, bool> Commands(JsonElement result)
                => result.GetProperty("commands").EnumerateArray().ToDictionary(c => c.GetProperty("command").GetString()!, c => c.GetProperty("enabled").GetBoolean());

            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "stack")).GetProperty("success").GetBoolean());
            var commands = Commands(await app.InvokeAsync("od.wpf-designer.context-commands"));
            Assert.False(commands["set:Orientation:Vertical"]);
            Assert.False(commands["set:HorizontalAlignment:Stretch"]);
            Assert.True(commands["set:DockPanel.Dock:Top"]);
            foreach (var command in new[] { "set:Orientation:Horizontal", "set:HorizontalAlignment:Left", "set:DockPanel.Dock:Top" }) {
                var result = await app.InvokeAsync("od.wpf-designer.context-command", command);
                Assert.True(result.GetProperty("success").GetBoolean(), command + ": " + result);
            }
            Assert.False(Commands(await app.InvokeAsync("od.wpf-designer.context-commands"))["set:Orientation:Horizontal"]);

            var saved = await app.InvokeAsync("od.file.save", xamlPath);
            Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
            var text = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("Orientation=\"Horizontal\"", text, StringComparison.Ordinal);
            Assert.Contains("HorizontalAlignment=\"Left\"", text, StringComparison.Ordinal);
            Assert.Contains("DockPanel.Dock=\"Top\"", text, StringComparison.Ordinal);

            // The DockPanel is not a StackPanel and has no DockPanel parent.
            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "dock")).GetProperty("success").GetBoolean());
            commands = Commands(await app.InvokeAsync("od.wpf-designer.context-commands"));
            Assert.False(commands["set:Orientation:Horizontal"]);
            Assert.False(commands["set:DockPanel.Dock:Top"]);
        } finally {
            await app.InvokeAsync("od.close-all-document-views");
            await File.WriteAllBytesAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    public async Task ContextMenu_GridRowAndColumnUnitsChangeTheTrackUnderThePointer()
    {
        // The in-process Grid rail's GridUnitSelector (Fixed/Star/Auto) as context-menu commands for
        // the selected Grid's row/column under the right-click point.
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllBytesAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Grid units">
              <Grid x:Name="layout">
                <Grid.RowDefinitions><RowDefinition Height="40" /><RowDefinition Height="*" /></Grid.RowDefinitions>
                <Grid.ColumnDefinitions><ColumnDefinition Width="100" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
                <TextBlock x:Name="label" Grid.Row="1" Text="Units" />
              </Grid>
            </Window>
            """);
            await app.InvokeAsync("od.close-all-document-views");
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());
            Dictionary<string, bool> Commands(JsonElement result)
                => result.GetProperty("commands").EnumerateArray().ToDictionary(c => c.GetProperty("command").GetString()!, c => c.GetProperty("enabled").GetBoolean());

            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "layout")).GetProperty("success").GetBoolean());
            // Inside row 0 (40 px, Fixed) and column 0 (100 px, Fixed): the current unit is disabled.
            Assert.True((await app.InvokeAsync("od.wpf-designer.context-menu-point", 20, 10)).GetProperty("success").GetBoolean());
            var commands = Commands(await app.InvokeAsync("od.wpf-designer.context-commands"));
            Assert.False(commands["grid-row-Pixel"]);
            Assert.True(commands["grid-row-Star"]);
            Assert.True(commands["grid-column-Auto"]);
            var row = await app.InvokeAsync("od.wpf-designer.context-command", "grid-row-Star");
            Assert.True(row.GetProperty("success").GetBoolean(), row.ToString());
            var column = await app.InvokeAsync("od.wpf-designer.context-command", "grid-column-Auto");
            Assert.True(column.GetProperty("success").GetBoolean(), column.ToString());

            var saved = await app.InvokeAsync("od.file.save", xamlPath);
            Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
            var text = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("Height=\"40*\"", text, StringComparison.Ordinal);
            Assert.Contains("Width=\"Auto\"", text, StringComparison.Ordinal);

            // A label is not a Grid: no unit command applies.
            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "label")).GetProperty("success").GetBoolean());
            Assert.False(Commands(await app.InvokeAsync("od.wpf-designer.context-commands"))["grid-row-Auto"]);
        } finally {
            await app.InvokeAsync("od.close-all-document-views");
            await File.WriteAllBytesAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    public async Task ContextMenu_AlignOrderAndWrapCommandsEditTheDocument()
    {
        // The in-process designer's ArrangeItems and RightClick context menus: Align needs a
        // multi-selection, Order a single element; both land as source edits.
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Context commands">
              <Canvas>
                <Button x:Name="first" Canvas.Left="30" Canvas.Top="30" Width="100" Height="40" Content="First" />
                <Button x:Name="second" Canvas.Left="220" Canvas.Top="90" Width="100" Height="40" Content="Second" />
              </Canvas>
            </Window>
            """);
            await app.InvokeAsync("od.close-all-document-views");
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());
            Dictionary<string, bool> Commands(JsonElement result)
                => result.GetProperty("commands").EnumerateArray().ToDictionary(c => c.GetProperty("command").GetString()!, c => c.GetProperty("enabled").GetBoolean());

            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "second")).GetProperty("success").GetBoolean());
            var single = Commands(await app.InvokeAsync("od.wpf-designer.context-commands"));
            Assert.False(single["align-left"]);
            Assert.True(single["order-back"]);
            var ordered = await app.InvokeAsync("od.wpf-designer.context-command", "order-back");
            Assert.True(ordered.GetProperty("success").GetBoolean(), ordered.ToString());
            // The moved element stays selected under its new path id.
            status = await app.InvokeAsync("od.wpf-designer.status");
            Assert.Single(status.GetProperty("selectedIds").EnumerateArray());

            Assert.True((await app.InvokeAsync("od.wpf-designer.multi-select", "first,second")).GetProperty("success").GetBoolean());
            var multi = Commands(await app.InvokeAsync("od.wpf-designer.context-commands"));
            Assert.True(multi["align-left"]);
            Assert.False(multi["order-front"]);
            var aligned = await app.InvokeAsync("od.wpf-designer.context-command", "align-top");
            Assert.True(aligned.GetProperty("success").GetBoolean(), aligned.ToString());

            var saved = await app.InvokeAsync("od.file.save", xamlPath);
            Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
            var text = await File.ReadAllTextAsync(xamlPath);
            Assert.True(text.IndexOf("x:Name=\"second\"", StringComparison.Ordinal) < text.IndexOf("x:Name=\"first\"", StringComparison.Ordinal), text);
            Assert.DoesNotContain("Canvas.Top=\"90\"", text, StringComparison.Ordinal);

            // Wrap the pair in a Grid (the new Grid is selected, so Unwrap applies), then unwrap.
            Assert.True((await app.InvokeAsync("od.wpf-designer.multi-select", "first,second")).GetProperty("success").GetBoolean());
            Assert.False(Commands(await app.InvokeAsync("od.wpf-designer.context-commands"))["wrap-Viewbox"]);
            var wrapped = await app.InvokeAsync("od.wpf-designer.context-command", "wrap-Grid");
            Assert.True(wrapped.GetProperty("success").GetBoolean(), wrapped.ToString());
            Assert.True(Commands(await app.InvokeAsync("od.wpf-designer.context-commands"))["unwrap"]);
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.Contains("<Grid", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);
            var unwrapped = await app.InvokeAsync("od.wpf-designer.context-command", "unwrap");
            Assert.True(unwrapped.GetProperty("success").GetBoolean(), unwrapped.ToString());
            await app.InvokeAsync("od.file.save", xamlPath);
            var restored = await File.ReadAllTextAsync(xamlPath);
            Assert.DoesNotContain("<Grid", restored, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"first\"", restored, StringComparison.Ordinal);

            // A single element wrapped in a Viewbox keeps its place in the child order; undo restores
            // the document exactly and redo re-applies the wrap in the same position.
            await app.InvokeAsync("od.file.save", xamlPath);
            var beforeWrap = await File.ReadAllTextAsync(xamlPath);
            // Wrap the element that comes FIRST, so a container appended at the end would show.
            var firstName = beforeWrap.IndexOf("x:Name=\"second\"", StringComparison.Ordinal) < beforeWrap.IndexOf("x:Name=\"first\"", StringComparison.Ordinal) ? "second" : "first";
            var otherName = firstName == "first" ? "second" : "first";
            bool ViewboxKeepsWrappedPosition(string text)
                => text.Contains("<Viewbox", StringComparison.Ordinal)
                    && text.IndexOf("<Viewbox", StringComparison.Ordinal) < text.IndexOf("x:Name=\"" + otherName + "\"", StringComparison.Ordinal);
            Assert.True((await app.InvokeAsync("od.wpf-designer.select", firstName)).GetProperty("success").GetBoolean());
            var viewbox = await app.InvokeAsync("od.wpf-designer.context-command", "wrap-Viewbox");
            Assert.True(viewbox.GetProperty("success").GetBoolean(), viewbox.ToString());
            await app.InvokeAsync("od.file.save", xamlPath);
            var wrappedText = await File.ReadAllTextAsync(xamlPath);
            Assert.True(ViewboxKeepsWrappedPosition(wrappedText), wrappedText);
            var undo = await app.InvokeAsync("od.wpf-designer.undo");
            Assert.True(undo.GetProperty("success").GetBoolean(), undo.ToString());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.Equal(beforeWrap, await File.ReadAllTextAsync(xamlPath));
            var redo = await app.InvokeAsync("od.wpf-designer.redo");
            Assert.True(redo.GetProperty("success").GetBoolean(), redo.ToString());
            await app.InvokeAsync("od.file.save", xamlPath);
            var redoneText = await File.ReadAllTextAsync(xamlPath);
            Assert.True(ViewboxKeepsWrappedPosition(redoneText), redoneText);
        } finally {
            await app.InvokeAsync("od.close-active-view");
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    public async Task PropertiesPad_ItemsEditorEditsAListBoxsItems()
    {
        // The in-process CollectionEditor role for plain list items: the Items row's "…" button opens
        // the IDE dialog, and its result is one document edit.
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Items editor">
              <Grid>
                <ListBox x:Name="list">
                  <ListBoxItem x:Name="one" Content="One" />
                  <ListBoxItem Content="Two" />
                </ListBox>
              </Grid>
            </Window>
            """);
            await app.InvokeAsync("od.close-all-document-views");
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());
            JsonElement selected = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                selected = await app.InvokeAsync("od.wpf-designer.select", "list");
                return selected.GetProperty("success").GetBoolean()
                    && selected.GetProperty("propertiesPadSelectedName").GetString() == "list";
            }, TimeSpan.FromSeconds(15), initialDelayMs: 50, maxDelayMs: 250), selected.ToString());

            // Cancelling the dialog makes no document edit.
            var cancelled = await app.InvokeAsync("od.wpf-designer.properties-pad.edit-items", "Items", "rename:0:Ignored;cancel");
            Assert.True(cancelled.GetProperty("success").GetBoolean(), cancelled.ToString());
            Assert.False(cancelled.GetProperty("committed").GetBoolean(), cancelled.ToString());
            Assert.False((await app.InvokeAsync("od.wpf-designer.status")).GetProperty("canUndo").GetBoolean());
            // Moving an item down and back up is not a change, so nothing is committed.
            var noChange = await app.InvokeAsync("od.wpf-designer.properties-pad.edit-items", "Items", "down:0;up:1");
            Assert.True(noChange.GetProperty("success").GetBoolean(), noChange.ToString());
            Assert.False(noChange.GetProperty("committed").GetBoolean(), noChange.ToString());
            Assert.False((await app.InvokeAsync("od.wpf-designer.status")).GetProperty("canUndo").GetBoolean());

            var edited = await app.InvokeAsync("od.wpf-designer.properties-pad.edit-items", "Items", "down:0;rename:1:One!;add:1:Three");
            Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
            Assert.Equal(new[] { "One", "Two" }, edited.GetProperty("before").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(new[] { "Two", "One!", "Three" }, edited.GetProperty("after").EnumerateArray().Select(item => item.GetString()));
            var saved = await app.InvokeAsync("od.file.save", xamlPath);
            Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
            var text = await File.ReadAllTextAsync(xamlPath);
            Assert.True(text.IndexOf("Content=\"Two\"", StringComparison.Ordinal) < text.IndexOf("x:Name=\"one\"", StringComparison.Ordinal), text);
            Assert.Contains("Content=\"One!\"", text, StringComparison.Ordinal);
            Assert.True(text.IndexOf("x:Name=\"one\"", StringComparison.Ordinal) < text.IndexOf("Three", StringComparison.Ordinal), text);
            // One dialog commit is one undo step.
            var undo = await app.InvokeAsync("od.wpf-designer.undo");
            Assert.True(undo.GetProperty("success").GetBoolean(), undo.ToString());
            Assert.False(undo.GetProperty("canUndo").GetBoolean(), undo.ToString());
        } finally {
            await app.InvokeAsync("od.close-active-view");
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    public async Task SnapshotSelection_RemainsResponsiveWhileTheWpfHostRecovers()
    {
        // OpenDevelopAppFixture selects Microsoft WPF on Windows and LibreWPF elsewhere.
        // This journey touches only the protocol snapshot/canvas and normal process lifecycle,
        // so it is deliberately exercised by both hosts.
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Snapshot selection">
              <Grid><Button x:Name="owner" Width="120" Height="40" Content="Open" /></Grid>
            </Window>
            """);
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean()
                    && status.GetProperty("hostAlive").GetBoolean() && status.GetProperty("hostProcessId").GetInt32() > 0;
            }, TimeSpan.FromSeconds(90)), status.ToString());
            var failedProcessId = status.GetProperty("hostProcessId").GetInt32();

            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "owner")).GetProperty("success").GetBoolean());
            var terminated = await app.InvokeAsync("od.wpf-designer.terminate-host");
            Assert.True(terminated.GetProperty("success").GetBoolean(), terminated.ToString());
            Assert.Equal(failedProcessId, terminated.GetProperty("oldHostProcessId").GetInt32());

            // Selection is computed from the last accepted DDP tree in the IDE, not by a blocking
            // child hit-test. It must therefore return immediately even while the shared process
            // has been killed and its automatic replacement is still starting.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var selectedDuringRecovery = await app.InvokeAsync("od.wpf-designer.select", "owner");
            stopwatch.Stop();
            Assert.True(selectedDuringRecovery.GetProperty("success").GetBoolean(), selectedDuringRecovery.ToString());
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "Snapshot selection waited for the dead child: " + stopwatch.Elapsed);

            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("designerLoaded").GetBoolean()
                    && status.GetProperty("hostAlive").GetBoolean()
                    && status.GetProperty("hostRecoveryCount").GetInt32() > 0
                    && status.GetProperty("hostProcessId").GetInt32() != failedProcessId;
            }, TimeSpan.FromSeconds(30)), status.ToString());
            var geometry = await app.InvokeAsync("od.wpf-designer.surface-geometry");
            Assert.True(geometry.GetProperty("selection").GetProperty("width").GetDouble() > 0, geometry.ToString());
            await app.InvokeAsync("od.close-active-view");
        } finally {
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    public async Task PropertiesPad_BindEvent_CreatesTheCodeBehindHandler()
    {
        // The in-process designer's double-click: the XAML attribute is bound and the IDE creates
        // the method in the code-behind. TargetUpdated (an EventHandler<DataTransferEventArgs>) is
        // available through LibreWPF's design model, so this is covered on macOS as well.
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var codeBehindPath = xamlPath + ".cs";
        var originalXaml = await File.ReadAllTextAsync(xamlPath);
        // Bytes, not text: a text round trip would drop the sample's UTF-8 byte order mark.
        var originalCodeBehind = await File.ReadAllBytesAsync(codeBehindPath);
        try {
            await app.InvokeAsync("od.close-all-document-views");
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Event binding">
              <Grid><Button x:Name="owner" Content="Open" /></Grid>
            </Window>
            """);
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());

            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "owner"))
                .GetProperty("success").GetBoolean());
            var binding = await app.InvokeAsync("od.wpf-designer.properties-pad.bind-event", "TargetUpdated");
            Assert.True(binding.GetProperty("success").GetBoolean(), binding.ToString());
            Assert.Equal("owner_TargetUpdated", binding.GetProperty("handler").GetString());

            await app.InvokeAsync("od.file.save", xamlPath);
            var saved = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("TargetUpdated=\"owner_TargetUpdated\"", saved, StringComparison.Ordinal);
            // The code-behind was closed, so the method was written to disk with the event's own
            // argument type.
            var codeBehind = await File.ReadAllTextAsync(codeBehindPath);
            // Rewriting the closed file keeps its UTF-8 byte order mark.
            Assert.Equal(originalCodeBehind.Take(3), (await File.ReadAllBytesAsync(codeBehindPath)).Take(3));
            Assert.Contains("private void owner_TargetUpdated(object sender, global::System.Windows.Data.DataTransferEventArgs e)", codeBehind, StringComparison.Ordinal);

            // Double-clicking the bound event again opens the existing handler; nothing is duplicated.
            // Navigation left the code-behind document active: bring the XAML document back, then its
            // design view.
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            var design = await app.InvokeAsync("od.wpf-designer.activate-design");
            Assert.True(design.GetProperty("success").GetBoolean(), design.ToString());
            JsonElement reselected = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                reselected = await app.InvokeAsync("od.wpf-designer.select", "owner");
                return reselected.GetProperty("success").GetBoolean()
                    && reselected.GetProperty("propertiesPadSelectedName").GetString() == "owner";
            }, TimeSpan.FromSeconds(15), initialDelayMs: 50, maxDelayMs: 250), reselected.ToString());
            var again = await app.InvokeAsync("od.wpf-designer.properties-pad.bind-event", "TargetUpdated");
            Assert.True(again.GetProperty("success").GetBoolean(), again.ToString());
            Assert.Equal("owner_TargetUpdated", again.GetProperty("before").GetString());
            await app.InvokeAsync("od.file.save", codeBehindPath);
            var afterAgain = await File.ReadAllTextAsync(codeBehindPath);
            Assert.Equal(afterAgain.IndexOf("owner_TargetUpdated", StringComparison.Ordinal), afterAgain.LastIndexOf("owner_TargetUpdated", StringComparison.Ordinal));
        } finally {
            await app.InvokeAsync("od.close-all-document-views");
            await File.WriteAllTextAsync(xamlPath, originalXaml);
            await File.WriteAllBytesAsync(codeBehindPath, originalCodeBehind);
        }
    }

    [Fact]
    public async Task PropertiesPad_ResetProperty_RemovesTheAuthoredXamlValue()
    {
        // Reset must travel through the same visible PropertyGrid descriptor that exposes the
        // user-facing reset command, rather than calling the child's reset RPC directly.
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Property reset">
              <Grid><TextBlock x:Name="owner" Text="Authored text" /></Grid>
            </Window>
            """);
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());

            JsonElement selected = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                selected = await app.InvokeAsync("od.wpf-designer.select", "owner");
                return selected.GetProperty("success").GetBoolean()
                    && selected.GetProperty("propertiesPadSelectedName").GetString() == "owner";
            }, TimeSpan.FromSeconds(15), initialDelayMs: 50, maxDelayMs: 250), selected.ToString());
            var reset = await app.InvokeAsync("od.wpf-designer.properties-pad.reset", "Text");
            Assert.True(reset.GetProperty("success").GetBoolean(), reset.ToString());
            Assert.Equal("owner", reset.GetProperty("selectedName").GetString());

            var savedResult = await app.InvokeAsync("od.file.save", xamlPath);
            Assert.True(savedResult.GetProperty("success").GetBoolean(), savedResult.ToString());
            var saved = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("x:Name=\"owner\"", saved, StringComparison.Ordinal);
            Assert.DoesNotContain("Text=", saved, StringComparison.Ordinal);
            Assert.DoesNotContain("Authored text", saved, StringComparison.Ordinal);

            // Reset is a regular source-authoritative designer mutation. Undo restores the
            // authored value, and redo removes it again rather than only changing the preview.
            var undo = await app.InvokeAsync("od.wpf-designer.undo");
            Assert.True(undo.GetProperty("success").GetBoolean(), undo.ToString());
            Assert.False(undo.GetProperty("canUndo").GetBoolean(), undo.ToString());
            Assert.True(undo.GetProperty("canRedo").GetBoolean(), undo.ToString());
            savedResult = await app.InvokeAsync("od.file.save", xamlPath);
            Assert.True(savedResult.GetProperty("success").GetBoolean(), savedResult.ToString());
            saved = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("Text=\"Authored text\"", saved, StringComparison.Ordinal);

            var redo = await app.InvokeAsync("od.wpf-designer.redo");
            Assert.True(redo.GetProperty("success").GetBoolean(), redo.ToString());
            Assert.True(redo.GetProperty("canUndo").GetBoolean(), redo.ToString());
            Assert.False(redo.GetProperty("canRedo").GetBoolean(), redo.ToString());
            savedResult = await app.InvokeAsync("od.file.save", xamlPath);
            Assert.True(savedResult.GetProperty("success").GetBoolean(), savedResult.ToString());
            saved = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("x:Name=\"owner\"", saved, StringComparison.Ordinal);
            Assert.DoesNotContain("Text=", saved, StringComparison.Ordinal);
            Assert.DoesNotContain("Authored text", saved, StringComparison.Ordinal);
        } finally {
            await app.InvokeAsync("od.close-active-view");
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    public async Task PropertiesPad_BindingOptions_EditDelayModeAndStringFormat()
    {
        // Binding.Delay is an Int32. The visible pad must expose it as an integer editor so a
        // fractional value is rejected instead of being silently rounded on its way to the child.
        // Nested Binding rows have dotted protocol names ("Text.Delay"); this journey also locks
        // that such a row actually commits through the pad's own value binding.
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Binding delay">
              <Window.Resources><BooleanToVisibilityConverter x:Key="visible" /></Window.Resources>
              <Grid><TextBox x:Name="owner" Text="{Binding Path=Title}" /></Grid>
            </Window>
            """);
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());

            JsonElement selected = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                selected = await app.InvokeAsync("od.wpf-designer.select", "owner");
                return selected.GetProperty("success").GetBoolean()
                    && selected.GetProperty("propertiesPadSelectedName").GetString() == "owner";
            }, TimeSpan.FromSeconds(15), initialDelayMs: 50, maxDelayMs: 250), selected.ToString());

            var fractional = await app.InvokeAsync("od.wpf-designer.properties-pad.edit", "Text.Delay", "12.5");
            Assert.False(fractional.GetProperty("success").GetBoolean(), fractional.ToString());
            var edited = await app.InvokeAsync("od.wpf-designer.properties-pad.edit", "Text.Delay", "250");
            Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
            // Sibling nested-Binding rows share the dotted protocol-name shape.
            var mode = await app.InvokeAsync("od.wpf-designer.properties-pad.edit", "Text.Mode", "OneWay");
            Assert.True(mode.GetProperty("success").GetBoolean(), mode.ToString());
            // A leading-brace format must be escaped as literal text, not emitted as a nested
            // markup extension that would make the Binding unparseable on reload.
            var format = await app.InvokeAsync("od.wpf-designer.properties-pad.edit", "Text.StringFormat", "{0:N2}");
            Assert.True(format.GetProperty("success").GetBoolean(), format.ToString());
            // The Converter row offers the in-scope IValueConverter resource keys.
            var converter = await app.InvokeAsync("od.wpf-designer.properties-pad.edit", "Text.Converter", "visible");
            Assert.True(converter.GetProperty("success").GetBoolean(), converter.ToString());

            var savedResult = await app.InvokeAsync("od.file.save", xamlPath);
            Assert.True(savedResult.GetProperty("success").GetBoolean(), savedResult.ToString());
            var saved = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("Delay=250", saved, StringComparison.Ordinal);
            Assert.DoesNotContain("12", saved, StringComparison.Ordinal);
            Assert.Matches(@"\{Binding (Path=)?Title\b", saved);
            Assert.Contains("Mode=OneWay", saved, StringComparison.Ordinal);
            Assert.Contains(@"StringFormat=\{0:N2\}", saved, StringComparison.Ordinal);
            Assert.Contains("Converter={StaticResource visible}", saved, StringComparison.Ordinal);
        } finally {
            await app.InvokeAsync("od.close-active-view");
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    public async Task Outline_HiddenAndLockedTogglesWriteDesignTimeAttributes()
    {
        // The in-process Outline's eye and lock toggles: d:IsHidden takes the element off the
        // surface, d:IsLocked makes the surface skip it; both stay in the Outline.
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllBytesAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Outline flags">
              <Grid><Button x:Name="flagged" Content="Flagged" /></Grid>
            </Window>
            """);
            await app.InvokeAsync("od.close-all-document-views");
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());

            var locked = await app.InvokeAsync("od.wpf-designer.outline-toggle", "flagged", "Locked");
            Assert.True(locked.GetProperty("success").GetBoolean(), locked.ToString());
            Assert.True(locked.GetProperty("locked").GetBoolean(), locked.ToString());
            var hidden = await app.InvokeAsync("od.wpf-designer.outline-toggle", "flagged", "Hidden");
            Assert.True(hidden.GetProperty("hidden").GetBoolean(), hidden.ToString());
            Assert.True((await app.InvokeAsync("od.file.save", xamlPath)).GetProperty("success").GetBoolean());
            var text = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("IsLocked=\"True\"", text, StringComparison.Ordinal);
            Assert.Contains("IsHidden=\"True\"", text, StringComparison.Ordinal);

            var unlocked = await app.InvokeAsync("od.wpf-designer.outline-toggle", "flagged", "Locked");
            Assert.False(unlocked.GetProperty("locked").GetBoolean(), unlocked.ToString());
            Assert.True((await app.InvokeAsync("od.file.save", xamlPath)).GetProperty("success").GetBoolean());
            Assert.DoesNotContain("IsLocked", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);
        } finally {
            await app.InvokeAsync("od.close-all-document-views");
            await File.WriteAllBytesAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    public async Task PropertiesPad_BrushEditorWritesAGradientAndNoBrush()
    {
        // Visual Studio's brush editor: a solid Background becomes a linear gradient, then "No brush"
        // ({x:Null}); each commit is one undo step.
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllBytesAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Brushes">
              <Grid>
                <Grid.Resources><SolidColorBrush x:Key="Accent" Color="Orange" /></Grid.Resources>
                <Button x:Name="painted" Background="Red" Content="Painted" />
              </Grid>
            </Window>
            """);
            await app.InvokeAsync("od.close-all-document-views");
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());
            JsonElement selected = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                selected = await app.InvokeAsync("od.wpf-designer.select", "painted");
                return selected.GetProperty("success").GetBoolean()
                    && selected.GetProperty("propertiesPadSelectedName").GetString() == "painted";
            }, TimeSpan.FromSeconds(15), initialDelayMs: 50, maxDelayMs: 250), selected.ToString());

            var gradient = await app.InvokeAsync("od.wpf-designer.properties-pad.edit-brush", "Background", "kind:LinearGradient;select:1;color:#FF0000FF");
            Assert.True(gradient.GetProperty("success").GetBoolean(), gradient.ToString());
            Assert.True(gradient.GetProperty("committed").GetBoolean(), gradient.ToString());
            Assert.Equal("Solid", gradient.GetProperty("before").GetProperty("Kind").GetString());
            Assert.True((await app.InvokeAsync("od.file.save", xamlPath)).GetProperty("success").GetBoolean());
            var text = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("<LinearGradientBrush", text, StringComparison.Ordinal);
            Assert.Contains("Color=\"#FF0000FF\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Background=\"Red\"", text, StringComparison.Ordinal);

            // The rebuilt row now opens the editor on the gradient; cancel commits nothing.
            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "painted")).GetProperty("success").GetBoolean());
            var cancelled = await app.InvokeAsync("od.wpf-designer.properties-pad.edit-brush", "Background", "reverse;cancel");
            Assert.True(cancelled.GetProperty("success").GetBoolean(), cancelled.ToString());
            Assert.False(cancelled.GetProperty("committed").GetBoolean());
            Assert.Equal("LinearGradient", cancelled.GetProperty("before").GetProperty("Kind").GetString());

            var none = await app.InvokeAsync("od.wpf-designer.properties-pad.edit-brush", "Background", "kind:Null");
            Assert.True(none.GetProperty("success").GetBoolean(), none.ToString());
            Assert.True((await app.InvokeAsync("od.file.save", xamlPath)).GetProperty("success").GetBoolean());
            Assert.Contains("Background=\"{x:Null}\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            var undo = await app.InvokeAsync("od.wpf-designer.undo");
            Assert.True(undo.GetProperty("success").GetBoolean(), undo.ToString());
            Assert.True((await app.InvokeAsync("od.file.save", xamlPath)).GetProperty("success").GetBoolean());
            Assert.Equal(text, await File.ReadAllTextAsync(xamlPath));

            // The resources tab references an in-scope Brush resource.
            Assert.True((await app.InvokeAsync("od.wpf-designer.select", "painted")).GetProperty("success").GetBoolean());
            var resource = await app.InvokeAsync("od.wpf-designer.properties-pad.edit-brush", "Background", "resource:Accent");
            Assert.True(resource.GetProperty("success").GetBoolean(), resource.ToString());
            Assert.True((await app.InvokeAsync("od.file.save", xamlPath)).GetProperty("success").GetBoolean());
            Assert.Contains("Background=\"{StaticResource Accent}\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);
        } finally {
            await app.InvokeAsync("od.close-all-document-views");
            await File.WriteAllBytesAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    public async Task PropertiesPad_EditsAndResetsTheParentGridsAttachedRow()
    {
        // The in-process Properties pad lists the parent panel's attached layout properties.
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        var originalXaml = await File.ReadAllBytesAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, """
            <Window x:Class="sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Width="420" Height="220" Title="Attached rows">
              <Grid>
                <Grid.ColumnDefinitions><ColumnDefinition /><ColumnDefinition /></Grid.ColumnDefinitions>
                <Button x:Name="cell" Content="Cell" />
              </Grid>
            </Window>
            """);
            await app.InvokeAsync("od.close-all-document-views");
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());
            JsonElement selected = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                selected = await app.InvokeAsync("od.wpf-designer.select", "cell");
                return selected.GetProperty("success").GetBoolean()
                    && selected.GetProperty("propertiesPadSelectedName").GetString() == "cell";
            }, TimeSpan.FromSeconds(15), initialDelayMs: 50, maxDelayMs: 250), selected.ToString());

            var edited = await app.InvokeAsync("od.wpf-designer.properties-pad.edit", "Grid.Column", "1");
            Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
            Assert.True((await app.InvokeAsync("od.file.save", xamlPath)).GetProperty("success").GetBoolean());
            Assert.Contains("Grid.Column=\"1\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            var reset = await app.InvokeAsync("od.wpf-designer.properties-pad.reset", "Grid.Column");
            Assert.True(reset.GetProperty("success").GetBoolean(), reset.ToString());
            Assert.True((await app.InvokeAsync("od.file.save", xamlPath)).GetProperty("success").GetBoolean());
            Assert.DoesNotContain("Grid.Column=", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);
        } finally {
            await app.InvokeAsync("od.close-all-document-views");
            await File.WriteAllBytesAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    public async Task Toolbar_SnappingControls_DriveTheDragSettingsAndPersist()
    {
        // The in-process WPF designer's zoom bar offered raster placement (with a width) and
        // snapline placement, defaulting to raster off, width 5, snaplines on. The out-of-process
        // toolbar must expose the same controls, and they must change the drag settings the
        // canvas controller actually uses, keep the visible gridlines on the same raster, and
        // persist for the next designer.
        var sample = Path.GetDirectoryName(app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(sample, "MainWindow.xaml");
        async Task<JsonElement> OpenAsync()
        {
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(90)), status.ToString());
            // The toolbar belongs to the design view; a previous journey can leave the XAML
            // source view of the same document active, and od.designer-canvas.* reads the active view.
            var design = await app.InvokeAsync("od.wpf-designer.activate-design");
            Assert.True(design.GetProperty("success").GetBoolean(), design.ToString());
            return status;
        }
        try {
            // Start from no open documents: a design view left by another journey would be
            // reported instead of ours and would keep the settings it was created with.
            await app.InvokeAsync("od.close-all-document-views");
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            var status = await OpenAsync();
            Assert.False(status.GetProperty("snapToGrid").GetBoolean(), status.ToString());
            Assert.True(status.GetProperty("snapToGuides").GetBoolean(), status.ToString());
            Assert.Equal(5, status.GetProperty("gridCellSize").GetDouble());
            var canvas = await app.InvokeAsync("od.designer-canvas.status");
            Assert.Contains("Snapping", canvas.GetProperty("toolbarItems").EnumerateArray().Select(item => item.GetString()));
            Assert.False(canvas.GetProperty("snapToGridChecked").GetBoolean(), canvas.ToString());
            Assert.True(canvas.GetProperty("snapToGuidesChecked").GetBoolean(), canvas.ToString());
            Assert.Equal(5, canvas.GetProperty("rasterWidth").GetInt32());

            var snapping = await app.InvokeAsync("od.designer-canvas.snapping", true, 10, false);
            Assert.True(snapping.GetProperty("success").GetBoolean(), snapping.ToString());
            status = await app.InvokeAsync("od.wpf-designer.status");
            Assert.True(status.GetProperty("snapToGrid").GetBoolean(), status.ToString());
            Assert.False(status.GetProperty("snapToGuides").GetBoolean(), status.ToString());
            Assert.Equal(10, status.GetProperty("gridCellSize").GetDouble());
            // The gridlines mark the positions a raster drag snaps to.
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () =>
                (await app.InvokeAsync("od.designer-canvas.status")).GetProperty("gridlineCellSize").GetDouble() == 10,
                TimeSpan.FromSeconds(5)));

            // A newly opened designer starts from the persisted choice.
            await app.InvokeAsync("od.close-all-document-views");
            status = await OpenAsync();
            Assert.True(status.GetProperty("snapToGrid").GetBoolean(), status.ToString());
            Assert.False(status.GetProperty("snapToGuides").GetBoolean(), status.ToString());
            Assert.Equal(10, status.GetProperty("gridCellSize").GetDouble());
            canvas = await app.InvokeAsync("od.designer-canvas.status");
            Assert.True(canvas.GetProperty("snapToGridChecked").GetBoolean(), canvas.ToString());
            Assert.Equal(10, canvas.GetProperty("rasterWidth").GetInt32());
        } finally {
            // Restore the defaults so later drag journeys are not snapped to a raster.
            await app.InvokeAsync("od.designer-canvas.snapping", false, 5, true);
            await app.InvokeAsync("od.close-active-view");
        }
    }

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
            status = await app.InvokeAsync("od.wpf-designer.status");
            Assert.True(status.GetProperty("canUndo").GetBoolean(), status.ToString());
            Assert.False(status.GetProperty("canRedo").GetBoolean(), status.ToString());
            await app.InvokeAsync("od.file.save", xamlPath);
            Assert.Contains("Header=\"Project\"", await File.ReadAllTextAsync(xamlPath), StringComparison.Ordinal);

            var undo = await app.InvokeAsync("od.wpf-designer.undo");
            Assert.True(undo.GetProperty("success").GetBoolean(), undo.ToString());
            status = await app.InvokeAsync("od.wpf-designer.status");
            Assert.True(status.GetProperty("canRedo").GetBoolean(), status.ToString());
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
