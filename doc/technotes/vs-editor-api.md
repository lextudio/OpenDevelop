# Enabling the Visual Studio Editor API in OpenDevelop

**Status:** design and implementation proposal  
**Target:** OpenDevelop on .NET 10 / LibreWPF / AvalonEdit  
**Prepared:** 2026-08-20  
**Primary goal:** make code written against the public `Microsoft.VisualStudio.Text.*` editor model usable inside OpenDevelop while keeping AvalonEdit as the actual editor control and text engine.

---

## 1. Executive summary

OpenDevelop can expose a useful subset of the Visual Studio Editor API without replacing AvalonEdit.

The recommended architecture is **not** to make `ICSharpCode.AvalonEdit` directly implement Microsoft interfaces. Instead, add a compatibility layer that wraps AvalonEdit and implements the public Visual Studio Editor contracts.

Conceptually:

```text
Existing OpenDevelop code
        |
        | ITextEditor / SharpDevelop editor services
        v
+------------------------------+
| AvalonEdit.AddIn             |
| CodeEditor / CodeEditorView  |
+------------------------------+
        |
        | existing AvalonEdit objects
        v
+------------------------------+
| AvalonEdit                   |
| TextDocument / TextArea      |
| TextView / UndoStack         |
+------------------------------+

        plus

+-------------------------------------------+
| OpenDevelop.VSEditor / AvalonEdit.VSEditor|
|                                           |
| AvalonTextBuffer      : ITextBuffer       |
| AvalonTextSnapshot    : ITextSnapshot     |
| AvalonTextVersion     : ITextVersion      |
| AvalonTextEdit        : ITextEdit         |
| AvalonTrackingPoint   : ITrackingPoint    |
| AvalonTrackingSpan    : ITrackingSpan     |
| AvalonTextView        : ITextView         |
| AvalonTextCaret       : ITextCaret        |
| AvalonTextSelection   : ITextSelection    |
| content type / tagging / classification   |
| MEF compatibility services                |
+-------------------------------------------+
```

This is unusually feasible because AvalonEdit already contains equivalents of several difficult VS editor concepts:

- immutable document snapshots;
- explicit document versions;
- change history between versions;
- moving offsets between versions;
- text anchors with insertion affinity;
- grouped document updates;
- undo grouping;
- a separate text view, text area, caret, and selection model.

The most important AvalonEdit interface is `ITextSourceVersion`. It already supports:

```text
BelongsToSameDocumentAs(...)
CompareAge(...)
GetChangesTo(...)
MoveOffsetTo(...)
```

Those operations are very close to what the VS editor snapshot/version/tracking model needs.

The recommended target is therefore:

> **Implement the public, cross-platform Visual Studio editor text model and selected editor services on top of AvalonEdit, then expose them through the existing OpenDevelop editor adapter/service architecture.**

Do **not** initially target full Visual Studio extension compatibility, the Visual Studio Shell, COM editor APIs, or the WPF-specific Visual Studio editor UI implementation.

---

## 2. Why this makes sense for OpenDevelop

OpenDevelop is already an unusually good host for this work.

The current repository explicitly keeps the classic SharpDevelop architecture while modernizing the runtime and platform support. Its README describes:

- the SharpDevelop add-in tree;
- the workbench;
- AvalonEdit;
- the project system;
- .NET 10;
- LibreWPF;
- Windows/macOS/Linux support.

Current repository:

- <https://github.com/lextudio/OpenDevelop>

OpenDevelop already carries AvalonEdit as a submodule:

```text
src/Libraries/AvalonEdit
    -> https://github.com/lextudio/AvalonEdit.git
```

The editor add-in is located at:

```text
src/AddIns/DisplayBindings/AvalonEdit.AddIn
```

and already contains a significant adapter layer:

```text
AvalonEditDisplayBinding.cs
AvalonEditEditorUIService.cs
AvalonEditViewContent.cs
AvalonEditorControlService.cs
CodeCompletionEditorAdapter.cs
CodeEditor.cs
CodeEditorAdapter.cs
...
```

`CodeEditorAdapter` already wraps the concrete AvalonEdit-based `CodeEditor` and exposes SharpDevelop's `ITextEditor`.

That means OpenDevelop already follows the right architectural pattern:

```text
editor control
    -> adapter
        -> IDE-facing editor contract
```

The VS Editor API should be added as a **second adapter surface**, not fused into the control.

---

## 3. Historical reason this API is useful

The public repository:

- <https://github.com/microsoft/vs-editor-api>

contains the open-source layers of the Visual Studio editor.

Microsoft describes it as containing:

- all public API definitions;
- parts of the text model;
- parts of text logic;
- editor primitives;
- editor operations.

Historically these layers were shared between Visual Studio on Windows and Visual Studio for Mac. The WPF and Cocoa UI layers were not fully open sourced.

The repository exposes project families corresponding to packages such as:

```text
Microsoft.VisualStudio.CoreUtility
Microsoft.VisualStudio.Text.Data
Microsoft.VisualStudio.Text.Logic
Microsoft.VisualStudio.Text.UI
Microsoft.VisualStudio.Text.UI.Wpf
Microsoft.VisualStudio.Language
Microsoft.VisualStudio.Language.Intellisense
Microsoft.VisualStudio.Language.StandardClassification
```

The repository itself is MIT licensed.

This is important for OpenDevelop because the VS editor API became an API surface understood by:

- Visual Studio editor extensions;
- Visual Studio for Mac editor integrations;
- Roslyn editor-layer code;
- language services;
- classification/tagging components;
- older MonoDevelop/VS for Mac code that migrated toward the VS editor model.

Exposing this API in OpenDevelop would therefore create a compatibility bridge to existing .NET editor ecosystem assets rather than inventing another entirely new abstraction.

---

## 4. Goal and non-goals

### 4.1 Primary goal

Allow components written against the public Visual Studio editor model to operate on an AvalonEdit document/view hosted by OpenDevelop.

For example:

```csharp
ITextBuffer buffer = ...;
ITextSnapshot snapshot = buffer.CurrentSnapshot;

SnapshotPoint point = new SnapshotPoint(snapshot, offset);

ITrackingPoint trackingPoint =
    snapshot.CreateTrackingPoint(
        offset,
        PointTrackingMode.Positive);
```

The underlying document should still be:

```csharp
ICSharpCode.AvalonEdit.Document.TextDocument
```

and the visible editor should still be AvalonEdit.

### 4.2 Secondary goal

Make selected historical MonoDevelop / Visual Studio for Mac / Roslyn editor components easier to reuse inside OpenDevelop.

### 4.3 Long-term optional goal

Support a constrained class of precompiled VS editor extensions that depend only on public editor contracts and services implemented by OpenDevelop.

This should be considered a later compatibility target, not the first milestone.

### 4.4 Explicit non-goals for the initial project

Do not attempt to implement:

```text
Visual Studio Shell
Microsoft.VisualStudio.Shell.*
IVsTextView
IVsTextBuffer
Visual Studio COM services
full VSIX hosting
full Visual Studio command routing
Visual Studio WPF editor implementation
Visual Studio-specific adornment UI
closed Microsoft editor implementation assemblies
private Microsoft.VisualStudio.Text.Implementation APIs
```

Do not make OpenDevelop dependent on Visual Studio being installed.

---

## 5. Compatibility modes

There are two fundamentally different ways to expose the API.

## 5.1 Mode A: use Microsoft's official contract assemblies

This is the recommended first approach.

Reference the official NuGet packages containing the public interfaces and value types, and implement those interfaces in OpenDevelop-owned assemblies.

Example package family:

```xml
<PackageReference Include="Microsoft.VisualStudio.CoreUtility"
                  Version="17.14.249" />
<PackageReference Include="Microsoft.VisualStudio.Text.Data"
                  Version="17.14.249" />
<PackageReference Include="Microsoft.VisualStudio.Text.Logic"
                  Version="17.14.249" />
<PackageReference Include="Microsoft.VisualStudio.Text.UI"
                  Version="17.14.249" />
```

The exact version should be pinned centrally and treated as part of the compatibility contract.

As of this design note, the low-level packages such as `CoreUtility`, `Text.Data`, and `Text.Logic` provide .NET Standard 2.0 assets and are therefore usable from modern .NET.

This mode has an important advantage:

> Code sees the actual Microsoft interface/type identities.

That means a precompiled component referencing the same official contract assembly can, in principle, receive an OpenDevelop implementation of `ITextBuffer`, `ITextSnapshot`, and so on.

Our implementation assembly does **not** need to pretend to be a Microsoft assembly. It merely implements interfaces defined by the genuine Microsoft contract assemblies.

Example:

```text
Microsoft.VisualStudio.Text.Data.dll
        |
        | defines ITextBuffer
        v
OpenDevelop.VSEditor.dll
        |
        | AvalonTextBuffer : ITextBuffer
        v
AvalonEdit TextDocument
```

This is the best route for maximum compatibility.

### Important restriction

Avoid:

```text
Microsoft.VisualStudio.Text.UI.Wpf
```

for the cross-platform layer.

Current packages of `Microsoft.VisualStudio.Text.UI.Wpf` target .NET Framework rather than modern cross-platform .NET. More importantly, the purpose of the project is to adapt the UI contracts to AvalonEdit/LibreWPF, not embed Microsoft's old editor UI implementation.

The non-WPF editor contracts should be preferred wherever possible.

---

## 5.2 Mode B: compile the MIT `vs-editor-api` source into OpenDevelop-owned assemblies

This is also legally and technically possible because `microsoft/vs-editor-api` is MIT licensed.

Advantages:

- full control over the source;
- easier modification;
- easier removal of incompatible dependencies;
- can freeze a historically useful API surface;
- avoids relying on future NuGet package behavior.

Disadvantage:

- assembly identity will differ unless Microsoft's exact signed assemblies are used;
- therefore this is primarily **source compatibility**, not binary compatibility;
- code compiled against Microsoft's DLLs may not automatically bind to OpenDevelop-owned copies of the same namespaces/types.

This mode is useful if official NuGet contracts become impractical on .NET 10.

### Recommendation

Start with **Mode A**.

Keep Mode B as a fallback.

---

## 6. Recommended repository/project layout

Two layouts are reasonable.

### 6.1 Initial development: keep it inside OpenDevelop

This is the fastest route while the API is experimental.

Suggested structure:

```text
src/
  Libraries/
    VSEditorCompat/
      OpenDevelop.VSEditorCompat.csproj

      Text/
        AvalonTextBuffer.cs
        AvalonTextSnapshot.cs
        AvalonTextSnapshotLine.cs
        AvalonTextVersion.cs
        AvalonTextEdit.cs
        AvalonTextChange.cs
        AvalonTrackingPoint.cs
        AvalonTrackingSpan.cs

      Services/
        AvalonTextBufferFactoryService.cs
        AvalonContentType.cs
        AvalonContentTypeRegistryService.cs
        AvalonTextDocumentFactoryService.cs

      View/
        AvalonTextView.cs
        AvalonTextCaret.cs
        AvalonTextSelection.cs
        AvalonViewScroller.cs
        AvalonTextViewModel.cs

      Tagging/
        AvalonTagAggregator.cs
        AvalonTagAggregatorFactoryService.cs

      Classification/
        AvalonClassifierAggregator.cs
        AvalonClassifierAggregatorService.cs

      Composition/
        EditorCompositionHost.cs
        EditorServiceRegistry.cs

tests/
  OpenDevelop.VSEditorCompat.Tests/
```

Once the abstraction proves generally reusable, it can be extracted.

### 6.2 Later extraction: reusable AvalonEdit project

Possible repository names:

```text
lextudio/AvalonEdit.VSEditor
lextudio/AvalonEdit.VisualStudio
lextudio/vs-editor-avalonedit
```

OpenDevelop can then consume it as another submodule or package.

### Recommendation

Develop in-tree first.

Extract only after:

```text
ITextBuffer
ITextSnapshot
ITextVersion
ITextEdit
ITrackingPoint
ITrackingSpan
```

are stable and tested.

---

## 7. Do not modify AvalonEdit more than necessary

The compatibility project should depend on AvalonEdit.

AvalonEdit itself should not depend on Visual Studio packages.

Avoid this:

```text
ICSharpCode.AvalonEdit
    -> Microsoft.VisualStudio.Text.*
```

Prefer this:

```text
Microsoft.VisualStudio.Text.*
          ^
          |
OpenDevelop.VSEditorCompat
          |
          v
ICSharpCode.AvalonEdit
```

This has several benefits:

- AvalonEdit stays generally reusable;
- no Microsoft editor dependency leaks into all AvalonEdit consumers;
- upstreaming AvalonEdit fixes stays easier;
- the compatibility layer can evolve independently;
- different versions of the VS contract API can potentially be supported;
- tests can isolate contract semantics from the editor control.

Only add small hooks to `lextudio/AvalonEdit` if the adapter genuinely cannot implement required semantics with the existing public API.

---

## 8. Core model mapping

The first phase should focus on `Microsoft.VisualStudio.Text`.

### 8.1 Main mapping

| Visual Studio Editor API | AvalonEdit backing concept | Difficulty |
|---|---|---:|
| `ITextBuffer` | `TextDocument` | Low/Medium |
| `ITextSnapshot` | immutable `ITextSource` from `CreateSnapshot()` | Low |
| `ITextVersion` | `ITextSourceVersion` | Low |
| `ITextSnapshotLine` | line metadata built over snapshot | Medium |
| `ITextEdit` | buffered operations applied inside `BeginUpdate()/EndUpdate()` | Medium |
| `ITextChange` | `TextChangeEventArgs` / `DocumentChangeEventArgs` | Medium |
| `ITrackingPoint` | version-aware offset or `TextAnchor` | Low/Medium |
| `ITrackingSpan` | two tracked boundaries | Medium |
| `SnapshotPoint` | Microsoft value type referencing our `ITextSnapshot` | Already provided by contract |
| `SnapshotSpan` | Microsoft value type referencing our `ITextSnapshot` | Already provided by contract |
| `Span` | Microsoft value type | Already provided |
| `NormalizedSnapshotSpanCollection` | Microsoft type / helper logic | Reuse contract package where possible |
| `PropertyCollection` | VS utility type | Reuse Microsoft `CoreUtility` |

---

## 9. `ITextSnapshot` implementation

AvalonEdit already exposes:

```csharp
ITextSource TextDocument.CreateSnapshot();
```

The returned snapshot is immutable.

The snapshot includes a version:

```csharp
ITextSource.Version
```

This maps well to:

```csharp
ITextSnapshot
```

Recommended wrapper:

```csharp
internal sealed class AvalonTextSnapshot : ITextSnapshot
{
    private readonly AvalonTextBuffer buffer;
    private readonly ITextSource source;
    private readonly AvalonTextVersion version;

    public AvalonTextSnapshot(
        AvalonTextBuffer buffer,
        ITextSource source)
    {
        this.buffer = buffer;
        this.source = source;
        this.version = new AvalonTextVersion(this, source.Version);
    }

    public ITextBuffer TextBuffer => buffer;

    public ITextVersion Version => version;

    public int Length => source.TextLength;

    public char this[int position] => source.GetCharAt(position);

    public string GetText()
        => source.Text;

    public string GetText(int startIndex, int length)
        => source.GetText(startIndex, length);
}
```

The real interface contains more members, but this illustrates the ownership model.

### Snapshot cache

Each buffer should return one stable wrapper instance for a given AvalonEdit version where practical.

Recommended structure:

```text
AvalonTextBuffer
    currentSnapshot
        |
        +-- ITextSource
        +-- ITextSourceVersion
```

When the document changes:

```text
old AvalonTextSnapshot
        |
        | document update
        v
new AvalonTextSnapshot
```

Old snapshots remain valid because their underlying `ITextSource` is immutable.

This property is essential for Roslyn-style background analysis.

---

## 10. `ITextVersion` implementation

AvalonEdit's `ITextSourceVersion` is one of the strongest reasons to attempt this project.

It already supports:

```csharp
bool BelongsToSameDocumentAs(ITextSourceVersion other);

int CompareAge(ITextSourceVersion other);

IEnumerable<TextChangeEventArgs>
    GetChangesTo(ITextSourceVersion other);

int MoveOffsetTo(
    ITextSourceVersion other,
    int oldOffset,
    AnchorMovementType movement);
```

This directly supports:

- determining version ordering;
- generating change sequences;
- moving positions across edits;
- incremental analysis.

Recommended wrapper:

```csharp
internal sealed class AvalonTextVersion : ITextVersion
{
    internal ITextSourceVersion SourceVersion { get; }

    public ITextBuffer TextBuffer { get; }

    public int VersionNumber { get; }

    // Next / Changes / ReiteratedVersionNumber
    // populated by AvalonTextBuffer.
}
```

### Version number

AvalonEdit versions do not necessarily expose an integer matching the VS contract.

`AvalonTextBuffer` should therefore maintain its own monotonic integer:

```text
0
1
2
3
...
```

while retaining the real `ITextSourceVersion` for semantic tracking.

---

## 11. `ITextBuffer` implementation

Recommended ownership:

```text
TextDocument
      |
      | exactly one adapter
      v
AvalonTextBuffer
      |
      +-- CurrentSnapshot
      +-- Properties
      +-- ContentType
      +-- edit transactions
      +-- VS change events
```

There must not be multiple unrelated `AvalonTextBuffer` wrappers for the same logical `TextDocument`, or identity comparisons will become unreliable.

### Adapter cache

Possible implementation:

```csharp
static readonly ConditionalWeakTable<TextDocument, AvalonTextBuffer>
    buffers = new();
```

or an OpenDevelop document-service registry.

A weak table prevents the compatibility layer from keeping closed documents alive.

---

## 12. Event translation

AvalonEdit provides a useful update lifecycle.

A grouped update follows approximately:

```text
BeginUpdate
    UpdateStarted
        Changing
        mutation
        Changed

        Changing
        mutation
        Changed
    TextChanged
    UpdateFinished
EndUpdate
```

The compatibility layer should translate a whole AvalonEdit update group into one VS-style buffer change transaction.

Recommended logic:

```text
UpdateStarted
    capture beforeSnapshot
    clear pendingChanges

Changed
    append change record

UpdateFinished
    create afterSnapshot
    create TextContentChangedEventArgs
    fire VS events
```

Depending on the exact target API version, VS editor events include variants such as:

```text
Changing
ChangedHighPriority
Changed
ChangedLowPriority
PostChanged
```

The adapter should preserve VS ordering even though AvalonEdit does not natively have separate priority event channels.

The first implementation can use the same change payload for each stage while honoring the expected event sequence.

### Reentrancy

VS editor clients can be sensitive to reentrancy.

Add explicit state:

```text
isApplyingEdit
isRaisingChanged
pendingPostChanged
```

and tests for handlers that attempt nested edits.

---

## 13. `ITextEdit` implementation

An `ITextEdit` should collect requested changes before mutating the document.

Example:

```text
CreateEdit()
    |
    +-- Replace(...)
    +-- Insert(...)
    +-- Delete(...)
    +-- Replace(...)
    |
    v
Apply()
```

At `Apply()` time:

1. validate all spans against the original snapshot;
2. normalize/merge conflicts according to VS semantics;
3. begin an AvalonEdit update group;
4. apply edits in a safe order, usually descending by source offset;
5. end the update group;
6. return the new snapshot.

Pseudo-code:

```csharp
public ITextSnapshot Apply()
{
    EnsureNotApplied();

    document.BeginUpdate();
    try
    {
        foreach (var change in NormalizeChanges()
                     .OrderByDescending(c => c.Start))
        {
            document.Replace(
                change.Start,
                change.OldLength,
                change.NewText);
        }
    }
    finally
    {
        document.EndUpdate();
    }

    return buffer.CurrentSnapshot;
}
```

The exact VS overlap rules must be verified with compatibility tests.

---

## 14. Tracking points

VS tracking modes conceptually answer:

> If text is inserted exactly at the tracked position, does the point stay before the inserted text or move after it?

AvalonEdit already exposes exactly this distinction:

```text
AnchorMovementType.BeforeInsertion
AnchorMovementType.AfterInsertion
```

Mapping:

```text
PointTrackingMode.Negative
    -> AnchorMovementType.BeforeInsertion

PointTrackingMode.Positive
    -> AnchorMovementType.AfterInsertion
```

For a tracking point tied to snapshots, the best implementation is usually version based:

```csharp
newVersion.MoveOffsetTo(
    oldVersion,
    offset,
    movement);
```

rather than a live UI-thread `TextAnchor`.

Why?

- a VS tracking point can be resolved against arbitrary snapshots;
- background consumers can ask about old/new snapshots;
- `ITextSourceVersion.MoveOffsetTo` is specifically designed for moving offsets between versions;
- live `TextAnchor` is thread-affine with `TextDocument`.

Use live anchors only where they give useful performance for current-document UI state.

---

## 15. Tracking spans

A tracking span can be modeled as two tracking points:

```text
[start]--------------------[end]
```

Different VS span tracking modes determine the insertion affinity of each boundary.

The implementation must carefully reproduce:

```text
SpanTrackingMode.EdgeExclusive
SpanTrackingMode.EdgeInclusive
SpanTrackingMode.EdgeNegative
SpanTrackingMode.EdgePositive
```

This is one of the areas that requires a semantic test matrix.

Example cases:

```text
original: ABCDEF
span:       CDE

insert at start edge
insert at end edge
delete inside
delete start boundary
delete complete span
replace across span
multiple edits in one transaction
```

Do not guess these semantics. Encode them as tests from the beginning.

---

## 16. Lines and line breaks

`ITextSnapshotLine` cannot directly wrap `DocumentLine` because `DocumentLine` belongs to the live document while `ITextSnapshotLine` belongs to an immutable snapshot.

Therefore:

```text
Do not:
    AvalonTextSnapshotLine -> live DocumentLine

Prefer:
    AvalonTextSnapshotLine
        -> immutable snapshot
        -> start offset
        -> length
        -> line-break length
        -> line number
```

Line indexes can be built:

- lazily;
- from newline scanning;
- with a snapshot-local compact line table;
- or by capturing required line information when the snapshot is created.

Start simple.

A line table containing integer starts is usually cheap enough and avoids accidental access to the live `TextDocument`.

---

## 17. Threading model

AvalonEdit `TextDocument` is owner-thread oriented.

Most direct access requires the owner thread.

However:

```csharp
TextDocument.CreateSnapshot()
```

is explicitly thread-safe.

This produces a very useful model:

```text
UI thread
    |
    +-- mutate TextDocument
    +-- caret / selection / view
    +-- create snapshots

background threads
    |
    +-- read immutable ITextSnapshot
    +-- parse / analyze
    +-- Roslyn operations
    +-- classification calculation
```

Recommended rule:

> VS-compatible mutations and view APIs stay on the OpenDevelop UI thread. Immutable snapshots may be consumed from background threads.

This is close to how modern editor/language-service architecture is expected to work anyway.

Add explicit thread checks in debug builds.

---

## 18. Undo/redo

AvalonEdit has `UndoStack` and groups document changes around updates.

VS editor APIs additionally expose undo history abstractions.

Possible mapping:

```text
ITextUndoHistory
    -> TextDocument.UndoStack

ITextUndoTransaction
    -> AvalonEdit grouped undo operation

ITextUndoHistoryRegistry
    -> registry keyed by ITextBuffer
```

The first text-model milestone does not need full VS undo services.

However, any edit performed through `ITextEdit` should immediately participate in AvalonEdit's normal undo system.

Later, add compatibility wrappers so components expecting VS undo history can create named transactions.

---

## 19. Content types

The VS editor uses content types extensively.

Examples:

```text
text
code
CSharp
Basic
XML
XAML
JSON
```

Implement:

```text
IContentType
IContentTypeRegistryService
```

OpenDevelop can map:

```text
FileName
    -> SharpDevelop ILanguageBinding
    -> VS content type
```

Recommended bootstrap hierarchy:

```text
any
  |
 text
  |
 code
  +-- CSharp
  +-- Basic
  +-- FSharp

 text
  +-- XML
      +-- XAML
```

The registry should permit extensions to add content types dynamically.

Do not hard-code every language forever.

---

## 20. Property bags

The VS editor uses property bags extensively:

```csharp
buffer.Properties
view.Properties
```

Use the official `PropertyCollection` from the VS utility contracts where possible.

This becomes an important bridge because extensions can attach OpenDevelop-specific objects without the compatibility layer understanding every type.

Examples:

```text
TextDocument
FileName
OpenDevelop ITextEditor
CodeEditor
Roslyn DocumentId
language binding
workspace
```

can all be discoverable through properties.

---

## 21. View-layer mapping

The second major stage is the visible editor.

### Main mapping

| VS API | AvalonEdit |
|---|---|
| `ITextView` | `CodeEditorView` / `TextView` / `TextArea` |
| `ITextCaret` | `TextArea.Caret` |
| `ITextSelection` | `TextArea.Selection` |
| `IViewScroller` | AvalonEdit scrolling APIs |
| viewport properties | `TextView` / `ScrollViewer` state |
| `ITextViewModel` | one-buffer model initially |
| text view roles | compatibility metadata |

Recommended structure:

```text
AvalonTextView
    |
    +-- CodeEditorView
    +-- TextArea
    +-- TextView
    +-- AvalonTextBuffer
    +-- AvalonTextCaret
    +-- AvalonTextSelection
```

Do not make AvalonEdit's `TextView` class itself implement Microsoft's `ITextView`.

---

## 22. `ITextView` scope

Implement only the parts required by real consumers first.

Likely first-wave features:

```text
TextBuffer
TextSnapshot
Caret
Selection
Properties
Roles
HasAggregateFocus
ViewportLeft
ViewportTop
ViewportWidth
ViewportHeight
ViewportRight
ViewportBottom
ViewScroller
Closed
LayoutChanged
Caret position notifications
```

Harder areas include:

```text
ITextViewLine
ITextViewLineCollection
formatted-line geometry
adornment layers
line transforms
space reservation
classification-driven formatting
```

Those APIs assume much more of the VS rendering engine.

Treat them as later work.

---

## 23. Caret mapping

The straightforward path:

```text
ITextCaret
    -> TextArea.Caret
```

Key responsibilities:

- current buffer position;
- move to snapshot point;
- preserve desired X coordinate where needed;
- overwrite/insert mode;
- caret position changed event;
- ensure requested location is visible.

The adapter must convert between:

```text
AvalonEdit offset
    <->
SnapshotPoint
```

The point must always belong to the view's current snapshot.

---

## 24. Selection mapping

AvalonEdit has a selection abstraction already.

Map:

```text
ITextSelection
    -> TextArea.Selection
```

Start with stream selection.

Box selection should be treated separately because the VS editor has its own virtual-space and box-selection semantics.

Important concepts:

```text
AnchorPoint
ActivePoint
Start
End
IsEmpty
Mode
Select(...)
Clear()
```

Virtual spaces may require additional state because AvalonEdit and VS represent end-of-line virtual positions differently.

---

## 25. Tagging

Tagging is one of the highest-value VS editor subsystems.

Important contracts:

```text
ITag
ITagSpan<T>
ITagger<T>
ITaggerProvider
ITagAggregator<T>
IViewTaggerProvider
```

OpenDevelop already has concepts such as:

- bookmarks;
- breakpoints;
- code coverage markers;
- bracket highlighting;
- change marker margins;
- diagnostics;
- semantic display information.

A VS tagging layer can become a common extension mechanism over those features.

Architecture:

```text
ITaggerProvider
      |
      v
ITagger<T>
      |
      | snapshot spans
      v
TagAggregator<T>
      |
      +--> AvalonEdit renderers/margins
      +--> other editor consumers
```

Do not immediately replace existing AvalonEdit rendering code.

First expose tags for compatibility.

Later, selected AvalonEdit renderers can consume the same tag source.

---

## 26. Classification

Classification is also high value because Roslyn/editor components commonly understand it.

Implement:

```text
IClassificationType
IClassificationTypeRegistryService
IClassificationTag
IClassifier
IClassifierProvider
IClassifierAggregatorService
```

The underlying result is:

```text
SnapshotSpan
    -> classification type
```

AvalonEdit rendering can translate those classifications into:

- text colors;
- font weight/style;
- decorations;
- semantic highlighting.

The compatibility layer should keep classification data separate from actual WPF brushes.

That keeps the core cross-platform and avoids coupling the text model to LibreWPF.

---

## 27. MEF compatibility

The VS editor extensibility model relies heavily on MEF.

OpenDevelop, on the other hand, already has the SharpDevelop add-in tree.

Do not replace OpenDevelop's add-in model.

Instead, if needed, host a **small MEF composition container specifically for VS-editor-compatible components**.

Architecture:

```text
OpenDevelop AddInTree
    |
    | remains primary IDE extension model
    v
OpenDevelop

VS Editor Compatibility Host
    |
    +-- MEF exports
    +-- ITaggerProvider
    +-- IClassifierProvider
    +-- buffer/view creation listeners
    +-- content type metadata
```

This gives OpenDevelop two extension ecosystems without forcing either one to emulate the other globally.

### MEF metadata to support later

Typical metadata includes:

```text
[ContentType(...)]
[TextViewRole(...)]
[Name(...)]
[Order(...)]
```

Start with explicit service registration.

Add metadata-driven MEF discovery only after the core editor interfaces work.

---

## 28. OpenDevelop service integration

`CodeEditorAdapter` already uses a service-oriented model and calls patterns such as:

```csharp
GetService<ITextEditorOptions>()
```

That suggests the VS adapter can be exposed using the existing editor service mechanism if practical.

Desired usage from an OpenDevelop extension:

```csharp
var vsBuffer =
    editor.GetService<ITextBuffer>();

var vsView =
    editor.GetService<ITextView>();
```

If the current SharpDevelop service container cannot register arbitrary externally-created services cleanly, introduce a dedicated provider:

```csharp
public interface IVSEditorCompatibilityService
{
    ITextBuffer GetTextBuffer(ITextEditor editor);
    ITextView? GetTextView(ITextEditor editor);
}
```

Then later bridge that provider into the normal service mechanism.

---

## 29. Recommended integration point in current OpenDevelop

The most logical place to instantiate/cache the wrappers is around the existing AvalonEdit add-in, not the core workbench.

Current relevant files include:

```text
src/AddIns/DisplayBindings/AvalonEdit.AddIn/Src/
    AvalonEditViewContent.cs
    CodeEditor.cs
    CodeEditorAdapter.cs
    CodeCompletionEditorAdapter.cs
```

Recommended ownership:

```text
CodeEditor
    |
    +-- existing SharpDevelop CodeEditorAdapter
    |
    +-- VSEditorCompatibilityContext
            |
            +-- AvalonTextBuffer
            +-- AvalonTextView
            +-- editor services
```

A compatibility context can be created lazily.

Example:

```csharp
sealed class VSEditorCompatibilityContext
{
    public AvalonTextBuffer TextBuffer { get; }

    public AvalonTextView TextView { get; }

    public VSEditorCompatibilityContext(
        CodeEditor codeEditor,
        CodeEditorView editorView)
    {
        TextBuffer =
            AvalonTextBufferRegistry.GetOrCreate(
                editorView.Document);

        TextView =
            new AvalonTextView(
                editorView,
                TextBuffer);
    }
}
```

Avoid creating a second `ITextBuffer` when split views display the same `TextDocument`.

Multiple views should share one buffer:

```text
                TextDocument
                    |
              AvalonTextBuffer
               /           \
              /             \
   AvalonTextView #1   AvalonTextView #2
```

---

## 30. Buffer factories

Implement at least:

```text
ITextBufferFactoryService
```

A factory-created VS buffer does not necessarily need to be visible.

It can own a standalone AvalonEdit `TextDocument`:

```text
ITextBufferFactoryService.CreateTextBuffer(...)
        |
        v
new TextDocument(...)
        |
        v
new AvalonTextBuffer(...)
```

This gives language services scratch buffers and test buffers without constructing UI.

That is valuable for compatibility tests.

---

## 31. Text documents and file paths

The VS editor distinguishes text buffers from file-backed text documents.

Eventually implement:

```text
ITextDocument
ITextDocumentFactoryService
```

Map them to:

```text
OpenDevelop FileName
file encoding
dirty state
save/reload logic
TextDocument
```

Do not duplicate OpenDevelop's file-management logic.

The VS wrapper should delegate saving/reloading to OpenDevelop where possible.

---

## 32. Projection buffers and `IBufferGraph`

This is one of the hardest areas.

The VS editor supports:

```text
IProjectionBuffer
IElisionBuffer
IBufferGraph
```

A visible buffer can be composed from spans originating in other buffers.

Conceptually:

```text
source buffer A ----\
                     \
source buffer B ------> projection buffer -> view
                     /
literal text --------/
```

This historically matters for technologies such as Razor and embedded languages.

AvalonEdit does not have a direct equivalent.

### Recommendation

Do not implement projection buffers in Phase 1.

When required, implement them as a logical text-model layer above AvalonEdit:

```text
source ITextBuffer(s)
        |
        v
AvalonProjectionBuffer
        |
        | generated immutable projection snapshot
        v
AvalonEdit display document
```

Mapping must remain bidirectional:

```text
projection point -> source point(s)
source span -> projection span(s)
```

This is a substantial subsystem.

It should be treated as its own workstream.

---

## 33. IntelliSense UI

Do not assume the old Visual Studio IntelliSense UI can simply be hosted.

Historically:

```text
Microsoft.VisualStudio.Language.Intellisense
```

and related packages contain UI/editor assumptions that are much more Visual-Studio-specific than the core text model.

OpenDevelop already has its own completion UI and editor integration.

Recommended strategy:

```text
VS text model
    + Roslyn/language service
        |
        v
OpenDevelop completion/signature-help UI
```

Rather than:

```text
old Visual Studio IntelliSense UI
        |
        v
OpenDevelop
```

The useful compatibility boundary is primarily:

- buffers;
- snapshots;
- tracking;
- tagging;
- classification;
- content types;
- editor operations.

Completion UI can remain native to OpenDevelop.

---

## 34. Editor operations

`IEditorOperations` is a useful medium-term target.

Many operations map directly to existing AvalonEdit functionality:

```text
Delete
Backspace
InsertText
InsertNewLine
Indent
Unindent
SelectAll
MoveSelectedLinesUp
MoveSelectedLinesDown
ConvertTabsToSpaces
ConvertSpacesToTabs
MakeUppercase
MakeLowercase
```

Create:

```text
AvalonEditorOperations : IEditorOperations
```

and delegate to AvalonEdit/OpenDevelop commands where possible.

This can make editor-command-oriented extensions reusable without exposing the entire VS command system.

---

## 35. Roles and creation listeners

VS editor extensions often activate based on view roles.

Examples:

```text
Editable
Document
Interactive
PrimaryDocument
Structured
```

Implement a small role set for OpenDevelop:

```text
OpenDevelopCodeEditor
Document
Editable
PrimaryDocument
```

Later support:

```text
ITextViewCreationListener
IWpfTextViewCreationListener
```

Careful: WPF-specific listener interfaces should only be considered if the referenced contract works with LibreWPF and provides real value. Prefer non-WPF abstractions for cross-platform compatibility.

---

## 36. WPF-specific VS editor API

OpenDevelop uses LibreWPF, but that does not mean the Microsoft Visual Studio WPF editor UI should be adopted.

These are separate issues.

LibreWPF makes WPF APIs available cross-platform for OpenDevelop's UI.

The VS WPF editor layer is Microsoft's specific editor implementation.

The adapter should therefore intentionally stop at:

```text
Microsoft.VisualStudio.Text.UI
```

where possible.

Avoid coupling to:

```text
Microsoft.VisualStudio.Text.UI.Wpf
```

unless a narrowly scoped contract is essential and can be cleanly reimplemented.

---

## 37. Binary compatibility: what is realistic

Earlier discussions of VS editor compatibility often mix two different questions.

### Case A: source code using public VS editor APIs

Very realistic.

Recompile it against OpenDevelop plus the official Microsoft contract packages.

### Case B: precompiled component using only public contract assemblies

Potentially realistic.

If OpenDevelop ships/loads the same official Microsoft contract assemblies, the component can see correct interface identities.

The remaining problems are:

- required services;
- package version compatibility;
- MEF composition;
- UI assumptions;
- VS Shell dependencies;
- command routing;
- WPF-only editor APIs.

### Case C: arbitrary VSIX extension

Not a goal.

Most VSIX extensions depend on far more than `ITextBuffer`.

Typical dependencies may include:

```text
Visual Studio Shell
IVs* services
VS command tables
package loading
AsyncPackage
VS service provider
Visual Studio-specific MEF exports
WPF editor adornment layers
COM APIs
```

Do not market the project initially as a general VSIX host.

A better statement is:

> OpenDevelop provides a growing implementation of the public Visual Studio Editor Platform API over AvalonEdit.

---

## 38. Versioning strategy

Pin one coherent VS editor contract generation.

Do not allow arbitrary independent package versions such as:

```text
Text.Data 17.14.x
Text.Logic 17.9.x
Text.UI 17.12.x
```

Use central package management or a shared property.

Example:

```xml
<PropertyGroup>
  <VSEditorApiVersion>17.14.249</VSEditorApiVersion>
</PropertyGroup>
```

Then:

```xml
<PackageReference Include="Microsoft.VisualStudio.CoreUtility"
                  Version="$(VSEditorApiVersion)" />

<PackageReference Include="Microsoft.VisualStudio.Text.Data"
                  Version="$(VSEditorApiVersion)" />

<PackageReference Include="Microsoft.VisualStudio.Text.Logic"
                  Version="$(VSEditorApiVersion)" />

<PackageReference Include="Microsoft.VisualStudio.Text.UI"
                  Version="$(VSEditorApiVersion)" />
```

Before committing to `17.14.249`, validate that every required package has the expected .NET Standard asset and that the dependency graph stays cross-platform.

A historically older contract set could also be chosen if the main compatibility target is Visual Studio for Mac-era components.

That decision should be driven by concrete consumers.

---

## 39. API priority tiers

### P0: mandatory proof-of-concept

Implement:

```text
ITextBuffer
ITextSnapshot
ITextSnapshotLine
ITextVersion
ITextEdit
ITrackingPoint
ITrackingSpan
```

and all required supporting members/types.

Success criterion:

> A non-UI library written only against these public VS editor contracts can edit an AvalonEdit document, hold old snapshots, and track points/spans correctly across edits.

### P1: essential editor platform

Add:

```text
ITextBufferFactoryService
IContentType
IContentTypeRegistryService
ITextDocument
ITextDocumentFactoryService
ITextUndoHistory
ITextUndoHistoryRegistry
```

### P2: visible editor

Add:

```text
ITextView
ITextCaret
ITextSelection
IViewScroller
basic view roles
basic view events
```

### P3: extensibility

Add:

```text
ITagger<T>
ITaggerProvider
ITagAggregator<T>
IClassifier
IClassifierProvider
IClassifierAggregatorService
MEF composition
```

### P4: editor operations

Add:

```text
IEditorOperations
IEditorOperationsFactoryService
search/navigation helpers
```

### P5: advanced text model

Add only when demanded:

```text
IProjectionBuffer
IElisionBuffer
IBufferGraph
projection mapping
```

### P6: advanced visual compatibility

Only after real consumers require it:

```text
ITextViewLine
line geometry
adornment layers
space reservation
advanced formatting APIs
```

---

## 40. Suggested first implementation classes

First PR:

```text
OpenDevelop.VSEditorCompat.csproj

Text/
    AvalonTextBuffer.cs
    AvalonTextSnapshot.cs
    AvalonTextSnapshotLine.cs
    AvalonTextVersion.cs
    AvalonTextEdit.cs
    AvalonTrackingPoint.cs
    AvalonTrackingSpan.cs
    AvalonTextChange.cs

Infrastructure/
    AvalonTextBufferRegistry.cs
```

Second PR:

```text
Services/
    AvalonTextBufferFactoryService.cs
    AvalonContentType.cs
    AvalonContentTypeRegistryService.cs
```

Third PR:

```text
View/
    AvalonTextView.cs
    AvalonTextCaret.cs
    AvalonTextSelection.cs
    AvalonViewScroller.cs
```

Fourth PR:

```text
Tagging/
Classification/
Composition/
```

---

## 41. First proof-of-concept test

Start with a test that does not create UI.

Pseudo-code:

```csharp
var document =
    new TextDocument("class C {}");

ITextBuffer buffer =
    new AvalonTextBuffer(document, contentType);

var snapshot0 = buffer.CurrentSnapshot;

var point =
    snapshot0.CreateTrackingPoint(
        6,
        PointTrackingMode.Positive);

using (var edit = buffer.CreateEdit())
{
    edit.Insert(6, "partial ");
    var snapshot1 = edit.Apply();

    Assert.Equal(
        "class partial C {}",
        snapshot1.GetText());
}

var tracked =
    point.GetPoint(buffer.CurrentSnapshot);

Assert.Equal(expectedOffset, tracked.Position);
```

Then verify:

```text
snapshot0.GetText()
```

still returns the original content.

That one test demonstrates three essential semantics:

- snapshot immutability;
- edit application;
- tracking across versions.

---

## 42. Essential semantic test matrix

The project should be test-driven because small differences in editor semantics cause subtle bugs.

### Snapshots

Test:

```text
old snapshot remains immutable
current snapshot changes after edit
two snapshots compare as different versions
snapshot text can be read on background thread
line enumeration is stable
```

### Tracking points

For both positive and negative modes:

```text
insert before point
insert exactly at point
insert after point
delete before point
delete across point
replace around point
multiple edits in one transaction
```

### Tracking spans

For every span tracking mode:

```text
insert at start
insert at end
insert inside
delete inside
delete across start
delete across end
delete complete span
replace complete span
```

### Edit transactions

Test:

```text
multiple inserts
overlapping replacements
cancelled edit
double Apply()
edit against stale snapshot
read-only region behavior if supported
nested update behavior
```

### Events

Test exact ordering:

```text
Changing
ChangedHighPriority
Changed
ChangedLowPriority
PostChanged
```

where present in the chosen API version.

### Undo

Verify:

```text
one ITextEdit.Apply()
    -> one expected AvalonEdit undo group
```

unless the VS edit explicitly creates multiple undo units.

---

## 43. Performance tests

The adapter should not turn every editor operation into whole-document string allocation.

Test at least:

```text
1 KB file
100 KB file
1 MB file
10 MB file
```

Scenarios:

```text
CurrentSnapshot retrieval
small insertion
large insertion
100 tracking points
10,000 tracking points
snapshot line lookup
GetText for small spans
classification query over visible region
```

AvalonEdit's rope-based snapshots are a major advantage.

Do not replace them with:

```csharp
document.Text
```

for every snapshot.

That would destroy much of the performance benefit.

---

## 44. Memory behavior

A major risk is accidentally retaining every old snapshot forever.

Rules:

- buffers own only the current snapshot strongly;
- tracking objects should retain only the minimum version information required;
- caches should use weak references where appropriate;
- extension property bags can retain large objects, so disposal on view/buffer close matters;
- closed `ITextView` objects must unsubscribe from AvalonEdit events.

Add memory tests that repeatedly:

```text
open document
edit many times
close document
force GC
```

and verify the compatibility layer does not keep the editor alive.

---

## 45. Disposal/lifetime

Define lifetimes clearly.

```text
TextDocument
    -> buffer lifetime

CodeEditorView
    -> view lifetime

OpenDevelop document closed
    -> ITextView.Close()
    -> detach events
    -> release view services

TextDocument no longer referenced
    -> buffer eligible for collection
```

Multiple views can share one buffer, so closing a view must **not** dispose the buffer if another view still uses it.

---

## 46. Error and validation behavior

Compatibility is not only about successful operations.

VS editor code may expect specific exceptions for:

```text
point belongs to wrong snapshot
span belongs to wrong buffer
position outside snapshot
edit already applied
edit already cancelled
tracking point requested for unrelated buffer
snapshot/version mismatch
```

Implement validation centrally.

Example helper:

```csharp
static void VerifySnapshotBelongsToBuffer(
    ITextSnapshot snapshot,
    AvalonTextBuffer buffer)
```

This prevents inconsistent error behavior across classes.

---

## 47. Read-only regions

The VS editor supports read-only checking.

AvalonEdit does not expose an identical model at the core text-document level.

For Phase 1:

- implement the required contract conservatively;
- allow all regions unless OpenDevelop explicitly marks them read-only.

Later, introduce a read-only region service owned by `AvalonTextBuffer`.

Do not bake WPF control read-only state into the text-model semantics.

A whole view can be read-only while the underlying buffer remains programmatically editable.

---

## 48. Roslyn integration value

A successful implementation gives OpenDevelop a useful bridge for Roslyn-oriented components.

The strongest reusable areas are likely:

```text
SnapshotSpan
SnapshotPoint
ITextBuffer
ITextSnapshot
tracking
classification
tagging
content types
```

These abstractions appear throughout historical and current editor integration code.

It may therefore become possible to reuse pieces that currently require translation into SharpDevelop-specific `ITextEditor` abstractions.

OpenDevelop does not need to replace `ITextEditor`.

Both can coexist:

```text
SharpDevelop ITextEditor
        |
        +-- existing OpenDevelop features

VS ITextBuffer / ITextView
        |
        +-- imported editor ecosystem components
```

---

## 49. Relationship to existing SharpDevelop abstractions

Do not rewrite all current OpenDevelop editor extensions to use the VS API.

That would create unnecessary migration risk.

Instead:

```text
existing feature
    -> keep using ITextEditor

new/reused VS-compatible feature
    -> use ITextBuffer / ITextView
```

Over time, common internal services can use whichever abstraction gives the cleaner result.

The VS compatibility layer is an **additional interoperability surface**, not a mandatory replacement.

---

## 50. Example dual-interface use

An OpenDevelop command could obtain both:

```csharp
ITextEditor sdEditor = ...;

ITextBuffer vsBuffer =
    VSEditorCompatibility.GetBuffer(sdEditor);
```

They represent the same document:

```text
sdEditor.Document
      |
      v
AvalonEdit TextDocument
      |
      v
AvalonTextBuffer
```

Therefore changes made through one side must immediately appear through the other.

This should be explicitly tested.

---

## 51. File synchronization and dirty state

Edits must not bypass OpenDevelop's normal dirty-state tracking.

If the current AvalonEdit add-in already determines dirty state from document changes, then changes made through `ITextEdit` naturally participate.

Verify:

```text
VS ITextEdit.Apply()
    -> TextDocument change
    -> OpenDevelop marks file dirty
    -> normal save flow works
```

No independent dirty flag should be maintained in the VS layer unless required for an `ITextDocument` contract.

---

## 52. Split views

VS editor APIs assume one buffer can have multiple views.

OpenDevelop should preserve this model from the beginning.

Wrong:

```text
view -> owns buffer
```

Correct:

```text
document -> owns/logically identifies buffer

view A -> buffer
view B -> buffer
view C -> buffer
```

This matters for:

- tracking points;
- taggers;
- classifiers;
- language services;
- file-backed document identity.

---

## 53. Classification/rendering architecture

Long-term, avoid a one-way dependency where VS classification knows WPF.

Prefer:

```text
Roslyn / classifier
      |
      v
SnapshotSpan + classification
      |
      v
OpenDevelop classification bridge
      |
      v
AvalonEdit text transformer
      |
      v
LibreWPF rendering
```

This keeps the text model independently testable.

---

## 54. Diagnostics and adornments

Diagnostics can first be implemented as tags.

Example:

```text
SnapshotSpan
    + severity
    + message
    + diagnostic ID
```

Then OpenDevelop can render them using existing AvalonEdit facilities.

Do not try to emulate Visual Studio's WPF adornment layer for the first diagnostic integration.

A semantic compatibility layer does not require pixel-identical VS rendering.

---

## 55. Breakpoints/bookmarks/code coverage

OpenDevelop already exposes visible editor metadata such as:

- breakpoints;
- bookmarks;
- code coverage markers.

These can eventually be represented as tag streams too.

This would be useful because VS-style editor extensions could participate in the same document without depending on SharpDevelop-specific marker interfaces.

Potential architecture:

```text
OpenDevelop breakpoint model
       |
       +-- existing AvalonEdit renderer
       |
       +-- BreakpointTagger : ITagger<BreakpointTag>
```

The reverse direction is also possible:

```text
external ITagger<T>
       |
       v
OpenDevelop tag aggregator
       |
       v
AvalonEdit margin/renderer
```

---

## 56. MEF versus AddInTree boundaries

A clean boundary is critical.

Recommended rule:

```text
OpenDevelop AddInTree
    owns IDE-level extension loading

VSEditor MEF container
    owns editor-component composition only
```

Do not let editor MEF exports become a second general OpenDevelop service container.

This keeps startup, lifetime, and dependency resolution understandable.

---

## 57. Licensing

Relevant known licenses:

```text
OpenDevelop
    MIT

AvalonEdit
    MIT

microsoft/vs-editor-api source repository
    MIT
```

Therefore, implementing adapters based on the public source/API design is compatible with an MIT OpenDevelop codebase.

However, before redistributing official Microsoft NuGet binaries, review the license metadata and notices of the exact packages chosen.

Avoid any dependency on historical private/internal packages such as:

```text
Microsoft.VisualStudio.Text.Implementation
```

unless Microsoft has clearly published and licensed the exact material being used.

Do not copy closed WPF/Cocoa editor implementation code.

---

## 58. Namespace and product naming

Implementation classes should live in an OpenDevelop/LeXtudio namespace.

For example:

```text
LeXtudio.OpenDevelop.VSEditor
LeXtudio.AvalonEdit.VSEditor
ICSharpCode.AvalonEdit.VSEditor
```

Do not create classes that imply they are Microsoft's implementation.

This is fine:

```csharp
AvalonTextBuffer : Microsoft.VisualStudio.Text.ITextBuffer
```

Avoid naming the implementation:

```text
Microsoft.VisualStudio.Text.Implementation.TextBuffer
```

unless there is an extremely strong compatibility reason and licensing/naming implications have been reviewed.

---

## 59. API coverage reporting

Maintain an explicit coverage file.

Suggested:

```text
docs/vs-editor-api-coverage.md
```

Format:

| API | Status | Notes |
|---|---|---|
| `ITextBuffer` | Complete | AvalonTextBuffer |
| `ITextSnapshot` | Complete | immutable rope snapshot |
| `ITextVersion` | Complete | version wrapper |
| `ITrackingPoint` | Complete | tested positive/negative |
| `ITrackingSpan` | Partial | edge modes pending |
| `ITextView` | Partial | geometry missing |
| `IBufferGraph` | Not started | Phase 5 |
| `IWpfTextView` | Not planned | cross-platform boundary |

This prevents vague claims of "VS editor API support."

---

## 60. Compatibility levels

It may be useful to publish levels.

### Level 1: Text Model

```text
buffers
snapshots
versions
edits
tracking
```

### Level 2: Editor Services

```text
content types
documents
undo
operations
```

### Level 3: Language/Tagging

```text
classification
tagging
aggregators
MEF providers
```

### Level 4: View

```text
caret
selection
scrolling
basic view events
```

### Level 5: Advanced Editor

```text
projection
buffer graph
advanced line/layout APIs
adornment compatibility
```

OpenDevelop can then say exactly which level a release supports.

---

## 61. Recommended development sequence

### Milestone 0: compile the contracts

Create the new project.

Reference only the minimum cross-platform Microsoft editor packages.

Verify on:

```text
Windows
macOS
Linux
```

under OpenDevelop's .NET 10 runtime.

Acceptance criterion:

> The compatibility assembly loads on all three platforms without Visual Studio installed.

### Milestone 1: snapshot spike

Implement:

```text
AvalonTextBuffer
AvalonTextSnapshot
AvalonTextVersion
```

Acceptance criterion:

> Old snapshots remain immutable while the live AvalonEdit document changes.

### Milestone 2: edits

Implement:

```text
ITextEdit
change collection
event translation
```

Acceptance criterion:

> An edit made through VS API modifies the same OpenDevelop document and participates in normal dirty state/undo.

### Milestone 3: tracking

Implement:

```text
ITrackingPoint
ITrackingSpan
```

Acceptance criterion:

> Edge behavior passes a comprehensive compatibility test matrix.

### Milestone 4: content types and factories

Implement:

```text
ITextBufferFactoryService
IContentTypeRegistryService
```

Acceptance criterion:

> A VS-style component can create a standalone buffer without OpenDevelop UI.

### Milestone 5: OpenDevelop adapter integration

Expose the buffer through the current AvalonEdit add-in.

Acceptance criterion:

```csharp
editor.GetService<ITextBuffer>()
```

or equivalent returns the wrapper for the currently edited document.

### Milestone 6: text view

Implement:

```text
ITextView
ITextCaret
ITextSelection
IViewScroller
```

Acceptance criterion:

> A VS-style editor component can follow caret/selection changes in a real OpenDevelop window.

### Milestone 7: tagging/classification

Implement:

```text
ITagger<T>
IClassifier
aggregators
provider discovery
```

Acceptance criterion:

> A VS-style classification/tag provider can color or annotate an AvalonEdit document.

### Milestone 8: MEF host

Implement editor-only MEF composition.

Acceptance criterion:

> A simple existing MEF-based editor component can be discovered and attached without modification.

### Milestone 9: real external consumer

Choose one historical component that was written for the VS Editor API.

Port/reuse it with minimal changes.

This is the point where API coverage should be expanded based on real need rather than theoretical completeness.

---

## 62. Best candidate for the first real-world validation

Do not begin with a huge Visual Studio extension.

Choose a component that needs only:

```text
ITextBuffer
ITextSnapshot
SnapshotSpan
tracking
classification/tagging
```

A Roslyn-related classifier/tagger or a historical MonoDevelop editor component is a better validation target than a full VSIX.

The ideal proof is:

```text
existing source
    |
    | little/no editor-model rewrite
    v
OpenDevelop
    |
    v
AvalonEdit
```

---

## 63. Potential hard blockers

### 63.1 Hidden implementation assumptions

A component may compile against public interfaces but cast objects to Microsoft's concrete implementation classes.

Those components will not be portable without modification.

### 63.2 Internal editor APIs

Historical VS/MonoDevelop code sometimes used:

```text
Microsoft.VisualStudio.Text.Implementation
internal editor APIs
```

Those should not define the target.

Patch the consumer to use public APIs.

### 63.3 WPF editor interfaces

A component may depend on:

```text
IWpfTextView
IWpfTextViewHost
IAdornmentLayer
```

Some of these might eventually be emulatable over LibreWPF/AvalonEdit, but they are substantially more expensive than the text model.

### 63.4 Projection buffers

Razor-like workloads can make projection support mandatory.

Treat that as a dedicated feature rather than accidentally implementing a partial/broken version.

### 63.5 MEF expectations

Some extensions assume a full Visual Studio MEF catalog.

The OpenDevelop compatibility container will intentionally contain only supported services.

---

## 64. Risk matrix

| Area | Risk | Reason |
|---|---:|---|
| immutable snapshots | Low | AvalonEdit already has them |
| text versions | Low | `ITextSourceVersion` is a strong match |
| point tracking | Low/Medium | direct movement mapping exists |
| span tracking | Medium | edge semantics need exact tests |
| basic edits | Medium | transaction/overlap semantics |
| undo | Medium | abstraction mismatch but underlying support exists |
| content types | Medium | registry must coexist with SharpDevelop languages |
| basic view | Medium | AvalonEdit has equivalent objects |
| caret/selection | Medium | virtual-space and snapshot semantics |
| tagging | Medium | mostly new service layer |
| classification | Medium | data easy, rendering bridge needed |
| MEF | Medium/High | second composition environment |
| view lines/layout | High | renderer-specific semantics |
| adornments | High | Visual Studio-specific UI assumptions |
| projection buffers | High | no direct AvalonEdit equivalent |
| arbitrary VSIX | Very High | far outside editor API alone |

---

## 65. Recommended first PR scope

Keep the first PR deliberately small.

Files:

```text
src/Libraries/VSEditorCompat/
    OpenDevelop.VSEditorCompat.csproj

    AvalonTextBuffer.cs
    AvalonTextSnapshot.cs
    AvalonTextVersion.cs

tests/OpenDevelop.VSEditorCompat.Tests/
    TextBufferTests.cs
    SnapshotTests.cs
    VersionTests.cs
```

No UI.

No MEF.

No taggers.

No projection.

No modification to current editor behavior.

The goal is simply to prove:

```text
AvalonEdit TextDocument
        ==
valid VS ITextBuffer
```

at the snapshot/version level.

---

## 66. Recommended second PR scope

Add:

```text
AvalonTextEdit
AvalonTextChange
event translation
tracking points
tracking spans
```

This is the real semantic milestone.

Once it passes tests, the project is no longer merely an experiment.

---

## 67. Recommended third PR scope

Integrate with the current editor add-in.

Changes should be limited to the adapter/service boundary.

Likely areas:

```text
CodeEditorAdapter.cs
AvalonEditViewContent.cs
AvalonEditorControlService.cs
```

Expose:

```text
ITextBuffer
```

for a live code editor.

Only after that is stable should `ITextView` be introduced.

---

## 68. Why `ITextBuffer` should come before `ITextView`

The VS editor API is layered.

A great deal of useful editor/language functionality needs only the text model.

Starting with the view introduces:

- rendering;
- WPF/LibreWPF;
- scrolling;
- visual lines;
- layout;
- caret;
- selection;
- roles;
- adornments.

None of that is required to validate the central idea.

The highest-value sequence is:

```text
TextDocument
    -> ITextBuffer
        -> Roslyn/language services
            -> later ITextView
```

not:

```text
TextEditor control
    -> emulate all of Visual Studio
```

---

## 69. Why AvalonEdit is better suited than it first appears

AvalonEdit is often described merely as a WPF text editor control.

For this project, the more important fact is that it also contains a mature text model.

Relevant capabilities include:

```text
Rope<char> storage
immutable rope snapshots
document version provider
change history
offset migration across versions
text anchors
anchor movement affinity
line model
undo stack
update grouping
thread-aware mutable document
thread-safe snapshot creation
```

Those are precisely the primitives needed by a modern editor compatibility layer.

The project is therefore less about "making AvalonEdit look like Visual Studio" and more about:

> adapting two mature text-model abstractions whose core semantics already overlap substantially.

---

## 70. Strategic value to OpenDevelop

If implemented well, this creates several benefits simultaneously.

### Reuse

OpenDevelop can consume code written for the Visual Studio editor ecosystem.

### Migration

Historical MonoDevelop / Visual Studio for Mac editor code may become easier to bring into OpenDevelop.

### Roslyn compatibility

Many Roslyn editor components naturally operate around VS editor abstractions.

### Extension surface

OpenDevelop gains a second recognized editor extension API without abandoning SharpDevelop's existing APIs.

### Validation of AvalonEdit

If AvalonEdit can satisfy VS editor snapshot/version/tracking semantics, it demonstrates that AvalonEdit is suitable as the core of a much broader IDE architecture.

### Future framework reuse

The adapter could eventually be reused by other AvalonEdit-based applications, not only OpenDevelop.

---

## 71. Suggested project statement

A precise description would be:

> OpenDevelop implements a compatibility layer for the public Microsoft Visual Studio Editor Platform API on top of AvalonEdit. The implementation focuses first on the cross-platform text model, snapshots, edits, tracking, content types, tagging, classification, and basic text-view services. It does not embed the Visual Studio editor or require Visual Studio to be installed.

Avoid claiming:

> OpenDevelop runs Visual Studio extensions.

That is much broader and would create incorrect expectations.

---

## 72. Concrete architecture recommendation

The target architecture should be:

```text
                            +----------------------+
                            | OpenDevelop Workbench|
                            +----------+-----------+
                                       |
                              SharpDevelop services
                                       |
                            +----------v-----------+
                            | AvalonEdit.AddIn     |
                            | CodeEditorAdapter    |
                            +----------+-----------+
                                       |
                  +--------------------+--------------------+
                  |                                         |
        existing ITextEditor API                  VS compatibility context
                  |                                         |
                  |                              +----------v-----------+
                  |                              | AvalonTextView       |
                  |                              +----------+-----------+
                  |                                         |
                  |                              +----------v-----------+
                  |                              | AvalonTextBuffer     |
                  |                              +----------+-----------+
                  |                                         |
                  +--------------------+--------------------+
                                       |
                            +----------v-----------+
                            | AvalonEdit           |
                            | TextDocument         |
                            | TextArea / TextView  |
                            +----------------------+
```

Service ecosystems:

```text
SharpDevelop AddInTree
    -> OpenDevelop extensions

Editor-only MEF host
    -> VS-editor-compatible components
```

Both operate on the same underlying document.

---

## 73. Decision summary

### Do

- keep AvalonEdit as the editor;
- add a separate VS compatibility assembly;
- use official Microsoft contract assemblies first;
- implement the text model before the view;
- use `ITextSourceVersion` heavily;
- cache one `ITextBuffer` per `TextDocument`;
- preserve OpenDevelop's existing `ITextEditor`;
- expose VS services through the existing adapter/service boundary;
- build extensive semantic tests;
- add tagging/classification after core text semantics are proven;
- treat projection buffers as a later dedicated workstream.

### Do not

- replace AvalonEdit;
- make AvalonEdit directly depend on VS editor packages;
- start by implementing the full UI;
- pull in `Microsoft.VisualStudio.Text.UI.Wpf` as the editor;
- depend on private `Text.Implementation` packages;
- promise arbitrary VSIX compatibility;
- rewrite existing OpenDevelop features simply to use the new API.

---

## 74. Immediate next action

The most useful next engineering task is a small, isolated spike.

Create:

```text
OpenDevelop.VSEditorCompat
```

and implement only:

```text
AvalonTextBuffer
AvalonTextSnapshot
AvalonTextVersion
```

with tests proving:

```text
1. CurrentSnapshot mirrors TextDocument.
2. An old snapshot remains immutable after editing.
3. Version ordering is correct.
4. Changes between versions can be enumerated.
5. Offsets can move from an old version to a new version.
6. Snapshot text can be consumed from a background thread.
```

Then add:

```text
AvalonTextEdit
AvalonTrackingPoint
AvalonTrackingSpan
```

If those semantics pass, there is enough evidence to commit to the broader implementation.

---

# Appendix A: current OpenDevelop references

OpenDevelop:

<https://github.com/lextudio/OpenDevelop>

Current README:

<https://github.com/lextudio/OpenDevelop/blob/master/README.md>

AvalonEdit submodule declaration:

<https://github.com/lextudio/OpenDevelop/blob/master/.gitmodules>

AvalonEdit add-in:

<https://github.com/lextudio/OpenDevelop/tree/master/src/AddIns/DisplayBindings/AvalonEdit.AddIn>

Current `CodeEditorAdapter`:

<https://github.com/lextudio/OpenDevelop/blob/master/src/AddIns/DisplayBindings/AvalonEdit.AddIn/Src/CodeEditorAdapter.cs>

LeXtudio AvalonEdit:

<https://github.com/lextudio/AvalonEdit>

---

# Appendix B: Visual Studio editor API references

Microsoft VS Editor API repository:

<https://github.com/microsoft/vs-editor-api>

README:

<https://github.com/microsoft/vs-editor-api/blob/main/README.md>

License:

<https://github.com/microsoft/vs-editor-api/blob/main/LICENSE>

Open-source project layers:

<https://github.com/microsoft/vs-editor-api/blob/main/src/OpenSource.Def.projitems>

Text Data definitions:

<https://github.com/microsoft/vs-editor-api/tree/main/src/Editor/Text/Def/TextData>

---

# Appendix C: AvalonEdit text-model references

`ITextSource` and `ITextSourceVersion`:

<https://github.com/lextudio/AvalonEdit/blob/master/ICSharpCode.AvalonEdit/Document/ITextSource.cs>

`TextDocument`:

<https://github.com/lextudio/AvalonEdit/blob/master/ICSharpCode.AvalonEdit/Document/TextDocument.cs>

`ITextAnchor` and `AnchorMovementType`:

<https://github.com/lextudio/AvalonEdit/blob/master/ICSharpCode.AvalonEdit/Document/ITextAnchor.cs>

`TextAnchor`:

<https://github.com/lextudio/AvalonEdit/blob/master/ICSharpCode.AvalonEdit/Document/TextAnchor.cs>

---

# Appendix D: package notes

At the time of this design, the recent Visual Studio Editor Platform package family includes versions in the 17.14 line.

Useful packages to investigate first:

```text
Microsoft.VisualStudio.CoreUtility
Microsoft.VisualStudio.Text.Data
Microsoft.VisualStudio.Text.Logic
Microsoft.VisualStudio.Text.UI
```

The low-level API packages provide .NET Standard 2.0 assets suitable for modern .NET consumers.

Avoid using the Microsoft WPF editor implementation as the basis of this project:

```text
Microsoft.VisualStudio.Text.UI.Wpf
```

The OpenDevelop implementation should provide its own AvalonEdit/LibreWPF view adapter.

NuGet pages:

<https://www.nuget.org/packages/Microsoft.VisualStudio.CoreUtility>

<https://www.nuget.org/packages/Microsoft.VisualStudio.Text.Data>

<https://www.nuget.org/packages/Microsoft.VisualStudio.Text.Logic>

<https://www.nuget.org/packages/Microsoft.VisualStudio.Text.UI>

<https://www.nuget.org/packages/Microsoft.VisualStudio.Text.UI.Wpf>

---

# Appendix E: possible future compatibility targets

After the text/view/tagging layers work, investigate real reuse candidates from:

- historical MonoDevelop editor integrations;
- Visual Studio for Mac editor integrations;
- Roslyn editor features that depend primarily on `ITextBuffer`/`ITextSnapshot`;
- standalone VS editor taggers/classifiers;
- editor utilities that use `SnapshotSpan` and tracking but do not depend on Visual Studio Shell.

For each candidate, record missing APIs and expand the compatibility surface only when justified by a real consumer.

This prevents the project from becoming an endless attempt to reimplement all of Visual Studio.

---

# Appendix F: implementation status (2026-08-20)

This appendix tracks what is actually built versus this design document's plan, and is the
living source of truth for "what's left" - update it as work lands rather than trusting the
plan sections above to reflect current reality.

## Done

**Text model (P0/P1)** - `src/Libraries/VSEditorCompat/Text/`, `Services/`, `Infrastructure/`:
`AvalonTextBuffer`, `AvalonTextSnapshot`, `AvalonTextSnapshotLine`, `AvalonTextVersion`,
`AvalonTextEdit`, `AvalonTextChange(Collection)`, `AvalonTrackingPoint`, `AvalonTrackingSpan`,
`AvalonReadOnlyRegionEdit`, `AvalonTextDocument`, content-type registry/service, buffer factory,
`AvalonTextBufferRegistry`. Full semantic test coverage (snapshot immutability, tracking
point/span edge modes verified against AvalonEdit's real `OffsetChangeMapEntry.GetNewOffset`
semantics, edit transactions, undo participation).

**View layer (Milestone 6)** - `View/`: `AvalonTextView`, `AvalonTextCaret`,
`AvalonTextSelection`, `AvalonViewScroller`, `AvalonEditorOptions`, `AvalonTextDataModel`,
`AvalonTextViewModel`, `AvalonTextViewRoleSet`, `AvalonTextViewRegistry`. Wired into
`CodeEditor.cs`/`CodeEditorAdapter` via the standard `GetService` chain (`ITextBuffer` per
document, `ITextView` per split view).

**`ITextViewLine`/`ITextViewLineCollection` geometry** - `View/AvalonTextViewLine(Collection)`,
backed by AvalonEdit's real `VisualLine`/`TextLine` layout (not faked). Requires a live,
laid-out window - verified via `VSEditorViewDevFlowActions`
(`src/AddIns/DisplayBindings/AvalonEdit.AddIn/Src/`) driving the real running app, exercised by
`tests/OpenDevelop.IntegrationTests/VSEditorViewIntegrationTests.cs`. Folding (a `VisualLine`
spanning multiple `DocumentLine`s) needed no special-casing in the offset math - verified against
a real `FoldingManager`-created fold.

**Projection buffers (section 32, P5)** - `Projection/`: `AvalonProjectionBuffer`,
`AvalonProjectionSnapshot(Line)`, `AvalonProjectionVersion`, `AvalonProjectionTrackingPoint/Span`,
`AvalonElisionBuffer(Snapshot)`, `AvalonProjectionBufferFactoryService`, plus
`FlatBufferGraph/MappingPoint/MappingSpan` (the non-projecting `IBufferGraph` every
`CaretPosition`/tag needs even without real projection). Bidirectional projection↔source mapping,
structural span edits (`InsertSpan`/`DeleteSpans`/`ReplaceSpans`), literal-string segments,
elision with tracked (not stale) elided ranges. **Known restriction**: an edit straddling a
segment boundary throws `NotSupportedException` - no `IProjectionEditResolver` protocol is
implemented (see "Not done" below).

**Tagging/Classification/Composition (P3)** - `Tagging/`, `Classification/`, `Composition/`:
`AvalonTagAggregator(FactoryService)`, tagger/classifier provider registries,
`AvalonClassifi(cation/er)*`, `EditorCompositionHost` (explicit registration, not real MEF
assembly-scanning - intentional per this doc's own section 27 staging).

**Editor operations & undo (P1/P4)** - `Operations/AvalonEditorOperations(FactoryService)`
implements `IEditorOperations`/`IEditorOperations2`/`IEditorOperations3` (~65 members: caret
navigation, word/line movement, deletion, indent, case conversion, transpose, duplicate/move
lines, clipboard, scrolling, zoom). Implemented directly against
`TextArea`/`Document`/`Caret`/`Selection` and
`ICSharpCode.AvalonEdit.Document.TextUtilities.GetNextCaretPosition` - **not** via
`System.Windows.Input.EditingCommands`, which turned out not to be part of this project's WPF
reference surface (same class of gap as `AvalonTextView`'s `IScrollInfo` issue - see that class's
comment). `Undo/AvalonTextUndoHistory(Transaction)`, `AvalonTextUndoHistoryRegistry` wrap
AvalonEdit's real `UndoStack.StartUndoGroup`/`EndUndoGroup`, with a parallel bookkeeping list for
transaction descriptions/enumeration (AvalonEdit's own stack doesn't expose that). All of this has
full unit coverage (`EditorOperationsTests.cs`, `UndoHistoryTests.cs`) - headless, no window
needed since none of it touches layout.

**Test counts as of this writing**: 141 unit tests (`tests/OpenDevelop.VSEditorCompat.Tests`, all
passing, no live app needed) + 6 DevFlow integration tests
(`tests/OpenDevelop.IntegrationTests/VSEditorViewIntegrationTests.cs`, against the real running
app). Whole solution (`OpenDevelop.Mvp.slnx`) builds with 0 errors.

## Not done

- **`IProjectionEditResolver` / cross-segment editing** - `AvalonProjectionBuffer` throws
  `NotSupportedException` for any edit spanning more than one segment. Needed for real Razor-like
  embedded-language editing at a segment boundary.
- **Real adornment / space-reservation layer** - `AvalonTextView.QueueSpaceReservationStackRefresh`
  is a no-op; `GetAdornmentBounds`/`GetAdornmentTags` return nothing. No peek-view/InfoBar-style
  UI exists. This is the largest remaining gap and would need new code in the AvalonEdit fork
  itself (not just the compat layer), per this doc's own risk matrix (section 64: "High risk").
- **MEF assembly-scanning discovery** - `EditorCompositionHost` requires explicit
  `RegisterTaggerProvider`/`RegisterClassifierProvider` calls; no `[Export]`-attribute catalog
  scanning. Matches this doc's own staged plan (section 27) but is a real gap vs. VS.
- **`IWpfTextView`/WPF-specific editor UI** - out of scope by design (section 36), unlikely to
  change.
- **An AvalonEdit-core rendering bug, found but NOT fixed**: a folded `VisualLine` spanning
  multiple `DocumentLine`s can still render as more than one physical `TextLine` row even with
  `WordWrap` off and ample width. Diagnostics (via `VSEditorViewDevFlowActions`) confirmed
  `FoldingManager.GetNextFoldedFoldingStart`/`GetFoldingsContaining` and
  `VisualLine.PerformVisualElementConstruction`'s own algorithm are both correct at the exact fold
  offset - the extra split happens somewhere inside WPF's `TextFormatter`/`FormattedTextElement`
  embedded-object line-breaking, past the point where live-app trial-and-error stopped being cost
  -effective to isolate further (each round-trip costs 20-30s+). `AvalonTextViewLine`'s own
  `Extent`/text-combination math is correct regardless of this (verified by
  `Folding_Merges_The_Folded_DocumentLines_Into_One_ITextViewLine_With_The_Combined_Extent`, which
  asserts on combined text/extent rather than assuming a specific row count). **If picked back
  up**: reproduce locally against `FormattedTextElement`/`TextFormatter` directly (no app, no
  DevFlow) rather than through the live app - much cheaper to iterate on.
- **Editor-creation listener hooks** - no `ITextViewCreationListener`-equivalent notifies external
  code when a new `AvalonTextView`/`AvalonProjectionBuffer` is created.
- **`RegisterHistory`/`RemoveHistory` semantics** - `AvalonTextUndoHistoryRegistry.RemoveHistory`
  is a documented no-op (`ConditionalWeakTable` has no reverse lookup); acceptable since nothing
  currently depends on eager removal, but a real caller relying on it would need this revisited.
