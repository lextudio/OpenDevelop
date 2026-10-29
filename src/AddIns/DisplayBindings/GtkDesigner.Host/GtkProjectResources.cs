using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace ICSharpCode.GtkDesigner.Host;

/// <summary>Compiles and registers the GResource manifests beside a GTK design document. GtkBuilder
/// resolves <c>resource:///</c> through GLib's process resource registry, so registrations are owned
/// by the document session and are released with its native preview.</summary>
sealed class GtkProjectResources : IDisposable
{
	readonly List<nint> resources = new();
	readonly List<string> bundles = new();
	bool disposed;

	GtkProjectResources() { }

	/// <summary>Registers every sibling manifest. A manifest that cannot be compiled is reported
	/// through <paramref name="diagnostics"/> and skipped: the rest of the document still previews,
	/// which is the same tolerance the preview applies to a stylesheet it cannot load.</summary>
	public static GtkProjectResources Load(string? uiFileName, IList<string> diagnostics)
	{
		var result = new GtkProjectResources();
		diagnostics.Clear();
		foreach (var manifest in Manifests(uiFileName)) {
			try { result.LoadManifest(manifest); }
			catch (Exception ex) {
				diagnostics.Add("GTK resource manifest '" + Path.GetFileName(manifest) + "' was ignored: " + Describe(ex));
			}
		}
		return result;
	}

	static string Describe(Exception ex) => ex is System.ComponentModel.Win32Exception
		? "glib-compile-resources could not be started; install GLib so it is on PATH."
		: ex.GetBaseException().Message;

	/// <summary>The manifests that belong to <paramref name="uiFileName"/>'s directory, in a stable
	/// order so the render key and the diagnostics do not change between runs.</summary>
	static IEnumerable<string> Manifests(string? uiFileName)
	{
		var directory = string.IsNullOrWhiteSpace(uiFileName) ? null : Path.GetDirectoryName(uiFileName);
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) yield break;
		foreach (var manifest in Directory.EnumerateFiles(directory, "*.gresource.xml", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal))
			yield return manifest;
	}

	/// <summary>The files a manifest references, resolved the way <c>glib-compile-resources</c>
	/// resolves them. Used to notice an edited image or stylesheet inside a resource.</summary>
	public static IReadOnlyList<string> ReferencedFiles(string? uiFileName)
	{
		var files = new List<string>();
		foreach (var manifest in Manifests(uiFileName)) {
			try {
				var directory = Path.GetDirectoryName(manifest)!;
				foreach (var file in XDocument.Load(manifest).Descendants("file")) {
					var name = file.Value.Trim();
					if (name.Length > 0) files.Add(Path.GetFullPath(Path.Combine(directory, name)));
				}
			} catch { /* an unreadable manifest is reported when the preview loads it */ }
		}
		return files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
	}

	/// <summary>A digest of everything GtkBuilder reads through <c>resource:///</c>. The render
	/// cache keys on it, so editing the image or the manifest re-renders without touching the .ui
	/// document - and a rebuild is forced because the image is resolved when the builder loads.</summary>
	public static string Fingerprint(string? uiFileName)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (var manifest in Manifests(uiFileName)) AppendFile(hash, manifest);
		foreach (var file in ReferencedFiles(uiFileName)) AppendFile(hash, file);
		return Convert.ToHexString(hash.GetHashAndReset());
	}

	static void AppendFile(IncrementalHash hash, string path)
	{
		hash.AppendData(Encoding.UTF8.GetBytes(path + "\0"));
		try {
			using var stream = File.OpenRead(path);
			hash.AppendData(SHA256.HashData(stream));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			hash.AppendData(Encoding.UTF8.GetBytes(ex.GetType().Name));
		}
	}

	void LoadManifest(string manifest)
	{
		var bundle = Path.Combine(Path.GetTempPath(), "OpenDevelop-GtkPreview", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
			"resource-" + Guid.NewGuid().ToString("N") + ".gresource");
		Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
		var start = new ProcessStartInfo("glib-compile-resources") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
		start.ArgumentList.Add("--target=" + bundle);
		start.ArgumentList.Add("--sourcedir=" + Path.GetDirectoryName(manifest)!);
		start.ArgumentList.Add(manifest);
		using var compiler = Process.Start(start) ?? throw new InvalidOperationException("Could not start glib-compile-resources for '" + manifest + "'.");
		var output = compiler.StandardOutput.ReadToEnd();
		var error = compiler.StandardError.ReadToEnd();
		compiler.WaitForExit();
		if (compiler.ExitCode != 0) throw new InvalidOperationException((error + output).Trim());
		var resource = GlibResources.Load(bundle);
		try {
			GlibResources.Register(resource);
			resources.Add(resource);
			bundles.Add(bundle);
		} catch {
			GlibResources.Unref(resource);
			try { File.Delete(bundle); } catch { }
			throw;
		}
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		foreach (var resource in resources.AsEnumerable().Reverse()) { try { GlibResources.Unregister(resource); } catch { } try { GlibResources.Unref(resource); } catch { } }
		resources.Clear();
		foreach (var bundle in bundles) try { File.Delete(bundle); } catch { }
		bundles.Clear();
	}
}

/// <summary>Small GLib ABI boundary intentionally kept here because Gir.Core does not expose
/// GResource. The names are GLib's stable C ABI names on every supported GTK host.</summary>
static unsafe class GlibResources
{
	static readonly Lazy<Natives> natives = new(LoadNatives);
	sealed class Natives
	{
		public nint Library;
		public delegate* unmanaged<byte*, nint*, nint> Load;
		public delegate* unmanaged<nint, void> Register;
		public delegate* unmanaged<nint, void> Unregister;
		public delegate* unmanaged<nint, void> Unref;
		public delegate* unmanaged<nint, void> ErrorFree;
	}
	static Natives LoadNatives()
	{
		// GResource lives in GIO, not libglib. It is a separate dylib/so even though GTK and
		// GResource documentation commonly call the whole stack "GLib".
		var name = OperatingSystem.IsWindows() ? "libgio-2.0-0.dll" : OperatingSystem.IsMacOS() ? "libgio-2.0.0.dylib" : "libgio-2.0.so.0";
		var errorName = OperatingSystem.IsWindows() ? "libglib-2.0-0.dll" : OperatingSystem.IsMacOS() ? "libglib-2.0.0.dylib" : "libglib-2.0.so.0";
		if (!NativeLibrary.TryLoad(name, out var library) || !NativeLibrary.TryLoad(errorName, out var errorLibrary)) throw new InvalidOperationException("GLib resource support is unavailable: could not load " + name + ".");
		return new Natives { Library = library,
			Load = (delegate* unmanaged<byte*, nint*, nint>)NativeLibrary.GetExport(library, "g_resource_load"),
			Register = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(library, "g_resources_register"),
			Unregister = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(library, "g_resources_unregister"),
			Unref = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(library, "g_resource_unref"),
			ErrorFree = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(errorLibrary, "g_error_free") };
	}
	public static nint Load(string bundle)
	{
		var n = natives.Value; var path = System.Text.Encoding.UTF8.GetBytes(bundle + "\0"); nint error = 0;
		fixed (byte* p = path) {
			var resource = n.Load(p, &error);
			if (resource != 0) return resource;
		}
		try { throw new InvalidOperationException("Could not load compiled GTK resource bundle '" + bundle + "'."); }
		finally { if (error != 0) n.ErrorFree(error); }
	}
	public static void Register(nint resource) => natives.Value.Register(resource);
	public static void Unregister(nint resource) => natives.Value.Unregister(resource);
	public static void Unref(nint resource) => natives.Value.Unref(resource);
}
