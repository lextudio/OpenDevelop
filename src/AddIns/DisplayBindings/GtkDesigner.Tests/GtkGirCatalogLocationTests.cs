using ICSharpCode.GtkDesigner;
using Xunit;

namespace GtkDesigner.Tests;

/// <summary>Where the IDE process finds the GIR data. The design host starts from GTK4_ROOT or the
/// default MSYS2 folders without GTK on PATH, so the .ui language service, which runs in the IDE
/// process, has to find the same installation (otherwise it reports GTK1000 and no property-level
/// diagnostics while the designer itself works).</summary>
public sealed class GtkGirCatalogLocationTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), "gtk-gir-location-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		try { Directory.Delete(root, true); } catch (IOException) { }
	}

	string CreateInstall(string installRoot)
	{
		var gir = Path.Combine(installRoot, "share", "gir-1.0");
		Directory.CreateDirectory(gir);
		File.WriteAllText(Path.Combine(gir, "Gtk-4.0.gir"), "<repository/>");
		var bin = Path.Combine(installRoot, "bin");
		Directory.CreateDirectory(bin);
		File.WriteAllText(Path.Combine(bin, "libgtk-4-1.dll"), "");
		return gir;
	}

	[Fact]
	public void Gtk4Root_AloneLocatesTheGirDirectory()
	{
		var gir = CreateInstall(Path.Combine(root, "gtk"));
		var found = GtkGirCatalog.FindGirDirectory(name => name == "GTK4_ROOT" ? Path.Combine(root, "gtk") : null);
		Assert.Equal(gir, found);
	}

	[Fact]
	public void Gtk4Root_AsItsBinFolder_LocatesTheGirDirectory()
	{
		var gir = CreateInstall(Path.Combine(root, "gtk"));
		var found = GtkGirCatalog.FindGirDirectory(name => name == "GTK4_ROOT" ? Path.Combine(root, "gtk", "bin") : null);
		Assert.Equal(gir, found);
	}

	[Fact]
	public void Gtk4GirDir_WinsOverGtk4Root()
	{
		var explicitDir = CreateInstall(Path.Combine(root, "explicit"));
		CreateInstall(Path.Combine(root, "gtk"));
		var found = GtkGirCatalog.FindGirDirectory(name => name switch {
			"GTK4_GIR_DIR" => explicitDir,
			"GTK4_ROOT" => Path.Combine(root, "gtk"),
			_ => null
		});
		Assert.Equal(explicitDir, found);
	}

	[Fact]
	public void Msys2Root_WithoutPath_LocatesTheWindowsEnvironmentForThisArchitecture()
	{
		Assert.SkipUnless(OperatingSystem.IsWindows(), "MSYS2 defaults only apply on Windows");
		var environment = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
			? "clangarm64" : "ucrt64";
		var gir = CreateInstall(Path.Combine(root, "msys", environment));
		var found = GtkGirCatalog.FindGirDirectory(name => name == "MSYS2_ROOT" ? Path.Combine(root, "msys") : null);
		Assert.Equal(gir, found);
	}
}
