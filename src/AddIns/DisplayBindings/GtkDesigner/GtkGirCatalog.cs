using System;
using System.IO;
using System.Threading;

namespace ICSharpCode.GtkDesigner;

/// <summary>
/// Locates and loads the installed GTK 4 introspection data (plus libadwaita when its GIR is
/// present). Shared by the design host's typed property metadata and by the IDE's `.ui` language
/// service, so both answer from the same catalogue and report the same diagnostic.
/// </summary>
public static class GtkGirCatalog
{
	static readonly Lazy<(GirCatalog? Catalog, string Diagnostic)> catalog =
		new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

	/// <summary>Starts loading off the caller's path; the GIR set is ~20 MB of XML.</summary>
	public static void Preload()
	{
		if (!catalog.IsValueCreated && Interlocked.Exchange(ref preloadQueued, 1) == 0)
			ThreadPool.QueueUserWorkItem(_ => _ = catalog.Value);
	}
	static int preloadQueued;

	public static GirCatalog? Catalog => catalog.Value.Catalog;
	public static string Diagnostic => catalog.Value.Diagnostic;

	static (GirCatalog?, string) Load()
	{
		var directory = FindGirDirectory();
		if (directory == null)
			return (null, "GTK introspection data (Gtk-4.0.gir) was not found, so the designer and its language service only see what the file already contains. Install the GTK 4 introspection files (MSYS2: they ship with the gtk4 package; Homebrew: gtk4; Debian/Ubuntu: gir1.2-gtk-4.0) or set GTK4_GIR_DIR.");
		try {
			// Libadwaita's catalogue joins when its GIR is installed; documents still opt in per file.
			var roots = File.Exists(Path.Combine(directory, "Adw-1.gir")) ? new[] { "Gtk-4.0", "Adw-1" } : new[] { "Gtk-4.0" };
			return (GirCatalog.Load(directory, roots), "");
		} catch (Exception ex) {
			return (null, "GTK introspection data in " + directory + " could not be read: " + ex.Message);
		}
	}

	/// <summary>GTK4_GIR_DIR, else share/gir-1.0 of GTK4_ROOT, else (Windows) beside a libgtk on PATH,
	/// else the default MSYS2 environments, else the OS locations. The Windows steps mirror
	/// GtkRuntimeLocator, which is not linked into every project that links this file.</summary>
	public static string? FindGirDirectory() => FindGirDirectory(Environment.GetEnvironmentVariable);

	internal static string? FindGirDirectory(Func<string, string?> environment)
	{
		static bool HasGtkGir(string? dir) => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "Gtk-4.0.gir"));
		var explicitDir = environment("GTK4_GIR_DIR");
		if (HasGtkGir(explicitDir)) return explicitDir;
		static string GirBesideBin(string bin) => Path.GetFullPath(Path.Combine(bin, "..", "share", "gir-1.0"));
		// GTK4_ROOT is what the design host's GtkRuntimeLocator honours (its bin folder or the root
		// itself), so the IDE-side lookup must too: the host finds GTK there without GTK on PATH.
		var gtkRoot = environment("GTK4_ROOT");
		if (!string.IsNullOrEmpty(gtkRoot)) {
			foreach (var candidate in new[] { Path.GetFullPath(Path.Combine(gtkRoot, "share", "gir-1.0")), GirBesideBin(gtkRoot) })
				if (HasGtkGir(candidate)) return candidate;
		}
		if (OperatingSystem.IsWindows()) {
			foreach (var entry in (environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
				if (!File.Exists(Path.Combine(entry, "libgtk-4-1.dll"))) continue;
				var candidate = GirBesideBin(entry);
				if (HasGtkGir(candidate)) return candidate;
			}
			// The default MSYS2 installs the designer host also falls back to (same environments,
			// and an arm64 process only takes clangarm64: it cannot load x64 GTK DLLs).
			var msysEnvironments = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
				? new[] { "clangarm64" }
				: new[] { "ucrt64", "mingw64", "clang64" };
			foreach (var msys in new[] { environment("MSYS2_ROOT"), @"C:\msys64", @"C:\tools\msys64" }) {
				if (string.IsNullOrEmpty(msys)) continue;
				foreach (var msysEnvironment in msysEnvironments) {
					var candidate = Path.Combine(msys, msysEnvironment, "share", "gir-1.0");
					if (HasGtkGir(candidate)) return candidate;
				}
			}
		}
		foreach (var candidate in new[] { "/opt/homebrew/share/gir-1.0", "/usr/local/share/gir-1.0", "/usr/share/gir-1.0", "/usr/lib64/gir-1.0" })
			if (HasGtkGir(candidate)) return candidate;
		return null;
	}
}
