#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.LanguageServices.OpenLens
{
    /// <summary>
    /// Contributes lenses to anchors already discovered by an <see cref="IOpenLensAnchorProvider"/>
    /// (doc/technotes/codelens.md §8.1). Owned by whichever AddIn owns the underlying capability
    /// (CSharpBinding/VBBinding for references and hierarchy, UnitTesting for test status, Git for
    /// history, CodeCoverage for coverage) - the OpenLens host must not know how any of these are
    /// calculated, only how to compose and render what providers return.
    /// </summary>
    public interface IOpenLensProvider
    {
        /// <summary>Stable identifier, used as <see cref="OpenLensItem.ProviderId"/> and for
        /// targeted refresh (<see cref="OpenLensRefreshEventArgs.ProviderId"/>).</summary>
        string Id { get; }

        /// <summary>Left-to-right ordering among providers contributing to the same anchor.</summary>
        int Order { get; }

        bool CanHandle(OpenLensDocumentContext context);

        /// <summary>
        /// Cheap: decides which of the given anchors this provider wants to contribute a lens for,
        /// and returns unresolved (or, if already cheaply known, resolved) items. Must not perform
        /// the provider's expensive work itself - that's <see cref="ResolveAsync"/>'s job.
        /// </summary>
        Task<IReadOnlyList<OpenLensItem>> ProvideAsync(
            OpenLensDocumentContext context,
            IReadOnlyList<OpenLensAnchor> anchors,
            CancellationToken cancellationToken);

        /// <summary>
        /// Expensive: computes the final <see cref="OpenLensItem.Presentation"/> and
        /// <see cref="OpenLensItem.Command"/> for one previously-provided item. Only called for
        /// items the OpenLens host has actually decided to resolve (visible + prefetch window,
        /// doc §12.2) - never for every anchor in a document up front.
        /// </summary>
        Task<OpenLensItem> ResolveAsync(
            OpenLensDocumentContext context,
            OpenLensItem item,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Discovers the anchors in a document eligible to receive lenses at all (doc §8.2) - separate
    /// from <see cref="IOpenLensProvider"/> because anchor discovery is language-sensitive
    /// (CSharpBinding/VBBinding own it) while lens contribution is not (Git/tests/coverage attach to
    /// whatever anchors the language owner already found).
    /// </summary>
    public interface IOpenLensAnchorProvider
    {
        string Id { get; }

        bool CanHandle(OpenLensDocumentContext context);

        /// <summary>
        /// <paramref name="requestedRange"/> is an optional hint (e.g. "just the visible viewport")
        /// - an implementation that can only discover anchors for the whole document may ignore it
        /// and return everything; the OpenLens host is responsible for narrowing to what it actually
        /// resolves.
        /// </summary>
        Task<IReadOnlyList<OpenLensAnchor>> GetAnchorsAsync(
            OpenLensDocumentContext context,
            OpenLensRange? requestedRange,
            CancellationToken cancellationToken);
    }
}
