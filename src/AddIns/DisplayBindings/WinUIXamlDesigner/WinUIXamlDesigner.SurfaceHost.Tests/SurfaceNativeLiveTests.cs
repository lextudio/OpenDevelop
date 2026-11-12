using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

/// <summary>Native-live mode: the document in the surface's own window, frames and picks off until it is left, a crash falls back to the last snapshot.</summary>
public class SurfaceNativeLiveTests
{
	static CancellationToken Ct => TestContext.Current.CancellationToken;
	static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
	const string Page = "<Page><Button x:Name=\"PrimaryButton\"/></Page>";

	static Task Hwnd(StreamWriter w)
		=> w.WriteLineAsync("{\"type\":\"Hwnd\",\"hwnd\":1180504,\"dipWidth\":480,\"dipHeight\":320,\"pixelWidth\":960,\"pixelHeight\":640,\"scale\":2.0}");

	static FakeSurface Surface(Func<string, StreamWriter, Task<bool>>? intercept = null)
	{
		bool native = false;
		return new FakeSurface(async (m, w) => {
			string type = FakeSurface.Type(m);
			if (intercept != null && await intercept(type, w))
				return;
			switch (type) {
				case "Hello": await FakeSurface.Ready(w); break;
				case "LoadXaml": await FakeSurface.Frame(w); break;
				case "UpdateXaml": await (native ? Hwnd(w) : FakeSurface.Frame(w)); break;
				case "EnterNative": native = true; await Hwnd(w); break;
				case "ExitNative": native = false; await w.WriteLineAsync("{\"type\":\"NativeExited\"}"); break;
			}
		});
	}

	static async Task<SurfaceConnection> Connect(FakeSurface surface)
		=> await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, Timeout, Ct);

	static DesignerDocumentRequest Request(long version, string xaml = Page)
	{
		var snapshot = new DesignerDocumentSnapshot { SessionId = "s1", DocumentId = "d1", Version = version, PrimaryFileName = "MainPage.xaml" };
		snapshot.Files.Add(new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = xaml });
		return new DesignerDocumentRequest { Snapshot = snapshot, Viewport = new DesignerViewport { Width = 480, Height = 320, Dpi = 2.0 } };
	}

	[Fact]
	public async Task Entering_native_mode_hands_over_the_window_and_renders_and_picks_are_off_until_it_is_left()
	{
		await using var surface = Surface();
		await using var conn = await Connect(surface);
		await conn.LoadXamlAsync(Page, 480, 320, 2.0, false, null, Ct);

		var window = await conn.EnterNativeLiveAsync(Page, 480, 320, 2.0, null, Ct);

		Assert.True(conn.IsNativeLive);
		Assert.Equal((1180504L, 960, 640, 2.0), (window.Hwnd, window.PixelWidth, window.PixelHeight, window.Scale));
		await Assert.ThrowsAsync<InvalidOperationException>(() => conn.UpdateXamlAsync(Page, false, null, Ct));
		await Assert.ThrowsAsync<InvalidOperationException>(() => conn.PickAtAsync(Page, 480, 320, 2.0, 1, 1, TimeSpan.FromSeconds(1), Ct));
		await Assert.ThrowsAsync<InvalidOperationException>(() => conn.EnterNativeLiveAsync(Page, 480, 320, 2.0, null, Ct));

		await conn.ExitNativeLiveAsync(Ct);
		Assert.False(conn.IsNativeLive);
		Assert.NotNull((await conn.UpdateXamlAsync(Page, false, null, Ct)).Frame);   // frames again
		await conn.ExitNativeLiveAsync(Ct);                                          // leaving twice is harmless
	}

	[Fact]
	public async Task The_document_switches_to_native_and_back_and_re_renders_the_edited_source()
	{
		await using var surface = Surface();
		await using var conn = await Connect(surface);
		await using var document = new SurfaceDesignDocument(conn) { BuildTree = false };
		var opened = await document.OpenAsync(Request(1), Ct);
		Assert.True(opened.Accepted);

		var entered = await document.EnterNativeLiveAsync(Ct);
		Assert.NotNull(entered.Window);
		Assert.True(document.IsNativeLive);

		// An edit while native is a normal refusal, not an exception, and a click is a miss (the window takes it).
		var during = await document.UpdateAsync(Request(2), Ct);
		Assert.False(during.Accepted);
		Assert.False((await document.HitTestAsync(5, 5, Ct)).Hit);

		const string Edited = "<Page><Button x:Name=\"PrimaryButton\" Content=\"Edited\"/></Page>";
		var left = await document.ExitNativeLiveAsync(Request(3, Edited), Ct);
		Assert.Null(left.Window);
		Assert.True(left.Fallback!.Accepted);
		Assert.NotNull(left.Fallback.Render);
		Assert.False(document.IsNativeLive);
	}

	[Fact]
	public async Task A_lost_surface_falls_back_to_the_last_snapshot_with_a_message()
	{
		// The surface process dies while its window is up (the fake drops the connection instead of answering ExitNative).
		await using var surface = Surface((type, w) => type == "ExitNative" ? throw new IOException("surface gone") : Task.FromResult(false));
		await using var conn = await Connect(surface);
		await using var document = new SurfaceDesignDocument(conn) { BuildTree = false };
		var opened = await document.OpenAsync(Request(1), Ct);
		Assert.NotNull((await document.EnterNativeLiveAsync(Ct)).Window);

		var left = await document.ExitNativeLiveAsync(Request(2), Ct);

		Assert.Null(left.Window);
		Assert.Same(opened.Render, left.Fallback!.Render);   // the last accepted snapshot, not an exception
		Assert.Contains("last snapshot", left.Message);
		Assert.False(document.IsNativeLive);
	}

	[Fact]
	public async Task A_surface_that_cannot_enter_native_mode_leaves_the_document_on_snapshots()
	{
		await using var surface = Surface(async (type, w) => {
			if (type != "EnterNative") return false;
			await w.WriteLineAsync("{\"type\":\"Error\",\"phase\":\"native\",\"message\":\"no window\"}");
			return true;
		});
		await using var conn = await Connect(surface);
		await using var document = new SurfaceDesignDocument(conn) { BuildTree = false };
		await document.OpenAsync(Request(1), Ct);

		var result = await document.EnterNativeLiveAsync(Ct);

		Assert.Null(result.Window);
		Assert.Contains("no window", result.Message);
		Assert.NotNull(result.Fallback);
		Assert.False(document.IsNativeLive);
		Assert.True((await document.UpdateAsync(Request(2), Ct)).Accepted);   // the surface was told to leave native mode again
	}
}
