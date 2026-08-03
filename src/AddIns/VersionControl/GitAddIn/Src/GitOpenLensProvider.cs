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
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.LanguageServices.OpenLens;

namespace ICSharpCode.GitAddIn
{
	/// <summary>
	/// "Author, N days ago" lens (doc/technotes/openlens.md §10.4), attaching to the same
	/// type/method/constructor/property/indexer/event anchors <c>LanguageOpenLensAnchorProvider</c>
	/// already discovers.
	///
	/// Unlike the coverage/test lenses (cheap in-memory lookups, resolved directly in
	/// <see cref="ProvideAsync"/>), blaming a line spawns an external `git` process
	/// (<see cref="GitBlame.GetLastEditAsync"/>) - genuinely expensive, so this follows the same
	/// two-stage shape as the reference/implementation lenses: <see cref="ProvideAsync"/> only
	/// returns a placeholder, the real blame runs in <see cref="ResolveAsync"/>, which the OpenLens
	/// host only calls for anchors in the visible viewport (doc §12.2).
	/// </summary>
	public sealed class GitOpenLensProvider : IOpenLensProvider
	{
		public const string LensId = "git-blame";

		public string Id => "Git";
		public int Order => 4;

		public bool CanHandle(OpenLensDocumentContext context) => Git.IsInWorkingCopy(context.FileName);

		public Task<IReadOnlyList<OpenLensItem>> ProvideAsync(
			OpenLensDocumentContext context, IReadOnlyList<OpenLensAnchor> anchors, CancellationToken cancellationToken)
		{
			var items = new List<OpenLensItem>();
			foreach (var anchor in anchors) {
				if (anchor.Kind is not (OpenLensAnchorKind.Type or OpenLensAnchorKind.Method
					or OpenLensAnchorKind.Constructor or OpenLensAnchorKind.Property or OpenLensAnchorKind.Indexer or OpenLensAnchorKind.Event))
					continue;
				items.Add(new OpenLensItem(
					ProviderId: Id, LensId: LensId, AnchorId: anchor.AnchorId, Order: 4,
					Presentation: new OpenLensPresentation("history"),
					Command: null, ResolveData: anchor, IsResolved: false));
			}
			return Task.FromResult<IReadOnlyList<OpenLensItem>>(items);
		}

		public async Task<OpenLensItem> ResolveAsync(OpenLensDocumentContext context, OpenLensItem item, CancellationToken cancellationToken)
		{
			if (item.ResolveData is not OpenLensAnchor anchor)
				return item;

			var blame = await GitBlame.GetLastEditAsync(context.FileName, anchor.Range.Span.Start.Line, cancellationToken).ConfigureAwait(false);
			if (blame == null)
				return item with { Presentation = new OpenLensPresentation("no history"), IsResolved = true };

			string title = blame.IsUncommitted
				? "uncommitted"
				: blame.Author + ", " + GitBlame.FormatRelativeTime(blame.AuthorTime, DateTimeOffset.Now);
			return item with {
				Presentation = new OpenLensPresentation(title, Tooltip: blame.Summary),
				Command = new OpenLensCommand("OpenLens.RunAction", (Action)(() => GitGuiWrapper.Log(context.FileName, null))),
				IsResolved = true,
			};
		}
	}
}
