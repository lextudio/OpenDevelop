# GTK 4 Designer

The workbench shell uses the common `Designer.Shell.DesignerSelectionController` for the GTK
object forest, stable-ID selection restoration and `GtkPropertyAdapter` recreation. Native GTK
rendering, GtkBuilder mutation, hit-testing and container rules remain backend-owned.
Undo, Redo and Delete also pass through the common `DesignerCommandController`; GTK supplies the
versioned RPC operations and invalidates command state after each mutation.

## Implemented process boundary (2026-08-23)

The GTK designer now uses the shared DDP process architecture. `ICSharpCode.GtkDesigner.dll`
remains in-process only for OpenDevelop UI integration, while `GtkDesigner.Host` owns the
authoritative GtkBuilder document model, undo/redo history, mutations and serialization. The IDE
receives only neutral `DesignerElementNode` snapshots over authenticated StreamJsonRpc and never
loads the document editor implementation. The integration test asserts a live child process id and
exercises the actual Tools, Outline and Properties pads, selection, Toolbox insertion, undo/redo,
save, Zoom/Fit/Gridlines, signal edits, child reorder, refresh and child-process restart. The child
uses Gir.Core to instantiate the GtkBuilder document for native measure/allocation/bounds and uses
GTK's own widget snapshot and GSK/Cairo renderer for real PNG pixels, including text and theme details. On
macOS rendering stays inside the long-lived `LSUIElement`/`LSBackgroundOnly` designer host,
so AppKit classifies the host as an accessory/background process before GTK rendering starts and
repeated preview refreshes do not create process churn. The saved source is
independently checked by `gtk4-builder-tool validate`.

Multiple `.ui` documents now share one GTK designer host process. `GtkDesignerHostClient` is a
per-document lease with its own `DocumentId`; the shared connection owns the child process and every
mutation/flush/hit-test RPC includes that `documentId`. `GtkDesigner.Host` keeps separate
`DocumentSession` objects for editor text, version, native bounds, render diagnostics and undo
history, and `session/close` removes only the closed document. Opening MainWindow and
SettingsWindow concurrently must therefore report the same host PID while edits and saves remain
isolated by document.

The host uses the common `DesignerChildHost` bootstrap. It exits on either an explicit bounded
`shutdown` RPC or loss of the parent RPC transport, so killing OpenDevelop cannot leave a GTK
designer orphan behind. This does not introduce per-render helpers: native text/theme rendering
continues inside the one long-lived, Dock-hidden GTK process shared by compatible documents.

The implemented lifecycle follows the normative shared-host design in
[`designer-common.md`](designer-common.md#shared-host-lifecycle-design-2026-08-23). The common
broker, rather than backend-specific static counters, owns the connection; the final close uses a
ten-second idle grace; a crash or explicit restart restores every live `.ui` lease from its
parent-owned snapshot; and pixels become an asynchronous, latest-revision-only result. GTK work
remains serialized on GTK's main thread and all documents reuse one GSK/Cairo renderer.
"Asynchronous" means the IDE and semantic edit path do not wait for pixels—it does not permit GTK
calls on an arbitrary worker thread.

The two-window integration scenario edits both documents, closes one without affecting its
sibling, kills/restarts the shared host while both remain open, verifies their models recover
independently, and rejects stale frames after rapid changes. It also covers the real Toolbox,
Outline and Properties pads and validates saved XML. Status automation exposes
pool/session/document identities, recovery count and requested/rendered revisions, so these
assertions do not depend on screenshots.

## Decision

Build a new source-backed GTK 4 designer. Do not port Stetic as the runtime designer.

The authoritative format is one standard GtkBuilder `.ui` file per independently designable
window/widget. OpenDevelop owns selection and pads; the isolated GTK 4 host owns parsing, source
edits, undo/redo and serialization, and will also own native construction/rendering as that layer
is added.

MonoDevelop's GTK 2 designer remains valuable as a requirements catalogue and for a few
toolkit-independent algorithms, but its live widget model, wrappers, property editors, code
generator and project-wide `gui.stetic` format must not become dependencies of the new designer.

Cambalache demonstrates that a data-model-first GTK 4 designer and an isolated renderer are
feasible, but its licensing is not permissive enough for the intended OpenDevelop dependency and
distribution policy. It is a research reference only.

## Cambalache non-reuse boundary

Do not copy, vendor, link, modify, translate, mechanically port, generate from, or distribute:

- Cambalache source code;
- its GTK/Adwaita catalogues or catalogue schemas;
- its SQLite schema, triggers or history implementation;
- its import/export implementation;
- Merengue, Casilda or their command protocol;
- generated assets produced specifically from Cambalache tooling;
- test fixtures whose copyrightable structure originates from Cambalache.

Do not make Cambalache an installed, optional or sidecar dependency of the designer. “Open in
Cambalache” is outside this plan as well, so the supported workflow does not depend on software
with a different license policy.

Publicly observable high-level facts may inform requirements—for example, that GTK 4 designers
benefit from a normalized semantic model and process-isolated preview—but OpenDevelop's concrete
model, schema, protocol, algorithms, catalogues, fixtures and UI must be independently designed.

The architectural ideas worth adopting independently are:

- **data model first**: edits operate on a toolkit-neutral semantic graph, not directly on live
  GTK objects;
- **catalog driven**: widget availability, inheritance, properties, signals and version gates are
  data, not hundreds of handwritten wrapper subclasses;
- **runtime separation**: source/model editing continues to work when GTK is missing or the native
  preview crashes;
- **out-of-process preview**: target widgets and native libraries never enter the IDE process;
- **multiple toolkit versions**: the document declares a target version and the catalogue filters
  types and properties accordingly;
- **semantic history**: add/remove/reorder/property/signal operations are transactions rather than
  opaque mutations of a live widget tree;
- **import/export boundary**: the editing model and GtkBuilder serialization are separate layers,
  with diagnostics for constructs that cannot be represented;
- **custom-library catalogues**: third-party GTK libraries extend metadata without requiring the
  IDE to instantiate their objects.

These principles are re-derived here from the GTK 4 problem and implemented using OpenDevelop's
own types, terminology and tests. No compatibility with Cambalache's internal representation or
protocol is a goal.

The clean implementation inputs are limited to permissively usable or normative sources:

- GTK/GDK/GSK/GLib GIR files installed with the toolkit;
- official GtkBuilder and GTK API documentation;
- GTK's own LGPL API/runtime used dynamically under its normal terms;
- OpenDevelop's existing permissively licensed infrastructure;
- independently authored override tables and fixtures.

Maintain provenance for every checked-in metadata field and override. Catalogue generation must
be reproducible from GIR plus OpenDevelop-owned overrides. Add a repository check that rejects
Cambalache file names, schema identifiers and protocol identifiers in shipped designer assets.

## Why Stetic cannot be upgraded in place

The copy under `externals/monodevelop/main/src/addins/MonoDevelop.GtkCore` is substantial:

- `libstetic`: about 28,000 lines of C#;
- `MonoDevelop.GtkCore.GuiBuilder`: about 4,200 lines of C#;
- 274 C#/XML/project files in the add-in;
- more than 100 GTK 2-specific wrapper and editor classes.

Its central abstraction is a live `Gtk.Widget` wrapped by a `Stetic.Wrapper.Object`. Containers,
packing properties, actions, stock items, Gtk.UIManager, Gtk.Bin and concrete GTK 2 widgets are
encoded throughout the object model. The local source alone references `Gtk.Widget` in 48 files,
GDK APIs in 58 files, Gtk.HBox in 26 files, and removed action/UI-manager APIs in many more.

GTK 4 is not an ABI-compatible widget update:

- `GtkContainer` and its generic add/remove and child-property model are gone;
- any widget may have children, but mutation is widget-specific (`append`, `attach`, `set_child`,
  `set_start_child`, and so on);
- `<packing>` becomes `<layout>` or child meta objects;
- GtkBin, GtkAction, GtkUIManager, GtkToolbar, GtkRadioButton and many GTK 2-era widgets or
  patterns are removed or replaced;
- rendering is snapshot/GSK based;
- modern list widgets use models, selection models and factories rather than a directly edited
  TreeView/ListStore hierarchy.

These changes cut through Stetic's wrappers, palette metadata, placeholder containers, drag/drop,
property system, serialization and rendering. Making those classes compile would not make their
semantics correct.

## Realistic reuse assessment

The percentages below distinguish direct code reuse from reuse of behavior or ideas.

| MonoDevelop/Stetic area | Direct code reuse | Design reuse | Decision |
|---|---:|---:|---|
| GTK 2 widget wrappers and placeholders | 0–5% | 20% | Rewrite from GIR and GTK 4 container adapters |
| Live design surface and drag/drop | 0% | 25% | Replace with remote host plus WPF overlay |
| `gui.stetic` reader/writer and generated C# | 0–10% | 30% | Standard GtkBuilder `.ui` replaces both |
| Property/signal descriptors | 10–20% | 50% | Preserve concepts; populate from GIR and overrides |
| XML diff/undo algorithms | 40–60% | 70% | Optional reference; prefer text snapshots initially |
| `CodeBinder` Roslyn rename/handler ideas | 10–20% | 60% | Reimplement against current OpenDevelop Roslyn services |
| Toolbox categorization and custom libraries | 10% | 50% | New metadata pipeline, same user-facing behavior |
| Project/file watching and external-change policy | 10–20% | 70% | Use OpenDevelop `OpenedFile`, not Stetic project state |
| Icons, localized labels and property-editor UX | 5–15% | 40% | Curate and modernize; do not load GTK 2 assemblies |

Overall, expect roughly **5–15% direct source reuse** and **40–60% requirements/design reuse**.
Trying to increase the source reuse percentage would increase GTK 2 coupling and delivery risk.

The best candidates to extract or adapt are:

- the intent of `CodeBinder`: field rename, signal handler creation/rename and source navigation;
- XML identity/diff ideas from `undo/UndoManager.cs`, `DiffGenerator.cs` and
  `XmlDiffAdaptor.cs` after removing all wrapper dependencies;
- component categories, translatability rules, property reset semantics and signal editing UX;
- external-file-change and project-library refresh behavior.

## Binding and metadata baseline

Use `GirCore.Gtk-4.0` for C# GTK 4 fixtures and the preview host. GtkSharp is a GTK 3 binding and
must not define the GTK 4 designer model. Gir.Core is also the natural metadata companion because
GTK's supported cross-language contract is GObject Introspection.

The designer metadata service reads installed GIR XML rather than reflecting over target
assemblies in the IDE process. It produces a versioned, binding-neutral catalogue:

```text
GtkType
  girName              Gtk.Button
  builderClass         GtkButton
  parent
  abstract
  constructible
  properties[]         name, type, default, readable, writable, constructOnly
  signals[]            name, parameters, returnType
  childPolicy          none | single | ordered | grid | namedSlots | pageObjects
  since/deprecated
```

A small checked-in override table handles facts GIR alone cannot express well for a designer:
toolbox category, default size, preferred property, child insertion/removal calls, named child
slots, synthetic values, and unsupported-at-design-time types. GTK and Libadwaita catalogues are
separate capabilities; Libadwaita is not silently assumed for a GTK-only project.

Never load a user's output assembly, native library or custom widget into OpenDevelop. Custom
widgets are resolved and instantiated only by the disposable preview host, with timeouts and a
safe placeholder fallback.

## File convention

Use a standard GtkBuilder file next to the behavior class:

```text
Windows/
  MainWindow.cs       user-owned behavior
  MainWindow.ui       designer-owned GtkBuilder UI
```

`MainWindow.ui` is canonical GTK 4 XML, not Stetic XML:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<interface>
  <requires lib="gtk" version="4.0" />
  <object class="GtkApplicationWindow" id="mainWindow">
    <property name="title">Example</property>
    <property name="default-width">800</property>
    <property name="default-height">600</property>
    <child>
      <object class="GtkBox" id="contentBox">
        <property name="orientation">vertical</property>
        <property name="spacing">12</property>
        <child>
          <object class="GtkButton" id="runButton">
            <property name="label">Run</property>
            <signal name="clicked" handler="OnRunClicked" />
          </object>
        </child>
      </object>
    </child>
  </object>
</interface>
```

The C# file loads the resource/file through the binding's normal Builder API and owns handler
bodies. The first implementation should support object-root UI files. Composite templates can be
added after Gir.Core's template/subclass contract is pinned by fixtures; they are not required for
the first usable designer.

One `.ui` file is one undo/save/conflict unit. Do not recreate Stetic's project-wide `gui.stetic`
database or generated `gtk-gui/generated.cs` directory.

## Source model and strict edit rules

`GtkUiDocument` is an immutable XML syntax model preserving comments, unknown elements,
namespaces, translation attributes and insignificant formatting wherever an edit does not touch
them. Each element has a stable design identity:

1. GtkBuilder `id` when present;
2. a document-local syntax identity for anonymous objects;
3. never a live native pointer.

The designer supports only well-defined GtkBuilder constructs. Unsupported but valid XML is
round-tripped and shown as a read-only/placeholder node. It must never be discarded because the
designer does not understand it.

Edits are XML transformations:

- set/reset `<property>`;
- insert/remove/reorder `<child>`;
- set GTK 4 `<layout>` properties;
- add/remove `<signal>`;
- rename `id` and update object references in the `.ui` document;
- optionally invoke Roslyn rename/create-handler changes in the behavior class as a separate,
  coordinated workspace edit.

Before committing an edit, validate the new document with the internal schema/metadata model.
The preview host then performs authoritative GtkBuilder validation. `gtk4-builder-tool validate`
is also used in build/integration tests when installed.

Blueprint may be accepted later as an import/source-editor format, but it is experimental and
compiles to GtkBuilder XML. The first designer must author `.ui` XML directly; maintaining two
lossless authoring backends would double the hardest part of the project.

## Process architecture

The current implementation instantiates the document with Gir.Core inside the isolated child,
performs GTK measure/allocation, and returns each widget's `ComputeBounds` rectangle. The
transparent WPF selection overlay is therefore driven by GTK-native geometry, and
`design/hit-test` resolves the smallest native rectangle at the requested design coordinate. Pixel
frames come from GTK's own renderer so text, theme, sizing and widget styling survive. No GTK
runtime or target assembly enters OpenDevelop.

The native overlay handles direct pointer selection and sibling drag-reorder, shows an insertion
line during a drag, and accepts Toolbox string drags. A Toolbox drop selects the native widget
under the pointer and inserts into its nearest GtkBuilder container rather than defaulting to the
first window-level container.

```text
OpenDevelop (WPF/.NET 10)
  GtkDesignerViewContent
    ├── GtkUiDocumentEditor       source authority and undo
    ├── Outline / Properties / Tools pads
    ├── WPF selection/drag overlay
    └── GtkPreviewHostClient  ───── JSON-RPC + binary frames ────┐
                                                               │
GTK 4 preview host (separate process, Gir.Core)                 │
  ├── GtkBuilder parse/instantiate                              │
  ├── target GTK theme/CSS/resource loading                     │
  ├── layout + widget bounds/identity map                       │
  ├── hit testing                                               │
  └── mapped transparent Gtk.Window → snapshot → GSK/Cairo ──────┘
```

On macOS the child is a background UI service, not a foreground application. It applies
`NSApplicationActivationPolicyAccessory` before and after GTK initialization and is launched with
the background Launch Services flags and also calls the macOS Process Manager
`TransformProcessType` API for itself. The GTK main context and Cairo renderer live for the full
host lifetime. JSON-RPC work is dispatched to the host's main thread because AppKit requires native
windows to be created there. Each document owns a mapped, fully transparent GTK window that is
never presented; its content widget is snapped independently so window transparency does not erase
the preview pixels. Frames are keyed by normalized GtkBuilder text and root id, so a refresh that
only advances the protocol version reuses the PNG and native tree; source or property changes
invalidate the key and produce a new GSK frame. Do not invoke
`gtk4-builder-tool render` on macOS. Opening or editing a GTK document must not add
transient foreground applications to the Dock or application switcher, and repeated preview
refreshes must not create `GtkRenderHelper` or `gtk4-*` render processes.

The GTK main loop never runs in OpenDevelop's WPF process. This avoids toolkit event-loop,
native-library, theme and crash isolation problems and follows the existing remote designer
direction used elsewhere in OpenDevelop.

The preview shown in WPF is a GTK-rendered frame with a WPF overlay for selection rectangles,
drop targets, resize/spacing guides and diagnostics. This is not an approximate WPF recreation of
GTK widgets; text and theme details must come from the GTK renderer.

Minimum protocol messages:

- `initialize(runtime, gtkVersion, scale, theme, resourceRoots)`;
- `load(documentText, rootId, documentVersion)`;
- `render(viewport, scale)` → frame plus layout version;
- `getTree()` → ids, types, parent/slot/order and semantic flags;
- `getBounds(id)` and `hitTest(x, y)`;
- `setSelection(ids)` for native state-sensitive rendering where needed;
- `diagnostics()`;
- host lifecycle, cancellation and heartbeat.

Source edits remain in OpenDevelop. The host is never allowed to serialize UI back into source,
which prevents runtime defaults and binding-specific behavior from rewriting the document.

## Pads and interaction

### Tools

The Tools pad is populated from the GIR catalogue filtered by the project's GTK version and
capabilities. Items are grouped into Toplevels, Layout, Display, Input, Lists, Navigation, Media,
Menus and Project Widgets. A toolbox insert is valid only when the selected target's child policy
has a deterministic XML representation. Otherwise the designer asks the user to choose a named
slot or displays a precise rejection reason.

### Outline

Outline is derived from XML, not from the preview process. It includes non-widget Builder objects,
page/meta objects, named child slots and factories so the complete document remains editable.
Selection synchronizes surface, Outline and Properties by GtkBuilder id/syntax identity.

### Properties and signals

Properties come from GIR plus overrides. The grid distinguishes normal, construct-only, layout,
object-reference, translatable and CSS-related values. Reset removes the XML property so GTK's
default applies. Values are parsed by type before source mutation; invalid text never enters the
document.

Signals get a dedicated category. Creating a handler is a Roslyn workspace operation in the user
class. The `.ui` signal entry is committed only if the handler edit succeeds. BuilderScope/binding
support varies, so signal hookup must have a Gir.Core fixture proving the exact supported pattern.

### Drag/drop

GTK 4 has no generic container mutation API. Every drop is resolved through `childPolicy`:

- `GtkBox`: ordered child insertion;
- `GtkGrid`: child plus `<layout>` row/column/span;
- single-child widgets: replace/wrap decision;
- `GtkPaned`: start/end named slot;
- `GtkStack`, `GtkNotebook` and similar widgets: explicit page/meta object;
- list/model/factory widgets: edit their model/factory objects, not fake visual children.

This adapter layer is the most important new GTK 4-specific code and should have table-driven
tests independent of the native preview host.

## Migration from Stetic and GTK 3 UI

Migration is an explicit tool, not an implicit designer load path:

1. export/obtain GtkBuilder/Glade XML from the Stetic project where possible;
2. run `gtk4-builder-tool simplify --3to4` on a copy;
3. preserve a migration report for removed types, properties, actions, stock items and signals;
4. split project-wide UI into one `.ui` file per root object;
5. generate or update C# Builder-loading scaffolding;
6. require successful GtkBuilder validation and a real host render before declaring migration
   complete.

The GTK tool documentation explicitly describes `--3to4` as a starting point requiring manual
fixups. The OpenDevelop migration UI must present unresolved items instead of silently dropping
them.

## Delivery plan

### Phase 0 — binding/runtime spike

- pin a Gir.Core version and supported GTK 4 runtime range;
- build hello-window fixtures on macOS, Linux and Windows/MSYS2 where supported;
- prove GtkBuilder object loading, signals, resources, CSS, WidgetPaintable rendering and clean
  process shutdown;
- decide the binary frame transport after measuring PNG versus raw BGRA/shared memory.

Exit criterion: a host loads a `.ui`, returns a tree/bounds/frame, survives malformed input and is
restartable after a native crash.

### Phase 1 — source designer MVP

- `.ui` display binding and XML editor;
- GTK/Libadwaita GIR catalogue;
- Outline, typed Properties and grouped Tools pad;
- add/delete/reorder/set/reset/rename with undo/redo;
- strict round-trip preservation and internal validation;
- static placeholder preview until the remote host is ready.

Exit criterion: complete source-backed editing of Window/Box/Grid/Label/Button/Entry with no native
GTK loaded into OpenDevelop.

### Phase 2 — native GTK preview

- remote GtkBuilder host and rendered frames;
- stable bounds after consecutive layout samples;
- surface/Outline/Properties selection synchronization;
- theme, scale, CSS and resource reload;
- diagnostics and automatic host restart.

Exit criterion: the rendered result and bounds come from GTK 4 itself, and source edits refresh
without reopening the document.

### Phase 3 — production interaction

- real toolbox drag/drop and reorder;
- common Toolbox catalogue filtering (control or category, case-insensitive), including selection
  repair/restoration and `od.gtk-designer.toolbox.filter` integration coverage;
- the shared Tools-pad search field is visibly mounted and survives multi-window switching;
- Grid/Paned/Stack/page adapters;
- keyboard navigation, delete, copy/paste and multi-select where semantically valid;
- signals/Roslyn handler creation;
- external file conflict handling and coordinated save.

### Phase 4 — ecosystem depth

- custom project widgets in the sandboxed host;
- Libadwaita catalogue and named-child policies;
- menus, actions, list models, factories and expressions;
- translation and resource tooling;
- explicit GTK 3/Stetic migration assistant;
- optional Blueprint import/editor integration.

## Test strategy

Unit tests:

- lossless XML parse/serialize and trivia preservation;
- every property value codec;
- every child-policy insertion/removal/reorder transformation;
- stable identity, rename/reference updates and unsupported-node preservation;
- undo/redo and concurrent/external edit conflict behavior;
- Stetic/GTK 3 migration diagnostics.

Host contract tests:

- valid/malformed GtkBuilder documents;
- tree, bounds, hit-test and frame version consistency;
- theme/scale/resources/custom-widget failure;
- timeout, cancellation, crash and restart;
- no target assembly loaded in the IDE process.

OpenDevelop integration tests:

- open `.ui`, activate Design, verify Tools/Outline/Properties hosts;
- select on surface and Outline, edit a typed property, inspect saved XML;
- toolbox insert, drag/drop, reorder, delete, undo/redo and save/reopen;
- add/rename a signal handler and compile the fixture;
- edit two independent windows without cross-document state leakage;
- wait for repeated stable layout samples before asserting bounds;
- use semantic DevFlow state and native layout data; do not use OS screenshots.

Current executable coverage is split between `GtkDesignerTests` (the primary fixture and complete
pad/toolbar/lifecycle contract) and `GtkDesignerIntegrationTests` (a larger two-toplevel, deeply
nested GtkBuilder document). The former asserts that the real Tools and Outline pads host the
designer controls and that selection populates the real Properties pad before editing persisted
XML. It invokes measured Fit, Zoom and Gridlines rather than merely checking that buttons exist,
requires a non-empty native GTK frame, requires native bounds for every fixture widget, selects
the Run button through native coordinate hit-testing, exercises native sibling pointer-reorder mapping, persists signal and reorder mutations, and
validates the final document with the installed GTK 4 validator. It also covers delete/undo/redo,
save/close/reopen, Properties rebinding after reopen, and compilation of the mutated fixture.
The fixture also opens MainWindow and SettingsWindow concurrently, requires the same host process
ID, edits and saves the second document without changing the first, closes it, and then proves
that the first document's original host and selection remain usable.
Native render failures are
returned as session warning diagnostics instead of silently falling back.

Still-open coverage gaps are OS-level automation of the pointer gestures (the native coordinate
mapping and resulting mutation are covered), behavior-file handler generation, malformed/external
edit recovery, and GTK theme/resource/custom-widget variants. Those items in
the lists above are acceptance targets, not claims about the present implementation.

Fixture matrix:

- plain Gtk.Window/Gtk.ApplicationWindow;
- Box and Grid layout properties;
- Paned/Stack/page objects;
- signals and translatable strings;
- CSS and embedded resources;
- Gir.Core composite template once supported;
- Libadwaita project;
- custom widget success/failure;
- converted GTK 3/Stetic sample.

An integration test that calls internal edit actions without asserting the actual active Tools and
Properties pad contents is insufficient. Pad ownership, realized item counts, selected object
identity and persisted source must all be asserted.

The shell binding is now implemented by the shared `DesignerPadController`: a host tree refresh
updates the live Outline, restores selection by GtkBuilder id, and rebinds the live Properties pad
to the corresponding `GtkPropertyAdapter`. Outline selection commits through the same controller,
whose re-entry guard prevents the asynchronous Outline notification from selecting twice. Common
DevFlow result envelopes and the shared-host lifecycle stress test are defined in
[`designer-common.md`](designer-common.md#shared-shell-contracts-completed-2026-08-24).

GTK selection is no longer a single-selection exception. `DesignerSelectionController` owns the
ordered id set and primary object, Ctrl-click toggles membership, and
`od.gtk-designer.multi-select` exercises replacement selection in integration tests. The Outline
tracks the primary object and the Properties pad receives their common editable property
intersection through `DesignerMultiPropertyAdapter`; edits are broadcast to each selected
`GtkPropertyAdapter`. The primary integration test performs that edit through the realized shared
Properties-pad `PropertyItem` and verifies both GtkBuilder objects in the saved `.ui`; the action
uses the common `selectedIds`/`primarySelectedId`/`selectionCount` result contract.

Undo, Redo and Delete are registered through the shared `DesignerCommandController`. The GTK
document host publishes exact history availability in every `DesignerSessionState`, so toolbar,
keyboard, global menu and automation enablement all follow the same state after edit/undo/redo.

GTK signals are first-class Properties-pad events. The child publishes the supported signal names
and current handler for each element in `DesignerElementNode.Events`; `GtkPropertyAdapter` exposes
them through `ICustomTypeDescriptor`, `IPropertyGridEventSource` and `IEventBindingHost`. Editing or
double-click binding uses versioned `design/set-event`, updates the GtkBuilder `<signal>`, rebuilds
the tree and participates in undo/redo. Integration binds `clicked` through the actual selected
Properties-pad object and verifies the conventional `runButton_clicked` handler in saved XML.

GTK implements `IDesignHostHitTesting` from native measured widget bounds, but deliberately does
not implement `IDesignHostBounds`: GtkBuilder containers own child layout and there is no
framework-independent absolute move/resize operation to expose through DDP.

## Risks and non-goals

- GTK 4 native runtime availability differs by operating system; installation diagnostics are a
  first-class feature, not a generic preview error.
- Gir.Core evolves and its Builder/composite-template behavior has had breaking changes; pin it
  behind the host protocol and fixtures.
- GTK XML can contain arbitrary custom Buildable semantics. Unknown constructs are preserved but
  may remain source-only.
- Pixel dragging cannot replace GTK layout semantics. The designer edits container constraints,
  not absolute coordinates except for GtkFixed.
- Phase 1 does not execute application code, constructors, handlers or arbitrary expressions.
- Reusing Stetic's GTK 2 runtime, `gui.stetic`, generated folder or wrapper hierarchy is explicitly
  out of scope.

## Primary references

- GTK 4 GtkBuilder: <https://docs.gtk.org/gtk4/class.Builder.html>
- GTK 3 to GTK 4 migration: <https://docs.gtk.org/gtk4/migrating-3to4.html>
- `gtk4-builder-tool`: <https://docs.gtk.org/gtk4/gtk4-builder-tool.html>
- GtkWidgetPaintable: <https://docs.gtk.org/gtk4/class.WidgetPaintable.html>
- GtkSnapshot: <https://docs.gtk.org/gtk4/class.Snapshot.html>
- Blueprint compiler: <https://gnome.pages.gitlab.gnome.org/blueprint-compiler/>
- Gir.Core GTK 4 binding: <https://github.com/gircore/gir.core/blob/main/docs/index.md>
