using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using StreamJsonRpc;

namespace ICSharpCode.SharpDevelop.LanguageServices.Lsp
{
    /// <summary>
    /// Generic LSP client backend (docs/language-services.md §3, slices 5-7). One instance
    /// owns one child server process for one project language, launched lazily on first use.
    /// Completion, diagnostics, hover, go-to-definition, and formatting (whole-document via
    /// <c>textDocument/formatting</c>, or ranged via <c>textDocument/rangeFormatting</c> when
    /// a span is requested) are all wired.
    ///
    /// Document sync uses full-text replace on every edit rather than incremental
    /// <c>TextDocumentContentChangeEvent</c> ranges — simpler, and matches how
    /// <see cref="MainPage"/> already re-syncs the whole buffer before every feature call.
    /// </summary>
    public sealed class LspLanguageService : ILanguageService, IAsyncDisposable
    {
        const string UnknownDiagnosticId = "LSP";
		static readonly string[] SupportedSemanticTokenTypes = {
			"namespace", "type", "class", "enum", "interface", "struct", "typeParameter",
			"parameter", "variable", "property", "enumMember", "event", "function", "method",
			"macro", "keyword", "modifier", "comment", "string", "number", "regexp", "operator",
			"decorator", "module"
		};

        readonly LspServerLaunchSpec _spec;
        readonly string _rootUri;
        readonly SemaphoreSlim _startGate = new(1, 1);
        readonly Dictionary<string, OpenDocument> _openDocuments = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, IReadOnlyList<LanguageDiagnostic>> _diagnosticsByUri =
            new(StringComparer.OrdinalIgnoreCase);

        // Last computed code-action list per document (docs/language-services.md §8), keyed by
        // the opaque CodeActionInfo.Id GetCodeActionsAsync handed out — a fresh
        // GetCodeActionsAsync call for a document replaces its entry, so an id from a
        // superseded list is simply absent (ApplyCodeActionAsync treats that as "no edits"
        // rather than throwing).
        readonly Dictionary<string, Dictionary<string, JsonElement>> _pendingCodeActionsByUri =
            new(StringComparer.OrdinalIgnoreCase);

        Process? _process;
        JsonRpc? _rpc;
        bool _unavailable;
		string[] _semanticTokenTypes = Array.Empty<string>();

        public LspLanguageService(LspServerLaunchSpec spec, string rootUri)
        {
            _spec = spec ?? throw new ArgumentNullException(nameof(spec));
            _rootUri = rootUri ?? throw new ArgumentNullException(nameof(rootUri));
        }

        public async ValueTask DisposeAsync()
        {
            if (_rpc is not null)
            {
                try
                {
                    await _rpc.InvokeAsync("shutdown");
                    await _rpc.NotifyAsync("exit");
                }
                catch
                {
                    // Best-effort shutdown; the process is killed below regardless.
                }

                _rpc.Dispose();
            }

            if (_process is { HasExited: false })
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Process may have already exited.
                }
            }

            _process?.Dispose();
            _startGate.Dispose();
        }

        public async Task UpsertDocumentAsync(DocumentId documentId, string text, CancellationToken cancellationToken)
        {
            if (documentId is null)
                throw new ArgumentNullException(nameof(documentId));
            if (text is null)
                throw new ArgumentNullException(nameof(text));

            if (!await EnsureStartedAsync(cancellationToken))
                return;

            var uri = ToUri(documentId.FileName);
            if (_openDocuments.TryGetValue(uri, out var open))
            {
                open.Version++;
                open.Text = text;
                await _rpc!.NotifyWithParameterObjectAsync("textDocument/didChange", new
                {
                    textDocument = new { uri, version = open.Version },
                    contentChanges = new[] { new { text } }
                });
                return;
            }

            open = new OpenDocument { Version = 1, Text = text };
            _openDocuments[uri] = open;
            await _rpc!.NotifyWithParameterObjectAsync("textDocument/didOpen", new
            {
                textDocument = new { uri, languageId = _spec.LanguageId, version = open.Version, text }
            });
        }

        public async Task<CompletionResult> GetCompletionsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var uri = ToUri(documentId.FileName);
            if (_unavailable || _rpc is null || !_openDocuments.TryGetValue(uri, out var open))
                return CompletionResult.Empty;

            var position = ToLspPosition(open.Text, offset);
            JsonElement result;
            try
            {
                result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                    "textDocument/completion",
                    new { textDocument = new { uri }, position },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException)
            {
                return CompletionResult.Empty;
            }

            if (!TryGetItemsArray(result, out var items))
                return CompletionResult.Empty;

            var completionItems = items
                .EnumerateArray()
                .Select(ConvertCompletionItem)
                .ToArray();
            return new CompletionResult(completionItems, null);
        }

        public async Task<QuickInfo?> GetQuickInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var uri = ToUri(documentId.FileName);
            if (_unavailable || _rpc is null || !_openDocuments.TryGetValue(uri, out var open))
                return null;

            var position = ToLspPosition(open.Text, offset);
            JsonElement result;
            try
            {
                result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                    "textDocument/hover",
                    new { textDocument = new { uri }, position },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException)
            {
                return null;
            }

            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("contents", out var contents))
                return null;

            var text = ConvertHoverContents(contents);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var span = result.TryGetProperty("range", out var rangeProperty) && rangeProperty.ValueKind == JsonValueKind.Object
                ? ConvertRange(rangeProperty)
                : (TextSpan?)null;
            return new QuickInfo(text, span);
        }

        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DocumentId documentId, CancellationToken cancellationToken)
        {
            var uri = ToUri(documentId.FileName);
            IReadOnlyList<LanguageDiagnostic> diagnostics = _diagnosticsByUri.TryGetValue(uri, out var cached)
                ? cached
                : Array.Empty<LanguageDiagnostic>();
            return Task.FromResult(diagnostics);
        }

		public async Task<IReadOnlyList<SemanticToken>> GetSemanticTokensAsync(DocumentId documentId, CancellationToken cancellationToken)
		{
			var uri = ToUri(documentId.FileName);
			if (_unavailable || _rpc is null || !_openDocuments.TryGetValue(uri, out var open)
			    || _semanticTokenTypes.Length == 0)
				return Array.Empty<SemanticToken>();

			JsonElement result;
			try {
				result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
					"textDocument/semanticTokens/full", new { textDocument = new { uri } }, cancellationToken);
			} catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException) {
				return Array.Empty<SemanticToken>();
			}

			if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("data", out var data)
			    || data.ValueKind != JsonValueKind.Array)
				return Array.Empty<SemanticToken>();

			var values = data.EnumerateArray().Select(value => value.GetInt32()).ToArray();
			var tokens = new List<SemanticToken>();
			var line = 0;
			var character = 0;
			for (var i = 0; i + 4 < values.Length; i += 5) {
				line += values[i];
				character = values[i] == 0 ? character + values[i + 1] : values[i + 1];
				var length = values[i + 2];
				var typeIndex = values[i + 3];
				if (typeIndex < 0 || typeIndex >= _semanticTokenTypes.Length)
					continue;
				var start = GetOffset(open.Text, line, character);
				var end = Math.Min(open.Text.Length, start + length);
				tokens.Add(new SemanticToken(new TextSpan(ToTextPosition(open.Text, start), ToTextPosition(open.Text, end)), _semanticTokenTypes[typeIndex]));
			}
			return tokens;
		}

        public async Task<IReadOnlyList<NavigationTarget>> GoToDefinitionAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var uri = ToUri(documentId.FileName);
            if (_unavailable || _rpc is null || !_openDocuments.TryGetValue(uri, out var open))
                return Array.Empty<NavigationTarget>();

            var position = ToLspPosition(open.Text, offset);
            JsonElement result;
            try
            {
                result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                    "textDocument/definition",
                    new { textDocument = new { uri }, position },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException)
            {
                return Array.Empty<NavigationTarget>();
            }

            return result.ValueKind switch
            {
                JsonValueKind.Array => result.EnumerateArray().Select(ConvertDefinitionResult).ToArray(),
                JsonValueKind.Object => new[] { ConvertDefinitionResult(result) },
                _ => Array.Empty<NavigationTarget>()
            };
        }

        public async Task<IReadOnlyList<TextEdit>> FormatAsync(DocumentId documentId, TextSpan? span, CancellationToken cancellationToken)
        {
            var uri = ToUri(documentId.FileName);
            if (_unavailable || _rpc is null || !_openDocuments.TryGetValue(uri, out var open))
                return Array.Empty<TextEdit>();

            var options = new { tabSize = 4, insertSpaces = true };
            JsonElement result;
            try
            {
                result = span is { } requestedSpan
                    ? await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                        "textDocument/rangeFormatting",
                        new { textDocument = new { uri }, range = ToLspRange(requestedSpan), options },
                        cancellationToken)
                    : await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                        "textDocument/formatting",
                        new { textDocument = new { uri }, options },
                        cancellationToken);
            }
            catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException)
            {
                return Array.Empty<TextEdit>();
            }

            if (result.ValueKind != JsonValueKind.Array)
                return Array.Empty<TextEdit>();

            return result.EnumerateArray().Select(ConvertLspTextEdit).ToArray();
        }

        public async Task<IReadOnlyList<DocumentOutlineNode>> GetDocumentOutlineAsync(DocumentId documentId, CancellationToken cancellationToken)
        {
            var uri = ToUri(documentId.FileName);
            if (_unavailable || _rpc is null || !_openDocuments.ContainsKey(uri))
                return Array.Empty<DocumentOutlineNode>();

            JsonElement result;
            try
            {
                result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                    "textDocument/documentSymbol",
                    new { textDocument = new { uri } },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException)
            {
                return Array.Empty<DocumentOutlineNode>();
            }

            if (result.ValueKind != JsonValueKind.Array)
                return Array.Empty<DocumentOutlineNode>();

            // Only top-level types get a node here; a DocumentSymbol's own `children` become
            // that node's outline children (namespaces/modules are flattened by recursing).
            var nodes = new List<DocumentOutlineNode>();
            foreach (var token in result.EnumerateArray())
                CollectDocumentSymbols(token, nodes);
            return nodes;
        }

        public async Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> RenameSymbolAsync(
            DocumentId documentId, int offset, string newName, CancellationToken cancellationToken)
        {
            var noEdits = new Dictionary<string, IReadOnlyList<TextEdit>>();
            if (string.IsNullOrWhiteSpace(newName))
                return noEdits;

            var uri = ToUri(documentId.FileName);
            if (_unavailable || _rpc is null || !_openDocuments.TryGetValue(uri, out var open))
                return noEdits;

            var position = ToLspPosition(open.Text, offset);
            JsonElement result;
            try
            {
                result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                    "textDocument/rename",
                    new { textDocument = new { uri }, position, newName },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException)
            {
                return noEdits;
            }

            return ParseWorkspaceEditChanges(result);
        }

        /// <summary>
        /// Parses a WorkspaceEdit's simpler shape: `changes: { [uri]: TextEdit[] }`. The
        /// `documentChanges` (versioned) shape isn't handled — no LSP server in
        /// `LspServerRegistry.CreateDefault()` requires it today (§4 slice 6). Shared by
        /// <see cref="RenameSymbolAsync"/> and <see cref="ApplyCodeActionAsync"/> (§8.2) so
        /// there's exactly one WorkspaceEdit parser rather than two copies that could drift.
        /// </summary>
        static IReadOnlyDictionary<string, IReadOnlyList<TextEdit>> ParseWorkspaceEditChanges(JsonElement workspaceEdit)
        {
            var editsByFile = new Dictionary<string, IReadOnlyList<TextEdit>>(StringComparer.OrdinalIgnoreCase);
            if (workspaceEdit.ValueKind != JsonValueKind.Object
                || !workspaceEdit.TryGetProperty("changes", out var changes)
                || changes.ValueKind != JsonValueKind.Object)
            {
                return editsByFile;
            }

            foreach (var change in changes.EnumerateObject())
            {
                if (change.Value.ValueKind != JsonValueKind.Array)
                    continue;

                var textEdits = change.Value.EnumerateArray().Select(ConvertLspTextEdit).ToArray();
                if (textEdits.Length > 0)
                    editsByFile[FromUri(change.Name)] = textEdits;
            }

            return editsByFile;
        }

        public async Task<IReadOnlyList<CodeActionInfo>> GetCodeActionsAsync(DocumentId documentId, TextSpan span, CancellationToken cancellationToken)
        {
            var uri = ToUri(documentId.FileName);
            if (_unavailable || _rpc is null || !_openDocuments.ContainsKey(uri))
                return Array.Empty<CodeActionInfo>();

            JsonElement result;
            try
            {
                result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                    "textDocument/codeAction",
                    new
                    {
                        textDocument = new { uri },
                        range = ToLspRange(span),
                        context = new { diagnostics = Array.Empty<object>() },
                    },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException)
            {
                return Array.Empty<CodeActionInfo>();
            }

            if (result.ValueKind != JsonValueKind.Array)
                return Array.Empty<CodeActionInfo>();

            // Slice 1 (docs/language-services.md §8.2) only supports actions that already carry
            // a literal `edit` in the response - command-only actions and ones needing a
            // codeAction/resolve round trip first are skipped.
            var pending = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var actions = new List<CodeActionInfo>();
            var index = 0;
            foreach (var item in result.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("edit", out _))
                    continue;
                if (!item.TryGetProperty("title", out var titleProperty) || titleProperty.ValueKind != JsonValueKind.String)
                    continue;

                var id = index++.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var isPreferred = item.TryGetProperty("isPreferred", out var preferredProperty)
                    && preferredProperty.ValueKind == JsonValueKind.True;

                pending[id] = item;
                actions.Add(new CodeActionInfo(id, titleProperty.GetString() ?? string.Empty, isPreferred));
            }

            _pendingCodeActionsByUri[uri] = pending;
            return actions;
        }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> ApplyCodeActionAsync(
            DocumentId documentId, string actionId, CancellationToken cancellationToken)
        {
            var uri = ToUri(documentId.FileName);
            var noEdits = new Dictionary<string, IReadOnlyList<TextEdit>>();
            if (!_pendingCodeActionsByUri.TryGetValue(uri, out var pending)
                || !pending.TryGetValue(actionId, out var action)
                || !action.TryGetProperty("edit", out var edit))
            {
                return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>(noEdits);
            }

            return Task.FromResult(ParseWorkspaceEditChanges(edit));
        }

        public void OnTextChanged(DocumentId documentId, TextChange change)
        {
            // No caller currently invokes this on ILanguageService (MainPage re-syncs the
            // whole buffer via UpsertDocumentAsync before every feature call instead).
        }

        public Task<IReadOnlyList<NavigationTarget>> FindMemberAsync(
            string typeFullName, string methodName, int? parameterCount, CancellationToken cancellationToken)
        {
            // Solution-wide symbol-by-name lookup (as opposed to `workspace/symbol`, an LSP
            // request no server in LspServerRegistry.CreateDefault() is asked to fulfil today -
            // this backend only serves TS/JS/Python, not the C#/VB test-explorer navigation use).
            return Task.FromResult<IReadOnlyList<NavigationTarget>>(Array.Empty<NavigationTarget>());
        }

        async Task<bool> EnsureStartedAsync(CancellationToken cancellationToken)
        {
            if (_unavailable)
                return false;
            if (_rpc is not null)
                return true;

            await _startGate.WaitAsync(cancellationToken);
            try
            {
                if (_unavailable)
                    return false;
                if (_rpc is not null)
                    return true;

                var startInfo = new ProcessStartInfo(_spec.Command)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (!string.IsNullOrEmpty(_spec.WorkingDirectory))
                    startInfo.WorkingDirectory = _spec.WorkingDirectory;

                foreach (var argument in _spec.Arguments)
                    startInfo.ArgumentList.Add(argument);

                Process process;
                try
                {
                    process = Process.Start(startInfo)
                        ?? throw new InvalidOperationException($"Failed to start '{_spec.Command}'.");
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"LSP server '{_spec.Command}' unavailable ({ex.Message}); falling back to lexical-only highlighting for {_spec.LanguageId}.");
                    _unavailable = true;
                    return false;
                }

                _process = process;
                var formatter = new SystemTextJsonFormatter();
                var handler = new HeaderDelimitedMessageHandler(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, formatter);
                var rpc = new JsonRpc(handler);
                rpc.AddLocalRpcMethod("textDocument/publishDiagnostics", new Action<JsonElement>(OnPublishDiagnostics));
                rpc.StartListening();
                _rpc = rpc;

				var initializeResult = await rpc.InvokeWithParameterObjectAsync<JsonElement>("initialize", new
                {
                    processId = Environment.ProcessId,
                    rootUri = _rootUri,
                    capabilities = new
                    {
						textDocument = new
						{
							semanticTokens = new { requests = new { full = true }, tokenTypes = SupportedSemanticTokenTypes, tokenModifiers = Array.Empty<string>(), formats = new[] { "relative" } },
                            // Declares support for literal-edit code actions (docs/language-
                            // services.md §8.2) — codeActionKind.valueSet left empty (any kind
                            // is fine) since GetCodeActionsAsync doesn't filter by kind today.
                            codeAction = new { codeActionLiteralSupport = new { codeActionKind = new { valueSet = Array.Empty<string>() } } },
                        },
                    },
				}, cancellationToken);
				_semanticTokenTypes = ReadSemanticTokenTypes(initializeResult);
                await rpc.NotifyAsync("initialized");

                return true;
            }
            finally
            {
                _startGate.Release();
            }
        }

        void OnPublishDiagnostics(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("uri", out var uriProperty))
                return;

            var uri = uriProperty.GetString();
            if (string.IsNullOrEmpty(uri))
                return;

            var diagnostics = parameters.TryGetProperty("diagnostics", out var diagnosticsProperty)
                && diagnosticsProperty.ValueKind == JsonValueKind.Array
                ? diagnosticsProperty.EnumerateArray().Select(ConvertDiagnostic).ToArray()
                : Array.Empty<LanguageDiagnostic>();
            _diagnosticsByUri[uri] = diagnostics;
        }

        static void CollectDocumentSymbols(JsonElement token, List<DocumentOutlineNode> results)
        {
            var kindNumber = token.TryGetProperty("kind", out var kindProperty) && kindProperty.ValueKind == JsonValueKind.Number
                ? kindProperty.GetInt32()
                : 0;
            var hasChildren = token.TryGetProperty("children", out var childrenToken) && childrenToken.ValueKind == JsonValueKind.Array;

            if (IsContainerSymbolKind(kindNumber))
            {
                // Namespace/module/package/file: not a type itself, recurse to find the types inside.
                if (hasChildren)
                {
                    foreach (var child in childrenToken.EnumerateArray())
                        CollectDocumentSymbols(child, results);
                }
                return;
            }

            var name = token.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() ?? string.Empty : string.Empty;
            var kind = ConvertSymbolKind(kindNumber);
            var span = GetDocumentSymbolSpan(token);

            var extentSpan = GetDocumentSymbolExtentSpan(token);
            if (!IsTypeSymbolKind(kindNumber))
            {
                // A top-level member with no enclosing type (e.g. a free function) — surface it
                // directly rather than dropping it.
                results.Add(new DocumentOutlineNode(name, kind, span, Array.Empty<DocumentOutlineNode>(), extentSpan));
                return;
            }

            var members = new List<DocumentOutlineNode>();
            if (hasChildren)
            {
                foreach (var child in childrenToken.EnumerateArray())
                {
                    var childKind = child.TryGetProperty("kind", out var childKindProperty) && childKindProperty.ValueKind == JsonValueKind.Number
                        ? childKindProperty.GetInt32()
                        : 0;
                    if (IsTypeSymbolKind(childKind))
                    {
                        // Nested types become their own top-level entries (flat list), matching
                        // the Roslyn backend's outline shape.
                        CollectDocumentSymbols(child, results);
                    }
                    else
                    {
                        var memberName = child.TryGetProperty("name", out var childNameProperty) ? childNameProperty.GetString() ?? string.Empty : string.Empty;
                        members.Add(new DocumentOutlineNode(
                            memberName, ConvertSymbolKind(childKind), GetDocumentSymbolSpan(child), Array.Empty<DocumentOutlineNode>(),
                            GetDocumentSymbolExtentSpan(child)));
                    }
                }
            }

            results.Add(new DocumentOutlineNode(
                name, kind, span, members.OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase).ToArray(), extentSpan));
        }

        static TextSpan GetDocumentSymbolSpan(JsonElement token)
        {
            // DocumentSymbol has selectionRange/range; SymbolInformation has location.range.
            // Prefer selectionRange (just the name token) — this is the "jump to declaration"
            // navigation target, not the full extent (see GetDocumentSymbolExtentSpan for that).
            var range = token.TryGetProperty("selectionRange", out var selectionRange) ? selectionRange
                : token.TryGetProperty("range", out var plainRange) ? plainRange
                : token.TryGetProperty("location", out var location) && location.TryGetProperty("range", out var locationRange) ? locationRange
                : default;
            return range.ValueKind == JsonValueKind.Object ? ConvertRange(range) : default;
        }

        /// <summary>
        /// The full declaration span (DocumentSymbol's own `range`, which spans the whole
        /// type/method body) — for nav-bar caret containment, distinct from the name-only
        /// navigation span <see cref="GetDocumentSymbolSpan"/> returns.
        /// </summary>
        static TextSpan GetDocumentSymbolExtentSpan(JsonElement token)
        {
            var range = token.TryGetProperty("range", out var plainRange) ? plainRange
                : token.TryGetProperty("selectionRange", out var selectionRange) ? selectionRange
                : token.TryGetProperty("location", out var location) && location.TryGetProperty("range", out var locationRange) ? locationRange
                : default;
            return range.ValueKind == JsonValueKind.Object ? ConvertRange(range) : default;
        }

        static bool IsContainerSymbolKind(int kind) => kind is 1 or 2 or 3 or 4; // File, Module, Namespace, Package

        static bool IsTypeSymbolKind(int kind) => kind is 5 or 10 or 11 or 23; // Class, Enum, Interface, Struct

        static string ConvertSymbolKind(int kind)
        {
            // LSP SymbolKind numeric values (textDocument/documentSymbol).
            return kind switch
            {
                5 => "Class",
                6 => "Method",
                7 => "Property",
                8 => "Field",
                9 => "Constructor",
                10 => "Enum",
                11 => "Interface",
                12 => "Function",
                13 => "Variable",
                14 => "Constant",
                23 => "Struct",
                24 => "Event",
                25 => "Operator",
                _ => "Symbol"
            };
        }

        static bool TryGetItemsArray(JsonElement result, out JsonElement items)
        {
            if (result.ValueKind == JsonValueKind.Array)
            {
                items = result;
                return true;
            }

            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("items", out items)
                && items.ValueKind == JsonValueKind.Array)
            {
                return true;
            }

            items = default;
            return false;
        }

        static LanguageDiagnostic ConvertDiagnostic(JsonElement token)
        {
            var id = token.TryGetProperty("code", out var codeProperty)
                ? codeProperty.ValueKind switch
                {
                    JsonValueKind.String => codeProperty.GetString() ?? UnknownDiagnosticId,
                    JsonValueKind.Number => codeProperty.GetRawText(),
                    _ => UnknownDiagnosticId
                }
                : UnknownDiagnosticId;
            var message = token.TryGetProperty("message", out var messageProperty) ? messageProperty.GetString() ?? string.Empty : string.Empty;
            var severity = token.TryGetProperty("severity", out var severityProperty) && severityProperty.ValueKind == JsonValueKind.Number
                ? severityProperty.GetInt32()
                : (int?)null;

            return new LanguageDiagnostic(
                id,
                message,
                ConvertSeverity(severity),
                ConvertRange(token.GetProperty("range")));
        }

        static DiagnosticSeverity ConvertSeverity(int? lspSeverity)
        {
            // LSP DiagnosticSeverity: 1=Error, 2=Warning, 3=Information, 4=Hint.
            return lspSeverity switch
            {
                1 => DiagnosticSeverity.Error,
                2 => DiagnosticSeverity.Warning,
                4 => DiagnosticSeverity.Hidden,
                _ => DiagnosticSeverity.Info
            };
        }

        static CompletionItem ConvertCompletionItem(JsonElement token)
        {
            var label = token.TryGetProperty("label", out var labelProperty) ? labelProperty.GetString() ?? string.Empty : string.Empty;
            var insertText = token.TryGetProperty("insertText", out var insertTextProperty)
                ? insertTextProperty.GetString() ?? label
                : label;
            var detail = token.TryGetProperty("detail", out var detailProperty) ? detailProperty.GetString() : null;
            return new CompletionItem(label, insertText, detail, null);
        }

        static string ConvertHoverContents(JsonElement contents)
        {
            // LSP hover `contents` is one of: MarkupContent {kind,value}, MarkedString (string
            // or {language,value}), or MarkedString[]. Collapse whichever shape arrives to
            // plain text good enough for a tooltip.
            return contents.ValueKind switch
            {
                JsonValueKind.String => contents.GetString() ?? string.Empty,
                JsonValueKind.Object when contents.TryGetProperty("value", out var value) => value.GetString() ?? string.Empty,
                JsonValueKind.Array => string.Join(
                    Environment.NewLine,
                    contents.EnumerateArray().Select(ConvertHoverContents).Where(text => !string.IsNullOrEmpty(text))),
                _ => string.Empty
            };
        }

        static NavigationTarget ConvertDefinitionResult(JsonElement token)
        {
            // Location {uri,range} and LocationLink {targetUri,targetRange,...} are both valid
            // `textDocument/definition` result shapes; normalize to one.
            var uri = token.TryGetProperty("targetUri", out var targetUri)
                ? targetUri.GetString()
                : token.TryGetProperty("uri", out var plainUri) ? plainUri.GetString() : null;
            var range = token.TryGetProperty("targetSelectionRange", out var targetRange)
                ? targetRange
                : token.TryGetProperty("range", out var plainRange) ? plainRange : default;

            var span = range.ValueKind == JsonValueKind.Object ? ConvertRange(range) : default;
            return new NavigationTarget(uri is null ? string.Empty : FromUri(uri), span.Start, span);
        }

        static TextSpan ConvertRange(JsonElement range)
        {
            return new TextSpan(ConvertPosition(range.GetProperty("start")), ConvertPosition(range.GetProperty("end")));
        }

        static TextEdit ConvertLspTextEdit(JsonElement token)
        {
            var newText = token.TryGetProperty("newText", out var newTextProperty) ? newTextProperty.GetString() ?? string.Empty : string.Empty;
            return new TextEdit(ConvertRange(token.GetProperty("range")), newText);
        }

        static object ToLspRange(TextSpan span)
        {
            return new
            {
                start = new { line = span.Start.Line - 1, character = span.Start.Column - 1 },
                end = new { line = span.End.Line - 1, character = span.End.Column - 1 }
            };
        }

        static TextPosition ConvertPosition(JsonElement position)
        {
            var line = position.TryGetProperty("line", out var lineProperty) ? lineProperty.GetInt32() : 0;
            var character = position.TryGetProperty("character", out var characterProperty) ? characterProperty.GetInt32() : 0;
            return new TextPosition(line + 1, character + 1);
        }

        static object ToLspPosition(string text, int offset)
        {
            offset = Math.Clamp(offset, 0, text.Length);
            var line = 0;
            var lineStart = 0;
            for (var i = 0; i < offset; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            return new { line, character = offset - lineStart };
        }

		static string[] ReadSemanticTokenTypes(JsonElement initializeResult)
		{
			if (initializeResult.ValueKind != JsonValueKind.Object
			    || !initializeResult.TryGetProperty("capabilities", out var capabilities)
			    || !capabilities.TryGetProperty("semanticTokensProvider", out var provider)
			    || provider.ValueKind != JsonValueKind.Object
			    || !provider.TryGetProperty("legend", out var legend)
			    || !legend.TryGetProperty("tokenTypes", out var tokenTypes)
			    || tokenTypes.ValueKind != JsonValueKind.Array)
				return Array.Empty<string>();
			return tokenTypes.EnumerateArray().Select(token => token.GetString() ?? string.Empty).ToArray();
		}

		static int GetOffset(string text, int requestedLine, int character)
		{
			var line = 0;
			var offset = 0;
			while (offset < text.Length && line < requestedLine) {
				if (text[offset++] == '\n')
					line++;
			}
			return Math.Clamp(offset + character, 0, text.Length);
		}

		static TextPosition ToTextPosition(string text, int offset)
		{
			offset = Math.Clamp(offset, 0, text.Length);
			var line = 0;
			var lineStart = 0;
			for (var index = 0; index < offset; index++) {
				if (text[index] == '\n') {
					line++;
					lineStart = index + 1;
				}
			}
			return new TextPosition(line + 1, offset - lineStart + 1);
		}

        static string ToUri(string fileName)
        {
            return new Uri(Path.GetFullPath(fileName)).AbsoluteUri;
        }

        static string FromUri(string uri)
        {
            return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile
                ? parsed.LocalPath
                : uri;
        }

        sealed class OpenDocument
        {
            public int Version { get; set; }
            public string Text { get; set; } = string.Empty;
        }
    }
}
