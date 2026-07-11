using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.TypeSystem;
using ICSharpCode.UnitTesting.Mtp;

namespace ICSharpCode.UnitTesting
{
	public class MtpTestProject : TestProjectBase
	{
		IReadOnlyList<MtpTestNode> discoveredNodes = Array.Empty<MtpTestNode>();
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
			TriggerDiscovery();
		}

		void TriggerDiscovery()
		{
			if (discoveryInProgress)
				return;

			discoveryInProgress = true;
			var _ = DiscoverTestsAsync();
		}

		async Task DiscoverTestsAsync()
		{
			try {
				var assemblyPath = ResolveAssemblyDll(Project);
				if (assemblyPath == null || !File.Exists(assemblyPath))
					return;

				await using var server = await MtpServerProcess.StartAsync(assemblyPath, Path.GetDirectoryName(assemblyPath), default);
				await server.InitializeAsync(default);
				discoveredNodes = await server.DiscoverTestsAsync(default);
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

			MtpTestTreeBuilder.BuildTree(this, collection, discoveredNodes);
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
			var path = Project.OutputAssemblyFullPath;
			if (path != null && File.Exists(path))
				return File.GetLastWriteTime(path);
			return null;
		}

		public override ITestRunner CreateTestRunner(TestExecutionOptions options)
		{
			if (options.UseDebugger)
				return new MtpTestDebugger(this, options);
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
			var method = FindTestMethod(NestedTestCollection, result.Name);
			if (method != null)
				method.SetResult(result.ResultType);
		}

		static MtpTestMethod FindTestMethod(IEnumerable<ITest> tests, string name)
		{
			foreach (var test in tests) {
				if (test is MtpTestMethod method && method.DisplayName == name)
					return method;
				if (test.NestedTests != null) {
					var found = FindTestMethod(test.NestedTests, name);
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
		public static string ResolveAssemblyDll(IProject project)
		{
			var dir = Path.GetDirectoryName(project.OutputAssemblyFullPath?.ToString());
			return dir != null ? Path.Combine(dir, project.AssemblyName + ".dll") : project.OutputAssemblyFullPath;
		}
	}
}
