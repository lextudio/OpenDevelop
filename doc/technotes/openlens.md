# OpenDevelop OpenLens Architecture and Implementation Plan

**Status:** revised design proposal  
**Date:** 2026-08-02  
**Target:** OpenDevelop / AvalonEdit  
**Related documents:** `doc/technotes/language-services.md`, `doc/technotes/csharp-vb-binding.md`, `doc/technotes/roslyn.md`

## 1. Executive summary

The original plan correctly separated OpenLens into data and rendering, and correctly required the feature to use `ILanguageService` instead of reaching directly into `RoslynWorkspaceHelper`.

However, that plan is still too narrow. It treats OpenLens as a fixed “references and implementations count” feature. Visual Studio and Visual Studio Code use OpenLens as an extensible host for multiple independent indicators and commands. A complete OpenDevelop design therefore needs at least five parts:

1. **Anchor discovery**: find declarations or other source ranges that can host lenses.
2. **Provider composition**: allow language bindings, testing, Git, coverage, and other AddIns to contribute lenses.
3. **Lazy resolution**: discover cheap placeholders first, then calculate expensive titles and commands only when needed.
4. **Editor presentation**: reserve vertical space, render multiple lenses, support mouse and keyboard interaction, and open a reusable results UI.
5. **Refresh and caching**: invalidate only the affected providers and documents, cancel stale work, and avoid N+1 workspace queries.

The most important correction is this:

> Existing point-based operations such as `FindReferencesAsync(documentId, offset)` are useful when a resolved lens is clicked, but they are not a sufficient OpenLens data contract.

OpenLens first needs to discover every eligible declaration in a document. Calling reference and hierarchy APIs independently for every declaration creates an expensive N+1 workflow and gives non-language AddIns no clean way to participate.

OpenDevelop should introduce a first-class, backend-neutral OpenLens provider model that resembles the VS Code and Language Server Protocol split between `textDocument/openLens` and `openLens/resolve`.

---

## 2. Product goals

The first production version should support:

- C# and Visual Basic declaration lenses.
- Reference counts.
- Implementation, override, or derived-type counts where meaningful.
- Clickable result lists with preview and navigation.
- Per-provider settings.
- Correct AddIn enable/disable behavior.
- Incremental refresh after edits, project changes, builds, test runs, and Git changes.
- A rendering model that does not overlap source code.
- A provider model that can later support tests, Git history, and coverage without changing the editor architecture.

The design should not make OpenLens dependent on Roslyn. C# and VB will be the first rich implementations, but LSP-backed languages and other AddIns should be able to contribute through the same editor-facing contract.

---

## 3. What Visual Studio and VS Code imply for the design

Visual Studio exposes several OpenLens indicator families, including references, source history and authors, linked work items and reviews, and associated unit tests. It also lets users choose indicators, interact with result lists, navigate to items, use keyboard access, customize appearance, and dock the details view.

VS Code exposes OpenLens as a language feature rather than a fixed reference-count widget. A provider first returns OpenLens items for a document and may resolve each item later to attach its command. The Language Server Protocol follows the same two-stage model with `textDocument/openLens`, optional `openLens/resolve`, and a refresh notification.

The useful lesson for OpenDevelop is not to duplicate every Visual Studio enterprise integration. It is to preserve the same architecture:

```text
cheap discovery
    ↓
visible unresolved lenses
    ↓
lazy resolution
    ↓
command execution
    ↓
result list, navigation, or direct action
```

This architecture allows one declaration line to contain independent contributions such as:

```text
12 references | 3 implementations | Run Test | 87% covered | Alex, 4 days ago
```

Each contribution can come from a different enabled AddIn.

---

## 4. Current draft: what is correct

The original draft makes several sound decisions:

- It separates data from editor rendering.
- It rejects direct long-term use of `RoslynWorkspaceHelper`.
- It routes language operations through `ILanguageService`.
- It recognizes that expensive queries must be asynchronous, cancellable, and cached.
- It considers declaration scope and visual noise.
- It preserves graceful behavior for language backends that do not implement every operation.
- It identifies `ChangeMarkerMargin` and AvalonEdit rendering hooks as useful precedents.

Those decisions should remain.

---

## 5. Current draft: what must change

### 5.1 OpenLens is a provider platform, not one feature

References and implementations should be the first providers, not hardcoded fields of a single OpenLens object.

The editor must not know how references, tests, Git history, or coverage are calculated. It should compose provider results and render commands.

### 5.2 Existing point-based language methods are not enough

The current plan says no new contract surface is needed because it can call:

```text
FindReferencesAsync(documentId, offset)
GetDerivedSymbolsAsync(documentId, offset)
GetBaseSymbolsAsync(documentId, offset)
```

That skips the first and most important question:

```text
Which declarations in this document should receive OpenLens?
```

A OpenLens host needs a document-level discovery call. Otherwise it must parse the language itself, depend on Roslyn types, or issue queries at arbitrary offsets.

It also causes N+1 work:

```text
1 document-symbol scan
N reference searches
N hierarchy searches
```

For a file with 80 eligible declarations, that can become 160 expensive solution-level operations before the user clicks anything.

### 5.3 “The backend will cache it” is not a complete performance strategy

The backend should cache semantic operations, but OpenLens still needs its own scheduling policy:

- Which declarations are discovered immediately?
- Which items are resolved only when visible?
- How many resolutions may run concurrently?
- What happens when the user scrolls quickly?
- How are stale document versions rejected?
- Which provider refreshes after a build versus after a Git change?
- How are counts reused between split editor views?

These policies belong to a OpenLens service, not to individual renderers.

### 5.4 An overlay-only renderer is not an acceptable final design

Drawing an annotation just above the source line without reserving space can overlap the preceding line, selection, diagnostics, or inline UI. It can also produce incorrect scrolling, hit testing, and accessibility behavior.

An adorner overlay is acceptable for a short proof of concept. The production path should add a first-class AvalonEdit block-adornment or reserved-line facility.

### 5.5 Result interaction is missing

The current plan mentions clicking but does not define:

- a direct command versus a list command;
- reference grouping by project and file;
- preview;
- keyboard focus;
- navigation;
- refresh;
- cancellation;
- empty and error states;
- whether the result view is a popup or dockable pad.

These need to be designed before provider APIs are finalized.

---

## 6. Recommended architecture

```text
Enabled AddIns
    ├── CSharpBinding
    ├── VBBinding
    ├── UnitTesting
    ├── Git
    └── CodeCoverage
             │
             ▼
      OpenLensProviderRegistry
             │
             ▼
        OpenLensService
    discovery / composition
    lazy resolution / cache
    refresh / cancellation
             │
             ▼
    AvalonEdit OpenLensPresenter
    reserved space / hit testing
    keyboard / theming
             │
             ▼
      command or details view
```

### Ownership rule

The OpenLens host and renderer may live in AvalonEdit AddIn, but individual lenses belong to the AddIn that provides the underlying capability.

Examples:

```text
CSharpBinding/VBBinding
    references, implementations, overrides, derived types

UnitTesting AddIn
    run, debug, test status, test duration

Git AddIn
    author, last change, history

CodeCoverage AddIn
    line or member coverage
```

Disabling an AddIn removes its provider registration and its lenses without disabling the entire OpenLens host.

---

## 7. Core data model

The editor-facing model must contain no Roslyn or LSP types.

```csharp
public readonly record struct OpenLensRange(
    int StartOffset,
    int Length);

public sealed record OpenLensAnchor(
    string AnchorId,
    DocumentId DocumentId,
    OpenLensRange Range,
    OpenLensAnchorKind Kind,
    string? DisplayName,
    string? SymbolKey,
    long DocumentVersion);

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
```

`AnchorId` must be stable enough to preserve resolved lenses while scrolling. For Roslyn-backed languages it may be derived from a symbol key plus declaration location. The editor must treat it as opaque.

A contributed lens:

```csharp
public sealed record OpenLensItem(
    string ProviderId,
    string LensId,
    string AnchorId,
    int Order,
    OpenLensPresentation Presentation,
    OpenLensCommand? Command,
    object? ResolveData,
    bool IsResolved);

public sealed record OpenLensPresentation(
    string Title,
    string? Tooltip = null,
    ImageSource? Icon = null,
    OpenLensSeverity Severity = OpenLensSeverity.Normal);

public sealed record OpenLensCommand(
    string CommandId,
    object? Argument = null);
```

The unresolved form may use a placeholder title such as:

```text
references
implementations
tests
history
```

or a subtle progress indicator. It should not display a misleading `0` before resolution.

---

## 8. Provider contracts

### 8.1 General provider contract

```csharp
public interface IOpenLensProvider
{
    string Id { get; }

    int Order { get; }

    bool CanHandle(OpenLensDocumentContext context);

    Task<IReadOnlyList<OpenLensItem>> ProvideAsync(
        OpenLensDocumentContext context,
        IReadOnlyList<OpenLensAnchor> anchors,
        CancellationToken cancellationToken);

    Task<OpenLensItem> ResolveAsync(
        OpenLensDocumentContext context,
        OpenLensItem item,
        CancellationToken cancellationToken);
}
```

`ProvideAsync` should be cheap. It decides which anchors receive this provider’s lens and returns unresolved or already-resolved items.

`ResolveAsync` performs expensive work and binds a final title and command.

Providers that already have cheap indexed data may return resolved items immediately.

### 8.2 Anchor provider contract

Anchor discovery is language-sensitive and should be separate from indicator contribution:

```csharp
public interface IOpenLensAnchorProvider
{
    string Id { get; }

    bool CanHandle(OpenLensDocumentContext context);

    Task<IReadOnlyList<OpenLensAnchor>> GetAnchorsAsync(
        OpenLensDocumentContext context,
        OpenLensRange? requestedRange,
        CancellationToken cancellationToken);
}
```

CSharpBinding and VBBinding should own their anchor providers.

An LSP-backed implementation can use `textDocument/documentSymbol` when native OpenLens is unavailable.

### 8.3 Native backend OpenLens

Some LSP servers already implement `textDocument/openLens`. OpenDevelop should not discard those results and recreate them from references.

Add an optional native-language operation:

```csharp
public interface ILanguageOpenLensService
{
    Task<IReadOnlyList<LanguageOpenLens>> GetOpenLensesAsync(
        DocumentId documentId,
        CancellationToken cancellationToken);

    Task<LanguageOpenLens> ResolveOpenLensAsync(
        DocumentId documentId,
        LanguageOpenLens item,
        CancellationToken cancellationToken);
}
```

The LSP backend maps this directly to OpenLens and OpenLens Resolve.

The C#/VB backend may implement the same operation using Roslyn anchors and language indicators.

The generic OpenLens host then combines native language lenses with AddIn-level providers such as Git and coverage.

---

## 9. Relationship with `ILanguageService`

There are two reasonable ways to evolve the current unified contract.

### Option A: add OpenLens methods to `ILanguageService`

```csharp
Task<IReadOnlyList<LanguageOpenLens>> GetOpenLensesAsync(...);
Task<LanguageOpenLens> ResolveOpenLensAsync(...);
```

Advantages:

- closely matches LSP;
- respects CSharpBinding/VBBinding lifecycle;
- no backend types cross the boundary;
- one lookup through `LanguageServiceRegistry`.

Disadvantage:

- expands an already broad interface.

### Option B: expose optional language capabilities

```csharp
var openLens = service.GetCapability<ILanguageOpenLensService>();
var anchors = service.GetCapability<IOpenLensAnchorProvider>();
```

Advantages:

- unsupported backends do not need empty implementations;
- capability discovery is explicit;
- avoids making every language service implement every future feature.

Recommended direction:

> Prefer optional capabilities if the current language-service architecture already supports capability discovery. Otherwise add OpenLens methods now, but keep their DTOs independent of Roslyn and LSP.

The old point-based reference and hierarchy methods should remain useful for explicit commands and as a fallback resolver, but they should not be the primary document-wide OpenLens protocol.

---

## 10. Provider set and rollout

### 10.1 Phase 1: language references

Applies to:

```text
types
methods
constructors
properties
indexers
events
```

Fields should be off by default because they can add substantial visual noise.

The resolved title should distinguish singular and plural:

```text
0 references
1 reference
12 references
```

Click opens the existing reference results UI, not a new OpenLens-only implementation.

The count must follow the same inclusion policy as the existing Find References command, especially around declarations, generated files, metadata, and cross-language references.

### 10.2 Phase 1: implementations and overrides

The term shown should match the anchor kind:

```text
interface/type      3 implementations
virtual method      2 overrides
abstract member     4 implementations
base type           5 derived types
```

Do not show a generic “implementation” count for declarations where the relation is meaningless.

Do not use `GetBaseSymbolsAsync` to calculate implementation count. Base symbols are better exposed as an optional direct-navigation lens such as:

```text
base: Stream
implements: IDisposable
```

That lens may navigate directly when only one result exists and show a list when there are several.

### 10.3 Phase 2: test lenses

Owned by UnitTesting AddIn.

For test methods:

```text
Run Test | Debug Test | Passed 120 ms
```

For production methods/types, “associated tests” requires a reliable source-to-test association model and should not be promised initially.

Refresh triggers:

```text
test discovery completed
test run started
test result changed
build completed, if discovery depends on build output
```

Direct actions such as Run and Debug should not require a details popup.

### 10.4 Phase 3: Git history lenses

Owned by Git AddIn.

Initial useful lens:

```text
Alex Chen, 4 days ago
```

Click opens member history or blame details.

Possible later forms:

```text
3 authors
8 changes
last changed by Alex
```

The provider needs a clear policy for mapping a declaration’s current span to Git history. Start with the declaration header line or current member range and document the limitations after edits or renames.

Refresh triggers:

```text
repository HEAD changed
index/worktree changed
file saved
branch switched
```

Do not refresh Git history on every keystroke.

### 10.5 Phase 3: coverage lens

Owned by CodeCoverage AddIn.

Examples:

```text
87% covered
12/14 lines covered
not covered
```

Coverage should be based on the latest known run and visually marked stale when source or binaries have changed.

Click opens the existing coverage details or highlights uncovered lines.

### 10.6 Optional later providers

```text
complexity or analyzer metrics
benchmark status
documentation links
linked issue or pull request
API compatibility status
generated-code provenance
```

These should not be part of the initial scope, but the provider architecture should not block them.

---

## 11. Scope and noise policy

Default anchors:

```text
Type
Method
Constructor
Property
Indexer
Event
```

Default exclusions:

```text
local functions
accessors
fields
enum members
anonymous functions
generated code
designer-generated partial files
declarations shorter than a configurable threshold, if needed
```

Settings should allow:

```text
Enable OpenLens
Enable references
Enable implementations/overrides
Enable tests
Enable Git history
Enable coverage
Show zero-reference lenses
Show fields
Show generated code
Maximum lenses per document
Resolve only visible lenses
```

Provider settings belong to the provider-owning AddIn. The host owns only general presentation and scheduling settings.

---

## 12. Scheduling and performance

### 12.1 Three stages

```text
Stage 1: anchor discovery for the document or requested range
Stage 2: cheap provider contribution
Stage 3: lazy resolution for visible or focused lenses
```

### 12.2 Visibility policy

The host should discover anchors for the whole open document when that operation is cheap, but resolve only:

- lenses in the visible viewport;
- a small prefetch window above and below it;
- the keyboard-focused lens;
- a lens explicitly requested by a command.

When scrolling stops, newly visible unresolved lenses enter the resolution queue.

### 12.3 Concurrency

Use a bounded queue, for example:

```text
maximum 2 expensive language resolutions
maximum 1 Git resolution
maximum 2 test/coverage resolutions
```

A global unbounded `Task.WhenAll` over all declarations is not acceptable.

### 12.4 Versioned cache

Cache key:

```text
DocumentId
DocumentVersion
AnchorId
ProviderId
ProviderDataVersion
```

`ProviderDataVersion` allows a test or Git provider to refresh without pretending the text document changed.

Examples:

```text
language provider data version -> solution/workspace version
test provider data version     -> discovery/result generation
Git provider data version      -> HEAD/index/worktree generation
coverage data version          -> coverage run identifier
```

### 12.5 Stale result handling

Every asynchronous result must be discarded if:

- the editor changed document;
- the AddIn/provider was disabled;
- the document version no longer matches;
- the anchor no longer exists;
- a newer provider generation exists;
- the editor session was disposed.

### 12.6 Negative caching

Cache empty results for a short generation so repeatedly scrolling over a zero-reference member does not repeat the same search.

Do not persist negative results across workspace changes.

### 12.7 Split views

Two editor views over the same document should share discovery and resolved data through `OpenLensService`, while each view owns its presentation objects and viewport subscriptions.

---

## 13. Refresh model

Follow the provider-refresh pattern rather than forcing a complete document refresh.

```csharp
public sealed class OpenLensRefreshEventArgs : EventArgs
{
    public string ProviderId { get; init; }
    public DocumentId? DocumentId { get; init; }
    public IReadOnlyCollection<string>? AnchorIds { get; init; }
}
```

Providers expose:

```csharp
event EventHandler<OpenLensRefreshEventArgs>? RefreshRequested;
```

Refresh levels:

```text
All documents for one provider
One document for one provider
Specific anchors for one provider
Anchor rediscovery for one document
```

Language text edits usually require anchor rediscovery and language-lens refresh.

A test result update usually refreshes only the affected test anchors.

A Git HEAD change refreshes Git lenses but not references or test status.

---

## 14. Rendering architecture

## 14.1 Proof of concept

An adorner or overlay can validate:

- provider composition;
- asynchronous resolution;
- command dispatch;
- theming;
- click behavior.

It should be explicitly labeled as a prototype because it does not solve source-line layout.

## 14.2 Production requirement: reserved vertical space

OpenLens must occupy layout space above the declaration line.

Because OpenDevelop vendors AvalonEdit, the best long-term solution is to add a small first-class block-adornment API rather than rely on overlapping WPF controls.

Possible abstraction:

```csharp
public interface IVisualLineBlockAdornment
{
    object Key { get; }
    int DocumentOffset { get; }
    double DesiredHeight { get; }

    UIElement CreateVisual();
}
```

`TextView` would:

1. associate block adornments with document lines;
2. include their height in visual-line measurement;
3. arrange the adornment above the source text;
4. keep line-number and other margins aligned with the source line;
5. include the height in scrolling and bring-into-view calculations;
6. recycle visuals when lines leave the viewport.

This is more invasive than a floating adorner, but it gives correct scrolling, hit testing, selection layout, zoom behavior, and accessibility.

## 14.3 Why `VisualLineElementGenerator` alone is insufficient

A normal element generator inserts content into the text flow at an offset. OpenLens is conceptually a block above a source line, not an inline token inside that line.

Trying to fake it with a zero-width inline element may create difficult behavior around:

```text
baseline and line height
selection
word wrapping
horizontal scrolling
caret navigation
line-number alignment
multiple lenses
folding
```

A dedicated block-adornment layer is clearer and more maintainable.

## 14.4 Composition on one anchor

Multiple providers should render in one row:

```text
12 references  |  3 overrides  |  Run Test  |  87% covered
```

Each item is a focusable command element.

The host controls separators and spacing. Providers supply title, tooltip, icon, command, order, and severity, but not custom arbitrary WPF trees in the first version.

Restricting provider visuals keeps layout, theming, and accessibility consistent.

## 14.5 Folding

When a declaration is folded:

- its OpenLens remains above the visible declaration header;
- lenses for declarations inside the folded body are not shown;
- expanding the fold should reuse cached items when versions still match.

## 14.6 Word wrap and zoom

The lens is anchored to the first visual line of the declaration and should not wrap with source text.

Use the editor zoom level for OpenLens font scaling, with a configurable relative size.

## 14.7 First line

The layout must support a lens above document line 1 without clipping.

---

## 15. Interaction design

### 15.1 Direct command

For commands such as:

```text
Run Test
Debug Test
Go to Base
```

clicking executes immediately.

### 15.2 Result command

For:

```text
12 references
3 implementations
8 changes
```

clicking opens a reusable OpenLens details view.

Recommended first implementation:

```text
lightweight popup anchored to the lens
    list of results
    keyboard navigation
    preview
    Enter or double-click to navigate
    Esc to close
```

The popup should offer a command to promote or dock the same result source into an existing pad, such as Search Results, References, Test Results, Git History, or Code Coverage.

Do not create separate result models when an existing OpenDevelop pad already represents the same information.

### 15.3 Single result

For one reference or one implementation, still open the list by default to keep behavior predictable, or make direct navigation a setting.

Direct navigation is more appropriate for “base type” than for “1 reference.”

### 15.4 Loading and errors

States:

```text
unresolved
resolving
resolved
empty
error
stale
```

An error should not replace source text with a stack trace or intrusive notification. Show a muted indicator with a tooltip and log the underlying error.

### 15.5 Keyboard

Minimum keyboard support:

```text
focus next/previous visible OpenLens row
move between lens items in a row
activate focused item
open context menu
dismiss popup
```

Avoid copying Visual Studio’s exact Alt-number shortcuts initially because lens count and provider order are dynamic. A general “Focus OpenLens” command plus arrow navigation is more robust.

### 15.6 Accessibility

Each lens item should expose:

```text
automation name
role as button or hyperlink
resolved title
provider name
loading state
keyboard focus
```

The entire row should not be one inaccessible drawing surface.

---

## 16. Theming and settings

Define editor theme resources rather than hardcoded brushes:

```text
OpenLensForeground
OpenLensForegroundHover
OpenLensForegroundDisabled
OpenLensForegroundError
OpenLensSeparatorForeground
OpenLensBackgroundHover
OpenLensFontFamily
OpenLensFontSizeRatio
```

General options:

```text
Enabled
Font size
Show icons
Show separators
Resolve visible only
Prefetch line count
Maximum lenses per document
```

Provider-specific options are contributed by their AddIns.

---

## 17. Language implementation details

## 17.1 C# and VB anchor discovery

The CSharpBinding and VBBinding providers should use their existing language-service backend.

For Roslyn-backed implementations, discover declaration symbols in one document operation and produce opaque anchor IDs.

The anchor provider should understand partial declarations. Each declaration location gets its own visible anchor, but the symbol identity may be shared for cached counts.

Recommended initial symbols:

```text
INamedTypeSymbol
IMethodSymbol excluding accessors and anonymous functions
IPropertySymbol
IEventSymbol
```

Constructors are methods but should retain a constructor anchor kind for provider filtering.

## 17.2 Reference count semantics

Decide and test:

- whether declaration locations count as references;
- whether implicit references count;
- whether references in XAML, Razor, resources, generated files, and metadata are included;
- whether duplicate linked-file locations are deduplicated;
- whether references in inactive code are included.

The OpenLens count and the clicked result list must use the same policy.

## 17.3 Implementation semantics

Map by symbol kind:

```text
interface/type        implementations
class/type            derived types, optional
abstract method       implementations/overrides
virtual method        overrides
interface member      implementations
non-virtual member    no implementation lens
```

Base relationships are a separate concept and should not be mixed into implementation count.

## 17.4 LSP languages

Preferred order:

1. Use native LSP OpenLens if the server advertises it.
2. If no native OpenLens exists, use document symbols as anchors.
3. Add generic reference lenses only when the backend supports references and the cost is acceptable.
4. Do not claim implementation support when the server lacks the capability.

Native LSP commands must be mapped to OpenDevelop command dispatch safely. The client must not allow an arbitrary server response to invoke unrestricted local operations.

---

## 18. AddIn lifecycle

The OpenLens provider registry must return a disposable registration.

```csharp
IDisposable RegisterProvider(IOpenLensProvider provider);
IDisposable RegisterAnchorProvider(IOpenLensAnchorProvider provider);
```

When an AddIn is disabled:

1. unregister its providers;
2. cancel their queued and active work;
3. remove their items from open editors;
4. close or update any details popup owned by them;
5. clear provider-specific cache entries;
6. leave other providers and the OpenLens host active.

When CSharpBinding or VBBinding is disabled, its language anchors disappear. Non-language providers cannot keep stale Git or coverage lenses attached to those missing anchors.

---

## 19. Suggested project layout

```text
src/AddIns/DisplayBindings/AvalonEdit.AddIn/
    OpenLens/
        OpenLensService.cs
        OpenLensProviderRegistry.cs
        OpenLensSession.cs
        OpenLensResolutionQueue.cs
        OpenLensCache.cs
        OpenLensPresenter.cs
        OpenLensDetailsPopup.cs
        OpenLensCommands.cs
        Models/
            OpenLensAnchor.cs
            OpenLensItem.cs
            OpenLensCommand.cs
        Rendering/
            VisualLineBlockAdornment.cs
            OpenLensRowControl.cs

src/Main/Base/Project/LanguageServices/
    OpenLens/
        ILanguageOpenLensService.cs
        IOpenLensAnchorProvider.cs
        LanguageOpenLens.cs

src/AddIns/BackendBindings/CSharpBinding/
    OpenLens/
        CSharpOpenLensAnchorProvider.cs
        CSharpReferenceOpenLensProvider.cs
        CSharpHierarchyOpenLensProvider.cs

src/AddIns/BackendBindings/VBBinding/
    OpenLens/
        VisualBasicOpenLensAnchorProvider.vb
        VisualBasicReferenceOpenLensProvider.vb
        VisualBasicHierarchyOpenLensProvider.vb

src/AddIns/Misc/UnitTesting/
    OpenLens/
        TestOpenLensProvider.cs

src/AddIns/Misc/Git/
    OpenLens/
        GitHistoryOpenLensProvider.cs

src/AddIns/Misc/CodeCoverage/
    OpenLens/
        CoverageOpenLensProvider.cs
```

Exact directories may differ, but AddIn ownership should remain visible in the project structure.

---

## 20. Delivery phases

**Status (this session):** Phase 0-2 implemented and built/tested; Phase 3-5 not started.

- Phase 0: done - `OpenLensProviderRegistry`, `IOpenLensProvider`/`IOpenLensAnchorProvider`,
  disposable registration, refresh events, `CodeEditorOptions.EnableOpenLens` flag, contract tests
  in `tests/OpenDevelop.Base.Tests`. Formal "provider disable cancels work promptly" lifecycle test
  not yet written (behavior exists via `CancellationTokenSource` + AddIn `Autostart` disposal, but
  isn't asserted by a test).
- Phase 1: done and superseded - the original overlay-prototype `OpenLensRenderer` (whole-document
  anchor discovery, viewport+prefetch resolution, bounded concurrency) has been fully replaced by
  Phase 2's production renderer rather than being a separate throwaway build.
- Phase 2: done - `IVisualLineBlockAdornment`/`IVisualLineBlockAdornmentGenerator`
  (`ICSharpCode.AvalonEdit.Rendering.BlockAdornment.cs`) added to vendored AvalonEdit;
  `TextView.BlockAdornmentGenerators` reserves real layout space above a document line, folded into
  `VisualLine.Height` and from there into the height tree that already drives word-wrap
  scrolling/hit-testing - so OpenLens rows now participate correctly in scrolling and hit testing
  without the old baseline-overflow trick. `OpenLensRenderer` rewritten as an
  `IVisualLineBlockAdornmentGenerator`; the inline-element prototype is gone. Regression-tested in
  `ICSharpCode.AvalonEdit.Tests/Rendering/BlockAdornmentTests.cs` (headless Measure/Arrange, no
  window). **Not done**: recyclable rows (a fresh visual is built per redraw, not reused/pooled),
  keyboard/automation peers, and folding-awareness (a lens on a folded-away line still isn't
  suppressed - anchors inside a fold aren't hidden). No GUI was available to visually confirm the
  rendered result; correctness rests on the height-tree math and the headless regression tests only.
- Phase 3-5: not started (implementations/overrides labeling refinement, LSP OpenLens bridge, test
  lenses, Git/coverage lenses, per-provider settings, results popup UI).

### Phase 0: contracts and lifecycle

- Add OpenLens DTOs with no Roslyn, LSP, or WPF leakage where inappropriate.
- Add provider and anchor-provider registries.
- Add disposable registration.
- Add refresh events.
- Add session cancellation and version checks.
- Add settings and feature flag.
- Add lifecycle tests for provider disable/unload.

### Phase 1: reference OpenLens proof of concept

- Implement C# and VB anchor discovery.
- Implement unresolved reference lenses.
- Resolve only visible lenses.
- Reuse existing Find References result model.
- Build temporary adorner renderer.
- Measure latency and number of backend calls.
- Validate split views, edits, scrolling, and cancellation.

This phase proves data architecture, not final visuals.

### Phase 2: production AvalonEdit layout

- Add reserved block-adornment support to vendored AvalonEdit.
- Implement recyclable OpenLens rows.
- Add keyboard, automation, theming, zoom, and folding behavior.
- Remove the overlay prototype.

### Phase 3: hierarchy and richer language lenses

- Add implementations, overrides, and derived types with correct labels.
- Add optional base-type navigation.
- Add per-provider settings.
- Add native LSP OpenLens bridge and resolve support.
- Add refresh support equivalent to LSP OpenLens refresh behavior.

### Phase 4: tests

- Add Run and Debug lenses.
- Add test state and duration.
- Refresh from discovery/build/test events.
- Reuse Test Results and runner commands.

### Phase 5: Git and coverage

- Add last-author/history lens.
- Add member coverage lens.
- Add provider-specific freshness and stale-state behavior.
- Reuse Git and coverage pads.

---

## 21. Performance acceptance criteria

For a typical file with 50 eligible declarations:

- Opening the document must not issue 50 reference searches immediately.
- Anchor discovery should complete without blocking the UI thread.
- Only visible lenses should begin expensive resolution.
- Scrolling away should cancel or deprioritize offscreen work.
- Repeated scrolling over the same version should reuse results.
- A single edit should not force unrelated Git, test, or coverage providers to recompute.
- Provider disable should cancel work promptly.
- The editor should remain responsive with OpenLens enabled on large files.

Suggested telemetry or debug counters:

```text
anchors discovered
items provided
items resolved
cache hits/misses
cancelled resolutions
average resolution latency
maximum queue length
backend calls per document
rendered rows
```

Telemetry should be local debug instrumentation unless OpenDevelop already has an explicit user-consented telemetry policy.

---

## 22. Test plan

### Contract tests

- Providers compose in deterministic order.
- One failing provider does not hide other providers.
- Unresolved items resolve to commands.
- Stale versions are rejected.
- Refresh invalidates only requested entries.
- Provider registration disposal removes items.

### C# and VB tests

- Types, methods, constructors, properties, indexers, and events get correct anchors.
- Accessors and local functions follow the configured policy.
- Partial declarations behave consistently.
- Reference count matches Find References results.
- Interface implementations and method overrides use correct labels.
- Cross-language C# ↔ VB references are counted.
- Unsaved changes update anchors and counts.
- Disabled binding removes its anchors and lenses.

### Rendering tests

- Lens above first line is visible.
- Lens rows reserve height and do not overlap code.
- Line numbers stay aligned.
- Word wrap does not move the lens into source text.
- Folding hides nested lenses correctly.
- Zoom and theme changes update visuals.
- Split views share data but not visual state.
- Mouse hit testing selects the intended item.
- Keyboard focus and activation work.
- Automation peers expose each command.

### Performance tests

- Large file with hundreds of declarations.
- Rapid scrolling.
- Rapid typing while lenses are resolving.
- Solution reload.
- Project reference change.
- Provider refresh storm coalescing.
- AddIn disable while work is active.

### Provider integration tests

- Test run refreshes only test lenses.
- Git branch switch refreshes Git lenses.
- Coverage run refreshes coverage lenses.
- Language edit does not unnecessarily recompute Git history.
- Details popup reuses existing result models and navigation.

---

## 23. Initial non-goals

The first production release should not attempt:

- Visual Studio Azure DevOps work items and code review integration.
- Automatic association between every production method and its tests.
- Arbitrary provider-supplied WPF controls.
- Persistent disk cache across OpenDevelop sessions.
- OpenLens for generated or decompiled files by default.
- Every possible symbol kind.
- Exact reproduction of Visual Studio keyboard shortcuts.
- Full OpenLens support for a language server that exposes neither native OpenLens nor document symbols.

---

## 24. Decision summary

1. Keep `ILanguageService` and the AddIn lifecycle boundary.
2. Do not call `RoslynWorkspaceHelper` from OpenLens.
3. Do not implement OpenLens as two hardcoded counts.
4. Add document-level anchor discovery.
5. Use provider composition.
6. Use provide/resolve semantics.
7. Resolve only visible or focused lenses.
8. Share caches across editor views.
9. Use provider-specific refresh generations.
10. Treat the adorner renderer as a prototype only.
11. Add a reserved block-adornment facility to AvalonEdit for production.
12. Reuse existing reference, test, Git, and coverage result models and pads.
13. Keep every lens owned by the AddIn that owns its underlying feature.
14. Start with C#/VB references, then hierarchy, tests, Git history, and coverage.

---

## 25. External references

- Visual Studio OpenLens documentation:  
  https://learn.microsoft.com/en-us/visualstudio/ide/find-code-changes-and-other-history-with-codelens

- Visual Studio Code programmatic language features and OpenLens provider model:  
  https://code.visualstudio.com/api/language-extensions/programmatic-language-features

- Language Server Protocol specification:  
  https://microsoft.github.io/language-server-protocol/specifications/lsp/3.18/specification/

---

## 26. Final assessment

The original proposal was a reasonable reference-count prototype, but not yet a full OpenLens architecture.

The largest changes are:

```text
from: two fixed data queries
to:   composable providers

from: point-based N+1 calls
to:   document discovery plus lazy resolution

from: overlay drawing
to:   reserved editor layout

from: click handling
to:   commands, reusable results, keyboard, and accessibility

from: document-version cache only
to:   provider-specific generations and refresh
```

With these changes, OpenDevelop can first match the useful C#/VB reference experience of Visual Studio, then naturally grow into test, Git, and coverage lenses without redesigning the editor each time.
