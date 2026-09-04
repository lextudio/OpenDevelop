// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Real per-control Toolbox icons for the WinForms toolbox pad.
//
// Why this exists at all: the PARENT process (OpenDevelop.exe / this add-in) loads the
// LibreWinForms build of System.Windows.Forms (assembly identity "System.Windows.Forms
// v0.1.0.0", from the librewinforms.system.windows.forms package), and that assembly carries
// ZERO manifest resources - so neither the legacy "<TypeFullName>.bmp" manifest-resource lookup
// nor System.Drawing.ToolboxBitmapAttribute.GetImageFromResource can ever produce an icon in
// this process, no matter how the lookup is written. Microsoft's real WinForms assembly DOES
// carry them (199 manifest resources), so the icons are read straight out of the installed
// Microsoft.WindowsDesktop.App copy of System.Windows.Forms.dll instead.
//
// Two details that the old code got wrong and this deliberately handles:
//   * Naming: the resources are named EXACTLY the full type name with NO extension
//     ("System.Windows.Forms.Button"), not "<TypeFullName>.bmp" - that suffix is the .NET
//     Framework-era convention and is why the legacy lookup always missed.
//   * Format: the payload is a Windows ICO stream (magic 00 00 01 00), not a BMP - e.g.
//     System.Windows.Forms.Button is 1150 bytes / 1 image / 16x16, while
//     System.Windows.Forms.ToolStripButton is 52366 bytes / 7 images / up to 64x64. It must be
//     decoded with System.Drawing.Icon, not new Bitmap(stream).
//
// The read is a RESOURCE-ONLY PE read (System.Reflection.Metadata + PortableExecutable, both
// in-box on net10.0) and deliberately never calls Assembly.Load/LoadFrom: loading Microsoft's
// System.Windows.Forms into this process would collide with the identically-named LibreWinForms
// assembly already loaded here.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ICSharpCode.SharpDevelop.Gui
{
	/// <summary>Reads per-type Toolbox icons out of the installed Microsoft WinForms assembly
	/// without loading it, and caches them for the lifetime of the process.</summary>
	public static class WinFormsToolboxIconProvider
	{
		static readonly object gate = new object();
		static Dictionary<string, byte[]> resources;
		static bool resourcesLoaded;
		static readonly Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>(StringComparer.Ordinal);

		/// <summary>The real Toolbox icon for <paramref name="typeFullName"/> (e.g.
		/// "System.Windows.Forms.Button"), or null when the Microsoft WinForms assembly is not
		/// installed, carries no resource for that type, or the payload cannot be decoded - in
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
					var map = LoadResources();
					if (map != null && map.TryGetValue(typeFullName, out var payload) && payload.Length > 0) {
						using var stream = new MemoryStream(payload);
						// The payload is an ICO container (possibly multi-image); Icon(stream, w, h)
						// picks the frame closest to the requested size, which is what a 16x16
						// toolbox row wants out of e.g. ToolStripButton's 7-image icon.
						using var icon = new System.Drawing.Icon(stream);
						using var full = icon.ToBitmap();
						// Normalize to the 16x16 the toolbox rows draw at - a multi-image ICO
						// (ToolStripButton's goes up to 64x64) otherwise renders oversized.
						bitmap = full.Width == 16 && full.Height == 16
							? new Bitmap(full)
							: new Bitmap(full, 16, 16);
					}
				} catch (Exception exception) {
					ICSharpCode.Core.LoggingService.Warn("WinFormsToolboxIconProvider.GetIcon(" + typeFullName + "): " + exception.Message);
					bitmap = null;
				}
				iconCache[typeFullName] = bitmap;
				return bitmap;
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
