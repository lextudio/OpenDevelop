using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using ICSharpCode.WpfDesign.XamlDom;

namespace ICSharpCode.WpfDesign.SurfaceHost
{
	/// <summary>
	/// Child-side type finder (Phase 1 slice, see wpf-designer.md's Phase 1 progress notes).
	/// Modeled on the live in-process WpfDesign.AddIn/Src/MyTypeFinder.cs, but driven purely by
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
					RegisterAssembly(Assembly.LoadFrom(path));
				}
				catch (Exception)
				{
					// Best-effort preload, same as MyTypeFinder.Create: a bad reference here
					// just means that reference's types won't resolve, not a fatal load error.
				}
			}

			if (!string.IsNullOrEmpty(projectAssemblyPath) && File.Exists(projectAssemblyPath))
			{
				try
				{
					projectAssembly = Assembly.LoadFrom(projectAssemblyPath);
					RegisterAssembly(projectAssembly);
				}
				catch (Exception)
				{
					projectAssembly = null;
				}
			}
		}

		public override Assembly? LoadAssembly(string name)
		{
			if (string.IsNullOrEmpty(name))
				return projectAssembly;

			var path = referencedAssemblyPaths.FirstOrDefault(candidate =>
				!string.IsNullOrEmpty(candidate) &&
				string.Equals(Path.GetFileNameWithoutExtension(candidate), name, StringComparison.OrdinalIgnoreCase));
			if (path != null && File.Exists(path))
			{
				try
				{
					return Assembly.LoadFrom(path);
				}
				catch (Exception)
				{
					// Fall through to the base resolver below.
				}
			}

			return base.LoadAssembly(name);
		}

		public override XamlTypeFinder Clone()
		{
			var copy = new SurfaceTypeFinder(projectAssemblyPath, referencedAssemblyPaths);
			copy.ImportFrom(this);
			return copy;
		}
	}
}
