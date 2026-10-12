using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WpfDesign.SurfaceHost;
using Xunit;

namespace WpfDesign.SurfaceHost.Tests;

public sealed class BrushEditTests
{
	const string Xaml = """
		<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="120">
		  <Button x:Name="solid" Background="Red" Content="Solid"/>
		  <Button x:Name="gradient" Content="Gradient">
		    <Button.Background>
		      <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
		        <GradientStop Color="Red" Offset="0"/>
		        <GradientStop Color="Blue" Offset="1"/>
		      </LinearGradientBrush>
		    </Button.Background>
		  </Button>
		  <Button x:Name="faded" Content="Faded">
		    <Button.Background><LinearGradientBrush Opacity="0.5"><GradientStop Color="Red"/></LinearGradientBrush></Button.Background>
		  </Button>
		  <Button x:Name="plain" Content="Plain"/>
		  <Button x:Name="rotated" Content="Rotated">
		    <Button.Background><LinearGradientBrush><LinearGradientBrush.RelativeTransform><RotateTransform Angle="45"/></LinearGradientBrush.RelativeTransform><GradientStop Color="Red"/></LinearGradientBrush></Button.Background>
		  </Button>
		</StackPanel>
		""";

	[Fact]
	public async Task ChildHost_DescribesAndWritesSolidAndGradientBrushes()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		DesignerPropertyInfo Background(DesignerSessionState state, int index)
			=> Assert.Single(state.Tree!.Children[index].Properties, p => p.Name == "Background");

		var solid = Background(opened, 0);
		Assert.Equal("Brush", solid.EditorKind);
		Assert.Equal("Solid", solid.Brush!.Kind);
		Assert.Equal("#FFFF0000", solid.Brush.Color);
		var gradient = Background(opened, 1);
		Assert.Equal("Brush", gradient.EditorKind);
		Assert.True(gradient.IsReadOnly);
		Assert.Equal("LinearGradient", gradient.Brush!.Kind);
		Assert.Equal((0.0, 0.0, 1.0, 0.0), (gradient.Brush.StartX, gradient.Brush.StartY, gradient.Brush.EndX, gradient.Brush.EndY));
		Assert.Equal(new[] { ("#FFFF0000", 0.0), ("#FF0000FF", 1.0) }, gradient.Brush.Stops.Select(stop => (stop.Color, stop.Offset)));
		// Opacity is one of the editor's settings, so that gradient is editable too.
		var faded = Background(opened, 2);
		Assert.Equal("Brush", faded.EditorKind);
		Assert.Equal(0.5, faded.Brush!.Opacity, 3);
		var plain = Background(opened, 3);
		// Unset: the editor starts from the effective (theme) brush, which is not authored.
		Assert.Equal("Brush", plain.EditorKind);
		Assert.False(plain.ShouldSerialize);
		Assert.Equal("Solid", plain.Brush!.Kind);

		// A transformed brush would lose its transform through the editor: it stays a reference.
		var rotated = Background(opened, 4);
		Assert.Null(rotated.Brush);
		Assert.NotEqual("Brush", rotated.EditorKind);

		// Solid -> radial gradient, and an unset brush -> solid.
		var radial = await client.SetBrushAsync(1, opened.Tree!.Children[0].Id, "Background", new DesignerBrushInfo {
			Kind = "RadialGradient", RadiusX = 0.8, SpreadMethod = "Reflect",
			Stops = { new DesignerGradientStopInfo { Color = "#FFFFFFFF", Offset = 0 }, new DesignerGradientStopInfo { Color = "#FF000000", Offset = 1 } }
		}, timeout.Token);
		Assert.True(radial.Accepted, radial.Error);
		Assert.Equal("RadialGradient", Background(radial, 0).Brush!.Kind);
		var filled = await client.SetBrushAsync(1, opened.Tree.Children[3].Id, "Background", new DesignerBrushInfo { Kind = "Solid", Color = "#FF00FF00" }, timeout.Token);
		Assert.True(filled.Accepted, filled.Error);
		var text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		// Only non-default settings are written.
		Assert.Contains("<RadialGradientBrush RadiusX=\"0.8\" SpreadMethod=\"Reflect\">", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Center=", text, StringComparison.Ordinal);

		// "No brush" is an explicit {x:Null}, and reads back as such.
		var none = await client.SetBrushAsync(1, opened.Tree.Children[1].Id, "Background", new DesignerBrushInfo { Kind = "Null" }, timeout.Token);
		Assert.True(none.Accepted, none.Error);
		Assert.Equal("Null", Background(none, 1).Brush!.Kind);
		text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.Contains("x:Name=\"gradient\" Content=\"Gradient\" Background=\"{x:Null}\"", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Background=\"Red\"", text, StringComparison.Ordinal);
		// The solid brush is attribute text (DDP prints a known color by name).
		Assert.Contains("x:Name=\"plain\" Content=\"Plain\" Background=\"Lime\"", text, StringComparison.Ordinal);

		// Malformed requests are rejected without an edit.
		Assert.False((await client.SetBrushAsync(1, opened.Tree.Children[1].Id, "Background", new DesignerBrushInfo { Kind = "LinearGradient" }, timeout.Token)).Accepted);
		Assert.False((await client.SetBrushAsync(1, opened.Tree.Children[1].Id, "Background", new DesignerBrushInfo { Kind = "LinearGradient",
			Stops = { new DesignerGradientStopInfo { Color = "#FF000000", Offset = 2 } } }, timeout.Token)).Accepted);
		Assert.False((await client.SetBrushAsync(1, opened.Tree.Children[1].Id, "Content", new DesignerBrushInfo { Color = "#FF000000" }, timeout.Token)).Accepted);
		Assert.Equal(text, Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text);
	}
}
