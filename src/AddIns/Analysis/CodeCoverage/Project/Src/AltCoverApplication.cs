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
using System.Diagnostics;
using System.IO;
using System.Text;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.CodeCoverage
{
	/// <summary>
	/// Replaces <see cref="OpenCoverApplication"/> as the code-coverage backend: OpenCover is
	/// unmaintained (last release 2018-era), AltCover (https://github.com/SteveGilham/altcover)
	/// is its actively-maintained successor and - conveniently - can emit reports in OpenCover's
	/// own XML schema (its default <c>--reportFormat</c>), so <see cref="CodeCoverageResults"/>
	/// and the rest of the parsing/UI pipeline need no changes at all.
	///
	/// Mechanically these two tools are NOT drop-in equivalents, though. OpenCover.Console is a
	/// single process: it hooks the assembly via the CLR profiling API at the moment the target
	/// process starts, so one invocation wraps prepare+run+report entirely
	/// (<c>OpenCover.Console -target:x -targetargs:y -output:z</c>). AltCover instruments ahead
	/// of time by rewriting IL on disk, so the same job takes three steps instead of one:
	///
	///   1. Prepare  - "AltCover.exe -i &lt;dir&gt; --inplace --save -r &lt;report&gt; ..."
	///                 Rewrites the assemblies in the target's output directory in place (the
	///                 unmodified originals are backed up under "__Saved_..." by AltCover itself)
	///                 and writes a zero-visits skeleton of the OpenCover-format report.
	///   2. (run)    - The caller launches the target process completely unwrapped - by the time
	///                 this runs, the on-disk assemblies already ARE the instrumented ones.
	///   3. Collect  - "AltCover.exe Runner -r &lt;dir&gt; ..."
	///                 Reads the visit-count data the instrumented assemblies recorded during the
	///                 run and merges it into the report from step 1, then restores the original
	///                 (pre-instrumentation) assemblies from the "__Saved_..." backup.
	///
	/// <see cref="RunTestWithCodeCoverageCommand"/> (and friends) must run all three steps in
	/// order: GetPrepareProcessStartInfo() to completion, then the actual test-runner process
	/// (unmodified - do NOT wrap it the way OpenCoverApplication.GetProcessStartInfo() used to),
	/// then GetCollectProcessStartInfo() to completion.
	///
	/// This project (like the rest of OpenDevelop) no longer targets classic .NET Framework, so
	/// AltCover is bundled as its net8.0 build (tools/net8.0/AltCover.dll in the AltCover NuGet
	/// package - a plain managed assembly, not a native exe) and launched via the "dotnet" host,
	/// mirroring the exact pattern DapSession.cs already uses to launch SharpDbg.Cli.dll: honor
	/// DOTNET_HOST_PATH if set, otherwise assume "dotnet" is on PATH. See
	/// DapSession.ResolveDotNetHost() (src/AddIns/Debugger/Debugger.AddIn/Service/Dap/DapSession.cs).
	///
	/// NOTE: this class was written and reviewed against AltCover's actual CLI argument-building
	/// source (externals/altcover/AltCover.Engine/Args.fs) but has NOT been executed end-to-end -
	/// treat the exact argument list as a well-researched first draft, not a verified one, and
	/// test it for real before relying on it.
	/// </summary>
	public class AltCoverApplication
	{
		string fileName = String.Empty;
		ProcessStartInfo targetProcessStartInfo;
		OpenCoverSettings settings;
		IProject project;

		public AltCoverApplication(
			ProcessStartInfo targetProcessStartInfo,
			OpenCoverSettings settings,
			IProject project)
		{
			this.targetProcessStartInfo = targetProcessStartInfo;
			this.settings = settings;
			this.project = project;
			GetAltCoverApplicationFileName();
		}

		void GetAltCoverApplicationFileName()
		{
			// Mirrors OpenCoverApplication's "bin\Tools\OpenCover\OpenCover.Console.exe" bundling
			// convention, but bundles AltCover's net8.0 build (a managed dll, run via "dotnet",
			// see ResolveDotNetHost()) rather than a native exe - see class remarks above for why.
			fileName = Path.Combine(FileUtility.ApplicationRootPath, @"bin\Tools\AltCover\AltCover.dll");
			fileName = Path.GetFullPath(fileName);
		}

		public string FileName {
			get { return fileName; }
			set { fileName = value; }
		}

		public string Target {
			get { return targetProcessStartInfo.FileName; }
		}

		public string GetTargetArguments()
		{
			return targetProcessStartInfo.Arguments;
		}

		public string GetTargetWorkingDirectory()
		{
			return Path.GetDirectoryName(project.OutputAssemblyFullPath);
		}

		public string CodeCoverageResultsFileName {
			get { return new ProjectCodeCoverageResultsFileName(project).FileName; }
		}

		/// <summary>
		/// Step 1: instruments the target's output directory in place. Must be run - and must
		/// have exited - before the (unwrapped) target process from step 2 is started.
		/// </summary>
		public ProcessStartInfo GetPrepareProcessStartInfo()
		{
			var processStartInfo = new ProcessStartInfo();
			processStartInfo.FileName = ResolveDotNetHost();
			processStartInfo.Arguments = "\"" + FileName + "\" " + GetPrepareArguments();
			return processStartInfo;
		}

		/// <summary>
		/// Step 3: merges the run's recorded visit data into the report started in step 1, then
		/// restores the pre-instrumentation assemblies. Must be run only after the target process
		/// from step 2 has exited.
		/// </summary>
		public ProcessStartInfo GetCollectProcessStartInfo()
		{
			var processStartInfo = new ProcessStartInfo();
			processStartInfo.FileName = ResolveDotNetHost();
			processStartInfo.Arguments = "\"" + FileName + "\" " + GetCollectArguments();
			return processStartInfo;
		}

		/// <summary>
		/// Same resolution rule as DapSession.ResolveDotNetHost(): DOTNET_HOST_PATH wins if set,
		/// otherwise fall back to "dotnet" on PATH. Kept in sync deliberately - if that method's
		/// resolution rule ever changes, update this one too.
		/// </summary>
		static string ResolveDotNetHost()
		{
			string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
			return !string.IsNullOrEmpty(host) ? host : "dotnet";
		}

		string GetPrepareArguments()
		{
			string targetDir = GetTargetWorkingDirectory();
			var arguments = new StringBuilder();
			arguments.AppendFormat("-i \"{0}\" ", targetDir);
			arguments.Append("--inplace --save ");
			arguments.AppendFormat("--reportFormat OpenCover -r \"{0}\" ", CodeCoverageResultsFileName);
			AppendIncludedItems(arguments);
			AppendExcludedItems(arguments);
			return arguments.ToString().Trim();
		}

		string GetCollectArguments()
		{
			string targetDir = GetTargetWorkingDirectory();
			var arguments = new StringBuilder();
			arguments.AppendFormat("Runner -r \"{0}\" ", targetDir);
			return arguments.ToString().Trim();
		}

		void AppendIncludedItems(StringBuilder arguments)
		{
			foreach (string item in settings.Include) {
				arguments.AppendFormat("-s \"{0}\" ", item);
			}
		}

		void AppendExcludedItems(StringBuilder arguments)
		{
			foreach (string item in settings.Exclude) {
				arguments.AppendFormat("-e \"{0}\" ", item);
			}
		}
	}
}
