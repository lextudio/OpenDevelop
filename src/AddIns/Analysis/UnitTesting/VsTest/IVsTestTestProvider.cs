using System.Collections.Generic;
using ICSharpCode.SharpDevelop.Project;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace ICSharpCode.UnitTesting
{
	interface IVsTestTestProvider
	{
		IProject Project { get; }
		IEnumerable<TestCase> GetTests();
	}
}
