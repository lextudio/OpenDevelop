using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ICSharpCode.GtkDesigner;

/// <summary>
/// Finds the native GTK 4 runtime the design host loads through Gir.Core. GTK is a system
/// install, not something OpenDevelop ships, so a missing runtime is reported as an
/// installation problem with instructions (doc/technotes/gtk-designer.md, "installation
/// diagnostics are a first-class feature") instead of the child's DllNotFoundException.
/// </summary>
static class GtkRuntimeLocator
{
	const string WindowsGtkDll = "libgtk-4-1.dll";

	/// <summary>The directory holding <c>libgtk-4-1.dll</c> on Windows, or null. Looks at
	/// <c>GTK4_ROOT</c> (its <c>bin</c> folder or itself), then <c>PATH</c>, then the MSYS2
	/// environment matching the host's architecture - an arm64 host cannot load x64 GTK DLLs.</summary>
	public static string? FindWindowsBinDirectory()
	{
		var root = Environment.GetEnvironmentVariable("GTK4_ROOT");
		if (!string.IsNullOrEmpty(root)) {
			foreach (var candidate in new[] { Path.Combine(root, "bin"), root })
				if (HasGtk(candidate)) return candidate;
		}
		foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
			if (HasGtk(entry)) return entry;
		var msysRoot = Environment.GetEnvironmentVariable("MSYS2_ROOT");
		var roots = new[] { msysRoot, @"C:\msys64", @"C:\tools\msys64" }.Where(r => !string.IsNullOrEmpty(r));
		foreach (var msys in roots)
			foreach (var environment in MsysEnvironments())
				if (HasGtk(Path.Combine(msys!, environment, "bin"))) return Path.Combine(msys!, environment, "bin");
		return null;
	}

	static string[] MsysEnvironments() => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
		? new[] { "clangarm64" }
		: new[] { "ucrt64", "mingw64", "clang64" };

	static bool HasGtk(string directory)
	{
		try { return File.Exists(Path.Combine(directory, WindowsGtkDll)); } catch (ArgumentException) { return false; }
	}

	/// <summary>How to install GTK 4 for this platform and architecture.</summary>
	public static string InstallInstructions()
	{
		if (OperatingSystem.IsWindows()) {
			var package = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
				? "mingw-w64-clang-aarch64-gtk4" : "mingw-w64-ucrt-x86_64-gtk4";
			var environment = MsysEnvironments()[0];
			return "Install MSYS2 (https://www.msys2.org, or: winget install MSYS2.MSYS2), then in an MSYS2 shell run:\n"
				+ "    pacman -S " + package + "\n"
				+ @"OpenDevelop finds C:\msys64\" + environment + @"\bin automatically. For another location, set GTK4_ROOT to the GTK installation folder or add its bin folder to PATH.";
		}
		if (OperatingSystem.IsMacOS())
			// Homebrew keeps gtk4 keg-only, so the dylib is in /opt/homebrew/opt/gtk4/lib rather than on
			// the dyld path. The designer host puts that directory on DYLD_LIBRARY_PATH itself, so
			// installing is all that is needed - but say so, because `brew install gtk4` followed by
			// running the app from a plain shell used to fail anyway, and the missing search path is
			// invisible from the outside.
			return "Install GTK 4 with Homebrew: brew install gtk4. Homebrew keeps it keg-only, so the "
				+ "libraries land in /opt/homebrew/opt/gtk4/lib; the designer host adds that directory to "
				+ "DYLD_LIBRARY_PATH for the render process, and there is nothing to configure by hand.";
		return "Install GTK 4 from your distribution, e.g. apt install libgtk-4-1 or dnf install gtk4.";
	}
}

/// <summary>The GTK 4 runtime is not installed where the design host can load it. Its
/// <see cref="ToString"/> is the message alone: the load-error view shows ToString(), and a
/// stack trace adds nothing to installation instructions.</summary>
sealed class GtkRuntimeMissingException : Exception
{
	public GtkRuntimeMissingException(string message) : base(message) { }
	public override string ToString() => Message;
}
