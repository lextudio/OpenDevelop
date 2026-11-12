using System.Text.Json;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

public class SurfaceDesignDocumentTests
{
	static CancellationToken Ct => TestContext.Current.CancellationToken;
	static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

	static DesignerDocumentRequest Request(string xaml, long version = 1, double width = 640, double height = 480, double dpi = 2.0)
	{
		var snapshot = new DesignerDocumentSnapshot {
			SessionId = "s1", DocumentId = "d1", Version = version, PrimaryFileName = "MainPage.xaml",
		};
		snapshot.Files.Add(new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = xaml });
		return new DesignerDocumentRequest { Snapshot = snapshot, Viewport = new DesignerViewport { Width = width, Height = height, Dpi = dpi } };
	}

	static async Task<SurfaceDesignDocument> Open(FakeSurface surface)
		=> new(await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, Timeout, Ct)) { BuildTree = false };

	static FakeSurface RenderingSurface(Func<JsonElement, StreamWriter, Task>? onRender = null)
		=> new(async (m, w) => {
			switch (FakeSurface.Type(m)) {
				case "Hello": await FakeSurface.Ready(w); break;
				case "LoadXaml" or "UpdateXaml":
					if (onRender != null) await onRender(m, w); else await FakeSurface.Frame(w);
					break;
			}
		});

	[Fact]
	public async Task Open_maps_the_frame_and_root_type_into_an_accepted_state()
	{
		await using var surface = RenderingSurface();
		await using var doc = await Open(surface);

		var state = await doc.OpenAsync(Request("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"/>"), Ct);

		Assert.True(state.Accepted);
		Assert.Equal(("s1", "d1", 1L, "Page"), (state.SessionId, state.DocumentId, state.Version, state.RootType));
		Assert.Equal(1, state.Render!.Sequence);
		Assert.Equal((1024, 720), (state.Render.Width, state.Render.Height));
		Assert.Equal(2.0, state.Render.Dpi);   // 1024 px / 512 DIP
		Assert.Equal(FakeSurface.Png, Convert.FromBase64String(state.Render.PngBase64));
		Assert.Null(state.Tree);               // the surface reports no element tree
		// The viewport reached the wire.
		var load = surface.Received.Last().RootElement;
		Assert.Equal((640, 480, 2.0), (load.GetProperty("width").GetInt32(), load.GetProperty("height").GetInt32(), load.GetProperty("scale").GetDouble()));
	}

	[Fact]
	public async Task Update_advances_the_frame_sequence_and_version()
	{
		await using var surface = RenderingSurface();
		await using var doc = await Open(surface);
		await doc.OpenAsync(Request("<Page/>", version: 1), Ct);

		var state = await doc.UpdateAsync(Request("<Grid/>", version: 2), Ct);

		Assert.True(state.Accepted);
		Assert.Equal((2L, 2L, "Grid"), (state.Version, state.Render!.Sequence, state.RootType));
		Assert.Equal("UpdateXaml", FakeSurface.Type(surface.Received.Last().RootElement));
	}

	[Fact]
	public async Task Stale_update_is_refused_without_touching_the_surface()
	{
		await using var surface = RenderingSurface();
		await using var doc = await Open(surface);
		await doc.OpenAsync(Request("<Page/>", version: 5), Ct);
		int sent = surface.Received.Count;

		var state = await doc.UpdateAsync(Request("<Page/>", version: 4), Ct);

		Assert.False(state.Accepted);
		Assert.Equal(5L, state.Version);
		Assert.Contains("Stale", state.Error);
		Assert.Equal(sent, surface.Received.Count);
	}

	[Fact]
	public async Task Render_error_is_a_rejected_state_with_a_positioned_diagnostic_and_the_last_good_frame()
	{
		int renders = 0;
		await using var surface = RenderingSurface(async (m, w) => {
			if (renders++ == 0) await FakeSurface.Frame(w);
			else await w.WriteLineAsync("{\"type\":\"Error\",\"phase\":\"parse\",\"message\":\"Unexpected end\",\"line\":4,\"column\":9}");
		});
		await using var doc = await Open(surface);
		var good = await doc.OpenAsync(Request("<Page/>", version: 1), Ct);

		var bad = await doc.UpdateAsync(Request("<Page", version: 2), Ct);

		Assert.False(bad.Accepted);
		Assert.Equal("Unexpected end", bad.Error);
		Assert.Equal(1L, bad.Version);                    // the document did not advance
		var diagnostic = Assert.Single(bad.Diagnostics);
		Assert.Equal(("MainPage.xaml", 4, 9, "Error"), (diagnostic.FileName, diagnostic.Line, diagnostic.Column, diagnostic.Severity));
		Assert.Same(good.Render, bad.Render);             // keep showing the last good frame
		Assert.Equal("", bad.RootType);
	}

	[Fact]
	public async Task Edits_are_refused_as_ordinary_results()
	{
		await using var surface = RenderingSurface();
		await using var doc = await Open(surface);
		await doc.OpenAsync(Request("<Page/>", version: 3), Ct);

		var state = doc.Refuse("s1", "d1", "design/set-property");

		Assert.False(state.Accepted);
		Assert.Equal(3L, state.Version);
		Assert.Contains("design/set-property", state.Error);
		Assert.Contains("source document", state.Error);   // the default reason: edits go to the parent's source model
	}

	[Fact]
	public async Task A_refusal_can_carry_its_own_reason_instead_of_the_default()
	{
		await using var surface = RenderingSurface();
		await using var doc = await Open(surface);
		await doc.OpenAsync(Request("<Page/>", version: 3), Ct);

		var state = doc.Refuse("s1", "d1", "design/theme", "its frames are always the Light theme.");

		Assert.False(state.Accepted);
		Assert.Equal(3L, state.Version);
		Assert.Equal("design/theme is not supported by the Surface renderer: its frames are always the Light theme.", state.Error);
	}

	[Fact]
	public async Task ExportPng_writes_the_last_frame()
	{
		await using var surface = RenderingSurface();
		await using var doc = await Open(surface);
		Assert.Throws<InvalidOperationException>(() => doc.ExportPng(Path.GetTempFileName()));
		await doc.OpenAsync(Request("<Page/>"), Ct);

		string path = Path.Combine(Path.GetTempPath(), "od-surface-" + Guid.NewGuid().ToString("N") + ".png");
		try {
			Assert.Equal(path, doc.ExportPng(path));
			Assert.Equal(FakeSurface.Png, File.ReadAllBytes(path));
		} finally {
			File.Delete(path);
		}
	}

	[Fact]
	public async Task A_surface_that_stops_answering_faults_the_document_instead_of_returning_a_stale_frame()
	{
		await using var surface = new FakeSurface(async (m, w) => {
			if (FakeSurface.Type(m) == "Hello") await FakeSurface.Ready(w);
		});
		await using var doc = new SurfaceDesignDocument(
			await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, TimeSpan.FromMilliseconds(200), Ct));

		await Assert.ThrowsAsync<SurfaceConnectionFaultedException>(() => doc.OpenAsync(Request("<Page/>"), Ct));
		await Assert.ThrowsAsync<SurfaceConnectionFaultedException>(() => doc.UpdateAsync(Request("<Page/>", 2), Ct));
	}
}
