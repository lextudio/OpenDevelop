using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

		void TriggerDiscovery()
		{
			if (discoveryInProgress)
				return;

			discoveryInProgress = true;
			var _ = DiscoverTestsAsync();
		}

		// Forces a fresh MTP discovery pass on demand, for an explicit "Refresh Tests" UI action -
		// discovery otherwise only runs once (lazily, on first NestedTests access) and again after
		// every build (OnBuildFinished). Internal rather than a full public ITestProject/ITest
		// member since it's a UI-triggered mechanism, not part of the test model contract itself.
		internal void RefreshForTesting() => TriggerDiscovery();

		async Task DiscoverTestsAsync()
		{
			try {
				var discovered = new Dictionary<string, IReadOnlyList<MtpTestNode>>(StringComparer.OrdinalIgnoreCase);
				foreach (var targetFramework in GetTargetFrameworks()) {
					var assemblyPath = ResolveAssemblyDll(Project, targetFramework);
					if (assemblyPath == null || !File.Exists(assemblyPath))
						continue;

					await using var server = await MtpServerProcess.StartAsync(assemblyPath, Path.GetDirectoryName(assemblyPath), default);
					await server.InitializeAsync(default);
					discovered[targetFramework] = await server.DiscoverTestsAsync(default);
				}
				discoveredNodesByTargetFramework = discovered;
				PopulateTree();
			} catch (Exception ex) {
				SD.Log.Warn("MTP discovery failed: " + ex.Message);
			} finally {
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
			var separator = result.Name.IndexOf('\0');
			var targetFramework = separator >= 0 ? result.Name.Substring(0, separator) : null;
			var displayName = separator >= 0 ? result.Name.Substring(separator + 1) : result.Name;
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
			if (project is MSBuildBasedProject msbuildProject && !string.IsNullOrEmpty(targetFramework)) {
				var outputPath = msbuildProject.GetEvaluatedProperty("OutputPath", targetFramework);
				var assemblyName = msbuildProject.GetEvaluatedProperty("AssemblyName", targetFramework);
				if (!string.IsNullOrEmpty(outputPath) && !string.IsNullOrEmpty(assemblyName))
					return Path.Combine(project.Directory.ToString(), outputPath, assemblyName + ".dll");
			}

			var dir = Path.GetDirectoryName(project.OutputAssemblyFullPath?.ToString());
			return dir != null ? Path.Combine(dir, project.AssemblyName + ".dll") : project.OutputAssemblyFullPath;
		}
	}
}
