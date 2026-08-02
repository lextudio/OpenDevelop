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
using System.IO;
using System.Text;

using ICSharpCode.CodeCoverage;
using ICSharpCode.CodeCoverage.Tests.Utils;
using ICSharpCode.SharpDevelop;
using NUnit.Framework;
using UnitTesting.Tests.Utils;

namespace ICSharpCode.CodeCoverage.Tests.Coverage
{
	[TestFixture]
	public class CodeCoverageSettingsFactoryTests : SDTestFixtureBase
	{
		CodeCoverageSettingsFactory factory;
		CodeCoverageSettings codeCoverageSettings;
		MockCSharpProject project;
		MockFileSystem fileSystem;
		
		[SetUp]
		public void Init()
		{
			fileSystem = new MockFileSystem();
			factory = new CodeCoverageSettingsFactory(fileSystem);
			project = new MockCSharpProject();
		}
		
		[Test]
		public void CreateCodeCoverageSettingsWhenFileDoesNotExistCreatesSettingsWithNoIncludes()
		{
			fileSystem.FileExistsReturnValue = false;
			CreateCodeCoverageSettingsFromFactory();
			Assert.AreEqual(0, codeCoverageSettings.Include.Count);
		}
		
		void CreateCodeCoverageSettingsFromFactory()
		{
			codeCoverageSettings = factory.CreateCodeCoverageSettings(project);
		}
		
		[Test]
		public void CreateCodeCoverageSettingsWhenFileExistsCreatesSettingsFromFile()
		{
			string codeCoverageSettingsXml =
				"<CodeCoverageSettings>\r\n" +
				"   <Rule>+test</Rule>\r\n" +
				"</CodeCoverageSettings>";
			
			StringReader reader = new StringReader(codeCoverageSettingsXml);
			fileSystem.CreateTextReaderReturnValue = reader;
			
			fileSystem.FileExistsReturnValue = true;
			
			CreateCodeCoverageSettingsFromFactory();
			Assert.AreEqual("test", codeCoverageSettings.Include[0]);
		}
	}
}
