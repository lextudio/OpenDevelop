using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

/// <summary>A surface that describes its frames: the element tree and hit-testing come from the frame, with no native-mode excursion.</summary>
public class SurfaceFrameTreeTests
{
	static CancellationToken Ct => TestContext.Current.CancellationToken;
	static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
	const string Page = "<Page><StackPanel><Button x:Name=\"Go\"/><TextBlock/></StackPanel></Page>";

	static Task DescribedFrame(StreamWriter w)
		=> w.WriteLineAsync("{\"type\":\"Frame\",\"format\":\"png\",\"width\":480,\"height\":320,\"dipWidth\":480,\"dipHeight\":320,\"data\":\""
			+ Convert.ToBase64String(FakeSurface.Png) + "\",\"tree\":["
			+ "{\"path\":\"0\",\"type\":\"Page\",\"name\":null,\"x\":0,\"y\":0,\"w\":480,\"h\":320},"
			+ "{\"path\":\"0.0\",\"type\":\"StackPanel\",\"name\":null,\"x\":0,\"y\":0,\"w\":480,\"h\":320},"
			+ "{\"path\":\"0.0.0\",\"type\":\"Button\",\"name\":\"Go\",\"x\":16,\"y\":16,\"w\":77,\"h\":32},"
			+ "{\"path\":\"0.0.1\",\"type\":\"TextBlock\",\"name\":null,\"x\":16,\"y\":48,\"w\":40,\"h\":19}]}");

	static DesignerDocumentRequest Request(long version)
	{
		var snapshot = new DesignerDocumentSnapshot { SessionId = "s1", DocumentId = "d1", Version = version, PrimaryFileName = "MainPage.xaml" };
		snapshot.Files.Add(new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = Page });
		return new DesignerDocumentRequest { Snapshot = snapshot, Viewport = new DesignerViewport { Width = 480, Height = 320, Dpi = 1 } };
	}

	[Fact]
	public async Task A_described_frame_gives_the_tree_and_hit_tests_without_native_mode()
	{
		await using var surface = new FakeSurface(async (m, w) => {
			switch (FakeSurface.Type(m)) {
				case "Hello": await FakeSurface.Ready(w); break;
				case "LoadXaml" or "UpdateXaml": await DescribedFrame(w); break;
			}
		});
		await using var document = new SurfaceDesignDocument(await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, Timeout, Ct));

		var state = await document.OpenAsync(Request(1), Ct);
		var onButton = await document.HitTestAsync(20, 20, Ct);
		var onText = await document.HitTestAsync(20, 50, Ct);

		Assert.True(state.Accepted);
		var stack = Assert.Single(state.Tree!.Children);
		Assert.Equal(("", "Page"), (state.Tree.Id, state.Tree.Type));
		Assert.Equal(new[] { ("0,0", "Go"), ("0,1", (string?)null) }, stack.Children.Select(c => (c.Path, c.Name)).ToArray());
		Assert.Equal((16.0, 16.0, 77.0, 32.0), (stack.Children[0].X, stack.Children[0].Y, stack.Children[0].Width, stack.Children[0].Height));
		Assert.True(onButton.Hit && onButton.ComponentName == "Go");
		Assert.False(onText.Hit);   // unnamed: not offered to the shell
		var types = surface.Received.Select(d => FakeSurface.Type(d.RootElement)).ToList();
		Assert.DoesNotContain("EnterNative", types);   // no excursion for the tree or the clicks
		Assert.True(surface.Received[1].RootElement.GetProperty("describe").GetBoolean());
	}

	static Task LayoutFrame(StreamWriter w)
		=> w.WriteLineAsync("{\"type\":\"Frame\",\"format\":\"png\",\"width\":480,\"height\":320,\"data\":\"" + Convert.ToBase64String(FakeSurface.Png) + "\",\"tree\":["
			+ "{\"path\":\"0\",\"type\":\"Page\",\"name\":null,\"x\":0,\"y\":0,\"w\":480,\"h\":320,\"layout\":{\"margin\":[0,0,0,0]}},"
			+ "{\"path\":\"0.0\",\"type\":\"Grid\",\"name\":null,\"x\":0,\"y\":0,\"w\":480,\"h\":320,\"layout\":{\"margin\":[0,0,0,0],\"zIndex\":0,"
			+ "\"rows\":[{\"offset\":0,\"size\":40},{\"offset\":40,\"size\":280}],\"columns\":[{\"offset\":0,\"size\":100},{\"offset\":100,\"size\":380}]}},"
			+ "{\"path\":\"0.0.0\",\"type\":\"TextBlock\",\"name\":\"A\",\"x\":4,\"y\":5,\"w\":20,\"h\":18,\"layout\":{\"margin\":[4,5,6,7],\"parent\":\"Grid\",\"row\":0,\"column\":0,\"rowSpan\":1,\"columnSpan\":1,\"zIndex\":0}},"
			+ "{\"path\":\"0.0.1\",\"type\":\"Canvas\",\"name\":null,\"x\":0,\"y\":40,\"w\":100,\"h\":280,\"layout\":{\"margin\":[0,0,0,0],\"parent\":\"Grid\",\"row\":1,\"column\":0,\"zIndex\":0}},"
			+ "{\"path\":\"0.0.1.0\",\"type\":\"Rectangle\",\"name\":\"R\",\"x\":12,\"y\":60,\"w\":30,\"h\":30,\"layout\":{\"margin\":[0,0,0,0],\"parent\":\"Canvas\",\"canvasLeft\":12,\"canvasTop\":20,\"zIndex\":3}}]}");

	[Fact]
	public async Task Measured_layout_becomes_grid_tracks_insets_and_z_order_on_the_shell_nodes()
	{
		await using var surface = new FakeSurface(async (m, w) => {
			switch (FakeSurface.Type(m)) {
				case "Hello": await FakeSurface.Ready(w); break;
				case "LoadXaml" or "UpdateXaml": await LayoutFrame(w); break;
			}
		});
		await using var document = new SurfaceDesignDocument(await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, Timeout, Ct));

		var tree = (await document.OpenAsync(Request(1), Ct)).Tree!;

		var grid = Assert.Single(tree.Children);
		Assert.True(grid.GridTracks!.Accepted);
		Assert.Equal(new[] { (0.0, 40.0), (40.0, 280.0) }, grid.GridTracks.RowTracks.Select(t => (t.Offset, t.Size)).ToArray());
		Assert.Equal(new[] { (0.0, 100.0), (100.0, 380.0) }, grid.GridTracks.ColumnTracks.Select(t => (t.Offset, t.Size)).ToArray());
		var text = grid.Children[0];
		Assert.Equal(("Margin", 4.0, 5.0, 6.0, 7.0), (text.LayoutInsets!.Kind, text.LayoutInsets.Left, text.LayoutInsets.Top, text.LayoutInsets.Right, text.LayoutInsets.Bottom));
		var rectangle = grid.Children[1].Children[0];
		Assert.Equal(("CanvasPosition", 12.0, 20.0), (rectangle.LayoutInsets!.Kind, rectangle.LayoutInsets.Left, rectangle.LayoutInsets.Top));
		Assert.Equal(3, rectangle.ZIndex);
		Assert.Null(tree.GridTracks);                       // the page root is not a Grid
		Assert.Equal("Grid", text.LayoutMode);              // parent kind unchanged from the type inference
	}

	[Fact]
	public async Task The_runtime_property_list_of_a_described_element_reaches_the_shell_in_frame_mode()
	{
		await using var surface = new FakeSurface(async (m, w) => {
			switch (FakeSurface.Type(m)) {
				case "Hello": await FakeSurface.Ready(w); break;
				case "LoadXaml" or "UpdateXaml": await DescribedFrame(w); break;
				case "DescribeElement":
					string path = m.GetProperty("path").GetString()!;
					await w.WriteLineAsync(path == "0.0.0"
						? "{\"type\":\"ElementDescribed\",\"path\":\"0.0.0\",\"found\":true,\"elementType\":\"Button\",\"props\":["
							+ "{\"name\":\"IsEnabled\",\"category\":\"Common\",\"type\":\"Boolean\",\"value\":\"True\",\"readOnly\":false,\"options\":[\"True\",\"False\"]},"
							+ "{\"name\":\"IsLoaded\",\"category\":\"Common\",\"type\":\"Boolean\",\"value\":\"True\",\"readOnly\":true,\"options\":null},"
							+ "{\"name\":\"Margin\",\"category\":\"Layout\",\"type\":\"Thickness\",\"value\":\"0,0,0,0\",\"readOnly\":false,\"options\":null},"
							+ "{\"name\":\"FlowDirection\",\"category\":\"Layout\",\"type\":\"FlowDirection\",\"value\":\"LeftToRight\",\"readOnly\":false,\"options\":[\"LeftToRight\",\"RightToLeft\"]}]}"
						: "{\"type\":\"ElementDescribed\",\"path\":\"" + path + "\",\"found\":false,\"props\":[]}");
					break;
			}
		});
		await using var document = new SurfaceDesignDocument(await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, Timeout, Ct));
		await document.OpenAsync(Request(1), Ct);

		var props = await document.DescribePropertiesAsync("0,0", Ct);   // the shell path of the Button maps to surface path 0.0.0
		var gone = await document.DescribePropertiesAsync("0,9", Ct);

		Assert.Equal(new[] { "IsEnabled", "IsLoaded", "Margin", "FlowDirection" }, props.Select(p => p.Name).ToArray());
		var enabled = props[0];
		Assert.Equal(("Boolean", "Common", false), (enabled.Kind, enabled.Category, enabled.IsReadOnly));
		Assert.Equal(new[] { "True", "False" }, enabled.AllowedValues);
		Assert.True(props[1].IsReadOnly);
		Assert.Equal("Thickness", props[2].Kind);
		Assert.Equal(("Enum", true), (props[3].Kind, props[3].IsEnum));
		Assert.Empty(gone);                                                  // a path that no longer resolves is "no catalog"
		var asked = surface.Received.Where(d => FakeSurface.Type(d.RootElement) == "DescribeElement").Select(d => d.RootElement.GetProperty("path").GetString());
		Assert.Equal(new[] { "0.0.0", "0.0.9" }, asked.ToArray());
		Assert.DoesNotContain("EnterNative", surface.Received.Select(d => FakeSurface.Type(d.RootElement)));
	}
}