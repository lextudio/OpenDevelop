#nullable enable
using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.LanguageServices.OpenLens
{
    /// <summary>
    /// A span within a document. Line/column (<see cref="TextSpan"/>), not offset+length: an anchor
    /// provider only has what <see cref="ILanguageService.GetDocumentOutlineAsync"/> already gives
    /// it, and has no live document buffer to convert a line/column position into an offset with -
    /// that conversion is the editor host's job (it has the buffer), same as every other
    /// <see cref="ILanguageService"/> DTO that carries a position.
    /// </summary>
    public readonly record struct OpenLensRange(TextSpan Span);

    public enum OpenLensAnchorKind
    {
        File,
        Namespace,
        Type,
        Method,
        Constructor,
        Property,
        Indexer,
        Event,
        Field,
        Test,
        Other
    }

    /// <summary>
    /// A discoverable location a lens can attach to (doc/technotes/codelens.md §7). Contains no
    /// Roslyn or LSP types - <see cref="AnchorId"/> is opaque to every caller except the anchor
    /// provider that produced it, which is free to derive it from a symbol key, a syntax node
    /// identity, or anything else internal to that backend.
    /// </summary>
    public sealed record OpenLensAnchor(
        string AnchorId,
        DocumentId DocumentId,
        OpenLensRange Range,
        OpenLensAnchorKind Kind,
        string? DisplayName,
        string? SymbolKey,
        long DocumentVersion,
        SymbolOverridability Overridability = SymbolOverridability.None);

    public enum OpenLensSeverity
    {
        Normal,
        Info,
        Warning,
        Error
    }

    public sealed record OpenLensPresentation(
        string Title,
        string? Tooltip = null,
        OpenLensSeverity Severity = OpenLensSeverity.Normal);

    public sealed record OpenLensCommand(string CommandId, object? Argument = null);

    /// <summary>
    /// One provider's contribution to one anchor's row (doc §7). <see cref="IsResolved"/> is false
    /// for a cheap placeholder returned from <see cref="IOpenLensProvider.ProvideAsync"/> before
    /// <see cref="IOpenLensProvider.ResolveAsync"/> has filled in <see cref="Presentation"/> and
    /// <see cref="Command"/> - callers must not treat an unresolved item's title as a real count.
    /// </summary>
    public sealed record OpenLensItem(
        string ProviderId,
        string LensId,
        string AnchorId,
        int Order,
        OpenLensPresentation Presentation,
        OpenLensCommand? Command,
        object? ResolveData,
        bool IsResolved);

    /// <summary>
    /// The context a provider or anchor provider is asked to work against - deliberately thin (just
    /// enough to identify the document and call into <see cref="ILanguageService"/>), not a live
    /// editor/WPF reference, so providers stay decoupled from AvalonEdit and from each other
    /// (doc §7, §8).
    ///
    /// <paramref name="ResolveOffset"/> exists because a provider only ever sees line/column
    /// positions (<see cref="OpenLensRange"/>, <see cref="ILanguageService"/> DTOs generally), never
    /// a raw character offset, but the older point-based <see cref="ILanguageService"/> members
    /// (<c>FindReferencesAsync</c>, <c>GetDerivedSymbolsAsync</c>, ...) still take an <c>int
    /// offset</c>. Converting a position to an offset needs the live document buffer, which only
    /// the editor host has - so the host supplies this callback rather than a provider needing a
    /// buffer reference of its own.
    /// </summary>
    public sealed record OpenLensDocumentContext(
        DocumentId DocumentId,
        string FileName,
        long DocumentVersion,
        Func<TextPosition, int> ResolveOffset);
}
