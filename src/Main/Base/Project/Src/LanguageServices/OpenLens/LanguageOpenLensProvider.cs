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

		// How long a document may keep its lens items UNRESOLVED while waiting to join its project.
		//
		// Bounded by TIME, not by a number of attempts. A retry only happens when a render/measure
		// pass finds the item still unresolved, so "give up after N attempts" measures render
		// activity, not elapsed time: three passes can all fire inside the first second, and the
		// count then gets published from a workspace that is not ready yet. That is exactly how a
		// wrong "0 references" used to get cached - permanently, since a resolved item is never
		// recomputed.
		static readonly TimeSpan UnresolvedBudget = TimeSpan.FromSeconds(30);

		// First time each document deferred, so the budget above can be applied. Keyed by file name:
		// the whole document settles at once, so per-item tracking would only multiply the entries.
		readonly Dictionary<string, DateTime> firstDeferralUtc = new(StringComparer.OrdinalIgnoreCase);

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

		/// <summary>
		/// Whether a count computed right now may be published as final, or the item should be left
		/// unresolved for a later pass. Untrustworthy means either the service could not answer at
		/// all, or the document is not yet part of its own project's compilation - a file opened
		/// before its project finished loading is registered against the shared loose ad-hoc project
		/// and adopted afterwards, and a search inside that window legitimately finds nothing while
		/// returning a perfectly valid (non-null) empty result.
		/// </summary>
		/// <summary>
		/// Last few resolutions, for diagnosis. Deducing what a lens saw from the outside does not
		/// work: by the time anything can be asked about it, the workspace has moved on and answers
		/// the same query correctly, which says nothing about the moment the cached count was
		/// computed. Exposed through od.openlens.resolutions.
		/// </summary>
		public readonly record struct ResolutionRecord(
			DateTime WhenUtc, string FileName, string LensId, int Offset, string Subject, int Count, bool ProjectBacked, bool Published);

		static readonly object resolutionLogLock = new();
		static readonly Queue<ResolutionRecord> resolutionLog = new();
		const int ResolutionLogCapacity = 64;

		/// <summary>Anchor discovery outcomes, same ring buffer rationale as the resolutions.</summary>
		public readonly record struct DiscoveryRecord(DateTime WhenUtc, string FileName, int OutlineTypes, int Anchors);

		static readonly Queue<DiscoveryRecord> discoveryLog = new();

		internal static void RecordDiscovery(string fileName, int outlineTypes, int anchors)
		{
			lock (resolutionLogLock)
			{
				discoveryLog.Enqueue(new DiscoveryRecord(DateTime.UtcNow, fileName, outlineTypes, anchors));
				while (discoveryLog.Count > ResolutionLogCapacity)
					discoveryLog.Dequeue();
			}
		}

		public static IReadOnlyList<DiscoveryRecord> GetDiscoveryLog()
		{
			lock (resolutionLogLock)
				return discoveryLog.ToArray();
		}

		internal static void RecordResolution(ResolutionRecord record)
		{
			lock (resolutionLogLock)
			{
				resolutionLog.Enqueue(record);
				while (resolutionLog.Count > ResolutionLogCapacity)
					resolutionLog.Dequeue();
			}
		}

		public static IReadOnlyList<ResolutionRecord> GetResolutionLog()
		{
			lock (resolutionLogLock)
				return resolutionLog.ToArray();
		}

		bool CanPublish(ILanguageService languageService, OpenLensDocumentContext context, bool hasResult)
		{
			var settled = hasResult && IsDocumentInItsProject(languageService, context);
			lock (firstDeferralUtc)
			{
				if (settled)
				{
					firstDeferralUtc.Remove(context.FileName);
					return true;
				}
				if (!firstDeferralUtc.TryGetValue(context.FileName, out var since))
				{
					firstDeferralUtc[context.FileName] = DateTime.UtcNow;
					return false;
				}
				// A file that belongs to no project never settles; the budget is what stops it from
				// deferring for the whole session. Its lenses are then resolved against the loose
				// project, which is the best answer available for such a file.
				if (DateTime.UtcNow - since < UnresolvedBudget)
					return false;
				firstDeferralUtc.Remove(context.FileName);
				return true;
			}
		}

		static bool IsDocumentInItsProject(ILanguageService languageService, OpenLensDocumentContext context)
		{
			// Only the Roslyn backend distinguishes loose from project-backed documents; for any
			// other service there is nothing to wait for.
			if (languageService is not Roslyn.CSharpVBLanguageService roslyn)
				return true;
			return !string.IsNullOrEmpty(roslyn.TryGetProjectDocument(context.FileName)?.Project.FilePath);
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
				var publish = CanPublish(languageService, context, result != null);
				RecordResolution(new ResolutionRecord(
					DateTime.UtcNow, context.FileName, item.LensId, offset,
					result?.Subject ?? "<null result>", result?.References.Count ?? -1,
					IsDocumentInItsProject(languageService, context), publish));
				if (!publish)
					return item;
				int count = result?.References.Count ?? 0;
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
				if (!CanPublish(languageService, context, result != null))
					return item;
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
