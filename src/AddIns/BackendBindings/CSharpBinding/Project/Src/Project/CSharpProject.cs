// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using ICSharpCode.SharpDevelop.Project;

namespace CSharpBinding
{
	/// <summary>
	/// IProject implementation for .csproj files.
	/// </summary>
	// Minimal ownership-correct registration (doc/technotes/csharp-vb-binding.md Phase 0). The old
	// NRefactory-based IProjectContent/CSharpProjectContent type-system wiring and CSharpProjectBehavior
	// (compiler-version negotiation, ISymbol-based symbol search) are retired here, not ported - project
	// loading/build/references come from the shared MSBuild/CPS project system regardless.
	public class CSharpProject : CompilableProject
	{
		public CSharpProject(ProjectLoadInformation loadInformation)
			: base(loadInformation)
		{
		}

		public CSharpProject(ProjectCreateInformation info)
			: base(info)
		{
		}

		public override string Language {
			get { return CSharpProjectBinding.LanguageName; }
		}

		public override bool UpgradeDesired {
			get {
				if (IsSdkStyleProject)
					return false;
				return base.UpgradeDesired;
			}
		}
	}
}
