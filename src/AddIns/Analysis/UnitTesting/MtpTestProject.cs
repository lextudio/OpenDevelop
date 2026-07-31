using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.TypeSystem;
using ICSharpCode.UnitTesting.Mtp;
using ICSharpCode.UnitTesting.Simple;

namespace ICSharpCode.UnitTesting
{
	public class MtpTestProject : TestProjectBase
	{
		IReadOnlyDictionary<string, IReadOnlyList<MtpTestNode>> discoveredNodesByTargetFramework
			= new Dictionary<string, IReadOnlyList<MtpTestNode>>(StringComparer.OrdinalIgnoreCase);
		DateTime? lastBuildTime;
		bool discoveryInProgress;

		public MtpTestProject(IProject project)
			: base(project)
		{
			lastBuildTime = GetAssemblyLastWriteTime();
			SD.BuildService.BuildFinished += OnBuildFinished;
		}

		protected override void OnNestedTestsInitialized()
		{
			// Deliberately does NOT chain to TestProjectBase.OnNestedTestsInitialized (that does
			// the old Roslyn/parser-based type walk this class replaced with MTP discovery), but
			// MUST still restore the composite-result binding that TestBase sets up - without this
			// the project node's Result stayed None forever, so a failing test coloured its
			// class/namespace nodes but the colour never propagated up to the project node or the
			// "All Tests" solution root above it.
			RebindCompositeResultToNestedTests();
			PopulateApproxTreeFromRoslyn();
			TriggerDiscovery();
		}

		// Fast, approximate pass: a syntax-only Roslyn scan of the project's own source (see
		// RoslynTestScanner and doc/technotes/unit-testing.md), so the tree shows candidate tests
		// immediately instead of staying empty for the ~30-60s an MTP discovery round trip can
		// take. TriggerDiscovery's real MTP pass replaces discoveredNodesByTargetFramework (and
		// rebuilds the tree via the same PopulateTree call) once it completes, same as it always
		// did - this just seeds it with an approximate answer first instead of nothing.
		void PopulateApproxTreeFromRoslyn()
		{
			try {
				var candidates = RoslynTestScanner.ScanProject(Project.Directory?.ToString());
				if (candidates.Count == 0)
					return;

				var approx = new Dictionary<string, IReadOnlyList<MtpTestNode>>(StringComparer.OrdinalIgnoreCase);
				foreach (var targetFramework in GetTargetFrameworks()) {
					approx[targetFramework] = candidates.Select(BuildApproxNode).ToList();
				}

				discoveredNodesByTargetFramework = approx;
				PopulateTree();
			} catch (Exception ex) {
				SD.Log.Warn("Roslyn approximate test scan failed: " + ex.Message);
			}
		}

		// A synthetic MtpTestNode standing in for a not-yet-MTP-confirmed candidate. Uid is
		// deliberately empty (never a valid MTP uid) rather than a made-up value, so
		// MtpTestRunner's unconfirmed-selection guard can detect it cheaply via
		// string.IsNullOrEmpty rather than needing a separate "is this real" flag threaded through
		// MtpTestMethod/MtpTestNode.
		static MtpTestNode BuildApproxNode(RoslynTestCandidate candidate)
		{
			var payload = new Dictionary<string, object?> {
				["uid"] = string.Empty,
				["display-name"] = candidate.DisplayName,
				["node-type"] = "action",
				["location.type"] = candidate.TypeFullName,
				["location.method"] = candidate.MethodName,
			};
			return MtpTestNode.FromJson(JsonSerializer.SerializeToElement(payload));
		}

		// The in-flight (or last completed) discovery pass, so callers can await completion instead
		// of polling the tree and guessing when it has settled. Never faulted: DiscoverTestsAsync
		// handles its own exceptions.
		Task discoveryTask = Task.CompletedTask;

		Task TriggerDiscovery(CancellationToken cancellationToken = default)
		{
			if (discoveryInProgress)
				return discoveryTask;

			discoveryInProgress = true;
			return discoveryTask = DiscoverTestsAsync(cancellationToken);
		}

		/// <summary>
		/// Starts a fresh MTP discovery pass (unless one is already in flight) and returns a task
		/// that completes once the tree reflects its result. Discovery otherwise runs only once
		/// (lazily, on first <see cref="TestBase.NestedTests"/> access) and again after every build
		/// (OnBuildFinished), so an explicit "Refresh Tests" action needs this entry point.
		/// </summary>
		/// <param name="cancellationToken">Abandons the pass; the tree keeps whatever it had.</param>
		public Task RefreshAsync(CancellationToken cancellationToken = default) => TriggerDiscovery(cancellationToken);

		async Task DiscoverTestsAsync(CancellationToken cancellationToken)
		{
			var discovered = new Dictionary<string, IReadOnlyList<MtpTestNode>>(StringComparer.OrdinalIgnoreCase);
			try {
				foreach (var targetFramework in GetTargetFrameworks()) {
					try {
						var assemblyPath = ResolveAssemblyDll(Project, targetFramework);
						if (assemblyPath == null || !File.Exists(assemblyPath))
							continue;

						cancellationToken.ThrowIfCancellationRequested();
						await using var server = await MtpServerProcess.StartAsync(assemblyPath, Path.GetDirectoryName(assemblyPath), cancellationToken);
						await server.InitializeAsync(cancellationToken);
						discovered[targetFramework] = await server.DiscoverTestsAsync(cancellationToken);
					} catch (OperationCanceledException) {
						throw;
					} catch (Exception ex) {
						SD.Log.Warn("MTP discovery failed for " + Project.Name + " " + targetFramework + ": " + ex.Message);
					}
				}
			} catch (OperationCanceledException) {
				// The user cancelled from the status bar: leave the tree exactly as it was rather
				// than reporting a failure for something they asked to stop.
			} finally {
				// Never replace an already-populated (approximate) tree with an empty result: when
				// no target framework yielded a built assembly the loop above `continue`s past every
				// await, so this would wipe the Roslyn candidates PopulateApproxTreeFromRoslyn just
				// added, leaving the project node permanently empty instead of falling back to the
				// approximate list.
				if (discovered.Count > 0) {
					discoveredNodesByTargetFramework = discovered;
					PopulateTree();
				}
				discoveryInProgress = false;
			}
		}

		void PopulateTree()
		{
			if (!NestedTestsInitialized)
				return;

			var collection = base.NestedTestCollection;
			collection.Clear();

			foreach (var pair in discoveredNodesByTargetFramework.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)) {
				var targetFramework = new MtpTargetFramework(this, pair.Key);
				MtpTestTreeBuilder.BuildTree(this, targetFramework.NestedTests, pair.Value, pair.Key);
				collection.Add(targetFramework);
			}
		}

		void OnBuildFinished(object sender, BuildEventArgs args)
		{
			if (!args.Projects.Contains(Project))
				return;

			var buildTime = GetAssemblyLastWriteTime();
			if (buildTime.HasValue && lastBuildTime.HasValue && buildTime <= lastBuildTime)
				return;

			lastBuildTime = buildTime;
			TriggerDiscovery();
		}

		DateTime? GetAssemblyLastWriteTime()
		{
			return GetTargetFrameworks()
				.Select(targetFramework => ResolveAssemblyDll(Project, targetFramework))
				.Where(path => path != null && File.Exists(path))
				.Select(path => (DateTime?)File.GetLastWriteTime(path))
				.DefaultIfEmpty(null)
				.Max();
		}

		public override ITestRunner CreateTestRunner(TestExecutionOptions options)
		{
#if !HAS_UNO
			if (options.UseDebugger)
				return new MtpTestDebugger(this, options);
#endif
			// "Run under debugger" (MtpTestDebugger/TestDebuggerBase, ICSharpCode.SharpDevelop.
			// Debugging-coupled) isn't linked under Uno - UnoDevelop's test panel never exposed
			// that option even under the old flat contract, so falling back to the plain runner
			// here isn't a regression.
			return new MtpTestRunner(this, options);
		}

		public override IEnumerable<ITest> GetTestsForEntity(IEntity entity)
		{
			return Enumerable.Empty<ITest>();
		}

		public override void UpdateTestResult(TestResult result)
		{
			// Match the incoming result back to the MtpTestMethod node it belongs to by display
			// name (MtpTestRunner builds the SD TestResult's name from the same MtpTestNode.DisplayName
			// that MtpTestMethod's own DisplayName came from at discovery time) and apply it.
			var resultName = result is MtpTestResult mtpResult ? mtpResult.FullName : result.Name;
			var separator = resultName.IndexOf('\0');
			var targetFramework = separator >= 0 ? resultName.Substring(0, separator) : null;
			var displayName = separator >= 0 ? resultName.Substring(separator + 1) : resultName;
			var method = FindTestMethod(NestedTestCollection, targetFramework, displayName);
			if (method != null)
				method.SetResult(result.ResultType);
		}

		static MtpTestMethod FindTestMethod(IEnumerable<ITest> tests, string targetFramework, string name)
		{
			foreach (var test in tests) {
				if (test is MtpTestMethod method && method.DisplayName == name
				    && (targetFramework == null || string.Equals(method.TargetFramework, targetFramework, StringComparison.OrdinalIgnoreCase)))
					return method;
				if (test.NestedTests != null) {
					var found = FindTestMethod(test.NestedTests, targetFramework, name);
					if (found != null)
						return found;
				}
			}
			return null;
		}

		protected override bool IsTestClass(ITypeDefinition typeDefinition)
		{
			return false;
		}

		protected override ITest CreateTestClass(ITypeDefinition typeDefinition)
		{
			return null;
		}

		protected override void UpdateTestClass(ITest test, ITypeDefinition typeDefinition)
		{
		}

		protected override void AddToDirtyList(TopLevelTypeName className)
		{
		}

		public IReadOnlyList<MtpTestNode> GetTestNodesForSelectedTests(IEnumerable<ITest> selectedTests)
		{
			var nodes = new List<MtpTestNode>();
			CollectTestNodes(selectedTests, nodes);
			return nodes;
		}

		internal IReadOnlyList<MtpTestMethod> GetTestMethodsForSelectedTests(IEnumerable<ITest> selectedTests)
		{
			var methods = new List<MtpTestMethod>();
			CollectTestMethods(selectedTests, methods);
			return methods;
		}

		void CollectTestMethods(IEnumerable<ITest> tests, List<MtpTestMethod> results)
		{
			foreach (var test in tests) {
				if (test is MtpTestMethod method)
					results.Add(method);
				else if (test.NestedTests != null)
					CollectTestMethods(test.NestedTests, results);
			}
		}

		void CollectTestNodes(IEnumerable<ITest> tests, List<MtpTestNode> results)
		{
			foreach (var test in tests) {
				if (test is MtpTestMethod method) {
					results.Add(method.Node);
				} else if (test.NestedTests != null) {
					CollectTestNodes(test.NestedTests, results);
				}
			}
		}

		// VSTest discovery/execution always needs the managed assembly (.dll), regardless of the
		// project's OutputType. Modern MTP test projects (xunit.v3) set OutputType=Exe so `dotnet
		// exec`/the apphost can run them as a self-hosted test app, but don't necessarily produce a
		// native apphost for every TFM/platform - so project.OutputAssemblyFullPath (which follows
		// OutputType's Exe/WinExe/.exe-or-apphost naming convention) can point at a file that was
		// never built. The managed assembly next to it is always "<AssemblyName>.dll", and that's
		// what `dotnet exec`/MtpServerProcess.StartAsync needs.
		public IReadOnlyList<string> GetTargetFrameworks()
		{
			var frameworks = ProjectTargetFrameworkService.GetTargetFrameworks(Project);
			return frameworks.Count == 0 ? new[] { string.Empty } : frameworks;
		}

		public static string ResolveAssemblyDll(IProject project, string targetFramework)
		{
			// Candidates in preference order; the first that exists on disk wins, so a project whose
			// declared output layout doesn't match what was actually built still resolves.
			var candidates = new List<string>();

			if (project is MSBuildBasedProject msbuildProject && !string.IsNullOrEmpty(targetFramework)) {
				var assemblyName = msbuildProject.GetEvaluatedProperty("AssemblyName", targetFramework);
				if (!string.IsNullOrEmpty(assemblyName)) {
					var configuration = project.ActiveConfiguration.Configuration;
					candidates.Add(Path.Combine(project.Directory.ToString(), "bin", configuration, targetFramework, assemblyName + ".dll"));
				}

				var outputPath = msbuildProject.GetEvaluatedProperty("OutputPath", targetFramework);
				if (!string.IsNullOrEmpty(outputPath) && !string.IsNullOrEmpty(assemblyName)) {
					// OutputPath comes straight from MSBuild, which writes Windows separators
					// ("bin\Debug\") on every platform. Path.Combine does not translate those, so on
					// Unix the backslashes stay literal and the result names one absurd directory
					// ("bin\Debug") that cannot exist - silently skipping discovery for this TFM.
					var normalizedOutputPath = NormalizeDirectorySeparators(outputPath);
					candidates.Add(Path.Combine(project.Directory.ToString(), normalizedOutputPath, assemblyName + ".dll"));
					// OutputPath is TFM-qualified for multi-targeted projects but not always for
					// single-TFM ones, so try both shapes rather than assuming which applies.
					candidates.Add(Path.Combine(project.Directory.ToString(), normalizedOutputPath, targetFramework, assemblyName + ".dll"));
				}
			}

			var dir = Path.GetDirectoryName(project.OutputAssemblyFullPath?.ToString());
			if (dir != null) {
				candidates.Add(Path.Combine(dir, project.AssemblyName + ".dll"));
				// Project models that report a TFM-less OutputAssemblyFullPath such as
				// "bin/Debug/X.dll" while the SDK actually writes "bin/Debug/<tfm>/X.dll".
				if (!string.IsNullOrEmpty(targetFramework))
					candidates.Add(Path.Combine(dir, targetFramework, project.AssemblyName + ".dll"));
			}

			foreach (var candidate in candidates) {
				if (File.Exists(candidate))
					return candidate;
			}
			// Nothing is built yet: hand back the most specific guess so callers report a missing
			// assembly at the path a build would produce, rather than a nonexistent-by-construction one.
			return candidates.Count > 0 ? candidates[candidates.Count - 1] : project.OutputAssemblyFullPath;
		}

		static string NormalizeDirectorySeparators(string path)
		{
			return Path.DirectorySeparatorChar == '\\'
				? path
				: path.Replace('\\', Path.DirectorySeparatorChar);
		}
	}
}
