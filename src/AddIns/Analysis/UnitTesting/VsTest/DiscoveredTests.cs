using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace ICSharpCode.UnitTesting
{
	class DiscoveredTests
	{
		readonly List<TestCase> tests = new List<TestCase>();

		public IEnumerable<TestCase> Tests {
			get { return tests; }
		}

		public void Add(IEnumerable<TestCase> newTests)
		{
			tests.AddRange(newTests);
		}

		public IReadOnlyList<TestCase> GetTestCases(ITest test)
		{
			if (test is VsTestMethod method)
				return method.TestCases;

			return Array.Empty<TestCase>();
		}
	}
}
