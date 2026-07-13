using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
    public interface ILanguageService
    {
        Task<CompletionResult> GetCompletionsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken);
        Task<QuickInfo?> GetQuickInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken);
        Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DocumentId documentId, CancellationToken cancellationToken);
        Task<IReadOnlyList<NavigationTarget>> GoToDefinitionAsync(DocumentId documentId, int offset, CancellationToken cancellationToken);
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
        /// </summary>
        Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> RenameSymbolAsync(
            DocumentId documentId, int offset, string newName, CancellationToken cancellationToken);

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
        /// (docs/language-services.md §8). A computed action is short-lived backend-side state
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

    public sealed class DocumentOutlineNode
    {
        public DocumentOutlineNode(
            string name,
            string kind,
            TextSpan span,
            IReadOnlyList<DocumentOutlineNode> children,
            TextSpan? extentSpan = null,
            string? accessibility = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            Span = span;
            Children = children ?? throw new ArgumentNullException(nameof(children));
            ExtentSpan = extentSpan ?? span;
            Accessibility = accessibility;
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
