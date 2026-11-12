using System.Text.Json;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

public class SurfacePickTests
{
	static CancellationToken Ct => TestContext.Current.CancellationToken;
	static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
	const string Page = "<Page><Button x:Name=\"PrimaryButton\"/></Page>";

	static Task Selected(StreamWriter w, string? name, string path = "0.0", string type = "Button")
		=> w.WriteLineAsync("{\"type\":\"Selected\",\"id\":7,\"elementType\":\"" + type + "\",\"name\":"
			+ (name == null ? "null" : "\"" + name + "\"") + ",\"path\":\"" + path + "\",\"x\":1,\"y\":20,\"w\":96,\"h\":30}");

	static Task Hwnd(StreamWriter w)
		=> w.WriteLineAsync("{\"type\":\"Hwnd\",\"hwnd\":1180504,\"dipWidth\":480,\"dipHeight\":320,\"pixelWidth\":480,\"pixelHeight\":320,\"scale\":1.0}");

	static Task NativeExited(StreamWriter w) => w.WriteLineAsync("{\"type\":\"NativeExited\"}");

	/// <summary>
	/// The real surface's behaviour: it only selects in native mode, answers a hit with Selected and a miss with silence, and in native mode
	/// answers UpdateXaml with Hwnd instead of a frame. Like the real one it handles messages one at a time, in order.
	/// </summary>
	static FakeSurface NativeOnlySurface(Func<StreamWriter, Task>? onPick = null, Func<StreamWriter, Task>? onEnter = null)
	{
		bool native = false;
		return new FakeSurface(async (m, w) => {
			switch (FakeSurface.Type(m)) {
				case "Hello": await FakeSurface.Ready(w); break;
				case "LoadXaml": await FakeSurface.Frame(w); break;
				case "UpdateXaml": await (native ? Hwnd(w) : FakeSurface.Frame(w)); break;
				case "EnterNative": native = true; await (onEnter ?? Hwnd)(w); break;
				case "PickAt": if (onPick != null) await onPick(w); break;
				case "ExitNative": native = false; await NativeExited(w); break;
			}
		});
	}

	static async Task<SurfaceConnection> Connect(FakeSurface surface)
		=> await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, Timeout, Ct);

	static List<string> Types(FakeSurface surface) => surface.Received.Select(d => FakeSurface.Type(d.RootElement)).ToList();

	[Fact]
	public async Task A_hit_enters_native_mode_picks_and_always_leaves_it_again()
	{
		await using var surface = NativeOnlySurface(w => Selected(w, "PrimaryButton"));
		await using var conn = await Connect(surface);

		var first = await conn.PickAtAsync(Page, 480, 320, 2.0, 40, 35, TimeSpan.FromSeconds(2), Ct);
		var second = await conn.PickAtAsync(Page, 480, 320, 2.0, 41, 36, TimeSpan.FromSeconds(2), Ct);

		Assert.Equal(("PrimaryButton", "Button", 7), (first!.Name, first.ElementType, first.Id));
		Assert.Equal((1.0, 20.0, 96.0, 30.0), (first.X, first.Y, first.Width, first.Height));
		Assert.NotNull(second);
		// Each pick is a complete excursion: nothing is left in native mode, where UpdateXaml would not answer with a frame.
		Assert.Equal(new[] { "Hello", "EnterNative", "SetMode", "PickAt", "UpdateXaml", "ExitNative", "EnterNative", "SetMode", "PickAt", "UpdateXaml", "ExitNative" }, Types(surface));
		var enter = surface.Received[1].RootElement;
		Assert.Equal(Page, enter.GetProperty("xaml").GetString());
		Assert.Equal((960, 640), (enter.GetProperty("paneWidthPx").GetInt32(), enter.GetProperty("paneHeightPx").GetInt32()));
		var view = enter.GetProperty("view");   // 1:1, not "fit", so pick coordinates are design coordinates
		Assert.False(view.GetProperty("fit").GetBoolean());
		Assert.Equal(1.0, view.GetProperty("zoom").GetDouble());
		Assert.True(surface.Received[2].RootElement.GetProperty("design").GetBoolean());
		var pick = surface.Received[3].RootElement;
		Assert.Equal((40.0, 35.0), (pick.GetProperty("x").GetDouble(), pick.GetProperty("y").GetDouble()));
	}

	[Fact]
	public async Task A_miss_is_null_native_mode_is_left_and_the_next_render_is_a_frame()
	{
		await using var surface = NativeOnlySurface();   // PickAt on empty space: silence
		await using var conn = await Connect(surface);

		var miss = await conn.PickAtAsync(Page, 480, 320, 1, 900, 600, TimeSpan.FromMilliseconds(150), Ct);
		var render = await conn.LoadXamlAsync(Page, 480, 320, 1, false, null, Ct);

		Assert.Null(miss);
		Assert.NotNull(render.Frame);   // a miss neither faulted the connection nor left it in native mode
		Assert.Equal(new[] { "Hello", "EnterNative", "SetMode", "PickAt", "UpdateXaml", "ExitNative", "LoadXaml" }, Types(surface));
	}

	/// <summary>
	/// The case a miss timer gets wrong: a hit that takes a while (a loaded machine). The surface handles messages in order, so the
	/// fence cannot overtake the pick's Selected however long it takes; this one takes far longer than the 400 ms the old timer used.
	/// </summary>
	[Fact]
	public async Task A_slow_hit_is_still_a_hit_because_the_fence_cannot_overtake_it()
	{
		await using var surface = NativeOnlySurface(async w => { await Task.Delay(900); await Selected(w, "PrimaryButton"); });
		await using var conn = await Connect(surface);

		var pick = await conn.PickAtAsync(Page, 480, 320, 1, 10, 10, TimeSpan.FromSeconds(10), Ct);

		Assert.Equal("PrimaryButton", pick?.Name);
	}

	/// <summary>Stray messages from before a request (here a Selected nobody asked for) must not be taken for its reply.</summary>
	[Fact]
	public async Task A_stray_selection_from_before_a_request_is_not_taken_for_its_reply()
	{
		await using var surface = new FakeSurface(async (m, w) => {
			switch (FakeSurface.Type(m)) {
				case "Hello": await FakeSurface.Ready(w); break;
				case "EnterNative": await Hwnd(w); break;
				case "PickAt": break;                      // a miss
				case "UpdateXaml": await Hwnd(w); break;   // the fence
				case "ExitNative": await NativeExited(w); break;
				case "LoadXaml":
					await Task.Delay(300);   // a stray Selected lands while this render is in flight
					await FakeSurface.Frame(w);
					break;
			}
		});
		await using var conn = await Connect(surface);

		var pick = await conn.PickAtAsync(Page, 480, 320, 1, 10, 10, TimeSpan.FromSeconds(5), Ct);
		var render = await conn.LoadXamlAsync(Page, 480, 320, 1, false, null, Ct);

		Assert.Null(pick);   // silence before the fence is a certain miss: nothing was waited for
		Assert.NotNull(render.Frame);
		Assert.Null(render.Error);
	}

	[Fact]
	public async Task A_surface_that_cannot_host_the_page_natively_gives_a_miss_and_is_left_in_frame_mode()
	{
		await using var surface = NativeOnlySurface(onEnter: w => w.WriteLineAsync("{\"type\":\"Error\",\"phase\":\"native\",\"message\":\"cannot host\"}"));
		await using var conn = await Connect(surface);

		var pick = await conn.PickAtAsync(Page, 480, 320, 1, 10, 10, TimeSpan.FromMilliseconds(150), Ct);
		var render = await conn.LoadXamlAsync(Page, 480, 320, 1, false, null, Ct);

		Assert.Null(pick);
		Assert.NotNull(render.Frame);
		Assert.Equal(new[] { "Hello", "EnterNative", "ExitNative", "LoadXaml" }, Types(surface));   // no pick sent into a page that was not hosted
	}

	static DesignerDocumentRequest Request(double width = 480, double height = 320, double dpi = 1)
	{
		var snapshot = new DesignerDocumentSnapshot { SessionId = "s1", DocumentId = "d1", Version = 1, PrimaryFileName = "MainPage.xaml" };
		snapshot.Files.Add(new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = Page });
		return new DesignerDocumentRequest { Snapshot = snapshot, Viewport = new DesignerViewport { Width = width, Height = height, Dpi = dpi } };
	}

	[Fact]
	public async Task Only_a_named_element_is_a_hit_for_the_shell_and_the_page_and_viewport_are_the_documents()
	{
		string? nextName = "PrimaryButton";
		await using var surface = NativeOnlySurface(async w => { if (nextName != "<none>") await Selected(w, nextName); });
		await using var doc = new SurfaceDesignDocument(await Connect(surface)) { PickTimeout = TimeSpan.FromSeconds(5), BuildTree = false };
		await doc.OpenAsync(Request(640, 400, 1.5), Ct);

		var named = await doc.HitTestAsync(5, 5, Ct);
		nextName = null;    // an element without an x:Name: the surface's path is not the shell's, so it is not offered
		var unnamed = await doc.HitTestAsync(5, 5, Ct);
		nextName = "<none>";   // empty space
		var empty = await doc.HitTestAsync(5, 5, Ct);

		Assert.True(named.Hit);
		Assert.Equal(("PrimaryButton", "Button"), (named.ComponentName, named.ComponentType));
		Assert.Equal(new[] { "PrimaryButton" }, named.Chain);
		Assert.False(unnamed.Hit);
		Assert.False(empty.Hit);
		var enter = surface.Received.First(d => FakeSurface.Type(d.RootElement) == "EnterNative").RootElement;
		Assert.Equal(Page, enter.GetProperty("xaml").GetString());
		Assert.Equal((640.0, 400.0, 1.5), (enter.GetProperty("width").GetDouble(), enter.GetProperty("height").GetDouble(), enter.GetProperty("scale").GetDouble()));
	}
}
