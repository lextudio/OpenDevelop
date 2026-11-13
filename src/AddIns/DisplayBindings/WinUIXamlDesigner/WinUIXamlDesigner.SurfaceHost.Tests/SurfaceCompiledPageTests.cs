using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

public class SurfaceCompiledPageTests
{
	static CancellationToken Ct => TestContext.Current.CancellationToken;

	/// <summary>
	/// The real surface, started with a built WinUI project's assembly: a page with <c>{x:Bind Greeting}</c> renders the property's value
	/// as a compiled page, and the placeholder when rendered from its text. Needs OD_SURFACE_EXE and OD_SURFACE_ARGS naming a matched host
	/// and <c>--user-dll</c> of a project that keeps its own bootstrap out (WindowsAppSdkBootstrapInitialize=false) and has a page
	/// <c>WinUISample.BindPage</c> with <c>Text="{x:Bind Greeting}"</c>. The two renders must differ; the compiled one is shown in the log.
	/// </summary>
	[Fact]
	public async Task Real_surface_renders_a_compiled_x_bind_page_differently_from_its_markup()
	{
		string? exe = Environment.GetEnvironmentVariable("OD_SURFACE_EXE");
		string? args = Environment.GetEnvironmentVariable("OD_SURFACE_ARGS");
		if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(args) || !args.Contains("--user-dll", StringComparison.Ordinal) || !OperatingSystem.IsWindows())
			Assert.Skip("OD_SURFACE_EXE / OD_SURFACE_ARGS (with --user-dll) are not set (or not Windows).");

		const string Xaml = "<Page x:Class=\"WinUISample.BindPage\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
			+ "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><StackPanel Padding=\"16\">"
			+ "<TextBlock FontSize=\"24\" Text=\"{x:Bind Greeting}\"/><TextBlock Text=\"{x:Bind Count}\"/></StackPanel></Page>";
		var snapshot = new DesignerDocumentSnapshot { SessionId = "s1", DocumentId = "d1", Version = 1, PrimaryFileName = "BindPage.xaml" };
		snapshot.Files.Add(new DesignerSourceFileSnapshot { FileName = "BindPage.xaml", Kind = "Source", Text = Xaml });
		var request = new DesignerDocumentRequest { Snapshot = snapshot, Viewport = new DesignerViewport { Width = 480, Height = 320, Dpi = 1 } };

		async Task<byte[]> RenderAsync(bool compiled)
		{
			var host = await SurfaceProcessHost.StartAsync(exe, args, TimeSpan.FromSeconds(60));
			await using var document = new SurfaceDesignDocument(host) { BuildTree = false, UseCompiledPages = compiled };
			var state = await document.OpenAsync(request, Ct);
			Assert.True(state.Accepted, state.Error + "\n" + document.ChildLog);
			string path = Path.Combine(Path.GetTempPath(), "od-compiled-" + compiled + ".png");
			document.ExportPng(path);
			return File.ReadAllBytes(path);
		}

		var markup = await RenderAsync(false);
		var compiledPage = await RenderAsync(true);
		TestContext.Current.SendDiagnosticMessage($"markup frame {markup.Length} bytes, compiled frame {compiledPage.Length} bytes");
		Assert.NotEqual(markup, compiledPage);
	}
}
