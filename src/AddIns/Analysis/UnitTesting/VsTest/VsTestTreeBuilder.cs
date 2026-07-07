using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace ICSharpCode.UnitTesting
{
	static class VsTestTreeBuilder
	{
		public static void BuildTree(
			VsTestProject project,
			TestCollection rootCollection,
			IEnumerable<TestCase> testCases)
		{
			if (project == null)
				throw new ArgumentNullException("project");
			var groupedByClass = testCases
				.GroupBy(tc => GetClassName(tc))
				.OrderBy(g => g.Key);

			foreach (var classGroup in groupedByClass) {
				var className = classGroup.Key;
				var classNamespace = GetNamespace(className);
				var shortClassName = GetShortName(className);

				var nsCollection = FindOrCreateNamespace(project, rootCollection, classNamespace);

				var testClass = new VsTestClass(project, shortClassName);
				foreach (var tc in classGroup.OrderBy(tc => tc.DisplayName)) {
					var method = new VsTestMethod(project, tc);
					testClass.NestedTests.Add(method);
				}

				nsCollection.Add(testClass);
			}
		}

		static TestCollection FindOrCreateNamespace(VsTestProject project, TestCollection rootCollection, string ns)
		{
			if (string.IsNullOrEmpty(ns))
				return rootCollection;

			foreach (var node in rootCollection.OfType<TestNamespace>()) {
				if (ns == node.NamespaceName)
					return node.NestedTests;
				if (ns.StartsWith(node.NamespaceName + ".", StringComparison.Ordinal))
					return FindOrCreateNamespace(project, node.NestedTests, ns);
			}

			var parts = ns.Split('.');
			var displayName = parts.Last();

			var newNs = new TestNamespace(project, ns, displayName);
			rootCollection.Add(newNs);
			return newNs.NestedTests;
		}

		static string GetClassName(TestCase tc)
		{
			var fqn = tc.FullyQualifiedName;
			int lastDot = fqn.LastIndexOf('.');
			if (lastDot < 0)
				return fqn;
			return fqn.Substring(0, lastDot);
		}

		static string GetNamespace(string className)
		{
			int lastDot = className.LastIndexOf('.');
			if (lastDot < 0)
				return string.Empty;
			return className.Substring(0, lastDot);
		}

		static string GetShortName(string className)
		{
			int lastDot = className.LastIndexOf('.');
			if (lastDot < 0)
				return className;
			return className.Substring(lastDot + 1);
		}
	}
}
