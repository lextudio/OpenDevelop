using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.UnitTesting.Mtp
{
	static class MtpTestTreeBuilder
	{
		public static void BuildTree(
			MtpTestProject project,
			TestCollection rootCollection,
			IEnumerable<MtpTestNode> nodes,
			string targetFramework)
		{
			if (project == null)
				throw new ArgumentNullException("project");

			// Only leaf ("action") nodes are actual tests - "group" nodes (assembly/namespace/class
			// groupings the host itself may report) are ignored; the tree below is rebuilt from the
			// leaves using each leaf's declaring-type name, same as VsTestTreeBuilder did from
			// TestCase.FullyQualifiedName.
			var groupedByClass = nodes
				.Where(n => n.NodeType == "action")
				.GroupBy(GetClassName)
				.OrderBy(g => g.Key);

			foreach (var classGroup in groupedByClass) {
				var className = classGroup.Key;
				var classNamespace = GetNamespace(className);
				var shortClassName = GetShortName(className);

				var nsCollection = FindOrCreateNamespace(project, rootCollection, classNamespace);

				var testClass = new MtpTestClass(project, shortClassName);
				foreach (var node in classGroup.OrderBy(n => n.DisplayName)) {
					var method = new MtpTestMethod(project, node, targetFramework);
					testClass.NestedTests.Add(method);
				}

				nsCollection.Add(testClass);
			}
		}

		static TestCollection FindOrCreateNamespace(MtpTestProject project, TestCollection rootCollection, string ns)
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

		static string GetClassName(MtpTestNode node)
		{
			if (!string.IsNullOrEmpty(node.LocationType))
				return node.LocationType!;

			// Fallback for hosts that don't report location.type (e.g. NUnit's MTP/VSTest bridge):
			// treat everything up to the last dot in the display name as the "class".
			var fqn = node.DisplayName;
			int lastDot = fqn.LastIndexOf('.');
			return lastDot < 0 ? fqn : fqn.Substring(0, lastDot);
		}

		static string GetNamespace(string className)
		{
			int lastDot = className.LastIndexOf('.');
			return lastDot < 0 ? string.Empty : className.Substring(0, lastDot);
		}

		static string GetShortName(string className)
		{
			int lastDot = className.LastIndexOf('.');
			return lastDot < 0 ? className : className.Substring(lastDot + 1);
		}
	}
}
