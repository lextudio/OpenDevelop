// Copyright (c) 2025 AlphaSierraPapa for the SharpDevelop Team
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
using System.Collections.Generic;
using ICSharpCode.SharpDevelop.LanguageServices.OpenLens;
using ICSharpCode.UnitTesting.Mtp;

namespace ICSharpCode.UnitTesting
{
	/// <summary>
	/// Lets another AddIn contribute extra items to the test lens's click menu (the
	/// <see cref="OpenLensMenu"/> <see cref="TestOpenLensProvider"/> attaches to each test method
	/// anchor). The dependency direction stays right: UnitTesting cannot reference CodeCoverage
	/// (CodeCoverage references UnitTesting), so the contributor is registered by the owning AddIn
	/// (e.g. <c>RegisterCodeCoverageOpenLensProviderCommand</c>) and consumed here.
	///
	/// <paramref name="resolveCurrentTest"/> re-resolves the test by symbol key at click time -
	/// the tree node captured at menu-build time can be stale, since the test tree is replaced
	/// wholesale on discovery/solution changes. The contributor's returned item must invoke it
	/// before using the test instance.
	/// </summary>
	public interface ITestLensMenuContributor
	{
		OpenLensMenuItem GetMenuItem(Func<ITest> resolveCurrentTest);
	}

	/// <summary>
	/// Registry of <see cref="ITestLensMenuContributor"/>s, matching how the OpenLens host itself
	/// composes providers: an AddIn that owns a test-adjacent capability (code coverage, ...)
	/// registers its contributor while loaded, and unregisters it in Dispose, so disabling that
	/// AddIn removes its menu items exactly like it removes its pads.
	/// </summary>
	public static class TestLensMenuContributors
	{
		static readonly List<ITestLensMenuContributor> contributors = new List<ITestLensMenuContributor>();

		public static IDisposable Register(ITestLensMenuContributor contributor)
		{
			lock (contributors) {
				contributors.Add(contributor);
				return new Unregistration(contributor);
			}
		}

		public static IEnumerable<ITestLensMenuContributor> GetContributors()
		{
			lock (contributors) {
				return contributors.ToArray();
			}
		}

		sealed class Unregistration : IDisposable
		{
			readonly ITestLensMenuContributor contributor;

			public Unregistration(ITestLensMenuContributor contributor)
			{
				this.contributor = contributor;
			}

			public void Dispose()
			{
				lock (contributors) {
					contributors.Remove(contributor);
				}
			}
		}
	}
}
