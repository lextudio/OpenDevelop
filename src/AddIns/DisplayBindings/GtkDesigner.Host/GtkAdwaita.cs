namespace ICSharpCode.GtkDesigner.Host;

/// <summary>
/// Libadwaita for documents that declare <c>&lt;requires lib="libadwaita"&gt;</c> - never assumed
/// for a GTK-only document (doc/technotes/gtk-designer.md). Initialised once, on the GTK thread,
/// the first time such a document is opened: Adw.Module.Initialize loads the native library and
/// adw_init registers its types and style manager. Until then (or when libadwaita is not
/// installed) GtkBuilder rejects Adw classes, so the preview shows them as placeholders.
/// </summary>
static class GtkAdwaita
{
	static bool attempted;
	public static bool Available { get; private set; }
	public static string Diagnostic { get; private set; } = "";

	public static bool Requires(string uiText) => System.Text.RegularExpressions.Regex.IsMatch(uiText, @"<requires\s+lib\s*=\s*""libadwaita""");

	/// <summary>Must run on the GTK thread.</summary>
	public static void EnsureInitialized()
	{
		if (attempted) return;
		attempted = true;
		try {
			Adw.Module.Initialize();
			Adw.Functions.Init();
			Available = true;
		} catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException) {
			Diagnostic = "This document requires libadwaita, which could not be loaded, so Adw widgets are previewed as placeholders. "
				+ (OperatingSystem.IsWindows() ? "Install it in MSYS2: pacman -S " + (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "mingw-w64-clang-aarch64-libadwaita" : "mingw-w64-ucrt-x86_64-libadwaita") + "."
					: OperatingSystem.IsMacOS() ? "Install it with: brew install libadwaita."
					: "Install your distribution's libadwaita-1 package.")
				+ " (" + ex.GetBaseException().Message + ")";
		}
	}
}
