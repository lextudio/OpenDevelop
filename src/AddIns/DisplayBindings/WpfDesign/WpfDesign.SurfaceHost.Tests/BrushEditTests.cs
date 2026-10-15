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

	[Fact]
	public async Task ChildHost_OffersAndWritesInScopeBrushResources()
	{
		const string xaml = """
			<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="120">
			  <StackPanel.Resources>
			    <LinearGradientBrush x:Key="Fade"><GradientStop Color="White"/></LinearGradientBrush>
			    <SolidColorBrush x:Key="Accent" Color="Orange"/>
			    <BooleanToVisibilityConverter x:Key="NotABrush"/>
			  </StackPanel.Resources>
			  <Button x:Name="target" Background="Red" Content="Target"/>
			  <Button x:Name="referenced" Background="{StaticResource Accent}" Content="Referenced"/>
			</StackPanel>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		DesignerPropertyInfo Background(DesignerSessionState state, int index)
			=> Assert.Single(state.Tree!.Children[index].Properties, p => p.Name == "Background");

		// Name order, whatever order the dictionary (a hash table) enumerates its keys in.
		Assert.Equal(new[] { "Accent", "Fade" }, Background(opened, 0).BrushResourceKeys);
		Assert.Equal(new[] { "Accent", "Fade" }, Background(opened, 1).BrushResourceKeys);
		var referenced = Background(opened, 1);
		Assert.Equal("Brush", referenced.EditorKind);
		Assert.Equal(("Resource", "Accent"), (referenced.Brush!.Kind, referenced.Brush.ResourceKey));

		var set = await client.SetBrushAsync(1, opened.Tree!.Children[0].Id, "Background", new DesignerBrushInfo { Kind = "Resource", ResourceKey = "Fade" }, timeout.Token);
		Assert.True(set.Accepted, set.Error);
		Assert.Equal("Fade", Background(set, 0).Brush!.ResourceKey);
		var text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.Contains("x:Name=\"target\" Background=\"{StaticResource Fade}\"", text, StringComparison.Ordinal);

		// A key that is not a Brush resource in scope is rejected without an edit.
		Assert.False((await client.SetBrushAsync(1, opened.Tree.Children[0].Id, "Background", new DesignerBrushInfo { Kind = "Resource", ResourceKey = "NotABrush" }, timeout.Token)).Accepted);
		Assert.Equal(text, Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text);
	}

	[Fact]
	public async Task ChildHost_DescribesAndWritesSystemColorsBrushes()
	{
		const string xaml = """
			<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="120">
			  <Button x:Name="target" Background="Red" Content="Target"/>
			  <Button x:Name="themed" Background="{DynamicResource {x:Static SystemColors.ControlBrushKey}}" Content="Themed"/>
			</StackPanel>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		DesignerPropertyInfo Background(DesignerSessionState state, int index)
			=> Assert.Single(state.Tree!.Children[index].Properties, p => p.Name == "Background");

		var themed = Background(opened, 1);
		Assert.Equal("Brush", themed.EditorKind);
		Assert.Equal(("System", "ControlBrushKey"), (themed.Brush!.Kind, themed.Brush.ResourceKey));

		// A member that is not a SystemColors brush key is refused without an edit.
		var before = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.False((await client.SetBrushAsync(1, opened.Tree!.Children[0].Id, "Background", new DesignerBrushInfo { Kind = "System", ResourceKey = "ControlColorKey" }, timeout.Token)).Accepted);
		Assert.False((await client.SetBrushAsync(1, opened.Tree.Children[0].Id, "Background", new DesignerBrushInfo { Kind = "System", ResourceKey = "Nope" }, timeout.Token)).Accepted);
		Assert.Equal(before, Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text);

		var set = await client.SetBrushAsync(1, opened.Tree.Children[0].Id, "Background", new DesignerBrushInfo { Kind = "System", ResourceKey = "HighlightBrushKey" }, timeout.Token);
		Assert.True(set.Accepted, set.Error);
		Assert.Equal(("System", "HighlightBrushKey"), (Background(set, 0).Brush!.Kind, Background(set, 0).Brush!.ResourceKey));
		var text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.Contains("x:Name=\"target\" Background=\"{DynamicResource {x:Static SystemColors.HighlightBrushKey}}\"", text, StringComparison.Ordinal);

		// The written markup reloads and is described the same way.
		var reopened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(2, text), timeout.Token);
		Assert.True(reopened.Accepted, reopened.Error);
		Assert.Equal(("System", "HighlightBrushKey"), (Background(reopened, 0).Brush!.Kind, Background(reopened, 0).Brush!.ResourceKey));
	}

	[Fact]
	public async Task ChildHost_MarksImageSourcesForTheProjectImagePicker()
	{
		const string xaml = """
			<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="120">
			  <Image x:Name="empty"/>
			  <Image x:Name="bound" Source="{Binding Picture}"/>
			</StackPanel>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		DesignerPropertyInfo Source(DesignerSessionState state, int index)
			=> Assert.Single(state.Tree!.Children[index].Properties, p => p.Name == "Source");

		Assert.Equal("ImageSource", Source(opened, 0).EditorKind);
		// A binding keeps its own editor; the picker would overwrite it with text.
		Assert.NotEqual("ImageSource", Source(opened, 1).EditorKind);

		// The picker writes a relative URI through the ordinary text path.
		var set = await client.SetPropertyAsync(1, opened.Tree!.Children[0].Id, "Source", "Images/dot.png", timeout.Token);
		Assert.True(set.Accepted, set.Error);
		Assert.Contains("Source=\"Images/dot.png\"", Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_DescribesAndWritesImageBrushes()
	{
		const string xaml = """
			<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="120">
			  <Button x:Name="target" Background="Red" Content="Target"/>
			  <Button x:Name="tiled" Content="Tiled"><Button.Background><ImageBrush ImageSource="images/a.png" Stretch="Uniform"/></Button.Background></Button>
			  <Button x:Name="transformed" Content="T"><Button.Background><ImageBrush ImageSource="images/a.png"><ImageBrush.RelativeTransform><RotateTransform Angle="10"/></ImageBrush.RelativeTransform></ImageBrush></Button.Background></Button>
			  <Button x:Name="tiledSettings" Content="S"><Button.Background><ImageBrush ImageSource="images/a.png" TileMode="Tile" AlignmentX="Left" AlignmentY="Bottom" Viewport="0,0,0.5,0.5" ViewportUnits="RelativeToBoundingBox" Viewbox="0,0,10,10" ViewboxUnits="Absolute"/></Button.Background></Button>
			</StackPanel>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		DesignerPropertyInfo Background(DesignerSessionState state, int index)
			=> Assert.Single(state.Tree!.Children[index].Properties, p => p.Name == "Background");

		var tiled = Background(opened, 1).Brush!;
		Assert.Equal(("Image", "images/a.png", "Uniform"), (tiled.Kind, tiled.ImageSource, tiled.Stretch));
		// A setting the editor cannot show (a transform) keeps the brush a read-only reference.
		Assert.Null(Background(opened, 2).Brush);
		var tileSettings = Background(opened, 3).Brush!;
		Assert.Equal(("Tile", "Left", "Bottom", "0,0,0.5,0.5", "RelativeToBoundingBox", "0,0,10,10", "Absolute"),
			(tileSettings.TileMode, tileSettings.AlignmentX, tileSettings.AlignmentY, tileSettings.Viewport, tileSettings.ViewportUnits, tileSettings.Viewbox, tileSettings.ViewboxUnits));
		// Each invalid tile setting is refused with the source untouched.
		var beforeTile = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		foreach (var bad in new[] {
			new DesignerBrushInfo { Kind = "Image", ImageSource = "b.png", TileMode = "Bogus" },
			new DesignerBrushInfo { Kind = "Image", ImageSource = "b.png", AlignmentX = "Top" },
			new DesignerBrushInfo { Kind = "Image", ImageSource = "b.png", Viewport = "1,2,3" },
			new DesignerBrushInfo { Kind = "Image", ImageSource = "b.png", Viewbox = "0,0,-1,1" },
			new DesignerBrushInfo { Kind = "Image", ImageSource = "b.png", ViewportUnits = "Sideways" } })
			Assert.False((await client.SetBrushAsync(1, opened.Tree!.Children[0].Id, "Background", bad, timeout.Token)).Accepted);
		Assert.Equal(beforeTile, Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text);
		var tileSet = await client.SetBrushAsync(1, opened.Tree!.Children[0].Id, "Background", new DesignerBrushInfo {
			Kind = "Image", ImageSource = "images/c.png", TileMode = "FlipXY", AlignmentX = "Right", Viewport = "0,0,20,20", ViewportUnits = "Absolute" }, timeout.Token);
		Assert.True(tileSet.Accepted, tileSet.Error);
		var tileText = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.Matches("<ImageBrush(?=[^>]*TileMode=\"FlipXY\")(?=[^>]*AlignmentX=\"Right\")(?=[^>]*Viewport=\"0,0,20,20\")(?=[^>]*ViewportUnits=\"Absolute\")", tileText);
		Assert.DoesNotContain("AlignmentY=\"Center\"", tileText, StringComparison.Ordinal);
		var tileBack = Background(tileSet, 0).Brush!;
		Assert.Equal(("FlipXY", "Right", "", "0,0,20,20", "Absolute"), (tileBack.TileMode, tileBack.AlignmentX, tileBack.AlignmentY, tileBack.Viewport, tileBack.ViewportUnits));

		var before = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.False((await client.SetBrushAsync(1, opened.Tree!.Children[0].Id, "Background", new DesignerBrushInfo { Kind = "Image", ImageSource = "" }, timeout.Token)).Accepted);
		Assert.False((await client.SetBrushAsync(1, opened.Tree.Children[0].Id, "Background", new DesignerBrushInfo { Kind = "Image", ImageSource = "b.png", Stretch = "Sideways" }, timeout.Token)).Accepted);
		Assert.Equal(before, Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text);

		var set = await client.SetBrushAsync(1, opened.Tree.Children[0].Id, "Background", new DesignerBrushInfo { Kind = "Image", ImageSource = "images/b.png", Stretch = "UniformToFill", Opacity = 0.5 }, timeout.Token);
		Assert.True(set.Accepted, set.Error);
		var text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.Matches("<ImageBrush(?=[^>]*ImageSource=\"images/b.png\")(?=[^>]*Stretch=\"UniformToFill\")(?=[^>]*Opacity=\"0.5\")", text);
		Assert.DoesNotContain("Background=\"Red\"", text, StringComparison.Ordinal);
		var written = Background(set, 0).Brush!;
		Assert.Equal(("Image", "images/b.png", "UniformToFill", 0.5), (written.Kind, written.ImageSource, written.Stretch, written.Opacity));
	}

	[Fact]
	public async Task ChildHost_EditsDrawingAndVisualBrushTileSettingsInPlace()
	{
		const string xaml = """
			<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="120">
			  <Button x:Name="drawn" Content="D"><Button.Background><DrawingBrush TileMode="Tile" Viewport="0,0,10,10" ViewportUnits="Absolute"><DrawingBrush.Drawing><GeometryDrawing Brush="Red"><GeometryDrawing.Geometry><RectangleGeometry Rect="0,0,5,5"/></GeometryDrawing.Geometry></GeometryDrawing></DrawingBrush.Drawing></DrawingBrush></Button.Background></Button>
			  <Button x:Name="visual" Content="V"><Button.Background><VisualBrush Stretch="Uniform"><VisualBrush.Visual><Border Background="Blue" Width="4" Height="4"/></VisualBrush.Visual></VisualBrush></Button.Background></Button>
			  <Button x:Name="moved" Content="M"><Button.Background><DrawingBrush><DrawingBrush.Transform><RotateTransform Angle="5"/></DrawingBrush.Transform></DrawingBrush></Button.Background></Button>
			</StackPanel>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		DesignerPropertyInfo Background(DesignerSessionState state, int index)
			=> Assert.Single(state.Tree!.Children[index].Properties, p => p.Name == "Background");

		var drawn = Background(opened, 0).Brush!;
		Assert.Equal(("Drawing", "Tile", "0,0,10,10", "Absolute"), (drawn.Kind, drawn.TileMode, drawn.Viewport, drawn.ViewportUnits));
		var visual = Background(opened, 1).Brush!;
		Assert.Equal(("Visual", "Uniform"), (visual.Kind, visual.Stretch));
		// A transform keeps the brush a read-only reference.
		Assert.Null(Background(opened, 2).Brush);

		// The wrong kind for the property's brush is refused, source untouched.
		var before = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.False((await client.SetBrushAsync(1, opened.Tree!.Children[0].Id, "Background", new DesignerBrushInfo { Kind = "Visual" }, timeout.Token)).Accepted);
		Assert.False((await client.SetBrushAsync(1, opened.Tree.Children[0].Id, "Background", new DesignerBrushInfo { Kind = "Drawing", TileMode = "Bogus" }, timeout.Token)).Accepted);
		Assert.Equal(before, Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text);

		var edited = await client.SetBrushAsync(1, opened.Tree.Children[0].Id, "Background",
			new DesignerBrushInfo { Kind = "Drawing", TileMode = "FlipX", Viewport = "0,0,20,20", ViewportUnits = "Absolute", AlignmentX = "Left", Opacity = 0.5 }, timeout.Token);
		Assert.True(edited.Accepted, edited.Error);
		var text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.Matches("<DrawingBrush(?=[^>]*TileMode=\"FlipX\")(?=[^>]*Viewport=\"0,0,20,20\")(?=[^>]*AlignmentX=\"Left\")(?=[^>]*Opacity=\"0.5\")", text);
		// The drawing itself survived, and a setting reset to its default is no longer written.
		Assert.Contains("<GeometryDrawing", text, StringComparison.Ordinal);
		Assert.Contains("RectangleGeometry", text, StringComparison.Ordinal);
		var back = Background(edited, 0).Brush!;
		Assert.Equal(("Drawing", "FlipX", "Left", 0.5), (back.Kind, back.TileMode, back.AlignmentX, back.Opacity));

		var cleared = await client.SetBrushAsync(1, opened.Tree.Children[0].Id, "Background", new DesignerBrushInfo { Kind = "Drawing" }, timeout.Token);
		Assert.True(cleared.Accepted, cleared.Error);
		var clearedText = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.DoesNotContain("TileMode=", clearedText, StringComparison.Ordinal);
		Assert.DoesNotContain("Viewport=", clearedText, StringComparison.Ordinal);
		Assert.Contains("<GeometryDrawing", clearedText, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_OffersDescribesAndWritesColorResourceBrushes()
	{
		const string xaml = """
			<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="120">
			  <StackPanel.Resources>
			    <Color x:Key="Accent">Orange</Color>
			    <Color x:Key="Base">#FF202020</Color>
			    <SolidColorBrush x:Key="NotAColor" Color="Red"/>
			  </StackPanel.Resources>
			  <Button x:Name="target" Background="Red" Content="Target"/>
			  <Button x:Name="themed" Content="Themed"><Button.Background><SolidColorBrush Color="{StaticResource Base}"/></Button.Background></Button>
			</StackPanel>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		DesignerPropertyInfo Background(DesignerSessionState state, int index)
			=> Assert.Single(state.Tree!.Children[index].Properties, p => p.Name == "Background");

		Assert.Equal(new[] { "Accent", "Base" }, Background(opened, 0).ColorResourceKeys);
		Assert.Equal(("ColorResource", "Base"), (Background(opened, 1).Brush!.Kind, Background(opened, 1).Brush!.ResourceKey));

		var before = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.False((await client.SetBrushAsync(1, opened.Tree!.Children[0].Id, "Background", new DesignerBrushInfo { Kind = "ColorResource", ResourceKey = "NotAColor" }, timeout.Token)).Accepted);
		Assert.Equal(before, Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text);

		var set = await client.SetBrushAsync(1, opened.Tree.Children[0].Id, "Background", new DesignerBrushInfo { Kind = "ColorResource", ResourceKey = "Accent" }, timeout.Token);
		Assert.True(set.Accepted, set.Error);
		var text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.Contains("<SolidColorBrush Color=\"{StaticResource Accent}\"", text, StringComparison.Ordinal);
		Assert.Equal(("ColorResource", "Accent"), (Background(set, 0).Brush!.Kind, Background(set, 0).Brush!.ResourceKey));
	}
}
