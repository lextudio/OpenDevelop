// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// Optionally implemented by an <see cref="IMSBuildEngine"/> that can run MSBuild's
	/// ResolveReferences for many projects in one MSBuild process. One `dotnet msbuild` per project
	/// spends most of its time starting the process and resolving the SDK; one process for a whole
	/// solution shares both, and its evaluation caches (doc/technotes/fast-mode.md).
	/// </summary>
	public interface IBatchAssemblyReferenceResolver
	{
		/// <summary>
		/// The resolved reference assembly paths of each project, keyed by project. A project whose
		/// resolution failed maps to an empty list (the failure is reported the same way a single
		/// resolution's is). A project missing from the result was not resolved at all - the whole
		/// batch failed - and callers fall back to <see cref="IMSBuildEngine.ResolveAssemblyReferences"/>
		/// for it. Never throws.
		/// </summary>
		IReadOnlyDictionary<MSBuildBasedProject, IReadOnlyList<string>> ResolveAssemblyReferencePaths(IReadOnlyList<MSBuildBasedProject> projects);
	}
}
