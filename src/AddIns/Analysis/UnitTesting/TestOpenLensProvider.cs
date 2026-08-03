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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices.OpenLens;
using ICSharpCode.UnitTesting.Mtp;

namespace ICSharpCode.UnitTesting
{
	/// <summary>
	/// "Run | ✓ Passed (12ms)" lens (doc/technotes/openlens.md §20 Phase 4), attaching to the same
	/// method anchors <c>LanguageOpenLensAnchorProvider</c> already discovers - this class
	/// contributes an additional lens to those rows, it doesn't discover its own anchors.
	///
	/// Matches an anchor to a test by <see cref="OpenLensAnchor.SymbolKey"/> ("Type.Method", set by
	/// <c>LanguageOpenLensAnchorProvider</c>) against <see cref="MtpTestMethod.FullyQualifiedName"/> -
	/// found by walking <see cref="ITestService.OpenSolution"/>'s tree, since there is no
	/// queryable-by-name lookup service (only the tree itself holds live result state). This is a
	/// per-request O(n) walk over the whole test tree, not indexed - acceptable for the tree sizes
	/// this AddIn already handles interactively (nav bar, test explorer), but a solution with a very
	/// large number of discovered tests could make this lens noticeably slower to resolve than the
	/// reference/implementation/coverage lenses, which don't need a full-tree walk per anchor.
	/// </summary>
	public sealed class TestOpenLensProvider : IOpenLensProvider
	{
		public const string LensId = "test";

		public string Id => "UnitTesting";
		public int Order => 3;

		public bool CanHandle(OpenLensDocumentContext context) => true;

		public Task<IReadOnlyList<OpenLensItem>> ProvideAsync(
			OpenLensDocumentContext context, IReadOnlyList<OpenLensAnchor> anchors, CancellationToken cancellationToken)
		{
			var testService = SD.GetService<ITestService>();
			var solution = testService?.OpenSolution;
			if (solution == null)
				return Task.FromResult<IReadOnlyList<OpenLensItem>>(Array.Empty<OpenLensItem>());

			var items = new List<OpenLensItem>();
			foreach (var anchor in anchors) {
				if (anchor.Kind != OpenLensAnchorKind.Method || anchor.SymbolKey == null)
					continue;

				var method = FindTestMethod(solution.NestedTests, anchor.SymbolKey);
				if (method == null)
					continue;

				items.Add(new OpenLensItem(
					ProviderId: Id, LensId: LensId, AnchorId: anchor.AnchorId, Order: 3,
					Presentation: new OpenLensPresentation(FormatStatus(method)),
					Command: new OpenLensCommand("OpenLens.RunAction", (Action)(() =>
						testService.RunTestsAsync(new ITest[] { method }, new TestExecutionOptions()).FireAndForget())),
					ResolveData: null, IsResolved: true));
			}
			return Task.FromResult<IReadOnlyList<OpenLensItem>>(items);
		}

		public Task<OpenLensItem> ResolveAsync(OpenLensDocumentContext context, OpenLensItem item, CancellationToken cancellationToken) =>
			Task.FromResult(item);

		static string FormatStatus(MtpTestMethod method)
		{
			string duration = method.Duration is { } d ? $" ({(int)d.TotalMilliseconds}ms)" : "";
			return method.Result switch {
				TestResultType.Success => "Run | ✓ Passed" + duration,
				TestResultType.Failure => "Run | ✗ Failed" + duration,
				TestResultType.Ignored => "Run | Ignored",
				_ => "Run | Not run",
			};
		}

		static MtpTestMethod FindTestMethod(IEnumerable<ITest> tests, string symbolKey)
		{
			foreach (var test in tests) {
				if (test is MtpTestMethod method && method.FullyQualifiedName == symbolKey)
					return method;
				if (test.NestedTests != null) {
					var found = FindTestMethod(test.NestedTests, symbolKey);
					if (found != null)
						return found;
				}
			}
			return null;
		}
	}
}
