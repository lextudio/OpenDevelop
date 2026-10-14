using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using ICSharpCode.WpfDesign.XamlDom;

namespace ICSharpCode.WpfDesign.SurfaceHost
{
	/// <summary>
	/// Child-side type finder (Phase 1 slice, see wpf-designer.md's Phase 1 progress notes).
	/// Modeled on the former in-process WpfDesign.AddIn MyTypeFinder (removed: the IDE must not
	/// load project assemblies), but driven purely by
	/// snapshot-carried paths - no OpenedFile/SD.ProjectService/IDE project-system dependency,
	/// since none of that exists in this child. Loading target assemblies here (not in
	/// OpenDevelop) is the whole point of this slice.
	/// </summary>
	sealed class SurfaceTypeFinder : XamlTypeFinder
	{
		readonly string projectAssemblyPath;
		readonly IReadOnlyList<string> referencedAssemblyPaths;
		Assembly? projectAssembly;

		/// <summary>The project's own loaded assembly (null if <c>projectAssemblyPath</c> was
		/// empty or failed to load) - lets a caller reflect it for design-time conventions such
		/// as enumerating embedded <c>themes/*.xaml</c> resources without its own separate load.</summary>
		public Assembly? ProjectAssembly => projectAssembly;

		public SurfaceTypeFinder(string projectAssemblyPath, IReadOnlyList<string> referencedAssemblyPaths)
		{
			this.projectAssemblyPath = projectAssemblyPath;
			this.referencedAssemblyPaths = referencedAssemblyPaths;
			ImportFrom(CreateWpfTypeFinder());

			foreach (var path in referencedAssemblyPaths)
			{
				if (string.IsNullOrEmpty(path) || !File.Exists(path))
					continue;
				try
				{
					RegisterAssembly(LoadWithoutLocking(path));
				}
				catch (Exception)
				{
					// Best-effort preload, as the in-process type finder did: a bad reference here
					// just means that reference's types won't resolve, not a fatal load error.
				}
			}

			if (!string.IsNullOrEmpty(projectAssemblyPath) && File.Exists(projectAssemblyPath))
			{
				try
				{
					projectAssembly = LoadWithoutLocking(projectAssemblyPath);
					RegisterAssembly(projectAssembly);
				}
				catch (Exception e)
				{
					projectAssembly = null;
					Console.Error.WriteLine($"design-host: could not load project assembly '{projectAssemblyPath}': {e.GetBaseException().Message}");
				}
			}
		}

		public override Assembly? LoadAssembly(string name)
		{
			if (string.IsNullOrEmpty(name))
				return projectAssembly;
			// The project's own output is loaded from bytes (LoadWithoutLocking), which the runtime
			// cannot find again by name, so an explicit "clr-namespace:X;assembly=<project>" has to
			// be answered here or it falls through to Assembly.Load and fails.
			if (projectAssembly != null && string.Equals(projectAssembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
				return projectAssembly;

			var path = referencedAssemblyPaths.FirstOrDefault(candidate =>
				!string.IsNullOrEmpty(candidate) &&
				string.Equals(Path.GetFileNameWithoutExtension(candidate), name, StringComparison.OrdinalIgnoreCase));
			if (path != null && File.Exists(path))
			{
				try
				{
					return LoadWithoutLocking(path);
				}
				catch (Exception)
				{
					// Fall through to the base resolver below.
				}
			}

			return base.LoadAssembly(name);
		}

		/// <summary>An application-relative pack URI that names no assembly
		/// (<c>pack://application:,,,/Assets/x.png</c>) resolves against
		/// Application.ResourceAssembly - this child host, not the designed project - so name the
		/// project assembly explicitly. Done here, on the converted value, rather than by rewriting
		/// the page text: the text is the document model and is saved back to the user's file.</summary>
		public override Uri ConvertUriToLocalUri(Uri uri)
		{
			const string prefix = "pack://application:,,,/";
			var text = uri.OriginalString;
			if (projectAssembly == null
				|| !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
				|| text.IndexOf(";component/", StringComparison.OrdinalIgnoreCase) >= 0)
				return uri;
			return new Uri(prefix + projectAssembly.GetName().Name + ";component/" + text.Substring(prefix.Length), UriKind.Absolute);
		}

		public override XamlTypeFinder Clone()
		{
			var copy = new SurfaceTypeFinder(projectAssemblyPath, referencedAssemblyPaths);
			copy.ImportFrom(this);
			return copy;
		}

		// Assembly.LoadFrom keeps the file open for as long as this process lives, and this host is
		// deliberately long-lived: SharedDesignerHostPool reuses it across documents and even across
		// IDE restarts. That left the project's OWN output locked after previewing a page, so the
		// next build of that project died with MSB3027/MSB3021 "The file is locked by: .NET Host",
		// which reads like a stale lock rather than the designer still holding the assembly it was
		// asked to reflect over. Read the bytes and let the handle close instead - the same thing
		// the in-process designer already does (Base/Project/Designer/TypeResolutionService.cs).
		//
		// Assembly.Load(byte[]) does not deduplicate the way LoadFrom does: loading one file twice
		// produces two assemblies whose types do not unify, and Clone() rebuilds a finder from the
		// same paths, so the cache has to be process-wide rather than per-instance. Keying on the
		// file's identity-and-stamp means a rebuilt assembly is picked up as a new load instead of
		// being served stale from the cache.
		static readonly ConcurrentDictionary<string, Assembly> loadedAssemblies =
			new ConcurrentDictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

		static Assembly LoadWithoutLocking(string path)
		{
			var info = new FileInfo(path);
			var key = info.FullName + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length;
			return loadedAssemblies.GetOrAdd(key, _ => Assembly.Load(File.ReadAllBytes(info.FullName)));
		}
	}
}
