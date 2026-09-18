# WinUI Designer Runtimes: Uno, Windows App SDK, and ProGPU

## Native WinUI long-term loading contract: research correction (2026-09-13)

This section supersedes earlier claims that removing `AnimatedIcon.State`, replacing toolkit
controls, or rendering a substituted page establishes a correct native WinUI loader. The
SettingsPage root cause was application-generated metadata being bypassed by the framework
provider; the production fix and its no-workaround acceptance test are documented below.

### Verified source and temporary-project evidence

Source inspected: local `../ms-ui-xaml-upstream`, commit
`25d2cb1c6e4086dd14b387a4a149cce0649dbe17`. This identifies the inspected upstream source,
not the exact source revision of the Gallery's installed native binaries.

- `src/controls/dev/AnimatedIcon/AnimatedIcon.idl` publicly declares `StateProperty`,
  `SetState(DependencyObject, String)` and `GetState(DependencyObject)`.
- `src/controls/dev/Generated/AnimatedIcon.properties.cpp` registers `State` with
  `true /* isAttached */`; its accessors call the target's `SetValue`/`GetValue`.
- `src/controls/dev/AnimatedIcon/APITests/AnimatedIconTests.cs` exercises state on parent
  elements and directly on AnimatedIcon. Therefore State is a real runtime attached dependency
  property. Calling it a compiler-only property unsupported by definition is incorrect.

A temporary, independent .NET console project was created and executed at
`C:\Users\lextudio\AppData\Local\Temp\WinUIDesignerResearch\TreeProbe.csproj`:

```powershell
dotnet run --project C:\Users\lextudio\AppData\Local\Temp\WinUIDesignerResearch\TreeProbe.csproj
```

It reproduces the current substitution algorithm using three nested SettingsCard XML nodes:

```text
Parent-first: reported replacements=3, live SettingsCard remaining=2
Child-first: reported replacements=3, live SettingsCard remaining=0
```

LINQ to XML copies a node added to another parent while the original still has a parent.
The precomputed traversal subsequently edits detached originals, not the copies in the live
document. This explains how a replacement count can exceed the number actually replaced.
It disproves the inference that residual controls necessarily came from a later resource merge.
The temporary project verifies XML transformation semantics only: it does **not** run WinUI,
prove a native crash cause, or validate SettingsPage rendering.

### Architecture decision

**Native experiment update:** A real temporary WinUI project now exists at
`C:\Users\lextudio\AppData\Local\Temp\WinUINativeProbe\Probe.csproj`. It uses
Windows App SDK 2.1.3, net9.0-windows10.0.22621.0, win-arm64, unpackaged,
WindowsAppSDKSelfContained=true and SelfContained=false. Built successfully using VS 18
MSBuild `/restore /t:Build`, then executed its generated Probe.exe directly.

Controlled A/B result (same C# loader and runtime settings):

| App.xaml input | Framework resources | Direct SetState/GetState | Loose XamlReader State |
|---|---|---|---|
| Only XamlControlsResources | Success | Normal | State not found, line 1 position 116 |
| Also a keyed Grid with controls:AnimatedIcon.State="Normal" | Success | Normal | Normal |

The second build's `obj/Debug/net9.0-windows10.0.22621.0/win-arm64/XamlTypeInfo.g.cs`
contains an AnimatedIcon type entry with `userType.AddMemberName("State")` and generated
accessors. Logs are in the corresponding `bin/.../win-arm64/probe.log` (append-only).
This proves that **compiler-generated application metadata coverage changes whether loose
XAML can load this attached property**, even though the runtime API works in both cases.
It also proves standard unpackaged self-contained framework-resource initialization works
in this minimal app. It does not yet establish the cause of the separate native crash.

Concrete new solution to pursue: generate a preview application with metadata coverage for
the designed markup, and expose that application's generated provider as the primary provider.
The current host's owning-assembly-only selection is insufficient as a general strategy:
the application compiler generates metadata for members of types owned by other assemblies.
Returning a non-null framework IXamlType does not prove it describes every required member.
Validate provider ordering/coverage with this A/B case before removing production workarounds.

Use a **project-specific, compiled preview application running out of process**, with a small
designer bootstrap and the project's evaluated dependency/resource graph. Keep the IDE side
responsible for source editing, selection and IPC. The native child owns WinUI objects, the
UI thread, window/XamlRoot, metadata, resource lookup and rendering. No native WinUI object
crosses into the WPF shell.

The preview application should be generated into a disposable cache from evaluated MSBuild
inputs. Its identity includes TFM, RID, configuration, Windows App SDK package graph, deployment
mode, relevant compiler inputs and referenced outputs. Build it with the actual WinUI XAML/PRI
toolchain. Do not substitute a newer bundled SDK merely because it resolves more type names;
log the actual loaded managed and native module paths/versions to verify the selected graph.

The preview Application should use generated metadata and normal framework resources, including
XamlControlsResources, in a valid initialized WinUI lifecycle. Resolve app and library compiled
resources with their original URI identity and resource context. A host-local XamlControlsResources
failure is a bootstrap/resource defect to investigate, not evidence that unpackaged WinUI generally
cannot use it. Avoid cloning megabytes of a different framework version's theme source into every
page or extracting XBF into guessed paths as the normal resource mechanism.

Use two explicitly different loading paths inside that application:

1. **Loose XAML preview** for edits representable by XamlReader, with generated-provider lookup
   and valid application resources. Reflection is a deliberate fallback for missing metadata,
   not a replacement for the full framework/provider contract. Trace provider identity and
   GetMember/IsAttachable results before adding any member-specific workaround.
2. **Compiled preview** for x:Class, x:Bind and other compilation-dependent behavior. Generate
   and compile a preview page/component with the WinUI toolchain, load its generated component
   and matching resources, and replace/restart the child when assembly/resource generations
   cannot safely coexist. Define code-behind execution and design-data policy explicitly;
   merely calling the user's Application/Main is not a safe or sufficient preview bootstrap.

Link reusable original host/protocol C# into this generated application, consistent with the
existing source-link approach. Place framework-neutral project fingerprinting, build/cache and
child-process coordination in designer common; WinUI compiler, IXamlMetadataProvider, PRI and
XamlRoot handling remain in the WinUI adapter. Do not put a toolkit-specific Border substitution
in framework-neutral common code or assume it applies identically to Uno.

### Native experiment gates still required

**Compiled preview path verified:** `WinUINativeProbe/PreviewPage.xaml` now declares a
StackPanel with `AnimatedIcon.State="Normal"` and a named real SettingsCard containing a
ToggleSwitch. Its partial Page calls generated InitializeComponent. The application creates
that Page, reads State and the named card through code-behind, attaches it to the offscreen
Window and renders the Page. VS MSBuild succeeded and Probe.exe exited 0 with:

```text
compiled state=Normal; card=CommunityToolkit.WinUI.Controls.SettingsCard
layout=1365x70; template=True; children=1
render=1365x70
```

The independent loose State probe also succeeds again because the preview Page participates
in application metadata generation. This verifies the core proposed path end-to-end in the
temporary app: XAML compilation → generated component/metadata/resources → real third-party
template → offscreen render. The research has identified and demonstrated a new solution;
this is not a claim that the production designer or Gallery SettingsPage has been fixed.

Recommended implementation sequence:

1. Generate a disposable preview project from the evaluated target project and compile the
   preview Page and resource inputs with its matching toolchain. Keep original files untouched.
2. Link the existing RPC/bootstrap-independent C# into that child, with normal generated
   Application metadata and resources replacing the reflection-first bootstrap.
3. Bring in Gallery resource dependencies one at a time, preserving library PRI/XBF identities;
   validate SettingsPage with actual named controls through DevFlow before enabling the path.
4. Remove the now-unnecessary State stripping, toolkit proxies and framework-theme copying
   only after their corresponding real-control cases pass. Surface unsupported previews as
   diagnostics rather than silently reporting a substituted control as faithful rendering.

**Additional control experiment:** Removing the explicit State declaration from App.xaml
while keeping the Toolkit package makes loose `<Grid c:AnimatedIcon.State="Normal"/>`
fail again. Catching that failure separately and continuing in the same process still loads
and renders the real SettingsCard at 1365x70 with its default template. Process exit is 0.
Thus the library's compiled template and arbitrary loose member lookup are different paths;
SettingsCard's template is not inherently incompatible with this unpackaged/offscreen host.
The generated application's OtherProviders list includes the framework and SettingsControls
providers, yet package aggregation alone does not give arbitrary loose markup full coverage.

Implementation consequence: generate/compile the preview document (including its resource
dependencies) so its required members participate in application metadata generation, or build
an explicitly validated equivalent metadata bridge. Merely generating an empty application
and referencing packages is insufficient. Preserve library compiled resources instead of
flattening those templates into loose XAML. This is the experimentally supported new approach;
integration into OpenDevelop and diagnosis of its separate native exception remain follow-up work.

**Third-party follow-up verified:** The same temporary Probe.csproj now references
`CommunityToolkit.WinUI.Controls.SettingsControls` 8.2.251219 (the Gallery version).
Its loose XAML creates a real SettingsCard with Header, Description and a ToggleSwitch.
No SettingsCard declaration was added to compiled App.xaml; the referenced library's generated
provider participates through normal generated application metadata. The AnimatedIcon.State
compiled declaration from the preceding experiment remains present.

After activating a real Window, moving it to (-32000,-32000), waiting for layout and calling
RenderTargetBitmap.RenderAsync on the card, the process exited with code 0 and logged:

```text
resources initialized
direct=Normal
loose=Normal
card=CommunityToolkit.WinUI.Controls.SettingsCard
layout=1365x70; template=True; children=1
render=1365x70
```

No app-PRI disable switch, reflection metadata provider, vendored theme dictionary, State
stripping or control proxy is used. No screenshot was captured or image file saved; the
renderer was exercised and only dimensions logged. This establishes an executable proof
of the proposed bootstrap/metadata/resource approach for the specific third-party control
and an offscreen rendering lifecycle. It does not prove the full Gallery SettingsPage or
all interactive template states. Port this working baseline into the designer adapter before
adding Gallery's resource graph incrementally; preserve this project as the control case.

The architecture above is the proposed long-term direction, not a proven implementation. The
next temporary **WinUI** project must run the following controlled cases with the Gallery's
evaluated SDK/RID/deployment mode, without string stripping or control substitution:

| Case | Required evidence |
|---|---|
| Normal compiled Application + XamlControlsResources | Successful initialization; loaded native module paths |
| Grid + AnimatedIcon.SetState/GetState | Round-trip `Normal` on the UI thread |
| Loose XAML using explicitly qualified AnimatedIcon.State | Successful load and state read-back, or exact provider/member trace |
| Same markup compiled by WinUI | Compare compiled and loose results under the same runtime graph |
| Real SettingsCard with standard resources | Actual CLR type, template application and nonzero layout |
| App/library PRI and SettingsPage | Resource identities, live named controls and nonzero rendered frame |

Change one input at a time between the normal generated application and the designer bootstrap.
If a native exception occurs, record its first relevant stack/stowed exception and the last
metadata/resource request. A JSON-RPC disconnect, changed parse position, or disappearance of
one log message is not a native root cause. A different earlier parser error does not prove the
original later error has been eliminated.

Completion requires the real SettingsPage in OpenDevelop via DevFlow, valid frame dimensions
and live control geometry, with diagnostic PRI-disable and substitution switches absent. It also
requires a documented response to compilation-dependent markup and resource reloads. Temporary
proxies can only be an explicit degraded-preview mode; they cannot satisfy that acceptance gate.

The State stripping and SettingsControls substitutions described by earlier investigation notes
were removed after the production path below rendered the real control. Previous claims of SDK
incompatibility or template crash causes should be treated as historical hypotheses, not facts.

### Application metadata precedence fix (2026-09-13)

The host now identifies the designed executable from its output directory's runtimeconfig file
and prefers that application's generated `IXamlMetadataProvider` before the provider from the
CLR assembly that owns each requested type. This is necessary because application XAML compilation
can generate a member overlay for a framework type; resolving `AnimatedIcon` directly from the
framework provider loses the application's `State` member metadata.

Verified through DevFlow against real `WinUIGallery/Pages/SettingsPage.xaml`, ARM64
Debug-Unpackaged, with neither `OD_DESIGNHOST_NO_DESIGN_TEMPLATES` nor
`OD_DESIGNHOST_NO_APP_PRI` set:

```text
Rendered by WinUI design host (1208×729).
SpatialAudioCard and spatialSoundBox exist in the resolved name tree; child process remains alive.
design-host: application metadata assembly is WinUIGallery.
design-host: serving app resources from WinUIGallery.pri.
design-host: using application XAML metadata from WinUIGallery before framework metadata.
```

Both SettingsControls proxy paths and all AnimatedIcon.State stripping have been removed from
the final document and vendored framework theme. `GallerySettingsPage_UsesApplicationMetadataAndRendersRealToolkitControl`
is the regression gate: it requires a nonzero rendered frame, real named SettingsCard content,
the application metadata and PRI log markers, and absence of the old proxy/state-rewrite markers.
It passed on 2026-09-13 against Gallery ARM64 Debug-Unpackaged (one test, 32.8 seconds).
The complete `WinUIGalleryDesignerTests` Microsoft-host corpus then passed 25/25 in 10 minutes
18 seconds: 21 rendered pages, two intentional abstract-root diagnostics, backend routing, and
this real SettingsCard acceptance case.

This technote is the dedicated home for the WinUI-family designer: architecture decisions, the
XAML Studio/ProGPU integration boundary, packaging workflow, the current state, and the
real-world preview problem catalog (updated 2026-08-15). The cross-designer roadmap (WinForms + WPF +
WinUI together), framework detection, provider contracts, phases, and the test matrix live in
[`xaml-services.md`](xaml-services.md).

The parent workbench now shares `Designer.Shell.DesignerSelectionController` with the other four
designers for the element forest, stable-ID selection restoration, and Properties adapter
lifetime. `DocumentOutlineControl` is the common WPF view over that state; Uno/WinUI runtime
loading, XAML mutations, hit testing, and rendering stay backend-specific.
Undo, Redo and Delete now execute through the common `DesignerCommandController`, while WinUI
retains its XML history, source mutations, multi-selection gestures and preview synchronization.

Current status: the out-of-process Uno host is implemented and is the preferred renderer for Uno
projects. It starts a real Uno 6.5.31 `net10.0-desktop` child, loads XAML and application
resources, renders a bitmap, returns a visual-tree snapshot and hit-test results, and supplies a
runtime-derived Toolbox catalog. The WPF-side surface also implements selection overlays, zoom,
pan, design-size changes, drag/resize source edits, inline text editing, unnamed-element picking,
and child-process lifecycle probing. The `WinUIDesigner_*` integration tests cover the shared
source-edit/render path against `src/Samples/UnoXamlSample`.

The Uno client explicitly implements the optional `IDesignHostEventBinding` DDP capability. Its
versioned `design/set-event` validates the live element/event name and keeps the incremental
preview synchronized; it does not create or compile a code-behind method, which remains a
backend-specific source-edit concern.

There are **three supported runtime profiles**, not two interchangeable names for one renderer:

| Project/runtime profile | Renderer | Host model | Current state |
|---|---|---|---|
| Uno Platform (`Uno.Sdk`, `Uno.WinUI`) | Bundled Uno 6.5.31 runtime today; project runtime is the target | Out of process | Implemented for the fixture; project-version loading remains |
| ProGPU WinUI (`ProGPU.WinUI`) | ProGPU's WinUI-shaped runtime and compositor | In process in the LibreWPF shell | Standard-control path implemented; must be routed explicitly |
| Native WinUI (`Microsoft.WindowsAppSDK`) | The project's Windows App SDK/WinUI runtime | Windows-only, out of process | Planned adapter |

The repository does not yet encode that routing completely. `XamlFrameworkKind` currently has
only `WinUI` and `Uno`; the detector recognizes Uno and Windows App SDK markers but not
`ProGPU.WinUI`. Both host factories are then registered globally, with the Uno factory tried
first whenever its child binary exists, regardless of framework kind. Before calling all three
profiles product-complete, add an explicit ProGPU profile (or an equivalent runtime discriminator)
and make each factory decline projects it does not own. ProGPU must not be a silent fallback for
an Uno or Windows App SDK project, and the Uno child must not claim a ProGPU or native WinUI
project.

## Architecture

### Reuse Boundary with UnoDevelop and XAML Studio

UnoDevelop's `src/AddIns/DisplayBindings/XamlDesigner/` has implemented native `Microsoft.UI.Xaml` Source/Design secondary views, a Toolbox provider, an Outline provider, Properties Pad wiring, and integration tests. OpenDevelop reuses that IDE wiring approach but does not re-implement the renderer: the original `XamlRenderService` in `externals/xamlstudio/XamlStudio.Toolkit/Services/XamlRenderService/` and its models/extensions are the upstream code, consumed through linked source or a standalone toolkit project, with only the narrow adaptations required to compile. Its algorithms must not be rewritten as a WPF XAML parser, nor maintained as a behavior-forked copy.

UnoDevelop/XAML Studio's UI files cannot directly become OpenDevelop WPF visuals: the former's control types are `Microsoft.UI.Xaml.*`, while the latter's shell and document views are `System.Windows.*`. The two visual trees must be isolated by an explicit host, similar to how the WinForms designer embeds WPF through `WindowsFormsHost` rather than loading WinForms controls as WPF controls.

### External References and Dependencies

```text
externals/
├── xamlstudio/       `https://github.com/lextudio/xamlstudio` submodule; linked reuse of the original renderer source
├── vscode-wpf/
│   ├── external/WpfDesigner/   OpenDevelop's WPF designer engine
│   └── external/wxsg/          XAML language services and framework profiles
└── AXSG (included transitively by wxsg)    XAML analysis/generation foundation
```

The submodules are pinned to the WinUI migration commit of `origin/unodevelop`, and the XAML Studio renderer already uses `Microsoft.UI.Xaml`; it still cannot compile into the WPF shell, but should compile into a standalone WinUI/Uno renderer assembly. Upstream still contains UWP Storage/Media assumptions; the port should isolate those APIs behind small platform adapters while preserving upstream file identity and licenses; it must not be replaced by WPF's `System.Windows.Markup.XamlReader`.

OpenDevelop owns the `externals/xamlstudio` submodule directly; it must not fetch source indirectly through a sibling directory in the UnoDevelop parent repository. The current ingestion baseline is commit `d711d64fed7d07d5c2dda545d255d1007588ab78`; when upgrading the submodule, the renderer compilation, standard control rendering, and error-recovery tests must be re-run.

### Runtime Routing Decision (2026-08-15)

The common `Microsoft.UI.Xaml` namespace is a source-level compatibility surface, not a CLR type
identity guarantee. Runtime selection therefore follows project evidence and is never chosen by
asking which renderer happens to be installed:

1. `Uno.Sdk`, `Uno.WinUI`, or `Uno.UI` selects the out-of-process Uno adapter.
2. `ProGPU.WinUI` (or an explicit future ProGPU project property) selects the in-process ProGPU
   adapter. This is the supported cross-platform **WinUI on ProGPU** profile.
3. `Microsoft.WindowsAppSDK`, `Microsoft.UI.Xaml`, or `UseWinUI=true` selects the native Windows
   App SDK adapter and is available only on Windows.

The routing order above is also the detector precedence when a project contains incidental
references from more than one family. A conflicting project should produce a diagnostic rather
than fall through to a different runtime. The host registry contract remains useful, but its
factories must be predicates over `XamlFrameworkContext`, not availability-based fallbacks.

ProGPU is a legitimate renderer for projects authored against `ProGPU.WinUI`; it is not a
compatibility renderer for assemblies built against Uno.WinUI or the Windows App SDK. Conversely,
the Uno child must run the project's Uno assemblies, and a native WinUI child must run the
project's Windows App SDK assemblies. No `Microsoft.UI.Xaml` object crosses into the WPF shell in
any profile.

### ProGPU WinUI Host

> **Updated 2026-08-15:** the in-process ProGPU path is supported for projects targeting
> `ProGPU.WinUI`. It remains retired as a renderer or fallback for projects targeting Uno.WinUI
> or the Windows App SDK because those assemblies have incompatible type identities.

This profile uses the `Microsoft.UI.Xaml` implementation supplied by `ProGPU.WinUI`; it does not
load Uno or Windows App SDK UI assemblies. ProGPU currently materializes pages through its XAML
compiler/Roslyn preview assembly and does not provide `Microsoft.UI.Xaml.Markup.XamlReader`;
therefore XAML Studio's preprocessing, binding inspection, diagnostics, and result model remain as
original linked source, while the final instantiation point connects to the ProGPU pipeline
through `IProGpuXamlExecutor`. The WPF hosting part is built on the ProGPU render surface/
`IWindowHost` and plays a role similar to `WindowsFormsHost`.

The current hosting control uses `WgpuContext`, `ProGPU.Scene.Compositor.RenderOffscreen`, and a WPF `WriteableBitmap`. Each arrange rebuilds the render target at the WPF DPI; WPF mouse/wheel/text/focus events are converted into ProGPU `InputSystem` events; unload/dispose cancels the frame callback and releases the staging buffers, textures, compositor, and context. The first version uses GPU-to-CPU readback to verify correctness first; later it should switch to same-device texture sharing between LibreWPF and ProGPU to avoid per-frame synchronous readback.

The former dependency gap is closed. The published `0.1.0-preview.47` feed was built from the
`wieslawsoltes/ProGPU` branch `openwpf` at commit `bab4dbef993f2b2d722ff46689021604c2e9b947`
(recorded in the `ProGPU.WinUI` nuspec `<repository>` element); that branch no longer exists on
GitHub, but the commit is still fetchable, and at that commit `ProGPU.Xaml`, `ProGPU.Xaml.Roslyn`,
`ProGPU.Xaml.Workspaces`, and `ProGPU.WinUI.Designer` all exist and are already marked
`IsPackable=true`. They were simply never published. OpenDevelop therefore hosts them itself:
they are packed from that exact commit and dropped into the same local feed as
`0.1.0-preview.47`, so there is no version drift and `LibreWPF.ProGPU` preview.41 — which was
compiled against ProGPU preview.47 — keeps its binary-compatible dependency closure. The
`ProGPU.*` pattern already present in `NuGet.config`'s `packageSourceMapping` covers the new
packages without configuration changes. Upstream's default branch is at the preview.48 baseline
(`d63f5cfa`, 2026-08-12) and still marks all four packable; OpenDevelop remains pinned to the
internally consistent preview.47 package set. If ProGPU publishes the missing packages, the local
copies should be dropped rather than upgraded piecemeal.

Note that ProGPU exposes **no** runtime XAML parser: `ProGPU.WinUI` contains a
`Microsoft.UI.Xaml.Markup` namespace and a `MarkupExtension` base type, but no `XamlReader`,
no `LoadComponent`, and no `IXamlMetadataProvider`. Materialization is only ever
compiler-driven, which is why `IProGpuXamlExecutor` is the correct seam.

- OpenDevelop's WPF visual tree does not host `Microsoft.UI.Xaml.UIElement` directly;
- WinUI dispatcher, resource lookup, XamlRoot, input, focus, and DPI can be embedded into a WPF document tab;
- when loading custom controls of the designed project, dependencies and `x:Bind`/code-behind can be safely isolated.

Implementation order and acceptance items:

1. Establish a standalone renderer/host assembly that links the XAML Studio renderer source files and renders pages containing only standard controls through the ProGPU XAML compiler/preview assembly pipeline.
2. Display the render surface in the WPF document area with the new ProGPU-in-WPF host; verify resize, input, focus, DPI, and theme resources.
3. Load valid/invalid XAML in succession and verify that exceptions never pollute the IDE and that the last valid preview can be restored.
4. After unloading a document, check that threads, windows, events, and the collectible load context are released.
5. Run at least once on Windows and on each non-Windows target ProGPU currently supports.

The shell boundary stays replaceable:

- **In-process ProGPU WPF host.** A WPF hosting control similar to `WindowsFormsHost`; the
  renderer stays a separate assembly. This path serves the ProGPU WinUI profile only. It is a
  dead end for Uno and native Windows App SDK project assemblies, but that does not invalidate it
  for projects that actually reference ProGPU.WinUI.
- **Out-of-process preview host.** A small WinUI/Uno preview process that exchanges XAML, project
  context, viewport, and selection over JSON-RPC, hosting the preview in a native child window or
  a captured surface (the same shape as `DesktopWindowXamlSource` on Windows). This is now the
  target architecture for real-project support, not merely an isolation upgrade.

The WPF `XamlReader` compatibility renderer that was implemented at one point is not part of any official path: it conflates the object models, resource semantics, and control capabilities, and must be deleted — tests must not treat its successful rendering as a successful WinUI/Uno designer.

### Out-of-process host decision for Uno and native WinUI (2026-08-14; clarified 2026-08-15)

**Decision: Uno and native Windows App SDK project support requires an out-of-process host running
the project's actual runtime.** This supersedes the original framing of out-of-process as merely "Option B, a fallback
for untrusted assemblies" (the "Host stays replaceable" list above records the earlier framing).
Two independent findings from implementing Fix A (below) forced this:

1. **Type identity.** `ProGPU.WinUI` is a from-scratch reimplementation of `Microsoft.UI.Xaml` —
   its `FrameworkElement`, `Button`, etc. are unrelated CLR types that merely share a name and
   namespace with the real Uno.WinUI SDK's types of the same name. A single Roslyn compilation
   can reference at most one of them without "ambiguous type" errors, so the preview compiler can
   never see both ProGPU.WinUI (needed to materialize *anything*, since it is the only runtime
   this in-process host can actually render) and the project's real Uno.WinUI references (needed
   to resolve `muxc:InfoBar`, the real `Grid.ColumnSpacing`, etc.) at the same time. This is a
   structural ceiling on the in-process host, not a missing feature - no amount of `ProGPU.WinUI`
   API completion (roadmap item B below) removes it for a project that references Uno.WinUI types
   ProGPU.WinUI doesn't implement.
2. **Runtime load, not just compile-time resolution.** Fix A adds the opened project's own output
   assembly (e.g. `UnoXamlSample.dll`) as a `MetadataReference` for the preview *analysis*
   compilation - this genuinely resolves the project's own converters/custom controls/code-behind
   as *types* (category 1+2 in the diagnostics catalog below no longer appear). But the generated
   preview program is materialized into a separate collectible `AssemblyLoadContext`
   (`WinUiXamlLivePreviewSession`'s `PreviewAssemblyLoadContext`, in `ProGPU.WinUI.Designer`) that
   has no load path to the project's own build output directory. Verified live: after Fix A, a
   converter type resolves cleanly with zero diagnostics, but materialization then fails with
   `Could not load file or assembly 'UnoXamlSample, Version=1.0.0.0, ...'. The system cannot find
   the file specified.` — trading a wall of compile-time diagnostics for a single, clearer
   runtime-load error, but still not a rendered preview. Teaching that ALC to probe the project's
   output directory is a small, separate fix (`PreviewAssemblyLoadContext.Load` override) and
   *would* work for the project's own assembly - but finding 1 still blocks it the moment the
   project references any real Uno.WinUI-only type, which real Uno projects do pervasively (every
   `Page`/`FrameworkElement` base class the generated program itself needs to be ProGPU.WinUI's,
   while the project's compiled code needs the real Uno.WinUI's - the same collectible ALC cannot
   satisfy both for a project that mixes them, which is every real Uno project, not an edge case).

This is exactly the problem class Microsoft's own out-of-process WinForms designer solves for the
in-process-hosting equivalent risk (see
[the .NET blog post on it](https://devblogs.microsoft.com/dotnet/custom-controls-for-winforms-out-of-process-designer/)):
run the *real* runtime the project targets in its own process, and talk to it over RPC, rather
than trying to reconcile two incompatible in-process object models. For WinUI/Uno the need is
structural (type identity), not merely defense-in-depth against a crashing/untrusted assembly -
WinForms does not have this specific same-name type-identity forcing function, but its own
out-of-process boundary is also required for target-runtime and third-party designer isolation
(see [`winforms-designer.md`](winforms-designer.md#out-of-process-host-decision-2026-08-15)).

**What ships now vs. later (updated 2026-08-15):**

- **There is no cross-runtime in-process fallback.** The out-of-process Uno host is the only valid
  renderer for Uno projects, including `src/Samples/UnoXamlSample`. `ProGpuRuntimeHost` and
  `ProGpuXamlExecutor` remain valid for the separate ProGPU WinUI profile; they must never be used
  to materialize a project assembly built against Uno.WinUI or the Windows App SDK.
- Fix A's compile-time half (project assembly as a `MetadataReference`) remains valid for what it
  is - quick in-process feedback - but no longer feeds a product renderer; diagnostics for real
  projects come from the child process's own runtime parser instead.
- Roadmap item B (ProGPU.WinUI API completion) remains relevant to the ProGPU WinUI profile, but
  does not unblock Uno or Windows App SDK compatibility.
- Out-of-process scoping: see "Out-of-process host scoping (2026-08-14)" below.

### Out-of-process host scoping (2026-08-14)

Verified ground truth this scoping rests on (all from the Uno source tree `uno-tools/uno`, package
line 6.6.184, and the DotUninstall project's restored graph):

1. **The project's runtime is loadable on macOS.** A Uno.Sdk `net10.0-desktop` project (e.g.
   DotUninstall) *is* a Skia desktop app; its `bin/Debug/net10.0-desktop` contains the full runtime
   (`Uno.WinUI.Runtime.Skia`, `...Skia.MacOS`, SkiaSharp, FluentTheme, fonts). No Windows runtime
   is involved at any point.
2. **Uno has a real runtime XAML parser.** `Microsoft.UI.Xaml.Markup.XamlReader.Load(string)`
   (`src/Uno.UI/UI/Xaml/Markup/XamlReader.cs`) runs the production `XamlStringParser` +
   `XamlObjectBuilder` (`src/Uno.UI/UI/Xaml/Markup/Reader/`), which resolves types from loaded
   assemblies and defers resource/template expansion via a post-action queue. This entirely
   replaces the ProGPU compiler pipeline (the XamlStudio/ProGPU.Xaml.Roslyn port in this repo) for
   the OOP path: no Roslyn compilation, no collectible ALC, no type-identity conflict, because
   "the runtime" and "the project's runtime" are the same assemblies in one process.
3. **Offscreen rendering needs no window.** `RenderTargetBitmap.RenderAsync(element)` on Skia
   (`src/Uno.UI/UI/Xaml/Media/Imaging/RenderTargetBitmap.skia.cs`) creates a CPU `SKSurface`,
   forces the software compositor (`Compositor.IsSoftwareRenderer = true`), clears the layout clip,
   and renders `element.Visual` via `RenderRootVisual` - no Metal, no NSApplication, no visible
   surface. `GetPixelsAsync()` returns BGRA8 premultiplied pixels. DPI resolves to 1 when there is
   no XamlRoot/current view.
4. **Headless boot is a dispatcher override, not a native host.** `NativeDispatcher` on Skia
   exposes `DispatchOverride` + `HasThreadAccessOverride` (the exact hooks `MacSkiaHost` sets via
   `MacOSDispatcher`); after setting them, `Application.Start(...)` initializes the full runtime
   (`Application.skia.cs`) without AppKit. The macOS native shim (`UnoNativeMac`/NSApplication) is
   only needed for real windows.
5. **Dependency-identity packaging (target, not current implementation).** `dotnet exec --runtimeconfig <project>.runtimeconfig.json
   --depsfile <project>.deps.json <host.dll>` makes the child's dependency graph *be* the
   project's dependency graph. The child is a thin shim; all Uno assemblies resolve from the
   project's bin, so the parent "OpenDevelop-side version" problem disappears - the child always
   runs the exact Uno the project references.

**Architecture.** Child process `UnoDesignHost` (Uno.Sdk `net10.0-desktop` console shim, no app
template): boots headless Uno (fact 4),
materializes the design surface's current XAML text via `XamlReader.Load` (fact 2), renders
offscreen via `RenderTargetBitmap` (fact 3), and answers StreamJsonRpc calls over loopback TCP.
OpenDevelop's shell
(`WinUIXamlDesignerViewContent`, toolbox, property pad, outline, DevFlow actions) keeps its
contracts; only `WinUIXamlHost`'s backing changes from in-process ProGPU to child-process RPC. The
fixture (`UnoXamlSample`) is handled by that child using the controls/resources available in the
child's bundled runtime.
The checked-in child currently runs its own Uno 6.5.31 `.runtimeconfig.json`/`.deps.json`; it does
not yet accept a project assembly or run under the opened project's dependency context described
in fact 5. That is the main remaining boundary between fixture support and version-correct real
Uno project support.

**Runtime adapters.** One shell contract; per-runtime bootloaders. Uno headless Skia is the first
out-of-process adapter and is reachable on macOS. A Windows App SDK adapter is Windows-only and
remains future work. Contrary to the earlier version of this technote, current Windows App SDK
WinUI does expose [`Microsoft.UI.Xaml.Markup.XamlReader.Load`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.markup.xamlreader);
the adapter investigation should begin with that supported runtime parser and determine its
custom-control, compiled resource, dispatcher, and offscreen capture constraints before choosing
Roslyn compilation. ProGPU is the separate in-process adapter for ProGPU.WinUI projects. The
wire/data contracts remain runtime-neutral so another out-of-process adapter is additive.

**Protocol surface** (the implementation uses StreamJsonRpc over a fresh loopback TCP connection;
the child connects back to the parent's listener and logs over redirected stdout/stderr):

- `initialize` -> `capabilities`: runtime name/version and toolbox catalog (categories, default
  XAML template snippets, required namespaces). The catalog is generated in-child by reflecting
  the loaded runtime assemblies and applying a design-time allowlist. Today it therefore matches
  the bundled Uno version; after project-context launch is implemented it must match the project's
  actual Uno version.
- `load` `{xaml, viewportWidth, viewportHeight, dpi}` -> `{elementTree, diagnostics}`: materialize
  via `XamlReader.Load`, measure/arrange at the viewport, return the namescope-backed element tree
  (x:Name, type, bounds via `TransformToVisual`) and parse/layout diagnostics. An implicit render
  follows.
- `render` -> `{bitmap (BGRA8), width, height}`: `RenderTargetBitmap` readback. On-demand only
  (source edit, viewport resize, theme change) - never a frame stream, so base64 in JSON is an
  acceptable first transport; shared-memory transport is a later optimization if latency demands.
- `hit-test` `{x, y}` -> `{chain}`: nearest named elements with bounds, resolved in-child, so
  selection mapping is authoritative (x:Name crosses the boundary, never UI objects - same rule as
  the in-process design's `FindName` rule).
- `shutdown`; `log`/`diagnostics` notifications (async layout/runtime errors outside a request).

**Editing model unchanged:** every operation is a versioned XAML source edit in OpenDevelop
(existing XML document model + `WinUIXamlElementPropertyAdapter`), followed by `load` + re-render.
No child-side mutation protocol in the first milestone.

**Milestones:**

- **M0 - Headless probe: done.** The production child now boots headless Uno, uses
  `XamlReader.Load`, and renders through `RenderTargetBitmap`.
- **M1 - Child host protocol: done.** The implemented surface includes capabilities/toolbox,
  `design/load`, `design/layout`, `app/resources`, hit testing, and shutdown/lifecycle handling.
- **M2 - OpenDevelop wiring: done for the fixture.** The parent spawns the bundled child, presents PNG
  frames, consumes the element tree, and supports selection, editing, viewport, and lifecycle
  operations through the shared shell contracts.
- **Shared host lifecycle: done (2026-08-23).** The production adapter uses the common
  compatibility-keyed pool. Compatible windows share one child process, Uno `Application` and
  headless dispatcher, while every `DocumentId` routes to an independent `DesignHost` with its own
  live tree, XAML, viewport, theme and app resources. `session/close` removes only that document;
  final-lease shutdown uses the common idle grace. `StartAsync` remains the isolated diagnostic
  entry and production uses `AcquireSharedAsync`.
- **M3 - Real-project parity: in progress.** First launch the child under the opened project's
  runtimeconfig/depsfile and pass its project assembly. App.xaml and merged-resource preprocessing
  already exists; then validate DotUninstall-style custom types, converters, compiled resources, and code-behind, and
  finish line-addressable diagnostics plus a design-time unsupported-construct policy.

Shared Uno hosts use the common DDP recovery coordinator. After a child exit, the runtime host
rebinds each compatible document, reapplies App.xaml resources on its dispatcher, and rerenders
the latest source using its current design viewport and a fresh render revision; a reconnect alone
is not considered a restored preview.
- **M4 - ProGPU routing.** Detect ProGPU.WinUI explicitly, route only that profile to
  `ProGpuRuntimeHost`, and add a ProGPU-targeted fixture so the Uno child cannot mask regressions.
- **M5 - Windows-only native WinUI adapter.** Prototype `XamlReader.Load` in a child running the
  project's Windows App SDK dependency context, then select the capture/materialization strategy
  from measured limitations rather than assuming Roslyn is mandatory.

### Designer Chrome Decision

`ProGPU.WinUI.Designer` ships a complete in-surface designer: `DesignerHost`, `DesignerCanvas`,
`SelectionAdorner`, `PanelDragEditor`, `PropertyGrid`, `Toolbox`, `VisualTreeOutline`,
`DesignerSerializer`, and `VirtualizedCodeEditor`. Those chrome widgets are `Microsoft.UI.Xaml`
controls, so adopting them wholesale would render the Toolbox and Properties **inside** the
ProGPU surface and leave OpenDevelop's own pads empty — inconsistent with the WinForms and WPF
designers and contrary to the shared IDE-experience goal.

The decision is therefore **split chrome**: consume `WinUiXamlLivePreviewSession` for
materialization and (later) `DesignerCanvas`/`SelectionAdorner`/`PanelDragEditor` for the design
surface, but **not** ProGPU's `PropertyGrid`, `Toolbox`, or `VisualTreeOutline`. Toolbox,
Properties, and Outline are served through OpenDevelop's existing shell contracts —
`IToolsHost.ToolsContent`, `IHasPropertyContainer.PropertyContainer`, and
`IOutlineContentHost.OutlineContent` — exactly as `WpfViewContent` does.

Design-surface picking uses ProGPU's public `InputSystem.HitTest`, and maps the hit visual back
to the document through the WinUI namescope: the emitter never assigns `FrameworkElement.Name`, it
publishes names via `XamlTemplateFactory.RegisterName`, so `root.FindName(x)` is the supported way
back. Only the x:Name **string** crosses the host boundary - never a `Microsoft.UI.Xaml` object -
and a pick walks up to the nearest ancestor that exists in the source, because a hit normally lands
on a control-template part with no counterpart in the document. A surface pick and an Outline pick
call the same `SelectElement(name)`, so there is one selection concept rather than two.

Because that name map holds strong references to preview elements, it must be cleared before the
collectible preview assembly is unloaded; `WinUiXamlLivePreviewSession.Reset` requires the caller
to have detached the root first, and otherwise the whole ALC stays pinned. `LiveHostCount` and a
weak reference to the last preview root are exposed as lifecycle probes so
`WinUIDesigner_ClosingDocument_ReleasesRuntimeHostAndPreviewAssembly` can assert this for real
rather than merely observing that nothing crashed.

`WinUIXamlElementPropertyAdapter` backs the Properties pad with the XAML **source** element
(`System.Xml.Linq` only) rather than the live ProGPU visual. This keeps `Microsoft.UI.Xaml` out of
the shell and makes every property change a source mutation that re-parses, re-renders, and can
be undone.

### Backend Layering

| Layer | Responsibility | Reusable Source |
|---|---|---|
| Document model | XML/XAML nodes, stable IDs, source spans, diagnostics, text edits | AXSG/wxsg and UnoDevelop's UI-free logic |
| Render protocol | Load/Update, viewport, theme, diagnostics, visual-tree snapshot, selection | New; compatible with both in-process and out-of-process implementations |
| Renderer | Runtime-specific materialization: ProGPU compiler pipeline, Uno `XamlReader` + `RenderTargetBitmap`, or the future native WinUI adapter | XAML Studio + ProGPU packages for ProGPU; project runtime for OOP adapters |
| Runtime host | Explicitly selected ProGPU in-process surface, Uno child, or future Windows App SDK child | `IWinUIXamlRuntimeHost` and optional capability interfaces |
| OpenDevelop adapter | WPF secondary view, host lifecycle, Toolbox/Outline/Properties wiring | OpenDevelop shell + provider contracts |
| Editing operations | Insert, delete, move, resize, set property → versioned text edits | Three backends share command semantics; each generates its own edits |

Do not treat the runtime visual tree as the only document model. Every operation must ultimately produce an undoable source edit; re-parse and refresh the preview afterwards. This supports invalid intermediate text, Undo/Redo, formatting preservation, and out-of-process renderers.

Standard controls remain the minimum acceptance fixture. The implemented Uno path also imports
App.xaml and local merged dictionaries; custom controls, compiled resources, `x:Bind`, and
code-behind require real-project validation and clear diagnostics where runtime loading cannot
reproduce application startup.

## Toolbox-to-design-surface drop was silently landing at the document root (fixed 2026-08-14)

A real synthetic-mouse-drag test enrichment (bringing the WinUI designer's drag-drop test coverage
up to parity with the WPF designer's) surfaced two independent, real bugs that a weaker
substring-based test assertion had been masking - every toolbox-to-canvas drop had been silently
falling back to inserting at the **document root** instead of the container the user visibly
dropped onto, and the original test's `Assert.Contains("<TextBlock", onDisk)` couldn't tell the
difference between that and success.

1. **`InputSystem.HitTest` was hit-testing against a stale/absent root.**
   `InputSystem.HitTest` (in `ProGPU.WinUI`) bails out immediately if `InputSystem.Current.Root`
   is null (`if (_root == null) return null;`). That root is only ever set by
   `ProGpuWinUIHostControl.SelectInput`, itself only called from real mouse move/down/up on the
   render surface - never from a WPF `DragEventArgs.Drop`. A real toolbox drag starts on the
   Toolbox pad and never first moves the mouse over the design surface, so `Current.Root` was
   simply never set (or stale from an unrelated host), and every drop's hit test silently
   returned null regardless of where the pointer actually was - confirmed live via
   `LastPickDiagnostic`: a drop dead-center on a button's own on-screen bounds reported a
   hit-test point that was numerically correct against that button's local bounds, yet resolved
   to nothing. Fixed in `ProGpuRuntimeHost.ResolveNameAt`
   (`WinUIXamlDesigner.ProGPUHost/ProGpuRuntimeHost.cs`) by explicitly setting
   `InputSystem.Current.Root = control.WinUIRoot` before hit-testing, rather than depending on
   incidental prior mouse traffic having set it.
2. **A resolved leaf control was used directly as the insertion container.** Once (1) was fixed,
   a drop onto `PrimaryButton` resolved the name correctly (matching click-to-select's own
   resolution), but `InsertFromToolbox` inserted the new element as `PrimaryButton`'s own child -
   which the real WinUI compiler correctly rejects for anything with a single-value content
   property ("Member '\$content' cannot contain multiple values"). `ResolveNameAt`'s "nearest
   named ancestor" is the right answer for click-to-select, but not for drop-target resolution: a
   drop onto an existing leaf control is aiming at its *container*, not asking to become that
   leaf's own content. Fixed in `WinUIXamlDesignerViewContent.InsertFromToolbox` by walking up
   from the resolved element to the nearest ancestor whose tag is one of the toolbox's two actual
   multi-child panel types (`Grid`, `StackPanel`), matching what a real design surface does.

A third, separate finding did **not** get a product fix (out of scope, upstream ProGPU.WinUI):
`ProGPU.WinUI`'s hit-test `HasBackground` check (`InputSystem.HitTestInternal`) only recognizes
`Control`/`Border`/`ContentPresenter`, not `Panel`/`StackPanel` - so setting `Background` on a
`StackPanel` has no effect on whether its own empty area is hit-testable, unlike real WinUI/UWP.
The `src/Samples/UnoXamlSample/MainPage.xaml` fixture's `StackPanel` was named `RootStack` for the
new test's parent-comparison assertion, but does **not** carry a `Background` (it would have no
effect and could mislead a future reader into thinking it does something).

See `WinUIDesigner_DragToolboxItemOntoDesignSurface_InsertsIntoDroppedContainer`'s test body and
comments for the full before/after repro detail.

## Local Feed and Packaging Workflow

`ProGPU.*` (including the never-published `ProGPU.Xaml.Roslyn`, `ProGPU.Xaml.Workspaces`,
`ProGPU.WinUI.Designer`) and `LibreWPF.*` are consumed from the local feed
`/Users/lextm/wpf-tools/librewpf/artifacts/local-feed` (see `NuGet.config`'s
`packageSourceMapping`). Every ProGPU.WinUI/designer change therefore requires repacking into
that feed, then clearing the NuGet global cache, then restoring OpenDevelop — same traps as the
LibreWPF workflow documented in [`librewpf.md`](librewpf.md):

1. Build **and pack** with the same configuration (`dotnet pack -c Release` — packing a Debug
   build or packing after a bare `dotnet build` ships stale bits).
2. Delete the old `.nupkg` from the feed before packing (a partial/failed pack must not leave a
   stale copy behind).
3. Delete the matching `~/.nuget/packages/<id>` folder(s) — NuGet serves the first restored copy
   forever for an unchanged version string.
4. `dotnet restore --force --no-cache`, then relaunch. Stale `obj`/`bin` in OpenDevelop's own
   projects can also hide changes (see `librewpf.md`'s second trap).

The four `ProGPU.*` designer packages are packed from the `progpu-p47` worktree
(`/Users/lextm/wpf-tools/progpu-p47`, wieslawsoltes/ProGPU at `bab4dbef`). Future ProGPU work
beyond preview.47 (e.g. the control/API additions in the problem catalog below) should be carried
on the lextudio fork (which is what `progpu`/`progpu-p47` track) and packed at a new version line.

## Real-World Project Preview Problem (2026-08-14)

### Symptom

Opening a real Uno Platform project's `MainPage.xaml` in the Design tab produces a **wall of
diagnostics** (169 lines for DotUninstall's `Presentation/MainPage.xaml`) and the preview does
not materialize. The fixture sample (`src/Samples/UnoXamlSample/MainPage.xaml`) is fine because
it only uses `Grid`/`StackPanel`/`TextBlock`/`Button`.

The full diagnostics are returned by the `od.winui-designer.status` action (`status` field) and
shown in the status line under the design surface. Because they live in that TextBlock, they
cannot be selected/copied in the UI — a product gap (see roadmap below).

### Root causes already found and fixed (2026-08-14)

Two independent bugs made the design surface render an empty frame even for the fixture sample;
both were diagnosed live via DevFlow and are fixed in `WinUIXamlDesigner.ProGPUHost`:

1. **Text never rendered — `PopupService.DefaultFont` was never initialized.**
   ProGPU's `Window` constructor is the only place that sets the process-wide
   `PopupService.DefaultFont` (on macOS: `/System/Library/Fonts/Supplemental/Arial.ttf`).
   The offscreen host creates no `Window`, so `DefaultFont` stayed `null`, and
   `RichTextBlock.GetOrUpdateRenderCommandCache` returned an empty command cache for every
   `TextBlock`/button label — the compositor compiled zero glyphs (`glyphs=0, glyphBatches=0`
   in the compositor metrics). Fixed in `ProGpuRuntimeHost` with `EnsureDefaultFont()`,
   mirroring the `Window` constructor. Verified: the WinUI command probe went from
   `commands=3 [DrawRoundedRect=3]` to `commands=11 [DrawText=8, DrawRoundedRect=3]`, and the
   frame readback shows the text rows.

2. **Presented frame invisible on screen — `WgpuContext.Current` was clobbered.**
   `WgpuContext.Initialize` sets the thread-static `WgpuContext.Current` to *this* context
   (`Current = this` in `ProGPU.Backend/WgpuContext.cs`). Creating the host's own offscreen
   context therefore stole `Current` from LibreWPF on the UI thread. LibreWPF's
   `WpfBitmapSourceImageAdapter` prefers `Current` when creating the GPU texture for a
   `DrawImage`/`DrawTexture` command, so the frame bitmap was uploaded onto the designer's
   context while the WPF window is composited on LibreWPF's own context — a cross-device
   texture that silently renders nothing (the red diagnostic border and text, being vector
   primitives, still showed, which is what made this diagnosable). Fixed in
   `ProGpuWinUIHostControl.Start()` by saving and restoring `WgpuContext.Current` around
   `context.Initialize(null)`. Verified: `capturedAtOnRender` now reports LibreWPF's context.

Note the readback path itself was always healthy — the frame content was provably in the
`WriteableBitmap` buffer; only the on-screen presentation was lost to the cross-context texture.

### Diagnostic tooling added (DevFlow actions, temporary)

Kept for now to support further investigation; all under `od.winui-designer.*`:

- `frame-profile` — row-by-row non-white pixel profile + per-pixel samples of the presented
  frame (reads the `WriteableBitmap` buffer; not an OS screenshot).
- `compositor-metrics` — the `Compositor.Metrics` snapshot (draw calls, vector/text vertices,
  glyph/path-atlas counts, pipeline counts, retained-composition state, timings).
- `draw-calls` — reflection dump of the compositor's `_drawCalls` (read between frames, so the
  list is restored to empty; the metrics are the reliable source).
- `winui-commands` — walks the WinUI visual tree, calls `OnRender` on every node, and reports
  the emitted command counts per type.
- `image-path` — replays LibreWPF's `WpfBitmapSourceImageAdapter` path step by step
  (`TryGetGpuTexture`, portable pixels, context identity at OnRender).
- `overlay` — red border + status text + two 64×64 test images (WriteableBitmap vs
  `BitmapSource.Create`) drawn in `OnRender`, to isolate vector vs image rendering.
- `recreate-bitmap` / `background-brush` — alternate presentation experiments (in-place
  `WriteableBitmap` updates vs per-frame recreation vs `Background = ImageBrush`).

### Real-project diagnostics catalog (DotUninstall, 169 lines)

Categories, in order of frequency:

1. **Project's own types unresolved (~140 lines) — FIXED 2026-08-14, at the type-resolution
   layer only.** `conv:NullToVisibilityConverter`, `conv:BoolToVisibilityConverter`, ...
   (`using:DotNetUninstall.Presentation.Converters`) and `controls:TwoPartBadge`/`controls:SingleBadge`
   (`using:DotNetUninstall.Presentation.Controls`) failed to resolve, and every member of an
   unresolved owner followed ("Member 'Label' cannot be resolved because its owner type is
   unresolved", ~8 member errors per usage). Root cause: the preview compilation
   (`ProGpuXamlExecutor.EnsureProject`) built an `AdhocWorkspace` from the framework metadata
   references + the ProGPU runtime directory only — the project's own source files (converters,
   custom controls, code-behind) and its output assembly were never included. Fixed in
   `ProGpuXamlExecutor.CollectMetadataReferences` by resolving the opened project via
   `SD.ProjectService.FindProjectContainingFile` and adding its `OutputAssemblyFullPath` as a
   `MetadataReference` (deliberately NOT the project's own Uno.WinUI references - see
   "Out-of-process host decision" above for why that would create ambiguous-type errors instead).
   Verified live on a synthetic reproduction (a dependency-free marker class referenced from XAML
   as a resource): the diagnostic disappears entirely. **This closes the compile-time
   type-resolution half of the problem, not materialization** - see the out-of-process host
   decision above for the runtime-load half this surfaced (`PreviewAssemblyLoadContext` has no
   load path to the project's own build output).
2. **Code-behind event handlers unresolved.** `Code-behind event handler
   'OnMessageCenterFlyoutOpening' was not found or does not match the event delegate` and
   `'OnOpenReleasePage' ... 'Microsoft.UI.Xaml.RoutedEventHandler?'` — same root cause
   (code-behind is not compiled).
3. **`muxc:` (Microsoft.UI.Xaml.Controls) types unresolved.** `muxc:InfoBar`, `muxc:InfoBadge`,
   and their members. Two layers: (a) the preview references do not include the project's own
   WinUI assemblies (Uno.WinUI), and (b) ProGPU.WinUI does not implement `InfoBar`/`InfoBadge`
   at all, so even with references added they could only resolve if the analysis compilation
   used the project's Uno references (see Fix roadmap).
4. **WinUI baseline APIs missing from ProGPU.WinUI.** `Member 'ColumnSpacing' was not found on
   'Microsoft.UI.Xaml.Controls.Grid'`; `Member 'Loaded' was not found on
   'Microsoft.UI.Xaml.Controls.Button'`; `Type 'Microsoft.UI.Xaml.Controls.Pivot' does not
   declare a content member` (Pivot exists in ProGPU.WinUI but lacks the content-member
   declaration).
5. **GridLength/star sizing conversion.** `Text '*,Auto' cannot be converted to
   'Microsoft.UI.Xaml.Controls.ColumnDefinition' by profile 'WinUI'` (×2, plus
   `'Auto,*,Auto' ... RowDefinition`). `WinUiXamlProfile.TryCreateGridLength` handles
   `Auto`/`*`/absolute for `GridLength`, but `TryCreateLiteralExpression` has no
   `ColumnDefinition`/`RowDefinition` case, so whole-string grid shorthand fails.
6. **StaticResource forward reference.** `StaticResource 'InstallEntryTemplate' is declared
   later in the same lexical resource chain` — the checker rejects page-level forward
   references that real WinUI/Uno accepts.

### Fix roadmap (updated 2026-08-14)

Three workstreams; A is partly done, B and D are independent of each other, C is a quick, cheap
win done alongside A.

**A. OpenDevelop side — give the preview compilation project context.**
**Done (compile-time half):** the opened project's own output assembly is now a
`MetadataReference` in `ProGpuXamlExecutor.EnsureProject` (see the catalog entry above) — this
eliminates category 1 and 2 (~140 lines). **Not done (runtime half):** materialization still
fails to *load* that assembly (see "Out-of-process host decision" above) — teaching
`PreviewAssemblyLoadContext` to probe the project's output directory would close this for the
project's own assembly specifically, but does not help category 3a (`muxc:` types), which needs
the project's real Uno.WinUI references - blocked by the type-identity conflict, i.e. blocked on
workstream D.

**B. ProGPU side — extend `ProGPU.WinUI`/`ProGPU.Xaml.Roslyn` toward WinUI baseline.** Still
fully applicable regardless of the out-of-process decision: it's what makes the in-process host
(the fixture-sample renderer, and any real project restricted to the ProGPU.WinUI-covered
subset) more capable. Each item has a known landing site:

| Missing piece | Landing site | Rough size |
|---|---|---|
| `Grid.ColumnSpacing`/`RowSpacing` | `src/ProGPU.WinUI/Controls/Grid.cs` | ~50 lines |
| `FrameworkElement.Loaded` event | `src/ProGPU.WinUI/Core/FrameworkElement*.cs` | ~30 lines |
| `Pivot` content member | `src/ProGPU.WinUI/Controls/Pivot.cs` | ~10 lines |
| `ColumnDefinition`/`RowDefinition` text conversion | `src/ProGPU.Xaml.Roslyn/WinUiXamlProfile.cs` | ~50 lines |
| `InfoBadge` control | new, `src/ProGPU.WinUI/Controls/` | ~100-200 lines |
| `InfoBar` control | new, `src/ProGPU.WinUI/Controls/` | ~300-400 lines |
| StaticResource forward reference | `ProGPU.Xaml` checker | ~30-50 lines |

**C. Product improvement — diagnostics surfacing. Done 2026-08-14.** The design-surface status
control (`WinUIXamlDesignerViewContent`'s `status` field) is now a read-only, scrollable
`TextBox` instead of a plain `TextBlock` - diagnostics can be selected and copied like any other
text, without needing the full Error List integration this item originally proposed. Routing into
the shared Error List / Message View (line-navigable, filterable alongside build errors) remains
a further improvement, not yet done; `od.winui-designer.status` stays the DevFlow surface either
way.

**D. Out-of-process hosts for project-native runtimes.** See "Out-of-process host decision for
Uno and native WinUI" and "Out-of-process host scoping" above. The Uno M0-M2 implementation is
present; M3 real-project validation remains. The Windows App SDK adapter is separate Windows-only
work and should start by probing its real `XamlReader`, dispatcher, resource, and capture behavior.

**E. Explicit ProGPU WinUI profile.** Add detector evidence and runtime discrimination, make the
ProGPU and Uno factories decline foreign profiles, and add a ProGPU-targeted integration fixture.
This turns the existing ProGPU implementation into intentional WinUI-on-ProGPU support instead of
an availability-based fallback.

**Suggested order (updated 2026-08-15):** fix routing (E) first so tests exercise the intended
runtime; finish Uno real-project parity (D/M3); extend ProGPU coverage according to B; then build
the Windows-only native WinUI adapter (D/M5). A remains useful only as ProGPU compilation
introspection, and C is done.

## Design-surface improvements (2026-08-15)

Follow-on work on the out-of-process host's OpenDevelop shell, all verified live via DevFlow
and by the `WinUIDesigner_*` integration tests:

### Toolbox is populated from the runtime catalog (18 -> the loaded runtime's controls)

`WinUIXamlToolbox` was a hardcoded 18-item whitelist. The child's `initialize` catalog (built
by reflecting the loaded `Microsoft.UI.Xaml.Controls` assembly: `FrameworkElement` subclasses
with a parameterless ctor, denylisted for shell/template parts and navigation hosts) is now
wired through `IWinUIXamlToolboxCatalog` (`GetToolboxCatalog`) into the shared Toolbox pad
when the design host reports ready. The fixture now lists 140 controls. The catalog filter was
widened from `Control`/`ContentControl` to `FrameworkElement` so panels, `TextBlock`, `Border`
and `Image` are included.

### Unnamed elements are auto-named on pick

Clicking a control without an `x:Name` previously resolved to nothing (the Properties pad
stayed empty - every pick walked up to a *named* ancestor). Now:

- the child reports the innermost hit's **tree path** (`ElementNode.Path`, `HitTestResult.PickPath`)
  alongside the name chain (template parts leak names like a ScrollViewer's internal `Root`,
  so the chain alone cannot tell "unnamed" from "template name");
- the shell (`IWinUIXamlPathPick.GetPickChain`) maps the path back to the source document
  (walking up to the first element type the source actually contains), auto-assigns a unique
  `x:Name` through the editor (undoable, dirtied), and selects it - VS-style.

The pick path mapping is index-based among same-type elements in tree order; template parts of
the same type as the picked control are a known divergence risk, acceptable for now.

### Toolbox drag keeps the dragged tool selected

`WinUIXamlToolbox` reasserts the dragged item against the ListBox's internal Selector, which
keeps moving `SelectedItem` to whichever row is under the cursor while the button is held
during a drag (the hazard `WpfToolbox` documents); the tool stays selected until the drop on
the design surface completes.

### Scrollbars actually scroll the canvas

The design rect was positioned at `origin + pan + scrollOffset` inside the scroll content, so
the offset cancelled on screen - dragging the scrollbar thumb did nothing. The rect is now
anchored at a fixed content position (top-left when zoomed in, centered at fit), the
ScrollViewer moves it natively, and the scroll range covers the whole design. `ToDesignPoint`,
`DesignToSurfacePoint` and zoom-at-cursor are scroll-aware; `FitView` resets the scroll.

### Test coverage

- `WinUIDesigner_PropertiesPadEdit_UpdatesSourceAndRender` (new): a property edited through
  the shared Properties pad lands as a source edit and the re-rendered surface reflects it
  (the button widens); polls the measured bounds because `rendered` stays true across re-renders.
- `WinUIDesigner_DragToolboxItemOntoDesignSurface_InsertsIntoDroppedContainer` (existing):
  real synthetic pointer drag from the Toolbox onto the surface, verifying the drop resolves
  into the dropped container and lands as a source edit.
- The retired-ProGPU assertions were updated to the Uno host: `runtime-stats` now reports the
  child-process lifecycle (`IWinUIXamlLifecycleProbe`), `ClosingDocument` asserts the child
  dies on close, and `RendersButton` asserts a non-zero rendered button with no diagnostics.

### Project dependency context (A1, 2026-08-15)

The child now runs inside the designed project's dependency graph - the architecture's fact 5
landed. When the owning project has build output, `UnoDesignClient` spawns the child with

```
dotnet exec --runtimeconfig <project>.runtimeconfig.json --depsfile <project>.deps.json <host.dll> --port N --appbin <project-bin>
```

so Uno and every project assembly resolve from the project's bin (the project's real Uno
version, custom controls, converters, muxc types). Two child-side pieces make this work:

- **Own-dependency resolver**: with the project's deps, `AppContext.BaseDirectory` points at
  the project bin, not the child's deployment - so the resolver hook loads the child's own
  non-project dependencies (StreamJsonRpc etc.) from `typeof(Program).Assembly.Location`'s
  directory. Registered from a helper method because `Main`'s own JIT resolves StreamJsonRpc
  before the first line runs.
- **Project-assembly preload**: XamlReader's type resolution scans the *loaded* assemblies
  (`AppDomain.GetAssemblies()`), so the child preloads the project bin's dlls
  (`--appbin`); without this, `{using:UnoPropertyGrid}PropertyGridControl` reported
  "Unable to find type". Verified: `CustomControlPage.xaml` (a page referencing the sample's
  own `pg:PropertyGridControl`, no event handlers) renders with zero diagnostics, while the
  unbuilt `UnoXamlSample` fixture falls back to the child's own deployment unchanged.

The compile baseline stays Uno.Sdk 6.5.31 (the API floor); the project's runtime is whatever
the project references (verified against 6.6.42). The two reflection points into Uno internals
(`CoreDispatcher.DispatchOverride`, `RootScale._testOverrideScale`) remain the version-risk
surface; both fail with clear fallbacks.

## ProGPU host interface parity (2026-08-16)

The `IWinUIXamlRuntimeHost` contract grew several members (gridlines, simulated display scale,
render diagnostics, PNG export, pixel sampling, child log) when the out-of-process Uno host
implemented them; the in-process ProGPU host lagged behind and no longer compiled against the
shared interface. Both runtime profiles now implement the full contract, with the ProGPU side
(`ProGpuRuntimeHost`/`ProGpuWinUIHostControl`) mirroring the Uno host's semantics:

- **`RenderSample()`** samples the last frame's BGRA staging bytes at the same fixed points as
  the Uno host (center, top-left, mid-left) and returns `WxH center=#RRGGBB topleft=#RRGGBB
  midleft=#RRGGBB` — so a DevFlow pixel check reads identically for both runtimes.
- **`ExportPng(path)`** encodes the last frame from the staging bytes via
  `PngBitmapEncoder` (`Wrote <path> (WxH)`, or `Nothing to export (no design loaded)` /
  `Export failed: ...`), instead of relying on the child process the Uno host uses.
- **`RenderTiming()`** times the compositor pass with a `Stopwatch` around
  `Compositor.RenderOffscreen` + GPU readback and reports `(RenderMs, Width, Height, Dpi,
  CompressedBytes, RawBytes)`. In-process there is no wire compression, so compressed == raw.
- **`EffectiveDisplayDpi` / `SetSimulatedDpi()`** — the render loop now renders at the
  *effective* scale instead of the raw WPF DPI: the simulated override wins, then the
  `UNO_DESIGN_DPI` environment override (the Uno host's existing test hook, so the two runtimes
  share it), then the real `VisualTreeHelper.GetDpi` reading. The change is observable in
  `compositor-metrics` (`dpi=` field) and re-renders on the next composition tick, exercising
  the same DPI-aware render path a real monitor move would.
- **`Gridlines` / `SetGridlines()`** — a design-space gridlines overlay drawn in the host
  control's `OnRender` (24 px pitch, semi-transparent), matching the Uno surface's overlay.
- **`ChildLog`** returns `"(in-process host)"` since this runtime owns no child process.

The DevFlow actions that consume these members — `od.winui-designer.gridlines`,
`od.winui-designer.debug-dpi`, `od.winui-designer.render-timing`, `od.winui-designer.export-png`,
`od.winui-designer.render-sample`, and `od.winui-designer.child-log` — therefore behave
identically against both runtime profiles.

## Shared-shell alignment (2026-08-19/20)

A batch of changes brought the Uno surface and its toolbar onto the shared shell features added
for all three designers (see designer-common.md "Done (2026-08-20)" for the shared-layer
details; this section records the Uno-side specifics).

- **Selection/guide offset fix (2026-08-19)**: selection adorners, snap-guide lines and grid-guide
  drags had been re-applying `origin + pan` a second time, drifting the overlays off the rendered
  frame once panning or the canvas margin was involved. They now map design→surface through a
  canvas-local viewport (`UnoDesignSurfaceControl.CanvasLocalViewport`, which folds `CanvasMargin`
  into the pan once — `CurrentViewport` still handles frame placement). Guide/box/selection
  drawing changed from `origin + pos*scale + pan` to bare `pos*scale` inside the already-positioned
  `viewportCanvas`. Integration tests for the selection offset were added/updated.
- **Theme combo**: the toolbar's theme combo now lists the app's real
  `ResourceDictionary.ThemeDictionaries` keys (hoisted by `AppResourceBuilder.GetThemeNames` in
  `UnoDesignRuntimeHost.EnsureAppResourcesAsync` → `surface.SetDesignThemes`), and
  `UnoDesignSurfaceControl.SetTheme` re-renders by that name — the old hardcoded Light/Dark
  button is gone.
- **Gridlines via shared `GridlineOverlay`**: the tiled-`DrawingBrush` grid never rendered under
  LibreWPF-on-macOS; the Uno surface now uses the shared line-drawing overlay (see
  `designer-gridlines-bug.md`).
- **Show-names toggle**: `DesignerCanvas.ShowNames` (default on) toggles the control-name label
  above the selection outline, consistent with WPF/WinForms.
- **Shared `SharedToolbox`**: `WinUIXamlToolbox` became a thin facade over the shared toolbox
  pad engine (`Base/Project/Src/Gui/Pads/SharedToolbox.cs`), preserving its `ToolboxControl`/
  `DragDataFormat`/`FindItem` surface; a WinUI document's Tools pad shows only WinUI categories
  via the shared ListBox's per-scope filter.

## Real WinUI-Gallery preview: seven stacked causes (2026-09-11)

Opening WinUI-Gallery under `OD_WINUI_RUNTIME=microsoft` failed on every page. It turned out to be
seven independent problems in a row, each hidden behind the previous one. The order below is the
order they had to be fixed in; none of them could be seen before the one above it was gone.

### Where this landed (2026-09-11)

12 of 13 sampled WinUI-Gallery pages render with real content, measured through
`od.winui-designer.status` (`rendered: true` plus a non-zero size): Pivot, ComboBox, Slider, Button,
CheckBox, TextBox, ToggleSwitch, ProgressBar, AppBarButton, RadioButton, Expander, TreeView. For
comparison, the note above this one records a corpus run that rendered 9 of 187. The one remaining
failure, NavigationView, fails gracefully with the child alive (section 7).

Of the failures seen at that point, ListView and GridView are the KNOWN abstract-root limitation
(they derive from `ItemsPageBase`; see "An abstract root element cannot be previewed" below) and now
say so explicitly. Pivot and NavigationView were something else entirely - they were CRASHING the
child, which section 7 covers.

Two items are deliberately left open:

- **`CompiledXamlMirror`'s location is measured, not derived.** The `.xbf` files have to sit next to
  the host; the WinUI sources point at a different base. Written up in 5.
- **Substitution is still the fallback path.** With the app's resources served, the app's own
  controls construct for real, so the substituter should fire less and less. It has not been
  re-measured with substitution disabled across a wide corpus - `OD_DESIGNHOST_NO_SUBSTITUTE=1` is
  there for exactly that comparison.

### Why this took so long: the error messages point at the wrong things

Recorded first because it cost the most time. In this stack a diagnostic was misleading more often
than not:

| Reported | Actually |
|---|---|
| `The type 'AnimatedIcon' was not found` | The name SEARCHED for was `WinUIGallery.Controls.AnimatedIcon`; the message prints the name as WRITTEN in markup. Those differ exactly when the failure is interesting. |
| `The attachable property 'FallbackIconSource' was not found in type 'AnimatedIcon'` | It is a real, ordinary instance property of a real type. The owner's namespace had been resolved through the wrong prefix. |
| `Cannot create instance of type 'ControlExample'` | The real cause (a generic type failing to resolve) appears NOWHERE in the message. |
| `[Line: 49 Position: 114807]` | Sometimes exact (the AnimatedIcon case), sometimes meaningless: when the exception comes from a CONSTRUCTOR rather than from parsing, the position is wherever the parser happened to be. It pointed at unrelated theme markup twice. |
| `rendered: true` | Not proof that anything was drawn - a page can report success at `0x0`. Always check the reported size. |

Two switches are what actually settled it, and both are worth reaching for early:

- `OD_XAMLMETA_TRACE=<type name>` - logs every type/member the parser asks the metadata provider
  for, which `IXamlType` instance it got back, and each `GetMember` result. This is what proved the
  parser asks for qualified names (never bare ones) and that member lookup was succeeding.
- `OD_DESIGNHOST_XAML_DUMP=<dir>` - writes the exact text handed to `XamlReader`. A reported position
  indexes into THAT text and into no file on disk (page + ~3MB of injected theme resources, then
  rewritten by the repairs below), so reading a position without it is guesswork. It runs last in
  `XamlDocumentRepair`, so the dump is byte-for-byte what the parser sees.

### 1. Architecture mismatch (build, not code)

`WinUIXamlDesigner.MicrosoftHost` built for the wrong RID dies with
`FileLoadException: The assembly architecture is not compatible with the current process architecture`,
which surfaces in the IDE as `Microsoft WinUI design host failed to start: A task was canceled`.

Do NOT infer the RID from `uname -m` or `PROCESSOR_ARCHITECTURE` - both report x64 under emulation on
an arm64 machine. The RID from `dotnet --info` is authoritative.

### 2. Launch arguments: the app's runtime graph is often unusable

`UnoDesignRuntimeHost.ProjectDependencyContext` decides what the child is launched with, and two
cases must NOT adopt the app's runtimeconfig. `dotnet exec --runtimeconfig` pins the child to the
framework that file names, and a net10.0 host then cannot load at all
(`Could not load file or assembly 'System.Runtime, Version=10.0.0.0'`):

- **Self-contained apps** declare `includedFrameworks` rather than `framework`/`frameworks`. A check
  that only looks at the latter two finds no version, assumes compatible, and kills the child.
- **Older-major apps** (a net9.0 app against a net10.0 host).

Both still get `--appbin` on its own, which is what `HostBootstrap.PreloadProjectAssemblies` needs.
`AcquireSharedAsync` therefore takes `appBinPath` and folds it into `CompatibilityKey`, so documents
from different projects never share a child that preloaded the wrong app.

Also: the shared-pool path silently returned `(null, null)` in every one of these cases, and the
child's preload returned silently too, so "none of the app's types resolve" looked like a XAML
authoring error. Both paths now log.

### 3. Three repairs that only apply to the COMBINED document

`XamlDocumentRepair` (shared by both children, called from `DesignHost.LoadDesignAsync` AFTER the
host-specific transform) fixes problems that exist in no single input file - which is why verifying
`AppResourceBuilder`'s output, the theme dictionary and the metadata provider each "checked out
fine" while the render still failed:

- **Ambiguous xmlns prefixes** (`XamlPrefixNormalizer`). The theme markup re-declares
  `xmlns:local="using:Microsoft.UI.Xaml.Controls"` on its elements; a page declares
  `xmlns:local="using:TheApp.Controls"`. One prefix, two namespaces, one document. XML says the
  inner declaration wins and XLinq serializes exactly that, but WinUI's parser resolved a property
  element's owner through the OUTER binding. Renaming the re-declarations removes the ambiguity.
  Attribute VALUES have to be rewritten too (`TargetType="local:Foo"`) - XLinq only tracks names.
- **Unprefixed attached properties** (`AttachedPropertyQualifier`). `AnimatedIcon.State="Normal"`
  names no namespace anywhere, so by the XAML rules it belongs to the default xmlns. The parser
  instead qualifies it with whatever `using:` prefix happens to be in scope. Standing alone the
  theme resolves these correctly (it binds no CLR prefix); injected under a page that binds one,
  they start resolving against the app's namespace. Minimal repro:
  `src/Samples/MicrosoftWinUISample/AttachedPropertyPage.xaml` - deleting its one `xmlns:local`
  line makes the same page render.
- **Uninstantiable app controls** (`CompiledXamlControlSubstituter`) - see 5.

Correcting the second one inside the metadata provider does NOT work: answering the misqualified
name with the framework type requires reporting a `FullName` that does not match the request, and
that aliased entry then stands in for the type itself and breaks later correctly-qualified lookups
on it. Rewriting the markup keeps the provider honest.

### 4. Metadata-provider gaps a real corpus hits immediately

All in `ReflectionXamlMetadata.cs`:

- `IReference<T>` projects to `Nullable<T>` - neither an enum nor convertible. Unwrap it first or
  both `EasingMode="EaseOut"` and `Duration="0:0:0.4"` fail. Several types XAML routinely writes as
  text (`TimeSpan` above all, in every animation) implement no `IConvertible` and need explicit
  parsing.
- XAML spells a closed generic `Ns.Type`2<A, B>` (what `x:TypeArguments` produces) while
  reflection wants `Ns.Type`2[[A],[B]]`. Without translating, CommunityToolkit's
  `Animation`2<String, Vector3>` is unresolvable, which is what actually made
  `ControlExample` uninstantiable.
- `IsCollection` accepts `ICollection<T>`, but `AddToVector` cast to the non-generic `IList` -
  reported as `Cannot add instance of 'OffsetAnimation' to a collection of 'ImplicitAnimationSet'`,
  which reads like a content-model rejection.

### 5. ms-appx: serving the app's own resources (the real fix, not a workaround)

A control declared in XAML gets a generated `InitializeComponent` calling
`LoadComponent(ms-appx:///Controls/Example.xaml)`. That resolves through MRT against the RUNNING
process's app resources, so it threw `Cannot locate resource from 'ms-appx:///...'` and failed every
Gallery page (every sample is wrapped in `ControlExample`).

**WinUI supports this scenario directly.** `ModernResourceProvider::Create` asks the app for a
replacement resource manager ("Give the app a chance to provide its own ResourceManager to handle
app resources" - microsoft-ui-xaml, `src/dxaml/xcp/components/mrt/ModernResourceProvider.cpp:159`),
surfaced as `Application.ResourceManagerRequested` ->
`ResourceManagerRequestedEventArgs.CustomResourceManager`. Implemented in
`AppResourceManagerProvider`. Three details are load-bearing:

- **Subscribe in the Application CONSTRUCTOR.** The resource manager is created lazily
  (`CCoreServices::GetResourceManager`), but the framework's own initialization touches it first and
  the event fires only on that one creation. Subscribing in `OnLaunched` registers a handler that is
  never called - indistinguishable from the API not working.
- **Construct MRT Core's `ResourceManager` from a FILE PATH.** `Windows.Storage` cannot open these
  files in an unpackaged process: `StorageFile.GetFileFromPathAsync` fails with `0x80070002` for a
  path `File.Exists` confirms. That is what rules out the otherwise obvious
  `ResourceManager.Current.LoadPriFiles`.
- **The framework's own resources keep working** - they are served by a separate framework-package
  resource manager that this does not touch. The host declares no XAML of its own, so handing the
  app's resources over wholesale costs nothing.

**The .pri is only half of it.** `TryLoadXamlResourceHelper` probes for the compiled `.xbf` first
(`XamlNodeStreamCacheManager::GetBinaryResourceForXamlUri` swaps the extension) and only falls back
to `.xaml`. Only the compiled form is usable at design time: it carries `x:Bind` as references into
generated code (which lives in the app assembly the host preloads), whereas the `.xaml` fallback
still contains `{x:Bind}` markup and a runtime parser reports `The type 'Bind' was not found`. An
unpackaged app keeps its `.xbf` files LOOSE in the output directory rather than inside its .pri, so
serving the .pri alone lands on the `.xaml` fallback.

`CompiledXamlMirror` copies them next to the host. **Where they must go was established by
experiment and contradicts the sources**: copied into the host's own directory the app's controls
construct and render, while setting the child's working directory to the app's output changes
nothing. The only base findable in the WinUI sources is `CommonResourceProvider`'s
`GetModuleFileName(NULL)`, which for a `dotnet exec` child is the dotnet host's directory - so some
other path resolves these and the mechanism is NOT fully traced. If this ever needs revisiting,
those two measurements are the ones to repeat. Known limitation: that directory is shared by every
child, so two WinUI projects designed at once overwrite each other's `.xbf`; stale copies are
cleared on each start, which keeps a single project always correct.

`CompiledXamlControlSubstituter` stays as the fallback for when no compiled XAML is available. It
keeps the replaced control's children INCLUDING the children of its property elements - dropping
property elements wholesale is the obvious reading and it silently emptied pages, because
WinUI-Gallery assigns the real content through `<ControlExample.Example>`; the result rendered as
`0x0` while still reporting success.

### 6. x:Bind in the page under design - the single biggest blocker

Found last and affects the most pages: **75 of WinUI-Gallery's 120 sample pages use `x:Bind` in
their own markup** (SliderPage alone has 14). `x:Bind` is a COMPILE-TIME feature - the XAML compiler
turns each expression into generated code in the page's partial class - so a runtime parser reading
the page's SOURCE sees a markup extension named `Bind` and fails the whole page with
`The type 'Bind' was not found`.

Note the asymmetry with 5, which is easy to conflate (and was, for several rounds):

- The app's own **controls** have a compiled form (`.xbf`) that CAN be loaded, so their `x:Bind`
  works as generated code. That is why serving the app's resources is a real fix.
- The **page being designed** has no usable compiled form - the file being edited IS the source, and
  a `.xbf` on disk is from the last build, not from what the user is looking at. There is also
  nothing for a compiled binding to resolve against at design time: no page instance, no
  code-behind state.

`CompileTimeBindingStripper` therefore drops `{x:Bind ...}` attributes before parsing, leaving the
property at its default. This is standard designer behaviour: show the structure, do not evaluate
compiled bindings. Only `{x:Bind}` is touched - `{Binding}` is a runtime expression the parser
handles by itself and degrades to an empty value without failing.

Attributing a `Bind` failure to the app's controls is the trap: with substitution active the log
showed `substituted a panel for ... ControlExample`, which means it was never constructed, so the
`Bind` error could not have come from it. Check whether the PAGE uses `x:Bind` before looking
further.

### 7. Resolving framework template parts CRASHES the host (2026-09-11)

`PivotPage` and `NavigationViewPage` did not fail to parse - they killed the child process. The IDE
only sees `The JSON-RPC connection with the remote party was lost before the request could
complete`, `od.winui-designer.runtime-stats` reports `childAlive: false`, and the child's log ends
mid-render with no managed exception, because the crash is native.

The cause is the provider's own namespace fallback. The parser qualifies default-xmlns names with a
candidate list that omits `Controls.Primitives`, so it asks for
`Microsoft.UI.Xaml.Controls.PivotPanel` when the type is
`Microsoft.UI.Xaml.Controls.Primitives.PivotPanel`. Answering that retry with the relocated type
lets the control's default template be built against this reflection metadata - and constructing a
framework-internal template part that way takes the process down.

So the fallback is kept, but **never for a `UIElement`**. Left unresolved, the theme builder drops
that single default Style (the existing "dropping default Style for unresolvable TargetType" path)
and the control renders untemplated: visibly plain, but alive. Helpers, converters and brushes found
the same way (`ComboBoxHelper`, `CornerRadiusFilterConverter`, `AcrylicBrush`) are never instantiated
as visuals and DO need resolving.

Measured over the same 13 Gallery pages, which is the only reason the rule is where it is:

| fallback | rendered | failed | crashed |
|---|---|---|---|
| all types | 11 | 2 | 2 (Pivot, NavigationView) |
| disabled entirely | 11 | 4 | 0 |
| **excluding UIElement** | **12** | **1** | **0** |

`OD_DESIGNHOST_NO_NS_FALLBACK=1` disables it outright; that switch is what made the three-way
comparison possible and is worth keeping for the next time a control crashes the host.

`src/Samples/MicrosoftWinUISample/CrashProbePage.xaml` is the isolated repro - a bare `<Pivot>` with
no app types, no bindings and no content. It crashed identically to the Gallery page, which is what
separated "this control's default template" from "something in that page".

Two things this corrected, both of which had been recorded as fact:

- **Not every remaining failure was the abstract-root limitation.** Of the four, only ListView and
  GridView derive from `ItemsPageBase`; NavigationView and Pivot do not, and were crashes. The
  earlier claim came from confirming ONE page and generalising.
- **A crash is not a render failure.** They were counted together, which is precisely what let a
  process death sit disguised as a known limitation. Count them separately.

`NavigationViewPage` still fails, now gracefully: `Catastrophic failure` (COMException 0x8000FFFF)
with the child alive. That is the same HRESULT the host's own notes record for WinAppSDK's native
resource paths when unpackaged.

### Substituted content must be filtered to visuals

`CompiledXamlControlSubstituter` keeps the replaced control's children including those of its
property elements (dropping property elements wholesale renders `0x0` - WinUI-Gallery assigns real
content through `<ControlExample.Example>`). But a property element can also hold DATA objects:
`<ControlExample.Substitutions>` carries `ControlExampleSubstitution`, a Key/Value pair. Lifting one
into a panel fails with
`Cannot add instance of type 'ControlExampleSubstitution' to a collection of type 'UIElementCollection'`.
Only elements that resolve to a `UIElement` (or whose type does not resolve as an app type at all -
i.e. framework markup, where the page's real content lives) are lifted.

### Measuring this is its own hazard

Four separate wrong conclusions in this investigation came from the measurement, not the code:

- `rendered: true` says a render completed, NOT that anything is visible. A page can report success
  at `0x0` - which is exactly what a substitution that dropped all content produced. Always assert
  the reported size too, and distinguish "failed" from "rendered empty".
- The size in `od.winui-designer.status` is `W×H` with `×` JSON-escaped, so a naive
  `grep 'host ([0-9]*x[0-9]*)'` silently matches nothing and reports every page as failed. Extract
  the digits with a tolerant separator.
- Shell quoting in the pass condition mis-scored a whole run as 0/6 when it was really 4/6.
- Too short a wait after `activate-design` reports failures for pages that simply had not rendered
  yet; the child needs ~25-30s on first use because the default theme dictionary is ~3MB.

When a batch result looks uniformly bad, re-check one page by hand through
`od.winui-designer.status`/`diagnostics` before believing the batch.

### Two things that must not be reintroduced

- **Do not probe constructibility by constructing.** Replacing the substituter's static check with
  `Activator.CreateInstance` reads better on paper and hung the designer: these constructors run on
  the XAML parse thread and reach for a live dispatcher, so one of them never returned. A wrong
  substitution costs some fidelity; a hang costs the whole session.
- **Do not wait synchronously on the child from the UI thread.** `ResolveNameAtWithPath` runs in the
  pointer-pressed handler, so its `GetAwaiter().GetResult()` froze the entire IDE window - not just
  the design surface - for the transport's full 30s operation timeout whenever the child was
  unresponsive. It now gives up after 2s and treats it as "nothing picked". The repo's
  `GetAwaiter().GetResult()` deadlock warning applies to designer-host calls, not only to DevFlow
  actions.

### Configuration and fixtures

- **The active configuration must match what was actually built.** The designer resolves the app's
  output through `project.OutputAssemblyFullPath`, which follows the ACTIVE configuration.
  WinUI-Gallery had only ever been built as `Debug-Unpackaged/ARM64` while the IDE defaulted to
  `Debug`, so the entire dependency context came back empty. `od.solution.set-configuration` and
  `od.project.dependency-context` were added to drive and inspect this.
- **The dependency context is read ONCE, when the document opens.** Changing the configuration or
  building does not refresh an already-open designer - the document has to be reopened. A document
  restored from the previous session's layout captures the state from before any change, which is
  easy to mistake for the change not working.
- **`src/Samples/MicrosoftWinUISample`** is the Microsoft-backend fixture (the pre-existing
  `ProGpuWinUISample` is Uno, so it exercises a different child). `XamlFrameworkDetector` reads only
  the csproj XML, so `<UseWinUI>true</UseWinUI>` alone routes documents there with no
  PackageReference and no restore - which matters because a designer repro must not depend on the
  real Windows App SDK packages. `MainPage.xaml` renders; `AttachedPropertyPage.xaml` is the
  prefix/attached-property probe.
- **An abstract root element cannot be previewed.** `<local:ItemsPageBase x:Class="...HomePage">` is
  ordinary compiled XAML (the generated `HomePage : ItemsPageBase` is what the app instantiates),
  but `XamlReader` ignores `x:Class` and constructs the ROOT ELEMENT's own type. `DesignHost` now
  says so explicitly instead of surfacing WinRT's bare "No matching constructor found".
- **Naming trap**: the Microsoft backend reuses `UnoDesignRuntimeHost` and `UnoDesignClient` on the
  IDE side (only the child dll and display name differ), so seeing "Uno" in a stack or a file name
  does not mean a document was routed to the Uno child. `od.winui-designer.status`'s `backend` field
  is the reliable answer.

## WinUI-Gallery integration corpus (2026-09-12)

`tests/OpenDevelop.IntegrationTests/WinUIGalleryDesignerTests.cs` turns the manual
investigation above into a standing regression suite. It opens the **real** WinUI-Gallery
checkout (not a vendored copy) with the Microsoft WinUI child and asserts each page's outcome.
Verified 2026-09-12: 17/17 cases pass.

Gating (the suite skips, never silently passes, when its prerequisites are absent):

- `OD_WINUI_RUNTIME=microsoft` is required - the Gallery is a Windows App SDK app and must run on
  the Microsoft child, per the runtime-routing decision above.
- A Gallery checkout must be present: `OD_WINUI_GALLERY_ROOT`, or a sibling `WinUI-Gallery`
  directory at any ancestor of the test binary (`OpenDevelopAppFixture.WinUIGalleryRoot`).
- The Gallery must already be built for the configuration/platform under test. Defaults are
  `Debug-Unpackaged` and (on arm64) `ARM64`; override with `OD_WINUI_GALLERY_CONFIGURATION` /
  `OD_WINUI_GALLERY_PLATFORM`. Set `OD_WINUI_GALLERY_BUILD=1` to build it through
  `od.build-solution` first.

**Each page is opened in a fresh design host.** The out-of-process child does not survive being
handed one Gallery document after another - the second and later opens crash it natively
mid-render - which is exactly why the standalone `GalleryProbe` starts a new client per file. The
suite reproduces that isolation by calling `od.close-all-document-views` and waiting for
`runtime-stats.childAlive == false` before opening the next page, so each page repeats the ~30s
theme build on a clean child instead of crashing the shared one.

Assertion contract, deliberately matching what the investigation learned to distrust:

- Every "renders" case asserts `framework == "WinUI"`, `backend == "WinUI"`, `rendered: true`,
  **and a non-zero rendered size parsed from `od.winui-designer.status`'s `status` text** - because
  `rendered: true` alone can be a 0x0 frame (see "Measuring this is its own hazard"). It then
  checks the document's `elementNames` and, where the page names an inner control, a non-zero
  `od.winui-designer.query-element-screen-bounds` - a live-tree proof, not just a namescope echo.
- Failures do **not** arrive via `documentError` (that field is reserved for source-model errors):
  the host puts the reason in the user-visible `status` text. An abstract-root page shows
  `Cannot preview this page: its root element 'ItemsPageBase' is an abstract class…`.

The corpus covers two verified outcomes:

- **Renders with real content**: Button, CheckBox, ComboBox, Slider, ToggleSwitch, ProgressBar,
  RadioButton, Expander, TreeView, AppBarButton, TextBox, Pivot, AutoSuggestBox, NavigationView.
- **Abstract-root failure** (`ListView`/`GridView`, root `ItemsPageBase`): no render, explicit
  explanation, child stays alive.

AutoSuggestBox and NavigationView used to be a third outcome - a native crash that took the child
down and made the host give up after three restarts. That is now fixed host-side.

### AutoSuggestBox's Popup crashed the Microsoft host (fixed 2026-09-12)

A bare `<AutoSuggestBox/>` - no app types, no bindings - took the child down with a **pure native
fault**: `renderDiagnostics` stayed empty and there was no managed exception, so it could not be
caught (unlike Pivot/NavigationView's COMException path). Bisecting the Gallery's
`NavigationViewPage` isolated it: removing only `NavigationView.AutoSuggestBox` made the whole page
render, and a one-element page proved the control, not the page, was the trigger. The `.NET`
runtime's dump-on-crash did not fire either, confirming the fault never passes through the CLR.

Cause: AutoSuggestBox's framework default template hosts a `Popup`, and creating that popup faults
in this offscreen host. WinUI's own guard for the no-island case
(`AutoSuggestBox::OnPropertyChanged2` for `IsSuggestionListOpen`) does not cover template
construction/load, which is where this dies.

Fix: `FrameworkDefaultResources.ApplyDesignTimeControlTemplates` rewrites every `<AutoSuggestBox>`
in the combined document with an explicit, `Popup`-free design-time `Template` rendering a
`TextBox` bound to `Text`/`PlaceholderText`. It rewrites the element's own `Template` property
rather than adding an implicit `Style` to `Application.Resources` because - measured - an implicit
style merged into `Application.Resources` at startup is *not* applied to these offscreen elements,
while the same implicit style in `Page.Resources` is; the element-local property is the
highest-precedence form and works. The element itself is untouched, so selection, outline and the
Properties pad still target it.

Verified: a bare `AutoSuggestBox` went from a native crash to `rendered: true` (1365x32), and the
Gallery's `AutoSuggestBoxPage` and `NavigationViewPage` now render in the corpus.

The class carries `[Trait("DesignerBackend", "Microsoft")]` so it runs with the existing
`RunMicrosoftDesignerIntegration` target alongside the `WinUIOnly_*` tests.

Prerequisite note: the integration test project deliberately does **not** build
`WinUIXamlDesigner.MicrosoftHost` (it needs VS's MSBuild for the `UseWinUI`/PRI toolchain). Build
and deploy that host first, exactly as for any `DesignerBackend=Microsoft` run; otherwise the
suite's `OD_WINUI_RUNTIME=microsoft` gate is satisfied but the child is not present.

## Full-corpus campaign (2026-09-12): 120-page sweep, fixes, current state

The curated corpus above was extended into a full sweep of every WinUI-Gallery catalog page, and
each failure class it exposed was fixed. This section is the authoritative record of that campaign.

### Windows App SDK upgrade: 1.7 -> 2.4.0

`Directory.Packages.props` moved `Microsoft.WindowsAppSDK` from `1.7.250606001` to **`2.4.0`**
(kept in lockstep for the Microsoft host; the Gallery is on `2.1.3`). The upgrade was required, not
cosmetic:

- Microsoft's lifecycle: 1.7 (released 2025-03-18) is **out of support since 2026-03-18**; 1.8's
  end of servicing was 2026-09-09. The current supported stable line is 2.x (latest patch 2.4.0,
  supported to 2027-04-29). Staying on 1.7 means no security/compat fixes.
- Correctness: `SplitMenuFlyoutItem` and `SystemBackdropElement` do not exist in the 1.7 runtime at
  all, so any page (or theme file) referencing them failed to parse. 2.4.0 resolves them and fixed
  MenuFlyoutPage, XamlStylesPage and SystemBackdropElementPage. The deployed host's
  `Microsoft.WinUI.dll` is now `3.0.0.2608`.

### Sweep methodology

`tests/OpenDevelop.IntegrationTests/WinUIGallerySweep.cs` (gated on `OD_WINUI_GALLERY_SWEEP=1`)
enumerates `WinUIGallery/Samples/**/*Page.xaml`, opens each with a **fresh** design host (the child
does not survive one Gallery document after another), and appends a row to `%TEMP%\od-gallery-sweep.tsv`
classified as `RENDER` / `EMPTY` (rendered but 0x0) / `ABSTRACT` / `CRASH` / `FAIL` / `TIMEOUT` /
`EXCEPTION`. `WinUIGalleryProbe.cs` (gated on `OD_PROBE_PAGE`) is the single-page triage tool; it
dumps status, surface geometry, per-element screen bounds, render diagnostics, icon-bar bookmarks
and the child log for one page.

Results over the 120 catalog pages:

| Sweep | SDK | RENDER | ABSTRACT | FAIL | EMPTY | CRASH |
|---|---|---|---|---|---|---|
| r1 | 1.7 | 106 | 6 | 5 | 2 | 1 |
| r3 | 2.4.0 + all fixes below | 113 | 6 | 0 | 1 | 0 |

After the empty-`Frame` fix (ConnectedAnimation, below) the only non-render pages are the six
abstract-root ones, so a re-sweep is expected to report 114 / 6 / 0 / 0 / 0.

### Fixes by failure class

- **XamlStylesPage ("Failed to assign to property 'AcrylicBrush.TintColor'").** The vendored Fluent
  theme references `SystemAccentColor*` keys that only a real device's UISettings merge supplies.
  `FrameworkDefaultResources.ApplyAccentPalette` now reads the seven colours from
  `Windows.UI.ViewManagement.UISettings` (Accent / AccentLight1-3 / AccentDark1-3) and **bakes every
  `{ThemeResource|StaticResource SystemAccentColor*}` reference into a literal** (86 references),
  then still defines the keys for key-based consumers. The literal rewrite is load-bearing: under
  2.4.0 the per-theme-dictionary key definitions are not honoured when the accent AcrylicBrush is
  constructed, so only the literal works. Falls back to WinUI's default accent if UISettings is
  unavailable. This replaced an earlier hardcoded fallback.
- **SemanticZoomPage ("xClassCanOnlyBeUsedOnLoadComponent", a red herring).** `InjectDesignData` used
  to strip the blend/markup-compatibility namespaces with regexes; on this page it removed `xmlns:d`
  while `d:Source="{Binding ..., Source={d:DesignData ...}}"` survived, so the combined document no
  longer parsed and `Combine` silently returned the page unchanged - which surfaced as the bogus
  `x:Class` error. It now removes design-time markup **by namespace** (every attribute in the blend
  or markup-compatibility namespace, plus their xmlns declarations), which leaves parseable markup.
- **CustomXamlConditionalsPage ("The type 'AnimatedIcon' was not found").** Conditional attributes
  (`newExp:Background` / `legacy:Background`) collapse to the same expanded attribute name after
  the conditional URI is stripped, which is a duplicate-attribute parse failure; `XamlDocumentRepair`
  then bailed and the prefix normalizer never ran, so the injected theme's `controls:AnimatedIcon`
  stayed mis-qualified. `ConditionalXmlnsStripper` now drops conditional-prefixed **attributes**
  (like x:Bind: show the structure, not the compile-time evaluation); conditional **elements** are
  still kept via the namespace rewrite.
- **PullToRefreshPage (0x0).** `RefreshContainer`'s default template presents nothing offscreen.
  `ApplyDesignTimeControlTemplates` gives it `<ContentPresenter Content="{TemplateBinding Content}"/>`;
  the page now renders 200x200.
- **ConnectedAnimationPage (0x0).** Four bare `<Frame>` elements (no `Source`) make
  `RenderTargetBitmap` return 0x0 for the whole surface even though the page's elements lay out
  correctly (measured: `pageRoot` 1365x663, frames up to 750px, no render exception). A design-time
  **template** did not help - the fault is in `Frame`'s own offscreen behaviour - so
  `ApplyDesignTimeControlTemplates` **renames an empty `Frame` to a `Border`** (a `Frame` with a
  `Source` or content is left untouched). The page now renders 1365x663.
- **MapControlPage and AutoSuggestBox (native crashes).** Both crash the child natively; Windows App
  SDK 2.4.0 does **not** fix it (re-verified). `ApplyDesignTimeControlTemplates` handles them without
  touching the element: AutoSuggestBox gets a Popup-free `TextBox` template;
  MapControl gets a neutral placeholder `Border`.
- **0x0 frames were not a render-size problem.** A throwaway-render guard was tried in
  `DesignHost.RenderAsync` while chasing ConnectedAnimation's 0x0, then **reverted**: the real cause
  was the empty `Frame` (above), and the throwaway pass only made the render size jump to the window
  size.

### Design-time document features

- **`d:DesignWidth` / `d:DesignHeight`.** `InjectDesignData` captures them from the document root
  before stripping design-time markup, and `DesignHost` applies them as the root's **explicit**
  `Width`/`Height`. Explicit size is required because the offscreen window owns layout and discards
  the `Measure`/`Arrange` arguments; passing them to layout alone was measured to have no effect
  (the surface stayed at the viewport size). Verified: a page with `d:DesignWidth="800"
  d:DesignHeight="600"` renders 800x600 instead of 1365x663.
- **`x:Class` is stripped from the WHOLE document**, not just the root (a nested directive triggers
  the same `xClassCanOnlyBeUsedOnLoadComponent`). This was a defensive change made while chasing
  SemanticZoom; the real fix there was the design-time-markup removal above.
- **Why an implicit `Style` in `Application.Resources` does not work but in `Page.Resources` does.**
  Measured while fixing the empty-`Frame`/AutoSuggestBox workarounds: a style merged into
  `Application.Resources` at startup is not applied to the offscreen elements, while the same style
  in `Page.Resources`, or a `Template` set directly on the element, works. `ApplyDesignTimeControlTemplates`
  therefore rewrites the element's own `Template` property / element name rather than adding an app-level style.

### Microsoft host content sizing and the ComboBoxPage "jitter" (2026-09-12)

After the Windows App SDK 2.4.0 upgrade the offscreen window's Grid stretched its child (the design
root) to the WINDOW size, so every page reported the window size (1365x663) regardless of content.
Two visible bugs followed: a narrow page (AppBarButtonPage's content is 76px wide) had its content
spread across the whole surface, and the reported size was **nondeterministic between runs** - the
window's layout pass races `DesignHost`'s explicit `Measure`/`Arrange`. On pages like ComboBoxPage
the size also appeared to jitter.

Fixed in `DesignHost.FinishLayoutAsync`: measure the root at the **design width** (so text wraps
there), then pin explicit `Width = design width` and `Height = content height`. Measured:
AppBarButtonPage 68x348, ComboBoxPage 1284x272, CustomXamlConditionalsPage 1280x295,
AppNotificationPage 1284x445 - the same across repeated runs and repeated reads. Note the two wrong
turns worth avoiding: measuring at **infinite width** lets text run thousands of pixels wide
(CustomXamlConditionalsPage became 1970px), and letting the window content-size the root is
nondeterministic. `d:DesignWidth`/`d:DesignHeight` still override both dimensions.

Separately, an **outline rebuild on every render** (added to make the Outline runtime-sourced) was
removed: `outline.SetRoots` re-selects, which re-triggers `ShowSelection` and therefore another
render - a feedback loop that showed up as the page flashing. The Outline is rebuilt on document
load and on source edits as before.

### Document Outline (Design view)

`WinUIXamlDesignerViewContent.RebuildOutline` shows the **runtime visual tree** (projected onto the
outline model) and is now rebuilt on render-complete (`previewHost.StateChanged`), so the initial
source-tree outline is replaced by the real one. The design-view outline now:

- never shows resource definitions (`*.Resources` / `ResourceDictionary` subtrees are dropped);
- folds runtime-only nodes (framework template parts, generated item containers) into their nearest
  source-backed ancestor, so only elements the source declares (by `x:Name`) remain;
- omits elements the source marks not-visible (`Visibility="Collapsed"` / `x:Load="False"`).

`od.winui-designer.status` gained an `outlineNames` field for tests. Code view's outline
(`XamlOutlineContentHost`) still shows the whole document, resources included.

### Breakpoint-gutter icons removed for XAML/XML

`AvalonEdit.AddIn`'s `IconBarManager` drew declaration icons in the breakpoint gutter for **any**
file with a language-service outline; XAML/XML's outline is its element tree, whose kinds fell
through to the default Class icon. `UpdateLanguageServiceBookmarksAsync` now only adds
`OutlineBookmark`s for code files (`.cs`/`.csx`/`.vb`/`.fs`/`.fsi`/`.fsx`) and clears them otherwise.
Verified: a XAML source view reports `icon-bar-bookmarks` `{count:0}`, a C# file still gets them,
and a `.xaml.cs` code-behind keeps them.

### Known limitation: abstract-root pages (intentional)

Six pages root as `<pages:ItemsPageBase x:Class="...Page">` - a page whose root element is an
**abstract** `Page` subclass. `XamlReader` ignores `x:Class` and constructs the root's own type, so
it cannot instantiate them. They fail with the explicit message "Cannot preview this page: its root
element 'ItemsPageBase' is an abstract class...". A rewrite to the nearest instantiable base was
implemented and then **deliberately reverted** (product decision, 2026-09-12); these six stay as
expected failures: FlipView, GridView, ItemsRepeater, ItemsView, ListView, ParallaxView.

### Diagnostic switches added/used

- `OD_DESIGNHOST_NO_DESIGN_TEMPLATES=1` - disables `ApplyDesignTimeControlTemplates`, to test whether
  a newer SDK fixed a crash (it did not, for AutoSuggestBox/MapControl).
- `OD_DESIGNHOST_BOUNDS_LOG=1` - writes root/render sizes to `%TEMP%\opendevelop-designhost-bounds.log`.
- `OD_DESIGNHOST_XAML_DUMP=<dir>` - dumps the exact text handed to `XamlReader`.
- `OD_DESIGNHOST_NO_REPAIR=1`, `OD_DESIGNHOST_NO_SUBSTITUTE=1`, `OD_DESIGNHOST_NO_NS_FALLBACK=1` - as before.

### Residual risks / follow-ups

- The expanded `WinUIGalleryDesignerTests` covers 23 pages; a re-sweep after the empty-`Frame` fix
  should report 114 RENDER / 6 ABSTRACT / 0 EMPTY. The committed curated corpus should be extended
  to pin every page a sweep classifies as renderable.
- Promiscuous, corpus-wide changes (2.4.0 upgrade, accent literal rewrite, design-time templates,
  design-time-markup removal) are exercised per-class but only the whole-corpus sweep verifies the
  ~106 previously-rendering pages did not regress.
- Temporary tooling: `WinUIGallerySweep.cs` and `WinUIGalleryProbe.cs` are gated diagnostic tests,
  not part of the normal suite.

### Coordinate investigation: independent raster oracle (2026-09-13)

Status: investigation and proposed contract, NOT a verified fix for Gallery SettingsPage.
The earlier frame/root width-ratio compensation is not validated and must not be described as
a reliable solution. A different returned bitmap size alone does not establish the capture
origin, clipping or scaling. Do not ship further positional compensation based on those sizes.

Confirmed source findings:

- `ShowSelection` and `QueryElementBounds` both read `nodesByName`. DevFlow's agreement between
  selection and element rectangles therefore cannot establish agreement with rendered pixels.
- `GetBoundsInRoot` accumulates ActualOffset/layout slots and ignores RenderTransform and other
  visual transforms. Zero ActualOffset is not a missing-value sentinel; a slot fallback can
  also add a position which is not the rendered position.
- The recent BuildTree scaling is absent from CollectHits and SetEventCore's tree rebuild.
  Consequently selection, hit testing and event-only updates can use different units.
- UnoDesignSurfaceControl.ShowFrame divides frame dimensions by Render.Dpi and positions
  overlays in logical design units. Changing only tree coordinates to physical pixels violates
  that existing client contract at non-unit DPI.
- RenderAsync computes its requested size before awaiting native rendering, while the tree
  is read afterwards. A later tree read does not prove it describes the captured layout epoch.

Experiment added: `UnoDesignHostRpcTests.ChildHost_GeometryMatchesRasterPixels`. A white
320x240 Grid contains a magenta 60x40 Border at Canvas position (110,70). The test decodes
the ordinary RPC BGRA frame with DesignerFrameCodec and independently scans magenta pixels;
it does not call screenshot/export endpoints or take an OS screenshot. It compares all four
pixel edges with the reported node, with a one-pixel tolerance.

| Experiment | Observed raster edges L,T,R,B | Reported tree edges | Result |
| --- | --- | --- | --- |
| No transform | 110,70,170,110 | 110,70,170,110 | Pass |
| TranslateTransform(23,17) | 133,87,193,127 | 110,70,170,110 | Fail; max error 23 px |

These two cases ran against the legacy standalone deployed MicrosoftHost DLL at
`AddIns/DisplayBindings/WinUIXamlDesigner/MicrosoftHost/WinUIXamlDesigner.MicrosoftHost.dll`.
This proves a flaw in the manual bounds method, not the exact cause of the Gallery buttons.
The explicit newer `MicrosoftHost/net10.0` run produced no frame: without an app metadata
context it failed on AnimatedIcon.State in framework resources. Both cases failed before
geometry measurement; this is not evidence for or against their positional accuracy.

Reproduction (Microsoft.Testing.Platform, not VSTest arguments):

```powershell
dotnet test src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.MicrosoftHost.Tests/WinUIXamlDesigner.MicrosoftHost.Tests.csproj -- --filter-method '*ChildHost_GeometryMatchesRasterPixels' --no-progress
```

The standalone Remote project now defines DESIGNER_STANDALONE_CLIENT so shared client log
output goes to stderr instead of depending on the IDE Output pad. The pixel regression is
intentionally currently failing for the transformed case; it is a reproduction, not a green
acceptance result. The earlier root-size equality assertions are insufficient: matching root
size cannot detect this failure and assumes pixel units not supported by the current client.

Proposed reliable contract and implementation sequence:

1. Keep element local/layout bounds separate from the four visual corners in design-root DIPs.
   Obtain the latter through the backend's committed visual transform (WinUI TransformToVisual),
   including ancestor transforms and scrolling. Test this after settled native layout; do not
   assume Uno and Microsoft WinUI have identical commit semantics. A transformed control may
   need a quadrilateral; an axis-aligned bounding box loses rotation information. Report clipping
   separately. ToggleSwitch layout/hit bounds may legitimately exceed its painted track.
2. Capture an explicit, fixed-size design surface with a defined origin and background. Let its
   parent layout settle. Record requested capture rectangle, actual raster dimensions and the
   full DesignToRaster affine transform, including origin translation; never derive this matrix
   from a width ratio alone. Validate it with at least three non-collinear colored markers plus
   a fourth held-out marker. Marker disagreement means the proposed capture mapping is wrong.
3. Publish image, geometry, clipping and transforms together with SessionId, document version,
   monotonically increasing FrameId and layout epoch. During capture, detect layout changes and
   retry rather than combine old pixels with new geometry. Freeze animations for deterministic
   diagnostics; a dispatcher delay alone is not a composition completion fence.
4. The frontend composes DesignToRaster with RasterToCanvas and CanvasToScreen. Image and
   overlays must share this mapping. Pointer input uses its inverse and includes FrameId.
   Reject or explicitly remap stale input. SetBounds remains in layout DIPs; raster coordinates
   must never silently become XAML Width/Height/Margin. Event-only updates retain the same
   geometry epoch or request a complete fresh frame.
5. Put matrix math, frame identity, coordinate validation and structured diagnostics in designer
   common. Keep framework visual transforms, layout fencing, capture and native hit testing in
   each isolated host. No runtime visual objects cross RPC.

Diagnostic record per selected element/frame: actual loaded host path/hash, runtime and SDK,
source name and visual ancestry, layout bounds, visual corners, ancestor scroll/transform/clip,
capture rect, DesignToRaster, raster size, RasterToCanvas, canvas viewport/zoom/pan, display DPI,
CanvasToScreen, predicted screen polygon, independently measured marker edges and residuals.
DevFlow should expose these in one atomic response, not return two aliases of node bounds.

Layered acceptance:

- Backend-only: compare raster marker edges against native visual geometry at several positions,
  nested margins/padding, transforms, scroll offsets, clipping, and DPR 1/1.25/1.5/2. Include roots
  larger than the native window. Require <=1 physical-pixel edge error for axis-aligned markers.
- Frontend-only: feed a known frame plus known marker geometry, vary fit/zoom/pan/display DPI,
  and compare actual image visual mapping with overlay visual mapping. Round only at rasterization.
- End-to-end: use the actual Gallery .NET 9/ARM64/Windows App SDK 2.1.3 graph; probe soundToggle,
  ClearRecentBtn and UnfavoriteBtn in the same frame. For real templates use a diagnostic copy
  with a native-local marker/controlled brush change that does not alter layout, retaining the
  original full control bounds policy. Verify independent raster position, overlay and pointer
  hit result, then repeat after scrolling, resizing and frame updates. Do not declare Gallery
  fixed on the basis of the synthetic experiment or a matching pair of DevFlow rectangles.

Reference: Microsoft documents that RenderAsync's sized overload can change aspect ratio and
that TransformToVisual accounts for rendering transforms:
[RenderAsync](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.media.imaging.rendertargetbitmap.renderasync),
[coordinate transforms](https://learn.microsoft.com/en-us/windows/apps/develop/platform/xaml/transforms).

### Implemented Microsoft geometry correction and validation (2026-09-13 follow-up)

This supersedes the failing-experiment status above for the Microsoft host. The Uno fallback
has not been upgraded or validated for arbitrary transforms.

Implementation:

- The shared DesignHost exposes native-host hooks for committing layout, obtaining visual
  bounds, choosing the capture root and awaiting a composition frame. Microsoft uses
  TransformToVisual relative to its capture container for BOTH tree geometry and hit testing.
  The previous frame/root ratio scaling was removed; all geometry remains in design DIPs.
- Each document has its own fixed-size Grid capture container with a white background. Capturing
  the Page directly allowed blank content edges to be excluded before the sized RenderAsync
  overload scaled the bitmap. The opaque container establishes the complete capture extent.
  Transparent page areas now show the white design backdrop; alpha-preserving export is not
  implemented by this change.
- Commit native layout before reading RenderSize for the sized capture. A 2x experiment had
  previously returned 320x240 pixels while advertising DPI=2; it now matches the DIP geometry.
- Install DispatcherQueueSynchronizationContext in the custom WinUI bootstrap. Without it,
  an ordinary awaited task can resume off the UI thread. ConfigureAwait(true) alone cannot
  create a synchronization context.
- Await actual CompositionTarget.Rendering events and require three consecutive equal image
  payloads AND geometry snapshots before publishing. The settling window is bounded at two
  seconds; each composition wait is also bounded. Continuously changing content produces a
  diagnostic rather than a misleading selection frame. This is a bounded static-preview
  policy, not an animation playback contract or proof that a later animation cannot begin.
  Published frames receive an increasing Sequence within the design session.

Evidence from the full SettingsPage diagnostic copy:

1. Visual transforms alone: ClearRecentBtn raster edges (912,370,1038,400), reported
   (896,360,1016,390). The capture still altered position and width.
2. Fixed capture container: raster (896,382,1016,412), reported (896,360,1016,390).
   Horizontal distortion disappeared but the presentation had not settled.
3. Removing the page's ChildrenTransitions did NOT fix that residual 22px error. Changing
   the transform target to the capture container and simply rendering twice did not fix it
   either. Those observations do not support attributing the 22px error to that transition
   or to the root's offset. The attempted transition suppression was removed.
4. With the UI synchronization context installed, a diagnostic one-second wait did eliminate
   the residual error. The shipped candidate replaces that timing guess with the bounded
   composition/image/geometry settling check. No fixed one-second delay remains.

Validation completed:

- Six raster-oracle cases passed: plain marker, translated marker, 1280-wide surface, 2x DPI,
  actual SettingsCard with two buttons at 1x and 2x. All also hit-test the measured pixel centre
  converted to DIPs and require the expected named control in the result.
- Explicit GallerySettings_ButtonsMatchRasterPixels passed. It loads the FULL SettingsPage.xaml
  in memory, tests ClearRecentBtn and UnfavoriteBtn independently at DPI 1 and 2, and scans their
  diagnostic magenta background/border. It preserves border thickness/layout; only brushes and
  corner radius change. It does not alter the Gallery source file or use screenshot endpoints.
- The seven-test run passed. After restoring/building the matching SDK variant, the full-page
  test also passed against the actual deployed net9.0-windowsappsdk2.1.3 host DLL.
- The debug IDE was restarted and SettingsPage opened via DevFlow under Debug-Unpackaged|ARM64.
  It reports a 1280x753 frame, no document error, and 43 resolved names; the child process was
  checked to use net9.0-windowsappsdk2.1.3. DevFlow bounds are a presentation consistency check;
  the independent raster tests above remain the evidence for pixel alignment.
- Follow-up validation built the Microsoft host into the isolated `geometry-probe` deployment
  directory while the user-facing IDE child held the normal deployment DLL open. Against that
  isolated build and the real Gallery runtime/dependency graph,
  `GallerySettings_ButtonsMatchRasterPixels` passed (one explicit test, zero failures, 22.8s).
  That single test contains all four independently checked cases: ClearRecentBtn and
  UnfavoriteBtn at 1x and 2x DPI. This avoids treating a successful process exit, a frontend
  rectangle, or a same-source tree comparison as evidence of pixel alignment.
- The companion `ChildHost_GeometryMatchesRasterPixels` matrix also passed 6/6 (zero failures,
  24.3s) against that same isolated Microsoft build: untransformed and translated marker,
  normal and 1280-DIP widths, 1x and 2x DPI, plus the SettingsCard fixture. Test stdout is
  intentionally retained only as a temporary run artifact; this record contains the durable
  result and its exact host/dependency prerequisites.

To reproduce against the Gallery runtime, set `OD_GEOMETRY_RUNTIME_CONFIG` and
`OD_GEOMETRY_DEPS_FILE` to its built runtimeconfig/deps paths; set
`OD_GEOMETRY_APP_BIN` to that same output directory (so its custom controls and packages are
preloaded); set `OPENDEVELOP_WINUIDESIGNER_HOST_DLL` to the exact deployed Microsoft host DLL;
and set `OD_GEOMETRY_GALLERY_PAGE` to the full SettingsPage.xaml path. These variables are
deliberate: without them the shared test project defaults to the Uno host, whose transform
geometry and Gallery dependency graph are not the subject of this Microsoft-host regression test.

```powershell
dotnet test src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoHost.Tests/WinUIXamlDesigner.UnoHost.Tests.csproj -- --filter-method '*ChildHost_GeometryMatchesRasterPixels' '*GallerySettings_ButtonsMatchRasterPixels' --explicit on --no-progress
```

Remaining scope: arbitrary animated content, rotated selection polygons, fully clipped hit tests,
and frame-scoped stale pointer rejection need the broader protocol work described above. The
current transform hook returns an axis-aligned visual bounding box. This change does not claim
to complete that broader protocol or to have rerun the entire Gallery acceptance suite.

## Visual state preview (2026-09-17)

**Why.** A document's `VisualStateManager.VisualStateGroups` never render in the designer: the
surface only ever shows whatever state the page naturally loads in, so states like WinUI-Gallery
`SearchResultsPage.xaml`'s `NoResultsFound` or `NarrowLayout` were unreachable. The request started
as "should the Outline pad show the groups?" and the answer was no - the Outline is a visual-tree
browser whose nodes must be selectable on the canvas and have bounds, and a `VisualState` has
neither, so putting them there breaks the outline-to-surface contract the selection and geometry
code depends on. Listing them was never the problem; **previewing** them was.

**UI shape: one combo per group, not one combo for the document.** Visual state groups are
orthogonal - `SearchResultsPage` can be in `WideLayout` AND `NoResultsFound` simultaneously - so a
single "current state" picker would be wrong by construction. The shared `DesignerCanvas` toolbar
(all five designers) now hosts a `statesPanel` next to the theme combo, holding one `ComboBox` per
group, each offering `(none)` plus that group's states. `(none)` means "do not force this group".
The panel collapses entirely when the document declares no groups, which is almost every document,
so the toolbar is unchanged for normal work. Gated by `DesignerCanvasCapabilities.VisualStates` as
well, so a backend that cannot preview states never shows it.

**Protocol.** `DesignerSessionState.VisualStateGroups` (a `DesignerVisualStateGroup` per group:
name, states, currently-forced state) is populated in `DesignHost.FinishLayoutAsync` - the single
funnel every snapshot passes through, so the combos follow document switches and reloads with no
extra round trip. The new `design/go-to-state` RPC mirrors `design/theme` end to end
(`UnoDesignClient.GoToStateAsync` -> `DesignRpc` -> `DesignHost.GoToStateAsync`).

**`GoToState` cannot carry this on its own, and finding out why took three wrong turns.**
`VisualStateManager.GoToState(control, ...)` resolves the groups from a CONTROL's template root. The
design host, however, routinely previews a document's CONTENT rather than its root: WinUI-Gallery's
`SearchResultsPage` has an `ItemsPageBase` root the host cannot render, so
`UnrenderableRootUnwrapper` makes the inner `Grid` the design root. The groups are attached to that
Grid, and an unwrapped `Panel` root has no `Control` anywhere above it - so `GoToState` returns
false for every state. The first error message blamed the document ("Visual state 'WideLayout' was
not found in group 'LayoutVisualStates'") when the state was plainly there and the combo had been
populated from it; `GoToElementState`, which takes the state-groups root directly, is not on this
XAML flavour's public surface either.

So the host applies the state itself: `GoToState` is still tried first (documents whose root really
is a templated Control get the framework's own behaviour), and `TryApplyStateSetters` writes the
state's `VisualState.Setters` otherwise. **Read the setter shape from the runtime, not from the
XAML** - instrumentation showed the parser hands back something quite different from the markup:

```
XAML:    <Setter Target="resultsNavView.Visibility" Value="Collapsed" />
Runtime: path='Visibility'  Target.Target=NavigationView  Value=1 (Int32)
```

The element is already resolved into `Target.Target`, `Path` is the BARE property name, and an enum
value arrives as its underlying `Int32`. Splitting the path on `.` unconditionally - the obvious
reading of the markup - therefore skipped every setter of the normal, resolved shape, and even once
that was fixed a plain reflection `SetValue` rejected `1` for a `Visibility` property (hence
`CoerceSetterValue`). Both shapes are handled: resolved target + bare property, or null target +
`"element.Property"` resolved through the owner's `FindName`.

**Every apply reloads the document first.** Setters are one-way property writes with nothing to
un-write them, so switching `NarrowLayout` -> `WideLayout` would otherwise keep narrow's margins.
`GoToStateAsync` reloads, then re-applies every state still held in `forcedVisualStates`, which also
makes "(none)" fall out for free and keeps the groups independent (releasing one leaves the other
held - verified). `forcedVisualStates` is tracked rather than trusting
`VisualStateGroup.CurrentState`, which stays null for a state that was never entered naturally.

**A state that applies nothing is a success, not a failure.** `WideLayout` is pure `StateTriggers`
with no setters at all, and IS the unmodified arrangement - the reload alone already produces it.

Transitions are skipped: a design surface wants the settled end state, and `FinishLayoutAsync`'s
frame-stability loop would otherwise time out against a running animation with "The design preview
did not settle within two seconds".

**An error snapshot must still carry the groups.** The client repopulates its combos from every
snapshot, so an early-return error snapshot with an empty list blanked the whole states panel the
moment anything failed - the second symptom reported from the first build. The host now fills
`VisualStateGroups` on the failure path too, and the client only repopulates from a snapshot that
actually describes the document (`Tree != null`, or a non-empty group list).

**The Outline pad no longer lists the state machinery.** It used to show `ResultStates`,
`WideLayout` and the rest as ordinary nodes, because `XmlOutlineNode` - the source projection used
until the first render lands - filtered only resource dictionaries and happily walked into
`VisualStateManager.VisualStateGroups`. Those nodes cannot honour the outline's contract (click a
node, select that element on the canvas): a VisualState has no bounds and is not in the visual tree.
`IsVisualStateElement` now drops that subtree. The runtime projection never had the problem -
`BuildTree` only adds `UIElement` children, and a `VisualStateGroup` is not one.

**Driving it:** `od.winui-designer.visual-state` with no arguments lists the groups, their states
and whatever is currently forced; `<group> <state>` forces one; `<group>` with an empty state
releases it.

**Build gotcha this surfaced.** `DesignerCanvas` lives in `ICSharpCode.SharpDevelop.Widgets`, which
ships from the HOST PUBLISH, not from the AddIns tree. Rebuilding only the designer AddIn projects
left a stale Widgets assembly in the payload and the app failed at document-open with
`MissingMethodException: DesignerCanvas.add_VisualStateRequested` - a signature-level mismatch, not
a logic bug. Any change to a shared widget needs `./dist.ps1 -From host` (or a full build), not just
`./build.ps1 <addin>`.
