using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

public class SurfaceTreeTests
{
	static CancellationToken Ct => TestContext.Current.CancellationToken;
	static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
	const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

	sealed record Element(string Type, string? Name, double X, double Y, double W, double H);

	/// <summary>
	/// A surface that resolves SelectByPath from a table (an unlisted path is silent, like the real one) and, like the real one, only
	/// selects in native mode and answers UpdateXaml with Hwnd while native.
	/// </summary>
	static FakeSurface TreeSurface(Dictionary<string, Element> elements, List<string>? asked = null)
	{
		bool native = false;
		return new FakeSurface(async (m, w) => {
			switch (FakeSurface.Type(m)) {
				case "Hello": await FakeSurface.Ready(w); break;
				case "LoadXaml": await FakeSurface.Frame(w); break;
				case "UpdateXaml": await (native ? Hwnd(w) : FakeSurface.Frame(w)); break;
				case "EnterNative": native = true; await Hwnd(w); break;
				case "SelectByPath":
					string path = m.GetProperty("path").GetString()!;
					asked?.Add(path);
					if (elements.TryGetValue(path, out var e))
						await w.WriteLineAsync("{\"type\":\"Selected\",\"id\":1,\"elementType\":\"" + e.Type + "\",\"name\":"
							+ (e.Name == null ? "null" : "\"" + e.Name + "\"") + ",\"path\":\"" + path + "\",\"x\":" + e.X + ",\"y\":" + e.Y
							+ ",\"w\":" + e.W + ",\"h\":" + e.H + "}");
					break;
				case "ExitNative": native = false; await w.WriteLineAsync("{\"type\":\"NativeExited\"}"); break;
			}
		});
	}

	static Task Hwnd(StreamWriter w)
		=> w.WriteLineAsync("{\"type\":\"Hwnd\",\"hwnd\":1,\"dipWidth\":480,\"dipHeight\":320,\"pixelWidth\":480,\"pixelHeight\":320,\"scale\":1.0}");

	static DesignerDocumentRequest Request(string xaml, long version = 1)
	{
		var snapshot = new DesignerDocumentSnapshot { SessionId = "s1", DocumentId = "d1", Version = version, PrimaryFileName = "MainPage.xaml" };
		snapshot.Files.Add(new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = xaml });
		return new DesignerDocumentRequest { Snapshot = snapshot, Viewport = new DesignerViewport { Width = 480, Height = 320, Dpi = 1 } };
	}

	static async Task<SurfaceDesignDocument> Open(FakeSurface surface, Action<SurfaceDesignDocument>? configure = null)
	{
		var doc = new SurfaceDesignDocument(await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, Timeout, Ct));
		configure?.Invoke(doc);
		return doc;
	}

	static List<string> Types(FakeSurface surface) => surface.Received.Select(d => FakeSurface.Type(d.RootElement)).ToList();

	static readonly string StackPage = $"<Page {Ns}><StackPanel><Button x:Name=\"Go\"/><TextBlock/></StackPanel></Page>";

	static Dictionary<string, Element> StackElements() => new() {
		["0"] = new("Page", null, 0, 0, 480, 320),
		["0.0"] = new("StackPanel", null, 0, 0, 480, 48),
		["0.0.0"] = new("Button", "Go", 1, 2, 70, 29),
		["0.0.1"] = new("TextBlock", null, 1, 31, 78, 17),
	};

	[Fact]
	public async Task The_tree_follows_the_documents_structure_with_the_shells_path_scheme_and_the_surfaces_bounds()
	{
		var asked = new List<string>();
		await using var surface = TreeSurface(StackElements(), asked);
		await using var doc = await Open(surface);

		var state = await doc.OpenAsync(Request(StackPage), Ct);

		Assert.True(state.Accepted, state.Error);
		var root = state.Tree!;
		Assert.Equal(("", "Page", null), (root.Path, root.Type, root.Name));
		Assert.Equal(root.Path, root.Id);
		var panel = Assert.Single(root.Children);
		Assert.Equal(("0", "StackPanel"), (panel.Path, panel.Type));
		Assert.Equal(2, panel.Children.Count);
		var button = panel.Children[0];
		Assert.Equal(("0,0", "Button", "Go"), (button.Path, button.Type, button.Name));
		Assert.Equal((1.0, 2.0, 70.0, 29.0), (button.X, button.Y, button.Width, button.Height));
		Assert.Equal(("0,1", "TextBlock", null), (panel.Children[1].Path, panel.Children[1].Type, panel.Children[1].Name));
		Assert.True(button.IsDesignable && !button.IsTemplatePart);
		// One excursion for the whole document, in document order, and the next render is a frame again.
		Assert.Equal(new[] { "0", "0.0", "0.0.0", "0.0.1" }, asked);
		Assert.Equal(new[] { "Hello", "LoadXaml", "EnterNative", "SetMode", "SelectByPath", "SelectByPath", "SelectByPath", "SelectByPath", "UpdateXaml", "ExitNative" }, Types(surface));
	}

	[Fact]
	public async Task Property_elements_are_not_children_and_do_not_shift_the_paths()
	{
		var asked = new List<string>();
		await using var surface = TreeSurface(new() {
			["0"] = new("Page", null, 0, 0, 480, 320), ["0.0"] = new("Grid", null, 0, 0, 480, 320), ["0.0.0"] = new("Button", "B", 0, 0, 50, 30),
		}, asked);
		await using var doc = await Open(surface);

		var state = await doc.OpenAsync(Request(
			$"<Page {Ns}><Grid><Grid.RowDefinitions><RowDefinition/><RowDefinition/></Grid.RowDefinitions><Button x:Name=\"B\"/></Grid></Page>"), Ct);

		Assert.Equal(new[] { "0", "0.0", "0.0.0" }, asked);   // RowDefinition is under a property element: not asked about, and the Button is child 0
		Assert.Equal("B", state.Tree!.Children[0].Children[0].Name);
	}

	[Fact]
	public async Task An_element_the_surface_cannot_resolve_is_left_out_and_its_children_move_up()
	{
		var elements = StackElements();
		elements.Remove("0.0");   // the StackPanel does not resolve
		await using var surface = TreeSurface(elements);
		await using var doc = await Open(surface);

		var state = await doc.OpenAsync(Request(StackPage), Ct);

		var root = state.Tree!;
		Assert.Equal(new[] { "Button", "TextBlock" }, root.Children.Select(c => c.Type).ToArray());
		Assert.Equal(new[] { "0,0", "0,1" }, root.Children.Select(c => c.Path).ToArray());   // paths keep their own identity
	}

	[Fact]
	public async Task A_page_where_nothing_resolves_has_no_tree_but_is_still_accepted()
	{
		await using var surface = TreeSurface(new());
		await using var doc = await Open(surface);

		var state = await doc.OpenAsync(Request(StackPage), Ct);

		Assert.True(state.Accepted);
		Assert.Null(state.Tree);
		Assert.NotNull(state.Render);
	}

	[Fact]
	public async Task The_tree_can_be_turned_off_and_then_no_native_excursion_happens()
	{
		await using var surface = TreeSurface(StackElements());
		await using var doc = await Open(surface, d => d.BuildTree = false);

		var state = await doc.OpenAsync(Request(StackPage), Ct);

		Assert.True(state.Accepted);
		Assert.Null(state.Tree);
		Assert.Equal(new[] { "Hello", "LoadXaml" }, Types(surface));
	}

	[Fact]
	public async Task A_large_page_is_asked_about_only_up_to_the_limit()
	{
		var asked = new List<string>();
		await using var surface = TreeSurface(new() { ["0"] = new("Page", null, 0, 0, 1, 1), ["0.0"] = new("StackPanel", null, 0, 0, 1, 1), ["0.0.0"] = new("Button", null, 0, 0, 1, 1) }, asked);
		await using var doc = await Open(surface, d => d.MaxTreeElements = 3);

		var state = await doc.OpenAsync(Request(
			$"<Page {Ns}><StackPanel><Button/><Button/><Button/><Button/><Button/></StackPanel></Page>"), Ct);

		Assert.Equal(new[] { "0", "0.0", "0.0.0" }, asked);
		Assert.NotNull(state.Tree);
	}

	[Fact]
	public async Task A_rejected_render_gets_no_excursion()
	{
		await using var surface = new FakeSurface(async (m, w) => {
			switch (FakeSurface.Type(m)) {
				case "Hello": await FakeSurface.Ready(w); break;
				case "LoadXaml": await w.WriteLineAsync("{\"type\":\"Error\",\"phase\":\"parse\",\"message\":\"bad\",\"line\":1,\"column\":2}"); break;
			}
		});
		await using var doc = await Open(surface);

		var state = await doc.OpenAsync(Request("<Page"), Ct);

		Assert.False(state.Accepted);
		Assert.Null(state.Tree);
		Assert.Equal(new[] { "Hello", "LoadXaml" }, Types(surface));
	}

	[Fact]
	public async Task An_update_rebuilds_the_tree_from_the_new_document()
	{
		var asked = new List<string>();
		await using var surface = TreeSurface(StackElements(), asked);
		await using var doc = await Open(surface);
		await doc.OpenAsync(Request(StackPage, 1), Ct);
		asked.Clear();

		var state = await doc.UpdateAsync(Request($"<Page {Ns}><Button x:Name=\"Go\"/></Page>", 2), Ct);

		Assert.Equal(new[] { "0", "0.0" }, asked);   // the new document's structure, not the old one's
		Assert.True(state.Accepted);
	}
}
