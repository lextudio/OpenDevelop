#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.LanguageServices.OpenLens
{
	/// <summary>
	/// Generic <see cref="ILanguageService"/>-backed "N references | M implementations" provider
	/// (the two indicators the Phase 1 <c>OpenLensRenderer</c> prototype hardcoded), scoped to one
	/// file extension the same way as <see cref="LanguageOpenLensAnchorProvider"/>.
	/// </summary>
	public sealed class LanguageOpenLensProvider : IOpenLensProvider
	{
		public const string ReferencesLensId = "references";
		public const string ImplementationsLensId = "implementations";
		public const string OverridesLensId = "overrides";

		readonly string extension;

		// How often one item may come back UNRESOLVED because the service could not answer, before
		// its count is published anyway. Deferring is what lets a genuine answer replace an early
		// miss (see the references branch), but deferring without a bound makes the renderer retry
		// the same reference search on every refresh forever: measured, that alone stretched the
		// AddInTests run from 410s to 1845s and starved unrelated designer tests into their
		// timeouts. A couple of retries is all the workspace needs to finish adopting a freshly
		// opened document into its project.
		const int MaxUnresolvedAttempts = 3;

		// Keyed per document+anchor+lens. Bounded by the anchors of the files actually opened, and
		// entries are dropped as soon as an item resolves.
		readonly Dictionary<string, int> unresolvedAttempts = new(StringComparer.Ordinal);

		public LanguageOpenLensProvider(string id, string extension, int order = 0)
		{
			Id = id ?? throw new ArgumentNullException(nameof(id));
			this.extension = extension ?? throw new ArgumentNullException(nameof(extension));
			Order = order;
		}

		public string Id { get; }
		public int Order { get; }

		public bool CanHandle(OpenLensDocumentContext context) =>
			string.Equals(Path.GetExtension(context.FileName), extension, StringComparison.OrdinalIgnoreCase);

		public Task<IReadOnlyList<OpenLensItem>> ProvideAsync(
			OpenLensDocumentContext context, IReadOnlyList<OpenLensAnchor> anchors, CancellationToken cancellationToken)
		{
			var items = new List<OpenLensItem>(anchors.Count * 2);
			foreach (var anchor in anchors) {
				items.Add(new OpenLensItem(
					ProviderId: Id, LensId: ReferencesLensId, AnchorId: anchor.AnchorId, Order: 0,
					Presentation: new OpenLensPresentation("references"),
					Command: null, ResolveData: anchor, IsResolved: false));

				// doc/technotes/openlens.md §17.3: an interface/abstract member offers
				// "implementations", a virtual member/non-sealed override offers "overrides" -
				// never both, and nothing at all for a non-virtual, non-interface member.
				if (anchor.Overridability == SymbolOverridability.Implementable) {
					items.Add(new OpenLensItem(
						ProviderId: Id, LensId: ImplementationsLensId, AnchorId: anchor.AnchorId, Order: 1,
						Presentation: new OpenLensPresentation("implementations"),
						Command: null, ResolveData: anchor, IsResolved: false));
				} else if (anchor.Overridability == SymbolOverridability.Overridable) {
					items.Add(new OpenLensItem(
						ProviderId: Id, LensId: OverridesLensId, AnchorId: anchor.AnchorId, Order: 1,
						Presentation: new OpenLensPresentation("overrides"),
						Command: null, ResolveData: anchor, IsResolved: false));
				}
			}
			return Task.FromResult<IReadOnlyList<OpenLensItem>>(items);
		}

		static string UnresolvedKey(OpenLensDocumentContext context, OpenLensItem item) =>
			context.FileName + "\u0000" + item.AnchorId + "\u0000" + item.LensId;

		/// <summary>True while this item may still be left unresolved for a later retry.</summary>
		bool ShouldDeferUnresolved(OpenLensDocumentContext context, OpenLensItem item)
		{
			var key = UnresolvedKey(context, item);
			lock (unresolvedAttempts)
			{
				unresolvedAttempts.TryGetValue(key, out var attempts);
				if (attempts >= MaxUnresolvedAttempts)
				{
					unresolvedAttempts.Remove(key);
					return false;
				}
				unresolvedAttempts[key] = attempts + 1;
				return true;
			}
		}

		void ClearUnresolvedAttempts(OpenLensDocumentContext context, OpenLensItem item)
		{
			lock (unresolvedAttempts)
			{
				unresolvedAttempts.Remove(UnresolvedKey(context, item));
			}
		}

		public async Task<OpenLensItem> ResolveAsync(OpenLensDocumentContext context, OpenLensItem item, CancellationToken cancellationToken)
		{
			if (item.ResolveData is not OpenLensAnchor anchor)
				return item;

			var registry = SD.GetService<LanguageServiceRegistry>();
			if (registry == null || !registry.TryGetService(context.FileName, out var languageService))
				return item;

			int offset = context.ResolveOffset(anchor.Range.Span.Start);

			if (item.LensId == ReferencesLensId) {
				var result = await languageService.FindReferencesAsync(context.DocumentId, offset, cancellationToken).ConfigureAwait(false);
				// A null result means the service could not answer - typically no symbol resolved at
				// the offset yet, because the document is not in its project's compilation at this
				// instant (a file opened before its project finished loading is registered loose
				// first and adopted afterwards). Leaving the item UNRESOLVED gets it retried; the
				// previous "?? 0" instead rendered a confident "0 references" and set
				// IsResolved: true, which is cached, so one early miss poisoned the row for the rest
				// of the session. That is why OpenLens showed "0 references" for symbols with
				// obvious callers only in a long batch run, where the workspace is slower to settle.
				if (result == null && ShouldDeferUnresolved(context, item))
					return item;
				int count = result?.References.Count ?? 0;
				ClearUnresolvedAttempts(context, item);
				return item with {
					Presentation = new OpenLensPresentation(FormatCount(count, "reference", "references")),
					Command = new OpenLensCommand("OpenLens.ShowReferences", anchor),
					IsResolved = true,
				};
			}

			if (item.LensId == ImplementationsLensId || item.LensId == OverridesLensId) {
				var result = await languageService.GetDerivedSymbolsAsync(context.DocumentId, offset, cancellationToken).ConfigureAwait(false);
				// Same reasoning as the references branch above: do not cache a count derived from
				// "the service could not answer".
				if (result == null && ShouldDeferUnresolved(context, item))
					return item;
				int count = CountNodes(result?.Nodes);
				ClearUnresolvedAttempts(context, item);
				var (singular, plural) = item.LensId == OverridesLensId
					? ("override", "overrides")
					: ("implementation", "implementations");
				return item with {
					Presentation = new OpenLensPresentation(FormatCount(count, singular, plural)),
					Command = new OpenLensCommand("OpenLens.ShowImplementations", anchor),
					IsResolved = true,
				};
			}

			return item;
		}

		static int CountNodes(IReadOnlyList<SymbolNavigationNode>? nodes)
		{
			if (nodes == null)
				return 0;
			int count = 0;
			foreach (var node in nodes) {
				count++;
				count += CountNodes(node.Children);
			}
			return count;
		}

		static string FormatCount(int count, string singular, string plural) => count == 1 ? $"1 {singular}" : $"{count} {plural}";
	}
}
