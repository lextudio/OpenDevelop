// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.

using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// Optionally implemented by an <see cref="IMSBuildEngine"/>: the Analyzer items (analyzers and source
	/// generators) MSBuild resolves for one target framework of a project. Package and targeting-pack analyzers
	/// only exist after ResolvePackageAssets/ResolveTargetingPackAssets, so evaluation of a multi-targeted
	/// project's slice has none of them.
	/// </summary>
	public interface ITargetFrameworkAnalyzerResolver
	{
		/// <summary>The resolved analyzer paths, empty when resolution failed. Never throws.</summary>
		IReadOnlyList<string> ResolveAnalyzers(MSBuildBasedProject project, string targetFramework);
	}
}
