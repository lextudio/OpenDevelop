# WinUI designer landscape: XAML Studio, OpenDevelop, WinAppVSCE and WinUIDesigner

Measured 2026-11-11/12 against:

| Tree | Revision | Notes |
|---|---|---|
| OpenDevelop | `607e38fc05aa34772eba5954015dfd1b4550debf` | this checkout |
| `microsoft/WinAppVSCE` PR #323 | `6e4b3887e7975413dc26f58012a4e3296fbb6c55` (`zateutsch-xaml-previewer`, draft) | GitHub PR head |
| `0x5bfa/WinUIDesigner` | `456640e04fd2ae0f9e738c71503889844cf4060b` (`main`, v1.4.0) | GitHub main |
| XAML Studio | `d711d64fed7d07d5c2dda545d255d1007588ab78` (`externals/xamlstudio`, v2 `dev`) | submodule of this checkout |

This note compares the four technical approaches that have actually rendered WinUI/XAML in this
product line, not their feature marketing: XAML Studio's standalone renderer, and the three current
or likely-shipped hosts. It records enough source-level evidence that each claim can be re-checked
without re-reading the trees, and it deliberately separates:

- what a project **does today** (code),
- what it **says it will do** (workstream/handoff documents), and
- what its **structural ceiling** is (who owns the document model, what cannot be fixed without
  changing the architecture).

The short version: three of the four render XAML outside the editor process; XAML Studio is the
in-process baseline. They diverge on one question — **who owns the editable document model**. XAML
Studio and WinAppVSCE own none; WinUIDesigner borrows Visual Studio's; OpenDevelop owns its own.
Almost every capability and risk difference below follows from that.

---

## Summary

| | XAML Studio | OpenDevelop | WinAppVSCE PR #323 | WinUIDesigner |
|---|---|---|---|---|
| Product | Standalone WinUI/XAML prototyping app (UWP + WinUI 2) | Full designer in the OpenDevelop IDE | Live previewer inside Visual Studio (ported from a VS Code previewer) | Full designer inside Visual Studio, reusing VS's own XAML designer |
| Renderer | **In-process** `XamlRenderService` → live `UIElement` via `XamlReader.Load`; no snapshot, no child process | Out-of-process Uno or Microsoft WinUI child; **bitmap frames + element tree** | Out-of-process `Surface.exe`; **native HWND reparent** (VS) or PNG frames (VS Code) | Out-of-process `WinUISurface.exe`; **native WinUI XAML island** inside VS's artboard |
| Document model | None (Monaco text + live objects; single-attribute text patch) | Parent-side XAML source model (`XDocument`) + child live model; versioned `design/*` mutations | None (text + live objects); property edits are live-only | Visual Studio's `Markup.dll` document model + edit transactions |
| Writeback / undo | Partial: one attribute at a time by string patching; no designer undo | Yes (parent) | No (workstream I1) | Yes (VS) |
| Structural editing | No (toolbox appends a tag to text; no drag/resize/snap) | Yes (add/delete/move/resize/wrap/reorder) | No | Yes (VS tools) |
| Type/resource fidelity | Real UWP/WinUI 2 runtime in-process; UWP app resources, images/media; `{Binding}` debug only, **no `{x:Bind}`** | Reflection metadata + `.xbf` mirror + placeholder substitution; compiled preview app planned | Version-matched host built from the project's WASDK/NuGet/PRI closure; fixed Toolkit closure still placeholders | VS build system + markup services + construction actions |
| Cross-platform | No (Windows/UWP only) | Yes (Uno path: Windows/macOS/Linux/device) | No | No (Windows + VS only) |
| Main risk | UWP/WinUI 2 dead end; no isolation; text patching | WinUI compile-time semantics; five backends to maintain | No document model; fidelity tax of cleaning/placeholder | Private VS assemblies, private fields and a process-wide detour |
| Scale (product code) | ~7.9k app + ~3.6k toolkit | ~16.7k WinUI designer + ~11.7k shared designer layers | ~14.0k extension + ~15.8k renderer + ~1.5k provisioner | ~9.0k C# + 531 lines native TAP |
| Test signal | toolkit unit tests only (~1.2k lines); no integration | Uno host RPC tests, DevFlow journeys, WinUI Gallery corpus | 458 unit + 63 in-VS on 3 CI runners + fault/smoke/provisioner fixtures | 462 lines of tests |

---

## 1. XAML Studio: the fourth baseline

Both statements are true at once, because they describe different axes. "We support the Microsoft
WinUI designer" is about the **project runtime**; "based on XAML Studio" is about the **renderer
implementation**. For two weeks in August 2026 the Microsoft WinUI designer really was the XAML
Studio renderer, and since 2026-08-28 it has been a different, real-WinUI host.

### 1.1 Two axes: project runtime and preview runtime

| Period | Project runtime | Preview runtime | Renderer |
|---|---|---|---|
| 2026-08-13 → 2026-08-28 | any `WinUI` project, including `Microsoft.WindowsAppSDK` | **ProGPU.WinUI** (in-process) | **XAML Studio `XamlRenderService`**, linked into `XamlStudio.Toolkit.ProGPU` |
| 2026-08-15 → present | Uno | Uno (out-of-process) | Uno's production XAML parser, new `DesignHost` |
| 2026-08-28 → present | Microsoft WinUI | Microsoft WinUI (out-of-process, Windows only) | WinUI's `XamlReader` + metadata provider + `.xbf` mirror |
| 2026-08-28 → 2026-11-09 | ProGPU.WinUI | ProGPU.WinUI (in-process) | XAML Studio renderer (removed) |

Why the first row is not a mistake: `XamlRuntimeKind` did not exist until
`a85f2ccbcb Add Microsoft WinForms/WPF/WinUI backends` (2026-08-28). The initial detector classified
`Microsoft.WindowsAppSDK` / `Microsoft.UI.Xaml` / `UseWinUI` as plain `XamlFrameworkKind.WinUI`
([`XamlFrameworkDetector.cs` at `05a9b86`](https://github.com/lextudio/OpenDevelop/blob/05a9b86ae5da3889ce257b5fd596e1ebfaa0bb47/src/Main/Base/Project/Src/LanguageServices/Xaml/XamlFrameworkDetector.cs)),
and the only registered host was ProGPU's. So a Microsoft WinUI **project** was previewed by the
XAML Studio renderer — running on **ProGPU.WinUI**, a from-scratch `Microsoft.UI.Xaml`
reimplementation, never the real Windows App SDK. The Microsoft host added on 2026-08-28 is what
made the preview runtime match the project runtime.

### 1.2 Yes, the first WinUI designer was genuinely XAML Studio based

The initial WinUI designer commit, `05a9b86ae5 Implement WinUI designer and improve testing`
(2026-08-13), added `WinUIXamlDesigner.ProGPUHost` and `XamlStudio.Toolkit.ProGPU`. The latter was not
a reimplementation; it linked the upstream sources directly:

```xml
<Compile Include="..\..\..\..\..\externals\xamlstudio\XamlStudio.Toolkit\Extensions\StringExtensions.cs" ... />
<Compile Include="..\..\..\..\..\externals\xamlstudio\XamlStudio.Toolkit\Models\XamlRenderResultContext.cs" ... />
<Compile Include="..\..\..\..\..\externals\xamlstudio\XamlStudio.Toolkit\Parsers\*.cs" ... />
<Compile Include="..\..\..\..\..\externals\xamlstudio\XamlStudio.Toolkit\Services\XamlBindingWrapperManager.cs" ... />
<Compile Include="..\..\..\..\..\externals\xamlstudio\XamlStudio.Toolkit\Services\XamlRenderService\XamlRenderService.ParsePre.cs" ... />
<Compile Include="Port\ProGpuXamlRenderService.cs" />
```

and `ProGpuRuntimeHost` held a `XamlRenderService renderService`. So at birth, "the WinUI designer is
XAML Studio based" was exactly right: XAML Studio owned preprocessing, binding inspection,
diagnostics and the render result model; ProGPU supplied the runtime and the execution adapter. The
IDE shell added in the same commit (`WinUIXamlDocumentEditor`, `WinUIXamlToolbox`,
`WinUIXamlDesignerViewContent`) was OpenDevelop's own code, not XAML Studio's.

### 1.3 Why both newer profiles are not XAML Studio based

- `d1c371226f Develop out-of-process WinUI designer` (2026-08-15) added `WinUIXamlDesigner.UnoHost`
  as new code: a `DesignHost` that calls `XamlReader.Load`, a `DesignRpc` target and a headless
  dispatcher. It exists because the in-process ProGPU runtime is a from-scratch
  `Microsoft.UI.Xaml` reimplementation and a single process cannot give real Uno.WinUI types their
  own identity at the same time; see
  [`winui-designer.md`](winui-designer.md) and
  [`designer-common.md`](designer-common.md#removed-profile).
- `a85f2ccbcb Add Microsoft WinForms/WPF/WinUI backends` (2026-08-28) introduced runtime routing and
  added the Microsoft WinUI host by **source-linking the Uno host's implementation**, not by reviving
  the XAML Studio port:

  ```xml
  <Compile Include="..\WinUIXamlDesigner.UnoHost\DesignHost.cs" Link="DesignHost.cs" />
  <Compile Include="..\WinUIXamlDesigner.UnoHost\DesignRpc.cs" Link="DesignRpc.cs" />
  ```

  Its `Program.cs` says so explicitly: "Only the BOOTSTRAP lives here — the designer implementation
  (`DesignHost.cs`) and the whole 19-method DDP surface (`DesignRpc.cs`) are source-linked from the
  Uno host and compile unchanged, because both target the same `Microsoft.UI.Xaml` API."
  [`MicrosoftHost.csproj`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.MicrosoftHost/WinUIXamlDesigner.MicrosoftHost.csproj),
  [`MicrosoftHost/Program.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.MicrosoftHost/Program.cs)
- `76f51cc6e7 Remove the retired in-process ProGPU WinUI designer` (2026-11-09) deleted the ProGPU
  host, `XamlStudio.Toolkit.ProGPU`, the ProGPU language-server profile, the fixture and its tests —
  2,040 lines. Its message states the outcome: "Uno (out-of-process) and Microsoft WinUI remain the
  WinUI design hosts." The trigger was upstream: ProGPU removed its XAML compiler/designer packages
  (`2deb0316c`), so the port could not build any more.

`externals/xamlstudio` is still a pinned submodule (`d711d64`), but **no project references it**:
a search for `xamlstudio` in `*.csproj`, `*.props`, `*.targets`, `*.sln` and `*.slnx` outside
`externals/` returns nothing.

### 1.4 What XAML Studio is, as an engine

XAML Studio is a **standalone prototyping app** for WinUI XAML, not an IDE integration. It is a UWP
app (`TargetPlatformIdentifier UAP`, Windows 10 17763+) that hosts a Monaco editor in a WebView and
renders loose XAML next to it. It is MIT-licensed, a .NET Foundation seed project, upstream
`dotnet/XAMLStudio`; the pinned fork adds WinUI 2 + `System.Text.Json` (`c2f7129`, 2026-07-25).
[`XamlStudio/XamlStudio.csproj`](https://github.com/lextudio/xamlstudio/blob/d711d64fed7d07d5c2dda545d255d1007588ab78/XamlStudio/XamlStudio.csproj)

It is split into two pieces, and only the second is reusable:

| Piece | Size | Responsibility |
|---|---|---|
| `XamlStudio` app | 7,851 lines / 67 files | Monaco document UX, explorer, documentation toolbox, Properties/visual tree/state panels, data sources, binding debugger UI |
| `XamlStudio.Toolkit` | 3,612 lines / 35 files | `XamlRenderService`, string/XML parsers, binding wrapper, tree coordinator, result models |
| `XamlStudio.Toolkit.UnitTests` | 1,152 lines | parser/tree-coordinator tests |

The renderer is a text pipeline, not a document model:

1. **Preprocess the string.** `PreProcessXmlns` finds the first element with a regex and injects
   missing `xmlns` declarations; there is no XML tree until later.
   [`XamlRenderService.ParsePre.cs`](https://github.com/lextudio/xamlstudio/blob/d711d64fed7d07d5c2dda545d255d1007588ab78/XamlStudio.Toolkit/Services/XamlRenderService/XamlRenderService.ParsePre.cs)
2. **Intercept `{Binding}`.** `InterceptBindings` rewrites binding expressions through
   `XamlBindingWrapperConverter` so the debugger can record values. The searcher regex only matches
   `Binding`; the `x:Bind` alternative is **commented out**.
   [`XamlRenderService.ParseBinding.cs`](https://github.com/lextudio/xamlstudio/blob/d711d64fed7d07d5c2dda545d255d1007588ab78/XamlStudio.Toolkit/Services/XamlRenderService/XamlRenderService.ParseBinding.cs),
   [`XamlBindingWrapperManager.cs`](https://github.com/lextudio/xamlstudio/blob/d711d64fed7d07d5c2dda545d255d1007588ab78/XamlStudio.Toolkit/Services/XamlBindingWrapperManager.cs)
3. **Load it for real.** `XamlReader.Load` (or `LoadWithInitialTemplateValidation`) constructs live
   controls, applies templates and runs layout.
   [`XamlRenderService.cs`](https://github.com/lextudio/xamlstudio/blob/d711d64fed7d07d5c2dda545d255d1007588ab78/XamlStudio.Toolkit/Services/XamlRenderService/XamlRenderService.cs)
4. **Recover error positions by string.** It parses `"Line: N"` / `"Position: N"` out of the
   exception message and searches the original text for that fragment.
5. **Post-process.** `d:` design data, the app's `App.xaml` resource dictionary (merged into the
   root), and `ms-appx:`/`ms-resource:` images and media loaded from a folder.
6. **Return** `XamlRenderResultContext` — a live `object Element`, the intercepted bindings, errors
   and the rendered text. It has **no bounds, no pixels, no sequence and no tree**.

The visual↔source mapping is a separate 496-line hand-maintained table,
`XamlXmlTreeCoordinator.AttributeNameToDependencyProperty`, which maps attribute names to
`DependencyProperty`s for the installed UWP control set.
[`XamlXmlTreeCoordinator.cs`](https://github.com/lextudio/xamlstudio/blob/d711d64fed7d07d5c2dda545d255d1007588ab78/XamlStudio.Toolkit/Services/XamlXmlTreeCoordinator.cs)

### 1.5 What it can and cannot do

What it genuinely does well:

- A real UWP/WinUI 2 runtime, in process, with real app resources, images and media — the preview
  is the runtime, not an approximation.
- The **binding debugger** is still the most complete piece of its kind in this comparison: it
  rewrites `{Binding}` so values can be inspected at runtime.
- A small, MIT, self-contained surface (11.5k lines including tests) that is easy to study.

Why it is a previewer, not a designer:

- **UWP/WinUI 2 only.** It cannot run on WinUI 3 / Windows App SDK, and the tree coordinator cannot
  even compile there: it names UWP-only members (`BackgroundSizing`, `SemanticZoomOwner`,
  `IsZoomedInView`). The renderer passes `Microsoft.UI.Xaml` objects that only type-check because
  WinUI 2 type-forwards to `Windows.UI.Xaml`.
- **In-process.** No crash isolation, no untrusted project assemblies, no project build, metadata
  provider or PRI resolution.
- **No document model.** The source text is the only truth; the property panel writes live objects
  (`SelectedElement.SetValue`) and "Add to XAML" patches one line/attribute by string manipulation,
  with its own TODO admitting it should edit the XML tree instead. There is no undo, no transaction
  and no round-trip guarantee.
  [`Properties.xaml.cs`](https://github.com/lextudio/xamlstudio/blob/d711d64fed7d07d5c2dda545d255d1007588ab78/XamlStudio/Views/Properties.xaml.cs),
  [`Document.Design.xaml.cs`](https://github.com/lextudio/xamlstudio/blob/d711d64fed7d07d5c2dda545d255d1007588ab78/XamlStudio/Views/Document.Design.xaml.cs)
- **No structural design.** There is no drag, resize, snapping, marquee, multi-select or designer
  undo. Selection is an adorner over the live tree; the Toolbox appends a tag to the editor text.
  [`Toolbox.xaml.cs`](https://github.com/lextudio/xamlstudio/blob/d711d64fed7d07d5c2dda545d255d1007588ab78/XamlStudio/Views/Toolbox.xaml.cs)
- **No `{x:Bind}`, no code-behind.** The binding interceptor deliberately matches only `{Binding}`.
- **No frame contract.** `XamlRenderResultContext.Element` is a live object; it cannot cross a
  process boundary, and there is no raster path in the toolkit at all (the app's
  `RenderTargetBitmap` use is the Windows share sheet, with hard-coded dimensions).

### 1.6 Its ceiling and potential

- **As a product** it is a prototyping playground, and its own README positions it that way. It has
  no project system, no build integration and no design gestures, so it cannot become an IDE
  designer by adding features; it would need the document model and the out-of-process host it
  deliberately does not have. Microsoft's own WinUI tooling moved to the VS/VS Code previewers
  (§3), and the fork's last commits are July 2026.
- **As an engine** it is a good reference for XAML text tolerance, binding telemetry and "what can
  loose XAML do", and it is the origin of most of the problem knowledge now documented in
  [`winui-designer.md`](winui-designer.md). It is not a viable renderer for a modern designer: a
  WinUI 3 port would replace the load path, the tree coordinator and the result model, and the hard
  problems (compiled XAML, generated metadata, PRI) are outside its design.
- **As a thesis** it is the cleanest proof that *previewer ≠ designer*: it has selection, a property
  panel, a visual tree and visual states, and still cannot edit a document.

### 1.7 Why the old description still circulates

These are documentation defects, not evidence of a live dependency. They should be corrected so the
next reader is not misled the same way:

- The `IWinUIXamlRuntimeHost` doc comment still says "Implemented by the independent XAML
  Studio/Uno runtime assembly".
  [`WinUIXamlHost.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.AddIn/WinUIXamlHost.cs)
- [`winui-designer.md`](winui-designer.md) §"Reuse Boundary with UnoDevelop and XAML Studio" still
  describes `XamlRenderService` as the renderer and says it "should compile into a standalone
  WinUI/Uno renderer assembly". That assembly existed — `XamlStudio.Toolkit.ProGPU` — and was
  deleted with the ProGPU profile.
- [`xaml-services.md`](xaml-services.md) still lists "wiring up the original XAML Studio renderer" as
  the WinUI designer's state.
- [`build-from-source.md`](build-from-source.md) still names the renderer as a build input.

### 1.8 What does survive

- **Architecture.** The "IDE shell + separate runtime assembly behind a runtime-neutral interface"
  boundary was validated by the XAML Studio/Uno port and is the shape the current hosts still use.
- **Problem knowledge.** `{x:Bind}` stripping, namespace injection, error-position mapping and the
  "app controls vs page source" distinction were first learned on that renderer and are now
  documented in [`winui-designer.md`](winui-designer.md).
- **Naming.** The interface comment and the technote section above still use it.

What does not survive is any build reference, linked source file or package. The Microsoft WinUI
designer is based on **WinUI's own runtime parser and the Uno host's implementation**, and that is
the subject of §2.


---

## 2. OpenDevelop

### 2.1 Shape and scale

WinUI designer add-in and hosts (52 `.cs`, 16,692 lines, excluding `bin/`, `obj/` and the `UnoHost.Tests` project):

| Path | Files | Responsibility |
|---|---|---|
| [`WinUIXamlDesigner.AddIn/`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.AddIn) | 16 | IDE view content, source model, toolbox, context commands, property adapter, hot reload adapter |
| [`WinUIXamlDesigner.UnoHost/`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoHost) | 20 | The design child: DDP target, document load/repair, render, RPC |
| [`WinUIXamlDesigner.UnoDesignHost/`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoDesignHost) | 5 | Parent-side lease/client/bootstrap for both Uno and Microsoft children |
| [`WinUIXamlDesigner.MicrosoftHost/`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.MicrosoftHost) | 6 | Windows App SDK bootstrap, reflection metadata, `.xbf` mirror, Fluent defaults |
| [`WinUIXamlDesigner.LanguageServer.*/`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner) | 4 | WinUI/Uno XAML language-server profiles |

Common designer layers used by WinUI (and four other designers):

| Layer | Lines | Responsibility |
|---|---|---|
| [`Designer.Remote`](../../src/Main/Designer/Designer.Remote) | 3,839 | DDP DTOs, host process, lease/pool/recovery, frame codec |
| [`Designer.Server`](../../src/Main/Designer/Designer.Server) | 361 | Child bootstrap, document registry, source-model host |
| [`Designer.Shell`](../../src/Main/Designer/Designer.Shell) | 1,644 | Selection, command, pad, toolbox-drop controllers |
| [`Designer.Presentation`](../../src/Main/Designer/Designer.Presentation) | 5,852 | Frame presenter, viewport, hit testing, snapping, editors |
| [`DesignerCanvas`](../../src/AddIns/DisplayBindings/DesignerCanvas) | 3,721 (3,364 in the three core files) | The shared WPF canvas shell |

[`in-process-analysis.md`](in-process-analysis.md) (line 39) still says "3 files / 2282 lines" for the canvas; that
number is stale. Measured today: `DesignSurface.cs` 2,345 + `DesignSurfaceController.cs` 959 +
`IDesignCanvasBackend.cs` 60 = 3,364, and 3,721 across the whole add-in directory.

### 2.2 Process boundary and transport

- Each child is a separate process launched with `dotnet <child dll> <port> <token>`; the parent
  opens a `TcpListener` on `127.0.0.1:0`, passes the chosen port, and waits for the child to
  connect back. Connect timeout is 30 s (180 s for Android, 90 s for iOS).
  [`DesignerHostProcessClient.cs`](../../src/Main/Designer/Designer.Remote/DesignerHostProcessClient.cs)
- The wire is **StreamJsonRpc over `HeaderDelimitedMessageHandler`** on that loopback TCP stream, not
  a private binary protocol.
  [`DesignerJsonRpc.cs`](../../src/Main/Designer/Designer.Remote/DesignerJsonRpc.cs)
- Authentication is a 32-byte random hex token minted by the parent and validated by the child in
  `initialize`; the child also returns its PID, session id and protocol version. The parent rejects
  a wrong protocol version, PID or session echo.
  [`DesignerHostHandshakeValidator`](../../src/Main/Designer/Designer.Server/DesignerHostHandshakeValidator.cs),
  [`DesignerHostProcessClient.cs`](../../src/Main/Designer/Designer.Remote/DesignerHostProcessClient.cs)
- The protocol is versioned and grew additively: **DDP v70** today, with each version's delta
  documented in the constant.
  [`DesignerProtocol.cs`](../../src/Main/Designer/Designer.Remote/DesignerProtocol.cs#L119)
- Documents are first-class: `SessionId`, `DocumentId`, `Version` (generation), plus
  `ProjectFileName`, `TargetFramework`, `Architecture`, project assembly and resolved reference
  assembly paths in every snapshot.
  [`DesignerDocumentSnapshot`](../../src/Main/Designer/Designer.Remote/DesignerProtocol.cs#L139)
- Multiple documents share one child through `DesignerDocumentRegistry`; a pool key partitions
  incompatible runtimes/architectures, and leases survive child crashes through
  `SharedDesignerHostRecovery`.
  [`DesignerDocumentRegistry.cs`](../../src/Main/Designer/Designer.Server/DesignerDocumentRegistry.cs),
  [`SharedDesignerHostRecovery.cs`](../../src/Main/Designer/Designer.Remote/SharedDesignerHostRecovery.cs)

For comparison: this is materially more protocol than WinAppVSCE v1 (no document ids, no request
correlation) and materially more open than WinUIDesigner's reuse of VS's private TAP protocol.

### 2.3 Canvas, snapshot, editing

- The IDE never hosts a live XAML object. The child returns a snapshot: an element tree with bounds
  plus a raster frame. Frames are either base64 PNG (WinForms) or **deflate-compressed BGRA**
  (WinUI/Uno), with DPI and render time.
  [`DesignerRenderFrame`](../../src/Main/Designer/Designer.Remote/DesignerProtocol.cs#L550)
- Hit testing, hover, selection, marquee and adorner placement are answered **locally** from the
  last snapshot. The WinUI-specific pick path is part of the snapshot
  (`DesignerHitTestResult.Chain`/`PickPath`).
  [`DesignerSnapshotHitTester.cs`](../../src/Main/Designer/Designer.Presentation/DesignerSnapshotHitTester.cs)
- The shared canvas supplies zoom/fit/pan, gridlines, device chrome, resize handles and the
  "switch visual state" overlay; a backend only declares what it supports through its capability
  object.
  [`DesignSurface.cs`](../../src/AddIns/DisplayBindings/DesignerCanvas/DesignSurface.cs)
- Consequence: **no compositor effects and no live animations** in the preview. Everything the
  snapshot does not contain is invisible. In exchange, the canvas is identical on every OS and
  every runtime, and a crashing child cannot take the IDE down.

### 2.4 The two documents (the important part)

OpenDevelop's WinUI designer has two models that must agree:

1. **Parent-side source of truth.** `WinUIXamlDocumentEditor` parses the file with
   `XDocument.Parse(..., PreserveWhitespace | SetLineInfo)` and owns the undo/redo stacks.
   [`WinUIXamlDocumentEditor.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.AddIn/WinUIXamlDocumentEditor.cs)
2. **Child-side live tree.** The child loads the same text, applies `design/*` requests to live
   objects for fast incremental rendering, and returns a fresh snapshot.

Every gesture is a parent edit first: the add-in mutates the `XDocument`, then calls
`ApplyDocumentChange`, which marks the file dirty, rebuilds the outline, updates the selectable
names and asks the host to render — incrementally when the change is one the child can apply
(`design/set-property`, `design/set-bounds`), otherwise with a full reload.
[`WinUIXamlDesignerViewContent.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.AddIn/WinUIXamlDesignerViewContent.cs)

A rejected incremental mutation (stale version, unsupported edit, RPC failure) falls back to a full
document load using the parent's current text, so the parent never has to know which path ran.
[`UnoDesignRuntimeHost.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoDesignHost/UnoDesignRuntimeHost.cs)

`session/flush` is deliberately a stub in the Uno client: "this host holds no independent
child-side edit buffer, so this reports the current XAML as the sole file".
[`UnoDesignClient.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoDesignHost/UnoDesignClient.cs)

This is the structural opposite of WinAppVSCE and the structural sibling of WinUIDesigner: the
editable document is owned by the IDE side, and the renderer is a mirror.

### 2.5 Runtime routing

- `XamlFrameworkDetector` classifies the file by the project that owns it, not by looking at the
  markup or the installed renderer. `Uno.Sdk`/`Uno.WinUI` → Uno; `Microsoft.WindowsAppSDK`,
  `Microsoft.UI.Xaml`, `UseWinUI=true` → Microsoft WinUI.
- Off Windows a Microsoft WinUI project starts **no** child; the canvas shows the shared
  "unsupported on this OS" message.
  [`WinUIXamlHost.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.AddIn/WinUIXamlHost.cs)
- The registry tries the most recently registered factory first and lets it decline, which is how
  the Microsoft child takes precedence over the Uno compatibility child on Windows.
  [`WinUIXamlRuntimeHostRegistry`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.AddIn/WinUIXamlHost.cs)
- Host binaries are selected by **architecture and CLR major** from the designed app's
  `runtimeconfig`, with x64 and arm64 slices shipped side by side, because WinRT/COM interop and
  generated XAML metadata are architecture/CLR specific.
  [`UnoDesignRuntimeHostBootstrap.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoDesignHost/UnoDesignRuntimeHostBootstrap.cs)

### 2.6 Uno load path

The Uno child uses Uno's production `XamlStringParser` through `XamlReader.Load`, its own headless
dispatcher, and a 19-method DDP surface: `initialize`, `session/open|update|flush|close`,
`design/set-property|set-event|add-element|set-bounds|delete-elements|rename|theme|go-to-state`,
`app/resources`, `design/hit-test|export-png`, `ping`, `shutdown`.
[`DesignRpc.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoHost/DesignRpc.cs)

Design hosts share one process per compatible runtime graph; each document is a session in
`DesignerDocumentRegistry`, and every RPC marshals onto the UI dispatcher.
[`DesignHost.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoHost/DesignHost.cs)

Text repair is a set of narrow, named transforms (`XamlDocumentRepair`,
`ConditionalXmlnsStripper`, `CompileTimeBindingStripper`, `CompiledXamlControlSubstituter`,
`UnrenderableRootUnwrapper`, `EventHandlerAttributeStripper`), all in
[`UnoHost/`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoHost), not a
general-purpose XAML rewriter.

### 2.7 Microsoft WinUI load path

The Microsoft child is the same `DesignRpc`/`DesignHost` code source-linked, with a different
bootstrap:

- `Application.Start` owns the calling thread in WinUI, so the DDP loop runs on a worker and the UI
  thread stays inside `Application.Start`.
  [`Program.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.MicrosoftHost/Program.cs)
- The designed app's assemblies are preloaded before any XAML is parsed, because `XamlReader`
  resolves `local:` types by scanning loaded assemblies.
  [`HostBootstrap`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoHost/HostBootstrap.cs)
- `IXamlMetadataProvider` is layered: the app's generated provider first, then a generated provider
  from the owning assembly, then reflection. Merged/theme resources come from
  `FrameworkDefaultResources` because unpackaged WinUI cannot construct `XamlControlsResources`.
  [`ReflectionXamlMetadata.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.MicrosoftHost/ReflectionXamlMetadata.cs),
  [`FrameworkDefaultResources.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.MicrosoftHost/FrameworkDefaultResources.cs)
- The app's compiled `.xbf` files are copied next to the child, because the page's own `{x:Bind}`
  cannot be parsed but the app's *controls* can load from compiled XAML.
  [`CompiledXamlMirror.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.MicrosoftHost/CompiledXamlMirror.cs)
- `{x:Bind}` in the designed page is stripped (`CompileTimeBindingStripper`); `{Binding}` is left
  for the runtime parser.
- Known costs, all documented in [`winui-designer.md`](winui-designer.md):
  the `.xbf` mirror lives in the host directory and is shared by every child (two WinUI projects
  designed at once overwrite each other); resolving framework template parts through reflection can
  natively crash the child, so `UIElement` fallback is disabled; and a page whose `session/open`
  crashes natively can enter a restart loop, hence the 3-consecutive-failure circuit breaker
  (a real reproducer spawned 25+ children in under a minute).
  [`UnoDesignClient.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoDesignHost/UnoDesignClient.cs)

### 2.8 Capabilities and gaps

Delivered: open/render, diagnostics, Design/Source split, selection/multi-selection, drag move,
resize, Grid track and Canvas inset editing, wrap/unwrap, reorder, copy/paste/duplicate, Toolbox
built from the runtime catalog, Document Outline, Properties (source-backed), VisualState replay,
theme switch, device preview, context commands, WinUI Runtime Hot Reload through the WinUI XAML
Diagnostics TAP (`DOTNET_STARTUP_HOOKS` + agent).
[`WinUIApplicationHotReloadAdapter.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.AddIn/HotReload/WinUIApplicationHotReloadAdapter.cs)

Gaps, stated plainly in [`designer-common.md`](designer-common.md) and
[`winui-designer.md`](winui-designer.md):

- `{x:Bind}` in the page is not evaluated; code-behind never runs.
- Custom controls whose metadata the provider cannot produce are substituted, not rendered.
- Framework template parts are sometimes dropped rather than resolved (crash avoidance).
- The `.xbf` mirror is not concurrency-safe across projects.
- Abstract-root pages are intentionally unsupported.
- Preview is a bitmap, so compositor effects and animations are absent.

### 2.9 Tests and evidence

| Suite | What it covers |
|---|---|
| [`UnoDesignHostRpcTests.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoHost.Tests/UnoDesignHostRpcTests.cs) (19 tests) | session isolation, stale version, flush, sibling documents, recovery |
| [`XamlPositionMapperTests.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoHost.Tests/XamlPositionMapperTests.cs) (6) | source-position mapping |
| [`WinUIGalleryDesignerTests.cs`](../../tests/OpenDevelop.IntegrationTests/WinUIGalleryDesignerTests.cs) | 16 real Gallery pages, 2 abstract-root pages, backend routing, metadata, VisualState |
| [`WinUIGallerySweep.cs`](../../tests/OpenDevelop.IntegrationTests/WinUIGallerySweep.cs) | the full Gallery catalog, gated on `OD_WINUI_GALLERY_SWEEP=1` |

The Windows Microsoft-host corpus is recorded at 25/25 in
[`winui-designer.md`](winui-designer.md#winui-gallery-integration-corpus-2026-09-12).

### 2.10 Source map

| Concern | File |
|---|---|
| Add-in manifest and dialect ownership | [`WinUIXamlDesigner.addin`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.AddIn/WinUIXamlDesigner.addin), [`WinUIXamlDesignerDisplayBinding.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.AddIn/WinUIXamlDesignerDisplayBinding.cs) |
| View content and gestures | [`WinUIXamlDesignerViewContent.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.AddIn/WinUIXamlDesignerViewContent.cs) (2,470 lines) |
| Source model and undo | [`WinUIXamlDocumentEditor.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.AddIn/WinUIXamlDocumentEditor.cs) (769) |
| Runtime host interface/registry | [`WinUIXamlHost.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.AddIn/WinUIXamlHost.cs) |
| Child RPC surface | [`DesignRpc.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoHost/DesignRpc.cs) |
| Child document/render | [`DesignHost.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoHost/DesignHost.cs) |
| Parent lease/client/recovery | [`UnoDesignClient.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoDesignHost/UnoDesignClient.cs) |
| Microsoft bootstrap | [`MicrosoftHost/Program.cs`](../../src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.MicrosoftHost/Program.cs) |
| DDP wire | [`DesignerProtocol.cs`](../../src/Main/Designer/Designer.Remote/DesignerProtocol.cs) |
| Canvas | [`DesignSurface.cs`](../../src/AddIns/DisplayBindings/DesignerCanvas/DesignSurface.cs), [`DesignSurfaceController.cs`](../../src/AddIns/DisplayBindings/DesignerCanvas/DesignSurfaceController.cs) |
| Shared host lifecycle | [`SharedDesignerHostBroker.cs`](../../src/Main/Designer/Designer.Remote/SharedDesignerHostBroker.cs), [`SharedDesignerHostPool.cs`](../../src/Main/Designer/Designer.Remote/SharedDesignerHostPool.cs) |

---

## 3. WinAppVSCE PR #323 (Microsoft)

### 3.1 Provenance and shape

- Draft PR in `microsoft/WinAppVSCE`, branch `zateutsch-xaml-previewer`. The source is an intentional
  import of the MIT-licensed previewer at
  `nmetulev/winui-vsc@727dd22d02d95097525e0f0fc0cf5dfd533dd153`, applied onto
  `WinAppVSCE@b023f007d85064fdc62bf99ad9c28d2f076680e4`.
  [`docs/previewer-migration.md`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/docs/previewer-migration.md)
- Two deliverables: a `net472` VS extension (`Microsoft.VisualStudio.SDK 17.14.40265`, VS 17.8+)
  and the `surface/` renderer/provisioner (net10 renderer, BCL-only net8 provisioner).
- Product code: extension 14,045 lines (51 files), renderer 15,785 (31), provisioner 1,467.
  Tests are separate projects (unit 2,261, in-VS 1,930, renderer 1,765, provisioner 708).
- The PR is explicitly **not release-ready**; it ships a VSIX artifact for review and asks for VS CI
  lanes to become required checks.

### 3.2 Process boundary and wire

- `Surface.exe` listens on `127.0.0.1`, ephemeral port, announces `SURFACE_PORT=<n>` on stdout,
  accepts exactly one client, then closes the listener. Diagnostics go to stderr.
- Launch authentication: `SURFACE_AUTH_TOKEN` in the child's environment; the first record must be
  a matching `Hello` within 10 s and 64 KB, otherwise the connection is dropped without a reply.
- If nobody connects within 60 s (`SURFACE_CONNECT_TIMEOUT_MS`), the surface exits.
- The wire is **UTF-8 JSON per line**. There is no message length limit, compression, batch
  envelope, request id, document id, sequence number, cancellation, retry or resume. A disconnect
  ends the process. The C# client also puts the child in a kill-on-close job object.
  [`surface/protocol/README.md`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/protocol/README.md)
- Negotiation is one `Hello`/`Ready` exchange with `protocol`, `minProtocol` and string `caps`
  (`frame-stream`, `native-hwnd`). Capabilities are advertisements; dispatch has no capability
  gates.

Client → surface messages (canonical DTOs in
[`Messages.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/vs-extension/WinUIXamlPreview/Protocol/Messages.cs)):

| Type | Purpose |
|---|---|
| `Hello`, `Ping` | handshake / liveness |
| `LoadXaml`, `UpdateXaml`, `Resize` | document + viewport |
| `EnterNative`, `ExitNative` | reparent a real WinUI window / restore offscreen |
| `SetMode`, `SelectByPath`, `PickAt` | design-mode selection |
| `SetProperty` | live property edit (no source write) |
| `SetTheme`, `SetCanvasSize` | theme/size (theme is a compatibility no-op; the VS client relaunches a warm spare) |

Server → client: `Ready`, `Frame` (base64 PNG), `Error`, `Hwnd`, `NativeExited`, `Selected`,
`PropItem`, `ElementProps`, `ContentProps`, `View`. The `Frame` field is PNG; old JPEG comments in
the renderer are explicitly not authoritative.

The absence of `documentId`/request correlation is a known correctness gap: a late frame from a
superseded document can paint over the new one (workstream R1).

### 3.3 Rendering

`RenderHost` stands up two cloaked off-screen WinUI windows at startup: an anchor that keeps the
message loop alive and a reusable render window whose `Content` is swapped per document. All XAML
work runs on the UI dispatcher.
[`RenderHost.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/Surface/RenderHost.cs)

- **Frame mode** (VS Code, and the fallback): render at canvas DIP × scale, `RenderTargetBitmap`,
  PNG, no deltas.
- **Native mode** (VS): the render window is reparented into the tool window. This keeps the real
  compositor, so Acrylic, ThemeShadow/Translation, template animations and visual-state transitions
  are faithful.
- Selection is a `PreviewSurface` overlay inside the surface process: artboard grid, zoom/pan,
  selection chip with type/name and a Properties flyout, hover outline, and click-through walking
  from the deepest authored element upward on repeat clicks.
  [`PreviewSurface.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/Surface/PreviewSurface.cs)

Fidelity as documented:

| Content | Preview |
|---|---|
| In-app `AcrylicBrush`, `ThemeShadow`/`Translation` | Faithful |
| Template animations, indeterminate progress, state transitions | Faithful, animating |
| Window backdrop behind a page with no opaque background | Approximated (`ApplicationPageBackgroundThemeBrush`, no wallpaper tint) |
| Storyboards begun by code-behind | Not started; initial frame only |
| `{x:Bind}`, resources added in `App.xaml.cs` | Placeholders / dropped |

### 3.4 Provisioner (the most transferable piece)

`SurfaceProvisioner` builds a host **matched to the project's Windows App SDK version and PRI-bearing
package closure**, so the preview loads the same managed/native WinUI and the same resources as the
app:

- CLI result contract: exactly one JSON object on stdout (`contractVersion: 1`, `status`, `success`,
  `error`, `cancelled`, `discoveredVersion`, `versionSource`, `hostDir`, `hostExePath`,
  `mergedPriPath`, `cacheKey`, `engineStamp`, hashes, match booleans, timings). Diagnostics go to
  stderr; callers must drain both.
- Statuses: `built` (0), `cached` (0), `degraded` (3, explicit bundled fallback — **not** matched
  success), `failed` (1), `failed`+`cancelled` (130).
- Identity includes renderer/template source and assets, engine stamp, exact WASDK/WinUI component,
  RID/platform, resolved TFM/RID graphs, package identities and **actual hashes of every file in the
  resolved package closure**, plus cache-format version. The target project itself is not in the
  key: projects on the same WASDK + PRI-bearing packages share one host.
- The provisioner never builds or cleans installed SDK directories; it stages under short owned
  temp paths, publishes immutable generations atomically, and hands out per-process disposable run
  copies (`--prepare-run`) with `.surface-run` ownership markers.
  [`Provisioner.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/SurfaceProvisioner/Provisioner.cs),
  [`HostBuilder.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/SurfaceProvisioner/HostBuilder.cs)
- Architecture: the extension ships `Surface\` (x64) and `Surface-arm64\`, picks by OS native
  machine, and asks the provisioner for `--platform ARM64 --rid win-arm64` when appropriate.
  [`HostArch.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/vs-extension/WinUIXamlPreview/Protocol/HostArch.cs)
- `surface/README.md` still says "Only x64/win-x64 is accepted in this milestone"; the P9 commit
  (`5afc0f5`) and `HostArch` contradict that. Treat the README as stale on architecture.
- The matched host is a **sparse package** with a PE/Appx identity so it can resolve package-relative
  resources; registration is current-user and, in the default path, can displace an existing
  registration at a different location (workstream Y1).

Performance figures recorded in the PR branch (useful as a cost model for any compiled-preview
design):

| Path | Before | After |
|---|---|---|
| Open → matched preview | ~65 s | ~12 s |
| Conservative IDE cache hit | — | ~13 s (standalone memo 15.4 s → 1.2–1.9 s) |
| Spare client ready | 6–11 s | 1.3–3.1 s (P2b, on-demand resource-key lookup) |
| Edit → paint, ARM64 native vs x64 emulated | 1618 ms | 543 ms |
| Theme swap, ARM64 vs x64 emulated | 988 ms | 390 ms |

### 3.5 The XAML cleaner

`XamlCleaner` is a namespace-aware `XDocument` transform (attribute order and namespaces preserved)
that turns a compiler-only Page/UserControl/ResourceDictionary into markup `XamlReader.Load` accepts
without changing what it renders:

1. strip `x:Class` from the root;
2. drop `{x:Bind ...}` (property falls back to its default; sample data may replace it);
3. remove compiler-only `x:` directives (`FieldModifier`, `ClassModifier`, `Load`,
   `DeferLoadStrategy`, `DataType`, `Uid`, `Phase`), keeping `x:Name`/`x:Key`/`x:Null`/`x:Static`;
4. remove event-handler attributes by resolving the element type and asking `Type.GetEvent`;
5. strip design namespaces (`mc:Ignorable`, Blend `d:`), first mapping `d:DesignWidth`/
   `d:DesignHeight` and mirroring `d:*` onto real properties;
6. drop `{StaticResource}`/`{ThemeResource}` keys resolvable nowhere (a memoized
   `Resources.ContainsKey` replaced a 3.6–7.4 s startup enumeration);
7. replace unmappable custom types with placeholders (and, for mapped types, keep children
   including property-element content);
8. drop setters whose value was removed so the setter does not invalidate the document.
[`XamlCleaner.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/Surface/XamlCleaner.cs)

It is deliberately defensive: malformed input returns the original text so the real parse error
(with original line/column) reaches the UI and the server stays alive.

### 3.6 VS integration

- Package + package-level session registry + shutdown token; `PreviewPackage.Dispose` disposes every
  live session before the editor tracker, so IDE exit cannot orphan a surface.
  [`PreviewPackage.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/vs-extension/WinUIXamlPreview/Package/PreviewPackage.cs)
- One control hosts both the tool window and the editor margin; the margin follows the active
  document and tracks renames/saves.
  [`XamlPreviewMargin.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/vs-extension/WinUIXamlPreview/Editor/XamlPreviewMargin.cs)
- Toolbar: Reload, Select, Properties, Theme, Size, DTD (design-time data), Fidelity, plus zoom.
  It collapses in stages (title → labels → overflow menu).
  [`PreviewToolbar.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/vs-extension/WinUIXamlPreview/Editor/PreviewToolbar.cs)
- Properties panel reads live values and supports limited edits; edits change the running preview
  only. **No writeback, no undo transaction.**
  [`PropertyPanelControl.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/vs-extension/WinUIXamlPreview/UI/PropertyPanelControl.cs)
- Caret ↔ element sync is implemented against a source map (`SelectByPath`, `ContentProps`,
  `XamlSourceMap`) and is explicitly not the same thing as property writeback.
- Lifecycle hardening: a single idempotent `Fault()` funnel, lifetime CTS, non-blocking dispose,
  bounded restart budget and a reload banner with a fresh budget.
  [`SurfaceClient.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/vs-extension/WinUIXamlPreview/Protocol/SurfaceClient.cs)
- Telemetry: 10 events + exceptions (`docs/previewer-telemetry.md`).

### 3.7 Tests and CI

- `Test-Migration.ps1`: unit, fault-injection, XamlCleaner, identity, provisioner and protocol tests.
- `Test-Foundation.ps1`: provisioner fixtures (129 at P3), two simultaneous real CLI requests,
  cancellation, degraded/failed, run copies, actual user-PRI smoke.
- Renderer smoke: 66 assertions, 73 with `--user-pri` isolation cases.
- In-VS: 63 tests in 11 groups — Load, Margin, Render, Edit, Caret, Toolbar, Errors, Crash, Rebuild,
  Hang, Teardown — run on `windows-latest`, `windows-2022`, `windows-11-arm`.
  [`.github/workflows/previewer-ci.yml`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/.github/workflows/previewer-ci.yml)
- The self-assessment document (WT/WS1–WS8) is
  [`docs/designer-workstreams.md`](https://github.com/microsoft/WinAppVSCE/blob/f70889e21995eb1ecaf3001e368db96ca06ccfd4/docs/designer-workstreams.md)
  at commit `f70889e`; it is not in the PR head tree.

### 3.8 Known gaps, as stated by the authors

| Workstream | Gap |
|---|---|
| WS2 | `{x:Bind}`/code-behind not executed; custom controls still placeholdered; theme requires relaunch |
| WS3 | cache-hit latency, no request coalescing in some paths, no parsed-XAML cache, cache GC |
| WS4 | **Properties → XAML writeback is the #1 missing capability**; caret reverse sync; narrow editors |
| WS5 | no request/document correlation, weak version negotiation, `Ready.wasdk` accuracy |
| WS6 | no clean-machine/runtime matrix, no signing/release ownership |
| WS7 | multi-IDE sparse identity can displace another registration |
| WS8 | toolbar overflow (partly addressed), error-banner unification, frame-mode zoom/pan |

### 3.9 Source map

| Concern | File |
|---|---|
| PR/handoff rationale | [`docs/previewer-migration.md`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/docs/previewer-migration.md) |
| Wire spec | [`surface/protocol/README.md`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/protocol/README.md) |
| Protocol C# DTOs | [`Messages.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/vs-extension/WinUIXamlPreview/Protocol/Messages.cs) |
| Client lifecycle | [`SurfaceClient.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/vs-extension/WinUIXamlPreview/Protocol/SurfaceClient.cs) |
| Renderer | [`RenderHost.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/Surface/RenderHost.cs), [`FrameServer.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/Surface/FrameServer.cs) |
| Selection overlay | [`PreviewSurface.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/Surface/PreviewSurface.cs) |
| Cleaner | [`XamlCleaner.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/Surface/XamlCleaner.cs) |
| Provisioner | [`Program.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/SurfaceProvisioner/Program.cs), [`Provisioner.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/SurfaceProvisioner/Provisioner.cs), [`BuildStorage.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/SurfaceProvisioner/BuildStorage.cs) |
| Payload manifest/run copy | [`HostPayload.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/surface/Shared/HostPayload.cs) |
| VS package/tool windows | [`PreviewPackage.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/vs-extension/WinUIXamlPreview/Package/PreviewPackage.cs), [`PreviewToolWindow.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/vs-extension/WinUIXamlPreview/Package/PreviewToolWindow.cs) |
| Editor margin/toolbar | [`XamlPreviewMargin.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/vs-extension/WinUIXamlPreview/Editor/XamlPreviewMargin.cs), [`PreviewToolbar.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/vs-extension/WinUIXamlPreview/Editor/PreviewToolbar.cs) |
| Properties panel | [`PropertyPanelControl.cs`](https://github.com/microsoft/WinAppVSCE/blob/6e4b3887e7975413dc26f58012a4e3296fbb6c55/vs-extension/WinUIXamlPreview/UI/PropertyPanelControl.cs) |

---

## 4. WinUIDesigner (0x5bfa)

### 4.1 The registration trick

Visual Studio already knows how to be a XAML designer. Its desktop WinUI platform configuration
exists, but deliberately omits `PlatformCreatorAssembly`/`PlatformCreatorType` and
`HostPlatformAssembly`/`HostPlatformType`, so the design tab can appear while no backend is created.
WinUIDesigner fills those gaps at package load:

- reads the existing desktop WinUI `PlatformConfiguration` and replaces only those bindings (plus
  `ToolboxPage`) with its own types;
- validates the private entry points first (including the private field `UwpSceneView.imageHost`);
- installs a `MonoMod.RuntimeDetour` hook on `PlatformService.GetPlatformCreator` because the tested
  VS build bypasses the configured creator;
- restores everything on package dispose.
[`WinUIDesignerPackage.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Vsix/WinUIDesignerPackage.cs)

Consequences: it compiles against **VS PrivateAssemblies** (`DesignerHost`, `RuntimeHost`,
`RuntimeHostBase`, `Markup`, `UtilityBase`, `XamlLanguageServiceBase`, `UwpSurfaceDesigner`) and
requires a 64-bit VS with the XAML designer components.
[`WinUIDesigner.Vsix.csproj`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Vsix/WinUIDesigner.Vsix.csproj)

### 4.2 Surface process

- `WinUISurface.exe`: `net8.0-windows10.0.19041.0` or `net10.0-windows10.0.19041.0` depending on
  the MSBuild version, `Microsoft.WindowsAppSDK 2.5.1`, self-contained, single-file,
  `win-x64` + `win-arm64`.
  [`Directory.Build.props`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/Directory.Build.props),
  [`WinUIDesigner.Surface.csproj`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Surface/WinUIDesigner.Surface.csproj)
- It is launched by VS's host infrastructure with the VS PID, TAP path, pipe init data, DPI context
  and VS install root (so it loads the matching private designer contracts from that installation).
- The document is hosted in a `DesktopWindowXamlSource`; the island's window is reparented beneath
  the VS artboard overlay, which forwards input to the designer tools.
  [`WinUIOverlayWindow.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Vsix/Platform/WinUIOverlayWindow.cs),
  [`WinUISceneView.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Vsix/Platform/WinUISceneView.cs)
- Project assemblies/resources are shadow-copied into a per-document cache by the host platform.
  [`WinUICoreShadowCopyWorker.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Vsix/DesignerHost/WinUICoreShadowCopyWorker.cs)

### 4.3 Protocol reuse and designer operations

It does **not** define a new document protocol. It implements the receiving half of VS's existing
TAP/pipe contract: framed messages with request/reply correlation, data-contract JSON as UTF-16LE,
and runtime object handles resolved by the surface. Message IDs observed in the docs:

| ID | Meaning |
|---|---|
| 516 | `CreateSurface` (document + resource documents) |
| 517 | `CloseDocument` |
| 534 | `RequestConstructionActions` (fallback when XAML load fails) |
| 548 | `SetSurfacePosition` (holder HWND + viewport) |
| 507 | `ExecuteXamlActions` |

The surface implements property queries (including value sources), hit testing, snapping, model
changes (create/set/clear/collections/dictionaries/names/resources), and state/animation preview.
[`SurfaceService.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Surface/Services/SurfaceService.cs),
[`SurfaceService.XamlActions.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Surface/Services/SurfaceService.XamlActions.cs)

Document load has two paths: `XamlReader.Load` of prepared markup, or ordered construction actions
from VS when the markup cannot load. The second path is what associates failures with editing-model
nodes and is the reason the integration is more faithful than a plain `XamlReader` host.
Object identity is stable across rebuilds so undo can reconnect an existing object before close.
[`ObjectIdentityRegistry.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Surface/Common/ObjectIdentityRegistry.cs)

XAML text parsing and serialization are **not** implemented here; they are VS's `Markup.dll` and
XAML language service, which is why design edits write back losslessly with VS undo.

### 4.4 The native diagnostics TAP

`WinUIDesigner.DiagnosticsTap.dll` is a 531-line C++ COM in-proc server loaded into the surface. It
exposes:

- `WinUIDesignerDiagnostics_Initialize`
- `WinUIDesignerDiagnostics_GetPropertySources` (value source per property: default/local/style/
  animation, which public WinUI APIs cannot answer)
- `WinUIDesignerDiagnostics_SetRenderingEnabled` (suspend/resume rendering around action batches)
- the COM class factory entry points
[`DiagnosticsTap.cpp`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.DiagnosticsTap/DiagnosticsTap.cpp)

When diagnostics are unavailable the property service falls back to local values and explicit style
setters, which is a documented fidelity/behavior downgrade.

### 4.5 Toolbox and editing

Because the editing platform is VS's, the extension supplies only the WinUI-specific pieces:

- static Common/All control groups, encoded in the shared XAML toolbox format with WinUI capability
  metadata;
- a Choose Items discovery provider that accepts eligible public, non-abstract controls with a
  public parameterless constructor;
- a separate metadata AppDomain for discovery (not the render process);
- no reliance on VS's automatic population, which recognizes WPF/UWP base types but not
  `Microsoft.UI.Xaml.FrameworkElement`.
[`WinUIDesigner.Vsix/Toolbox`](https://github.com/0x5bfa/WinUIDesigner/tree/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Vsix/Toolbox)

### 4.6 Limits and observed failures

Documented limits:

- C# WinUI projects only; x86 unsupported; `Window` and subclasses cannot be previewed; Windows App
  SDK 2.5.1 required.
- No code-behind execution; design-time data policy is VS's.

Observed in the public issue tracker (all still open as of this writing):

| Issue | Symptom | What it demonstrates |
|---|---|---|
| #1 | 1.2.0 fails on VS2022 17.14: `Microsoft.VisualStudio.Interop 18.0` not found | VS-version coupling; fixed by 1.4.0 (`a76f0f9`) |
| #6 | `Microsoft_VisualStudio_DesignTools_TemplateDocumentId` attached property not found in WinUI `DependencyObject` | VS private design-time markup leaks into the WinUI surface; a version drift breaks document construction |
| #2 | `FindActiveProjectCfgName` catastrophic failure | dependence on VS's project/build internals |
| #3 | `NullReferenceException` in `CreateSurfaceProcessInfo` for multi-targeted Uno projects | project-system coupling beyond WinUI |
| #4/#5 | Uno conditional/`win-`-prefixed XAML namespaces requested | users want Uno support; the surface itself is WinUI-only |

Scale and maintenance signal: 65 `.cs` files / 9,017 lines (VSIX 2,335, Surface 6,299, Shared 383,
TAP 531), 462 lines of tests, one author, 86 commits since 2026-09-27.

### 4.7 Source map

| Concern | File |
|---|---|
| How it bends VS | [`WinUIDesignerPackage.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Vsix/WinUIDesignerPackage.cs) |
| Platform/host bindings | [`WinUIPlatformCreator.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Vsix/Platform/WinUIPlatformCreator.cs), [`WinUIHostPlatform.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Vsix/DesignerHost/WinUIHostPlatform.cs) |
| Scene/view embedding | [`WinUISceneView.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Vsix/Platform/WinUISceneView.cs), [`WinUIHwndHost.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Vsix/Platform/WinUIHwndHost.cs) |
| Surface startup | [`Program.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Surface/Program.cs) |
| XAML island | [`DesignerSurface.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Surface/DesignerSurface.cs) |
| Pipe bridge/frames | [`SurfacePipeDataBridge.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Surface/Transport/SurfacePipeDataBridge.cs), [`MessageFrameReader.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Surface/Transport/MessageFrameReader.cs) |
| Operations | [`SurfaceService.HitTest.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Surface/Services/SurfaceService.HitTest.cs), [`SurfaceService.Properties.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Surface/Services/SurfaceService.Properties.cs), [`SurfaceService.SnapLines.cs`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.Surface/Services/SurfaceService.SnapLines.cs) |
| Native diagnostics | [`DiagnosticsTap.cpp`](https://github.com/0x5bfa/WinUIDesigner/blob/456640e04fd2ae0f9e738c71503889844cf4060b/src/WinUIDesigner.DiagnosticsTap/DiagnosticsTap.cpp) |

---

## 5. Comparison

### 5.1 Architecture

| Dimension | XAML Studio | OpenDevelop | WinAppVSCE #323 | WinUIDesigner |
|---|---|---|---|---|
| Who owns the editable document | nobody (source text + live objects) | OpenDevelop (parent `XDocument`) | nobody yet | Visual Studio (`Markup.dll`) |
| Who owns the live objects | the XAML Studio process (in process) | child process | child process | child process |
| Transport | none (direct calls) | StreamJsonRpc/loopback TCP, DDP v70, document ids, versions, token | line JSON/TCP v1, no ids, token | VS private TAP/pipe, request ids, object handles |
| Render transport | live `UIElement`; no frame | PNG / deflated BGRA frame + tree | native HWND or PNG frame | native XAML island |
| Hit testing | live visual tree + adorner | client-side from snapshot | in surface process, selection messages | VS tools + live surface answers |
| Multi-document per process | yes (one app, many documents) | yes, registry + leases | one surface per client (session) | VS host manages |
| Crash isolation | none | full (bitmap + recovery) | full (process + restart budget) | full (surface process) |
| Multi-OS | no (UWP) | yes (Uno) | no | no |

### 5.2 Capability

| Capability | XAML Studio | OpenDevelop | WinAppVSCE #323 | WinUIDesigner |
|---|---|---|---|---|
| Live preview | yes (UWP/WinUI 2, in process) | yes (bitmap, no animations) | yes (native, animations faithful) | yes (native island) |
| Selection / hover / outline | yes (adorner + visual tree/state explorer) | yes | yes | yes |
| Move / resize / snap | no | yes | no | yes |
| Toolbox insertion | text only (appends a tag) | yes | no | yes |
| Structural edits (wrap/reorder/copy/paste) | no | yes | no | yes (VS) |
| Properties read | yes | yes | yes | yes |
| Properties writeback | partial (one attribute, string patch) | yes | **no** | yes |
| Undo/redo of design edits | no (Monaco text undo only) | yes (parent) | no | yes (VS) |
| Visual states / theme | yes (VisualStateManager + theme toggle) | yes | theme via relaunch; states not exposed | yes |
| `{x:Bind}` / code-behind | no `{x:Bind}`; no code-behind | no (stripped) | no (placeholders) | no user code; construction actions |
| Custom controls | only types the app can load/resolve | placeholder fallback | matched-host types + placeholder fallback | load where metadata permits |
| Toolbar/zoom/fit/device chrome | refresh/grid/clip/theme | yes | yes (zoom/pan in surface) | yes (VS) |
| Hot reload (app running) | live edit only; no app hot reload | yes (WinUI XAML Diagnostics TAP) | no | VS's own only; not supplied by this extension |

### 5.3 Engineering and risk

| Dimension | XAML Studio | OpenDevelop | WinAppVSCE #323 | WinUIDesigner |
|---|---|---|---|---|
| Product code | ~11.5k lines (7.9k app + 3.6k toolkit) | ~28k lines (16.7k WinUI + 11.7k shared) | ~31k lines | ~9k lines |
| Tests | ~1.2k toolkit unit tests; no integration | host RPC + journeys + Gallery corpus | 458 unit + 63 in-VS × 3 runners + fixtures | 462 lines |
| CI | GitHub Actions (build/tests) | in-repo lanes | 3 Windows runners, PR gate | GitHub Actions (build only) |
| Governance | .NET Foundation seed project; fork single-maintainer | one vendor, long-term product | Microsoft team, draft PR | one author, 6 weeks, 6 open issues |
| Update risk | **UWP/WinUI 2 dead end; in-process** | WinUI/Uno semantics; 5 backends | VS/WASDK packaging; no document model | **VS private contracts; highest** |
| Distribution | Microsoft Store v1.1 / source | OpenDevelop only | VS (if merged) + VS Code renderer | Marketplace, VS only |

### 5.4 The decisive difference

A designer is not a renderer. The capability boundary is exactly the document model:

- **XAML Studio** is the control experiment: it has a real runtime, selection, a property panel, a
  visual tree and visual states — and still cannot edit a document. Its renderer is a text pipeline
  whose output is a live object, not a document, and it cannot isolate a host or load a project.
- **WinAppVSCE** can show the truest pixels of the three Windows hosts, but it cannot write a
  property back into the file, cannot undo, and cannot add a control. Those are not missing buttons;
  they are missing state. Adding them means adopting a document model (VS's, or its own), which turns
  it into one of the other architectures.
- **WinUIDesigner** gets the model for free, but the model belongs to Visual Studio. Its ceiling is
  whatever VS's designer does, and its floor is whatever VS's private internals do next release.
  This is a product decision, not an engineering defect: it is why 9k lines buy a full designer, and
  why the designer breaks when VS changes a private field.
- **OpenDevelop** pays for the model itself and therefore owns the ceiling. Its current weak point is
  the opposite one: it owns the *renderer* too, and WinUI's compile-time semantics (`x:Bind`,
  generated metadata, PRI, template parts) are not something a runtime parser can fully reproduce.

---

## 6. Future potential

### 6.1 XAML Studio

It is a finished product with a clear, narrow purpose, and its potential is at that scale: a good
place to try a control or a template, not a platform. The app is UWP/WinUI 2, the fork has not moved
it to WinUI 3, and upstream is quiet; even if it were revived, a WinUI 3 port would replace the
loader, the tree coordinator and the result model. Its durable value is the **ideas**: text
tolerance, binding interception, and the discipline of separating a renderer from the host.
OpenDevelop already mined those (the ProGPU port), and should keep using it as a reference, not as
code to resurrect.

### 6.2 WinAppVSCE

The PR alone suggests a previewer: it cannot write back, undo or add a control, because that needs
a document model (WS4-I1, WS5-R1 keep deferring it). The **issue trackers say the previewer is the
first step of a designer roadmap**, and they contradict an earlier reading of this note (that
Microsoft would converge on WinUIDesigner by reusing VS's private designer infrastructure):

| Issue | Date | What it commits to |
|---|---|---|
| `WinAppVSCE#54` Visualizer, `#56` XAML Designer, `#59` Live Visual Tree | 2026-07-14 | One author's roadmap (C3 → C4 → C6) on a shared **B4 design-time engine**: render frames, hit testing, bounds, property metadata |
| `WinAppVSCE#56` | 2026-07-14 | The designer is **text-authoritative**: gestures become minimal, formatting-preserving XAML text edits through a whitespace/comment/attribute-order preserving CST, undo on the editor's text stack, no designer metadata in the file. Round-trip correctness is named "the top engineering risk" and the effort "multi-quarter" |
| `WinAppVSCE#61` | 2026-07-15 | The VS extension is a **client of the same engine and protocol**, "only the host shell should be VS-specific"; non-goals include "a separate protocol just for Visual Studio" and rebuilding VS's XAML language service. Path: experimental VSIX, then possibly inboxed |
| `winappCli#636` (closed) | 2026-07-15 | `winapp run --devtools`: a versioned, editor-agnostic runtime protocol (tree, properties, select/highlight, edit, change notifications), "the foundation for the broader design-time engine" |

Reading: Microsoft's stated direction is a **shared engine plus thin editor clients, with the
document owned by the editor side and text-authoritative** - the same shape as OpenDevelop's
(parent source model, child renderer, bitmap frames, hit-test metadata), not VS's private designer.
Bitmap-per-change first with a protocol that allows streaming later (`#54`) is also OpenDevelop's
DDP shape, and a runtime design mode (Uno Hot Design style) is listed as a later option on the same
protocol (`#56`, `#59`).

Consequences: (1) "we own a document model, they do not" is a **window, not a moat**: they know the
gap (`#56`) and have it on the roadmap; (2) the protocol is intentionally not VS-specific, so
integrating with it is a realistic option rather than a one-off; (3) the best leading indicators are
whether the unnamed B4b/B4c engine appears (it is referenced as a hard dependency but no separate
public issue or implementation was found; the PR's `surface/` may be its first form), whether
WS4-I1 (writeback) starts, and whether the `winappCli` protocol is versioned and documented. These
are inferences from what the authors wrote publicly, not knowledge of Microsoft's plans.

### 6.3 WinUIDesigner

Highest capability per line, lowest survivability. It is a one-person extension that depends on
private assemblies, a private field, and a process-wide detour inside `devenv.exe`; the public issue
list already contains a VS-version break and a private design-time markup failure. It will remain a
useful niche tool and a very good reference implementation (its documentation of VS's designer flow
is the best available), but it cannot be a platform, and it cannot leave Visual Studio.

### 6.4 OpenDevelop

The only approach that can be simultaneously faithful, editable, cross-platform and independently
governed — if it closes the WinUI compile-time gap. The code and docs already point at the right
answer: a **project-specific, compiled preview application** built with the real WinUI toolchain
(generated metadata, real PRI, real XamlControlsResources), loaded out of process, with the existing
source model and mutation protocol on top. That is the same idea as WinAppVSCE's provisioner, which
is strong evidence that the direction is correct and gives a cost model (seconds of cache-hit and
tens of MB of payload) for planning.

### 6.5 Scenario table

| Scenario | Winner | Why |
|---|---|---|
| Microsoft merges the PR and ships it in VS/VS Code | WinAppVSCE | Distribution and first-party trust; preview becomes table stakes |
| A VS update changes designer internals | OpenDevelop | WinUIDesigner breaks, WinAppVSCE is unaffected but still preview-only |
| Users need to drag, resize, insert and save | WinUIDesigner or OpenDevelop | WinAppVSCE cannot; XAML Studio cannot either |
| Users need macOS/Linux/Uno | OpenDevelop | The other three cannot |
| A team needs a designer they can test in CI without a UI | OpenDevelop | Snapshot + DevFlow actions are programmable |
| A team wants the smallest possible maintenance bill on Windows | WinAppVSCE (if it ships) | No private contracts, Microsoft-owned |
| A team wants a quick loose-XAML playground, not a project designer | XAML Studio | That is exactly what it is, and it is MIT |

---

## 7. Recommendations for OpenDevelop

### 7.0 Evaluate consuming WinAppVSCE's surface before building our own provisioner

§6.2 shows the shared protocol is intentionally editor-agnostic, so the cheaper way to get §7.1 may
be to consume the upstream `Surface.exe` + `SurfaceProvisioner` as one more Microsoft WinUI host
backend behind DDP, instead of writing a second provisioner. The decision belongs to a short
feasibility spike (§7.8); §7.1 is what we build if the spike fails or upstream stalls.

### 7.1 Make the compiled preview application a product capability

Treat the provisioned host (ours or upstream's) as the primary Microsoft WinUI load path, not an
experiment:

- key on TFM, RID, Windows App SDK version, WinUI component, evaluated package closure and PRI
  inputs, exactly as `surface/README.md` describes;
- build with the real XAML compiler/PRI toolchain so `{x:Bind}`, generated metadata and package
  resources are authentic;
- keep the parent source model, mutations, selection and undo unchanged — the compiled host is only a
  better renderer, not a new editing model;
- return a structured result (`built|cached|degraded|failed`) and never silently substitute a host.

Acceptance evidence: a Gallery page using `{x:Bind}` renders its real values (not placeholders), and
two different WASDK versions in one solution open concurrently without cross-contamination. (Measured: the first half is
not reachable with the upstream surface, which never runs generated binding code; see the `{x:Bind}` table in section 7.8.
It needs our own compiled-page route. The second half still applies to either.)

### 7.2 Do not replace the snapshot canvas with native embedding

The bitmap snapshot is why OpenDevelop can support five runtimes, recover from a native crash, and
drive tests over DevFlow. Native embedding buys animations and compositor effects at the cost of
Win32 airspace, DPI/input forwarding and Windows-only scope.

Instead, add a per-document **native-live capability** on Windows, advertised through the existing
protocol, so the same document can opt into a reparented child window while the rest of the shell
continues to use snapshots. WinAppVSCE has already proven the reparenting mechanics; OpenDevelop's
`EnterNative`-equivalent does not exist yet, but the capability negotiation does.

Acceptance evidence: the same document can switch between snapshot and native modes without losing
selection, and a native-mode crash falls back to the last snapshot with the recovery banner.

**Built (renderer side, 2026-11).** `SurfaceConnection.EnterNativeLiveAsync/ExitNativeLiveAsync`,
`SurfaceDesignDocument.EnterNativeLiveAsync/ExitNativeLiveAsync` and the child RPC `design/native-live`
(`enabled`, current request on leaving) switch one document between snapshots and the surface's own
window. The reply carries the window handle and size when native; otherwise the snapshot to show and a
message. While native, the surface sends no frames, so `session/update` is refused with a reason,
hit-tests miss (the window takes the clicks) and picks are off; leaving re-renders the current source.
A lost surface returns the last accepted snapshot and "showing the last snapshot" instead of an
exception. It is a renderer capability, not part of the shared designer protocol, so a shell that never
calls it never leaves snapshots. Tested on the fake surface (4 tests, including a dropped connection
and a refused `EnterNative`) and on the real 2.2.0 surface over DDP (the handle is a live window,
update refused, back to a frame). **Not built:** the shell side, i.e. reparenting that HWND into the
LibreWPF canvas, DPI/input forwarding, and the UI to toggle it; that cannot be verified without
a visible IDE and is the remaining work.

### 7.3 Treat WinUI source round-trip as the moat

WinAppVSCE cannot do writeback because it has no model; WinUIDesigner's model is borrowed. OpenDevelop
owns its model, so prove it:

- extend the `xaml-loader-comparison.md` method to the WinUI dialect: a corpus of pages with
  resources, templates, attached properties, property elements, comments, prefixes, `mc:Ignorable`,
  design-time attributes and `x:Bind`;
- assert parse → save with no edit is byte-identical (modulo declared exceptions), and that a
  single-property edit changes only the expected bytes;
- publish the known-difference list and make it shrink-only, exactly as the WPF corpus does.

Acceptance evidence: an unedited Gallery page saves back unchanged, and the difference list is
enforced in both directions.

**Results (2026-11, `tests/OpenDevelop.WinUIXamlDocument.Tests`).** The editor class is source-linked
into a pure-managed test project. Synthetic cases (42 tests) and the baseline
`real-files-baseline.txt` (every `tests/fixtures` `.xaml`, checked in both directions;
`OD_UPDATE_BASELINE=1` rewrites it on purpose) measure an edit-free parse and save. The editor holds an
`XDocument` and re-serializes it wholesale, so the answer is: the model is faithful, the layout is not.
Preserved: text, comments, entities (`&gt;`), `<a></a>`, trailing newline. Lost: attribute line breaks
and multi-space separation (joined by single spaces), `<a/>` spacing, single quotes, the XML
declaration, and LF becomes the platform line ending (a separate flag in the analysis).

One-off survey of every `.xaml` in the checkout (build copies excluded):

| Set | Files | Identical | Attribute whitespace | Empty-element form | Other | Not loadable |
|---|---|---|---|---|---|---|
| all | 1499 | 512 (34%) | 503 | 257 | 223 | 4 |
| WinUI-relevant (xamlstudio + fixtures) | 60 | 9 (15%) | 40 | 0 | 11 | 0 |
| `src/` WPF | 944 | 493 (52%) | 235 | 104 | 109 | 3 |

"Other" is mostly a layout difference combined with a second one (length differs, 192 of 223) and a
minority of character references (30). So the first edit of a typical WinUI file rewrites its layout
(23 of 24 fixtures lose attribute whitespace). The fix is to patch the text instead of re-serializing
the document. **Done afterwards:** `WinUIXamlDocumentEditor.PreserveLayout` runs after every edit and puts
back, from the text before the edit, the declaration, the line ending and the exact start tag of every
element whose name, attributes and values are unchanged. An edit now changes the edited element's own
start tag (laid out by the serializer) and nothing else; `EditPreservationTests` covers attribute edit,
insert, undo, and LF/CRLF sources. Remaining loss: the edited tag itself is reflowed, and two
identical start tags may swap their layout.

### 7.4 Close the two protocol gaps that matter

DDP v70 already has document ids, versions, stale rejection and authentication. Add:

- per-request correlation so a late frame from a superseded generation cannot paint (the exact gap
  WinAppVSCE lists as R1);
- explicit feature negotiation rather than inferring support from a protocol number alone (its
  `caps` array is advertised but not enforced; OpenDevelop's optional RPCs are version-gated).

### 7.5 Keep the differentiation explicit

Microsoft will not build a macOS/Linux WinUI designer and will not design Uno. OpenDevelop's window is
"cross-platform + Uno + real editing + programmable tests". Do not compete on "most faithful Windows
pixels"; compete on "the only designer that works where your project does, and that you can drive
from CI".

### 7.6 Keep XAML Studio as a reference, not a dependency

The ProGPU port proved both the value and the limit of linking XAML Studio: it produced a working
renderer quickly, then could not survive a runtime change and had to be deleted. Do not re-link it
for WinUI 3. Keep the *ideas* — namespace injection, binding interception, the app-controls vs
page-source distinction, error-position mapping — and, if a loose-XAML preview is ever wanted, write
it against the project runtime behind the existing host interface rather than against UWP
`XamlReader`.

### 7.7 How to import it: a lextudio fork published as packages, not a submodule

What would be imported is a **process** (`Surface.exe`, `SurfaceProvisioner`) that DDP talks to, not
a library. The repository already has the convention for that, and the failed alternative:

- **Fork `microsoft/WinAppVSCE`** at PR #323 (`6e4b3887e7`), keep only `surface/` (Surface,
  SurfaceProvisioner, Shared); drop the VSIX and the VS Code client.
- **The fork's workflow publishes packages** (x64 and arm64 slices in per-RID folders, matching the
  PR's own `Surface\` / `Surface-arm64\` layout) to lextudio GitHub Packages. OpenDevelop pins the
  version in `Directory.Packages.props` and syncs it through `sync-openavalon-feed.ps1`, like the
  AvalonEdit/AvalonDock forks (SharpDbg, the other precedent, was a submodule and moved to packages).
- **The DDP adapter is our own code**, a host next to `UnoDesignRuntimeHost` mapping `LoadXaml` /
  `UpdateXaml` onto `session/open|update` and frames/selection onto the snapshot canvas. Do not
  reuse the PR's net472 client; it lives in the VSIX project.
- **Carry few patches and send them upstream first.** The one we need most is document/request
  correlation (their R1/WS5 gap). The fork keeps only what upstream does not take.
- **Do not use a submodule with linked `.cs` files.** That is exactly how `XamlStudio.Toolkit.ProGPU`
  was built; it died with an upstream runtime change (§1.3, 2,040 lines deleted). It also gives
  nothing here, because we never compile their sources.

Things to confirm before forking: the repository's own LICENSE (the PR says the code was imported
from MIT `nmetulev/winui-vsc`; the WinAppVSCE licence itself was not checked), whether the surface
can be launched without VS or VS Code (it expects a client that passes `SURFACE_AUTH_TOKEN`), that
sparse-package registration does not displace a user's installed WinApp extension (their Y1), and
that the provisioner uses only the SDK selected in **Tools > Options > .NET SDK**, per the
repository's two-SDK-roles rule.

### 7.8 What switching would give, and what it would not

Gains, all in the Microsoft WinUI backend on Windows:

1. Generated metadata and PRI are authentic instead of stripped or approximated, and custom controls stop being
   placeholders. **`{x:Bind}` is not in this list:** the surface never runs the project's generated binding code, so it stays a
   placeholder or is dropped (measured in the spike results above).
2. Removes two known defects: the `.xbf` mirror collision between concurrently opened WinUI
   projects, and the reflection-on-template-parts native crash that forced the 3-failure circuit
   breaker.
3. Real compositor effects (Acrylic, ThemeShadow, template animations) in native mode.
4. Saves a self-built provisioner (about 1.5k lines of cache identity, atomic publishing,
   cancellation, degraded status and sparse-package handling) and its recorded optimisations
   (open to matched preview about 65 s to 12 s, cache hit about 1-2 s), plus native ARM64.
5. Its unit, fault-injection, renderer and in-VS tests can run in our CI against the fork.

Not gained: nothing for Uno, macOS or Linux; the document model, undo and writeback stay ours; and
animations need the native-embedding work of §7.2, because the frame mode is PNG without them.

Costs: a fork and publishing pipeline, our adapter, a thin v1 protocol without document ids,
unmerged draft upstream, heavier runtime requirements (build a host per project), and identity
conflicts. The boundary stays small: the surface is one backend behind DDP, so it can be replaced
by our own provisioner without touching the document model or the Uno path.

**Feasibility spike (about one day), before forking:** build the PR head, launch `Surface.exe`
without VS from a Windows machine, send one `LoadXaml` from DevFlow, and confirm a frame comes back.
If that fails, build §7.1 ourselves.

**Spike result (2026-11-12, Windows 11 x64 VM, PR head `6e4b388`): passed.** Measured, not inferred:

| Check | Result |
|---|---|
| `dotnet build surface/Surface/Surface.csproj -p:Platform=x64 -p:RuntimeIdentifier=win-x64` | 0 errors, ~78 s including restore |
| `Surface.exe` launched with no VS/VS Code, only `SURFACE_AUTH_TOKEN` | port announced in 2.2-2.7 s; `Hello` -> `Ready` (protocol 1, caps `frame-stream,native-hwnd`) |
| `LoadXaml` (bundled WASDK 2.2.0 host) | PNG `Frame` in ~1.3 s; real Button/ToggleSwitch/Slider with correct theming |
| `SurfaceProvisioner --build` on `tests/fixtures/WinUIDesignerFixture/Microsoft/WinUISample.csproj` (WASDK 2.4.0) | `status: built`, `matchedHost: true`, managed and native hashes matched, WinUI component 2.3.6; cold 212 s (surface build 40 s, PRI build 46 s) |
| Same request again | `status: cached`, `cacheHit: true`, 2.5 s; cache entry 0.16 GB |
| `LoadXaml` of the fixture's `MainPage.xaml` (has `x:Class`) on the matched host | `Ready.wasdk` = 2.4.0; `Frame` in 0.7 s with the page's own background and button |

Things the run showed that the PR documents do not say:

- **The provisioner discovers the WASDK version from the project, so it needs a `PackageReference`.**
  `tests/fixtures/WinUIDesignerFixture/MicrosoftHost/MicrosoftWinUISample.csproj` deliberately has none (so it restores
  with no feed) and fails with `Could not discover a WindowsAppSDK version` (`versionSource: 3`). Use `Microsoft/WinUISample.csproj`.
- **The provisioner targets net8 and fails to start on a machine with only .NET 10+ (exit 150).** `DOTNET_ROLL_FORWARD=Major`
  worked for the spike; a fork should retarget it or ship the runtime.
- **Launching a matched host is two steps:** copy the cache entry's `host` folder to a per-process run directory, then start
  `Surface.exe --user-pri <run>\Surface.designtime.pri` (`SurfaceClient.cs:186-206`). The cache entry itself is pristine and
  must not be launched against. The run copy took ~1.4 s here (`--prepare-run` does this for callers that use the CLI).
- **Path length is a hard failure.** Started from a deep scratch path, `Surface.exe` died with `DllNotFoundException:
  Microsoft.WindowsAppRuntime.Bootstrap.dll ... filename too long (0x800700CE)`. Everything worked from `C:\wxp\...`.
  The adapter must place the cache, staging root and run directories under a short root.
- **No pre-existing .NET 10 pin.** The PR repository has no `global.json`, so it built with the newest SDK installed (11.0 RC).

Answered from source and from the run:

- **Sparse-package identity (their Y1) does not apply to a `surface/`-only consumer.** The registration code is in the VSIX
  project (`vs-extension/WinUIXamlPreview/Protocol/SurfaceIdentity.cs`, `SurfaceIdentity/Register-Experimental.ps1`); nothing
  under `surface/Surface` or `surface/SurfaceProvisioner` registers a package, and the spike ran unpackaged with no identity.
  Our adapter registers nothing unless it chooses to.
- **SDK selection:** `HostBuilder.cs:86` runs a bare `dotnet` from PATH. The repository's two-SDK-roles rule therefore has to
  be enforced by our adapter (set `PATH`/`DOTNET_ROOT` for the provisioner child to the SDK chosen in Tools > Options > .NET SDK),
  not assumed.

Fork preparation was rehearsed locally (nothing pushed): a branch with only `surface/`, a `global.json` pinning .NET 10, and
`SurfaceProvisioner`/its tests retargeted `net8.0` -> `net10.0` builds and runs natively with no roll-forward, and reuses the
existing cache entry. `surface/protocol/Compatibility.csproj` links sources from the removed `vs-extension` and needs its own
fix in the fork.

The first piece of the §7.7 adapter exists as `src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.SurfaceHost`
(framework-neutral; the DDP `IWinUIXamlRuntimeHost` on top of it is not written). It holds the wire connection (handshake,
`frame-stream` capability check, one request in flight, and any timeout or cancellation faults the connection so a late frame
cannot answer the next request - the R1 gap handled client-side), the provisioner result parser (`degraded`, unknown or
inconsistent statuses are never "matched") and the launch rules (port line, short-root guard, per-process run copy,
`--user-pri`). On top of those, `SurfaceDesignDocument` expresses one surface as the shared DDP shapes: `session/open|update`
become a `DesignerSessionState` with the PNG frame (`Dpi` = pixels / DIPs), a stale `Version` is refused without touching the
surface, a render error is a rejected state with a positioned diagnostic while the last good frame stays, and edit RPCs are
refused as ordinary results (the surface has no document model, so the parent's source model stays the authority).
The surface reports no element tree, so the document builds one (`BuildTree`, on by default): see "Element tree with bounds"
below. The Document Outline works from the parent's own source model either way. Its tests (`WinUIXamlDesigner.SurfaceHost.Tests`) use an in-process fake surface
(39 pass) and one test that drives the real `Surface.exe` when `OD_SURFACE_EXE`/`OD_SURFACE_ARGS` are set; it passed against
the matched 2.4.0 host.

The child executable now exists: `WinUIXamlDesigner.SurfaceHost.Child` (plain `dotnet build`, no WinUI or VS MSBuild). It
runs through the shared `DesignerChildHost` (`--port`/`--token`), takes `--surface-exe` and optional `--surface-args`, answers
the same 19-method DDP map as the other WinUI children, launches one `Surface.exe` per document (`SurfaceProcessHost`, kill
on dispose) and kills them on shutdown or parent loss. Edits, `design/theme` and `design/go-to-state` are refused as ordinary
results; `design/hit-test` never hits; `app/resources` reports success (the matched host merges the project's resources
itself). `SurfaceChildProcessTests` plays the IDE side over real JSON-RPC: token and protocol checks, handshake, a failed
launch that leaves the child alive, clean exit 0 on `shutdown`, and, with a real surface, open, update, refused edit, PNG
export, a positioned error and close. 43 of 43 passed three runs in a row against the matched 2.4.0 host. A graceful
`shutdown` used to race its own reply (the child could exit first); the child now releases `Main` 200 ms after replying.

**The package and the IDE wiring (2026-11-12).** The fork is `lextudio/WinAppVSCE`, branch `opendevelop` (the precedent fork's
branch name): `surface/` only, `global.json` pinning .NET 10, the provisioner on `net10.0`, `surface/Package` and
`.github/workflows/release-surface.yml`, which builds both architectures with `Build-Payload.ps1`, smoke-tests the x64 surface
(`surface/Smoke-Surface.ps1`: Hello/Ready and one PNG frame, no IDE), packs and, on a manual dispatch with `publish`, replaces
and pushes the package to GitHub Packages. **`LeXtudio.WinUISurface` 0.1.0 is published** (32 MB: `HostSDK/` plus the bundled
surface for `win-x64` and `win-arm64`). Unpacked on its own, its provisioner built a matched 2.4.0 host for
`tests/fixtures/WinUIDesignerFixture/Microsoft`. Things the runs taught:

- The surface needs the **Windows App Runtime installed** (Main, Singleton and DDLM packages, not only the framework package);
  without it it exits `0x80670016`. Hosted runners lack it, and the upstream CI installs it with
  `windowsappruntimeinstall-x64.exe --quiet --force`; so does the workflow, and the fork README lists it as a prerequisite.
  Any machine that opens a Surface-backed designer needs it too.
- A package of subfolders must not put `%(RecursiveDir)` in `PackagePath` (NuGet appends the folder a second time).
- `dotnet nuget push "dir/*.nupkg"` is not glob-expanded on Windows; GitHub Packages 404s a package that does not exist yet.

OpenDevelop pins `LeXtudio.WinUISurface` in `Directory.Packages.props`, maps it under both feeds in `NuGet.config` and in
`sync-openavalon-feed.ps1`'s fork list (so a local build needs no token), and `WinUIXamlDesigner.SurfaceHost.Child` references it
(`ExcludeAssets=all`, `GeneratePathProperty`) and copies it into `SurfacePayload/` beside the child, which `DeployToAddIns`
puts in the add-in's `SurfaceHost/` folder; the add-in project references the child so it builds with the add-in.

**`OD_WINUI_RUNTIME=surface`** is wired and opt-in (nothing changes for the other values or when unset):

- `RegisterDevFlowActionsCommand` registers `SurfaceDesignRuntimeHostBootstrap` last when the variable is `surface`; the
  Microsoft and Uno factories stay registered behind it. The bootstrap declines non-Windows-App-SDK projects and any machine
  where `SurfaceHost/` is not deployed.
- `UnoDesignClient.AcquireSharedAsync` gained an optional `extraArguments` (appended to the child command line and part of
  `CompatibilityKey`, so documents needing different arguments never share a child); `UnoDesignRuntimeHost` gained an optional
  `childLaunchProvider`. When it is set the child is launched on its own runtime (never inside the designed app's runtimeconfig
  and deps, which would pin it to the app's framework and a graph without the child's assemblies) and its status text carries
  the provider's reason when it cannot start.
- The provider (`SurfaceDesignRuntimeHostBootstrap.PrepareAsync`) finds the project, picks a short root (`OD_SURFACE_ROOT`, else
  `%LOCALAPPDATA%\OpenDevelop\surf`; a root over 120 characters is refused with the reason), runs the provisioner with the SDK
  from `DotNetSdkService` on its PATH, logs to the "WinUI Designer" output channel, and accepts only a verified matched host
  (`built` or `cached`): `degraded`, `failed` or anything else is reported to the user and **no surface is started**, so a bundled
  host is never silently substituted. It passes `--host-dir`, `--run-root` and `--surface-args "--user-dll <project output>"`.
- The child makes a private run copy of the matched host per document (a surface rewrites its own `Surface.pri` at start) and
  removes it on close, after the process has exited.

Verified by tests (48 of 48 pass with every gated test enabled): the provisioner runner against the real unpacked package; the
child's per-document run copies; and **the real IDE-side `UnoDesignClient`** (its standalone build) launching the real child with
`extraArguments`, handshaking as `WinUI.Surface` and rendering a page through the matched-host surface.

**Compiled, but not run in an IDE.** The add-in chain builds with no errors (`dotnet build` of
`ICSharpCode.WinUIXamlDesigner.UnoDesignHost.csproj` with `-p:SkipWinUIHotReloadTap=true`, which is the documented way to skip the
native Hot Reload TAP when Visual Studio's C++ tools are absent; the `externals/winappvsce` submodule had to be initialised and
its `WinUiXaml.LanguageServer` built first). That covers `UnoDesignRuntimeHost`, `UnoDesignClient`, the bootstrap, the registration
command and the child project's `DeployToAddIns`: the add-in's `SurfaceHost/` folder then holds the child and the 116 MB payload,
the host assembly contains `SurfaceDesignRuntimeHostBootstrap`, and the registration command carries its type name and the
`surface` selection.

**Run in a live IDE (2026-11-12, this VM, no Visual Studio needed).** The shell builds with `dotnet build
src/Main/SharpDevelop/SharpDevelop.csproj -p:DisableGitVersionTask=true -p:ProGpuWpfUseCurrentRuntimeIdentifier=false
-p:GitVersion_*=...` (`build.ps1 shell` mangled `-p:DisableGitVersionTask=true` into `- p : D i s a b l e ...` and failed with
MSB1001, a bug in the script's argument handling on this machine). The IDE needs two more add-ins to open a project, which
the partial tree did not have: `TextTemplating` (a declared dependency of C# Binding; without it C# Binding is skipped silently and
the solution load fails with "No backend for project type installed") and `CSharpBinding`. With
`OD_TEST_MODE=1 OD_WINUI_RUNTIME=surface dotnet run --project src/Main/SharpDevelop/SharpDevelop.csproj -f net10.0-windows
--no-build`, DevFlow on port 9299, `od.open-solution` for `tests/fixtures/WinUIDesignerFixture/Microsoft/WinUISample.slnx` and
`od.open-file` for `MainPage.xaml`:

- `od.winui-designer.status`: `backend: WinUI`, `rendered: true`, status `Rendered by WinUI Surface host (1024×720)`.
- The "WinUI Designer" output channel shows the whole chain: the project build, the provisioner (cold: `Built, Windows App SDK
  2.4.0, 69.7s`; warm: `Cached`, 18.4 s including the project build), the child starting a surface from a private run copy with
  `--user-pri` and `--user-dll <WinUISample.exe>`, and `surface ready (WASDK 2.4.0)`.
- **Document Outline works** (`Page, Grid, RootStack, TitleText, PrimaryButton`): it comes from the parent's source model.
- Edit round trip: `od.winui-designer.select PrimaryButton`, then `properties-pad.edit Content` changed `Hello WinUI` to
  `Edited in the IDE` with the designer still rendered, dirty and `canUndo: true`; `od.winui-designer.undo` restored it
  (`canUndo: false`, `canRedo: true`).
- **Pixel check of the edit** (`od.winui-designer.export-png` of the designer frame, three exports, compared byte for byte):
  `Hello WinUI` gave `4BE6315BB585` (7,356 bytes), which is byte-identical to the frame the standalone client got from the same page
  earlier; `A much longer button caption than before` gave `26FBFBDC1155` (9,153 bytes) and the image shows that caption on the
  button; after `undo` the frame is `4BE6315BB585` again. So the edit repainted through the surface and undo restored the exact
  original pixels. (`od.winui-designer.frame-profile` and `render-sample` are ProGPU-era diagnostics and say only
  `render 1024x720 png=0KB` / `no frame` for this backend; they prove nothing either way.) The fixture file on disk was never saved.
- Without the C# Binding add-in loaded the status was `WinUI Surface host: '<file>' belongs to no project.`: the bootstrap
  declines with its own reason, as designed.

Rules found on the way: a designer build may use only the SDK selected in Tools > Options > .NET SDK (the startup SDK when nothing
is selected), so the provider takes `DotNetSdkService.ResolveSdkForProject(project)` and refuses to start when there is none; it
no longer calls `ResolveDotnetHostForArchitecture`, which ranks installed SDKs. A force-killed IDE cannot close its documents, so
their run copies would stay under `%LOCALAPPDATA%\OpenDevelop\surf\run`; each child therefore keeps its copies in a folder named
for its process id, removes that folder on shutdown, and at startup sweeps the id-named folders of processes that no longer exist
(never a live one's, never a folder whose name is not a process id).

**Usability follow-ups (done after the first commit).**

- *Missing Windows App Runtime.* A surface whose bootstrapper finds no usable runtime exits `0x80670016` before announcing a port.
  `SurfaceLaunch.DescribeStartupExit` maps exactly that code to an actionable message (install the runtime, with the installer URL and
  the ARM64 note); `SurfaceProcessHost` waits for the exit code and appends the message to "exited before announcing a port"; other
  codes are reported as the bare code, never guessed at. Tested with a fake surface (`cmd /c exit <code>`), including through the
  child's RPC, where the message reaches the IDE side as the `session/open` error. Not tested on a machine that really lacks the
  runtime (this one has it).
- *Provisioning progress.* The launch provider now gets a status reporter: it shows "provisioning a host matched to X (the first build
  takes several minutes, later ones use the cache)…" and then each phase the provisioner announces. Measured in the live IDE against a
  fresh cache root: `WinUI Surface host: compiling matched host…` for about a minute, then `Rendering…`, then `Rendered by WinUI
  Surface host (1024×720)` at 67 s.
- *Choosing the backend without an environment variable.* `RegisterDevFlowActionsCommand` falls back to the string property
  `WinUIXamlDesigner.Runtime` (`uno`, `microsoft` or `surface`) when `OD_WINUI_RUNTIME` is not set. This is compile-verified only
  (the IDE's global settings file does not exist in test mode and the user's profile was not modified), and there is no Options page
  for it yet.
- *CI.* `.github/ci/test-plan.json` has a `surfacehost-tests` entry in the `unit` lane (`observe` on Windows x64 and ARM64, `excluded`
  elsewhere with a reason: the surface and provisioner are Windows-only and two tests create their root under the drive root). The plan
  parses and `plan-matrix.ps1` builds its matrices; **the suite itself has not run on a runner**, and `dist.ps1` was not exercised
  (the child and payload ride on the add-in's `DeployToAddIns` when the solution builds). Not added: a DevFlow journey that opens a
  WinUI page with the Surface runtime (it needs the Windows App Runtime installed on the runner, which the fork's workflow already
  does with `windowsappruntimeinstall-x64.exe --quiet --force`).
- *Canvas hit test (`design/hit-test`), and what it does not give you.* Probed against the real surface:
  - **`PickAt` finds nothing in frame mode**, even on a named button (the surface logs `PickAt: no authored element at (20,15)`).
    It only selects in native-window mode. After `EnterNative` the same click answers `Selected` for the named `Button` (with its
    `x:Name`, `path` `0.0.0` and bounds), for the unnamed `TextBlock`, and for the `StackPanel` on empty space. Picks and bounds are in
    *pane* space, and the default view is "fit" (zoom 0.91 for a 480x320 page in a 1024x720 pane), so the pick sends
    `view: { fit: false, zoom: 1 }` to make pane space design space.
  - **In native mode `UpdateXaml` answers `Hwnd`, not a frame**, so a pick is a self-contained excursion under the connection's request
    gate: `EnterNative`, `SetMode` design, `PickAt`, a fence, `ExitNative`. The native window is built only when somebody clicks; the first
    click took 549 ms end to end (that includes the surface's first native window), repeat clicks were not timed.
  - **The fence replaces a miss timer.** A hit is `Selected` and a miss is silence, which cannot be told from a slow hit by waiting;
    the first version (400 ms) lost a hit once under load (a flake in the full suite). The surface handles messages on its UI thread in
    order, and `UpdateXaml` in native mode re-hosts the page and answers `Hwnd`, so sending one right after `PickAt` fences it: whatever
    `Selected` came before that `Hwnd` is the answer and none is a certain miss. Tested with a hit that takes 900 ms.
  - **Only a named element is a hit.** The shell resolves a click by `x:Name`, and the surface's own tree path (`0.0.1`) is not the
    shell's path scheme, so an unnamed element or empty space is reported as a miss and the shell keeps its source-model selection.
  - **The WinUI runtime host does not call `design/hit-test`.** Nothing in the WinUI path uses `IDesignHostHitTesting`; its canvas
    resolves clicks, overlays and drags with `ResolveNameAt` over the session's `Tree` (`DesignerElementNode` bounds). So this method is
    correct and tested (fake surface, real surface over DDP, and the real `UnoDesignClient`) but is not what drives canvas selection;
    the tree below is.
  - Verified in the IDE only as far as the client: the IDE runs hidden here, so a real pointer click was not injected.

**Element tree with bounds (what the canvas works from).** The surface has no message that lists a tree, only per-element `Selected`
(from `PickAt`, or `SelectByPath` with a path). So after each accepted render `SurfaceDesignDocument.BuildTreeAsync` walks the
document's elements (children only: a property element such as `Grid.RowDefinitions` is skipped, so it neither becomes a node nor
shifts a sibling's index), gives each the surface's dotted path (`0` root, `0.0` first child, ...) and selects them all in one native
excursion with the same fence as the pick. What comes back (type, `x:Name`, bounds in design space, thanks to the 1:1 view) becomes
`DesignerElementNode`s using the other WinUI hosts' ids and paths (`""` for the root, then `"0"`, `"0,1"`), in document order, which
is what the shell's pick-chain mapping (node type and same-type index in tree order) expects. An element the surface cannot resolve is
dropped and its children move to the nearest resolved ancestor; a page where nothing resolves gets no tree but is still accepted.

- Measured with the real surface: a burst of 7 paths (3 of them nonexistent) came back as 4 `Selected` before the fence in 433 ms,
  and `session/open` for the fixture page returns `Page > StackPanel > [Button "Go", TextBlock]` with real bounds.
- **A/B in the live IDE** (`od.winui-designer.query-element-screen-bounds`, which reads the tree): with the tree on, `PrimaryButton` is
  98x32, `TitleText` 173x19 and `RootStack` 1024x720 on screen; with `OD_SURFACE_TREE=0` the same session answers `No rendered element
  named 'PrimaryButton'` (and the other two). So the geometry the canvas needs now exists. A real pointer click and a drag were not
  injected (the IDE runs hidden here), so selecting by clicking is **not yet exercised end to end**.
- **Cost:** one native excursion per accepted `session/open` or `session/update`, roughly half a second more per render (the burst
  above took 433 ms; a first native window is the dearest part). `OD_SURFACE_TREE=0` in the environment turns it off, and
  `MaxTreeElements` (400) bounds the burst.
- **The path rule was checked against the real surface** with `Button > Border > TextBlock` (implicit content) next to a `Grid` with a
  `Grid.RowDefinitions`: all 7 paths resolved to the right elements (`0.0.0`, `0.0.0.0`, `0.0.0.0.0`, then `0.0.1` and `0.0.1.0`, so the
  property element is not counted and implicit content is a child like any other).
- **Limits:** elements written inside a property element (`<Button.Content><Border/></Button.Content>`; not probed), template content
  and generated items (an `ItemsControl`'s rows) are not in the tree, because their surface paths cannot be derived from the markup;
  they are simply absent, not mislabelled. Elements without an `x:Name` are in the tree (so hit-testing by geometry and the pick chain
  can see them) but the shell can only name them by type and index.

**Theme and visual states (item 5): not possible with this surface's frames, and refused with the reason.** Upstream says a surface
takes its theme once, from `SURFACE_THEME` at startup (`SetTheme` is a logged no-op), so a theme change would be a relaunch. A relaunch
(new surface with the variable, replay the last document and viewport, rebuild the tree, retire the old surface) was written and
passed against fake surfaces, then **discarded** because the real surface does not respond: rendering the same page with
`SURFACE_THEME` unset, `Light` and `Dark` gave byte-identical frames (SHA-256 `D1CE88DCF1E0`, 11,536 bytes) on both the bundled
2.2.0 host and a matched 2.4.0 host, and the surface logs `Preview-canvas background brush: ApplicationPageBackgroundThemeBrush (Light
theme)` in every case (its `boot.log`, where `App.ctor` would log `RequestedTheme = Dark`, is not written when the surface runs from a
run copy, so the reason inside the surface was not seen). A restart would have cost a process and a render and then reported a theme
change that did not happen. So `design/theme` is refused with that measured reason, and `design/go-to-state` is refused because the
surface has no message for forcing a visual state. `SurfaceThemeObservationTests` is a tripwire: it asserts that a Dark launch still
renders the Light frame, and its failure message says to implement the restart when it starts to differ. The VS client uses the native
window path, which may honour the theme; this client only gets frames from it.
Also fixed on the way: disposing a `SurfaceConnection` twice threw `ObjectDisposedException`; it is now idempotent.

Still not done: the OpenCode review (`build/opencode-review.ps1` needs the OpenCode CLI and its provider auth, which were not on the
machine; the change was committed without it at the user's explicit request) and ARM64 (this machine is AMD64).

**`{x:Bind}` and custom controls, measured** (upstream `TestUserApp` built with `dotnet build`, copied to a short path and passed
as `--user-dll`, bundled WASDK 2.2.0 host, one page with a `FancyBadge` custom control and two `{x:Bind}` properties):

| Content | `sampleData: true` | `sampleData: false` |
|---|---|---|
| `<local:FancyBadge Label="..."/>` (type from the project DLL) | real control: its own template and its `Label` dependency property | same |
| Project `Themes/Styles.xaml` (merged as `ms-appx:///TestUserApp/Themes/Styles.xaml`) | applied (the button took the project's teal style) | same |
| `Text="{x:Bind Title}"` | placeholder text named after the property: `Title` | binding dropped, empty |
| `Content="{x:Bind ButtonLabel}"` | placeholder: `Button label` | dropped, empty button |

So **custom controls become real; `{x:Bind}` does not.** The surface parses markup with `XamlReader` and never runs the project's
generated binding code, so no real value can appear. This corrects two statements above: gain 1 of section 7.8 holds for custom
controls, generated metadata and PRI resources, but not for `{x:Bind}`; and the section 7.1 acceptance test ("a Gallery page
using `{x:Bind}` renders its real values (not placeholders)") cannot be met by the upstream surface. Meeting it needs the
compiled-page route (run the XAML compiler and instantiate the generated page class), which is the §7.1 build, not this backend.

The same page on the version-matched host (`TestUserApp` rebuilt with `-p:TestWasdkVersion=2.4.0`, provisioned: `built`,
`matchedHost: true`, 110 s cold, `cached` in 2.2 s afterwards; launched with `--user-dll` and `--user-pri`, `Ready.wasdk`
2.4.0) produced a frame **byte-identical** (same SHA-256) to the bundled 2.2.0 host's with sample data on, so the table holds
for a matched host too.

**Two WASDK versions at once, measured:** the bundled 2.2.0 surface and a matched 2.4.0 surface were started together
and each rendered the fixture `MainPage.xaml` (two background jobs, 7.4 s wall time for both, ports announced after ~1.8 s,
`Ready.wasdk` 2.2.0 and 2.4.0 respectively, both frames identical because that page does not depend on the version). They are
separate processes, each with its own run directory, so there is no shared `.xbf` mirror to collide on (the known defect in the
current Microsoft host, section 7.8 item 2). This covers two versions side by side in one session, not two projects sharing
one matched host.

Still open: ARM64 (this machine is AMD64; only x64 was exercised).

---

## 8. Open questions worth measuring

1. **OpenDevelop Microsoft host**: time to first frame and steady-state edit→paint for a real Gallery
   page, measured the same way WinAppVSCE measures (`open → paint`, `edit → paint`, theme swap).
2. **Compiled preview application cost**: cold build, warm cache hit, payload size, and how many
   pages per project share one host.
3. **Concurrency**: two Microsoft WinUI projects open at once (the `.xbf` mirror currently collides).
4. **Memory**: frame + snapshot per document versus a native child window.
5. **Writeback fidelity**: how many of the 120 Gallery pages round-trip byte-identically today.
6. **Recovery**: whether a native crash in a provisioned host can be distinguished from a user-code
   crash without VS's TAP.
7. **Standalone surface**: whether the upstream `Surface.exe` runs without VS/VS Code, and what its
   LICENSE allows (§7.7, §7.8).
8. **B4b/B4c**: whether the design-time engine the WinAppVSCE issues depend on is public, or is
   what `surface/` already is.

---

## 9. Method and evidence

- Line counts: `find <dir> -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | xargs wc -l`,
  and `git ls-tree`/worktree checkout for the PR commit. Counts exclude generated code and tests
  unless stated.
- OpenDevelop facts were checked in this checkout (`607e38fc05`), including the absence of build
  references to `externals/xamlstudio` and the presence of DDP v70 in
  [`DesignerProtocol.cs`](../../src/Main/Designer/Designer.Remote/DesignerProtocol.cs#L119).
- WinAppVSCE facts were checked against PR head `6e4b3887e797` in a temporary worktree; the
  repository's `main` worktree was left untouched. Documents that exist only in the PR history
  (`docs/designer-workstreams.md`) are linked at the commit that introduced them (`f70889e`).
- WinUIDesigner facts were checked against `main` `456640e04fd2`.
- XAML Studio facts were checked in `externals/xamlstudio` at `d711d64`: the app is a UAP project
  with the `Microsoft.UI.Xaml` (WinUI 2) package, the toolkit is 3,612 lines and the app 7,851. Its
  renderer returns a live `object Element` with no bounds/pixels/tree, and
  `XamlXmlTreeCoordinator` names UWP-only members (`BackgroundSizing`, `SemanticZoomOwner`,
  `IsZoomedInView`), which is why it cannot compile against WinUI 3.
- Known stale statements found during the comparison (do not trust them over the code):
  - `in-process-analysis.md` says the canvas is 2282 lines; it is 3,364 in the three core files.
  - `surface/README.md` says the provisioner accepts x64 only; `HostArch`/P9 support ARM64.
  - `surface/Surface/RenderHost.cs` has old JPEG comments; the wire `Frame` is PNG.
  - `xaml-services.md` and `build-from-source.md` still describe the retired XAML Studio renderer as
    current.
  - `winui-designer.md` §"Reuse Boundary with UnoDevelop and XAML Studio" still describes
    `XamlRenderService` as the renderer; it was the ProGPU profile's, deleted on 2026-11-09.
  - `WinUIXamlHost.cs`'s `IWinUIXamlRuntimeHost` comment still says "Implemented by the independent
    XAML Studio/Uno runtime assembly"; the current Uno and Microsoft hosts are separate code.
- The WinUIDesigner issue list was read on 2026-11-11 (`#1`–`#6`).
- The WinAppVSCE design issues (`#54`, `#56`, `#59`, `#61`) and `microsoft/winappCli#636` were read
  on 2026-11-12 with `gh issue view`. Only their public text was read; B4b/B4c was not found as a
  separate public artifact, and statements about Microsoft's direction are inferences from those
  texts.

## See also

- `externals/xamlstudio` — the fourth baseline (UWP + WinUI 2), upstream `dotnet/XAMLStudio` v2
  `dev`, pinned at `d711d64`.
- [`winui-designer.md`](winui-designer.md) — OpenDevelop's WinUI/Uno runtime, loading contract and
  corpus history.
- [`designer-common.md`](designer-common.md) — DDP, host lifecycle, shell/backend boundary.
- [`in-process-analysis.md`](in-process-analysis.md) — why xamlstudio is a previewer, not a designer.
- [`xaml-loader-comparison.md`](xaml-loader-comparison.md) — the round-trip corpus method to extend to
  WinUI.
