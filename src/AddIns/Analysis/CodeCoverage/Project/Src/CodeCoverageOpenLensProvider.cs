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
using ICSharpCode.SharpDevelop.LanguageServices.OpenLens;

namespace ICSharpCode.CodeCoverage
{
	/// <summary>
	/// "N% covered" lens (doc/technotes/openlens.md §10.5), attaching to the same
	/// method/constructor/property anchors <c>LanguageOpenLensAnchorProvider</c> already discovers -
	/// this class contributes an additional lens to those rows, it doesn't discover its own anchors.
	///
	/// Sourced entirely from <see cref="CodeCoverageService.Results"/> (the last run's in-memory
	/// results - no persisted cache, matching how the CodeCoverage pad itself works) and refreshed
	/// via <see cref="CodeCoverageService.ResultsChanged"/>, not on every keystroke (doc §10.5/§13).
	///
	/// Matching an anchor to a <see cref="CodeCoverageMethod"/> is by file + member name only, not by
	/// full symbol signature - overload resolution isn't attempted, so two overloads of the same
	/// name in the same file would be conflated. Good enough for a first cut; doc §17.2 discusses the
	/// same tradeoff for reference counts.
	/// </summary>
	public sealed class CodeCoverageOpenLensProvider : IOpenLensProvider
	{
		public const string LensId = "coverage";

		public string Id => "CodeCoverage";
		public int Order => 2;

		public bool CanHandle(OpenLensDocumentContext context) =>
			context.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
			|| context.FileName.EndsWith(".vb", StringComparison.OrdinalIgnoreCase);

		public Task<IReadOnlyList<OpenLensItem>> ProvideAsync(
			OpenLensDocumentContext context, IReadOnlyList<OpenLensAnchor> anchors, CancellationToken cancellationToken)
		{
			// Cheap: CodeCoverageService.Results is already in memory, no I/O - so this resolves
			// immediately (IsResolved: true) rather than deferring to ResolveAsync, unlike the
			// reference/implementation lenses which need an actual (expensive) backend query.
			var items = new List<OpenLensItem>();
			foreach (var anchor in anchors) {
				if (anchor.Kind is not (OpenLensAnchorKind.Method or OpenLensAnchorKind.Constructor or OpenLensAnchorKind.Property or OpenLensAnchorKind.Indexer))
					continue;
				if (anchor.DisplayName == null)
					continue;

				var coverage = FindCoverage(context.FileName, anchor.DisplayName);
				if (coverage == null)
					continue;

				var (visited, total) = coverage.Value;
				string title = total == 0 ? "not covered" : (visited * 100 / total) + "% covered";
				items.Add(new OpenLensItem(
					ProviderId: Id, LensId: LensId, AnchorId: anchor.AnchorId, Order: 2,
					Presentation: new OpenLensPresentation(title),
					Command: new OpenLensCommand("OpenLens.RunAction", (Action)(() => CodeCoverageService.CodeCoverageHighlighted = true)),
					ResolveData: null, IsResolved: true));
			}
			return Task.FromResult<IReadOnlyList<OpenLensItem>>(items);
		}

		public Task<OpenLensItem> ResolveAsync(OpenLensDocumentContext context, OpenLensItem item, CancellationToken cancellationToken) =>
			Task.FromResult(item);

		static (int Visited, int Total)? FindCoverage(string fileName, string memberName)
		{
			int visited = 0, total = 0;
			bool found = false;
			foreach (var result in CodeCoverageService.Results) {
				foreach (var module in result.Modules) {
					foreach (var method in module.Methods) {
						if (!MatchesMember(method, memberName))
							continue;
						var points = method.GetSequencePoints(fileName);
						if (points.Count == 0)
							continue;
						found = true;
						visited += points.Count(p => p.VisitCount != 0);
						total += points.Count;
					}
				}
			}
			return found ? (visited, total) : null;
		}

		static bool MatchesMember(CodeCoverageMethod method, string memberName)
		{
			if (method.Name == memberName)
				return true;
			// A property anchor's DisplayName is the property's own name ("Foo"), but coverage
			// data is recorded against its compiler-generated get_Foo/set_Foo accessor methods -
			// both accessors count toward the same anchor's coverage.
			if (method.IsProperty && (method.Name == "get_" + memberName || method.Name == "set_" + memberName))
				return true;
			return false;
		}
	}
}
