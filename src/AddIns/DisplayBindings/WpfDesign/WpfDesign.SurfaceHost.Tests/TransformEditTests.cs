using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WpfDesign.SurfaceHost;
using Xunit;

namespace WpfDesign.SurfaceHost.Tests;

public sealed class TransformEditTests
{
	const string Xaml = """
		<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="160">
		  <Button x:Name="plain" Content="Plain"/>
		  <Button x:Name="rotated" Content="R" RenderTransformOrigin="0.5,0.5"><Button.RenderTransform><RotateTransform Angle="30"/></Button.RenderTransform></Button>
		  <Button x:Name="grouped" Content="G"><Button.RenderTransform><TransformGroup><ScaleTransform ScaleX="2" ScaleY="3"/><TranslateTransform X="5"/></TransformGroup></Button.RenderTransform></Button>
		  <Button x:Name="matrix" Content="M"><Button.RenderTransform><MatrixTransform Matrix="1,0,0,1,4,4"/></Button.RenderTransform></Button>
		  <Button x:Name="bound" Content="B"><Button.RenderTransform><RotateTransform Angle="{Binding Spin}"/></Button.RenderTransform></Button>
		  <Button x:Name="named" Content="N"><Button.RenderTransform><RotateTransform x:Name="spinner" Angle="10"/></Button.RenderTransform></Button>
		  <Button x:Name="infinite" Content="I"><Button.RenderTransform><TranslateTransform X="1e400"/></Button.RenderTransform></Button>
		</StackPanel>
		""";

	static DesignerPropertyInfo RenderTransform(DesignerSessionState state, int index)
		=> Assert.Single(state.Tree!.Children[index].Properties, property => property.Name == "RenderTransform");

	[Fact]
	public async Task ChildHost_DescribesTransformsAsSimpleOperationLists()
	{
		using var timeout = new CancellationTokenSource(System.TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		var plain = RenderTransform(opened, 0);
		Assert.Equal(("Transform", "Transform"), (plain.Kind, plain.EditorKind));
		Assert.Empty(plain.Transform!);

		var rotated = Assert.Single(RenderTransform(opened, 1).Transform!);
		Assert.Equal(("Rotate", 30d), (rotated.Type, rotated.Angle));

		var group = RenderTransform(opened, 2).Transform!;
		Assert.Equal(new[] { "Scale", "Translate" }, group.Select(op => op.Type));
		Assert.Equal((2d, 3d, 5d, 0d), (group[0].ScaleX, group[0].ScaleY, group[1].X, group[1].Y));

		// A transform the editor would lose (a Matrix, or a value that is not plain text) stays a read-only reference.
		Assert.NotEqual("Transform", RenderTransform(opened, 3).EditorKind);
		Assert.NotEqual("Transform", RenderTransform(opened, 4).EditorKind);
		// A named transform would lose its name when rewritten, and a non-finite value could never be written back.
		Assert.NotEqual("Transform", RenderTransform(opened, 5).EditorKind);
		Assert.NotEqual("Transform", RenderTransform(opened, 6).EditorKind);
	}

	[Fact]
	public async Task ChildHost_WritesSingleGroupedAndClearedTransforms()
	{
		using var timeout = new CancellationTokenSource(System.TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		string Text() => Assert.Single(client.FlushAsync(1, timeout.Token).GetAwaiter().GetResult().Files).Text;
		var plainId = opened.Tree!.Children[0].Id;

		// One operation is written as that transform, only non-default values.
		var single = await client.SetTransformAsync(1, plainId, "RenderTransform", new[] { new DesignerTransformOp { Type = "Rotate", Angle = 45, CenterX = 10 } }, timeout.Token);
		Assert.True(single.Accepted, single.Error);
		var singleText = Text(); Assert.True(System.Text.RegularExpressions.Regex.IsMatch(singleText, "<RotateTransform(?=[^>]*Angle=\"45\")(?=[^>]*CenterX=\"10\")(?![^>]*CenterY)"), singleText);
		var back = Assert.Single(RenderTransform(single, 0).Transform!);
		Assert.Equal(("Rotate", 45d, 10d), (back.Type, back.Angle, back.CenterX));

		// Several are a TransformGroup in order.
		var grouped = await client.SetTransformAsync(1, plainId, "RenderTransform", new[] {
			new DesignerTransformOp { Type = "Scale", ScaleX = 2, ScaleY = 0.5 },
			new DesignerTransformOp { Type = "Skew", AngleX = 10 },
			new DesignerTransformOp { Type = "Translate", X = 3, Y = -4 } }, timeout.Token);
		Assert.True(grouped.Accepted, grouped.Error);
		var text = Text();
		Assert.Matches("<TransformGroup>[\\s\\S]*<ScaleTransform[^>]*ScaleX=\"2\"[\\s\\S]*<SkewTransform[^>]*AngleX=\"10\"[\\s\\S]*<TranslateTransform[^>]*X=\"3\"", text);
		Assert.DoesNotContain("<RotateTransform Angle=\"45\"", text, System.StringComparison.Ordinal);
		Assert.Equal(new[] { "Scale", "Skew", "Translate" }, RenderTransform(grouped, 0).Transform!.Select(op => op.Type));

		// An empty list clears the property.
		var cleared = await client.SetTransformAsync(1, plainId, "RenderTransform", System.Array.Empty<DesignerTransformOp>(), timeout.Token);
		Assert.True(cleared.Accepted, cleared.Error);
		Assert.DoesNotContain("RenderTransform", Text().Split("<Button x:Name=\"rotated\"")[0].Split("x:Name=\"plain\"")[1], System.StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_RefusesUnwritableTransformEditsWithTheSourceUntouched()
	{
		using var timeout = new CancellationTokenSource(System.TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var before = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		var children = opened.Tree!.Children;

		Assert.False((await client.SetTransformAsync(1, children[0].Id, "RenderTransform", new[] { new DesignerTransformOp { Type = "Matrix" } }, timeout.Token)).Accepted);
		Assert.False((await client.SetTransformAsync(1, children[0].Id, "RenderTransform", new[] { new DesignerTransformOp { Type = "Rotate", Angle = double.NaN } }, timeout.Token)).Accepted);
		Assert.False((await client.SetTransformAsync(1, children[0].Id, "Width", new[] { new DesignerTransformOp() }, timeout.Token)).Accepted);
		// A transform the editor cannot represent is not overwritten (that would lose it).
		Assert.False((await client.SetTransformAsync(1, children[3].Id, "RenderTransform", new[] { new DesignerTransformOp { Type = "Rotate", Angle = 1 } }, timeout.Token)).Accepted);
		Assert.False((await client.SetTransformAsync(1, children[4].Id, "RenderTransform", System.Array.Empty<DesignerTransformOp>(), timeout.Token)).Accepted);
		Assert.Equal(before, Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text);
	}
}
