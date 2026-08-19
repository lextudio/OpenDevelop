#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
// Disambiguates against the COM interop "Accessibility" namespace (Accessibility.dll), now visible
// transitively now that this project sets UseWindowsForms=true for the resurrected WinForms<->WPF
// bridge (see ICSharpCode.SharpDevelop.csproj's WinForms\I*.cs re-include comment).
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynDocumentId = Microsoft.CodeAnalysis.DocumentId;
using RoslynProjectId = Microsoft.CodeAnalysis.ProjectId;
using RoslynTextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace ICSharpCode.SharpDevelop.LanguageServices.Roslyn
{
    public sealed partial class CSharpVBLanguageService : ILanguageService, IDisposable
    {
        const string NoTargetFrameworkKey = "";

        readonly AdhocWorkspace _workspace;
        // Multi-targeted projects (externals/OpenDevelop/doc/technotes/language-services.md §4 slice 4) get one Roslyn project
        // per TFM slice, so a document that's shared across TFMs has one RoslynDocumentId per
        // TFM here, keyed by TFM (NoTargetFrameworkKey for single-targeted/loose files).
        readonly Dictionary<DocumentId, Dictionary<string, RoslynDocumentId>> _documentVariantsByTfm;
        readonly Dictionary<DocumentId, string> _documentProjectFileNames;
        readonly Dictionary<string, RoslynProjectId> _projectsByLanguage;
        readonly Dictionary<string, RoslynProjectId> _projectsByKey;
        readonly Dictionary<string, List<string>> _targetFrameworksByProjectFileName;
        readonly Dictionary<string, string> _activeTargetFrameworkByProjectFileName;
        readonly IAnalyzerAssemblyLoader _analyzerAssemblyLoader = new DirectAnalyzerAssemblyLoader();
        // Serializes access to the dictionaries above (and the variants-check-then-add sequences
        // built on them). LoadProjectDocumentsAsync (background project load) and
        // UpsertDocumentAsync (file opened) run concurrently and both add documents; without a
        // lock, a concurrent Dictionary mutation corrupts state and one of the two documents ends
        // up orphaned - doubling every OpenLens reference count (and raising "concurrent update
        // performed on this collection" failures). No await happens while holding this lock.
        readonly object _documentLock = new();

        // Last computed code-action list per document (externals/OpenDevelop/doc/technotes/language-services.md §8), keyed by
        // the opaque CodeActionInfo.Id GetCodeActionsAsync handed out. See
        // CSharpVBLanguageService.CodeActions.cs.
        readonly Dictionary<DocumentId, Dictionary<string, CodeAction>> _pendingCodeActionsByDocument = new();

        public CSharpVBLanguageService()
        {
            _workspace = new AdhocWorkspace(MefHostServices.DefaultHost);
            _documentVariantsByTfm = new Dictionary<DocumentId, Dictionary<string, RoslynDocumentId>>();
            _documentProjectFileNames = new Dictionary<DocumentId, string>();
            _projectsByLanguage = new Dictionary<string, RoslynProjectId>(StringComparer.OrdinalIgnoreCase);
            _projectsByKey = new Dictionary<string, RoslynProjectId>(StringComparer.OrdinalIgnoreCase);
            _targetFrameworksByProjectFileName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _activeTargetFrameworkByProjectFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }

        public bool ContainsDocument(DocumentId documentId)
        {
            if (documentId is null)
                throw new ArgumentNullException(nameof(documentId));

            lock (_documentLock)
            {
                return _documentVariantsByTfm.ContainsKey(documentId);
            }
        }

        /// <summary>
        /// All TFMs known for a multi-targeted project (empty for a single-targeted project —
        /// there's nothing to pick between, so no UI should be shown).
        /// </summary>
        public IReadOnlyList<string> GetTargetFrameworks(string projectFileName)
        {
            lock (_documentLock)
            {
                return _targetFrameworksByProjectFileName.TryGetValue(projectFileName, out var targetFrameworks) && targetFrameworks.Count > 1
                    ? targetFrameworks.ToArray()
                    : Array.Empty<string>();
            }
        }

        /// <summary>
        /// The TFM slice currently used for completion/diagnostics/etc. for this project's
        /// documents (the "active target framework" a VS-style navigation bar lets you switch).
        /// </summary>
        public string? GetActiveTargetFramework(string projectFileName)
        {
            lock (_documentLock)
            {
                return _activeTargetFrameworkByProjectFileName.TryGetValue(projectFileName, out var targetFramework) ? targetFramework : null;
            }
        }

        public void SetActiveTargetFramework(string projectFileName, string targetFramework)
        {
            if (string.IsNullOrWhiteSpace(projectFileName))
                throw new ArgumentException("A project file name is required.", nameof(projectFileName));
            if (string.IsNullOrWhiteSpace(targetFramework))
                throw new ArgumentException("A target framework is required.", nameof(targetFramework));

            lock (_documentLock)
            {
                _activeTargetFrameworkByProjectFileName[projectFileName] = targetFramework;
            }
        }

        /// <summary>
        /// Targeted add for a single new <c>Compile</c> item (externals/OpenDevelop/doc/technotes/language-services.md §2.1/§4
        /// slice 3) — adds one document to each of the project's already-known TFM slices
        /// (`Workspace.OnDocumentAdded` under the hood via <see cref="AddDocument"/>) instead of
        /// re-snapshotting and diffing the whole project's file list. Assumes the new file is
        /// included under every TFM the project already has projects for — true for the common
        /// case (implicit globbing, no per-TFM `Condition` on the `Compile` item); a project with
        /// genuinely per-TFM-conditional individual file inclusion needs a full reload
        /// (<see cref="LoadProjectAsync(IProject,CancellationToken)"/>) to pick that up correctly.
        /// No-ops if the project hasn't been loaded yet (nothing to add to).
        /// </summary>
        public async Task AddCompileDocumentAsync(string projectFileName, string fileName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(projectFileName))
                throw new ArgumentException("A project file name is required.", nameof(projectFileName));
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("A file name is required.", nameof(fileName));

            var documentId = new DocumentId(fileName);
            bool alreadyTracked;
            lock (_documentLock)
            {
                alreadyTracked = _documentVariantsByTfm.ContainsKey(documentId);
            }
            if (alreadyTracked || !File.Exists(fileName))
                return;

            cancellationToken.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(fileName, cancellationToken);
            var targetFrameworks = GetTargetFrameworks(projectFileName);
            if (targetFrameworks.Count == 0)
            {
                RoslynProjectId? projectId = null;
                lock (_documentLock)
                {
                    projectId = _projectsByKey.TryGetValue(ProjectKey(projectFileName, null), out var id) ? id : null;
                }
                if (projectId is not null)
                    AddDocument(projectId, projectFileName, documentId, text, NoTargetFrameworkKey);
                return;
            }

            foreach (var targetFramework in targetFrameworks)
            {
                RoslynProjectId? projectId = null;
                lock (_documentLock)
                {
                    projectId = _projectsByKey.TryGetValue(ProjectKey(projectFileName, targetFramework), out var id) ? id : null;
                }
                if (projectId is not null)
                    AddDocument(projectId, projectFileName, documentId, text, targetFramework);
            }
        }

        /// <summary>
        /// Targeted removal for a single deleted/excluded <c>Compile</c> item — removes every TFM
        /// variant of this document directly by id, the counterpart to
        /// <see cref="AddCompileDocumentAsync"/>. No-ops if the document isn't tracked.
        /// </summary>
        public void RemoveDocument(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("A file name is required.", nameof(fileName));

            var documentId = new DocumentId(fileName);
            lock (_documentLock)
            {
                if (!_documentVariantsByTfm.TryGetValue(documentId, out var variants))
                    return;

                foreach (var tfmKey in variants.Keys.ToArray())
                    RemoveDocumentVariant(documentId, tfmKey);
            }
        }

        public Task LoadProjectAsync(IProject project, CancellationToken cancellationToken)
        {
            return LoadProjectAsync(LanguageServiceProjectSnapshot.FromProject(project), cancellationToken);
        }

        public async Task LoadProjectAsync(LanguageServiceProjectSnapshot projectSnapshot, CancellationToken cancellationToken)
        {
            await LoadProjectsAsync(new[] { projectSnapshot }, cancellationToken);
        }

        public async Task LoadProjectsAsync(IReadOnlyList<LanguageServiceProjectSnapshot> projectSnapshots, CancellationToken cancellationToken)
        {
            if (projectSnapshots is null)
                throw new ArgumentNullException(nameof(projectSnapshots));

            cancellationToken.ThrowIfCancellationRequested();

            foreach (var projectSnapshot in projectSnapshots)
            {
                EnsureProject(projectSnapshot);
            }

            foreach (var projectSnapshot in projectSnapshots)
            {
                ApplyProjectReferences(projectSnapshot);
            }

            foreach (var projectSnapshot in projectSnapshots)
            {
                await LoadProjectDocumentsAsync(projectSnapshot, cancellationToken);
            }
        }

        async Task LoadProjectDocumentsAsync(LanguageServiceProjectSnapshot projectSnapshot, CancellationToken cancellationToken)
        {
            if (projectSnapshot is null)
                throw new ArgumentNullException(nameof(projectSnapshot));

            cancellationToken.ThrowIfCancellationRequested();

            var projectId = EnsureProject(projectSnapshot);
            var tfmKey = TfmKey(projectSnapshot.TargetFramework);
            RemoveProjectDocumentVariantsMissingFromSnapshot(projectSnapshot, tfmKey);
            foreach (var fileName in projectSnapshot.DocumentFileNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var documentId = new DocumentId(fileName);
                bool alreadyTracked;
                lock (_documentLock)
                {
                    alreadyTracked = _documentVariantsByTfm.TryGetValue(documentId, out var variants) && variants.ContainsKey(tfmKey);
                }
                if (alreadyTracked)
                    continue;

                var text = await File.ReadAllTextAsync(fileName, cancellationToken);
                AddDocument(projectId, projectSnapshot.ProjectFileName, documentId, text, tfmKey);
            }
        }

        public Task UpsertDocumentAsync(DocumentId documentId, string text, CancellationToken cancellationToken)
        {
            if (documentId is null)
                throw new ArgumentNullException(nameof(documentId));
            if (text is null)
                throw new ArgumentNullException(nameof(text));

            cancellationToken.ThrowIfCancellationRequested();

            var sourceText = SourceText.From(text);
            bool hasVariants;
            lock (_documentLock)
            {
                hasVariants = _documentVariantsByTfm.TryGetValue(documentId, out var variants) && variants.Count > 0;
                if (hasVariants)
                {
                    // Every TFM slice shares the same buffer — keep them all in sync so whichever
                    // one becomes active later reflects the latest edit, not a stale snapshot.
                    foreach (var roslynDocumentId in variants.Values)
                    {
                        _workspace.TryApplyChanges(_workspace.CurrentSolution.WithDocumentText(roslynDocumentId, sourceText));
                    }
                }
            }

            if (hasVariants)
                return Task.CompletedTask;

            var projectId = EnsureProject(GetLanguage(documentId.FileName));
            AddDocument(projectId, string.Empty, documentId, text, NoTargetFrameworkKey);
            return Task.CompletedTask;
        }

        public async Task<CompletionResult> GetCompletionsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
            if (document is null)
                return CompletionResult.Empty;

            var completionService = CompletionService.GetService(document);
            if (completionService is null)
                return CompletionResult.Empty;

            var completions = await completionService.GetCompletionsAsync(document, offset, cancellationToken: cancellationToken);
            if (completions is null)
                return CompletionResult.Empty;

            var items = new List<CompletionItem>(completions.ItemsList.Count);
            foreach (var item in completions.ItemsList)
            {
                items.Add(await ConvertCompletionItemAsync(completionService, document, item, cancellationToken));
            }

            await AddResourceKeyCompletionsAsync(document, offset, items, cancellationToken);

            return new CompletionResult(items, await ConvertSpanAsync(document, completions.Span, cancellationToken));
        }

        /// <summary>
        /// Appends .resx-key completion items when the cursor is inside the key-string-literal
        /// argument of a BCL-style resource-access call (X.GetString/GetObject/GetStream("|"),
        /// X.ApplyResources(_, "|"), X("|")/X["|"]) - the code-completion piece of OpenDevelop's
        /// Hornung.ResourceToolkit. Shared by both C# and VB documents via
        /// ResourceReferenceResolver's language dispatch (document.Project.Language already
        /// distinguishes them for everything else in this class).
        /// ICSharpCode.Core.ResourceService.GetString("|") is intentionally not offered here -
        /// IResourceService has no "list all registered keys" API to complete against.
        /// </summary>
        static async Task AddResourceKeyCompletionsAsync(Document document, int offset, List<CompletionItem> items, CancellationToken cancellationToken)
        {
            var text = await document.GetTextAsync(cancellationToken);
            var reference = ResourceReferenceResolver.FindResourceKeyAtCursor(document.Project.Language, text.ToString(), offset);
            if (reference?.Kind != ResourceReferenceResolver.ResourceReferenceKind.BclResourceManager)
                return;

            // Prefer the real project's directory; fall back to the document's own directory for
            // loose/ad-hoc single-file "projects" (UpsertDocumentAsync's EnsureProject bootstrap
            // path for a file never added via AddCompileDocumentAsync has no FilePath at all).
            var projectFilePath = document.Project.FilePath;
            var projectDirectory = string.IsNullOrEmpty(projectFilePath) ? null : Path.GetDirectoryName(projectFilePath);
            if (string.IsNullOrEmpty(projectDirectory))
                projectDirectory = Path.GetDirectoryName(document.FilePath);
            if (string.IsNullOrEmpty(projectDirectory) || !Directory.Exists(projectDirectory))
                return;

            foreach (var resxFile in Directory.EnumerateFiles(projectDirectory, "*.resx", SearchOption.AllDirectories))
            {
                IReadOnlyList<LeXtudio.OpenDevelop.ResourceFiles.ResourceEntry> entries;
                try
                {
                    entries = LeXtudio.OpenDevelop.ResourceFiles.ResourceFileReader.Read(resxFile);
                }
                catch
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (entry.IsEditable && entry.Type.Equals("string", StringComparison.OrdinalIgnoreCase))
                    {
                        items.Add(new CompletionItem(entry.Name, entry.Name, entry.Value, glyph: "Resource"));
                    }
                }
            }
        }

        public async Task<QuickInfo?> GetQuickInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
            if (document is null)
                return null;

            var quickInfoService = QuickInfoService.GetService(document);
            if (quickInfoService is null)
                return null;

            var info = await quickInfoService.GetQuickInfoAsync(document, offset, cancellationToken);
            if (info is null)
                return null;

            var text = string.Join(Environment.NewLine, info.Sections.Select(section => section.Text));
            return string.IsNullOrWhiteSpace(text)
                ? null
                : new QuickInfo(text, await ConvertSpanAsync(document, info.Span, cancellationToken));
        }

        public async Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DocumentId documentId, CancellationToken cancellationToken)
        {
            var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
            if (document is null)
                return Array.Empty<LanguageDiagnostic>();

            var diagnostics = await ComputeRoslynDiagnosticsAsync(document, cancellationToken);
            return diagnostics.Select(ConvertDiagnostic).ToArray();
        }

        /// <summary>
        /// Symbol-kind classifications for the four groups doc/technotes/csharp-vb-binding.md §8.4/§14
        /// call out for phase 1 (ReferenceTypes/ValueTypes/MethodCall/FieldAccess) - everything
        /// Roslyn's classifier also reports for keywords/strings/comments/numbers/operators/
        /// punctuation/plain identifiers is intentionally left unmapped and dropped, since AvalonEdit's
        /// own .xshd lexical highlighter already colors those and this must not double-paint them.
        /// </summary>
        public async Task<IReadOnlyList<SemanticToken>> GetSemanticTokensAsync(DocumentId documentId, CancellationToken cancellationToken)
        {
            var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
            if (document is null)
                return Array.Empty<SemanticToken>();

            var text = await document.GetTextAsync(cancellationToken);
            var classifiedSpans = await Microsoft.CodeAnalysis.Classification.Classifier.GetClassifiedSpansAsync(
                document, new RoslynTextSpan(0, text.Length), cancellationToken);

            var tokens = new List<SemanticToken>();
            foreach (var classifiedSpan in classifiedSpans)
            {
                var type = ConvertClassificationType(classifiedSpan.ClassificationType);
                if (type is null)
                    continue;
                tokens.Add(new SemanticToken(ConvertLineSpan(text.Lines.GetLinePositionSpan(classifiedSpan.TextSpan)), type));
            }
            return tokens;
        }

        static string? ConvertClassificationType(string roslynClassificationType)
        {
            switch (roslynClassificationType)
            {
                case Microsoft.CodeAnalysis.Classification.ClassificationTypeNames.ClassName:
                case Microsoft.CodeAnalysis.Classification.ClassificationTypeNames.RecordClassName:
                case Microsoft.CodeAnalysis.Classification.ClassificationTypeNames.InterfaceName:
                case Microsoft.CodeAnalysis.Classification.ClassificationTypeNames.DelegateName:
                case Microsoft.CodeAnalysis.Classification.ClassificationTypeNames.ModuleName:
                    return "ReferenceTypes";
                case Microsoft.CodeAnalysis.Classification.ClassificationTypeNames.StructName:
                case Microsoft.CodeAnalysis.Classification.ClassificationTypeNames.RecordStructName:
                case Microsoft.CodeAnalysis.Classification.ClassificationTypeNames.EnumName:
                    return "ValueTypes";
                case Microsoft.CodeAnalysis.Classification.ClassificationTypeNames.MethodName:
                case Microsoft.CodeAnalysis.Classification.ClassificationTypeNames.ExtensionMethodName:
                    return "MethodCall";
                case Microsoft.CodeAnalysis.Classification.ClassificationTypeNames.FieldName:
                case Microsoft.CodeAnalysis.Classification.ClassificationTypeNames.EnumMemberName:
                case Microsoft.CodeAnalysis.Classification.ClassificationTypeNames.PropertyName:
                case Microsoft.CodeAnalysis.Classification.ClassificationTypeNames.EventName:
                    return "FieldAccess";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Compiler + analyzer diagnostics for <paramref name="document"/>, as raw Roslyn
        /// <see cref="RoslynDiagnostic"/>s rather than our <see cref="LanguageDiagnostic"/> DTO —
        /// shared by <see cref="GetDiagnosticsAsync"/> and <see cref="GetCodeActionsAsync"/>
        /// (externals/OpenDevelop/doc/technotes/language-services.md §8.3), which needs the raw diagnostics to match against
        /// each <c>CodeFixProvider.FixableDiagnosticIds</c>.
        /// </summary>
        async Task<ImmutableArray<RoslynDiagnostic>> ComputeRoslynDiagnosticsAsync(Document document, CancellationToken cancellationToken)
        {
            var compilation = await document.Project.GetCompilationAsync(cancellationToken);
            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (compilation is null || syntaxTree is null)
                return ImmutableArray<RoslynDiagnostic>.Empty;

            var analyzers = document.Project.AnalyzerReferences
                .SelectMany(reference => reference.GetAnalyzers(document.Project.Language))
                .ToImmutableArray();

            // Compiler-only diagnostics is the common (no third-party analyzer) case — cheaper
            // than always going through CompilationWithAnalyzers.
            var diagnostics = analyzers.IsEmpty
                ? compilation.GetDiagnostics(cancellationToken)
                : (IReadOnlyList<RoslynDiagnostic>)await compilation
                    .WithAnalyzers(analyzers, document.Project.AnalyzerOptions)
                    .GetAllDiagnosticsAsync(cancellationToken);

            return diagnostics
                .Where(diagnostic => diagnostic.Location.SourceTree == syntaxTree || diagnostic.Location == Location.None)
                .ToImmutableArray();
        }

        public async Task<IReadOnlyList<NavigationTarget>> GoToDefinitionAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
            if (document is null)
                return Array.Empty<NavigationTarget>();

            var sourceText = await document.GetTextAsync(cancellationToken);
            if (offset < 0 || offset > sourceText.Length)
                return Array.Empty<NavigationTarget>();

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel is null)
                return Array.Empty<NavigationTarget>();

            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, offset, _workspace, cancellationToken);
            if (symbol is null)
                return Array.Empty<NavigationTarget>();

            var sourceSymbol = await SymbolFinder.FindSourceDefinitionAsync(symbol, document.Project.Solution, cancellationToken)
                ?? symbol;

            return sourceSymbol.Locations
                .Where(location => location.IsInSource && location.SourceTree is not null)
                .Select(ConvertLocationToNavigationTarget)
                .Where(target => !string.IsNullOrEmpty(target.FileName))
                .ToArray();
        }

        public async Task<SymbolReferencesResult?> FindReferencesAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
            if (document is null)
                return null;

            var sourceText = await document.GetTextAsync(cancellationToken);
            if (offset < 0 || offset > sourceText.Length)
                return null;

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel is null)
                return null;

            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, offset, _workspace, cancellationToken);
            if (symbol is null)
                return null;

            var referencedSymbols = await SymbolFinder.FindReferencesAsync(
                symbol, document.Project.Solution, cancellationToken);
            var references = referencedSymbols
                .SelectMany(result => result.Locations)
                .Where(reference => reference.Location.IsInSource && reference.Location.SourceTree is not null)
                .Select(reference => ConvertLocationToNavigationTarget(reference.Location))
                .Where(target => !string.IsNullOrEmpty(target.FileName))
                // A type used via 'new' shows up as both the NamedType reference and the implicit
                // .ctor reference at the same location; a duplicate workspace document (see
                // AddDocument) doubles it again. The lens count must be unique locations, like
                // Visual Studio's CodeLens - not ReferencedSymbol entries.
                .DistinctBy(target => (target.FileName, target.Span))
                .ToArray();
            return new SymbolReferencesResult(symbol.Name, references);
        }

        public async Task<IReadOnlyList<TextEdit>> FormatAsync(DocumentId documentId, TextSpan? span, CancellationToken cancellationToken)
        {
            var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
            if (document is null)
                return Array.Empty<TextEdit>();

            var sourceText = await document.GetTextAsync(cancellationToken);
            var formattedDocument = span is null
                ? await Formatter.FormatAsync(document, cancellationToken: cancellationToken)
                : await Formatter.FormatAsync(document, ToRoslynSpan(sourceText, span.Value), cancellationToken: cancellationToken);
            var formattedText = await formattedDocument.GetTextAsync(cancellationToken);

            return formattedText.GetTextChanges(sourceText)
                .Select(change => ConvertTextChange(sourceText, change))
                .ToArray();
        }

        public async Task<IReadOnlyList<DocumentOutlineNode>> GetDocumentOutlineAsync(DocumentId documentId, CancellationToken cancellationToken)
        {
            var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
            if (document is null)
                return Array.Empty<DocumentOutlineNode>();

            var compilation = await document.Project.GetCompilationAsync(cancellationToken);
            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (compilation is null || syntaxTree is null)
                return Array.Empty<DocumentOutlineNode>();

            var types = new List<DocumentOutlineNode>();
            CollectTypes(compilation.GlobalNamespace, syntaxTree, types);
            return types.OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public async Task<SymbolHierarchyResult?> GetBaseSymbolsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var symbol = await FindSymbolAsync(documentId, offset, cancellationToken);
            if (symbol is null)
                return null;

            var nodes = new List<SymbolNavigationNode>();
            if (symbol is INamedTypeSymbol type)
            {
                for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
                    AddNavigationNode(nodes, baseType);
                foreach (var interfaceType in type.AllInterfaces)
                    AddNavigationNode(nodes, interfaceType);
            }
            else
            {
                for (var current = symbol; current is not null; current = GetBaseMember(current))
                {
                    var baseMember = GetBaseMember(current);
                    if (baseMember is null)
                        break;
                    AddNavigationNode(nodes, baseMember);
                }
            }
            return new SymbolHierarchyResult(FormatHierarchyName(symbol), nodes);
        }

        public async Task<SymbolHierarchyResult?> GetDerivedSymbolsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var symbol = await FindSymbolAsync(documentId, offset, cancellationToken);
            if (symbol is null)
                return null;

            if (symbol is INamedTypeSymbol type)
            {
                var derived = (type.TypeKind == TypeKind.Interface
                    ? await SymbolFinder.FindImplementationsAsync(type, _workspace.CurrentSolution, cancellationToken: cancellationToken)
                    : await SymbolFinder.FindDerivedClassesAsync(type, _workspace.CurrentSolution, cancellationToken: cancellationToken))
                    .OfType<INamedTypeSymbol>().ToList();
                return new SymbolHierarchyResult(FormatHierarchyName(type), BuildDerivedTypeNodes(type, derived));
            }

            if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
            {
                var overrides = await SymbolFinder.FindOverridesAsync(symbol, _workspace.CurrentSolution, cancellationToken: cancellationToken);
                var nodes = new List<SymbolNavigationNode>();
                foreach (var item in overrides)
                    AddNavigationNode(nodes, item);
                return new SymbolHierarchyResult(FormatHierarchyName(symbol), nodes);
            }

            return null;
        }

        async Task<ISymbol?> FindSymbolAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
            if (document is null)
                return null;
            var model = await document.GetSemanticModelAsync(cancellationToken);
            return model is null ? null : await SymbolFinder.FindSymbolAtPositionAsync(model, offset, _workspace, cancellationToken);
        }

        static IReadOnlyList<SymbolNavigationNode> BuildDerivedTypeNodes(INamedTypeSymbol parent, List<INamedTypeSymbol> allDerived)
        {
            var nodes = new List<SymbolNavigationNode>();
            foreach (var child in allDerived.Where(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate.BaseType, parent)
                || candidate.Interfaces.Contains(parent, SymbolEqualityComparer.Default)))
            {
                AddNavigationNode(nodes, child, BuildDerivedTypeNodes(child, allDerived));
            }
            return nodes;
        }

        static void AddNavigationNode(List<SymbolNavigationNode> nodes, ISymbol symbol, IReadOnlyList<SymbolNavigationNode>? children = null)
        {
            var location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
            if (location is null)
                return;
            nodes.Add(new SymbolNavigationNode(
                FormatHierarchyName(symbol), symbol.Kind.ToString(), ConvertLocationToNavigationTarget(location), children,
                symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null));
        }

        static string FormatHierarchyName(ISymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        static readonly SymbolDisplayFormat HelpKeywordFormat = new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

        public async Task<string?> GetHelpKeywordAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var symbol = await FindSymbolAsync(documentId, offset, cancellationToken);
            if (symbol is null)
                return null;
            if (symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } enumField)
                symbol = enumField.ContainingType;
            return symbol.ToDisplayString(HelpKeywordFormat);
        }

        public async Task<string?> GetContainingTypeNameAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var symbol = await FindSymbolAsync(documentId, offset, cancellationToken);
            return (symbol as INamedTypeSymbol ?? symbol?.ContainingType)?.Name;
        }

        public Task RefreshProjectAsync(DocumentId documentId, CancellationToken cancellationToken)
        {
            var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(documentId.FileName));
            return project is null
                ? Task.CompletedTask
                : LoadProjectAsync(LanguageServiceProjectSnapshot.FromProject(project), cancellationToken);
        }

        static ISymbol? GetBaseMember(ISymbol member)
        {
            if (member is IMethodSymbol method)
            {
                if (method.ExplicitInterfaceImplementations.Length > 0) return method.ExplicitInterfaceImplementations[0];
                if (method.OverriddenMethod is not null) return method.OverriddenMethod;
            }
            if (member is IPropertySymbol property)
            {
                if (property.ExplicitInterfaceImplementations.Length > 0) return property.ExplicitInterfaceImplementations[0];
                if (property.OverriddenProperty is not null) return property.OverriddenProperty;
            }
            if (member is IEventSymbol eventSymbol)
            {
                if (eventSymbol.ExplicitInterfaceImplementations.Length > 0) return eventSymbol.ExplicitInterfaceImplementations[0];
                if (eventSymbol.OverriddenEvent is not null) return eventSymbol.OverriddenEvent;
            }
            var containingType = member.ContainingType;
            if (containingType is null) return null;
            foreach (var interfaceType in containingType.AllInterfaces)
                foreach (var interfaceMember in interfaceType.GetMembers())
                    if (SymbolEqualityComparer.Default.Equals(containingType.FindImplementationForInterfaceMember(interfaceMember), member))
                        return interfaceMember;
            return null;
        }

        public async Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> RenameSymbolAsync(
            DocumentId documentId, int offset, string newName, CancellationToken cancellationToken,
            bool renameOverloads = false, bool renameInStrings = false, bool renameInComments = false)
        {
            var noEdits = new Dictionary<string, IReadOnlyList<TextEdit>>();
            if (string.IsNullOrWhiteSpace(newName))
                return noEdits;

            var symbol = await FindSymbolAtAsync(documentId, offset, cancellationToken);
            if (symbol is null)
                return noEdits;

            var originalSolution = symbol.Value.Document.Project.Solution;
            var options = new SymbolRenameOptions(
                RenameOverloads: renameOverloads, RenameInStrings: renameInStrings, RenameInComments: renameInComments, RenameFile: false);
            Solution renamedSolution;
            try
            {
                renamedSolution = await Renamer.RenameSymbolAsync(originalSolution, symbol.Value.Symbol, options, newName, cancellationToken);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"Rename failed for '{symbol.Value.Symbol.Name}' -> '{newName}': {ex.Message}");
                return noEdits;
            }

            return await DiffSolutionsToTextEditsAsync(originalSolution, renamedSolution, cancellationToken);
        }

        public async Task<string?> GetSymbolNameAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var symbol = await FindSymbolAtAsync(documentId, offset, cancellationToken);
            return symbol?.Symbol.Name;
        }

        public async Task<SymbolKindInfo?> GetSymbolKindAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var found = await FindSymbolAtAsync(documentId, offset, cancellationToken);
            if (found is null)
                return null;

            var symbol = found.Value.Symbol;
            bool isMember = symbol is IMethodSymbol or IFieldSymbol or IPropertySymbol or IEventSymbol;
            bool isType = symbol is INamedTypeSymbol;
            bool isNamespace = symbol is INamespaceSymbol;
            bool isLocal = symbol is ILocalSymbol or IParameterSymbol;
            bool hasSourceLocation = !symbol.Locations.IsEmpty && symbol.Locations[0].IsInSource;
            return new SymbolKindInfo(isMember, isType, isNamespace, isLocal, hasSourceLocation);
        }

        /// <summary>
        /// Looks up the document's Roslyn project language once (cached from the last successful
        /// load/sync - see <see cref="_projectsByLanguage"/>/<see cref="GetOrLoadDocumentAsync"/>)
        /// rather than requiring a fresh document round trip just to know C# vs. VB.
        /// </summary>
        public async Task<bool> IsValidIdentifierAsync(DocumentId documentId, string name, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
            if (document is null)
                return false;

            return document.Project.Language == LanguageNames.VisualBasic
                ? Microsoft.CodeAnalysis.VisualBasic.SyntaxFacts.IsValidIdentifier(name)
                : Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name);
        }

        /// <summary>
        /// Shared by <see cref="RenameSymbolAsync"/> and <see cref="GetSymbolNameAsync"/> - both
        /// need "the symbol at this offset, plus the document it came from" (the latter for its
        /// containing solution, used as the rename's before-state).
        /// </summary>
        async Task<(ISymbol Symbol, Document Document)?> FindSymbolAtAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
            if (document is null)
                return null;

            var sourceText = await document.GetTextAsync(cancellationToken);
            if (offset < 0 || offset > sourceText.Length)
                return null;

            // Deliberately the Document overload (no `workspace` argument): the Workspace overloads
            // re-bind the resolved symbol to an equivalent symbol in that workspace's solution, and
            // with several documents of the same file name in the shared AdhocWorkspace (e.g. two
            // tests' temp copies of Widget.cs) that rebinding can land on the WRONG document's
            // syntax tree - which then leaks into DeclaringSyntaxReferences and made ExtractInterface
            // write its class edit to a stale file path. The Document overload resolves against the
            // semantic model alone, keeping the symbol rooted in this document; callers that need a
            // solution-wide symbol (Rename) already navigate via symbol.Document.Project.Solution.
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, cancellationToken);
            return symbol is null ? null : (symbol, document);
        }

        /// <summary>
        /// Diffs every document present in both solutions and returns the resulting
        /// <see cref="TextEdit"/>s per absolute file path — shared by <see cref="RenameSymbolAsync"/>
        /// and <see cref="ApplyCodeActionAsync"/> (externals/OpenDevelop/doc/technotes/language-services.md §8.3), since both
        /// end up needing "what changed across the whole solution" from a before/after
        /// <see cref="Solution"/> pair. Every TFM slice of a multi-targeted project is a separate
        /// Roslyn <see cref="Document"/> pointing at the same real file — only the first slice's
        /// edits per file path are kept so the caller doesn't see (and try to apply) the same
        /// text change twice.
        /// </summary>
        static async Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> DiffSolutionsToTextEditsAsync(
            Solution originalSolution, Solution changedSolution, CancellationToken cancellationToken)
        {
            var editsByFile = new Dictionary<string, IReadOnlyList<TextEdit>>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in originalSolution.Projects)
            {
                foreach (var originalDocument in project.Documents)
                {
                    if (originalDocument.FilePath is not { } filePath || editsByFile.ContainsKey(filePath))
                        continue;

                    var changedDocument = changedSolution.GetDocument(originalDocument.Id);
                    if (changedDocument is null)
                        continue;

                    var originalText = await originalDocument.GetTextAsync(cancellationToken);
                    var changedText = await changedDocument.GetTextAsync(cancellationToken);
                    var textEdits = changedText.GetTextChanges(originalText)
                        .Select(change => ConvertTextChange(originalText, change))
                        .ToArray();
                    if (textEdits.Length > 0)
                        editsByFile[filePath] = textEdits;
                }
            }

            return editsByFile;
        }

        public async Task<IReadOnlyList<NavigationTarget>> FindMemberAsync(
            string typeFullName, string methodName, int? parameterCount, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(typeFullName) || string.IsNullOrWhiteSpace(methodName))
                return Array.Empty<NavigationTarget>();

            // The type can live in any project in the solution (a test project references the
            // production project, but the class-under-test's own declaration is what we want) -
            // GetTypeByMetadataName only searches one compilation, so try every project's.
            foreach (var project in _workspace.CurrentSolution.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var compilation = await project.GetCompilationAsync(cancellationToken);
                var type = compilation?.GetTypeByMetadataName(typeFullName);
                if (type is null)
                    continue;

                var candidates = type.GetMembers(methodName)
                    .OfType<IMethodSymbol>()
                    .Where(method => parameterCount is null || method.Parameters.Length == parameterCount.Value)
                    .ToArray();
                if (candidates.Length == 0)
                    continue;

                var targets = new List<NavigationTarget>();
                foreach (var candidate in candidates)
                {
                    var sourceSymbol = await SymbolFinder.FindSourceDefinitionAsync(candidate, project.Solution, cancellationToken)
                        ?? candidate;
                    targets.AddRange(sourceSymbol.Locations
                        .Where(location => location.IsInSource && location.SourceTree is not null)
                        .Select(ConvertLocationToNavigationTarget));
                }

                if (targets.Count > 0)
                    return targets;
            }

            return Array.Empty<NavigationTarget>();
        }

        // GetCodeActionsAsync/ApplyCodeActionAsync (externals/OpenDevelop/doc/technotes/language-services.md §8.3) are
        // implemented in CSharpVBLanguageService.CodeActions.cs.

        public void OnTextChanged(DocumentId documentId, TextChange change)
        {
            lock (_documentLock)
            {
                if (!_documentVariantsByTfm.TryGetValue(documentId, out var variants))
                    return;

                foreach (var roslynDocumentId in variants.Values.ToArray())
                {
                    var document = _workspace.CurrentSolution.GetDocument(roslynDocumentId);
                    if (document is null)
                        continue;

                    var sourceText = document.GetTextAsync().GetAwaiter().GetResult();
                    var roslynSpan = ToRoslynSpan(sourceText, change.Span);
                    var changedText = sourceText.Replace(roslynSpan, change.NewText);
                    _workspace.TryApplyChanges(_workspace.CurrentSolution.WithDocumentText(roslynDocumentId, changedText));
                }
            }
        }

        async Task<Document?> GetOrLoadDocumentAsync(DocumentId documentId, CancellationToken cancellationToken)
        {
            if (documentId is null)
                throw new ArgumentNullException(nameof(documentId));

            var roslynDocumentId = ResolveActiveRoslynDocumentId(documentId);
            if (roslynDocumentId is not null)
                return _workspace.CurrentSolution.GetDocument(roslynDocumentId);

            if (!File.Exists(documentId.FileName))
                return null;

            var text = await File.ReadAllTextAsync(documentId.FileName, cancellationToken);
            await UpsertDocumentAsync(documentId, text, cancellationToken);
            roslynDocumentId = ResolveActiveRoslynDocumentId(documentId);
            return roslynDocumentId is null ? null : _workspace.CurrentSolution.GetDocument(roslynDocumentId);
        }

		/// <summary>
		/// Returns the project-backed Roslyn document for IDE subsystems that must perform semantic,
		/// analyzer-config-aware source transformations (for example the WinForms designer).
		/// The returned document remains owned by this language service's workspace.
		/// </summary>
		public Task<Document?> GetProjectDocumentAsync(string fileName, CancellationToken cancellationToken = default) =>
			GetOrLoadDocumentAsync(new DocumentId(fileName), cancellationToken);

		/// <summary>Gets an already tracked project document without asynchronous loading.</summary>
		public Document? TryGetProjectDocument(string fileName)
		{
			var id = ResolveActiveRoslynDocumentId(new DocumentId(fileName));
			return id == null ? null : _workspace.CurrentSolution.GetDocument(id);
		}

        /// <summary>
        /// Picks which TFM slice's <see cref="RoslynDocumentId"/> to use for a document that may
        /// have one variant per TFM — the project's "active target framework"
        /// (<see cref="GetActiveTargetFramework"/>), falling back to whichever variant exists if
        /// no active TFM has been recorded yet.
        /// </summary>
        RoslynDocumentId? ResolveActiveRoslynDocumentId(DocumentId documentId)
        {
            lock (_documentLock)
            {
                if (!_documentVariantsByTfm.TryGetValue(documentId, out var variants) || variants.Count == 0)
                    return null;

                var projectFileName = _documentProjectFileNames.TryGetValue(documentId, out var pf) ? pf : null;
                var activeTargetFramework = projectFileName is not null ? GetActiveTargetFramework(projectFileName) : null;
                if (activeTargetFramework is not null && variants.TryGetValue(activeTargetFramework, out var activeId))
                    return activeId;

                return variants.Values.First();
            }
        }

        RoslynProjectId EnsureProject(string language)
        {
            lock (_documentLock)
            {
                if (_projectsByLanguage.TryGetValue(language, out var projectId))
                    return projectId;

                projectId = RoslynProjectId.CreateNewId("UnoDevelop " + language);
                var projectInfo = ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    "UnoDevelop " + language,
                    "UnoDevelop." + language,
                    language,
                    metadataReferences: CreateDefaultMetadataReferences(),
                    compilationOptions: CreateCompilationOptions(language),
                    parseOptions: CreateParseOptions(language));

                _workspace.AddProject(projectInfo);
                _projectsByLanguage[language] = projectId;
                return projectId;
            }
        }

        RoslynProjectId EnsureProject(LanguageServiceProjectSnapshot projectSnapshot)
        {
            lock (_documentLock)
            {
                var key = ProjectKey(projectSnapshot.ProjectFileName, projectSnapshot.TargetFramework);
                if (_projectsByKey.TryGetValue(key, out var projectId))
                {
                    UpdateProject(projectId, projectSnapshot);
                    return projectId;
                }

            var language = ToRoslynLanguageName(projectSnapshot.Language);
            var assemblyName = Path.GetFileNameWithoutExtension(projectSnapshot.ProjectFileName);
            var displayName = projectSnapshot.TargetFramework is null
                ? assemblyName
                : $"{assemblyName} ({projectSnapshot.TargetFramework})";
            projectId = RoslynProjectId.CreateNewId(key);
            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                displayName,
                // Must be a valid assembly identity, not a path — the project's full file path
                // (previously passed here) trips CS8203 "invalid assembly name" on every
                // compilation.
                assemblyName,
                language,
                filePath: projectSnapshot.ProjectFileName,
                metadataReferences: CreateMetadataReferences(projectSnapshot.MetadataReferenceFileNames),
                compilationOptions: CreateCompilationOptions(language, projectSnapshot.NullableContext),
                parseOptions: CreateParseOptions(language, projectSnapshot.LanguageVersion, projectSnapshot.PreprocessorSymbols),
                analyzerReferences: CreateAnalyzerReferences(projectSnapshot.AnalyzerAssemblyFileNames));

            _workspace.AddProject(projectInfo);
            _projectsByKey[key] = projectId;
            RegisterTargetFramework(projectSnapshot.ProjectFileName, projectSnapshot.TargetFramework);
                return projectId;
            }
        }

        void RegisterTargetFramework(string projectFileName, string? targetFramework)
        {
            if (targetFramework is null)
                return;

            if (!_targetFrameworksByProjectFileName.TryGetValue(projectFileName, out var targetFrameworks))
            {
                targetFrameworks = new List<string>();
                _targetFrameworksByProjectFileName[projectFileName] = targetFrameworks;
            }

            if (!targetFrameworks.Contains(targetFramework, StringComparer.OrdinalIgnoreCase))
                targetFrameworks.Add(targetFramework);

            // First TFM seen becomes the default active one; a later UI selection
            // (SetActiveTargetFramework) overrides it.
            if (!_activeTargetFrameworkByProjectFileName.ContainsKey(projectFileName))
                _activeTargetFrameworkByProjectFileName[projectFileName] = targetFramework;
        }

        static string ProjectKey(string projectFileName, string? targetFramework)
        {
            return targetFramework is null ? projectFileName : projectFileName + "|" + targetFramework;
        }

        static string TfmKey(string? targetFramework) => targetFramework ?? NoTargetFrameworkKey;

        void UpdateProject(RoslynProjectId projectId, LanguageServiceProjectSnapshot projectSnapshot)
        {
            var language = ToRoslynLanguageName(projectSnapshot.Language);
            var solution = _workspace.CurrentSolution
                .WithProjectMetadataReferences(projectId, CreateMetadataReferences(projectSnapshot.MetadataReferenceFileNames))
                .WithProjectCompilationOptions(projectId, CreateCompilationOptions(language, projectSnapshot.NullableContext))
                .WithProjectParseOptions(projectId, CreateParseOptions(language, projectSnapshot.LanguageVersion, projectSnapshot.PreprocessorSymbols))
                .WithProjectAnalyzerReferences(projectId, CreateAnalyzerReferences(projectSnapshot.AnalyzerAssemblyFileNames));

            _workspace.TryApplyChanges(solution);
        }

        /// <summary>
        /// Loads third-party analyzer/source-generator assemblies (externals/OpenDevelop/doc/technotes/language-services.md
        /// §2.3) via <see cref="AnalyzerFileReference"/> — the same Roslyn Workspace abstraction
        /// covers both `DiagnosticAnalyzer`s and `ISourceGenerator`/`IIncrementalGenerator`s, and
        /// once attached to a Project, `Project.GetCompilationAsync()` automatically runs any
        /// generators and includes their generated trees, no separate generator-driver plumbing
        /// needed. Assemblies are loaded directly (no per-analyzer `AssemblyLoadContext`
        /// isolation) — matches VS/Rider's default behavior; isolation was the doc's own open
        /// question and is left as a follow-up if a misbehaving analyzer ever needs killing
        /// without restarting the whole language service.
        /// </summary>
        ImmutableArray<AnalyzerReference> CreateAnalyzerReferences(IReadOnlyList<string> analyzerAssemblyFileNames)
        {
            var references = ImmutableArray.CreateBuilder<AnalyzerReference>();
            foreach (var path in analyzerAssemblyFileNames)
            {
                try
                {
                    references.Add(new AnalyzerFileReference(path, _analyzerAssemblyLoader));
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"Failed to load analyzer/generator assembly '{path}': {ex.Message}");
                }
            }

            return references.ToImmutable();
        }

        /// <summary>
        /// Minimal <see cref="IAnalyzerAssemblyLoader"/> — just <see cref="Assembly.LoadFrom"/>,
        /// which already returns the cached assembly for a path it's loaded before.
        /// </summary>
        sealed class DirectAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
        {
            public void AddDependencyLocation(string fullPath)
            {
            }

            public System.Reflection.Assembly LoadFromPath(string fullPath) => System.Reflection.Assembly.LoadFrom(fullPath);
        }

        void ApplyProjectReferences(LanguageServiceProjectSnapshot projectSnapshot)
        {
            lock (_documentLock)
            {
                var key = ProjectKey(projectSnapshot.ProjectFileName, projectSnapshot.TargetFramework);
                if (!_projectsByKey.TryGetValue(key, out var projectId))
                    return;

                var references = projectSnapshot.ProjectReferenceFileNames
                    .Select(referencedProjectFileName => ResolveReferencedProjectId(referencedProjectFileName, projectSnapshot.TargetFramework))
                    .Where(id => id is not null)
                    .Select(id => new ProjectReference(id!))
                    .ToArray();

                var solution = _workspace.CurrentSolution.WithProjectReferences(projectId, references);
                _workspace.TryApplyChanges(solution);
            }
        }

        /// <summary>
        /// Resolves a project reference to the referenced project's Roslyn <see cref="RoslynProjectId"/>
        /// for the given referencing TFM slice. Prefers an exact TFM match — the common real-world
        /// case where both projects multi-target the same TFM set (e.g. both build
        /// `net8.0;net9.0`) — the same way MSBuild picks a same-TFM output when one exists. Falls
        /// back to the referenced project's "active" TFM (or its only slice, if it isn't
        /// multi-targeted) when no exact match is loaded. This does not implement full NuGet
        /// nearest-compatible-framework resolution (netstandard fallback, version-range matching,
        /// asset-compat scoring, ...) — that's a real build-system concern out of scope for an
        /// editor's approximate compilation context; an exact match covers the case that actually
        /// matters for completion/diagnostics fidelity, and the active-TFM fallback keeps
        /// everything else working the way it did before this method existed.
        /// </summary>
        RoslynProjectId? ResolveReferencedProjectId(string referencedProjectFileName, string? referencingTargetFramework)
        {
            lock (_documentLock)
            {
                if (referencingTargetFramework is not null
                    && _projectsByKey.TryGetValue(ProjectKey(referencedProjectFileName, referencingTargetFramework), out var exactMatchId))
                {
                    return exactMatchId;
                }

                var activeTargetFramework = GetActiveTargetFramework(referencedProjectFileName);
                if (activeTargetFramework is not null
                    && _projectsByKey.TryGetValue(ProjectKey(referencedProjectFileName, activeTargetFramework), out var activeId))
                {
                    return activeId;
                }

                return _projectsByKey.TryGetValue(ProjectKey(referencedProjectFileName, null), out var id) ? id : null;
            }
        }

        void AddDocument(RoslynProjectId projectId, string projectFileName, DocumentId documentId, string text, string tfmKey)
        {
            // UpsertDocumentAsync (file opened) and LoadProjectDocumentsAsync (project loaded) can
            // race: both may pass their variants check and both then call AddDocument, leaving two
            // workspace documents for one file - which silently doubles every reference count.
            // Make this idempotent: if the file is already registered for this project, just push
            // the latest text into the existing document instead of creating a second one.
            lock (_documentLock)
            {
                AddDocumentCore(projectId, projectFileName, documentId, text, tfmKey);
            }
        }

        void AddDocumentCore(RoslynProjectId projectId, string projectFileName, DocumentId documentId, string text, string tfmKey)
        {
            var sourceText = SourceText.From(text);
            var existing = _workspace.CurrentSolution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => d.Project.Id == projectId
                    && string.Equals(d.FilePath, documentId.FileName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                _workspace.TryApplyChanges(_workspace.CurrentSolution.WithDocumentText(existing.Id, sourceText));
                if (!_documentVariantsByTfm.TryGetValue(documentId, out var existingVariants))
                {
                    existingVariants = new Dictionary<string, RoslynDocumentId>();
                    _documentVariantsByTfm[documentId] = existingVariants;
                }

                existingVariants[tfmKey] = existing.Id;
                _documentProjectFileNames[documentId] = projectFileName;
                return;
            }

            var documentInfo = DocumentInfo.Create(
                RoslynDocumentId.CreateNewId(projectId, documentId.FileName + "|" + tfmKey),
                Path.GetFileName(documentId.FileName),
                filePath: documentId.FileName,
                loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create())));

            _workspace.AddDocument(documentInfo);
            if (!_documentVariantsByTfm.TryGetValue(documentId, out var variants))
            {
                variants = new Dictionary<string, RoslynDocumentId>();
                _documentVariantsByTfm[documentId] = variants;
            }

            variants[tfmKey] = documentInfo.Id;
            _documentProjectFileNames[documentId] = projectFileName;
        }

        void RemoveProjectDocumentVariantsMissingFromSnapshot(LanguageServiceProjectSnapshot projectSnapshot, string tfmKey)
        {
            var currentDocumentFileNames = new HashSet<string>(projectSnapshot.DocumentFileNames, StringComparer.OrdinalIgnoreCase);
            List<DocumentId> stale;
            lock (_documentLock)
            {
                stale = _documentProjectFileNames
                    .Where(item => string.Equals(item.Value, projectSnapshot.ProjectFileName, StringComparison.OrdinalIgnoreCase)
                        && !currentDocumentFileNames.Contains(item.Key.FileName))
                    .Select(item => item.Key)
                    .ToList();
            }
            foreach (var documentId in stale)
            {
                RemoveDocumentVariant(documentId, tfmKey);
            }
        }

        void RemoveDocumentVariant(DocumentId documentId, string tfmKey)
        {
            lock (_documentLock)
            {
                if (!_documentVariantsByTfm.TryGetValue(documentId, out var variants) || !variants.TryGetValue(tfmKey, out var roslynDocumentId))
                    return;

                _workspace.TryApplyChanges(_workspace.CurrentSolution.RemoveDocument(roslynDocumentId));
                variants.Remove(tfmKey);
                if (variants.Count == 0)
                {
                    _documentVariantsByTfm.Remove(documentId);
                    _documentProjectFileNames.Remove(documentId);
                }
            }
        }

        static void CollectTypes(INamespaceOrTypeSymbol container, SyntaxTree syntaxTree, List<DocumentOutlineNode> results)
        {
            // Symbol-based (not syntax-based) so the same walk works for both C# and VB without
            // a per-language syntax tree visitor.
            foreach (var member in container.GetMembers())
            {
                if (member is INamespaceSymbol namespaceSymbol)
                {
                    CollectTypes(namespaceSymbol, syntaxTree, results);
                    continue;
                }

                if (member is not INamedTypeSymbol type || !type.Locations.Any(location => location.SourceTree == syntaxTree))
                    continue;

                var members = type.GetMembers()
                    .Where(m => !m.IsImplicitlyDeclared
                        && m is not INamedTypeSymbol
                        && IsOutlineMemberKind(m)
                        && m.Locations.Any(location => location.SourceTree == syntaxTree))
                    .Select(m => new DocumentOutlineNode(
                        FormatMemberName(m), m.Kind.ToString(), ToOutlineSpan(m), Array.Empty<DocumentOutlineNode>(),
                        ToOutlineExtentSpan(m, syntaxTree), ToOutlineAccessibility(m.DeclaredAccessibility),
                        ToMemberOverridability(m)))
                    .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                results.Add(new DocumentOutlineNode(
                    type.Name, type.TypeKind.ToString(), ToOutlineSpan(type), members,
                    ToOutlineExtentSpan(type, syntaxTree), ToOutlineAccessibility(type.DeclaredAccessibility),
                    type.TypeKind == TypeKind.Interface ? SymbolOverridability.Implementable : SymbolOverridability.None));

                // Nested types are listed as their own top-level entries (flat list), not nested
                // under their declaring type — matches how a class/member navigation bar reads.
                CollectTypes(type, syntaxTree, results);
            }
        }

        // doc/technotes/openlens.md §17.3: interface member/abstract member -> implementations;
        // virtual member or non-sealed override -> overrides; anything else -> no lens.
        static SymbolOverridability ToMemberOverridability(ISymbol member)
        {
            if (member.ContainingType?.TypeKind == TypeKind.Interface)
                return SymbolOverridability.Implementable;
            if (member.IsAbstract)
                return SymbolOverridability.Implementable;
            if (member.IsSealed)
                return SymbolOverridability.None;
            if (member.IsOverride || member.IsVirtual)
                return SymbolOverridability.Overridable;
            return SymbolOverridability.None;
        }

        static string? ToOutlineAccessibility(RoslynAccessibility accessibility)
        {
            return accessibility switch
            {
                RoslynAccessibility.Public => "Public",
                RoslynAccessibility.Private => "Private",
                RoslynAccessibility.Protected or RoslynAccessibility.ProtectedOrInternal or RoslynAccessibility.ProtectedAndInternal => "Protected",
                RoslynAccessibility.Internal => "Internal",
                _ => null
            };
        }

        static bool IsOutlineMemberKind(ISymbol member)
        {
            return member is not IMethodSymbol method
                || method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.ExplicitInterfaceImplementation;
        }

        static string FormatMemberName(ISymbol member)
        {
            return member switch
            {
                IMethodSymbol { MethodKind: MethodKind.Constructor } method => $"{method.ContainingType.Name}({FormatParameters(method)})",
                IMethodSymbol method => $"{method.Name}({FormatParameters(method)})",
                _ => member.Name
            };
        }

        static string FormatParameters(IMethodSymbol method)
        {
            return string.Join(", ", method.Parameters.Select(parameter => parameter.Type.Name));
        }

        static TextSpan ToOutlineSpan(ISymbol symbol)
        {
            var location = symbol.Locations.FirstOrDefault(l => l.IsInSource) ?? symbol.Locations[0];
            return ConvertLineSpan(location.GetLineSpan());
        }

        /// <summary>
        /// The full declaration span (e.g. the entire class/method body), for nav-bar caret
        /// containment — distinct from <see cref="ToOutlineSpan"/>'s name-token span, which is
        /// only good for "jump to this declaration".
        /// </summary>
        static TextSpan ToOutlineExtentSpan(ISymbol symbol, SyntaxTree syntaxTree)
        {
            var syntaxReference = symbol.DeclaringSyntaxReferences.FirstOrDefault(r => r.SyntaxTree == syntaxTree);
            if (syntaxReference is null)
                return ToOutlineSpan(symbol);

            return ConvertLineSpan(syntaxTree.GetLineSpan(syntaxReference.Span));
        }

        static string GetLanguage(string fileName)
        {
            return Path.GetExtension(fileName).Equals(".vb", StringComparison.OrdinalIgnoreCase)
                ? LanguageNames.VisualBasic
                : LanguageNames.CSharp;
        }

        static string ToRoslynLanguageName(string language)
        {
            return language.Equals(LanguageNames.VisualBasic, StringComparison.OrdinalIgnoreCase)
                || language.Equals("VB", StringComparison.OrdinalIgnoreCase)
                || language.Equals("Visual Basic", StringComparison.OrdinalIgnoreCase)
                ? LanguageNames.VisualBasic
                : LanguageNames.CSharp;
        }

        static CompilationOptions CreateCompilationOptions(string language, string? nullableContext = null)
        {
            if (language == LanguageNames.VisualBasic)
                return new Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

            var options = new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
            return string.Equals(nullableContext, "enable", StringComparison.OrdinalIgnoreCase)
                ? options.WithNullableContextOptions(NullableContextOptions.Enable)
                : options;
        }

        static ParseOptions CreateParseOptions(
            string language,
            string? languageVersion = null,
            IReadOnlyList<string>? preprocessorSymbols = null)
        {
            if (language == LanguageNames.VisualBasic)
                return Microsoft.CodeAnalysis.VisualBasic.VisualBasicParseOptions.Default;

            var options = Microsoft.CodeAnalysis.CSharp.CSharpParseOptions.Default;
            if (!string.IsNullOrWhiteSpace(languageVersion)
                && Microsoft.CodeAnalysis.CSharp.LanguageVersionFacts.TryParse(languageVersion, out var parsedVersion))
            {
                options = options.WithLanguageVersion(parsedVersion);
            }

            return preprocessorSymbols is { Count: > 0 }
                ? options.WithPreprocessorSymbols(preprocessorSymbols)
                : options;
        }

        static IEnumerable<MetadataReference> CreateDefaultMetadataReferences()
        {
            var assemblies = new[]
            {
                typeof(object).Assembly,
                typeof(Console).Assembly,
                typeof(Enumerable).Assembly,
                typeof(Task).Assembly,
                typeof(System.Runtime.GCSettings).Assembly
            };

            return assemblies
                .Where(assembly => !string.IsNullOrEmpty(assembly.Location))
                .Select(assembly => MetadataReference.CreateFromFile(assembly.Location));
        }

        static IEnumerable<MetadataReference> CreateMetadataReferences(IReadOnlyList<string> referenceFileNames)
        {
            return CreateDefaultMetadataReferences()
                .Concat(referenceFileNames
                    .Where(File.Exists)
                    .Select(fileName => MetadataReference.CreateFromFile(fileName)))
                .GroupBy(reference => reference.Display, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());
        }

        static async Task<CompletionItem> ConvertCompletionItemAsync(
            CompletionService completionService,
            Document document,
            RoslynCompletionItem item,
            CancellationToken cancellationToken)
        {
            var insertionText = item.Properties.TryGetValue("InsertionText", out var value)
                ? value
                : item.DisplayText;
            var description = await completionService.GetDescriptionAsync(document, item, cancellationToken);

            return new CompletionItem(
                item.DisplayText,
                insertionText,
                description?.Text ?? string.Empty,
                item.Tags.FirstOrDefault());
        }

        static LanguageDiagnostic ConvertDiagnostic(RoslynDiagnostic diagnostic)
        {
            return new LanguageDiagnostic(
                diagnostic.Id,
                diagnostic.GetMessage(),
                ConvertSeverity(diagnostic.Severity),
                ConvertLineSpan(diagnostic.Location.GetLineSpan()));
        }

        static NavigationTarget ConvertLocationToNavigationTarget(Location location)
        {
            var lineSpan = location.GetLineSpan();
            var span = ConvertLineSpan(lineSpan);
            return new NavigationTarget(
                lineSpan.Path,
                span.Start,
                span);
        }

        static TextEdit ConvertTextChange(SourceText sourceText, Microsoft.CodeAnalysis.Text.TextChange change)
        {
            return new TextEdit(
                ConvertLineSpan(sourceText.Lines.GetLinePositionSpan(change.Span)),
                change.NewText ?? string.Empty);
        }

        static DiagnosticSeverity ConvertSeverity(Microsoft.CodeAnalysis.DiagnosticSeverity severity)
        {
            return severity switch
            {
                Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden => DiagnosticSeverity.Hidden,
                Microsoft.CodeAnalysis.DiagnosticSeverity.Info => DiagnosticSeverity.Info,
                Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
                Microsoft.CodeAnalysis.DiagnosticSeverity.Error => DiagnosticSeverity.Error,
                _ => DiagnosticSeverity.Info
            };
        }

        static async Task<TextSpan> ConvertSpanAsync(Document document, RoslynTextSpan span, CancellationToken cancellationToken)
        {
            var text = await document.GetTextAsync(cancellationToken);
            return ConvertLineSpan(text.Lines.GetLinePositionSpan(span));
        }

        static TextSpan ConvertLineSpan(FileLinePositionSpan span)
        {
            return ConvertLineSpan(span.Span);
        }

        static TextSpan ConvertLineSpan(LinePositionSpan span)
        {
            return new TextSpan(
                new TextPosition(span.Start.Line + 1, span.Start.Character + 1),
                new TextPosition(span.End.Line + 1, span.End.Character + 1));
        }

        static RoslynTextSpan ToRoslynSpan(SourceText sourceText, TextSpan span)
        {
            var start = sourceText.Lines.GetPosition(new LinePosition(span.Start.Line - 1, span.Start.Column - 1));
            var end = sourceText.Lines.GetPosition(new LinePosition(span.End.Line - 1, span.End.Column - 1));
            return RoslynTextSpan.FromBounds(start, end);
        }
    }
}
