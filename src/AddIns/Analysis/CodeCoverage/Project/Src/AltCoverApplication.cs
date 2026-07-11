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
		OpenCoverSettings settings;
		IProject project;

		readonly string workingResultsFileName;

		public AltCoverApplication(
			OpenCoverSettings settings,
			IProject project)
		{
			this.settings = settings;
			this.project = project;
			GetAltCoverApplicationFileName();
			workingResultsFileName = MakeWorkingResultsFileName();
		}

		void GetAltCoverApplicationFileName()
		{
			// Mirrors OpenCoverApplication's "bin\Tools\OpenCover\OpenCover.Console.exe" bundling
			// convention, but bundles AltCover's net8.0 build (a managed dll, run via "dotnet",
			// see ResolveDotNetHost()) rather than a native exe - see class remarks above for why.
			// Path.Combine treats a literal "bin\Tools\..." string as a single path segment on
			// non-Windows platforms (backslash isn't a separator there), so the previous
			// @"bin\Tools\AltCover\AltCover.dll" never resolved to a real file on macOS/Linux -
			// AltCover's prepare/collect steps silently no-op against a nonexistent path, so no
			// results ever appear and od.code-coverage.run's result-polling loop burns its full
			// timeout. Combine each segment separately so Path.Combine uses the platform separator.
			//
			// Also: use AppDomain.CurrentDomain.BaseDirectory, not FileUtility.ApplicationRootPath.
			// CodeCoverage.csproj's DeployAltCoverTool target - by design (see that target's own
			// comment) - copies AltCover's files under the exe's own output directory
			// (bin/Debug/<tfm>/bin/Tools/AltCover), on the assumption that ApplicationRootPath
			// "resolves to AppDomain.CurrentDomain.BaseDirectory at runtime". That assumption is
			// wrong: ApplicationRootPath (SharpDevelopMain.FindApplicationRootPath) walks up from
			// the exe looking for a data/resources/languages/LanguageDefinition.xml marker file,
			// which happens to exist at this repo's root - so it resolves there instead, well
			// above where the tool is actually deployed, and the prepare/collect processes always
			// launched against a nonexistent path.
			fileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "Tools", "AltCover", "AltCover.dll");
			fileName = Path.GetFullPath(fileName);
		}

		public string FileName {
			get { return fileName; }
			set { fileName = value; }
		}

		public string GetTargetWorkingDirectory()
		{
			return Path.GetDirectoryName(project.OutputAssemblyFullPath);
		}

		public string CodeCoverageResultsFileName {
			get { return new ProjectCodeCoverageResultsFileName(project).FileName; }
		}

		// Every AltCover-instrumented process (not just the actual test run - a plain VSTest
		// *discovery* pass against the in-place-instrumented assembly counts too, and the IDE
		// triggers those on its own) registers a process-exit handler that flushes recorded visits
		// to the report path given to Prepare/Collect via -r. Two coverage runs (or a coverage run
		// racing an incidental discovery pass) close enough together then throw "IOException: ...
		// being used by another process" or "FileNotFoundException" fighting over the SAME shared
		// CodeCoverageResultsFileName. Give every AltCoverApplication instance (one per Run()) a
		// unique working path instead, so no two runs/processes can ever collide - then copy the
		// finished result onto the stable CodeCoverageResultsFileName in PromoteResultsToStableFileName()
		// once collection genuinely succeeds, since CodeCoverageService.SolutionLoaded reads that
		// well-known per-project path (not this instance) to restore last known results when a
		// solution is reopened.
		public string WorkingResultsFileName {
			get { return workingResultsFileName; }
		}

		string MakeWorkingResultsFileName()
		{
			string stable = CodeCoverageResultsFileName;
			string directory = Path.GetDirectoryName(stable);
			string extension = Path.GetExtension(stable);
			string baseName = Path.GetFileNameWithoutExtension(stable);
			return Path.Combine(directory ?? string.Empty, baseName + "." + Guid.NewGuid().ToString("N") + extension);
		}

		/// <summary>
		/// Copies the just-collected report from this run's unique working path onto the stable,
		/// well-known per-project path other code (CodeCoverageResultsReader, SolutionCodeCoverageResults)
		/// expects, then removes the working copy. Call only after GetCollectProcessStartInfo()'s
		/// process has exited successfully.
		/// </summary>
		public void PromoteResultsToStableFileName()
		{
			if (!File.Exists(workingResultsFileName))
				return;
			string stable = CodeCoverageResultsFileName;
			string stableDirectory = Path.GetDirectoryName(stable);
			if (!string.IsNullOrEmpty(stableDirectory))
				Directory.CreateDirectory(stableDirectory);
			File.Copy(workingResultsFileName, stable, overwrite: true);
			File.Delete(workingResultsFileName);
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
			arguments.AppendFormat("--reportFormat OpenCover -r \"{0}\" ", WorkingResultsFileName);
			AppendIncludedItems(arguments);
			AppendExcludedItems(arguments);
			return arguments.ToString().Trim();
		}

		string GetCollectArguments()
		{
			string targetDir = GetTargetWorkingDirectory();
			var arguments = new StringBuilder();
			// --collect is required: without it, "Runner" tries to launch a process itself (needs
			// -x/--executable) rather than processing the already-recorded raw coverage data from
			// the (already-completed) unwrapped test run - omitting it made AltCover reject the
			// command entirely and print its usage/help text instead of collecting anything, so
			// results never appeared (verified end-to-end: -r alone above shows help; with
			// --collect it correctly reports real visited classes/methods/points).
			arguments.AppendFormat("Runner -r \"{0}\" --collect ", targetDir);
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
