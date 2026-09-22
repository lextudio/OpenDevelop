using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Previewing a page must not make the project unbuildable.
///
/// The WPF design surface runs out of process and reflects over the project's own output assembly
/// to resolve local XAML types. It used to do that with Assembly.LoadFrom, which holds the file
/// open for the lifetime of the process - and that process is deliberately long-lived, because
/// SharedDesignerHostPool reuses it across documents and even across IDE restarts. So once a page
/// had been previewed, every later build of that project failed with
///
///     MSB3027: Could not copy "obj\...\X.dll" to "bin\...\X.dll" ...
///              The file is locked by: ".NET Host (NNNN)"
///
/// which names a generic ".NET Host" rather than the designer, and reads like a stale lock left
/// behind by something that has already exited. Hot Reload was the loudest victim: its build step
/// failed for a reason that had nothing to do with the code being compiled.
///
/// The host now reads the bytes and lets the handle close, matching what the in-process designer
/// already did (Base/Project/Designer/TypeResolutionService.cs).
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class WpfDesignerAssemblyLockTests
{
	readonly OpenDevelopAppFixture _app;

	public WpfDesignerAssemblyLockTests(OpenDevelopAppFixture app) => _app = app;

	[Fact]
	public async Task BuildSucceeds_AfterTheDesignerHasLoadedTheProjectAssembly()
	{
		var solution = _app.WpfHotReloadFixturePath;
		var opened = await _app.ReopenSolutionAsync(solution);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

		// Build once up front. This is what gives the designer an output assembly to load, and it
		// also means a failure in the assertion below cannot be blamed on the project being
		// unbuildable in the first place.
		var initial = await _app.InvokeAsync("od.build-solution");
		Assert.Equal("Success", initial.GetProperty("result").GetString());

		// The fixture is only an SDK wrapper: every XAML it builds is <Page Include="..." Link="..."/>
		// from vscode-wpf's sample, so the file to open lives there rather than next to the solution.
		var xaml = Path.Combine(RepositoryRoot(), "externals", "vscode-wpf", "sample", "net6.0", "MainWindow.xaml");
		Assert.True(File.Exists(xaml), $"The fixture's window XAML must exist: {xaml}");
		Assert.True((await _app.InvokeAsync("od.open-file", xaml)).GetProperty("opened").GetBoolean());

		// Wait for the surface host to actually load the assembly. "Opened the document" is not
		// enough: the lock only exists once the child process has reflected over the output, so
		// asserting before that would pass even with the old LoadFrom in place.
		JsonElement status = default;
		var loaded = await OpenDevelopAppFixture.PollUntilAsync(async () => {
			status = await _app.InvokeAsync("od.wpf-designer.status");
			return status.TryGetProperty("active", out var active) && active.GetBoolean()
				&& status.TryGetProperty("designerLoaded", out var ready) && ready.GetBoolean();
		}, TimeSpan.FromSeconds(120));
		Assert.True(loaded, $"The WPF designer never finished loading: {status}");

		// The regression: with the assembly still held open by the design host, this build could
		// not overwrite its own output.
		var rebuild = await _app.InvokeAsync("od.build-solution");
		var log = rebuild.TryGetProperty("buildLog", out var text) ? text.GetString() ?? string.Empty : string.Empty;
		Assert.DoesNotContain("MSB3027", log);
		Assert.DoesNotContain("MSB3021", log);
		Assert.DoesNotContain("locked by", log);
		Assert.Equal("Success", rebuild.GetProperty("result").GetString());
	}

	static string RepositoryRoot()
	{
		var directory = AppContext.BaseDirectory;
		while (directory is not null && !Directory.Exists(Path.Combine(directory, "externals", "vscode-wpf")))
			directory = Path.GetDirectoryName(directory);
		Assert.NotNull(directory);
		return directory!;
	}
}
