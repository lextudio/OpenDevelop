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
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices.OpenLens;
using ICSharpCode.UnitTesting.Mtp;

namespace ICSharpCode.UnitTesting
{
	/// <summary>
	/// "✓ Passed (12ms)" lens (doc/technotes/openlens.md §20 Phase 4), attaching to the same
	/// method anchors <c>LanguageOpenLensAnchorProvider</c> already discovers - this class
	/// contributes an additional lens to those rows, it doesn't discover its own anchors. Clicking
	/// the lens opens a small menu ("Run Test" / "Debug Test") supplied through
	/// <see cref="OpenLensMenu"/>, so the run and debug behaviors both live in this AddIn while the
	/// OpenLens host only provides the anchored popup.
	///
	/// Matches an anchor to a test by <see cref="OpenLensAnchor.SymbolKey"/> ("Type.Method", set by
	/// <c>LanguageOpenLensAnchorProvider</c>) against <see cref="MtpTestMethod.FullyQualifiedName"/> -
	/// found by walking <see cref="ITestService.OpenSolution"/>'s tree, since there is no
	/// queryable-by-name lookup service (only the tree itself holds live result state). This is a
	/// per-request O(n) walk over the whole test tree, not indexed - acceptable for the tree sizes
	/// this AddIn already handles interactively (nav bar, test explorer), but a solution with a very
	/// large number of discovered tests could make this lens noticeably slower to resolve than the
	/// reference/implementation/coverage lenses, which don't need a full-tree walk per anchor. The
	/// menu actions re-walk the tree by symbol key at click time rather than capturing a tree node,
	/// since the tree is replaced wholesale on discovery/solution changes.
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
					Presentation: new OpenLensPresentation(
						FormatStatus(method), Severity: SeverityFor(method.Result), IconKey: IconKeyFor(method.Result)),
					Command: new OpenLensCommand("OpenLens.ShowMenu", CreateMenu(testService, anchor.SymbolKey)),
					ResolveData: null, IsResolved: true));
			}
			return Task.FromResult<IReadOnlyList<OpenLensItem>>(items);
		}

		public Task<OpenLensItem> ResolveAsync(OpenLensDocumentContext context, OpenLensItem item, CancellationToken cancellationToken) =>
			Task.FromResult(item);

		static OpenLensMenu CreateMenu(ITestService testService, string symbolKey)
		{
			var items = new List<OpenLensMenuItem> {
				new OpenLensMenuItem("Run Test", () => RunTests(testService, symbolKey, debug: false), IconKey: "Icons.16x16.RunAllIcon"),
				new OpenLensMenuItem("Debug Test", () => RunTests(testService, symbolKey, debug: true), IconKey: "Icons.16x16.Debug.Bug"),
			};
			foreach (var contributor in TestLensMenuContributors.GetContributors()) {
				var extra = contributor.GetMenuItem(() => ResolveTest(testService, symbolKey));
				if (extra != null)
					items.Add(extra);
			}
			return new OpenLensMenu(items);
		}

		static MtpTestMethod ResolveTest(ITestService testService, string symbolKey)
		{
			return testService.OpenSolution == null ? null : FindTestMethod(testService.OpenSolution.NestedTests, symbolKey);
		}

		static void RunTests(ITestService testService, string symbolKey, bool debug)
		{
			var method = ResolveTest(testService, symbolKey);
			if (method == null) {
				LoggingService.Warn("OpenLens: test '" + symbolKey + "' not found in the test tree.");
				return;
			}
			testService.RunTestsAsync(new ITest[] { method }, new TestExecutionOptions { UseDebugger = debug }).FireAndForget();
		}

		// The lens row renders icon-only (this title becomes the tooltip), using the same
		// "UnitTesting.Status.*" icons as the Unit Tests pad.
		static string FormatStatus(MtpTestMethod method)
		{
			string duration = method.Duration is { } d ? $" ({(int)d.TotalMilliseconds}ms)" : "";
			return method.Result switch {
				TestResultType.Success => "Passed" + duration,
				TestResultType.Failure => "Failed" + duration,
				TestResultType.Ignored => "Ignored",
				_ => "Not run",
			};
		}

		static string IconKeyFor(TestResultType result) => result switch {
			TestResultType.Success => "UnitTesting.Status.Passed",
			TestResultType.Failure => "UnitTesting.Status.Failed",
			TestResultType.Ignored => "UnitTesting.Status.Skipped",
			_ => "UnitTesting.Status.NotRun",
		};

		static OpenLensSeverity SeverityFor(TestResultType result) => result switch {
			TestResultType.Failure => OpenLensSeverity.Error,
			TestResultType.Ignored => OpenLensSeverity.Warning,
			_ => OpenLensSeverity.Normal,
		};

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
