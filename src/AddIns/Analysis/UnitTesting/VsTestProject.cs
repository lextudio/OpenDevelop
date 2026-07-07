using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.TypeSystem;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace ICSharpCode.UnitTesting
{
	public class VsTestProject : TestProjectBase
	{
		DiscoveredTests discoveredTests = new DiscoveredTests();
		DateTime? lastBuildTime;
		bool discoveryInProgress;

		public VsTestProject(IProject project)
			: base(project)
		{
			lastBuildTime = GetAssemblyLastWriteTime();
			SD.BuildService.BuildFinished += OnBuildFinished;
		}

		protected override void OnNestedTestsInitialized()
		{
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
				discoveredTests = await VsTestDiscoveryAdapter.Instance.DiscoverTestsAsync(Project);
				PopulateTree();
			} catch (Exception ex) {
				SD.Log.Warn("VsTest discovery failed: " + ex.Message);
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

			VsTestTreeBuilder.BuildTree(this, collection, discoveredTests.Tests);
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
				return new VsTestDebugger(this, options);
			return new VsTestRunner(this, options);
		}

		public override IEnumerable<ITest> GetTestsForEntity(IEntity entity)
		{
			return Enumerable.Empty<ITest>();
		}

		public override void UpdateTestResult(TestResult result)
		{
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

		public IReadOnlyList<TestCase> GetTestCasesForSelectedTests(IEnumerable<ITest> selectedTests)
		{
			var cases = new List<TestCase>();
			CollectTestCases(selectedTests, cases);
			return cases;
		}

		void CollectTestCases(IEnumerable<ITest> tests, List<TestCase> results)
		{
			foreach (var test in tests) {
				if (test is VsTestMethod method) {
					results.AddRange(method.TestCases);
				} else if (test.NestedTests != null) {
					CollectTestCases(test.NestedTests, results);
				}
			}
		}

		public void UpdateResult(TestResult result)
		{
		}
	}
}
