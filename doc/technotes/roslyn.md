# VB.NET Language Support: Extend the Roslyn Bridge, Retire NRefactory Further

## Why this exists

`doc/technotes/csharp-roslyn.md` already replaced NRefactory-for-C# (which wasn't even in the MVP
build) with a real, Roslyn-backed `IParser`/completion/go-to-definition/etc. pipeline
(`src/Main/Base/Project/Roslyn/`, namespace `ICSharpCode.SharpDevelop.Roslyn`). VB.NET today has
**no language service at all** — `src/AddIns/BackendBindings/VBBinding/Project/` (`VBBinding.vbproj`,
which *is* in the MVP build, unlike the old `CSharpBinding`) only carries project-system glue
(`VBProject.vb`, `VBProjectBinding.vb`, `VbcEncodingFixingLogger.vb`, a few options-panel XAML
files) — zero `IParser`, zero `CodeCompletionBinding`, zero NRefactory.VB, zero
`Microsoft.CodeAnalysis.VisualBasic`. VB.NET projects load and build; they get no parsing,
completion, go-to-definition, or code model whatsoever.

This is good news for the plan: there is no legacy VB language-service code to migrate *away*
from — this is pure additive work, modeled directly on the C# Roslyn bridge that already exists and
already works.

## Precedent: UnoDevelop already did this, for both languages, in one implementation

`/Users/lextm/uno-tools/UnoDevelop/src/Main/Base/Src/LanguageServices/Roslyn/CSharpVBLanguageService.cs`
(see its `docs/language-services.md`) backs **both** `.cs` and `.vb` through a single
`AdhocWorkspace(MefHostServices.DefaultHost)`, branching purely on file extension:

```csharp
// language selection
Path.GetExtension(fileName).Equals(".vb", StringComparison.OrdinalIgnoreCase)
    ? LanguageNames.VisualBasic
    : LanguageNames.CSharp;

// per-language compilation/parse options
if (language == LanguageNames.VisualBasic)
    return new Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
...
if (language == LanguageNames.VisualBasic)
    return Microsoft.CodeAnalysis.VisualBasic.VisualBasicParseOptions.Default;
```

Every feature (completion, quick info, diagnostics, go-to-definition, find-references, format,
rename, code actions) is implemented once, generically, against `Microsoft.CodeAnalysis` APIs
(`CompletionService`, `QuickInfoService`, `SymbolFinder`, `Formatter`, `Renamer`) — none of it is
C#-specific, so VB gets all of it "for free" through the same code paths, the same way real Visual
Studio's Roslyn workspace hosts both languages side by side. This is the template to copy — not a
symmetrical "build a VB analog of every C# file," but "generalize the existing C# infrastructure to
be language-parameterized, the way UnoDevelop already proved works."

## What NOT to do

- **Do not implement a VB analog of `ICSharpCode.TypeSystem.Abstractions`.** Per
  `csharp-roslyn.md`'s explicit, already-settled decision, that abstraction is a dead-end mock and
  C#'s own Roslyn bridge deliberately bypasses it except for a thin adapter shim
  (`RoslynEntityAdapters.cs`) serving ~10 legacy `ResolveResult`-shaped consumers. VB should reuse
  that *same* shim (it's already language-agnostic — `RoslynTypeDefinitionAdapter` etc. wrap
  `ISymbol`/`Compilation`, which Roslyn gives identically for C# and VB compilations) rather than
  growing a second one.
- **Do not resurrect `ICSharpCode.NRefactory.VB`** (it doesn't exist in this codebase — grep found
  zero references) or port MonoDevelop's/`externals/monodevelop`'s vendored `VBNetBinding` add-in.
  That add-in is Xwt/GTK-oriented and unrelated to this codebase's WPF editor infrastructure; it is
  not wired into OpenDevelop and shouldn't be.
- **Do not touch `src/Libraries/NRefactory/*`'s stub `.csproj` files.** Confirmed in
  `csharp-roslyn.md`: ~40 other, not-yet-MVP-ported add-ins still hold real `<ProjectReference>`s to
  those paths (`CSharpBinding`, `Debugger.Core`, `XamlBinding`, `PackageManagement`, `UnitTesting`,
  `ResourceToolkit`, `ILSpyAddIn`, etc.). Deleting them breaks those add-ins' ability to ever build
  again, for no benefit to this plan — same reasoning as before, still holds.
- **`ICSharpCode.Decompiler`/`externals/ilspy`'s use of `ICSharpCode.NRefactory.CSharp`-descended AST
  types** (decompiler output formatting, not an IDE language service) is unrelated to this plan and
  out of scope.

## Status: Phases 0-2 DONE this session; two general (non-VB-specific) bugs found and fixed along the way

Implemented Phases 0-2 below essentially as planned. `RoslynParser`/`RoslynCompilation`/
`RoslynUnresolvedFile`/`RoslynEntityAdapters`/`RoslynCodeCompletionBinding` needed **zero changes** -
confirming the plan's central bet. Only `RoslynWorkspaceHelper.cs` needed editing:
`GetSolution()`'s project filter now accepts `.vbproj` alongside `.csproj`; `EnsureProject`/
`SyncCompilationOptions` branch on `IsVBProject(project)` to pick
`VisualBasicCompilationOptions`/`VisualBasicParseOptions` (VB's `PreprocessorSymbols` are
`KeyValuePair<string,object>`, not bare names like C#'s - mapped `name → true`) vs. the existing C#
path; added `Microsoft.CodeAnalysis.VisualBasic.Workspaces` (same `5.3.0` version already pinned for
the C# packages) to `ICSharpCode.SharpDevelop.csproj`. `RoslynParser.CanParse` now accepts `.vb` too.
Registered both `Parser id="VB-Roslyn"` and `CodeCompletionBinding id="VB-Roslyn"` in
`VBBinding.addin`, reusing the exact same classes `AvalonEdit.AddIn.addin` registers for C#.

New fixture: `tests/fixtures/VBFixture/` (`VBFixture.vbproj`, SDK-style, `Class1.vb`) +
`OpenDevelopAppFixture.VBFixtureSolutionPath` + `tests/OpenDevelop.IntegrationTests/VBBindingTests.cs`
(5 tests: addin loads, solution opens, solution tree shows the source file node, file opens in
AvalonEdit *and* `RoslynWorkspaceHelper.FindDocument` returns a real `Document` with
`Project.Language == "Visual Basic"`, project builds via `vbc`). New DevFlow action
`od.parser.status(fileName)` checks `RoslynWorkspaceHelper.FindDocument` specifically - **not**
`SD.ParserService.GetCompilationForFile`, which for any project-owned file (C# or VB) still routes
through the old `IProject.ProjectContent` mock (`SharpDevelopSolutionSnapshot.GetCompilation`), never
through `RoslynParser` at all; that's a separate, pre-existing gap, not something this session's
work touches or needs to touch (none of the real consumers - GoToDefinition, completion, etc. - go
through `GetCompilationForFile` either, they all call `RoslynWorkspaceHelper` directly).

**Two real, general bugs found while verifying against the actual running app (not by reading code) -
both affect C# users too, just never surfaced because the MVP-build C# path (CPS-based) doesn't
route through the legacy `CompilableProject`/`MSBuildBasedProject` machinery VB and F# still use:**

1. **False "Project Upgrade" wizard for any SDK-style project.**
   `MSBuildBasedProject.MinimumSolutionVersion` (`Src/Project/MSBuildBasedProject.cs`) treated an
   empty `ToolsVersion` as "VS2005" - correct for genuinely ancient projects, but SDK-style projects
   (`<Project Sdk="...">`) never set `ToolsVersion` at all, so *every* SDK-style VB or F# project
   was misidentified as needing an upgrade (`CompilableProject.UpgradeDesired =>
   MinimumSolutionVersion < SolutionFormatVersion.VS2010` was always true). Reproduced live: opening
   the VB fixture opened an `UpgradeViewContent` tab instead of/alongside the actual file; the same
   thing happened opening the F# fixture once cross-checked. **Fix**: check the already-existing
   `IsSdkStyleProject` property first and return `SolutionFormatVersion.VS2012` (the highest defined
   value) for any SDK-style project, bypassing the `ToolsVersion` heuristic entirely.
2. **Solution Explorer showed no source file node at all for `.vb`/`.fs` (SDK-style) projects.**
   `ProjectDisplayItems.IsSupportedProjectItemPath` (`Src/Services/ProjectDisplayItems.cs`) - the
   extension allowlist gating which evaluated MSBuild items become tree nodes for SDK-style projects
   - was a hardcoded C#-only list (`.cs`, `.xaml`, `.csproj`, `.props`, `.targets`, `.json`, `.md`,
   `.txt`, `.resx`, `.xml`), verbatim from its stated origin (UnoDevelop's own C#-only
   `UnoProjectService.cs`, see the file's header comment). `.vb`/`.fs`/`.vbproj`/`.fsproj` were
   simply never added, so their `Compile` items always failed the extension check and got silently
   dropped, even though `GetEvaluatedProjectDisplayItems` itself was already fully language-neutral.
   **Fix**: added `.vb`/`.fs`/`.vbproj`/`.fsproj` to the list. Also fixed the `od.solution-tree`
   DevFlow action itself (`OpenDevelopDevFlowActions.cs`), which had an independent, narrower version
   of the same bug (`p.Items.OfType<FileProjectItem>()` - the *raw*, unevaluated item snapshot, which
   is always empty for SDK-style implicit-glob items) - now delegates to
   `ProjectDisplayItems.GetProjectDisplayItems`, the same source Solution Explorer's own tree uses.

Also fixed two pre-existing, unrelated test bugs surfaced while investigating (both in
`FSharpBindingTests.cs`/mirrored in the new `VBBindingTests.cs`): `Assert.True(result["success"],
$"...{result["error"]}")` throws `KeyNotFoundException` instead of a useful assertion message
whenever `od.build-solution` succeeds, since its JSON only has an `error` field for the early-exit
(no-solution/no-project) cases - `success` itself is unconditionally `true` once a build actually
runs, regardless of whether the build had errors. Both tests now assert
`result.GetProperty("result").GetString() == "Success"` instead, the field that actually reflects
build success/failure.

**Phase 3 audit, continued (second pass):**

- **Icon mapping for VB-specific symbol kinds — verified fine, no change needed.**
  `RoslynSymbolIcons.GetCompletionImage` already has a `TypeKind.Module` case (→
  `CompletionImage.StaticClass`) sitting right next to `Interface`/`Struct`/`Enum`/`Delegate` - VB
  `Module` was apparently already anticipated when this switch was written, not something added for
  C# only.
- **VB-aware `IFormattingStrategy` — verified fine, no crash, but no VB-aware smart indent either.**
  `VBBinding.addin` registers no `/SharpDevelop/Workbench/LanguageBindings` entry at all (confirmed
  via grep - only `ICSharpCode.SharpDevelop.addin`'s `CSharp` entry exists). Unlike the C# stub bug
  `csharp-roslyn.md` Phase 0 hit (a *registered* binding whose `FormattingStrategy` returned `null`
  and crashed `OptionControlledIndentationStrategy`), a `.vb` file has *no* binding at all, so
  `LanguageBindingService` falls back to `DefaultLanguageBinding.DefaultInstance`, which *does*
  supply a non-null `IFormattingStrategy` (`DefaultFormattingStrategy.DefaultInstance`) - confirmed
  by `VBBindingTests`'s `OpenVBFile_DisplaysInAvalonEditAndParses` passing with no crash. So this is
  safe, just generic (no VB-aware `Sub`/`End Sub` block-aware indent) - a real but non-crashing gap,
  matching the doc's own "VB-specific refactorings out of scope" non-goal in spirit; not fixed this
  session.
- **VB snippet support — not verified, left as a known smaller gap** (per the original plan's own
  wording). `CodeSnippet.GetCurrentClass()` (`${ClassName}`) is language-neutral via
  `RoslynWorkspaceHelper`, so that one snippet variable works for VB automatically; whether
  OpenDevelop's snippet *library* itself ships any VB-syntax snippet definitions (vs. only C#-shaped
  ones) wasn't checked.
- **Cross-language go-to-definition** (the "ideally" item in the original Acceptance Bar) - not
  attempted this session.

## Original phase plan (mirrors `csharp-roslyn.md`'s numbering/shape, VB-scoped)

### Phase 0: Generalize `RoslynWorkspaceHelper` to be multi-language

Currently `RoslynWorkspaceHelper` (`src/Main/Base/Project/Roslyn/RoslynWorkspaceHelper.cs`) builds
each Roslyn `Project` implicitly as C# (`ProjectInfo.Create(..., LanguageNames.CSharp, ...)` or
equivalent — confirm exact call site before editing). Needs:

- A `GetLanguageForFile(string fileName)` helper (`.vb` → `LanguageNames.VisualBasic`, else
  `LanguageNames.CSharp`), used when constructing each project's `ProjectInfo` and when deciding
  which documents belong to which Roslyn project — **but note**: OpenDevelop's `IProject` is
  per-`.csproj`/`.vbproj`, one language per project already (unlike a hypothetical mixed-language
  project), so this is actually simpler than UnoDevelop's per-file branching: the project's own file
  extension/`.vbproj` vs `.csproj` type already tells you the language for the *whole* project. Only
  need to pick `VisualBasicCompilationOptions`/`VisualBasicParseOptions.Default` vs. the C#
  equivalents once per project, not per file.
- Add `Microsoft.CodeAnalysis.VisualBasic.Workspaces` (pulls in `Microsoft.CodeAnalysis.VisualBasic`)
  alongside the existing `Microsoft.CodeAnalysis.CSharp.Workspaces` package reference in
  `src/Main/Base/Project/ICSharpCode.SharpDevelop.csproj` (verify version match against whatever
  `Microsoft.CodeAnalysis.CSharp.Workspaces` version is already pinned there — they must be the same
  Roslyn release).
- `SyncProject`'s source-file enumeration currently likely filters on `.cs` (`ItemType.Compile` items
  whose `.FileName` ends `.cs`, or similar) — generalize to accept `.vb` too when the owning
  `IProject` is a `VBProject`. Confirm exact filter before editing (read the method fully first).
- Verify `IProject.GetItemsOfType(ItemType.Compile)`/`ResolveAssemblyReferences` behave identically
  for `VBProject` as for the C# `MSBuildBasedProject` path — `VBProject.vb` should already support
  this generically (it's an `MSBuildBasedProject` subclass per the existing SharpDevelop project
  model), but confirm rather than assume.

### Phase 1: Register a VB `IParser`

Register `RoslynParser` (`src/Main/Base/Project/Roslyn/RoslynParser.cs`) — or a thin
language-parameterized wrapper around it — for `.vb` at `/SharpDevelop/Parser`
(`supportedfilenamepattern="\.vb$"`) in `VBBinding.addin` (mirroring how
`AvalonEdit.AddIn.addin` registers it for `.cs`). `RoslynParser`'s own logic
(`SD.ParserService.Resolve`/`GetCompilationForFile`/`Parse`) is already Roslyn-`Compilation`/
`SemanticModel`-based, not C#-syntax-specific — confirm it doesn't call any
`Microsoft.CodeAnalysis.CSharp`-specific API (e.g. `CSharpSyntaxTree`/`SyntaxKind.CSharp*`) before
assuming it's a drop-in; if it does, those spots need a language-neutral (`SyntaxNode`/generic
`SemanticModel`) equivalent.

The adapter classes (`RoslynCompilation.cs`, `RoslynUnresolvedFile.cs`, `RoslynEntityAdapters.cs`)
should need **no changes** — they wrap `ISymbol`/`Compilation`, which are language-neutral Roslyn
types already. This is the biggest practical win of the "Roslyn hosts both" approach: the ~10
in-tree MVP consumers (`GoToDefinition`, `DefinitionViewPad`, `DeclaringTypeSubMenuBuilder`,
`SymbolTypeAtCaretConditionEvaluator`, `EditorRefactoringContext`, `CodeEditorView`,
`QuickClassBrowser`, `CodeSnippet`, `FindBaseClasses`, `FindDerivedClassesOrOverrides`) should work
for VB source files with **zero code changes**, purely because they already talk to
`Microsoft.CodeAnalysis` symbols, not to `Microsoft.CodeAnalysis.CSharp` syntax.

### Phase 2: Code completion

Register `RoslynCodeCompletionBinding` for `.vb` (`CodeCompletionBinding id="VB-Roslyn"
extensions=".vb"` in `VBBinding.addin`, alongside/reusing the C# one's class). Confirm
`CompletionService.GetCompletionsAsync`/`GetChangeAsync` are language-neutral in the same way (they
are, in real Roslyn — `CompletionService.GetService(document)` picks the right language's
`CompletionProvider`s automatically based on `document.Project.Language`) before assuming zero
changes needed; the trigger-character logic (letter/`_`/`.`/Ctrl+Space) may need a VB-specific
addition (`.` still applies, but VB's `Dim x As |` completion trigger differs from C#'s pattern —
verify against real VB typing behavior, not just assumption).

### Phase 3: Verify the "free" consumers, fix what's actually C#-flavored

Even though the adapters are language-neutral, some of the ~10 consumer files or their surrounding
UI may have baked-in C#-flavored assumptions worth auditing (not fixing blind — confirm each is
actually broken before touching it):

- `QuickClassBrowser.cs` — walks `INamedTypeSymbol`/`ISymbol`; check its "kind" icon mapping
  (`RoslynSymbolIcons`) covers VB-specific symbol kinds (e.g. VB `Module` vs. C# `static class`)
  reasonably, not just C# ones.
- `CodeSnippet.GetCurrentClass()` (`${ClassName}` snippet variable) — VB has its own snippet XML
  format historically; confirm OpenDevelop's snippet system already supports `.vb`-targeted
  snippets, or note it as a separate, smaller gap.
- `DefaultFormattingStrategy`/indentation — VB's block structure (`End Sub`/`End If`/no braces) needs
  its own `IFormattingStrategy`, not the C# one `CSharpLanguageBinding` stub currently falls back to
  (per `csharp-roslyn.md` Phase 0's bug #2) — check what `VBProjectBinding`'s language binding
  currently returns for `FormattingStrategy` and whether it needs a VB-aware implementation or can
  reuse `Microsoft.CodeAnalysis.VisualBasic`'s own `Formatter` (already available once Phase 0/2 pull
  in `VisualBasicCompilationOptions`/completion, since `Formatter.FormatAsync` is used elsewhere in
  UnoDevelop's `CSharpVBLanguageService` generically).

### Non-goals (mirroring `csharp-roslyn.md`'s own non-goals, VB-specific notes)

- **Multi-TFM VB projects** — same limitation as C#, for the same root cause
  (`MSBuildBasedProject` evaluates one TFM, full stop). Not fixable without a project-system change;
  out of scope here, same as the existing C# non-goal.
- **VB-specific refactorings/code actions** (e.g. VB's `My.*` namespace helpers, `With` blocks) —
  Roslyn's built-in VB code-fix/refactoring providers should mostly work for free through
  `CompletionService`/whatever code-action host Phase 2/3 wires up, but exhaustively cataloging VB
  language-feature parity is out of scope for "give VB a working language service at all."
- **"Convert C# ↔ VB" tooling** — confirmed no existing scaffolding for this anywhere in the repo
  (grep found nothing). Not part of this plan; a real, separate feature if ever wanted (Roslyn's own
  `Microsoft.CodeAnalysis.CSharp`/`.VisualBasic` syntax trees make a converter *feasible*, but it's
  additive tooling, not a language-service gap).

## Acceptance bar

Modeled on `csharp-roslyn.md`'s own verification style: full `OpenDevelop.Mvp.slnx` builds with 0
errors after each phase; then a concrete, driven check — open a `.vbproj` fixture (or add one to
`tests/fixtures/`, mirroring the existing `.csproj` fixtures used by `NuGetAddInTests`/others),
confirm `SD.ParserService.GetCompilationForFile` returns non-null for a `.vb` file, confirm
Ctrl+Space produces real completion items, and confirm Ctrl+Click go-to-definition navigates
correctly within a `.vb` file and (ideally) across a C#↔VB project reference — the latter being the
real payoff of "one shared workspace, two languages," and worth calling success/failure on
explicitly rather than assuming it works.

## Unified F# and XAML LSP semantic highlighting

F# and XAML use the same LSP client infrastructure. `LspServiceManager` owns one
`LspLanguageService` per language/workspace root; completion and editor semantic highlighting must
obtain the service from that manager so they share the server process and synchronized document.
Language-specific code is limited to `LspServerRegistry` launch specifications.

XAML uses the vendored WPF XAML language server under `externals/vscode-wpf`. That server advertises
`semanticTokensProvider` and implements `textDocument/semanticTokens/full`. F# uses
fsautocomplete, pinned as a repository-local dotnet tool in `.config/dotnet-tools.json`; run
`dotnet tool restore` after cloning. It is launched with `dotnet tool run fsautocomplete --`, and
the workspace comes from the standard LSP `initialize.rootUri` rather than obsolete command-line
workspace switches.

`LspLanguageService` advertises standard semantic token types, captures the server's token legend,
requests full relative tokens, and converts the delta-encoded token stream to OpenDevelop text
spans. `LspSemanticColorizer` debounces AvalonEdit document changes, synchronizes the full buffer,
and overlays semantic foreground colors for `.fs`, `.fsi`, and `.xaml`. Lexical highlighting stays
installed underneath it. If a server is missing, does not advertise semantic tokens, or a request
fails, the editor safely retains lexical highlighting.
