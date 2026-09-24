// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Real per-control Toolbox icons for the WinForms toolbox pad.
//
// Where the icons come from: the PARENT process (OpenDevelop.exe / this add-in) loads the
// LibreWinForms build of System.Windows.Forms (assembly identity "System.Windows.Forms
// v0.1.0.0", from the librewinforms.system.windows.forms package). Its current builds embed the
// same 199 toolbox icons as Microsoft's assembly (Resources/System/Windows/Forms/*.ico, named
// after the type), so they are read from that already-loaded assembly first - which is also the
// only source on macOS, where no Microsoft.WindowsDesktop.App is installed. Older LibreWinForms
// builds carried ZERO manifest resources, so the installed Microsoft.WindowsDesktop.App copy of
// System.Windows.Forms.dll remains the next source, read without loading it.
//
// Two details that the old code got wrong and this deliberately handles:
//   * Naming: in modern .NET the resources are named EXACTLY the full type name with NO extension
//     ("System.Windows.Forms.Button"), not "<TypeFullName>.bmp" - that suffix is the .NET
//     Framework-era convention and is why the legacy lookup always missed. BOTH spellings are
//     accepted here, because both sources below are consulted.
//   * Format: the modern payload is a Windows ICO stream (magic 00 00 01 00), not a BMP - e.g.
//     System.Windows.Forms.Button is 1150 bytes / 1 image / 16x16, while
//     System.Windows.Forms.ToolStripButton is 52366 bytes / 7 images / up to 64x64. It must be
//     decoded with System.Drawing.Icon; the .NET Framework-era payloads really are BMPs and need
//     new Bitmap(stream) instead, so the decoder sniffs the magic bytes.
//
// Icon SOURCES, in order. Modern .NET only kept the toolbox icons that live in
// System.Windows.Forms.dll; the ones belonging to types in other assemblies (DataSet/DataView,
// BackgroundWorker, EventLog, PerformanceCounter, Process, FileSystemWatcher, SerialPort,
// PrintDocument) were dropped from the shared framework entirely - verified by scanning every
// manifest resource of every DLL in Microsoft.WindowsDesktop.App / Microsoft.NETCore.App /
// Microsoft.AspNetCore.App, which yields exactly one hit (System.Windows.Forms.Timer). They are,
// however, still present in the .NET Framework assemblies that ship with Windows, under the
// legacy "<TypeFullName>.bmp" name:
//   * %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Data.dll   - DataSet, DataView
//   * ...\System.dll         - BackgroundWorker, EventLog, PerformanceCounter, Process,
//                              FileSystemWatcher, SerialPort
//   * ...\System.Drawing.dll - PrintDocument
// Reading those files gives the very icons the original SharpDevelop showed for these components.
//
// Every read is a RESOURCE-ONLY PE read (System.Reflection.Metadata + PortableExecutable, both
// in-box on net10.0) and deliberately never calls Assembly.Load/LoadFrom: loading Microsoft's
// System.Windows.Forms into this process would collide with the identically-named LibreWinForms
// assembly already loaded here, and the .NET Framework assemblies cannot be loaded into a
// .NET 10 process at all. Only their resource tables are parsed.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ICSharpCode.SharpDevelop.Gui
{
	/// <summary>Reads per-type Toolbox icons out of the loaded LibreWinForms assembly (or, failing
	/// that, the installed Microsoft WinForms assembly without loading it), and caches them for the
	/// lifetime of the process.</summary>
	public static class WinFormsToolboxIconProvider
	{
		static readonly object gate = new object();
		static Dictionary<string, byte[]> resources;
		static bool resourcesLoaded;
		static readonly Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>(StringComparer.Ordinal);

		/// <summary>The real Toolbox icon for <paramref name="typeFullName"/> (e.g.
		/// "System.Windows.Forms.Button"), or null when neither the loaded LibreWinForms assembly
		/// nor an installed Microsoft WinForms assembly carries a resource for that type, or the payload cannot be decoded - in
		/// which case the caller keeps its own placeholder/fallback behavior.</summary>
		public static Bitmap GetIcon(string typeFullName)
		{
			if (String.IsNullOrEmpty(typeFullName))
				return null;
			lock (gate) {
				if (iconCache.TryGetValue(typeFullName, out var cached))
					return cached;
				Bitmap bitmap = null;
				try {
					// The LibreWinForms build loaded in this process embeds the same toolbox icons
					// (Resources/System/Windows/Forms/*.ico, named after the type). Reading them
					// from there needs no Microsoft WinForms install, so it also works on macOS.
					bitmap = LoadLoadedWinFormsIcon(typeFullName);
					var map = bitmap == null ? LoadResources() : null;
					// Modern .NET names the resource after the type with no suffix; the .NET
					// Framework assemblies use the legacy "<TypeFullName>.bmp" spelling.
					if (map != null
						&& (map.TryGetValue(typeFullName, out var payload) || map.TryGetValue(typeFullName + ".bmp", out payload))
						&& payload.Length > 0) {
						bitmap = Decode(payload);
					}
					// Fall back to the icons shipped with OpenDevelop for the components whose
					// icons modern .NET no longer carries anywhere (see this file's header).
					bitmap ??= LoadShippedIcon(typeFullName);
				} catch (Exception exception) {
					ICSharpCode.Core.LoggingService.Warn("WinFormsToolboxIconProvider.GetIcon(" + typeFullName + "): " + exception.Message);
					bitmap = null;
				}
				iconCache[typeFullName] = bitmap;
				return bitmap;
			}
		}

		/// <summary>The toolbox icon as a frozen WPF ImageSource (for the Toolbox pad and the
		/// Document Outline), or null when <see cref="GetIcon"/> has none.</summary>
		public static System.Windows.Media.ImageSource GetImageSource(string typeFullName)
		{
			try {
				// NOT disposed: GetIcon caches its bitmaps for the process lifetime.
				var bitmap = GetIcon(typeFullName);
				if (bitmap == null)
					return null;
				lock (gate) {
					using var stream = new MemoryStream();
					bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
					stream.Position = 0;
					var image = new System.Windows.Media.Imaging.BitmapImage();
					image.BeginInit();
					image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
					image.StreamSource = stream;
					image.EndInit();
					image.Freeze();
					return image;
				}
			} catch (Exception exception) {
				ICSharpCode.Core.LoggingService.Warn("WinFormsToolboxIconProvider.GetImageSource(" + typeFullName + "): " + exception.Message);
				return null;
			}
		}

		/// <summary>The icon embedded in the System.Windows.Forms assembly already loaded in this
		/// process (the LibreWinForms build), or null when it is not loaded or has no such
		/// resource. Never loads the assembly itself.</summary>
		static Bitmap LoadLoadedWinFormsIcon(string typeFullName)
		{
			var forms = AppDomain.CurrentDomain.GetAssemblies()
				.FirstOrDefault(assembly => assembly.GetName().Name == "System.Windows.Forms");
			if (forms == null)
				return null;
			using var stream = forms.GetManifestResourceStream(typeFullName);
			if (stream == null)
				return null;
			using var buffer = new MemoryStream();
			stream.CopyTo(buffer);
			return buffer.Length > 0 ? Decode(buffer.ToArray()) : null;
		}

		/// <summary>Decodes a toolbox-icon payload, normalized to the 16x16 the toolbox rows and
		/// tray entries draw at. Both formats this provider encounters are handled by sniffing the
		/// magic bytes: modern System.Windows.Forms resources are Windows ICO containers (possibly
		/// multi-image, up to 64x64), while the .NET Framework-era ones are plain BMPs whose
		/// transparency is the classic "bottom-left pixel is the transparent colour" convention
		/// that Bitmap.MakeTransparent() implements - without it those icons render on an opaque
		/// magenta/silver block.</summary>
		static Bitmap Decode(byte[] payload)
		{
			using var stream = new MemoryStream(payload);
			var isIcon = payload.Length >= 4 && payload[0] == 0x00 && payload[1] == 0x00
				&& payload[2] == 0x01 && payload[3] == 0x00;
			if (isIcon) {
				using var icon = new System.Drawing.Icon(stream);
				using var full = icon.ToBitmap();
				return full.Width == 16 && full.Height == 16 ? new Bitmap(full) : new Bitmap(full, 16, 16);
			}
			using var source = new Bitmap(stream);
			var bitmap = source.Width == 16 && source.Height == 16 ? new Bitmap(source) : new Bitmap(source, 16, 16);
			try {
				bitmap.MakeTransparent();
			} catch {
				// An indexed/odd-format bitmap can refuse MakeTransparent; an opaque icon is
				// still better than none.
			}
			return bitmap;
		}

		/// <summary>The icon shipped inside this assembly for the components modern .NET no longer
		/// provides one for (Resources\WinFormsToolboxIcons\&lt;TypeFullName&gt;.bmp, embedded with
		/// its file name as the resource name), or null when there is none.</summary>
		static Bitmap LoadShippedIcon(string typeFullName)
		{
			try {
				using var stream = typeof(WinFormsToolboxIconProvider).Assembly
					.GetManifestResourceStream(typeFullName + ".bmp");
				if (stream == null)
					return null;
				using var buffer = new MemoryStream();
				stream.CopyTo(buffer);
				return Decode(buffer.ToArray());
			} catch (Exception exception) {
				ICSharpCode.Core.LoggingService.Warn(
					"WinFormsToolboxIconProvider.LoadShippedIcon(" + typeFullName + "): " + exception.Message);
				return null;
			}
		}

		/// <summary>Parses the whole manifest-resource table once (199 entries) into a
		/// name-&gt;bytes map. Returns null when the assembly cannot be located or read.</summary>
		static Dictionary<string, byte[]> LoadResources()
		{
			if (resourcesLoaded)
				return resources;
			resourcesLoaded = true;
			var path = LocateMicrosoftWinFormsAssembly();
			if (path == null) {
				ICSharpCode.Core.LoggingService.Info(
					"WinFormsToolboxIconProvider: no Microsoft.WindowsDesktop.App System.Windows.Forms.dll found; Toolbox icons fall back to the placeholder.");
				return null;
			}
			try {
				var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
				using var file = File.OpenRead(path);
				using var pe = new PEReader(file);
				var metadata = pe.GetMetadataReader();
				var directory = pe.PEHeaders.CorHeader.ResourcesDirectory;
				if (directory.Size == 0)
					return resources = map;
				var sectionIndex = pe.PEHeaders.GetContainingSectionIndex(directory.RelativeVirtualAddress);
				if (sectionIndex < 0)
					return resources = map;
				var section = pe.PEHeaders.SectionHeaders[sectionIndex];
				var block = pe.GetSectionData(section.VirtualAddress).GetContent();
				var baseOffset = directory.RelativeVirtualAddress - section.VirtualAddress;
				foreach (var handle in metadata.ManifestResources) {
					var resource = metadata.GetManifestResource(handle);
					// Only resources embedded in THIS file have no implementation reference.
					if (!resource.Implementation.IsNil)
						continue;
					var name = metadata.GetString(resource.Name);
					var offset = baseOffset + (int)resource.Offset;
					if (offset < 0 || offset + 4 > block.Length)
						continue;
					var length = BitConverter.ToInt32(block.ToArray(), offset);
					if (length < 0 || offset + 4 + length > block.Length)
						continue;
					map[name] = block.Slice(offset + 4, length).ToArray();
				}
				ICSharpCode.Core.LoggingService.Debug(
					"WinFormsToolboxIconProvider: read " + map.Count + " manifest resources from " + path);
				return resources = map;
			} catch (Exception exception) {
				ICSharpCode.Core.LoggingService.Warn("WinFormsToolboxIconProvider.LoadResources: " + exception.Message);
				return null;
			}
		}

		/// <summary>Highest-versioned installed Microsoft.WindowsDesktop.App copy of
		/// System.Windows.Forms.dll, or null when the shared framework is not installed.
		/// Version directories are compared as real Versions (so 10.0.x beats 9.0.4, which a
		/// plain string sort would get wrong).</summary>
		static string LocateMicrosoftWinFormsAssembly()
		{
			try {
				var roots = new[] {
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "shared", "Microsoft.WindowsDesktop.App")
				};
				return roots.Where(Directory.Exists)
					.SelectMany(root => Directory.GetDirectories(root))
					.Select(directory => new {
						Path = Path.Combine(directory, "System.Windows.Forms.dll"),
						Version = Version.TryParse(Path.GetFileName(directory).Split('-')[0], out var version) ? version : new Version(0, 0)
					})
					.Where(candidate => File.Exists(candidate.Path))
					.OrderByDescending(candidate => candidate.Version)
					.Select(candidate => candidate.Path)
					.FirstOrDefault();
			} catch (Exception exception) {
				ICSharpCode.Core.LoggingService.Warn("WinFormsToolboxIconProvider.LocateMicrosoftWinFormsAssembly: " + exception.Message);
				return null;
			}
		}
	}
}
