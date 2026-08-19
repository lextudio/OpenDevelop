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
				int count = result?.References.Count ?? 0;
				return item with {
					Presentation = new OpenLensPresentation(FormatCount(count, "reference", "references")),
					Command = new OpenLensCommand("OpenLens.ShowReferences", anchor),
					IsResolved = true,
				};
			}

			if (item.LensId == ImplementationsLensId || item.LensId == OverridesLensId) {
				var result = await languageService.GetDerivedSymbolsAsync(context.DocumentId, offset, cancellationToken).ConfigureAwait(false);
				int count = CountNodes(result?.Nodes);
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
