using System.Text;
using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>Drives the WPF designer's Transform editor, resource-reference picker, brush tile/transform
/// editing and on-canvas gradient handles through the real Properties pad and OS pointer input.</summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class DesignerEditorJourneyTests(OpenDevelopAppFixture app)
{
    const string Xaml = """
        <Window x:Class="sample.MainWindow"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                Width="300" Height="140" Title="Editors">
          <Grid>
            <Grid.Resources>
              <Style x:Key="Big" TargetType="Button"><Setter Property="FontSize" Value="24" /></Style>
              <Style x:Key="Small" TargetType="Button"><Setter Property="FontSize" Value="10" /></Style>
            </Grid.Resources>
            <Button x:Name="target" Width="120" Height="60" Style="{StaticResource Small}" Content="Target">
              <Button.Background>
                <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                  <GradientStop Color="Red" Offset="0" />
                  <GradientStop Color="Blue" Offset="1" />
                </LinearGradientBrush>
              </Button.Background>
            </Button>
          </Grid>
        </Window>
        """;

    async Task<JsonElement> PollAction(string name, params object[] args)
    {
        JsonElement result = default;
        Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
            result = await app.InvokeAsync(name, args);
            return result.GetProperty("success").GetBoolean();
        }, TimeSpan.FromSeconds(15), initialDelayMs: 50, maxDelayMs: 250), name + ": " + result);
        return result;
    }

    async Task<string> SaveAndRead(string xamlPath)
    {
        Assert.True((await app.InvokeAsync("od.file.save", xamlPath)).GetProperty("success").GetBoolean());
        return await File.ReadAllTextAsync(xamlPath);
    }

    async Task Select(string name)
    {
        JsonElement selected = default;
        Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
            selected = await app.InvokeAsync("od.wpf-designer.select", name);
            return selected.GetProperty("success").GetBoolean()
                && selected.GetProperty("propertiesPadSelectedName").GetString() == name;
        }, TimeSpan.FromSeconds(15), initialDelayMs: 50, maxDelayMs: 250), selected.ToString());
    }

    [Fact]
    public async Task WpfDesigner_TransformResourceBrushAndHandleEditsRoundTrip()
    {
        var xamlPath = Path.Combine(Path.GetDirectoryName(app.WpfSampleSolutionPath)!, "MainWindow.xaml");
        var originalXaml = await File.ReadAllBytesAsync(xamlPath);
        try {
            await File.WriteAllTextAsync(xamlPath, Xaml);
            await app.InvokeAsync("od.close-all-document-views");
            Assert.True((await app.ReopenSolutionAsync(app.WpfSampleSolutionPath)).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.open-file", xamlPath)).GetProperty("opened").GetBoolean());
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.wpf-designer.status");
                return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean()
                    && status.GetProperty("diagnostics").GetArrayLength() == 0;
            }, TimeSpan.FromSeconds(90)), status.ToString());
            await Select("target");

            // Transform editor.
            var transform = await PollAction("od.wpf-designer.properties-pad.edit-transform", "RenderTransform", "add:Rotate;set:0:Angle:30");
            Assert.True(transform.GetProperty("committed").GetBoolean(), transform.ToString());
            var text = await SaveAndRead(xamlPath);
            Assert.Contains("RotateTransform", text, StringComparison.Ordinal);
            Assert.Contains("30", text, StringComparison.Ordinal);

            // Resource-reference picker.
            await Select("target");
            var offered = await PollAction("od.wpf-designer.properties-pad.pick-resource", "Style");
            Assert.True(offered.GetProperty("items").EnumerateArray().Any(i => i.GetString() == "{StaticResource Big}"), offered.ToString());
            var picked = await PollAction("od.wpf-designer.properties-pad.pick-resource", "Style", "{StaticResource Big}");
            Assert.Equal("{StaticResource Big}", picked.GetProperty("selected").GetString());
            text = await SaveAndRead(xamlPath);
            Assert.Contains("Style=\"{StaticResource Big}\"", text, StringComparison.Ordinal);

            // Brush tile/transform script edit (an ImageBrush-free path: the gradient's RelativeTransform).
            await Select("target");
            var brush = await PollAction("od.wpf-designer.properties-pad.edit-brush", "Background", "transform:RelativeTransform:add:Rotate|set:0:Angle:45");
            Assert.True(brush.GetProperty("committed").GetBoolean(), brush.ToString());
            text = await SaveAndRead(xamlPath);
            Assert.Contains("RelativeTransform", text, StringComparison.Ordinal);

            // On-canvas gradient handle: real pointer drag of the Start handle.
            await Select("target");
            JsonElement handles = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                handles = await app.InvokeAsync("od.wpf-designer.brush-handles");
                return handles.TryGetProperty("handles", out var h) && h.GetArrayLength() >= 2;
            }, TimeSpan.FromSeconds(15)), handles.ToString());
            var surfaceGeometry = await app.InvokeAsync("od.wpf-designer.surface-geometry");
            var end = handles.GetProperty("handles").EnumerateArray().First(h => h.GetProperty("name").GetString() == "Start");
            double x = end.GetProperty("x").GetDouble(), y = end.GetProperty("y").GetDouble();
            var viewport = surfaceGeometry.GetProperty("viewport");
            Assert.InRange(x, viewport.GetProperty("x").GetDouble() + 6, viewport.GetProperty("x").GetDouble() + viewport.GetProperty("width").GetDouble() - 6);
            Assert.InRange(y, viewport.GetProperty("y").GetDouble() + 6, viewport.GetProperty("y").GetDouble() + viewport.GetProperty("height").GetDouble() - 6);
            var before = text;
            var changed = false;
            var events = "";
            for (var attempt = 1; attempt <= 3 && !changed; attempt++) {
                await app.InvokeAsync("od.activate");
                await app.InvokeAsync("od.pointer-events", true);
                // macOS reliably delivers the press, moves and release to one native window when
                // they are emitted as one Cliclick drag transaction. The decomposed endpoints
                // can move the cursor yet lose their mouse-down between windows.
                var dragged = await app.DragPointerAsync(x, y, x + 30, y + 15, steps: 8);
                Assert.True(dragged.GetProperty("ok").GetBoolean(), dragged.ToString());
                JsonElement observedPress = default;
                Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                    observedPress = await app.InvokeAsync("od.pointer-events", false);
                    return observedPress.GetProperty("count").GetInt32() > 0;
                }, TimeSpan.FromSeconds(3), initialDelayMs: 50, maxDelayMs: 150),
                    "The native drag did not deliver a mouse-down to the main window: " + observedPress);
                events = observedPress.ToString();
                changed = await OpenDevelopAppFixture.PollUntilAsync(async () => {
                    text = await SaveAndRead(xamlPath);
                    return text != before;
                }, TimeSpan.FromSeconds(6));
            }
            var tree = new List<string>();
            if (!changed) {
                void Walk(JsonElement n, int d) {
                    if (n.ValueKind == JsonValueKind.Object) {
                        var type = n.TryGetProperty("type", out var t) ? t.GetString() : null;
                        if (type != null && (type.Contains("DesignSurface") || type == "TextView" || type == "TextArea" || type.Contains("WpfSurfaceDesignerControl")))
                            tree.Add(d + ":" + type + " " + (n.TryGetProperty("bounds", out var b) ? b.GetRawText() : "?"));
                        foreach (var p in n.EnumerateObject()) Walk(p.Value, d + 1);
                    } else if (n.ValueKind == JsonValueKind.Array) foreach (var c in n.EnumerateArray()) Walk(c, d + 1);
                }
                Walk(await app.GetUITreeAsync(), 0);
            }
            Assert.True(changed, string.Join(" | ", tree) + " gradient handle drag did not change the XAML; handles=" + handles + " geometry=" + surfaceGeometry + " events=" + events);
            Assert.Contains("StartPoint", text, StringComparison.Ordinal);
            Assert.DoesNotContain("StartPoint=\"0,0\"", text, StringComparison.Ordinal);
            status = await app.InvokeAsync("od.wpf-designer.status");
            Assert.True(status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean()
                && status.GetProperty("diagnostics").GetArrayLength() == 0, status.ToString());
        } finally {
            await app.InvokeAsync("od.close-all-document-views");
            await File.WriteAllBytesAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    [Trait(RuntimeCell.TraitName, RuntimeCell.LibreWinForms)]
    public Task WinFormsDesigner_ImageListEditorWritesSourceAndResxAsOneUndoableTransaction_LibreWinForms() => WinFormsDesigner_ImageListEditorWritesSourceAndResxAsOneUndoableTransactionAsync(RuntimeCell.LibreWinForms);

    [Fact(Skip = "The Microsoft designer backends are Windows-only; see AddInTests.MicrosoftDesignerBackendsAvailable.", SkipUnless = nameof(AddInTests.MicrosoftDesignerBackendsAvailable), SkipType = typeof(AddInTests))]
    [Trait("DesignerBackend", "Microsoft")]
    [Trait(RuntimeCell.TraitName, RuntimeCell.MicrosoftWinForms)]
    public Task WinFormsDesigner_ImageListEditorWritesSourceAndResxAsOneUndoableTransaction_MicrosoftWinForms() => WinFormsDesigner_ImageListEditorWritesSourceAndResxAsOneUndoableTransactionAsync(RuntimeCell.MicrosoftWinForms);

    async Task WinFormsDesigner_ImageListEditorWritesSourceAndResxAsOneUndoableTransactionAsync(string cell)
    {
        var directory = Path.GetDirectoryName(app.WinFormsSampleSolutionPath)!;
        var formCodePath = Path.Combine(directory, "Form1.cs");
        var designerPath = Path.Combine(directory, "Form1.Designer.cs");
        var resourcePath = Path.Combine(directory, "Form1.resx");
        // Creating the form's first .resx makes the designer add it to the opened project, so that file is restored too.
        var projectPath = cell == RuntimeCell.LibreWinForms
            ? Path.Combine(Path.GetDirectoryName(app.LibreWinFormsSampleSolutionPath)!, "LibreWinFormsSample.csproj")
            : Path.Combine(directory, "WinFormsSample.csproj");
        var originalProject = await File.ReadAllBytesAsync(projectPath);
        var originalDesigner = await File.ReadAllBytesAsync(designerPath);
        var originalResource = File.Exists(resourcePath) ? await File.ReadAllBytesAsync(resourcePath) : null;
        var imagePath = Path.Combine(Path.GetTempPath(), "od-journey-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAGUlEQVR4nGP4z8DwnxLMMGrAqAGjBgwXAwAwxP4QHCfkAAAAAABJRU5ErkJggg=="));
        try {
			if (File.Exists(resourcePath)) File.Delete(resourcePath);
            var designer = Encoding.UTF8.GetString(originalDesigner).Replace("\r\n", "\n", StringComparison.Ordinal);
            var created = designer
                .Replace("            dropPanel = new System.Windows.Forms.Panel();\n",
                    "            dropPanel = new System.Windows.Forms.Panel();\n            imageList1 = new System.Windows.Forms.ImageList();\n", StringComparison.Ordinal)
                .Replace("    }\n}", "        private System.Windows.Forms.ImageList imageList1;\n    }\n}", StringComparison.Ordinal);
            Assert.NotEqual(designer, created);
            await File.WriteAllTextAsync(designerPath, created);

            Assert.True((await app.ReopenSolutionAsync((cell == RuntimeCell.LibreWinForms ? app.LibreWinFormsSampleSolutionPath : app.WinFormsSampleSolutionPath))).GetProperty("success").GetBoolean());
            await app.InvokeAsync("od.open-file", formCodePath);
            JsonElement status = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                status = await app.InvokeAsync("od.forms-designer.status");
                return status.GetProperty("designerLoaded").GetBoolean();
            }, TimeSpan.FromSeconds(60), initialDelayMs: 100, maxDelayMs: 1000), status.ToString());

            JsonElement selected = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                selected = await app.InvokeAsync("od.forms-designer.select", "imageList1");
                return selected.GetProperty("success").GetBoolean();
            }, TimeSpan.FromSeconds(15), initialDelayMs: 50, maxDelayMs: 250), selected.ToString());

            JsonElement edit = default;
            // The action is retried until the designer is ready; keep every attempt's answer, because a
            // later attempt can fail differently from the first (a half-applied first attempt changes state).
            var editAttempts = new List<string>();
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				edit = await app.InvokeAsync("od.forms-designer.properties-pad.edit-image-list", "Images", "add:" + imagePath + ";size:24x20;depth:Depth8Bit");
                var text = edit.ToString();
                if (editAttempts.Count == 0 || editAttempts[^1] != text) editAttempts.Add(text);
                return edit.GetProperty("success").GetBoolean();
            }, TimeSpan.FromSeconds(15), initialDelayMs: 100, maxDelayMs: 300), string.Join("\n", editAttempts));
            Assert.True(edit.GetProperty("committed").GetBoolean(), edit.ToString());

            Assert.True((await app.InvokeAsync("od.file.save", formCodePath)).GetProperty("success").GetBoolean());
			Assert.True(File.Exists(resourcePath));
            Assert.Contains("imageList1.ImageStream", await File.ReadAllTextAsync(resourcePath), StringComparison.Ordinal);
            var source = await File.ReadAllTextAsync(designerPath);
            Assert.Contains("imageList1.ImageStream", source, StringComparison.Ordinal);
            Assert.Contains("imageList1.Images.SetKeyName(0, \"" + Path.GetFileName(imagePath) + "\")", source, StringComparison.Ordinal);
			Assert.Contains("imageList1.ImageSize = new System.Drawing.Size(24, 20);", source, StringComparison.Ordinal);
			Assert.Contains("imageList1.ColorDepth = System.Windows.Forms.ColorDepth.Depth8Bit;", source, StringComparison.Ordinal);

            Assert.True((await app.InvokeFormsHistoryAsync("od.forms-designer.undo")).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.file.save", formCodePath)).GetProperty("success").GetBoolean());
            Assert.DoesNotContain("imageList1.ImageStream", await File.ReadAllTextAsync(designerPath), StringComparison.Ordinal);
            Assert.DoesNotContain("imageList1.ImageStream", File.Exists(resourcePath) ? await File.ReadAllTextAsync(resourcePath) : "", StringComparison.Ordinal);
			Assert.DoesNotContain("imageList1.ImageSize", await File.ReadAllTextAsync(designerPath), StringComparison.Ordinal);

            Assert.True((await app.InvokeFormsHistoryAsync("od.forms-designer.redo")).GetProperty("success").GetBoolean());
            Assert.True((await app.InvokeAsync("od.file.save", formCodePath)).GetProperty("success").GetBoolean());
            Assert.Contains("imageList1.ImageStream", await File.ReadAllTextAsync(resourcePath), StringComparison.Ordinal);
            Assert.Contains("imageList1.ImageStream", await File.ReadAllTextAsync(designerPath), StringComparison.Ordinal);
			Assert.Contains("ColorDepth.Depth8Bit", await File.ReadAllTextAsync(designerPath), StringComparison.Ordinal);
        } finally {
            await app.InvokeAsync("od.close-all-document-views");
            await File.WriteAllBytesAsync(designerPath, originalDesigner);
            await File.WriteAllBytesAsync(projectPath, originalProject);
            if (originalResource != null) await File.WriteAllBytesAsync(resourcePath, originalResource);
            else File.Delete(resourcePath);
            File.Delete(imagePath);
        }
    }
}
