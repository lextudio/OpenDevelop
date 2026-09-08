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
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// A solution whose projects are evaluated by an in-process MSBuild
	/// <see cref="Microsoft.Build.Evaluation.ProjectCollection"/>.
	///
	/// This exists so that <see cref="ISolution"/> - the core interface every consumer sees - does
	/// not itself expose an MSBuild type. Only code that genuinely evaluates MSBuild projects
	/// (<see cref="MSBuildBasedProject"/>) needs the collection; Solution Explorer, the designers,
	/// PackageManagement and the AddIn system all just read project data and were never served by
	/// having it on the core interface.
	///
	/// Keeping it separate is what makes it possible to later move evaluation out of the IDE
	/// process (doc/technotes/roslyn-host-process.md §7a): a solution whose projects are evaluated
	/// elsewhere simply does not implement this interface, and the compiler - rather than a runtime
	/// failure - identifies every place that still assumes in-process evaluation.
	/// </summary>
	public interface IMSBuildSolution : ISolution
	{
		/// <summary>
		/// The project collection this solution's projects are loaded into. Access is guarded by
		/// <c>MSBuildInternals.SolutionProjectCollectionLock</c>; see its callers.
		/// </summary>
		Microsoft.Build.Evaluation.ProjectCollection MSBuildProjectCollection { get; }
	}
}
