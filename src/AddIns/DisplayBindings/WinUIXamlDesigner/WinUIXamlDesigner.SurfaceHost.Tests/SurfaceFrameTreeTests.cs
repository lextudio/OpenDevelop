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
}
