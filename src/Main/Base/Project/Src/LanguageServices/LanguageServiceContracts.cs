#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
    public interface ILanguageService
    {
        Task UpsertDocumentAsync(DocumentId documentId, string text, CancellationToken cancellationToken);
        Task<CompletionResult> GetCompletionsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken);
        Task<QuickInfo?> GetQuickInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken);
        Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DocumentId documentId, CancellationToken cancellationToken);
        Task<IReadOnlyList<NavigationTarget>> GoToDefinitionAsync(DocumentId documentId, int offset, CancellationToken cancellationToken);
        Task<SymbolReferencesResult?> FindReferencesAsync(DocumentId documentId, int offset, CancellationToken cancellationToken);
        Task<IReadOnlyList<TextEdit>> FormatAsync(DocumentId documentId, TextSpan? span, CancellationToken cancellationToken);
        void OnTextChanged(DocumentId documentId, TextChange change);

        /// <summary>
        /// Two-level type/member outline for the editor's navigation bar (VS's classic
        /// "class dropdown" + "member dropdown"): top-level entries are types declared in the
        /// document, each with its own members as children.
        /// </summary>
        Task<IReadOnlyList<DocumentOutlineNode>> GetDocumentOutlineAsync(DocumentId documentId, CancellationToken cancellationToken);

        /// <summary>
        /// Renames the symbol at <paramref name="offset"/> to <paramref name="newName"/> across
        /// every file that references it, returning the edits per absolute file path (which may
        /// include files other than the one <paramref name="documentId"/> points to) without
        /// applying them — the caller is responsible for applying edits to open editors and/or
        /// disk. Returns an empty map if there's no renameable symbol at that position.
        /// <paramref name="renameOverloads"/>/<paramref name="renameInStrings"/>/
        /// <paramref name="renameInComments"/> mirror Visual Studio's own Rename dialog options
        /// (all default to false, matching those defaults — a text match inside a string literal
        /// or comment isn't something the backend can prove is the same symbol, so it's opt-in).
        /// A backend that has no equivalent concept (e.g. plain LSP `textDocument/rename`, which
        /// has no such options) is free to ignore them.
        /// </summary>
        Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> RenameSymbolAsync(
            DocumentId documentId, int offset, string newName, CancellationToken cancellationToken,
            bool renameOverloads = false, bool renameInStrings = false, bool renameInComments = false);

        /// <summary>
        /// The display name of the symbol at <paramref name="offset"/> (e.g. to pre-fill a Rename
        /// dialog's "old name" field), or <see langword="null"/> if there's no renameable/
        /// nameable symbol there. Deliberately just a name, not a full <see cref="QuickInfo"/> —
        /// callers that need a signature/description should use <see cref="GetQuickInfoAsync"/>.
        /// </summary>
        Task<string?> GetSymbolNameAsync(DocumentId documentId, int offset, CancellationToken cancellationToken);

        /// <summary>
        /// Whether <paramref name="name"/> would be a syntactically valid identifier in the
        /// language <paramref name="documentId"/>'s project is written in (e.g. C# keywords like
        /// <c>class</c> aren't valid unescaped, but <c>@class</c> is) — used for live validation
        /// in a Rename dialog's text box. A backend with no such notion (e.g. LSP, which doesn't
        /// expose grammar-level identifier rules) should fall back to a conservative generic check
        /// rather than always returning <see langword="true"/>.
        /// </summary>
        Task<bool> IsValidIdentifierAsync(DocumentId documentId, string name, CancellationToken cancellationToken);

        /// <summary>
        /// Finds a type member by name across the whole solution rather than at a cursor position
        /// in an already-open document — e.g. for jumping from a test explorer entry (which only
        /// knows "class X, method Y" from the test host, not a file/line) to its declaration.
        /// <paramref name="parameterCount"/> disambiguates overloads when known; pass
        /// <see langword="null"/> to match by name alone (returning every overload's locations).
        /// </summary>
        Task<IReadOnlyList<NavigationTarget>> FindMemberAsync(
            string typeFullName, string methodName, int? parameterCount, CancellationToken cancellationToken);

        /// <summary>
        /// Lists the code actions (quick fixes/refactorings) applicable at <paramref name="span"/>
        /// (externals/OpenDevelop/doc/technotes/language-services.md §8). A computed action is short-lived backend-side state
        /// (a Roslyn <c>CodeAction</c>, or an LSP action that may still need a
        /// <c>codeAction/resolve</c> round trip) — it can't be handed back as plain data, so
        /// <see cref="CodeActionInfo.Id"/> is an opaque token the backend caches against, valid
        /// only until the next call to this method for the same document.
        /// </summary>
        Task<IReadOnlyList<CodeActionInfo>> GetCodeActionsAsync(DocumentId documentId, TextSpan span, CancellationToken cancellationToken);

        /// <summary>
        /// Computes the edits for the action <paramref name="actionId"/> returned by a preceding
        /// <see cref="GetCodeActionsAsync"/> call on the same document, in the same shape
        /// <see cref="RenameSymbolAsync"/> returns (per absolute file path, not yet applied).
        /// Returns an empty map for an unknown/stale id rather than throwing.
        /// </summary>
        Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> ApplyCodeActionAsync(
            DocumentId documentId, string actionId, CancellationToken cancellationToken);

        /// <summary>
        /// Candidate members for Extract Interface at <paramref name="offset"/> (public instance
        /// methods/properties/events on the type there), or <see langword="null"/> if there's no
        /// eligible type at that position. <see cref="ExtractInterfaceMember.Id"/> is an opaque
        /// token, same convention as <see cref="CodeActionInfo.Id"/> - valid only until the next
        /// call to this method for the same document, and only <see cref="ExtractInterfaceAsync"/>
        /// on that same document/offset consumes it.
        /// </summary>
        Task<ExtractInterfaceInfo?> GetExtractInterfaceInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken);

        /// <summary>
        /// Generates the new interface file's source text for the chosen <paramref name="memberIds"/>
        /// (from a preceding <see cref="GetExtractInterfaceInfoAsync"/> call on the same
        /// document/offset) and, if <paramref name="addInterfaceToClass"/>, the edit that adds it to
        /// the original type's base list - in the same "return, don't apply" shape as
        /// <see cref="RenameSymbolAsync"/>. Returns <see langword="null"/> for a stale/unknown
        /// document or member id.
        /// </summary>
        Task<ExtractInterfaceResult?> ExtractInterfaceAsync(
            DocumentId documentId, int offset, string interfaceName, IReadOnlyList<string> memberIds,
            bool addInterfaceToClass, bool includeComments, CancellationToken cancellationToken);

        /// <summary>
        /// Broad classification of the symbol at <paramref name="offset"/> - e.g. for menu/command
        /// enable-conditions that only need "is this a member, a type, or a local?" rather than a
        /// full symbol. Returns <see langword="null"/> if there's no symbol there.
        /// </summary>
        Task<SymbolKindInfo?> GetSymbolKindAsync(DocumentId documentId, int offset, CancellationToken cancellationToken);

        /// <summary>
        /// Symbol-kind classifications (doc/technotes/csharp-vb-binding.md §8.4/§14) for spans the
        /// editor's own lexical/keyword highlighter (AvalonEdit's .xshd) can't tell apart on its own
        /// — e.g. distinguishing a reference type name, a value type name, a method call, and a
        /// field access, all of which lex identically. Callers should filter out any token type the
        /// lexical highlighter already colors (keyword/string/comment/number/operator) rather than
        /// double-painting it.
        /// </summary>
        Task<IReadOnlyList<SemanticToken>> GetSemanticTokensAsync(DocumentId documentId, CancellationToken cancellationToken);

        Task<SymbolHierarchyResult?> GetBaseSymbolsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken);
        Task<SymbolHierarchyResult?> GetDerivedSymbolsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken);
        Task<string?> GetHelpKeywordAsync(DocumentId documentId, int offset, CancellationToken cancellationToken);
        Task<string?> GetContainingTypeNameAsync(DocumentId documentId, int offset, CancellationToken cancellationToken);
        Task RefreshProjectAsync(DocumentId documentId, CancellationToken cancellationToken);
    }

    public sealed class SymbolReferencesResult
    {
        public SymbolReferencesResult(string subject, IReadOnlyList<NavigationTarget> references)
        {
            Subject = subject ?? throw new ArgumentNullException(nameof(subject));
            References = references ?? throw new ArgumentNullException(nameof(references));
        }

        public string Subject { get; }
        public IReadOnlyList<NavigationTarget> References { get; }
    }

    public sealed class SymbolHierarchyResult
    {
        public SymbolHierarchyResult(string subject, IReadOnlyList<SymbolNavigationNode> nodes)
        {
            Subject = subject ?? throw new ArgumentNullException(nameof(subject));
            Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
        }

        public string Subject { get; }
        public IReadOnlyList<SymbolNavigationNode> Nodes { get; }
    }

    public sealed class SymbolNavigationNode
    {
        public SymbolNavigationNode(string name, string kind, NavigationTarget target, IReadOnlyList<SymbolNavigationNode>? children = null, string? container = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Children = children ?? Array.Empty<SymbolNavigationNode>();
            Container = container;
        }

        public string Name { get; }
        public string Kind { get; }
        public NavigationTarget Target { get; }
        public IReadOnlyList<SymbolNavigationNode> Children { get; }
        public string? Container { get; }
    }

    public sealed class CodeActionInfo
    {
        public CodeActionInfo(string id, string title, bool isPreferred = false)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Title = title ?? throw new ArgumentNullException(nameof(title));
            IsPreferred = isPreferred;
        }

        public string Id { get; }
        public string Title { get; }

        /// <summary>Maps to Roslyn's CodeAction priority / LSP's CodeAction.isPreferred.</summary>
        public bool IsPreferred { get; }

        public override string ToString() => Title;
    }

    public sealed class ExtractInterfaceMember
    {
        public ExtractInterfaceMember(string id, string displayText)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            DisplayText = displayText ?? throw new ArgumentNullException(nameof(displayText));
        }

        public string Id { get; }
        public string DisplayText { get; }

        public override string ToString() => DisplayText;
    }

    public sealed class ExtractInterfaceInfo
    {
        public ExtractInterfaceInfo(string typeName, IReadOnlyList<ExtractInterfaceMember> members)
        {
            TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
            Members = members ?? throw new ArgumentNullException(nameof(members));
        }

        public string TypeName { get; }
        public IReadOnlyList<ExtractInterfaceMember> Members { get; }
    }

    public sealed class ExtractInterfaceResult
    {
        public ExtractInterfaceResult(string interfaceFileContent, IReadOnlyDictionary<string, IReadOnlyList<TextEdit>> edits)
        {
            InterfaceFileContent = interfaceFileContent ?? throw new ArgumentNullException(nameof(interfaceFileContent));
            Edits = edits ?? throw new ArgumentNullException(nameof(edits));
        }

        /// <summary>Source text for the new interface file - not yet written to disk.</summary>
        public string InterfaceFileContent { get; }

        /// <summary>Edits to existing files (e.g. adding the interface to the class's base list),
        /// per absolute file path, not yet applied - same shape as <see cref="ILanguageService.RenameSymbolAsync"/>.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<TextEdit>> Edits { get; }
    }

    public sealed class SymbolKindInfo
    {
        public SymbolKindInfo(bool isMember, bool isType, bool isNamespace, bool isLocal, bool hasSourceLocation)
        {
            IsMember = isMember;
            IsType = isType;
            IsNamespace = isNamespace;
            IsLocal = isLocal;
            HasSourceLocation = hasSourceLocation;
        }

        /// <summary>Method, field, property, or event.</summary>
        public bool IsMember { get; }
        public bool IsType { get; }
        public bool IsNamespace { get; }

        /// <summary>Local variable or parameter.</summary>
        public bool IsLocal { get; }

        /// <summary>Whether the symbol has at least one declaration in source (vs. only in a
        /// referenced assembly) - e.g. to gate a "project only" menu condition.</summary>
        public bool HasSourceLocation { get; }
    }

    public sealed class DocumentId : IEquatable<DocumentId>
    {
        public DocumentId(string fileName)
        {
            FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        }

        public string FileName { get; }

        public bool Equals(DocumentId? other) =>
            other is not null && StringComparer.OrdinalIgnoreCase.Equals(FileName, other.FileName);

        public override bool Equals(object? obj) => Equals(obj as DocumentId);

        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(FileName);

        public override string ToString() => FileName;
    }

    public sealed class CompletionResult
    {
        public static CompletionResult Empty { get; } = new(Array.Empty<CompletionItem>(), null);

        public CompletionResult(IReadOnlyList<CompletionItem> items, TextSpan? replacementSpan)
        {
            Items = items ?? throw new ArgumentNullException(nameof(items));
            ReplacementSpan = replacementSpan;
        }

        public IReadOnlyList<CompletionItem> Items { get; }
        public TextSpan? ReplacementSpan { get; }
    }

    public sealed class CompletionItem
    {
        public CompletionItem(string displayText, string? insertionText = null, string? description = null, string? glyph = null)
        {
            DisplayText = displayText ?? throw new ArgumentNullException(nameof(displayText));
            InsertionText = insertionText ?? displayText;
            Description = description;
            Glyph = glyph;
        }

        public string DisplayText { get; }
        public string InsertionText { get; }
        public string? Description { get; }
        public string? Glyph { get; }
    }

    public sealed class QuickInfo
    {
        public QuickInfo(string text, TextSpan? span = null)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            Span = span;
        }

        public string Text { get; }
        public TextSpan? Span { get; }
    }

    public sealed class LanguageDiagnostic
    {
        public LanguageDiagnostic(string id, string message, DiagnosticSeverity severity, TextSpan span)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Severity = severity;
            Span = span;
        }

        public string Id { get; }
        public string Message { get; }
        public DiagnosticSeverity Severity { get; }
        public TextSpan Span { get; }
    }

    public enum DiagnosticSeverity
    {
        Hidden,
        Info,
        Warning,
        Error
    }

    public sealed class NavigationTarget
    {
        public NavigationTarget(string fileName, TextPosition position, TextSpan? span = null)
        {
            FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            Position = position;
            Span = span;
        }

        public string FileName { get; }
        public TextPosition Position { get; }
        public TextSpan? Span { get; }
    }

    public sealed class TextEdit
    {
        public TextEdit(TextSpan span, string newText)
        {
            Span = span;
            NewText = newText ?? throw new ArgumentNullException(nameof(newText));
        }

        public TextSpan Span { get; }
        public string NewText { get; }
    }

    public sealed class SemanticToken
    {
        public SemanticToken(TextSpan span, string type)
        {
            Span = span;
            Type = type ?? throw new ArgumentNullException(nameof(type));
        }

        public TextSpan Span { get; }
        public string Type { get; }
    }

    /// <summary>
    /// Whether further declarations could extend this one - doc/technotes/openlens.md §17.3's
    /// "implementations vs overrides" table, generalized beyond OpenLens since it's really a
    /// property of the symbol, not of any one feature. A backend that can't classify this (e.g.
    /// plain LSP `textDocument/documentSymbol`, which carries no modifier info) reports
    /// <see cref="None"/> rather than guessing - see doc §17.4 "do not claim implementation
    /// support when the server lacks the capability".
    /// </summary>
    public enum SymbolOverridability
    {
        /// <summary>Not further extensible (non-virtual, non-interface member; sealed override;
        /// or unknown/unclassified by this backend) - no second lens should be shown.</summary>
        None,

        /// <summary>An interface, or an abstract/interface member - "N implementations".</summary>
        Implementable,

        /// <summary>A virtual (non-abstract) member, or a non-sealed override - "N overrides".</summary>
        Overridable
    }

    public sealed class DocumentOutlineNode
    {
        public DocumentOutlineNode(
            string name,
            string kind,
            TextSpan span,
            IReadOnlyList<DocumentOutlineNode> children,
            TextSpan? extentSpan = null,
            string? accessibility = null,
            SymbolOverridability overridability = SymbolOverridability.None)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            Span = span;
            Children = children ?? throw new ArgumentNullException(nameof(children));
            ExtentSpan = extentSpan ?? span;
            Accessibility = accessibility;
            Overridability = overridability;
        }

        public string Name { get; }
        public string Kind { get; }

        /// <summary>Navigation-target span (e.g. just the name token) — where a click jumps to.</summary>
        public TextSpan Span { get; }

        /// <summary>
        /// Full declaration span (e.g. the whole type/method body), used to test whether the
        /// caret currently sits "inside" this node for nav-bar auto-selection. Defaults to
        /// <see cref="Span"/> when a backend doesn't report a wider extent.
        /// </summary>
        public TextSpan ExtentSpan { get; }

        /// <summary>
        /// "Public"/"Private"/"Protected"/"Internal" (or <see langword="null"/> if unknown/not
        /// reported), for the nav-bar's modifier icon overlay. LSP's `textDocument/documentSymbol`
        /// has no accessibility field, so <see cref="Accessibility"/> is always
        /// <see langword="null"/> for that backend.
        /// </summary>
        public string? Accessibility { get; }

        /// <summary>See <see cref="SymbolOverridability"/>. Defaults to <see cref="SymbolOverridability.None"/>.</summary>
        public SymbolOverridability Overridability { get; }

        public IReadOnlyList<DocumentOutlineNode> Children { get; }

        public override string ToString() => Name;
    }

    public sealed class TextChange
    {
        public TextChange(TextSpan span, string newText)
        {
            Span = span;
            NewText = newText ?? throw new ArgumentNullException(nameof(newText));
        }

        public TextSpan Span { get; }
        public string NewText { get; }
    }

    public readonly struct TextSpan : IEquatable<TextSpan>
    {
        public TextSpan(TextPosition start, TextPosition end)
        {
            Start = start;
            End = end;
        }

        public TextPosition Start { get; }
        public TextPosition End { get; }

        public bool Equals(TextSpan other) => Start.Equals(other.Start) && End.Equals(other.End);
        public override bool Equals(object? obj) => obj is TextSpan other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Start, End);
    }

    public readonly struct TextPosition : IEquatable<TextPosition>
    {
        public TextPosition(int line, int column)
        {
            if (line < 1)
                throw new ArgumentOutOfRangeException(nameof(line), "Line numbers are one-based.");
            if (column < 1)
                throw new ArgumentOutOfRangeException(nameof(column), "Column numbers are one-based.");

            Line = line;
            Column = column;
        }

        public int Line { get; }
        public int Column { get; }

        public bool Equals(TextPosition other) => Line == other.Line && Column == other.Column;
        public override bool Equals(object? obj) => obj is TextPosition other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Line, Column);
        public override string ToString() => Line + ":" + Column;
    }
}
