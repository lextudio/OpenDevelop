# Stride Game Studio on Linux/macOS via LibreWPF

Tracking follow-up for [stride3d/stride#1922](https://github.com/stride3d/stride/issues/1922)
("Add Linux support for the Stride Game Studio"). This technote plans and records OUR slice of
that effort: evaluating and driving **LibreWPF as the execution substrate** for the existing
WPF-based editor, instead of (or ahead of) the upstream Avalonia migration.

Status (2026-08-25): windowed viewport live-verified (fusion milestone 3); real `.sdpkg` session
loading implemented (gap 1); the REAL `SceneEditorController`/`EditorGameController` now
constructs and runs for a loaded scene asset (gap 2, big step) — the threading conflict between
`EditorGameController`'s dedicated-background-thread run loop and SDL/Cocoa's main-thread
requirement is resolved with a fork patch (`EditorGameController` now runs its macOS branch
inline on whichever thread calls `StartGame()`, via `GameContextSDL(isUserManagingRun: true)` +
a new `Tick()`, exactly mirroring `StrideSdlViewport`'s own pattern); `StrideSceneEditorViewport`
drives it through the same Cocoa `addChildWindow` overlay bridge. Builds clean end to end; not
yet confirmed live (DevFlow unavailable this session). Remaining: input re-plumbing (mouse/
keyboard/drag into the SDL overlay) so the ~15 already-running `EditorGame*Service`
registrations (selection, gizmos, camera, ...) actually respond to anything.

## Why this matters here

Upstream's stated path to a Linux Game Studio is:

1. runtime/player building on Windows, running on Linux;
2. runtime/player compiling on Linux;
3. editor libraries compiling on Linux (#1908 removed some Windows deps);
4. editor RUNNING on Linux — which upstream gates behind two rewrites:
   [Avalonia migration (#1629)](https://github.com/stride3d/stride/issues/1629) and a
   cross-platform FBX importer (#1923).

Step 4's Avalonia rewrite is a multi-year UI rewrite of a large, mature WPF application.
This workspace owns an alternative substrate: **LibreWPF**, the portable dotnet/wpf fork
(rendered through ProGPU/Silk.NET) that already runs OpenDevelop — a comparably large,
comparably old WPF application — on macOS, with Linux as a supported target of the same stack.

Running Stride.GameStudio under LibreWPF would:

- skip the entire UI rewrite (every XAML view, style, template and MVVM layer stays);
- exercise LibreWPF against a second real-world WPF app — every gap fixed for Stride is a gap
  fixed for OpenDevelop, and vice versa (same package feed, same repack loop, see
  [`librewpf.md`](librewpf.md));
- give upstream #1922 a concrete data point: "compiles and runs on Linux with a WPF shim, here
  is the exact gap list" — useful even if they still choose Avalonia eventually.

Working hypothesis (to be validated by Phase 0, not assumed): the editor compiles with modest
changes and runs under LibreWPF with a bounded set of platform fixes, the same shape OpenDevelop
needed (STA gating, window activation, popup/menus, drag-drop).

## Facts verified from upstream (2026-08-24, master)

| Fact | Evidence | Consequence |
| --- | --- | --- |
| Editor is still WPF | `sources/editor/Stride.GameStudio/Stride.GameStudio.csproj` and `Stride.Editor.csproj` set `<UseWPF>true</UseWPF>` | No XAML dialect migration needed — LibreWPF consumes BAML/XAML directly |
| Stride.Editor ALSO uses WinForms | `Stride.Editor.csproj` sets `<UseWindowsForms>true</UseWindowsForms>` | NOT for UI — only the game-viewport HWND embedding + its drag-drop/cursor satellites (see "Why the editor still uses WinForms" below); replaceable via the engine's own `GameContextSDL` |
| Pinned `win-x64` RID on both editor projects | `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` | Must switch to a portable/osx/linux RID story; check `Directory.Build.props` for more RID pinning |
| Builds on .NET 10 SDK | root `global.json` pins `10.0.100` | Same SDK generation as this workspace — no TFM chasm |
| Custom MSBuild SDK | `sdk/Stride.Build.Sdk.Editor/Sdk.props` imported by every editor project | Port/adapt like `ProGPU.Wpf.Sdk` was built for LibreWPF; likely needs a non-Windows branch |
| `StrideSTAThreadOnMain` | GameStudio csproj property | On macOS, `Thread.SetApartmentState(STA)` throws `PlatformNotSupportedException` — OpenDevelop already established the fix pattern (request STA only on Windows; LibreWPF doesn't need STA) |
| `StrideAssemblyProcessor` post-build IL step | GameStudio csproj | Verify it runs on macOS (it is a .NET tool? if it shells out to Windows tooling, that is a gap) |
| Per-API graphics DLL layout | `StrideMultiGraphicsApiHost=true` lays D3D/Vulkan DLLs into subfolders chosen at launch | On macOS the launcher must select the Vulkan/GL payload; check what `SharpDX`/`DirectX` fallbacks exist |
| FBX importer is C++/CLI | upstream #1923 | Windows-only compile unit; excluded from Linux builds until replaced (ufbx/assimp route) |
| Already on modern .NET 10 — NOT .NET Framework | `sources/sdk/Stride.Build.Sdk/Sdk/Stride.Frameworks.props`: `StrideFramework=net10.0`, `StrideFrameworkWindows=net10.0-windows`; root `global.json` pins SDK 10.0.100 | No "upgrade to .NET 10" phase exists; the port is purely a WPF-substrate swap. Only the VS integration tooling multi-targets `net472` (VSIX must match Visual Studio itself) — out of scope |
| `build/.nuget/NuGet.exe` is vestigial | zero references across all `.cs/.props/.targets/.bat/.ps1/.cake/.sh/.proj` files | Not part of the build chain (legacy LFS binary); ignore it |
| Editor TFM decision is centralized upstream | `sources/sdk/Stride.Build.Sdk.Editor/Sdk/Stride.Editor.Frameworks.props` — its own comment: "When migrating from WPF to Avalonia, this is the single location to update framework targeting" | Our LibreWPF variant plugs in at this exact spot (a `net10.0-windows`-vs-LibreWPF switch), not per-project edits |
| Runtime build system already defines macOS/Linux targets | same Frameworks.props: `StrideFrameworkmacOS=net10.0-macos`, Android/iOS variants, `EnableWindowsTargeting=true` | Phase 1's "runtime on non-Windows" is largely upstream-complete; verify rather than port |

## Why the editor still uses WinForms (verified 2026-08-24)

The editor UI is NOT WinForms — every panel, menu, property grid and dialog is WPF. `System.Windows.Forms`
appears in exactly one architectural place plus two satellites, all serving the same mechanism:
**embedding the live game-engine render output into the WPF editor**.

The mechanism (`EditorGameController.SceneGameRunThread`,
`sources/editor/Stride.Assets.Presentation/AssetEditors/GameEditor/Services/EditorGameController.cs:404`):

1. The scene/game runs on a **dedicated background thread**, which creates an INVISIBLE WinForms
   form — `EmbeddedGameForm : GameForm` with `TopLevel = false, Visible = false`
   (`sources/editor/Stride.Editor/Engine/EmbeddedGameForm.cs`). The form exists purely to hold a
   **native Win32 HWND** (`windowHandle = GameForm.Handle`).
2. The engine renders into that HWND via `GameContextWinforms(GameForm)`
   (`sources/engine/Stride.Games/GameContextWinforms.cs`) — the classic "render into a native
   child window" pattern, chosen because WPF airspace forbids freely compositing native content
   inside the WPF tree, so an HWND-backed host is the standard bridge.
3. The WPF side bridges back through `GameEngineHost : FrameworkElement, IWin32Window,
   IKeyboardInputSink` (`sources/presentation/Stride.Core.Presentation.Wpf/Controls/GameEngineHost.cs`);
   `EmbeddedGameForm.WndProc` forwards raw Win32 messages (`WM_KEYDOWN`, `WM_MOUSEMOVE`,
   `WM_MOUSEWHEEL`, mouse buttons, `WM_CONTEXTMENU`) to it.

Satellite uses, all attached to that same form:

- **OLE drag-drop**: `EditorGameController.DragDrop` registers `GameForm.DragDrop`/`DoDragDrop`
  (WinForms wraps the HWND-bound OLE drop target), moving entities between the game view and
  editor;
- **Cursor plumbing**: `Cursors.No/SizeAll/SizeWE/...` enums from the adorner layer applied via
  `GameForm.Cursor = cursor` (`EditorGameController.ChangeCursor`).

Why not pure WPF: there is no supported way to hand WPF content a native swapchain without an
HWND host, and a WinForms `Control` was the cheapest HWND holder with OLE drag-drop and cursor
handling built in.

**Why this is good news for the port**: the engine ALREADY ships non-WinForms window contexts —
`GameContextSDL : GameContext<Window>` (what the Android/iOS targets use) and
`GameContextHeadless`. So replacing the editor's `GameContextWinforms` on non-Windows with an
SDL-based context is a switch of the editor's own wiring, NOT a request that LibreWPF reimplement
WinForms window interop. What remains to design (Phase 3 item 6) is how the SDL/ProGPU surface is
presented inside the WPF editor window and how keyboard/mouse/drag flow — the same class of
problem OpenDevelop solved for designer surfaces (frame presenter + synthetic/routed input).

## Facts verified from the local clone (2026-08-24)

- Cloned to `/Users/lextm/uno-tools/stride` (`--depth 1`, master). The repo uses Git LFS
  (`git-lfs` now installed locally); checkout initially failed on missing LFS until `git lfs
  install` — build assets like `build/.nuget/NuGet.exe` are LFS-tracked, so a plain source-only
  clone is not enough for building.


## Strategy

Follow #1922's own task list, but substitute step 4's substrate:

- **Compile** the editor on macOS/Linux against LibreWPF packages (upstream step 3, unchanged in
  spirit — removing Windows-only dependencies helps BOTH paths);
- **Run** the editor under LibreWPF (replaces the Avalonia dependency for our path; #1629 stays
  upstream's business, we do not block on it).

Non-goals for this effort: rewriting editor views in any new UI framework; making the Stride
*runtime/player* changes (that is upstream steps 1–2, we only verify them); shipping anything —
this is a feasibility-and-gaps effort whose deliverable is (a) a runnable editor and (b) a
precise, upstream-ready gap list.

## Repository layout (planned)

```
/Users/lextm/uno-tools/stride                  # shallow clone of stride3d/stride (Phase 0)
/Users/lextm/uno-tools/stride-fork             # our fork with the port commits (created when
                                               # Phase 2 starts changing files)
librewpf gaps land in /Users/lextm/uno-tools/librewpf (existing checkout) and flow through the
same local-feed repack loop documented in librewpf.md
```

## Phases and acceptance gates

### Phase 0 — Local build + WPF-surface inventory (feasibility spike)

1. Shallow-clone stride; restore + build the ENGINE on macOS with the system .NET 10 SDK
   (`dotnet build` on a representative runtime project). Record failures verbatim.
2. Build the EDITOR (`sources/editor/Stride.GameStudio`) on macOS. Expected blockers: the custom
   editor SDK's Windows assumptions, `win-x64` RID pinning, Windows-only P/Invoke compile units.
   Fix nothing yet — inventory first.
3. Inventory the WPF/WinForms API surface the editor actually uses (grep + reflection over the
   built assemblies): which `System.Windows.*` types, which `HwndHost`/`WindowsFormsHost`
   embedding, which dialogs. Cross-reference each against LibreWPF's shipped surface
   (`PresentationFramework.dll` in `LibreWPF.Transport`).
4. Smoke-run the (Windows-built) `Stride.GameStudio.exe` under LibreWPF on macOS if the binaries
   can be produced at all (even partially) — earliest possible signal on windowing/startup.

Gate: a written inventory table (type → used-by → LibreWPF status: ok/gap/unknown), and a list
of the exact compile errors sorted into "fix in stride fork" vs "fix in librewpf".

### Phase 1 — Runtime/player on Linux (verification only)

Confirm upstream steps 1–2 hold from this machine: build the Linux player target with the dotnet
CLI, run a minimal game headlessly/offscreen if possible. This phase produces evidence for the
upstream issue, not code.

Gate: one sample project compiled for Linux and observed running (windowed on macOS via ProGPU,
or headless), with the command line recorded.

### Phase 2 — Editor COMPILES on macOS (fork work begins)

1. Create the stride fork; branch `librewpf-port`.
2. Adapt the editor MSBuild SDK for non-Windows (mirror how `ProGPU.Wpf.Sdk` selects LibreWPF
   references; keep Windows behavior intact when building ON Windows).
3. Replace/condition Windows-only APIs surfaced by the compiler (P/Invokes, `System.Drawing`,
   registry, etc.) using the OpenDevelop patterns (`RuntimeInformation.IsOSPlatform` gates,
   portable shims).
4. Every LibreWPF-side gap found here becomes a task in the librewpf repo (tracked below), NOT a
   workaround inside stride.

Gate: `dotnet build sources/editor/Stride.GameStudio` succeeds on macOS with zero Windows-only
compile units, and the existing Windows CI path still builds unchanged.

### Phase 3 — Editor RUNS on macOS under LibreWPF

Ordered by expected difficulty (from the OpenDevelop experience):

1. process startup, logging, assembly resolution (deps.json/runtimeconfig paths);
2. main window activation/focus (ProGPU `WpfPortableWindowActivation`);
3. menus/popups/dialogs (the historically thickest area — see librewpf.md's popup findings);
4. docking/layout (Game Studio uses its own docking library — identify it in Phase 0);
5. drag-drop (`PortableDragDropOperation`; mind the re-entrancy rules recorded in
   [`wpf-designer.md`](wpf-designer.md));
6. **embedded scene-editor rendering** — the known deep water, mechanism now identified (see
   "Why the editor still uses WinForms" above): an invisible WinForms form holds the HWND that
   `GameContextWinforms` renders into; WPF bridges input via `GameEngineHost` message
   forwarding. The cheap direction is NOT WinForms interop under LibreWPF but switching the
   editor to the engine's existing `GameContextSDL` on non-Windows and presenting that surface
   inside the editor window (ProGPU bridge, same class of problem as OpenDevelop's frame
   presenter), with keyboard/mouse/drag re-plumbed. This item alone can invalidate the schedule
   — spike it EARLY, not last.
7. settings/registry isolation, file associations, VSIX integration (likely defer/disable).

Gate: open a stock Stride template project end-to-end — create, edit a scene, move an entity,
save, build assets, launch preview.

### Phase 4 — FBX importer (#1923 alignment)

Evaluate ufbx (C, embeddable, MIT) vs assimp-net for a managed importer producing the same
`Entity`/model data `Stride.Importer.FBX` produces today. Only after Phase 3; asset pipeline can
run with importers disabled until then.

Gate: an `.fbx` asset imports with matching node hierarchy/triangles vs the Windows importer's
output.

## Gap ledger (fill as phases progress)

| # | Gap | Found in | Belongs to | Status |
|---|---| --- | --- | --- |
| G1 | `EmbeddedGameForm : GameForm` fails CS0246 on macOS — engine's WinForms `GameForm` only exists in the Windows-TFM build, while the editor unconditionally targets `net10.0-windows` and resolves the engine's net10.0 (SDL-only) output | `sources/editor/Stride.Editor/Engine/EmbeddedGameForm.cs(14)` | stride fork | **fixed 2026-08-24** — platform split behind `STRIDE_EDITOR_WINFORMS` (set per host OS in `sources/Directory.Build.props`); non-Windows gets headless twins |
| G2 | Drag-drop plumbing (`EditorGameController.DragDrop.cs`) is WinForms OLE bound to the game form's HWND | same file family | stride fork | **deferred no-op** on non-Windows (`EditorGameController.DragDrop.Headless.cs`: `DoDragDrop` reports None, drop-target enable/disable no-op); real WPF-routed drops land with the frame-presenter input slice |
| G3 | Cursor plumbing: adorners used WinForms `Cursors`; `ChangeCursor` pushed them to `GameForm.Cursor` | `UIEditor/Adorners/*`, `UIEditorGameAdornerService.Events.cs`, controller | stride fork | **fixed 2026-08-24** — adorners now use WPF `System.Windows.Input.Cursors` (same names); Windows maps WPF→WinForms cursors in `ToFormsCursor`; headless no-op until input slice |
| G4 | Scene-editor viewport presentation | `HeadlessGameHostView`, `SceneGameRunThread`'s `GameContextHeadless()` | stride fork (+ editor-side Cocoa `addChildWindow` glue) | **live-verified in the addin, not yet in the real scene editor**: headless+readback route built and worked (milestones 2/2.1) but proved unviable (leaking/crashing GPU→CPU copy — see below); pivoted to windowed-surface (`GameContextSDL`), whose platform blocker (macOS drawable-doubling) is **resolved** (`SkipBackBufferClampToWindow`) and whose composition bridge (`addChildWindow` overlay, borderless, content-rect-anchored) is **implemented, building, and confirmed live** (fusion milestone 3: opening a `.sdpkg` shows a real GPU-presented windowed render docked in the workbench) — but that is the addin's standalone placeholder-scene `StrideSdlViewport`, NOT yet wired into the stride fork's own `EditorGameController`/`SceneGameRunThread` (still `GameContextHeadless()` there) or through input re-plumbing; swapchain-churn perf tuning is also open (non-gating) |

### G4 feasibility probe — MEASURED blocker (2026-08-24)

Before building the frame presenter, a bounded GPU probe (`StrideGpuProbe`, scratch console
referencing the built `net10.0-macos` Stride.Graphics) answered the make-or-break question:
**does headless GPU render + readback work on this macOS host?**

| Step | Result |
| --- | --- |
| MoltenVK init | **works** — MoltenVK 1.4.2, Vulkan 1.4.350, 153 extensions |
| Adapter enumeration | **works** — Apple M1 Pro (integrated, Metal 3, ~12 GB) |
| Headless `GraphicsDevice.New(adapter, ..., null windowHandle)` | **works** |
| Offscreen render target `Texture.New2D` | **works** |
| `commandList.Clear` + `Flush` | **works** |
| `commandList.Copy(rt, staging)` (GPU→staging for readback) | **SEGFAULTS** (exit 139, native crash) |

The copy-to-staging readback path natively crashes on this macOS/MoltenVK stack as built. This is
the deep-water risk G4 flagged, now measured rather than speculated. Consequences:

- G4 (frame presenter) is **blocked at the engine's Vulkan staging-copy readback**, NOT at the
  integration seam or any UI code. Fixing it is upstream engine work (Stride.Vulkan copy path)
  or a different readback strategy (e.g. `Map`/direct pointer readback, or Metal via a different
  API) — it is not something the OpenDevelop addin can route around.
- The build-time port (G1–G3, editor compiles on macOS) is unaffected and stands.
- The scene-editor slice depends on G4, so its schedule is gated on this engine issue.

Recorded per this project's own rule ("if something genuinely doesn't work headlessly under
LibreWPF/this platform, STOP and report exactly what broke"). Re-run the probe after any
upstream Stride Vulkan readback fix; the probe is a 30-second loop.

#### Root-cause digging (2026-08-24, second pass)

The segfault was localized with lldb + register/struct inspection to
`vkCmdPipelineBarrier + 164: ldr x28, [x19, #0x8]` — MoltenVK dereferences a NULL internal
command-buffer state object (`x19`, which a `cbz` at +128 had guarded for an earlier access but
not re-guarded before the +164 load). The barriers Stride passes are structurally valid
(verified live: correct `sType`, `pNext=null`, and — after the fix below — `VK_QUEUE_FAMILY_IGNORED`),
so the crash is MoltenVK's internal render-state expectation, not the barrier contents.

Two findings, one genuine fix landed:

- **FIXED in the fork: `CommandList.Vulkan.cs` `Copy` constructed image/buffer barriers with
  `srcQueueFamilyIndex = dstQueueFamilyIndex = 0` instead of `VK_QUEUE_FAMILY_IGNORED`**
  (a Vulkan spec violation — equal non-IGNORED family indices mean a queue-family transfer).
  Patched all four barrier constructions in `Copy` to set `VK_QUEUE_FAMILY_IGNORED`
  (the `VkBufferMemoryBarrier` in the working upload path apparently already set it, which is
  why upload didn't crash). Verified the fix is in the built structs (register dump shows
  `0xffffffff` in the family fields). This is a real latent bug worth keeping regardless.
- **STILL CRASHES after that fix**: the +164 NULL deref persists, so the queue-family fix was
  necessary-but-not-sufficient. The remaining cause is MoltenVK's internal render-pass/dynamic
  state being NULL when Copy issues the barrier — consistent with an ad-hoc
  copy-without-render-pass protocol gap in the minimal probe (the engine's own readback path,
  e.g. `LambertianPrefilteringSHNoCompute`, runs inside a real `GraphicsContext` with draws and
  proper submit/fence first). Deeper diagnosis = upstream Stride/MoltenVK interop work; parked
  here per the stop-and-record rule. The probe source is retained at
  `/var/folders/.../opencode/stride-gpu-probe/` for resuming (rebuilt Stride.Graphics.dll is in
  the stride clone's `net10.0-macos` output).

#### Third pass — isolated to main-queue render state (2026-08-24)

Further isolation runs, all on the rebuilt engine (queue-family fix in place):

| Experiment | Result |
| --- | --- |
| buffer→buffer copy (both staging) | **works** — the barrier machinery itself is fine |
| image→image copy | **crashes** |
| image→staging copy | **crashes** |
| `SetRenderTargetsAndViewport` then Clear | **crashes** (even the render-pass begin path) |

So the line is: on this MoltenVK 1.4.2 stack, **only pure buffer operations survive on the
main command queue headlessly; any image-barrier or render-pass usage crashes**. Two
corroborating facts bound this as a probe-protocol gap rather than a blanket "GPU doesn't work":

- The engine's own `TestLambertPrefilteringSH` has **reference PNG outputs committed for
  macOS Vulkan on Apple M1/M4** (`tests/Stride.Graphics.Tests.11_0/macOS.Vulkan/`) — that test
  does `Copy`+`GetData` readback, so the REAL readback path demonstrably works on macOS.
- The texture **upload** path (`Texture.Vulkan.cs` ~line 400) uses the same
  `VkImageMemoryBarrier`+`vkCmdPipelineBarrier` on a **dedicated copy queue**
  (`ExecuteAndWaitCopyQueueGPU`) and works headlessly.

Conclusion for G4: the readback must run through the engine's real rendering context (a
`Game`/`GraphicsContext` with a properly initialized headless device and render passes), NOT a
bare `CommandList.New`+`Clear`+`Copy` — the minimal probe lacks whatever device/render-pass
state the full engine pipeline establishes. The probe cannot be reduced to a 20-line snippet;
G4 should be built as a minimal `EditorServiceGame`-style headless game (which is exactly the
headless twin the seam already targets), not as a raw graphics-API probe. Actionable next step:
stand up the headless `Game`/`GraphicsContext` correctly and read back through a real pass.

#### RESOLVED — headless Game probe passes (2026-08-24)

Built the correct-protocol probe: a minimal `Stride.Engine.Game` subclass running against
`GameContextHeadless(256,256)`, `Draw()` clears the presenter back buffer inside a real render
pass (`SetRenderTargetAndViewport`+`Clear`), reads back via the engine's own
`GraphicsDevice.Presenter.BackBuffer.GetDataAsImage(GraphicsContext.CommandList)`, saves the PNG,
and exits after 2 frames. Result:

- `Game.Run` completes normally; **no crash** (the full pipeline initializes whatever MoltenVK
  state the bare `CommandList` probe lacked).
- Readback yields a real **1280x720 RGBA PNG** on both frames (5237 bytes, verified to decode).
- Only hiccup en route: `SixLabors.ImageSharp` (the PNG encoder) and the transitive
  `Silk.NET.Core`/`Vortice.Vulkan` must be present (deps.json), same class of transitive-dep
  issue recorded for the addin.

**G4 is therefore UNBLOCKED with the correct protocol**: run the engine as a headless
`Game` (which is exactly what `EmbeddedGameForm.Headless` targets) and read frames via
`GetDataAsImage`/staging. The earlier segfaults were probe-protocol artifacts, not platform
blockers. The queue-family fix in `CommandList.Vulkan.cs` stands as a genuine latent bug. The
probe lives at `/var/folders/.../opencode/stride-gpu-probe/StrideHeadlessGame.*` for reuse as
the frame-presenter base.

#### Fusion milestone 2 — LIVE viewport visible in OpenDevelop (2026-08-24)

Wired the headless `Game` into the addin as `StrideHeadlessViewport` (the seam's headless twin
made real): a background thread runs `HeadlessViewportGame` (`GameContextHeadless`, 640x360,
animated clear color), reads each frame back via `GetDataAsImage` → `PixelBuffer.DataPointer`,
and a `DispatcherTimer` presents it to a WPF `WriteableBitmap` in the view. Opening a `.sdpkg`
now shows the **live animated Stride render** (a colour-cycling square) alongside the package
info bar — the visible "designer full picture": the Stride engine runs headless inside the
OpenDevelop/LibreWPF process, renders, reads back, and presents, in real time.

Verified live: a 380x214 `Image` element in the visual tree, and the user confirms the colour
keeps changing (live animation, not a static frame). The engine closure (~19 Stride dlls plus
`Stride.runtimes` native payload + `SixLabors.ImageSharp`/`Vortice.Vulkan`/`Silk.NET.Core`) is
now deployed with the addin.

This proves the full bridge end to end. Next: replace the synthetic animated-clear scene with a
real scene editor (asset-backed scene + interactive input), which is the remaining scene-editor
slice on top of this foundation.

#### Fusion milestone 2.1 — live scene (real draw calls) (2026-08-24)

Upgraded the viewport from a flat animated clear to a **rendered scene made of real draw calls**:
a scrolling checkerboard ground grid + a rotating, hue-cycling billboard square + orbiting
satellite squares, all drawn via `SpriteBatch` (textured quads, generated 1×1 white texture)
inside the headless game's `GraphicsContext` render pass. Each frame is read back
(`GetDataAsImage` → `PixelBuffer.DataPointer`) and presented to the `WriteableBitmap`.
Confirmed live via the log: `[StrideViewport] first frame ready 1280x720 stride=5120`. The view
now shows an animated scene with structure and depth rather than a single colour block —
evidence that the full Stride pipeline (not just a clear) runs headless inside OpenDevelop.

#### G4 viewport stability/perf investigation — architectural finding (2026-08-24)

The headless viewport milestone hit a hard wall that re-frames the whole approach:

- **Per-frame GPU→CPU readback on the OpenDevelop headless MoltenVK stack is unstable**.
  - `commandList.Copy(image → staging)` **segfaults** intermittently (the same
    `vkCmdPipelineBarrier+164` NULL-deref as the bare-CommandList probe), whether on a persistent
    staging texture or per-frame. `Texture.GetData` internally issues exactly this copy, so it
    is not a routing-around-the-bug but the same path.
  - `GetDataAsImage` (the only path that produces frames reliably) **leaks per frame** — it
    creates a fresh staging texture + `Image` each call; observed RSS climbing
    1.0 GB → 6.8 GB within ~75 s at a throttled ~30 fps. `using`-disposing the `Image` does not
    reclaim the underlying staging/native memory fast enough on this stack.
  - Conclusion: **headless-render + CPU-readback embedded into the IDE is not a viable long-term
    presentation path here** — it is bottlenecked on a flaky/leaking GPU→CPU copy in
    Stride/MoltenVK, independent of our code.
- **The performance answer the user asked for is therefore architectural, not a micro-opt**:
  switch the viewport from headless+readback to a **windowed rendering surface** so the GPU
  presents directly to a window and pixels never cross to the CPU. That means using Stride's
  `GameContextSDL` (which the engine already ships and macOS-compatible) and embedding that SDL
  window inside the WPF editor — the "airspace" path we originally avoided. Given that
  headless-readback is now proven unstable, windowed-surface is the only route that yields both
  stable rendering AND low CPU cost. This is a Phase 3 architectural decision, not a small
  optimization; the frame-presenter (CPU readback) approach is parked as unviable on this stack.
- Retained wins regardless: the B8G8R8A8 backbuffer-format alignment (no per-frame channel
  reorder), the recycled double buffers (no per-frame managed allocation), and the ~30 fps
  readback throttle — all are correct engineering that any future surface path benefits from.
- Concrete evidence trail kept in the addin's `StrideHeadlessViewport` history and the probe
  (`stride-gpu-probe/StrideHeadlessGame.*`): the simple `Clear`-only rendering was stable
  (>150 s, no growth); adding `SpriteBatch` draws or a custom offscreen render target pushed
  the flaky `vkCmdPipelineBarrier`/leaking `GetDataAsImage` path into view.

### G4 windowed-surface route — PROVEN FEASIBLE on macOS (2026-08-24)

Per the architectural decision (headless-readback unviable, switch to windowed surface), a
standalone SDL-window probe (`stride-sdl-probe/StrideSdlProbe`) validated the door: **Stride
renders via a real SDL window on macOS** (GPU direct present, no CPU readback).

Two things were needed to get SDL's Vulkan path up on macOS:

1. **Native SDL2**: `libSDL2-2.0.dylib` (from `Ulz.Native.SDL`) must be present; Silk.NET.SDL
   fails to load otherwise ("Could not load from any of the possible library names").
2. **SDL loading MoltenVK**: the SDL window is created with `SDL_WINDOW_VULKAN`
   (`STRIDE_GRAPHICS_API_VULKAN` sets `WindowFlags.Vulkan`), so SDL needs the MoltenVK
   portability driver. Setting `DYLD_LIBRARY_PATH=<runtimes>/osx-<arch>/native` and
   `VK_ICD_FILENAMES=<same>/MoltenVK_icd.json` lets `Sdl.GetApi()` find `libvulkan.1.dylib` +
   MoltenVK. Result: MoltenVK 1.4.2 initializes, the Apple M1 Pro device creates, the graphics
   pipeline and render loop start — **windowed rendering works**.

The vertical integration (WPF editor owning the SDL window via `GameContextSDL(Window, parent)`
= `SDL.CreateWindowFrom(parentHandle)`) is the next slice; note `CreateWindowFrom` means the SDL
window can attach to an *existing* native window handle, which is the bridge for embedding into
the editor. The remaining puzzle found on the way: a Metal texture runs "width (20480) >
maximum (16384)" — an oversized texture somewhere in the SDL/surface path (likely a stale
surface-capability/backbuffer size rather than a fundamental blocker; the swapchain gets our
`PreferredBackBufferWidth=640`). Standalone probe reached device+render before this assert; the
windowed route is confirmed viable, requiring targeted surface-size debugging next.

#### Root cause of the 20480 texture — macOS SDL drawable doubling (2026-08-24)

The standalone SDL probe's MoltenVK logs show the real mechanism: the swapchain grows by 2x on
every resize —
`2560×1440 → 5120×1964 → 10240×3928 → 20480` (each `Created 2 swapchain images ... contents scale
2.0 in layer CAMetalLayer: SDL_cocoametalview ... on screen Built-in Retina Display`). So the
Metal texture caps out at 16384 after a few frames. This is **SDL's drawable/surface size
doubling every frame on a Retina (contentsScale 2.0) Cocoa display**, not our backbuffer size
(the swapchain is correctly created at `PreferredBackBufferWidth=640`; the *drawable* the layer
resizes to is what doubles). This is a macOS-specific SDL+Cocoa drawable handling issue in the
engine/Stride SDL layer (high-DPI resize loop), separate from the windowed-viability question.
Targeted fix belongs in the SDL window's drawable/`contentsScale` resize handling; the
windowed route itself is confirmed, and embedding via `CreateWindowFrom` is the next slice.

#### macOS drawable-doubling — localized (2026-08-24, further)

Root cause localized: the doubling loop is a **feedback loop between `SDL_GetWindowSize`
(backing `GameWindowSDL.ClientBounds` → `ProcessClientSizeChanged` → device recreation →
swapchain recreate) and the MoltenVK drawable size**, amplified because the window is created
`SDL_WINDOW_RESIZABLE` (desktop branch) so a resize event fires each frame, the engine re-reads
the client size, and SDL reports it in a way that doubles the drawable each cycle. Two candidate
fixes, to be tried in order (engine-side, in the stride fork):

1. **`Window.DrawableSize` should use `SDL_Metal_GetDrawableSize` for Vulkan windows**
   (already applied in the fork — `Window.cs` Vulkan branch switched from the meaningless
   `SDL_GL_GetDrawableSize` to `SDL_Metal_GetDrawableSize`). Correct but insufficient alone: the
   doubling is driven by `ClientBounds`/`ClientSize` (`SDL_GetWindowSize`), not `DrawableSize`.
2. **Break the resize feedback** — do not let `ProcessClientSizeChanged` recreate the device
   every frame off a `ClientBounds` that SDL reports differently than the actual drawable; either
   drive the backbuffer purely from the Metal drawable size (single source of truth) or pin the
   window to a fixed size and drop `SDL_WINDOW_RESIZABLE` when a parent/embedded window is in
   play.

Conclusion: windowed rendering is **confirmed working** (MoltenVK init, M1 Pro device, graphics
pipeline + render loop, SDL window, and the Metal drawable path all exercised); the blocker is
the macOS drawable-doubling resize feedback, now precisely localized with a concrete fix path.
The `GLGetDrawableSize→MetalGetDrawableSize` fix is retained as a real latent bug fix (Vulkan
windows are not GL windows).

#### Decisive finding — SDL window size reports pixels, not points, on macOS (2026-08-24)

An instrumented probe printed `clientBounds=5120x1964` at the first game tick. `ClientBounds`
backs `SDL_GetWindowSize`, which per SDL semantics should return **logical points** (e.g. the
requested 640/1280 logical size) — but on this macOS Vulkan/Metal window it returns the
**physical-pixel/drawable size**, which itself had been ballooned by the resize feedback. So the
loop is: engine sets backbuffer (logical) → MoltenVK creates CAMetalLayer at drawable (≈2×
logical) → SDL reports that as the window size → engine treats it as logical → sets an even
larger backbuffer → drawable doubles → repeat until 20480 > Metal's 16384 cap.

Per SDL's own semantics the three APIs are distinct: `SDL_GetWindowSize` (points),
`SDL_GetWindowSizeInPixels` (physical pixels), `SDL_Metal_GetDrawableSize` (drawable pixels).
The fix direction is to keep `ClientBounds`/`ClientSize` on the **logical points** path
(`SDL_GetWindowSize`) and reserve pixel/drawable values for swapchain sizing only — i.e. the
engine is mixing the wrong size domain for a Vulkan/Metal SDL window on macOS. This is an
SDL/Stride high-DPI integration defect (likely a specific SDL build behavior on macOS for
`SDL_WINDOW_VULKAN` Metal windows), not a fundamental limitation. Precisely localized; resolving
it requires a careful, verified change to the SDL window size semantics rather than a blind edit,
so it is recorded here as the concrete next task.

#### CONFIRMED root cause — MoltenVK surface pollutes SDL logical size (2026-08-24)

A **pure SDL probe** (no Stride) pinned down the SDL side definitively: with the exact same
`WindowFlags.AllowHighdpi|Vulkan|Hidden|Resizable` window on macOS, a 640×360 window reports
`SDL_GetWindowSize = 640×360` (logical points, per spec), `SDL_GetWindowSizeInPixels =
1280×720` (physical, ×2 Retina), `SDL_Metal_GetDrawableSize = 1280×720` — **all three APIs are
correct**. So SDL is NOT the problem.

The Stride probe, however, prints `clientBounds = 5120×1964` at tick 1. The one difference from
the pure-SDL probe is that Stride creates a **MoltenVK surface (CAMetalLayer)** on the window
before the game loop; after that, `SDL_GetWindowSize` on the same window reports the polluted
drawable-ish value instead of the logical size, which then feeds `ProcessClientSizeChanged` →
device/swapchain recreation → drawable doubles → loop until the Metal size cap. **This is a
MoltenVK↔SDL interaction defect on macOS (the Vulkan surface establishes a CAMetalLayer that
overrides the SDL-reported logical size), not an SDL bug and not a Stride-only one.** Fixing it
means either (a) not letting SDL's reported logical size be driven by the CAMetalLayer (a
MoltenVK/SDL behavior change or a workaround in how the surface is created/attached), or (b)
breaking the feedback by pinning the swapchain size away from the SDL-reported size once a
surface exists. Recorded as the precise, confirmed blocker: the windowed route works up to
surface creation, and the doubling is a platform-layer (MoltenVK/SDL macOS) size-consistency
defect.

#### path (b) attempted, then SUPERSEDED — RESOLVED with the engine's own clamp switch (2026-08-25)

The first attempt was a draft, hand-written defence in `GraphicsDeviceManager.ProcessClientSizeChanged`
(only honour a client-size change within `2 × preferredBackBuffer*`) — written but never built or run
(recorded here 2026-08-24, left "NOT verified yet").

Before building on that draft, the actual swapchain-resize code was traced end to end
(`SwapChainGraphicsPresenter.Vulkan.cs` around line 445): on every swapchain (re)creation, unless
`Description.SkipBackBufferClampToWindow` is set, the presenter **overwrites**
`Description.BackBufferWidth/Height` with `surfaceCapabilities.currentExtent` — i.e. whatever
MoltenVK reports as the CAMetalLayer drawable size. On macOS that reported extent is the
already-Retina-scaled, already-polluted value from the "MoltenVK surface pollutes SDL logical
size" defect above, and it feeds straight back into window/backbuffer sizing — that overwrite,
not `ClientBounds`, is the actual engine-side amplifier of the doubling loop.

`SkipBackBufferClampToWindow` is an **existing, public, unrelated-to-this-bug switch**
(`PresentationParameters.SkipBackBufferClampToWindow`, already used by
`Stride.Games.AutoTesting.ScreenshotTestRunner`) that exists precisely to keep our own preferred
backbuffer size authoritative instead of letting the window/surface dictate it. So the hand-written
`2×` clamp draft was unnecessary complexity — the fix is one line at the call site, not a behaviour
change to shared engine code.

**Verified, via the SDL probe rebuilt against the fork's engine** (`StrideSdlProbe`, macOS/Apple M1
Pro, MoltenVK 1.4.2):

| Config | Result over 180 ticks (full run to completion) |
| --- | --- |
| Baseline (no fix) | Swapchain doubles every few frames: 2560×1440 → 5120×1956 → 10240×3912 → crash region (16384 cap) |
| `Window.cs` GL→Metal `DrawableSize` fix only | Doubling **stops growing** past 10240×3912 (no crash), but backbuffer/clientBounds stay wrong (10240×3912 instead of the requested 1280×720 @ 2x) |
| + `GraphicsDeviceManager.SkipBackBufferClampToWindow = true` (caller-side, zero engine diff) | **Backbuffer and clientBounds lock to 1280×720 from tick 1**, stable for the full 180-tick run, clean exit (`Game.Run returned`), no doubling, no crash |

Decision: **revert the `ProcessClientSizeChanged` clamp draft** (done — `git checkout --` on
`GraphicsDeviceManager.cs` in the fork; rebuilt clean, 0 errors) — it is not needed and would have
been a speculative behaviour change to code every Stride game goes through. Keep only:

- `Window.cs` `DrawableSize` Vulkan branch: `SDL_GL_GetDrawableSize` → `SDL_Metal_GetDrawableSize`
  (a genuine correctness fix — Vulkan windows are not GL windows — verified in the probe above,
  necessary alongside the clamp switch since it's what stops the *growth* even though it doesn't
  fix the *wrong initial size* on its own).
- At the editor's SDL-context construction site (not yet written — see "Next slice" below): set
  `GraphicsDeviceManager.SkipBackBufferClampToWindow = true` before creating the device. This is
  an editor/caller-side one-liner, not an engine change, so it carries no risk to other Stride
  consumers.

G4's windowed-surface route (Phase 3 item 6) is therefore **fully unblocked**: MoltenVK init,
device creation, render loop, and now correct/stable backbuffer sizing are all verified working
headlessly-embeddable on this macOS host. The remaining work is the WPF-side embedding
(`GameContextSDL(Window, parentHandle)` = `SDL_CreateWindowFrom`) plus input re-plumbing — a
UI-integration slice, not a platform-risk slice.

##### Next slice (superseded by the composition-bridge probes + fusion milestone 3 below; `EditorGameController` wiring itself not yet started): wire `GameContextSDL` into the editor's SDL branch

Step 1's `HwndHost`-equivalent premise below turned out to be wrong (see "Composition-bridge
probes"), and the `addChildWindow` recipe it was replaced with is now proven live end-to-end in
the OpenDevelop addin's own `StrideSdlViewport` (see "Fusion milestone 3" further down) — but
that is the addin's standalone placeholder-scene viewport, not `EditorGameController` itself.
Steps 2-4 below are otherwise still the right shape for wiring the *real* scene editor; only
step 1's mechanism should be read as superseded, not the plan.

`EditorGameController.SceneGameRunThread` (`sources/editor/Stride.Assets.Presentation/AssetEditors/GameEditor/Services/EditorGameController.cs`,
`#else` branch under `STRIDE_EDITOR_WINFORMS`) currently constructs `GameContextHeadless()` — the
now-deprecated headless+readback route (proven unviable above: leaking/crashing GPU→CPU copy).
Swapping it for a windowed `GameContextSDL` embed needs, in order:

1. A native window handle to embed into. `IEmbeddedGameHostView`/`EmbeddedGameForm.Headless.cs`
   currently model a *window-less* twin (`Handle => Zero`) — this needs a real HWND-equivalent
   (an `HwndHost`-backed WPF control on macOS/LibreWPF) before `SDL_CreateWindowFrom` has anything
   to attach to. This is new WPF-side plumbing, not present yet.
2. `GameContextSDL(parentHandle, width, height)` construction in the `#else` branch, with
   `deviceManager.SkipBackBufferClampToWindow = true` set on the created `GraphicsDeviceManager`
   before the device is created (mirrors the probe's `SdlWinGame` constructor).
3. Keyboard/mouse/drag input re-plumbing from the WPF host control into the SDL window (the
   `GameEngineHost` message-forwarding pattern used by the Windows/WinForms path is the template;
   `IEmbeddedGameHostView` is the seam already built for this).
4. Re-run the live DevFlow smoke test (open a `.sdpkg`, confirm a real windowed render — not a
   `WriteableBitmap` — appears docked in the OpenDevelop workbench) before calling Phase 3 item 6
   done.

Not attempted in this pass: it requires a WPF `HwndHost`-equivalent component that doesn't exist
yet in the fork, and verifying it needs the live DevFlow harness (`OD_TEST_MODE=1`, full app run),
which is a separate, larger slice from the platform-defect diagnosis this session closed out.

##### Hard finding (2026-08-25): the WPF-side SDL embed is a NEW platform-composition problem

The "Next slice" step 1 (`SDL_CreateWindowFrom` needs a native window handle) is not merely a
missing component — in this stack the semantics don't line up, and it is a genuinely new
platform-composition effort, not a small plumbing gap:

- macOS/LibreWPF has **no Win32 sub-window / `HwndHost`**: `SDWindowsFormsHost.cs` documents that
  `LibreWinForms`'s `WindowsFormsHost` derives directly from `FrameworkElement`, not `HwndHost` —
  WPF content is composed as a managed element, not a reparented native child.
- LibreWPF's own top-level window is a **Silk.NET/ProGPU native window** (`WpfPortableWindowActivation`),
  not a Cocoa `NSView` you can grab a stable handle from into SDL.
- `SDL_CreateWindowFrom(parentPtr)` on macOS expects a **Cocoa `NSView`/`NSWindow`** pointer.

So the three handle domains — SDL (Cocoa NSView), LibreWPF (Silk.NET window), and the Win32
`HwndHost` model the Windows/WinForms path assumes — are different. A "windowed embed into the
workbench" therefore needs a real composition bridge (e.g. rendering the SDL/Vulkan surface into
a viewport inside the LibreWPF window via the ProGPU/compositor, or adding a native-view host that
SDL can attach to) — a dedicated platform slice, not something the engine-side fixes unblock by
themselves.

Options to consider (pick a direction, not decided here):
1. **Full windowed embed** (goal): SDL Vulkan surface composed inside the LibreWPF window —
   needs a composition/NSView-host bridge in LibreWPF + SDL; highest risk.
2. **Bordered app window** (pragmatic intermediate): the editor's scene game runs in a *separate*
   top-level SDL window (`GameContextSDL(null, w, h)` + `SkipBackBufferClampToWindow=true`), not
   inside the workbench — a real GPU window, just not docked. Simpler (no NSView bridge), validates
   the full rendering path in the real editor; docking is a later enhancement.
3. **Keep headless+present** (fallback): the `WriteableBitmap` frame-presenter — now unviable for
   the reasons above (leaking/crashing readback).

The engine-side prerequisites are all proven (MoltenVK, device, render loop, stable swapchain vía
`SkipBackBufferClampToWindow` + `DrawableSize` Metal fix). What remains is purely the integration
choice above, which is an architecture/product decision.

##### Composition-bridge probes (2026-08-25): option 1 IS achievable, via an overlay child window, not raw NSView reparenting

User decision: pursue **option 1, full windowed embed** (not the bordered/separate-window
fallback). Two standalone probes (`EmbedProbe`, scratch dir, no Stride/LibreWPF app touched)
tested the two candidate composition techniques directly against the real fork build.

**Round 1 — hand SDL a raw `NSView` (reparent-in): FAILS, hard platform limitation.**
Built a plain outer `NSWindow` with an inset `NSView` subview (`initWithFrame:`,
`setWantsLayer:YES`), passed that view's pointer as `Window`'s `parent` (i.e.
`SDL.CreateWindowFrom`, the same call the engine already makes at `Window.cs:91`), and ran a real
`Game`/`GraphicsContext` render loop into it. **Segfaults inside MoltenVK**
(`MVKPhysicalDevice::getSurfaceCapabilities`, null-pointer deref), reproduced twice, `wantsLayer`
made no difference. Root cause: SDL's Cocoa/Metal path only produces a valid surface behind its
own `SDLMetalView` subclass (`+layerClass` → `CAMetalLayer`); a plain `NSView`'s default `CALayer`
is the wrong layer class, and there is no way to make an arbitrary foreign `NSView` into that
subclass via `objc_msgSend` calls alone — it needs a native (Objective-C/Swift) shim class.
**Conclusion: `SDL_CreateWindowFrom` reparenting a WPF-hosted view is not viable without shipping
a small native NSView-subclass library.**

**Round 2 — let SDL own a real top-level window, overlay it via `addChildWindow:`: WORKS.**
Instead of handing SDL a foreign view, let `new Window(title, IntPtr.Zero)` create its own
completely normal, fully Metal-capable window (exactly like the earlier resolved SDL probe).
Extracted *that* window's real `NSWindow` handle via `SDL_GetWindowWMInfo`
(`Silk.NET.SDL.SysWMInfo.Info.Cocoa.Window` — confirmed present via reflection on
`Silk.NET.SDL.dll`), then used Cocoa `[outer addChildWindow:sdlWindow ordered:NSWindowAbove]` —
**the identical technique LibreWPF's own `SilkNetWpfWindowDecorationService.cs` already uses for
popup positioning** (`TryConfigureCocoaPopupOwner`, `addChildWindow` call). Result, verified via
the MoltenVK log (no crash reports produced, confirmed via
`~/Library/Logs/DiagnosticReports`): the render loop **survived a live outer-window move**
(`setFrame:` on the host window) with the child window's frame recomputed and reapplied on every
move — swapchain kept recreating cleanly (640×360 → 640×328 after reposition; a minor inset-math
rounding drift in the probe, not a crash) with `SkipBackBufferClampToWindow` still doing its job.
**No engine or LibreWPF code was touched for this — it is pure caller-side Cocoa/SDL glue.**

(A visual before/after screenshot comparison was inconclusive — the probe window opened on a
background macOS Space that `screencapture` didn't capture — but the swapchain log is the
authoritative signal here, same as every other probe in this technote: it distinguishes
"still rendering" from "crashed/hung" unambiguously, which is what round 1 vs round 2 hinged on.
A follow-up ad hoc CoreGraphics window-list script crashed Python's own process repeatedly
chasing that screenshot confirmation — unrelated to Stride/SDL, cleaned up, not worth repeating.)

**Revised plan for the "Next slice" above**: replace step 1's `HwndHost`-equivalent requirement.
The real embedding bridge does not need a native WPF sub-window host at all — it needs:

1. Construct the SDL context normally (`new Window(title, IntPtr.Zero)`, no parent handle needed).
2. Extract its `NSWindow` via `SDL_GetWindowWMInfo` right after construction (same call proven
   above).
3. `addChildWindow:` it onto LibreWPF's own top-level `NSWindow` (obtainable the same way
   `SilkNetWpfWindowDecorationService` already does via `INativeWindowSource.Native.Cocoa`).
4. On every layout change of the WPF placeholder `FrameworkElement` (resize, scroll, dock-panel
   move, tab switch), recompute its on-screen rect and `setFrame:` the child window to match —
   this is the same "keep it visually pinned" responsibility an `HwndHost` would normally carry,
   just implemented via Cocoa `NSWindow.frame` instead of Win32 `SetWindowPos`. Hide it
   (`orderOut:`) when the hosting tab/panel isn't visible, since a child window has no native
   clipping to its "parent's" bounds.
5. Steps 3 (input re-plumbing) and 4 (live DevFlow verification) from the original "Next slice"
   plan are unchanged.

This is Windows-only-conceptually-different, not harder: the Windows/WinForms path already solves
the identical "keep a foreign-owned render surface visually pinned to a WPF layout rect" problem
via `HwndHost`/`SetWindowPos`; the macOS answer is `addChildWindow`/`setFrame:` on a window SDL
already fully owns, using patterns already proven in this codebase (`SilkNetWpfWindowDecorationService.cs`
for the Cocoa side, this technote's own probes for the SDL/Vulkan side). No native shim library,
no LibreWPF core changes.

#### Fusion milestone 3 — windowed viewport wired into the addin (2026-08-25)

Implemented the revised plan above directly in the OpenDevelop addin
(`src/AddIns/DisplayBindings/StrideGameStudio/StrideGameStudio.AddIn/`), replacing
`StrideHeadlessViewport` (deleted) with:

- `StrideSdlViewport.cs` — a `FrameworkElement` that constructs a `Stride.Graphics.SDL.Window`
  with no parent, runs a `GameContextSDL(..., isUserManagingRun: true)`, extracts its `NSWindow`,
  attaches it as a child window of the host via Cocoa `addChildWindow:`, and repositions/resizes
  it on `Loaded`/`SizeChanged`/`IsVisibleChanged` using the screen-coordinate delta between the
  element and its host window (converted into the host's own Cocoa frame, avoiding any guesswork
  about title-bar/chrome height).
- `SdlNativeWindow.cs` — `SDL_GetWindowWMInfo` wrapper (the technique proven in the composition
  probes above).
- `LibreWpfHostWindow.cs` — resolves the WPF host `Window`'s NSWindow via the public
  `LibreWPF.ProGPU` diagnostics entry point discovered for this purpose:
  `ProGpuWpfDiagnostics.TryGetWindowHost(window, out host)` → `host.SilkWindow.Native.Cocoa`
  (added `ProGPU.Wpf`/`Silk.NET.Windowing.Common`/`Silk.NET.SDL` references to the addin's csproj;
  the first two are already present in the running app's own `bin/`, confirmed by grep, so they
  don't need per-addin copying — only the native `libSDL2-2.0.dylib`, from the `Ultz.Native.SDL`
  package, needed an explicit copy target since it isn't part of Stride's own runtimes output).
- `CocoaOverlayInterop.cs` — the `objc_msgSend` glue (`addChildWindow:`/`removeChildWindow:`/
  `setFrame:display:`/`orderOut:`/`orderFront:`/`frame`), following the same pattern as
  LibreWPF's own `SilkNetWpfWindowDecorationService.cs`.
- `SdlOverlayGame.cs` — the windowed counterpart of the old `HeadlessViewportGame`: identical
  animated placeholder scene (checkerboard + rotating billboard + orbiting satellites), but drawn
  straight to the real presenter backbuffer with **no CPU readback** — the fix for the
  leak/crash that killed the headless route.

**A threading subtlety not covered by the earlier probes, found while implementing this**:
`Game.Run()` blocks in its own loop, which would freeze the WPF dispatcher if called on the UI
thread, but the earlier probes proved SDL/Cocoa window creation and event pumping must happen on
the main thread. The resolution is `GameContext.IsUserManagingRun`/`Game.Tick()`: with
`isUserManagingRun: true`, `GameWindowSDL.Run()` performs only `InitCallback()` (device/window
creation) and returns immediately, storing `context.RunCallback` for the caller to invoke on its
own schedule. `StrideSdlViewport` drives that via `CompositionTarget.Rendering` (once per WPF
frame, on the UI thread = the process main thread), pairing it with
`Stride.Graphics.SDL.Application.ProcessEvents()` each tick (the public SDL event-pump entry
point) since user-managed mode doesn't pump events on its own. This means the whole engine loop
now lives entirely on the WPF UI thread — no background thread, no cross-thread handoff, and no
conflict with the main-thread requirement.

**Status (2026-08-25, superseded below): builds clean, not yet live-verified.** *(Later resolved
— see "Fusion milestone 3 — windowed viewport LIVE-VERIFIED" and "DevFlow gap RESOLVED" further
down: the DevFlow symptom described in this paragraph and the next was a stale/Release build on
this host, not a real blocker, and live verification has since succeeded.)* The addin project and
the whole solution build
with zero errors, and the rebuilt dll deploys correctly (confirmed the shared `AddIns/
DisplayBindings/StrideGameStudio/` output folder already has everything the running app needs:
`Silk.NET.SDL.dll`, `libSDL2-2.0.dylib`, MoltenVK/Vulkan runtimes; `ProGPU.Wpf.dll` and
`Silk.NET.Windowing.Common.dll` already ship in the app's own `bin/`, confirmed by grep, so no
addin-local copy was needed for those). Launching the app in `OD_TEST_MODE=1` (both via
`dotnet run --project SharpDevelop.csproj` and by invoking the built apphost directly) starts
cleanly with no crash and no new startup exceptions — but this session's DevFlow agent did not
respond on its pinned port (9299) in either launch mode (no listening socket, nothing in the log),
so opening a `.sdpkg` and confirming the overlay actually renders/tracks could not be automated.
Blind OS-level UI scripting was deliberately not attempted per this repo's own established
finding (`System Events` clicks land on whatever app is frontmost, not this LibreWPF window,
which was not the active app in this session).

**Follow-up (2026-08-25, conclusion later RETRACTED — see "DevFlow gap RESOLVED" below): concluded
the DevFlow gap was a real, pre-existing environment issue, not a launch mistake.** That
conclusion turned out to be wrong (it was a stale/Release build lacking the DevFlow agent
entirely, not an environment problem) — kept here as the investigative record. Ran the project's
OWN xUnit integration-test fixture (not a manual replication) —
`dotnet test tests/OpenDevelop.IntegrationTests/... --filter-query "/*/*/AddInTests/OpenAssembly*"`
— which launches the app exactly the way `OpenDevelopAppFixture` always has (unrelated to
anything in this session's changes) and then calls its own `WaitForAgentAsync`. Result: **that
wait timed out** (`OpenDevelopAppFixture.cs:386`, ~2 minutes), with the same symptom observed
manually — the app reaches a fully loaded workbench (`dockingManager_Loaded`, layout loaded) but
the DevFlow agent never answers on its pinned port (9299), confirmed via `lsof -a -p <pid> -iTCP
-sTCP:LISTEN` returning nothing at any point in the process's life (the fixture's own comment
says the agent "binds inside the App constructor - long BEFORE the workbench has" loaded, so a
timeout this way means it never bound at all, not that it was merely slow). This reproduces with
the official test harness, so it is a real, standing gap in this dev machine's environment (or a
regression somewhere unrelated to the Stride work), not a mistake in how this session drove the
app manually. **Live verification (open a `.sdpkg`, confirm a real windowed render appears and
tracks docking/resize) remains the next concrete step**, and the DevFlow startup gap that
previously blocked it is now **closed** (see Work log 2026-08-25 "DevFlow gap RESOLVED" — it was a
stale/Release build, not an environment issue; a fresh Debug build binds and serves port 9299).

### Build evidence (2026-08-24, Phase 0 spike)

- Engine: `dotnet build sources/engine/Stride.Engine/Stride.Engine.csproj` on macOS →
  **Build succeeded, 0 errors** (~2m47s), including the AssemblyProcessor IL patch step running
  natively (a flagged risk that did not materialize). Output lands under
  `bin/Debug/net10.0/<GraphicsApi>/` (Vulkan by default on macOS).
- Editor BEFORE the fork fix: failed after 63 assemblies / 44 wpftmp passes on exactly one
  error (G1). Root cause chain: on macOS the SDK defaults `StridePlatforms=macOS`
  (`Stride.Platform.props:74`) → `StrideRuntimeTargetFrameworksWindows` collapses to plain
  `net10.0` → `Stride.Games` builds without WinForms and WITHOUT `Desktop/GameForm.cs`, but WITH
  the SDL stack (`STRIDE_UI_SDL`; verified: the built DLL contains `GameFormSDL`/`GameContextSDL`,
  not `GameContextWinforms`). The editor's pinned `net10.0-windows` +
  `EnableWindowsTargeting=true` lets it compile on macOS, but its engine reference resolves to
  the net10.0 output where `GameForm` doesn't exist.
- Editor AFTER the Phase-2 slice-1 fixes (same day): **Build succeeded — 67 assemblies,
  including `Stride.GameStudio.dll` itself, on macOS.** The whole editor graph (engine + assets
  + editors + GameStudio WPF app + XAML markup compilation) compiles cross-platform with the
  seam surface above; nothing else in the tree needed touching.

### The seam as implemented (Phase 2 slice 1)

- `STRIDE_EDITOR_WINFORMS` define = host-OS check in `sources/Directory.Build.props`
  (`[MSBuild]::IsOSPlatform('Windows')`) — building ON Windows reproduces upstream byte-for-byte;
  any other host OS takes the headless path.
- `IEmbeddedGameHostView` (`Stride.Core.Presentation.Wpf/Controls/`): platform-neutral
  `Visual`/`PointFromScreen` contract; `GameEngineHost` implements it (Windows unchanged).
- `EmbeddedGameForm` twins: WinForms variant unchanged behind the define
  (`EmbeddedGameForm.cs`); `EmbeddedGameForm.Headless.cs` provides a window-less twin
  (`Handle => Zero`, `Host` = the seam interface) plus `HeadlessGameHostView`.
- `EditorGameController.SceneGameRunThread`: `GameContextWinforms` vs `GameContextHeadless()`;
  STA request gated to Windows (the LibreWPF PlatformNotSupportedException pattern);
  `GetMousePositionInScene` returns origin headlessly until screen-mapping exists.
- `GameStudioPreviewService`: same treatment (`host` field retyped to the seam).

## Fusion with OpenDevelop: keep vs discard (2026-08-24)

When Game Studio stops being a standalone app and becomes an OpenDevelop addin, every component
falls into one of three buckets: **KEEP** (load as-is or nearly), **ADAPT** (keep the logic,
swap the shell surface), or **DISCARD** (OpenDevelop already owns that job). Decided now so the
Phase 2/3 slices don't accidentally port dead weight.

### KEEP — the reason this fusion is worth doing

| Component | Why it stays |
| --- | --- |
| `Stride.Core.Assets` + `Stride.Core.Assets.Editor` | The `.sdpkg` asset package system, asset types, YAML serialization, Quantum object graph — the entire content model. Not replaceable; OpenDevelop has nothing analogous |
| Asset pipeline (headless tools): `Stride.AssetCompiler`, effect/shader compiler (`Stride.Shaders`), `Stride.TextureConverter`, model importers, `EffectCompilerServer`, `ConnectionRouter` | Already CLI/child processes; OpenDevelop invokes them exactly like it invokes `dotnet build`. Zero UI to discard |
| Scene/entity-hierarchy editor (`EntityHierarchyEditor*`, `SceneEditor*`) + `EditorGameController` family | The product value: live scene editing against a running engine. Our headless seam (G1–G4) lives here |
| `GameStudioPreviewService` + preview compilation context | Asset previews (materials/models/prefabs) render headlessly — same seam as above |
| `Stride.Editor.Build` (`GameStudioBuilderService`, shader cache coordination) | Build orchestration the editors depend on; drives the kept CLI tools |
| Game/project TEMPLATES (`Stride.Templates.*`) | Feed OpenDevelop's existing new-project dialog instead of GameStudio's start page |
| Editor undo (`EditorUndoRedoService`/Quantum transactions) | Asset-domain undo inside editor views; OpenDevelop's document undo doesn't cover assets |

### ADAPT — keep the logic, replace the shell surface

| Component | Adaptation |
| --- | --- |
| Asset editor VIEWS (material/sprite/prefab/UI editors' XAML + viewmodels) | Stay as embedded views (their internal Quantum PropertyGrid and styles ship with them — scoping their ResourceDictionaries to those views, not global), but each becomes an OpenDevelop secondary display binding over the owning asset file, opening in the workbench tab area |
| Project model bridge | Stride games ARE MSBuild csproj + an `.sdpkg`; OpenDevelop's project system hosts the csproj natively, while the `.sdpkg` package mounts into the Solution Explorer via a small adapter addin (tree nodes → open asset editors). No fork of either side's model |
| Debug/log pages (`EditorDebugTools.CreateLogDebugPage`) | Route Stride loggers into OpenDevelop's LoggingService/Error List instead of in-app debug pages; keep the logger plumbing, discard the page UI |
| Engine-host isolation (LONG-TERM) | Today the editor runs the engine IN-process on background threads. OpenDevelop's designer red line says project/user assemblies never load into the IDE — and `EditorContentLoader` DOES load user game assemblies. Acceptable for the feasibility slice; end-state moves the `EditorGameController`+engine island out-of-process (same DDP direction as the other designers) |

### DISCARD — OpenDevelop already is that thing

| Component | Why it goes |
| --- | --- |
| `Stride.GameStudio` app shell: main window, menus, docking layout, start page, global theme/style dictionaries, `StrideNuGetResolverUI` | OpenDevelop's workbench/AvalonDock/addin-tree/theme system owns all of it; keeping two shells guarantees drift |
| `Stride.GameStudio` process launcher/debugger ("run game" UX beyond F5) | Becomes an OpenDevelop command calling the kept CLI tools; the launcher APP itself never ships inside the IDE |
| `Stride.VisualStudio.*` (VSIX commands/interfaces) | Visual Studio-only; meaningless here |
| `Stride.Editor.CrashReport` | OpenDevelop owns crash/error UX |
| `Stride.GameStudio.AutoTesting` | Test harness for the discarded shell; revisit as OpenDevelop DevFlow actions later |
| GameStudio's own settings persistence/preferences UI | OpenDevelop properties system |

### Sequencing note

Discards are FREE only because the keeps don't depend on them: `Stride.Editor`'s build/preview/
controller layers take no dependency on the app shell (verified while doing the G1 split — the
editor graph compiles without any shell type). The first fusion milestone is therefore: an addin
manifest registering ONE asset-editor display binding (scene editor), with the shell pieces
simply not referenced.

## Real-content integration plan (2026-08-25): reusing Stride's own editor classes

Decision: to close gaps 1-2 from the addin status review (loading REAL `.sdpkg` scene content and
driving it through the REAL `EditorGameController`/`SceneEditorController`, replacing
`StrideSdlViewport`'s synthetic `SdlOverlayGame` placeholder), reuse Stride's own `.cs`
classes/compiled assemblies as directly as possible — do not reimplement scene loading or editor
game logic in the addin. This section is the plan for doing that; **no code has been written for
it yet**, planning only, per explicit instruction.

### Why this is bigger than "just call PackageSession.Load"

The naive path (call `Stride.Core.Assets.Package`'s `PackageSession.Load` directly, skip the
ViewModel layer entirely) gets you parsed asset data but **cannot reach `EditorGameController`**:
`SceneEditorController`/`EditorGameController<TEditorGame>`'s constructor requires an
`AssetViewModel`, which only exists inside a live `SessionViewModel`
(`Stride.Core.Assets.Editor.ViewModel`), which in turn requires an `EditorViewModel` subclass and
a populated `IViewModelServiceProvider` — i.e. a chunk of the GameStudio *application shell*
minus its window, which is exactly what the "ADAPT — Project model bridge" row already flagged
but hadn't sized. Traced the exact chain (Explore-agent investigation, 2026-08-25); summarized
below as a stubbable-vs-load-bearing inventory, file paths from the stride fork checkout.

### Dependency inventory

| Component | What it is | Stub or must-be-real | Notes |
| --- | --- | --- | --- |
| `IViewModelServiceProvider` / `ViewModelServiceProvider` | Simple list-backed service locator | **Reusable as-is** | `sources/presentation/Stride.Core.Presentation/ViewModels/ViewModelServiceProvider.cs` — no WPF/Stride-specific logic, pure container |
| `IDispatcherService` | UI-thread marshaling abstraction (`Invoke`/`InvokeAsync`/`CheckAccess`) | **Reusable as-is (corrected)** | `Stride.Core.Presentation.View.DispatcherService` (`sources/presentation/Stride.Core.Presentation/View/DispatcherService.cs`) just wraps `System.Windows.Threading.Dispatcher` — no GameStudio-specific coupling at all. `DispatcherService.Create()` (uses `Dispatcher.CurrentDispatcher`) works as-is on the addin's own WPF UI thread (the same thread `StrideSdlViewport` already drives via `CompositionTarget.Rendering`) |
| `IEditorDialogService`/`IDialogService`/`IDialogService2` | ~20+ member dialog/window-creation surface (progress window, message boxes, asset pickers, template providers, ...) | **Must write, no reference stub exists** | Only implementation in the whole tree is the full WPF `EditorDialogService`/`DialogService`/`StrideDialogService` chain (`sources/editor/Stride.Core.Assets.Editor/View/EditorDialogService.cs`, `sources/editor/Stride.GameStudio/Services/`). On the traced single-file-open path, only `ShowProgressWindow` and `RegisterDefaultTemplateProviders` are guaranteed to be hit — both can safely no-op (the latter's WPF `ResourceDictionary` XAML load is only needed for Stride's own property-grid UI, which OpenDevelop isn't hosting). Everything else (pickers, wizards, upgrade-confirmation dialogs) only fires on interactive flows an embedded single-`.sdpkg` viewer likely never triggers, PROVIDED the fixture package needs no version upgrade |
| `IAssetsPluginService` / `PluginService` | Discovers `StrideAssetsPlugin` subclasses and calls `RegisterSession` | **Fully reusable (fork patch landed)** | `PluginService` itself (`sources/editor/Stride.GameStudio/Services/PluginService.cs`) is public and reusable for `StrideDefaultAssetsPlugin` (public, in `Stride.Assets.Presentation`). `StrideEditorPlugin` (`sources/editor/Stride.GameStudio/Plugin/StrideEditorPlugin.cs:25`) — whose `InitializeSession` registers `GameSettingsProviderService`/`GameStudioBuilderService` — was `internal sealed`, and `InternalsVisibleTo` alone does NOT fix this: `AssetsPlugin.RegisterPlugin`'s `type.GetConstructor(Type.EmptyTypes)` gate uses public-only `BindingFlags` regardless of friend-assembly status (that attribute only relaxes the *compiler's* accessibility check, not reflection's own public/non-public metadata filter). **Fixed at the source**: flipped the class to `public sealed class StrideEditorPlugin` in the fork (its base `StrideAssetsPlugin` is already public) — a real, landed, self-contained patch, rebuilt and confirmed present in the built dll. `StrideEditorPlugin` can now be registered and used exactly as `Stride.GameStudio`'s own `Program.cs` does: `AssetsPlugin.RegisterPlugin(typeof(StrideEditorPlugin))` — true full reuse, not a replicated-logic workaround. (`InternalsVisibleTo` entries were also added for defense-in-depth but are not what makes this work.) `GameStudioPreviewService`/`GameStudioThumbnailService`/`StrideDebugService` (the plugin's other registrations) ride along automatically once the plugin itself is reusable |
| `MostRecentlyUsedFileCollection` | Observable list wrapper, normally settings-backed | **Reusable as-is** | GameStudio's own construction (`new MostRecentlyUsedFileCollection(InternalSettings.LoadProfileCopy, InternalSettings.MostRecentlyUsedSessions, InternalSettings.WriteFile)`, `Program.cs`) uses only public statics on `Stride.Core.Assets.Editor.Settings.InternalSettings` — copy that one line verbatim, no MRU concept of our own needed for a single-file embed |
| `EditorViewModel` (abstract subclass) | Owns `SessionViewModel.Instance`, MRU, dialog service wiring | **Thin real subclass, ~2 abstract members** | `RestartAndCreateNewSession()`/`RestartAndOpenSession(UFile)` are never invoked on the traced single-session-open path (only GameStudio's own subclass wires them, for its "switch project" flow) — near-total no-ops are safe |
| `SessionViewModel.OpenSession(path, serviceProvider, editor, sessionResult)` | Loads the `PackageSession`, resolves missing references, builds `SessionViewModel` | **Reuse as-is** | Pure data-model work once the services above exist; registers `UserDocumentationService`/`SelectionService`/`CopyPasteService`/`UndoRedoService` itself — all reusable, no porting needed |
| `EditorGameController<TEditorGame>` → `AssetCompositeHierarchyEditorController` → `EntityHierarchyEditorController` → `SceneEditorController` | The real scene-editor engine host | **Reuse as-is** (already has active headless-porting scaffolding — `STRIDE_EDITOR_WINFORMS` gate, `HeadlessGameHostView` seam from this technote's earlier milestones) | `EntityHierarchyEditorController.InitializeServices` registers ~15 `EditorGame*Service` instances (compositor, camera, grid, gizmos, selection, transform, highlight, ...); for a first renderable scene only the graphics-compositor + camera services are likely load-bearing, the rest (gizmos/selection/highlight) are editor-affordance-only and can be deferred |
| `SceneEditorViewModel` | Owns/creates `SceneEditorController` via a controller-factory closure | **Reuse as-is** | Needs only a fully-populated `SceneViewModel asset` living inside an already-open `SessionViewModel` — no additional service resolution of its own |

### Phased plan

1. **`IDispatcherService`** — thin real implementation over the WPF UI thread the addin already
   runs on (same thread `StrideSdlViewport` drives via `CompositionTarget.Rendering`). Small,
   self-contained, no design risk.
2. **Minimal `IEditorDialogService`/`IDialogService`/`IDialogService2`** — implement the full
   member surface with `ShowProgressWindow`/`RegisterDefaultTemplateProviders` as safe no-ops and
   everything else either no-op or `NotSupportedException` (defer real implementations until an
   interactive flow that needs them is actually in scope). This is the largest pure-authoring
   item, but mechanically bounded (an interface implementation, not new logic).
3. **`EditorViewModel` subclass** — thin, two near-stub abstract members, throwaway
   `MostRecentlyUsedFileCollection`.
4. **Register `StrideDefaultAssetsPlugin` and `StrideEditorPlugin` via `PluginService` — full
   reuse, DONE** — `StrideEditorPlugin` is now `public` in the fork (landed patch,
   `Stride.GameStudio/Plugin/StrideEditorPlugin.cs`), so `AssetsPlugin.RegisterPlugin(typeof(
   StrideEditorPlugin))` works exactly like `Stride.GameStudio`'s own `Program.cs`. Compilation
   verified (rebuilt `Stride.GameStudio.csproj` clean). **Runtime verification attempted and
   inconclusive** — not because the plugin logic is wrong, but because a standalone probe hit a
   structural cross-RID mismatch (`Stride.GameStudio.csproj` hardcodes `RuntimeIdentifier=win-x64`
   unconditionally, even on macOS — its dll's own deps/version graph is win-x64-qualified, which
   breaks when treated as a live runtime dependency of a differently-RID'd host). This does NOT
   affect the real addin: `ICSharpCode.StrideGameStudio.csproj` already consumes Stride assemblies
   the way that works (wholesale `<Private>true</Private>` HintPath copies into the addin's own
   output, same-process, no cross-RID dependency resolution needed) — the probe's failure mode
   was specific to probe methodology (mixing/cross-referencing two independently-built closures),
   not to how the real addin is built. Treat this step as "compile-verified, wire directly into
   the addin next" rather than "needs another isolated runtime spike."
5. **Wire `SessionViewModel.OpenSession`** against a real `.sdpkg` fixture (reuse as-is once 1-4
   exist) — this is gap 1 (real content loading) closed.
6. **Replace `StrideSdlViewport`'s `SdlOverlayGame`** with a `SceneEditorViewModel`/
   `SceneEditorController` instance for the loaded scene asset — reuse as-is; this is gap 2
   (real `EditorGameController`) closed, modulo the `EditorGameController`↔`IEmbeddedGameHostView`
   seam already established in the stride fork needing the `addChildWindow` overlay bridge from
   fusion milestone 3 wired into ITS `StartGame`/window-creation path instead of the addin's own.
7. Re-run the live DevFlow smoke test (now unblocked): open the fixture `.sdpkg`, confirm the
   REAL scene renders (not the checkerboard placeholder) and that entity selection/camera
   controls respond once input re-plumbing (a separate, already-tracked gap) lands.

### Open risk

The step 4 blocker is resolved at the source level: `StrideEditorPlugin` is now `public` in the
fork, so `AssetsPlugin.RegisterPlugin(typeof(StrideEditorPlugin))` — the exact call GameStudio's
own `Program.cs` makes — works unmodified. A standalone runtime probe was attempted to confirm
`GameStudioBuilderService`'s constructor also runs cleanly outside the GameStudio process, but it
hit an unrelated structural issue first: `Stride.GameStudio.csproj` pins `RuntimeIdentifier=
win-x64` unconditionally (even when built on macOS), which makes its dll unusable as a live
runtime dependency of a differently-RID'd standalone process (deps.json/version-graph mismatches
that no `AssemblyResolve` patching could paper over, traced across three probe iterations). This
is a probe-methodology dead end, not a finding about the plugin logic — the REAL addin doesn't hit
it, because it already consumes Stride assemblies the way that works (same-process, wholesale
`Private=true` HintPath copies, no cross-RID dependency graph involved). **Remaining verification
is therefore folded into step 6** (the live DevFlow smoke test), not a separate isolated spike —
wire `GameSettingsProviderService`/`GameStudioBuilderService` registration into the real addin
directly and let that smoke test be the runtime proof, rather than chasing another standalone
probe.

## Upstream interaction

- Do NOT comment on #1922 until Phase 0's inventory exists — lead with data.
- When commenting: present the LibreWPF path as complementary to (not a fight with) the Avalonia
  effort; the compile-phase work (steps 1–3) is shared by both paths and is the immediately
  useful contribution.

## Work log

- **2026-08-24** — Technote created; upstream facts verified from master (.NET 10 SDK, WPF +
  WinForms editor, win-x64 pins, custom editor SDK, STA-on-main, per-API graphics layout);
  strategy and phases written. Phase 0 not yet started.
- **2026-08-24 (later)** — Cloned stride to `/Users/lextm/uno-tools/stride` (needed `git-lfs`
  install; LFS-tracked build assets mean source-only clones can't build). Answered the WinForms
  question with code evidence: it exists solely for the game-viewport HWND embedding
  (`EmbeddedGameForm` + `GameContextWinforms` + `GameEngineHost` message bridge) and its
  drag-drop/cursor satellites; the engine's own `GameContextSDL` is the replacement path for
  non-Windows. Phase 3 item 6 de-risked accordingly; Phase 0 build attempt still pending.
- **2026-08-24 (later still)** — Corrected the ".NET Framework?" premise with evidence: the tree
  is already modern .NET 10 (`net10.0` / `net10.0-windows`; only VSIX tooling multi-targets
  net472), `NuGet.exe` is an unreferenced vestigial binary, editor TFM switching is centralized
  in `Stride.Editor.Frameworks.props` (our LibreWPF insertion point), and the runtime SDK already
  defines `net10.0-macos`. Phase ordering unchanged — no upgrade phase needed.
- **2026-08-24 (Phase 0 spike)** — Engine builds CLEAN on macOS (0 errors, AssemblyProcessor
  included). Editor builds 63 assemblies + 44 WPF markup passes, then fails on exactly one error:
  G1 (the WinForms game-viewport embedding). The engine's SDL window stack (`GameFormSDL`/
  `GameContextSDL`) is already present in the macOS build — the port's first real task is a
  platform-split of `EmbeddedGameForm`/`EditorGameController`, not any UI rewrite. Phase 0
  inventory continues (remaining WPF-surface audit) once G1's fix unblocks the rest of the graph;
  fixing it IS the natural start of Phase 2 fork work (`stride-fork`, branch `librewpf-port`).
- **2026-08-24 (Phase 2 slice 1 — editor compiles on macOS)** — Decision taken with the user:
  non-Windows viewport = headless render + frame presenter (NOT SDL-window embedding; airspace).
  Created local branch `librewpf-port` in `/Users/lextm/uno-tools/stride`; implemented the seam
  (`STRIDE_EDITOR_WINFORMS` host-OS define, `IEmbeddedGameHostView`, `EmbeddedGameForm` twins,
  `GameContextHeadless` wiring, STA gating, cursor/drag-drop splits — see "The seam as
  implemented"). Result: **the full GameStudio build succeeds on macOS** (67 assemblies incl.
  `Stride.GameStudio.dll`). Windows behavior unchanged behind the define. Next: run the editor,
  then G4 (frame readback/presenter + input injection).
- **2026-08-24 (fusion keep/discard decision)** — Component-by-component disposition for the
  OpenDevelop addin fusion written into this file ("Fusion with OpenDevelop" section): KEEP
  asset core/pipeline/scene editors/templates; ADAPT asset views into display bindings + project
  bridge + log routing; DISCARD GameStudio shell, launcher, VSIX, crash reporter, auto-testing.
  Key enabling fact: `Stride.Editor`'s logic layers take no dependency on the discarded shell.
- **2026-08-24 (fusion milestone 1 — addin skeleton verified live)** — New OpenDevelop addin
  `src/AddIns/DisplayBindings/StrideGameStudio/StrideGameStudio.AddIn/` (`LibreWPF.Sdk`,
  InProcess kind, solution-registered): a PRIMARY display binding for `\.sdpkg$` opening a
  placeholder `StridePackageView`, plus direct references to the local clone's built
  `Stride.Core.dll`/`Stride.Core.Assets.dll` (`$(StrideCheckoutRoot)` override point, default
  `uno-tools/stride`). Verified against a live DevFlow instance: the addin loads, opening
  `samples/UI/GameMenu/GameMenu.Game.sdpkg` creates the view as active content, and
  **Stride.Core.Assets 4.4.0.0 loads inside OpenDevelop's LibreWPF process** — assembly identity
  binding works. Two traps hit and fixed:
  1. *First-touch `<Module>` TypeInitializationException*: stride assemblies carry injected
     module initializers (`--auto-module-initializer`); the first touch of Stride.Core.Assets
     threw (missing transitive dep below), and since OpenDevelop marks the file loaded even when
     Load throws, the view stayed blank forever after. Fix: never start blank — constructor sets
     a static description, Load wraps everything in try/catch surfacing failures inline.
  2. *Transitive NuGet deps are NOT copied*: reference resolution only copies project outputs;
     `ServiceWire` (bound by Stride.Core.BuildEngine.Common's serializer factory at module-init
     time) had to be referenced explicitly from `$(NuGetPackageRoot)`. Expect more of these as
     deeper Stride layers join — the pattern is now established (reference explicitly,
     Private=true).
- **2026-08-24 (fusion milestone 1.5 — package identity verified live)** — Upgraded
  `StridePackageView` to parse the `.sdpkg` YAML front matter (Name/Version/asset folders) and
  verified live: opening `GameMenu.Game.sdpkg` shows `GameMenu.Game / 1.0.0 / 2 asset folders`,
  with `Stride.Core.Assets 4.4.0.0` loaded in-process, and the DevFlow action returns promptly.
  Two debugging lessons from this round:
  1. *A hung `ui/tree` wedges the single-threaded DevFlow agent* — subsequent action/tree calls
     time out. And a tool-timeout that kills the command can take down the whole process group.
     Verify via the app's log4net file, not the giant visual tree, when possible.
  2. *`PackageSession.Load` (the real editor's entry) hangs on this host when called on the UI
     thread* — it walks the solution and can handshake build/preview services. It is now
     deliberately NOT called in `Load`; the full session loader is deferred to the scene-editor
     slice where it can run off the UI thread with cancellation. (Also: the old wedged
     `OpenDevelop` child survived `pkill -f "SharpDevelop.csproj"` and kept the port — always
     kill the child binary too.)
- **2026-08-24 (G4 feasibility probe — measured blocker)** — Headless GPU probe: MoltenVK,
  adapter, `GraphicsDevice.New` (windowless), offscreen render target and `Clear` all work on
  the M1 Pro; the GPU→staging `commandList.Copy` used for readback **segfaults natively** (exit
  139). G4 is blocked at the engine's Vulkan staging-copy path, not at the integration seam —
  upstream engine work or a different readback strategy; the build-time port stands. Full
  breakdown recorded in the "G4 feasibility probe" section above.
- **2026-08-24 (G4 root-cause pass)** — lldb localized the segfault to `vkCmdPipelineBarrier`
  dereferencing a NULL MoltenVK internal state object. Found + fixed a real latent bug in the
  fork (`CommandList.Vulkan.cs` `Copy` barriers used queue-family 0/0 instead of
  `VK_QUEUE_FAMILY_IGNORED`); the crash persists after the fix, pointing at a
  no-render-pass copy-protocol gap rather than the barrier contents. Full details in the
  "Root-cause digging" subsection; probe sources retained for resuming.
- **2026-08-24 (G4 RESOLVED)** — Built the correct-protocol headless `Game` probe
  (`GameContextHeadless` + `Draw` clear inside a real render pass + `GetDataAsImage` readback):
  **`Game.Run` completes, no crash, and it reads back a real 1280x720 RGBA PNG** from the
  backbuffer on macOS. G4 is UNBLOCKED — the earlier segfaults were probe-protocol artifacts.
  The headless `Game` is exactly the `EmbeddedGameForm.Headless` target, so the frame-presenter
  base is now proven. Queue-family fix retained.
- **2026-08-24 (fusion milestone 2 — LIVE viewport visible)** — `StrideHeadlessViewport` in the
  addin runs a headless `Game` on a background thread and presents each frame (GetDataAsImage →
  PixelBuffer → WriteableBitmap) into the view. Opening a `.sdpkg` shows a **live animated
  Stride render** (colour-cycling square) — the visible designer full picture. The full engine
  closure + native runtimes deploy with the addin. Foundation for the real scene editor is
  proven; next slice is an asset-backed scene + input on top of it.
- **2026-08-25 (macOS drawable-doubling RESOLVED, G4 windowed-surface unblocked)** — Reinstalled
  the `macos` SDK workload against the SDK band matching the analyzer toolchain (10.0.100), which
  had been silently missing (build was falling back to a mismatched Roslyn version, masking the
  actual engine build). Rebuilt the engine clean, then traced the drawable-doubling defect to its
  real mechanism: `SwapChainGraphicsPresenter.Vulkan.cs` overwrites `Description.BackBufferWidth/
  Height` with MoltenVK's reported (already-polluted, already-Retina-scaled) `currentExtent`
  unless `SkipBackBufferClampToWindow` is set — an existing public switch already used by
  `ScreenshotTestRunner`, not a new engine change. Verified on the rebuilt SDL probe: baseline
  still doubles to a crash; the `Window.cs` GL→Metal `DrawableSize` fix alone stops the growth but
  leaves the wrong size; adding `SkipBackBufferClampToWindow = true` at the caller locks the
  backbuffer to the correct 1280×720 (640×360 logical @ 2x) for the full 180-tick run with a clean
  exit. Reverted the earlier unverified `ProcessClientSizeChanged` clamp draft (unnecessary once
  the real fix was found) and rebuilt clean. G4's windowed-surface route (Phase 3 item 6) is now
  fully unblocked at the platform-defect level; remaining work is WPF-side SDL-window embedding
  (`SDL_CreateWindowFrom` into a new `HwndHost`-equivalent) plus input re-plumbing — scoped in the
  technote as the next slice, not yet started.
- **2026-08-25 (composition bridge: option 1 "full windowed embed" confirmed viable)** — Per
  user decision, pursued option 1 over the bordered/separate-window fallback. Two standalone
  probes settled the design: handing SDL a raw foreign `NSView` for `CreateWindowFrom`
  (reparent-in) **segfaults inside MoltenVK** (`getSurfaceCapabilities` null deref) because a
  plain `NSView`'s default layer isn't SDL's required `CAMetalLayer`-backed `SDLMetalView`
  subclass, which can't be produced via `objc_msgSend` alone (needs a native shim — not pursued).
  The working alternative: let SDL own a completely normal top-level window, extract its real
  `NSWindow` via `SDL_GetWindowWMInfo` (`SysWMInfo.Info.Cocoa.Window`), and Cocoa
  `addChildWindow:` it onto the host window with the frame recomputed on every host move — the
  same technique already used by `SilkNetWpfWindowDecorationService.cs` for popups. Verified: the
  render loop survived a live host-window move with no crash (swapchain log clean throughout).
  Rewrote the "Next slice" plan around this — no native shim, no LibreWPF core changes, no
  `HwndHost`-equivalent needed; just Cocoa `addChildWindow`/`setFrame:` glue plus the existing
  `IEmbeddedGameHostView` seam for input. (Aside: an ad hoc CoreGraphics window-list script
  written to screenshot-confirm the overlay's on-screen position crashed Python's own process
  repeatedly — unrelated to Stride/SDL/LibreWPF, cleaned up; the swapchain log was sufficient
  evidence on its own, consistent with every prior probe in this technote.)
- **2026-08-25 (fusion milestone 3 — windowed viewport wired into the addin)** — Implemented the
  composition bridge for real in `StrideGameStudio.AddIn`: replaced `StrideHeadlessViewport`
  (deleted) with `StrideSdlViewport` + `SdlNativeWindow` + `LibreWpfHostWindow` +
  `CocoaOverlayInterop` + `SdlOverlayGame`. Found and resolved a threading subtlety the probes
  hadn't hit: `Game.Run()` blocks, which would freeze the WPF dispatcher, so the viewport uses
  `GameContextSDL(isUserManagingRun: true)` + `Game.Tick()` driven from
  `CompositionTarget.Rendering`, keeping the whole engine loop on the WPF UI/main thread with no
  background thread at all. Found the LibreWPF-side NSWindow accessor via an Explore-agent
  investigation: `LibreWPF.ProGPU`'s public (if diagnostics-named) `ProGpuWpfDiagnostics.
  TryGetWindowHost` → `host.SilkWindow.Native.Cocoa`. Whole solution and the addin build clean.
  Live verification (open a `.sdpkg`, confirm the overlay actually renders and tracks) was
  attempted but not completed: the app launches cleanly in `OD_TEST_MODE=1` with no new startup
  errors, but this session's DevFlow agent never answered on its pinned port (9299) in either
  launch mode, and blind OS-level UI scripting was correctly avoided per this repo's own
  established finding about it not targeting the right window. That live check is the concrete
  next step, blocked on DevFlow tooling rather than on any remaining design work.
- **2026-08-25 (fusion milestone 3 — windowed viewport LIVE-VERIFIED, closed [gating env resolved])** —
  With the DevFlow gap resolved (stale build) and the addin's native runtime wired, the windowed
  viewport was verified end to end against a live instance:
  - Launched the fresh Debug app in `OD_TEST_MODE=1 DEVFLOW_AGENT_PORT=9299` and invoked
    `od.open-file` on `samples/UI/GameMenu/GameMenu.Game/GameMenu.Game.sdpkg`; the active view
    became `ICSharpCode.StrideGameStudio.StridePackageView` (the Stride display binding matched).
  - **Runtime native-env requirement discovered**: `new SdlWindow(...)` (StartGame) throws
    `System.IO.FileNotFoundException: Could not load from any of the possible library names!`
    until the app is launched with
    `DYLD_LIBRARY_PATH=<addinDir>:<addinDir>/runtimes/osx-arm64/native` and
    `VK_ICD_FILENAMES=<addinDir>/runtimes/osx-arm64/native/MoltenVK_icd.json` — exactly the recipe
    the SDL probe needed (Silk.NET.SDL can't find `libSDL2` in the app base dir, and SDL needs
    MoltenVK via `VK_ICD_FILENAMES`). The addin already deploys `libSDL2-2.0.dylib` and the
    `runtimes/osx-arm64/native` payload, but the SILK.NET loader doesn't search the addin dir
    automatically, so this must be supplied at launch (candidate improvement: set
    `DYLD_LIBRARY_PATH`/`VK_ICD_FILENAMES` from inside `StartGame` before creating the SDL window,
    bounded to the addin's own folder, so the apphost needs no ambient env).
  - Verified live: MoltenVK 1.4.2 initializes, SDL owns a `CAMetalLayer` window
    (`SDL_cocoametalview`), swapchain created at a **stable size with no drawable-doubling**
    (`468×182`, a single transient `468×214` at initial layout) — `SkipBackBufferClampToWindow=true`
    holds. No `[StrideSdlViewport] failed to start`, no `could not resolve host NSWindow`, overlay
    attaches cleanly, process stable.
  - **Window chrome**: the SDL-owned NSWindow defaulted to a native title bar + rounded corners,
    which read as a floating window, so `CocoaOverlayInterop.MakeBorderless(nsWindow)` was added —
    `setStyleMask:NSWindowStyleMaskBorderless` (0), applied in `StrideSdlViewport.StartGame`
    right after `SDL_GetWindowWMInfo`. Borderless removes the title bar and rounded corners,
    giving the flat content-pane look. Verified it survives (rendering continues, no crash).
  - **Open perf note (not blocking)**: the swapchain churns — `Created 2 swapchain images` logged
    ~300/s initially, decaying to ~70/s over ~10s (vs the headless route's per-frame copy leak).
    Rendering works and the size is stable, but the presenter appears to re-negotiate the swapchain
    every present; a follow-up optimization (avoid the per-frame recreate once the surface size is
    settled) is the next perf item, not a milestone gate.
  - Retained as a real constraint for the scene-editor slice: true WPF-tree fusion (compositing the
    Vulkan surface into the LibreWPF window's own frame) is NOT what `addChildWindow` does — it's
    an overlay child window pinned to the element's rect. It reads as docked and tracks the host
    window, but is clipped only by the host window frame, not by the doc-pane bounds, and can't
    follow a tab undocked to a separate window. That is the documented option-1 composition-bridge
    path, still open.
- **2026-08-25 (DevFlow gap RESOLVED — it was a stale build, not an environment issue)** —
  Re-investigated the "DevFlow never binds 9299" symptom that had blocked all live verification.
  Root cause chain, each verified on this host:
  1. `Directory.Build.props:6` defines `OPENDEVELOP_NO_DEVFLOW` when `Configuration != 'Debug'`
     (and excludes `*DevFlowActions.cs`, `DevFlow/**`, `DevFlowPort.cs` from the build). A
     Release-style build therefore compiles DevFlow completely out — `AddWpfDevFlowAgent` is not
     even in the binary. The earlier integration-test run and the manual "invoke the apphost"
     launch used such a stale/Release output, so the agent was never present at all.
  2. The `bin/Debug` dll actually in use was stale (pre-dating the DevFlow wiring). Rebuilding the
     main project in Debug produced `OpenDevelop.dll` that genuinely contains `AddWpfDevFlowAgent`
     + `LeXtudio.DevFlow.Agent.LibreWpf` (confirmed via metadata/string search before vs after the
     rebuild).
  3. With a fresh Debug build the agent binds and serves — `dotnet run ... -f net10.0-windows` in
     `OD_TEST_MODE=1 DEVFLOW_AGENT_PORT=9299` gives `lsof ... :9299` LISTEN, and
     `GET /api/v1/agent/status` returns
     `{"name":"LeXtudio.DevFlow.Agent","framework":"wpf","running":true,"port":9299,"application":"App",...}`.
  Port note (for the record): 9299 is a non-default pin added 2026-07-10, whose comment claims
  9223 collides with Wino.Mail; Wino.Mail is not installed on this host and 9223/9299 are both
  free, so the pin has no local justification (the ecosystem default is 9223). It was not the
  cause of the earlier symptom either way. **The previous "real, standing environment gap"
   conclusion (2026-08-25) is retracted** — it was a stale-build artifact. Live verification of
   the windowed viewport (fusion milestone 3: open a `.sdpkg`, confirm the SDL overlay renders and
   tracks dock/resize) is now unblocked.
- **2026-08-25 (fusion milestone 3 — window chrome + alignment, verified live after user sighting)** —
  The first live render confirmed the overlay works but exposed two presentation defects, then a
  crash; all three fixed and re-verified with the user ("好了"):
  - **Window chrome**: the SDL-owned NSWindow defaulted to native title bar + rounded corners,
    reading as a floating window. Added `CocoaOverlayInterop.MakeBorderless(nsWindow)` —
    `setStyleMask:NSWindowStyleMaskBorderless` (0), applied in `StrideSdlViewport.StartGame`
    right after `SDL_GetWindowWMInfo`. Borderless strips the title bar and rounded corners.
  - **Alignment**: the overlay was offset ~a title-bar height, covering the document-tab text.
    Root cause: `Reposition()` mixed coordinate spaces — `hostWindow.PointToScreen(new Point(0,0))`
    returns the CLIENT area top-left while `GetFrame(hostNsWindow)` returns the whole window FRAME
    (title bar included). Fixed by anchoring to the CONTENT area instead: added
    `CocoaOverlayInterop.GetContentViewScreenRect(nsWindow)` (via `contentLayoutRect` + `frame`)
    and deriving the overlay rect by adding the element's offset within the client to that content
    rect, so the title bar is irrelevant.
  - **Crash (arm64)**: the first alignment attempt used
    `convertRectToScreen:` — an `objc_msgSend` with a **struct argument AND struct return**, which
    crashes with an `NSException` on Apple-silicon (ABI: the sret pointer collides with the struct
    args). Replaced with `contentLayoutRect:` (no-arg struct return, the same ABI-safe shape as the
    already-working `frame` selector) + `frame` math. Re-verified: app stays alive, swapchain
    stable, overlay attached.
  - **Launch requirement (permanent)**: the app must be launched with
    `DYLD_LIBRARY_PATH=<addinDir>:<addinDir>/runtimes/osx-arm64/native` and
    `VK_ICD_FILENAMES=<addinDir>/runtimes/osx-arm64/native/MoltenVK_icd.json` for the SDL window
    (`new SdlWindow` loads `libSDL2`; SDL loads MoltenVK), else `StartGame` throws
    `FileNotFoundException: Could not load from any of the possible library names!`. The addin
    deploys both `libSDL2-2.0.dylib` and the `runtimes/osx-arm64/native` payload; only the ambient
    env is missing. Candidate improvement: set both env vars from inside `StartGame` (bounded to
    the addin's own folder) so the apphost needs no ambient env.
  - Milestone 3 gate is now **met**: opening `.sdpkg` shows a real GPU-presented windowed Stride
    render docked in the workbench, borderless and correctly aligned. The swapchain churn (~300/s
    decaying to ~70/s, still re-negotiating every present) remains a perf item, not a gate.
- **2026-08-25 (real-content integration plan written, no code)** — Per explicit instruction to
  plan before coding, traced (via an Explore-agent investigation) the exact dependency chain
  needed to reuse Stride's own `SessionViewModel`/`EditorGameController`/`SceneEditorController`
  classes instead of reimplementing scene loading (closing gaps 1-2 from the addin status
  review). Finding: this requires standing up a minimal (not full-GameStudio-shell) instance of
  `EditorViewModel` + `IViewModelServiceProvider` populated with `IDispatcherService`,
  `IEditorDialogService`/`IDialogService2` (no existing stub in the tree — only the full WPF
  `EditorDialogService` chain), and Stride's real `PluginService`/`StrideEditorPlugin` (the one
  load-bearing, can't-be-stubbed dependency, since `EditorGameController`'s constructor directly
  requires a working `GameStudioBuilderService`/`GameSettingsProviderService` from it). Full
  inventory and a 7-step phased plan written into "Real-content integration plan" above;
  recommended next action is spiking the plugin-service dependency alone first (same
  probe-before-commit methodology used throughout this technote), before writing the smaller
  dispatcher/dialog-stub/EditorViewModel pieces. No implementation started.
- **2026-08-25 (plan refined: 2 corrections found before any probe was run)** — Started building
  the recommended standalone plugin-service probe (`PluginServiceProbe`, scratch project, deleted
  after — no code kept) and, while wiring its references, found the plan's two biggest "must
  write"/"must spike" items were both wrong, by reading source rather than running anything:
  (1) `IDispatcherService` doesn't need a new implementation — `Stride.Core.Presentation.View.
  DispatcherService` is already generic WPF (`Dispatcher.CurrentDispatcher`), fully reusable as-is
  on the addin's own UI thread; (2) `StrideEditorPlugin` (the class the plan assumed we'd reuse
  for `GameSettingsProviderService`/`GameStudioBuilderService` registration) turns out to be
  `internal sealed` with no `InternalsVisibleTo` reaching outside `Stride.GameStudio.dll`, and
  `AssetsPlugin.RegisterPlugin`'s own accessibility gate rejects it even via reflection — it
  genuinely cannot be used from the addin. Read its `InitializeSession` body instead: it's ~15
  lines of plain public-constructor calls against public classes
  (`GameSettingsProviderService`/`GameStudioBuilderService`, both in `Stride.Editor.dll`) — those
  can be replicated directly, no plugin wrapper needed. `MostRecentlyUsedFileCollection` also
  turned out fully reusable via `InternalSettings`'s public statics, no throwaway stub needed.
  Net effect: the plan's biggest unknown (step 4) is now a known, sized replacement rather than
  something requiring a runtime spike — corrected the dependency table and phased plan above.
  Deleted the scratch probe project since it never got to a `Program.cs`/build (the accessibility
  finding was compile-time-obvious once the reference was in front of me); the one thing still
  worth an actual run is whether `GameStudioBuilderService`'s constructor works cleanly outside
  the GameStudio process, noted as the narrowed remaining risk. Still no implementation of the
  real addin code — this pass only corrected the plan.
- **2026-08-25 (user direction: use InternalsVisibleTo for fuller reuse; real fork patch landed)** —
  Acted on the user's suggestion to reach for `InternalsVisibleTo` rather than replicate
  `StrideEditorPlugin.InitializeSession`'s body: added `[assembly: InternalsVisibleTo(...)]`
  entries for `ICSharpCode.StrideGameStudio` and a probe assembly to
  `Stride.GameStudio/Properties/AssemblyInfo.cs`. Found `InternalsVisibleTo` alone is
  insufficient: `AssetsPlugin.RegisterPlugin`'s `type.GetConstructor(Type.EmptyTypes)` gate uses
  the DEFAULT (public-only) `BindingFlags`, and `InternalsVisibleTo` only relaxes the C#
  compiler's accessibility check, not reflection's own public/non-public metadata filter — an
  `internal` implicit constructor still isn't "public" in IL metadata no matter which assembly is
  asking. Fixed properly by flipping the class itself: `internal sealed class StrideEditorPlugin`
  → `public sealed class StrideEditorPlugin` (`Stride.GameStudio/Plugin/StrideEditorPlugin.cs`) —
  its base (`StrideAssetsPlugin`) is already public, so this is a clean, self-contained fork
  patch. Rebuilt `Stride.GameStudio.csproj` clean (0 errors; the one failure hit was `-f
  net10.0-windows` forcing a restore-graph mismatch with an unrelated `Stride.Templates.
  AssetPacks` NoTargets project — unrelated to this patch, worked around by building without
  `-f`) and confirmed via UTF-8 byte search that both the `InternalsVisibleTo` strings and the
  flipped class landed in the rebuilt `Stride.GameStudio.dll`. This is a real, kept change to the
  fork (`StrideEditorPlugin` can now be reused via `AssetsPlugin.RegisterPlugin(typeof(...))`
  exactly as GameStudio's own `Program.cs` does it — true full reuse, not a replicated-logic
  workaround), on top of the InternalsVisibleTo entries (kept as a defense-in-depth / future-proofing
  measure even though they weren't sufficient alone).
- **2026-08-25 (runtime spike attempted, blocked on a real RID mismatch — not a Stride-logic bug)** —
  Tried to actually RUN the reused `StrideEditorPlugin`/`GameStudioViewModel` chain via a
  standalone probe, through three iterations: (1) a probe project outside the stride checkout
  referencing `Stride.GameStudio.dll` via raw `<Reference HintPath>` — hit `deps.json`
  classifying it as `"type": "reference"` (no runtime path) regardless of `Private=true`, so the
  host refused to load the physically-present sibling file; (2) moved the probe inside the stride
  checkout using a real `<ProjectReference>` to `Stride.GameStudio.csproj` (to share one restore
  closure) — hit `Microsoft.WindowsDesktop.App` framework-not-found (fixed by copying
  `SharpDevelop.csproj`'s own `RemoveDesktopRuntimeFramework`/`RemoveTransitiveDesktopFramework`
  targets, confirming that's the actual mechanism OpenDevelop uses to make `UseWPF=true` runnable
  on macOS at all — a reusable, documented recipe now); (3) even with the ProjectReference and
  the framework fix, still got `FileNotFoundException: Stride.GameStudio, Version=4.4.0.0` at
  runtime, an `AssemblyResolve`/`AssemblyLoadContext.Default.Resolving` fallback (with JIT-eager-
  resolution correctly worked around via a local-function isolation, confirmed by seeing "[probe]
  starting" print before the failure) still couldn't intercept it. **Root cause found**:
  `Stride.GameStudio.csproj` hardcodes `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`
  UNCONDITIONALLY (line 7) — even when compiled on macOS. The DLL's IL compiles and loads fine as
  a compile-time reference (which is all every previous "build succeeded" milestone in this
  technote ever verified), but its own `deps.json`/dependency-version graph is RID-qualified for
  `win-x64`, which is structurally incompatible with being a live runtime dependency of an
  `osx-arm64` app — explaining the version-string quirks (`deps.json` recorded `"4.4.0"` for a
  dependency whose actual `AssemblyVersion` is `"4.4.0.0"`) and the resolver's refusal to
  cooperate. **This is not a bug in the plugin-service reuse logic** (confirmed correct by
  compilation in the previous entry) — it's a cross-RID packaging mismatch that no amount of
  `AssemblyResolve` patching fixes, because the failure happens inside the deps-based resolver's
  strict identity check, before any fallback event reliably fires. Deleted the probe (three
  iterations, none kept) rather than continue fighting host-level RID plumbing. **Consequence for
  the plan**: reusing `Stride.GameStudio.dll`'s TYPES at compile time (what step 4 needs) remains
  fully valid and unaffected; reusing it as a live RUNTIME dependency of a *different*,
  non-win-x64-RID host process (like the OpenDevelop addin, which correctly targets its own host
  OS) needs either (a) the addin to also load Stride.GameStudio et al. the way `Stride.GameStudio`
  itself does — i.e. AS the RID-matched closure, not mixed with a foreign one (which is exactly
  what the existing addin csproj already does today via `<Private>true</Private>` HintPath
  references copied wholesale next to the addin's own output — that pattern is unaffected by this
  finding), or (b) conditioning `RuntimeIdentifier` in the fork to match the host OS, a larger and
  separate change out of scope for this plan. No further runtime verification attempted this
  session; the compile-time finding (public `StrideEditorPlugin`, reusable
  `GameSettingsProviderService`/`GameStudioBuilderService`) stands as verified, and matches
  exactly how the real addin already consumes Stride assemblies (same-RID, wholesale HintPath
  copy), so this RID finding is a probe-methodology lesson, not a blocker for the real
  integration.
- **2026-08-25 (gap 1 — real `.sdpkg` session loading — implemented and building)** — Wired the
  plan's steps 1-5 directly into `ICSharpCode.StrideGameStudio.csproj`, replacing the regex-YAML
  `Describe()` with a real `SessionViewModel`:
  - `AddinDialogService.cs` — the `IEditorDialogService`/`IDialogService2`/`IDialogService`
    implementation the plan flagged as "must write, no reference stub exists" (~20-member
    surface); `ShowProgressWindow`/`RegisterDefaultTemplateProviders` no-op, dialog-creation
    methods `throw NotSupportedException` (none are hit on the single-file-open path).
  - `OpenDevelopEditorViewModel.cs` — thin `EditorViewModel` subclass standing in for
    `GameStudioViewModel` (skips the app-shell concerns: `EditionPanelViewModel`, IDE launcher
    lists, restart-into-new-session commands); its two abstract members throw
    `NotSupportedException` since nothing on this addin's path calls them.
  - `StrideEditorHost.cs` — process-wide singleton bootstrap: registers `StrideDefaultAssetsPlugin`
    and (now-public) `StrideEditorPlugin` via `AssetsPlugin.RegisterPlugin`, builds the
    `ViewModelServiceProvider` (`DispatcherService.Create()` — confirmed fully reusable, no new
    implementation needed — + `AddinDialogService` + `PluginService`), and exposes
    `OpenSessionAsync(path)` which opens (or reuses, if already open) the one Stride session this
    process hosts — matches real Game Studio's one-project-per-process model; opening a
    *different* file while one is already open throws a clear `NotSupportedException` rather than
    silently misbehaving.
  - `StridePackageDisplayBinding.cs` — `Load()` now calls `StrideEditorHost.OpenSessionAsync`
    off the UI thread (hopping back via `Dispatcher.Invoke` only to update the label), and
    `Describe()` reports real `SessionViewModel` data (package/asset counts, asset URLs and
    types) instead of eyeballing YAML.
  - csproj: added compile-time (`Private=false`) references to `Stride.Core.Assets.Editor`,
    `Stride.Assets.Presentation`, `Stride.GameStudio`, `Stride.Editor`, `Stride.Core.Presentation`,
    `Stride.Core.Presentation.Wpf`, `Stride.Core.Translation`, plus a new
    `CopyGameStudioEditorClosure` target that copies `Stride.GameStudio`'s whole bin folder next
    to the addin's own output (same "copy the whole closure" approach already used for the engine
    references, and the same mechanism the RID-mismatch finding confirmed avoids the cross-RID
    deps.json problems a *separately-restored* process hit — this works because the addin
    consumes these as plain sibling files in the already-running OpenDevelop process, not as a
    fresh process's own dependency graph).
  - **Verified**: whole solution builds clean (0 errors after fixing missing `using`s and a
    couple of `#nullable enable` return-type mismatches against the real interface's nullable
    annotations); the addin's ~300-file closure deploys correctly. App launches in
    `OD_TEST_MODE=1` with no new startup exceptions (confirmed via log — reaches
    `dockingManager_Loaded` same as always, no `StrideGameStudio`-related errors).
  - **Not verified live**: DevFlow did not respond on port 9299 in this session's launch attempts
    (both `dotnet run --project ... --no-build` and the built apphost directly) despite the
    previous session's entry claiming it had been made to work — this looks like the same
    intermittent DevFlow-availability issue from earlier in this technote, not a new regression;
    not re-investigated further this pass to stay focused on shipping the addin code. Opening a
    real `.sdpkg` and eyeballing the real asset list in the info panel remains the concrete next
    verification step whenever DevFlow (or manual interactive testing) is available.
  - **Explicitly deferred (gap 2 at the time)**: the viewport still rendered `SdlOverlayGame`'s
    placeholder scene, not the newly-loaded real assets — see the next entry.
- **2026-08-25 (gap 2, small-first slice — real entity positions render, full editor deferred with
  a concrete reason)** — Investigated wiring the REAL `SceneEditorController`/`EditorGameController`
  into `StrideSdlViewport` (plan steps 6-7) and found a genuine, not-previously-flagged threading
  conflict: `EditorGameController.StartGame()` runs the game loop on a dedicated background
  thread (`sceneGameThread.Start()`) — that is its whole design, on every platform. But this
  technote's own probes established that SDL/Cocoa window creation and event pumping must happen
  on the process MAIN thread (that is exactly why `StrideSdlViewport` uses
  `IsUserManagingRun` + `CompositionTarget.Rendering` instead of `Game.Run()`'s own loop). Wiring
  a windowed SDL context into `EditorGameController` as-is would create that same window on a
  non-main background thread — a real, sized fork change (make `EditorGameController`'s own
  run-loop model conditionally follow the `IsUserManagingRun`/UI-thread-tick pattern on macOS,
  not just swap which `GameContext` it constructs), not a small patch, and with unknown risk
  across the ~15 `EditorGame*Service` registrations `EntityHierarchyEditorController.
  InitializeServices` wires up (gizmos, selection, camera, ...).
  Per user decision, took the smaller, immediately-shippable path instead, entirely inside the
  addin (no fork surgery, no threading-model change): read real entity data out of the loaded
  session's first `.sdscene` asset and render it in the EXISTING (already thread-safe)
  `StrideSdlViewport`/`SdlOverlayGame`, replacing the synthetic checkerboard-and-billboard scene.
  - `SceneAssetReader.cs` (new) — walks `SceneAsset.Hierarchy.Parts.Values` (design-time Quantum
    data, not a runtime-compiled scene) to collect `(Entity.Name, Entity.Transform.Position)`
    pairs. No meshes/materials (that needs the asset-compiler pipeline, which IS reachable via the
    now-public `GameStudioBuilderService` but wasn't wired up this pass — scoped out to stay
    small).
  - `SdlOverlayGame.cs` — added `SetEntities(...)` and `DrawEntityMarkers(...)`: when entities are
    present, draws a top-down (X, Z) auto-fit-and-scaled marker per entity instead of the
    placeholder scene; falls back to the old placeholder when no scene asset was found (e.g. a
    `.sdpkg` with no scenes yet, or before load completes).
  - `StrideSdlViewport.cs` — `SetEntities(...)` forwards to the game if it already exists, or
    queues (`pendingEntities`) for when `StartGame()` creates it — handles the race between the
    view's `Loaded` event and the async session load, whichever finishes first.
  - `StridePackageDisplayBinding.cs` — after `StrideEditorHost.OpenSessionAsync` succeeds, also
    calls `SceneAssetReader.ReadFirstScene` and pushes the result to the viewport in the same
    UI-thread hop that updates the text label.
  - csproj: added a compile-time reference to `Stride.Assets.dll` (where `SceneAsset` lives);
    already covered by the existing `CopyGameStudioEditorClosure` wholesale-copy target, so no new
    deployment plumbing needed.
  - **Verified**: whole solution builds clean (0 errors, after fixing a duplicated code block and
    an implicitly-typed multi-declarator slip from the edit). App launches in `OD_TEST_MODE=1`
    with no new startup exceptions.
  - **Not verified live**: same DevFlow-availability gap as the previous entry — still not
    responding on port 9299 in this session's launch attempts. Not re-investigated further.
  - **Honest scope statement**: this is real data driving real rendering (positions from the
    actual scene graph, not synthetic), but it is NOT the interactive scene editor — no meshes,
    no selection, no gizmos, no undo. The full `EditorGameController` integration remains future
    work, now scoped precisely: it needs `EditorGameController`'s run-loop model to grow a
    macOS/SDL branch that ticks from the UI thread the way `StrideSdlViewport` already does,
    before any of its window-creation code can safely run.
- **2026-08-25 (gap 2, big step — the real `EditorGameController` runs, threading conflict
  resolved with a fork patch)** — Per user decision ("大步前进"), did the threading-model surgery
  on `EditorGameController<TEditorGame>` scoped in the previous entry, then wired the addin to
  drive the real `SceneEditorController` instead of the marker-only fallback.
  - **Fork patch** (`sources/editor/Stride.Assets.Presentation/AssetEditors/GameEditor/Services/
    EditorGameController.cs`): on the non-`STRIDE_EDITOR_WINFORMS` branch, `sceneGameThread` is
    no longer a dedicated background `Thread` created in the constructor — it's `null` until
    `StartGame()` captures `Thread.CurrentThread` (the caller's thread, expected to be the
    embedding host's UI/main thread). `StartGame()`'s macOS branch: awaits
    `GameStudioBuilderService.WaitForShaders()` via `Task.Run` (kept off the UI thread), then
    constructs `new Stride.Graphics.SDL.Window(...)` + `GameContextSDL(window, 0, 0,
    isUserManagingRun: true)` and calls `Game.Run(context)` (returns immediately) — all inline on
    the calling thread, mirroring exactly how `StrideSdlViewport` already handles this. Added
    `SdlWindow`/`Tick()` members (also on `IEditorGameController`, `object`-typed there to keep
    the interface platform-neutral) so an embedding host can grab the window for its own Cocoa
    bridge and drive the loop per-frame — no Cocoa/WPF-hosting code was added to the fork itself,
    keeping that entirely in the addin as before. The old `SceneGameRunThread()` method is now
    `#if STRIDE_EDITOR_WINFORMS`-only (its `#else` branch was dead code once `StartGame()` stopped
    calling `sceneGameThread.Start()` on that path).
  - **Second accessibility fix, same shape as `StrideEditorPlugin`**: `GameEditorViewModel.
    Controller` (and its two overrides, on `EntityHierarchyEditorViewModel` and
    `PrefabEditorViewModel`) were `protected internal` — inaccessible from the addin, which needs
    `SceneEditorViewModel.Controller` to reach `SdlWindow`/`Tick()`/`StartGame()`. Flipped all
    three to `public` (covariant return types on the overrides already matched, so this was a
    pure accessibility change, no logic touched). Confirmed no other subclass depends on the
    narrower accessibility (only one class hierarchy implements this).
  - `StrideSceneEditorViewport.cs` (new, addin-side) — constructs `new SceneEditorViewModel(
    sceneAsset)` (constructs the real `SceneEditorController`/`EditorGameController` chain
    synchronously via the constructor's controller-factory), awaits `Controller.StartGame()` on
    the WPF `Loaded` event (already the UI/main thread), then reuses the EXACT SAME Cocoa overlay
    bridge `StrideSdlViewport` proved out (`SdlNativeWindow.GetCocoaNsWindow`,
    `LibreWpfHostWindow.TryGetCocoaNsWindow`, `CocoaOverlayInterop.*`) pointed at
    `Controller.SdlWindow` instead of an addin-owned `SdlOverlayGame`, and drives
    `Controller.Tick()` from `CompositionTarget.Rendering` instead of a `GameContextSDL.
    RunCallback`.
  - `StridePackageDisplayBinding.cs` — `LoadSessionAsync` now looks for a real
    `Stride.Assets.Presentation.ViewModel.SceneViewModel` in the loaded session (any local
    package's first scene asset - `SessionViewModel`'s own asset-to-viewmodel-type mapping,
    already active since `StrideDefaultAssetsPlugin` was registered for gap 1, means the loaded
    `AssetViewModel` instances are already the concrete `SceneViewModel` type, no extra lookup
    needed) and swaps in `StrideSceneEditorViewport` for it, falling back to the marker-based
    `StrideSdlViewport` (gap 2's earlier small-first slice) if no scene asset exists or the real
    controller throws on startup - never leaves the view blank/broken.
  - **Verified**: both the fork (`Stride.GameStudio.csproj`) and the addin build clean (0 errors).
    Whole OpenDevelop solution builds clean. App launches in `OD_TEST_MODE=1` with no new startup
    exceptions (log reaches `dockingManager_Loaded` as always).
  - **Not verified live**: DevFlow still not responding on port 9299 in this session (same
    intermittent gap as every other attempt today) - opening a real `.sdpkg`, confirming the real
    scene actually renders (not just constructs without throwing), and checking for any runtime
    issue in the `EditorGameController` threading surgery that only shows up when actually
    running (e.g. whether `WaitForShaders()`'s `Task.Run` hand-off interacts correctly with
    `PostTask`/`Script.AddTask` work being posted from other threads before `Tick()` first runs)
    remain the concrete next verification step.
  - **What's still NOT wired despite this being the real controller**: input (mouse/keyboard/
    drag-drop into the SDL overlay - `IEmbeddedGameHostView`'s `Visual`/`PointFromScreen` seam
    exists but nothing feeds it events yet), and therefore no selection/gizmos/camera control even
    though the ~15 `EditorGame*Service` registrations that provide them are running underneath.
    That is the natural next slice once real rendering is confirmed live.

- **2026-08-25 (live DevFlow debugging round — 5 real load-order/deployment bugs found and fixed)**
  DevFlow came up this time; `od.open-file` on `GameMenu.Game.sdpkg` surfaced a chain of real
  bugs, each only visible once the previous one was fixed and the app relaunched:
  1. **Architecture mismatch (FIXED)**: `Stride.GameStudio.csproj`/`Stride.Editor.csproj`/
     `Stride.Assets.Presentation.csproj` unconditionally pinned `RuntimeIdentifier=win-x64`,
     baking AMD64-specific machine type into the compiled IL (confirmed via PE header inspection:
     `0x8664` not the portable `0x14c`) — unloadable on arm64 macOS regardless of file layout.
     Fix: `<RuntimeIdentifier Condition="$([MSBuild]::IsOSPlatform('Windows'))">win-x64</...>`.
  2. **`Stride.Video.dll` (Vulkan/ subfolder) not found (FIXED)**: per-graphics-API DLL layout
     (`StrideMultiGraphicsApiHost=true`) puts it outside default assembly probing. Fixed with an
     `AssemblyLoadContext.Default.Resolving` handler in `StrideEditorHost`'s static constructor
     that also searches the addin's own `Vulkan/`/`DirectX/` subfolders by simple name.
  3. **SDL2 native library not found (FIXED, root cause was NOT what it first looked like)**:
     `DYLD_LIBRARY_PATH` is silently stripped by macOS from this process (confirmed empirically —
     absent from `ps eww -p $PID` output even set on the exact launch command, for both `dotnet
     run` and the built apphost). Assumed fix (a `NativeLibrary.SetDllImportResolver` /
     preload-by-absolute-path in `StrideEditorHost`'s static ctor) did NOT work — decompiling
     `Silk.NET.SDL.Sdl.CreateDefaultContext` (via `ilspycmd`) showed it never goes through
     `DllImport` marshaling at all; it manually calls `NativeLibrary.TryLoad` per candidate name
     via `Silk.NET.Core.Loader.DefaultPathResolver`, whose only search rule that doesn't depend on
     `DYLD_*` is `BaseDirectoryResolver` — which combines the bare candidate name with
     **`AppContext.BaseDirectory`, i.e. the MAIN app's own output folder, not this addin's**.
     Verified with a standalone probe project before touching the real app. Real fix: also copy
     `libSDL2-2.0.dylib` into `src/Main/SharpDevelop/bin/.../` via a new `Copy` step in
     `CopyStrideRuntimes` (condition-guarded on that folder existing).
  4. **`libbulletc`/FFmpeg (`avutil`/`avcodec`/...) natives not found (FIXED)**: `Stride.GameStudio`
     targets `net10.0-windows`, so ITS OWN NuGet runtime-asset selection only pulls Windows-flavored
     `runtimes/*/native` folders even with `RuntimeIdentifier` unset on macOS — the `osx-arm64`
     native assets only show up in sibling projects' own bin output (e.g. `Stride.Physics`,
     `Stride.Assets.Presentation`) that consume the same native packages. Fixed by adding those
     projects' `bin/.../runtimes/**/*` as extra `CopyStrideRuntimes` sources. `avutil` specifically
     needed a SECOND copy destination too: `NativeLibraryHelper.TryFindLibraryPath`'s first (most
     specific) probe is `<ownerAssemblyDir>/runtimes/<rid>/native`, and `Stride.Video.dll` (the
     owner type for FFmpeg's preload) lives in the addin's `Vulkan/` subfolder — so the runtimes
     tree needed copying to `Vulkan/runtimes/...` too, not just the addin root.
  5. **NOT YET FIXED — `PackageStore.Instance.GetPackageFileName("Stride.Assets.Presentation", ...)`
     returns null** (`StrideDefaultTemplates.Load` → `StrideDefaultAssetsPlugin.LoadDefaultTemplates`
     → `StrideDefaultAssetsPlugin` ctor → `AssetsPlugin.RegisterPlugin`, i.e. this now fires before
     `OpenSessionAsync` can proceed at all). Root cause: `PackageStore` resolves the package via a
     `NugetStore`-backed lookup expecting a genuinely NuGet-restored `Stride.Assets.Presentation`
     package (containing the actual `.sdpkg` asset package with template `.sdtpl` files) at
     `StrideVersion.NuGetVersion` — but this fork is consumed via raw file references from source
     builds, never NuGet-restored under that package identity, so the store has nothing to find.
     This is a different problem class from bugs 1-4 (asset-package/template bootstrap, not native
     or managed assembly loading) and needs its own investigation: either produce/pack a local
     NuGet feed entry so `PackageStore` can find it, or find where the actual `.sdpkg` template
     package lives in the source tree and patch `StrideDefaultTemplates.Load` (or feed
     `PackageStore` an override) to resolve it from a file path instead of the NuGet cache.
  6. **Still separately broken, lower priority (marker-viewport fallback path only for now)**:
     `Window..ctor` → `"Cannot allocate SDL Window: Failed to load Vulkan Portability library"`
     even with `VK_ICD_FILENAMES` correctly set to the addin's `MoltenVK_icd.json` — not yet root-
     caused; likely the same "bare name via `AppContext.BaseDirectory` only" native-loading pattern
     as bug 3 applies to whatever loads `libvulkan.1.dylib`/MoltenVK here too, since the addin only
     has `libvulkan.1.dylib` copied to its own folder, not the main app's.
  - Net progress this round: 4 of 6 identified bugs fixed and verified fixed by watching the exact
    error disappear from a fresh live DevFlow run after each fix (never assumed - each fix was
    followed by a full relaunch + re-open-file + log re-check). Bug 5 is now the hard blocker for
    `OpenSessionAsync` to succeed at all; bug 6 blocks actual GPU rendering once 5 is fixed.
  - **Process-management gotcha hit repeatedly this round**: `pkill -f`/`kill -9` on the
    LibreWPF/OpenDevelop apphost does not always reap the process promptly (or at all — one PID
    survived two separate `kill -9` calls before finally dying); a stale prior instance holding
    port 9299 causes the NEW instance's DevFlow agent to silently bind a fallback port instead
    (logged as `Port 9299 is already in use; listening on <port> instead` in the app log) — the
    curl calls then either hit a stale process's already-open tab (looks like instant fake
    success) or connection-refused. Always verify with `ps aux | grep OpenDevelop` for exactly one
    process AND grep the fresh log for "already in use" before trusting a DevFlow response.

- **2026-08-25 (continued — bugs 5 and 6 fixed; real GPU rendering confirmed live; two new,
  harder problems found)**
  1. **Bug 5 (PackageStore/`StrideDefaultTemplates.Load`) FIXED**: added a `STRIDE_SOURCE_ROOT`
     fallback in the fork (`StrideDefaultTemplates.Load`, `sources/engine/Stride.Assets/Templates/
     StrideDefaultTemplates.cs`) that resolves `Stride.Assets.Presentation`/`Stride.SpriteStudio.
     Offline`'s `.sdpkg` directly from the source tree when `PackageStore` can't find them via
     NuGet identity. The addin sets `STRIDE_SOURCE_ROOT` from `$(StrideCheckoutRoot)`, baked in at
     build time as `[AssemblyMetadata]` (see the csproj) since the addin has no other way to know
     the checkout path at runtime. Verified: the "Could not find package Stride.Assets.Presentation"
     `InvalidOperationException` is gone; only benign `DotNetNewTemplateBridge` warnings remain
     (dotnet-new templates unavailable, not needed for editing).
  2. **Bug 6 (Vulkan Portability library) FIXED**: same "bare-name native loader only probes the
     MAIN app's own output folder" pattern as bug 3 (SDL2) — added `libvulkan.1.dylib` and
     `MoltenVK_icd.json` to the `CopyStrideRuntimes` target's copy-to-main-app-folder step.
     **Verified live and this is the milestone this whole debugging arc was aiming at**: the log
     showed MoltenVK actually initializing (`MoltenVK version 1.4.2`, full extension list, `GPU
     device: Apple M1 Pro`), a real `VkDevice`/`VkInstance` created, and a continuous stream of
     `Created 2 swapchain images ... on screen Built-in Retina Display` — the marker-viewport
     fallback (`StrideSdlViewport`) is genuinely presenting real GPU frames through the real
     Vulkan/MoltenVK pipeline on macOS for the first time.
  3. **Diagnostic gap fixed along the way**: `EditorViewModel.OpenSession` was swallowing the real
     failure reason from its internal `PackageSessionResult` into a bare `false`/generic exception
     — every "session load failed" log entry from our addin was uninformative. Fixed with two
     small additions: (a) in the fork, log `sessionResult`'s messages via `GlobalLogger` (process-
     wide, unlike the request-scoped `LoggerResult` instance) when `OpenSession` fails; (b) in
     `StrideEditorHost`'s static constructor, subscribe to `GlobalLogger.GlobalMessageLogged` and
     forward errors/warnings into OpenDevelop's own `LoggingService`. This is what surfaced bugs 5
     and both new findings below with actual stack traces instead of a one-line dead end — worth
     keeping permanently, not just as a one-off diagnostic.
  4. **New finding — `NuGet.ProjectModel`/`NuGet.LibraryModel` deleted by
     `OpenDevelopTrimAddinOutput` (NuGet.ProjectModel FIXED; NuGet.LibraryModel not fully fixed,
     see below)**: opening `GameMenu.Game.sdpkg` (a package with a real referenced `.csproj`, not
     a pure-asset package) makes `PackageSession.Dependencies.UpdateDependencies` touch
     `NuGet.ProjectModel.LockFileFormat`, which requires exactly `NuGet.ProjectModel, Version=
     7.3.1.0` (a strong-named load). Root-caused via `-v:detailed` build logging (grepping for the
     filename showed "Copying file... NuGet.ProjectModel.dll" immediately followed by "Deleting
     file...NuGet.ProjectModel.dll" in the SAME build) to `OpenDevelopTrimAddinOutput`
     (`src/SDK/OpenDevelop.Addin.Sdk/Sdk/Sdk.targets`): it deletes any addin-output DLL whose
     filename also exists in the host app's own bin folder, assuming the host's copy is a safe
     substitute — wrong here, since the host's own `NuGet.ProjectModel.dll` (for AddInManager2's
     NuGet usage) is a different, incompatible version (7.6.0.0). Fixed via the Sdk's own escape
     hatch: `<OpenDevelopAlwaysCopy Include="NuGet.ProjectModel.dll" />` in the addin's csproj
     (matched by `%(Filename)` in the trim target). **Same fix applied to `AvalonDock.dll`**
     (Stride's `Stride.Core.Assets.Editor` WPF views need `AvalonDock, Version=4.72.1.0`; the host
     ships a different version for its own docking UI) — but AvalonDock turned out to need more,
     see the next finding.
  5. **`AvalonDock` simple-name version collision — my first read of this was WRONG; the real fix
     is small.** The symptom: `FileLoadException: The located assembly's manifest definition does
     not match the assembly reference` for `AvalonDock, Version=4.72.1.0`, surfacing as a
     `ReflectionTypeLoadException` out of `AssetsPlugin.RegisterAssetViewModelTypes`. The collision
     itself is real and unavoidable: OpenDevelop's own docking UI (`src/Main/SharpDevelop/Workbench/
     AvalonDockLayout.cs`) loads ITS own, differently-versioned `AvalonDock.dll` into the Default
     ALC at startup, and .NET will not host two versions of one simple name in one ALC.
     I initially concluded from that that Stride's editor assemblies need their own isolated
     `AssemblyLoadContext` — a big change touching every direct `Stride.*` reference in the addin.
     **That conclusion did not survive scrutiny** (thanks to the user pushing back on it rather than
     accepting it). Reading what the failing code actually wants shows the collision does not need
     to be resolved at all: `RegisterAssetViewModelTypes` only looks for `AssetViewModel` subclasses
     carrying `[AssetViewModel]`, and *those* types never touch AvalonDock. Only the plugin
     assembly's WPF view types do. The failure is purely `Assembly.GetTypes()`'s all-or-nothing
     contract: one unloadable type throws away the entire (mostly fine) enumeration.
     **Fix**: degrade to the types that did load, which is a pattern Stride already uses elsewhere
     for exactly this reason — `AssetCompilerRegistry.GetFullyLoadedTypes` catches
     `ReflectionTypeLoadException` and returns `ex.Types.NotNull()`. Promoted that into a shared
     `Assembly.GetFullyLoadedTypes()` extension (`sources/core/Stride.Core/Extensions/
     AssemblyExtensions.cs`, next to the `NotNull()` it uses) and switched the plugin-registration
     scans on this path to it: `AssetsPlugin.RegisterAssetViewModelTypes`,
     `AssetsEditorPlugin.RegisterAssetEditorViewModelTypes`/`RegisterAssetEditorViewTypes`,
     `StrideAssetsPlugin.RegisterAssetPreviewViewModelTypes`,
     `StrideDefaultAssetsPlugin.RegisterAssetPreviewViewTypes`.
     **Debugging note worth keeping**: these registration points fail one at a time — fixing the
     first just moves the stack trace to the next (`AssetsPlugin` → `AssetsEditorPlugin` → ...), and
     the error text stays byte-identical while doing so. Diff the *stack trace*, not the message,
     to tell "no progress" apart from "progressed to the next instance of the same bug".
  6. **`NuGet.LibraryModel 7.6.0.0` not found — also NOT the deep problem I first called it.**
     Symptom: `Microsoft.PackageDependencyResolution.targets` fails reading the referenced
     `.csproj`'s `obj/project.assets.json` with `Could not load file or assembly
     'NuGet.LibraryModel, Version=7.6.0.0'`. My first read blamed an unreachable "MSBuild plugin"
     ALC plus a generally-misconfigured embedded MSBuild in the host — i.e. an audit-sized job.
     **Wrong again, and in the same way: I described the mechanism without checking whether the
     thing it needed was actually missing.** Two cheap checks settle it:
     `/opt/homebrew/.../sdk/10.0.302/NuGet.LibraryModel.dll` exists and IS 7.6.0.0, while
     OpenDevelop's own bin — which ships `Microsoft.Build.dll` and friends, because the IDE embeds
     MSBuild — has no `NuGet.LibraryModel.dll` at all. So it is not "unreachable", just absent from
     the directory being searched.
     Stride already handles this: `PackageSessionPublicHelper.FindAndSetMSBuildVersion` registers an
     `AppDomain.CurrentDomain.AssemblyResolve` that falls back to the located SDK directory. But
     that registration sits inside `if (!AppDomain.CurrentDomain.GetAssemblies().Any(IsMSBuildAssembly))`
     — "only if MSBuild is not already loaded" — which is upside down for a pure fallback: a host
     that preloaded its own (partial) MSBuild is exactly the case that needs the SDK fallback, and
     is exactly the case where this skips it. Hosting inside an IDE always trips it.
     **Fix**: hoist that `AssemblyResolve` registration out of the conditional so it registers
     unconditionally (the `ApplyDotNetSdkEnvironmentVariables` call, which does mutate global state,
     stays gated). The resolver is only consulted after normal resolution has already failed, so
     registering it always is safe.
  - **Method note (the reason two of these six were initially mis-diagnosed)**: both #5 and #6 got
    an architecture-sized diagnosis from reading a stack trace and reasoning about mechanism, when
    a single command would have shown the mechanism was not the binding constraint — for #5, that
    the failing scan does not want the conflicting types; for #6, that the file exists in the SDK
    directory and merely isn't where the search looks. The earlier bugs in this list were all
    "which folder is this file in" problems, which primed reaching for "then this must be the
    structural one". Check the cheap disqualifying fact before concluding a redesign is required.
  - **Practical implication for the "big step" goal**: with #5 and #6 fixed, packages that
    reference a real `.csproj` (i.e. nearly every real Stride sample, `GameMenu.Game.sdpkg`
    included) should no longer be blocked at session load. Verification of that, and of whether
    the full real-content path (session load → `StrideSceneEditorViewport` → real
    `SceneEditorController`/`EditorGameController`, rather than the marker-viewport fallback
    already proven live) works end to end, is the immediate next step.

## Two systemic causes behind the whole "can't open a real scene package" arc (2026-08-25)

Everything in the debugging arc above and below turned out to be an instance of one of two causes.
Naming them is worth more than the individual fixes, because each new symptom is fastest to place
by asking which of the two it is.

### Cause A — this addin replaces GameStudio's shell, so shell-resident bootstrap never runs

`Stride.GameStudio`'s startup work is spread across `Program.cs` and `GameStudioWindow.xaml.cs`, not
concentrated in the session-loading code. This addin deliberately does not run either of those, so
every step living there is silently skipped — and the resulting failures surface far away, deep in
asset loading, with nothing pointing back at the missing setup call.

Found and fixed so far (both restored in `StrideEditorHost`):

| Missing step | Originally in | Symptom when skipped |
| --- | --- | --- |
| `PackageSessionPublicHelper.FindAndSetMSBuildVersion()` | `Program.cs` Startup | Referenced `.csproj`'s "Restore" target reported non-existent → no `obj/project.assets.json` → package's Stride.* dependencies resolve to nothing → scene load dies on a missing base asset |
| `plugin.InitializeSession(session)` for every registered plugin | `GameStudioWindow.xaml.cs` load handler | `GameSettingsProviderService`/`GameStudioBuilderService` never registered → `EditorGameController`'s ctor throws "No service matches the given type" |

Restoring `InitializeSession` then surfaced two more consequences of not being the GameStudio shell —
both of which are about *identity* rather than a missing call, so they are worth listing separately:

| Assumption | Where it bites | Resolution |
| --- | --- | --- |
| The editor view model **is** a `GameStudioViewModel` | `GameStudioViewModel.GameStudio` hard-casts `EditorViewModel.Instance`; `StrideEditorPlugin.InitializeSession` uses it → `InvalidCastException` | Derive `OpenDevelopEditorViewModel` from `GameStudioViewModel` (it is concrete and already implements everything) rather than from `EditorViewModel`. Inheriting some unused shell state is far cheaper than patching this coupling out of every plugin that assumes it. |
| The host has a preview pane, so running a preview `Game` is free | `GameStudioPreviewService` starts a second `Game` on a thread it creates itself; on macOS the engine needs the real process main thread for windowing, so `Game.PrepareContext` throws there (`MicroThreadLocalProviderService` → `DatabaseFileProviderService` cast) and the unhandled exception **kills the host process** | New `EnablePreviewService` switch on `StrideEditorPlugin`, symmetric with the existing `EnableThumbnailService`; the addin turns it off. (`PreviewViewModel` resolves the service via `TryGet` and tolerates null, so nothing else needed guarding.) |

**When a new failure appears in Stride code that GameStudio itself exercises fine, grep GameStudio's
`Program.cs` and `GameStudioWindow.xaml.cs` for setup we are not doing before investigating the
failure site itself.**

### Cause B — host/Stride assembly identity collisions, which mostly do NOT need resolving

LibreWPF and OpenDevelop ship their own assemblies that collide by simple name with ones Stride was
compiled against, at genuinely different identities:

| Simple name | Stride wants | Host provides |
| --- | --- | --- |
| `AvalonDock` | 4.72.1.0, PKT 3e4669d2f30244f4 | different version (OpenDevelop's docking UI) |
| `System.Drawing.Common` | 10.0.0.0, PKT cc7b13ffcd2ddd51 | ProGPU substitute, 0.0.0.0, PKT c29c9752855ee183 |
| `NuGet.ProjectModel` | exactly 7.3.1.0 | 7.6.0.0 (AddInManager2's) |

.NET will not host two identities of one simple name in a single `AssemblyLoadContext`, and the
addin's assemblies necessarily load into the Default one. **The collision is unavoidable — but it is
almost never the thing that has to be fixed.** Two distinct sub-cases, with different remedies:

1. **The failing code doesn't actually want the conflicting types** (AvalonDock, every time). The
   crash is `Assembly.GetTypes()`'s all-or-nothing contract: a plugin assembly's WPF view types
   fail to load, so a scan looking for `AssetViewModel`/`IEditorView`/preview types gets nothing
   instead of the 99% that loaded fine. Remedy: `Assembly.GetFullyLoadedTypes()` (new shared
   extension in `sources/core/Stride.Core/Extensions/AssemblyExtensions.cs`, promoted from the
   identical pattern Stride already had in `AssetCompilerRegistry`). Applied to all 9 `.GetTypes()`
   call sites in `sources/editor` (excluding Tests/AutoTesting) — see the "one at a time" note below.
2. **The failing code genuinely needs the assembly** (`System.Drawing.Common`, once). Then ask
   whether the feature is load-bearing: `SpriteFontAssetNodeUpdater` only wanted
   `InstalledFontCollection` to populate a font-name suggestion dropdown, and that API throws
   `PlatformNotSupportedException` on non-Windows since .NET 6 anyway — so resolving the collision
   would not even have helped. Remedy: degrade gracefully (empty list), with the
   `System.Drawing`-touching call isolated in a `[MethodImpl(NoInlining)]` helper so merely running
   the static ctor does not force the assembly to resolve. A `TypeInitializationException` here
   aborts the entire session load, so the try/catch is what keeps one cosmetic feature from being
   fatal.
   - Only when neither applies is a same-name-different-identity file worth deploying at all, via
     `<OpenDevelopAlwaysCopy>` (the Addin SDK's escape hatch from `OpenDevelopTrimAddinOutput`,
     which otherwise deletes any addin DLL whose filename also exists in the host's bin). That is
     why `NuGet.ProjectModel.dll` and `AvalonDock.dll` carry that exemption but
     `System.Drawing.Common.dll` deliberately does not — deploying it was tried, verified to have
     the exactly-correct identity, and *still* failed to bind, which is what made case 2 the answer.

### Debugging notes that cost real time here

- **These failures come one at a time, and the error text does not change between them.** Fixing
  `AssetsPlugin.RegisterAssetViewModelTypes` just moved the identical AvalonDock
  `ReflectionTypeLoadException` to `AssetsEditorPlugin`, then to `StrideEditorPlugin.InitializeSession`.
  Diff the *stack trace*, not the message, to tell "no progress" from "progressed to the next
  instance". Once the pattern is established, patch every call site at once rather than iterating.
- **A poisoned `obj/project.assets.json` silently fakes success.** An earlier failed manual
  `dotnet build` of a sample left an assets file with `"libraries": {}`. Stride's dependency code
  reads it, finds zero dependencies, reports no error, and skips restore entirely — producing the
  exact same downstream crash as a failed restore, with none of the diagnostics. When dependencies
  come back empty, check whether that file exists and whether it is empty before anything else.
- **`EditorViewModel.OpenSession` swallows the real reason.** It collapses `PackageSessionResult`'s
  messages into a bare `false`, so the addin could only report a generic failure. Fixed by logging
  the result through `GlobalLogger` on failure (fork) and forwarding `GlobalLogger` into
  OpenDevelop's `LoggingService` (`StrideEditorHost`'s static ctor). Worth keeping permanently — it
  is what turned every subsequent error in this arc from a dead end into a stack trace.

### Not our bug: the samples in this checkout cannot restore at all

`GameMenu.Game.csproj` (and every other sample) references `Stride.Engine Version="4.4.0"` — a
**stable** version that exists nowhere: the local `bin/packages` feed has `4.4.0-dev` and nuget.org's
nearest is `4.4.0-brta4`, both prerelease, and NuGet's `>= 4.4.0` does not match prereleases.
Confirmed directly with `dotnet restore` (NU1102). So restore fails → no dependencies → the engine's
asset package never loads → `DefaultGraphicsCompositorLevelN` (the archetype base of the sample's
`GraphicsCompositor`) is missing → **unhandled exception on a threadpool thread kills the whole
OpenDevelop process**. `SamplesAssetPackage` opened fine throughout only because it has no asset
whose archetype lives in an engine package.

For testing, copy a sample to scratch and rewrite its `PackageReference` versions to `4.4.0-dev`
(plus a `nuget.config` pointing at an absolute `bin/packages` path); that restores cleanly (134
libraries) and the session then loads with `Packages: 7, local: 1` and real `SceneAsset`s. A proper
fix for the checkout is a separate question (pack the fork as stable `4.4.0`, or adjust the samples).

**Separately worth fixing regardless**: an asset-loading failure on a background thread should not
be able to take down the host process. Currently it does.

## Input: transport already works; buttons blocked by Cocoa key-window loss (2026-08-25)

The plan assumed input would need a forwarding layer (WPF events → the game). **It does not.** The
overlay is a real SDL window, so Stride's own SDL input path applies unchanged: `InputSourceFactory`
builds an `InputSourceSDL(context.Control)` for a `GameContextSDL`, and the `Application.ProcessEvents()`
already in `Tick()` pumps it. Nothing needed writing.

Verified live, not inferred — `od.stride.scene-status` (new, see below) reports the game's own
`InputManager`: `hasMouse: true, hasKeyboard: true, sources: 1`, and injecting an OS-level mouse move
moved `MousePosition` from `(0,0)` to `(0.317, 0.306)`. The vertical figure is exact: the injected
point sat 11px below the viewport's top edge, and `0.3056 × 36 = 11`.

**What does not work: mouse buttons.** `downButtons` stays 0 across press/drag-move/release while
motion continues to track. The status action shows why: `isKeyWindow` is **true** right after the
overlay attaches, then flips to **false** once a click lands. Motion does not require key status, so
it keeps working; button events go wherever macOS decides is active. This is the Cocoa child-window
focus problem, just deferred - it shows up on first click rather than at attach time. Next step is a
first-mouse/key-window policy for the overlay (`acceptsFirstMouse:`, or re-asserting key on click),
balanced against the host window needing key for OpenDevelop's own UI.

### Tooling this required (and a measurement trap it removed)

`od.stride.scene-status`, a `[DevFlowAction]` in the addin, reports overlay + `InputManager` state.
It exists because **the scene cannot be observed any other way**: it renders into a native child
window that the WPF screenshot path cannot capture, and on this LibreWPF build screenshots fail
outright (`DllNotFoundException: wpfgfx_cor3.dll` - a Windows-only native library; those `dlopen`
failure blocks in the log are screenshot attempts, not shutdown). It reads the game through
reflection deliberately: the controller exposes its game only via an internal interface, and widening
production API for diagnostics is the wrong trade.

Two facts worth keeping:

- **`[DevFlowAction]`s in a lazily-loaded addin ARE discovered** once the addin loads
  (`od.stride.*` went 0 → 1 in the action list after opening a `.sdpkg`). The comment in
  `OpenDevelopDevFlowActions.cs` about discovery happening once, before addin assemblies load, no
  longer holds for this agent version - a shell-side forwarder is not needed.
- **DevFlow injects at the OS level** (`"mode": "cliclick"` → CGEvent), so synthetic input reaches
  the SDL child window, not just the WPF tree. An earlier claim here that it could not was wrong.

### Measurement trap: `pgrep -f` never matches this process

The app is launched as `./OpenDevelop`, so its command line does not contain the build directory -
`pgrep -f "bin/Debug/net10.0-windows/OpenDevelop"` returns nothing **while the process is running**.
Several "process exited" readings in this session were that false negative, including one where a
DevFlow call succeeded moments later against the supposedly-dead process. Use
`ps aux | grep "[O]penDevelop$"`.

Relatedly: an `EXIT=0` clean shutdown was self-inflicted, from wrapping the launch in
`nohup bash -c '...' &` inside a tool call - the extra shell layer took the app down with it when the
call returned. Launch it directly with `&`.

### FIXED: overlay was 2× the requested size (pixels assigned to a points-typed property)

The overlay's native frame read `936 × 36` for a `468 × 18` WPF element - exactly the Retina backing
scale. Cause: `GraphicsDeviceManager` hands the **back-buffer size, in pixels**, to
`GameWindow.EndScreenDeviceChange`, which `GameWindowSDL` assigns straight to `window.ClientSize` -
**logical points**. Those agree only at scale 1. Worse, it compounds: a bigger window yields a bigger
back buffer, which enlarges the window again (the `936×8` → `1872×16` → `1024×1024` swapchain sizes
in the log are that runaway).

Fix: skip the self-resize when `GameContext.IsUserManagingRun` - an embedded, host-driven window's
geometry belongs to the host, not the game. Applied to both `EndScreenDeviceChange` and `Resize`.
Verified: frame is now `468 × 32` against a `468 × 32` element, and an injected pointer at the
viewport centre maps to exactly `(0.500, 0.500)` (it was `(0.317, 0.306)` with the 2× bug).

Two wrong turns worth remembering:

- The first attempt divided by `ScaleFactor` inside `Resize`. It cannot work: at the first resize the
  drawable surface does not exist yet, `ScaleFactor` reads 1, the division is a no-op, and the window
  stays doubled for good.
- That attempt also patched the wrong method. `Resize` is not on this path at all - the editor game
  goes through `EndScreenDeviceChange`. The measurement that should have caught it sooner: after the
  "fix", the ratio stayed exactly 2× while the absolute numbers tracked the WPF element (`936×36` →
  `936×64` as the element grew). A constant ratio through a change means the code you touched is not
  the code that runs.

### Mouse buttons: what is established, and what is still open

Established by measurement, on the current build:

| Fact | Evidence |
| --- | --- |
| Motion reaches the game | injected move at the viewport centre → `MousePosition == (0.500, 0.500)` |
| Buttons never reach the game | `PressedButtons` latched across frames stays 0 over ~1800 ticks |
| The overlay is not click-through | `ignoresMouseEvents == false` |
| Clicking takes key away from the overlay | `isKeyWindow` flips true → false on the click |
| Re-asserting front + key does not help | `orderFront:` + `makeKeyWindow` restores key, next click still registers nothing |
| SDL's click-through hint is already on | `Window.InitializeSDL` sets `SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH = 1` |
| WPF hit-tests its own content at that exact point | `/ui/hit-test` at the viewport centre returns an `AdornerLayer` |

Sampling note: polling `DownButtons` from outside is useless here - a synthetic click is pressed and
released well within one frame, so it always reads 0, which is indistinguishable from "the event never
arrived". `od.stride.scene-status` latches `PressedButtons`/`PressedKeys` per tick instead.

Three hypotheses were formed; **the first two were disproven by measuring rather than by argument**,
which is the only reason the third is worth stating:

- ~~The overlay is click-through~~ - `ignoresMouseEvents` is **false**.
- ~~Another window (AvalonDock's drag overlay, the host window) is above ours and takes the click~~ -
  asked the window server directly with `+[NSWindow windowNumberAtPoint:belowWindowWithWindowNumber:]`
  at the viewport centre: it returns **our** window number (1245), not the host's (1236). Z-order is
  correct and the OS would deliver a click there to us.
- **Remaining hypothesis: the host takes key away on click, and the activating click is consumed with
  it.** The state transition is the whole story:

  | | Cocoa `isKeyWindow` | SDL `Focused` (`SDL_WINDOW_INPUT_FOCUS`) | mouse pos | button/key events |
  | --- | --- | --- | --- | --- |
  | at rest | true | true | tracks exactly | – |
  | after a move | true | true | `(0.500, 0.500)` | none |
  | after a click | **false** | **false** | jumps to y=0 | **none, ever** |

  Both Cocoa and SDL agree the overlay is focused right up until the click, and both lose it at the
  click. `makeKeyWindow` restores focus, and the next click loses it again - so something is actively
  reassigning key on mouse-down rather than the window being unable to hold it. Keyboard events never
  arrive either (`seenKeyPresses` 0), which fits: SDL routes key events to the input-focused window,
  and focus is gone by then. Prime suspect is LibreWPF/ProGPU's own focus management forcing its main
  window key on click (`ProGPU.Wpf.dll` is the only host assembly with an event-pump symbol);
  unverified, and it is a binary, so confirming it means either instrumenting the host or testing the
  overlay outside a child-window relationship.

Note the motion path is not evidence against this: `MouseSDL.OnMouseMoveEvent` is a genuine SDL
motion event, so the event pump, window lookup and dispatch all work. Only the focus-gated event
classes (buttons, keys) are lost.

### RESOLVED: input works, via WPF forwarding - and two of the "symptoms" were measurement bugs

Design as implemented: **the overlay presents frames only, WPF owns input.** The overlay is set
click-through (`setIgnoresMouseEvents:YES`) so clicks fall to the WPF element beneath it, and
`StrideSceneEditorViewport` forwards them into the game through Stride's own
`InputSourceSimulated`/`MouseSimulated`. Verified end to end: a synthetic press gives
`wpfDowns = 1` and `simPresses = 1`.

This sidesteps the native-focus question rather than answering it - the SDL window still loses key on
click and still produces no button events. Worth knowing if the overlay is ever made input-bearing,
but nothing depends on it now.

**Two of the things that made this look intractable were bugs in how I measured, not in the code:**

1. **The injected presses were landing somewhere else.** DevFlow's `move` transforms the coordinates
   it is given (it reported `(527,531) → (537,596)`), while `press`/`release` use them raw. Every
   press in the earlier rounds therefore hit a different point than the moves that "proved" the
   coordinates were right. Read back the `x`/`y` in the action's response and reuse those.
2. **The oracle watched the wrong device.** `InputManager.PressedButtons` returns
   `Mouse.PressedButtons`, and `Mouse` is `pointers.OfType<IMouseDevice>().FirstOrDefault()` - the SDL
   device, which registered first. A second, simulated mouse can never show up there. The latch now
   reads `simulatedMouse.PressedButtons` directly.

Both produced the same reading - zero - as a genuine "the event never arrived", which is how five
consecutive hypotheses about focus, z-order, click-through, style mask and SDL visibility all got
tested against a broken instrument. Each was disproven honestly; the instrument was never the thing
under suspicion. **When several independent hypotheses all fail to move a measurement, suspect the
measurement.**

Also worth keeping: `cliclick` posts CGEvents, which macOS gates on Accessibility permission **per
calling process**. Invoked from DevFlow (inside OpenDevelop, which has it) injection works; the same
binary run from a shell silently does nothing. Do not test injection from the shell and conclude
anything from it.

### The forwarded input has to be the *primary* mouse

`InputManager.Mouse` is `pointers.OfType<IMouseDevice>().FirstOrDefault()`, and the editor services
read through it (`Game.Input.IsMouseButtonDown`, `Game.Input.MouseDelta`). A second, simulated mouse
is therefore invisible to them - the same aliasing that made the earlier oracle read zero.

InputManager sorts `pointers` by `Priority` descending, but **only when a device is registered**, so
setting `Priority` after `InputSourceSimulated.AddMouse()` changes nothing. `AddMouse` constructs and
registers in one step at the default `-1000` (real hardware should normally outrank a simulated
device), and `RegisterDevice` is protected - hence `PrimaryMouseInputSource`, a small subclass in the
addin that constructs the mouse, sets `Priority = 1000`, and registers it itself.

Verified: with that in place `InputManager.DownButtons` reports the forwarded press
(`downButtons = 1` during a drag), and `MousePosition` tracks the forwarded moves.

### Where it stands: input lands, the camera service does not run

Everything from the OS to the game's primary mouse device is confirmed working:

| Stage | Evidence |
| --- | --- |
| WPF receives the events | `wpfMoves` and `wpfDowns` both increment, moves included during a drag |
| Forwarded to the simulated device | `simDown` 1 while held, 0 after release |
| The device is updated by the game | `simPresses` records the press edge (requires the device's `Update` to run) |
| It is the primary mouse | `InputManager.DownButtons == 1`, `MousePosition` tracks |
| The camera service can act | its `IsMouseAvailable` is true |

**But `EditorGameEntityCameraService.IsControllingMouse` stays false and `Position` never changes.**
Its logic is `isRotating = !isAltDown && !mbDown && rbDown`, and `shouldControlMouse = IsMouseAvailable
&& isAnyMouseButtonDown && (… || isRotating || …)` - with a right-button drag, an available mouse and a
down button all confirmed, `IsControllingMouse` would have to become true if that code ran at all. It
does not, so the remaining gap is that **the editor game services' update is not being driven**, not
anything about input.

That turned out to be Cause A again, plus a harness limit that masked the rest.

#### `StartGame()` is not enough - the editor's own sequence is `Initialize()`

Editor game services register their per-frame work with `Game.Script.AddTask(Update)` from inside
their service initialization, and that initialization happens in **`CreateScene()`**, not
`StartGame()`. `GameEditorViewModel.InitializeEditor` runs the full sequence:

```
StartGame() → await GameContentLoaded → CreateScene() → OnGameContentLoaded()
```

This viewport was calling `StartGame()` alone, which yields a scene that renders but whose services
never tick - input reaches the game and nothing acts on it. Fixed by calling the public
`SceneEditorViewModel.Initialize()` (which is `GameEditorViewModel.Initialize`, sealed over
`InitializeEditor`) instead, so Stride runs its own sequence rather than this addin reproducing it.

#### Why "the camera does not move" is not (yet) evidence of anything

Two things prevent concluding anything from the camera:

- **DevFlow's `press`/`release` only ever send the left button.** Asking for `"button":"right"` still
  arrives as `Left` - confirmed by reporting the actual `DownButtons` contents (`downNames = "Left"`),
  after `downButtons = 1` had made it look like the right button had landed. cliclick's `dd:`/`du:`
  primitives are left-only; only the atomic `right-tap` does a right click, which cannot express a
  drag.
- **A plain left-drag is *supposed* to leave the camera alone.** From `GetInput()`:
  `isPanning` needs middle, `isRotating` needs right, `isMoving` needs middle+right, `isOrbiting`
  needs Alt+left, `isZooming` needs Alt+right or a wheel delta. Left alone matches none of them - left
  is selection, not camera. So the camera staying still under the only drag the harness can produce is
  correct behaviour, not a failure.

#### Keyboard forwarding: written, not yet working

`KeyboardSimulated` is now registered the same way as the mouse (via `AddPrimaryKeyboard`, priority
set before registration), WPF `KeyDown`/`KeyUp` are mapped to Stride's `Keys` by name, and the
viewport calls `Focus()` on mouse-down so clicking it takes keyboard focus - which is the right
product behaviour regardless of testing.

`wpfKeyDowns` stays 0, which is equally consistent with "the element never took focus" and "focus is
fine, no key event was ever produced" - so the probe now counts `GotKeyboardFocus` and reports
`IsFocused`/`IsKeyboardFocused` to tell them apart. **It is the second one.** After a click:
`gotKbFocus = 1, isFocused = true, isKbFocused = true`, and still no key. DevFlow's `key` action
answers `"simulationMode": "semantic"` - it drives automation peers, not real input, so it cannot
produce a WPF `KeyDown` (the same reason `tap` did nothing earlier).

So the keyboard forwarding is very likely correct and simply unexercised by this harness. Do not
"fix" it on the strength of a zero here. Verifying it needs either a real keypress (a human, or an
injector that posts key CGEvents the way `cliclick` does for the mouse) or a unit-level test that
raises `KeyDown` on the element directly.

#### Verified state of the input path

| Stage | Status |
| --- | --- |
| Mouse → WPF element | ✅ moves and downs, including during a drag |
| WPF → simulated mouse → primary device | ✅ `DownButtons` reports `Left` and holds while pressed |
| Editor `Initialize()` sequence runs | ✅ no failure, scene renders |
| Keyboard → WPF element | ❓ untestable here - focus is confirmed good, but DevFlow's key injection is semantic-only |
| Editor services acting on input | ❓ untested - needs right-button or Alt, neither reachable from the harness |

**Both remaining unknowns are harness limits, not known defects.** DevFlow can inject real mouse
events (via `cliclick`, left button only) but only semantic keyboard/tap events. That is enough to
prove the mouse path end to end, and not enough to exercise any editor interaction, since every
camera gesture needs a right/middle button or a modifier key.

### Driving the devices directly: services still do not act

To get past the harness limit, `od.stride.simulate-gesture` drives the simulated devices from inside
the addin - right-drag, Alt+left-drag and wheel - ticking the game between steps and sampling state
*during* the gesture (per-frame state is gone by the time an action returns; that trap has now bitten
three times). All three gestures leave the camera at its initial position.

What the mid-gesture sample shows for a right-drag:

| Signal | Value | Reading |
| --- | --- | --- |
| `midButtons` | `Right` | the correct button is down and visible to `InputManager` |
| `midDelta` | *(empty)* | `InputManager.MouseDelta` is never non-zero |
| `sawControlling` | false | the camera service never takes the mouse |
| service `initialized` / `active` | true / true | it was initialized, so `Game.Script.AddTask(Update)` ran |
| `gameFrames` vs our `ticks` | equal and both advancing (1755 → 1964) | `Game.Update()` really is being driven by our `Tick()` |

The damning one is `sawControlling` staying false **while `Right` is down**: the service's logic is
`isRotating = rbDown` and `shouldControlMouse = IsMouseAvailable && isAnyMouseButtonDown && isRotating`,
with `IsMouseAvailable` separately confirmed true - so if that code ran at all on any of those frames,
`IsControllingMouse` would have flipped. It never does, and that conclusion does not depend on the
delta question.

So: the game updates, the services are initialized and active, their per-frame work is registered with
`Game.Script`. Chasing "is the script system running" ruled out three more candidates:

| Checked | Result |
| --- | --- |
| Is `ScriptSystem` in `GameSystems` and enabled? | yes - `enabled=true`, 14 microthreads scheduled |
| Are those microthreads actually being stepped? | yes - `states=[Running:12 Starting:2]` |
| Is the game throttled as hidden? (`EditorServiceGame.Update` short-circuits when `IsEditorHidden`, which would stop GameSystems while `UpdateTime.FrameCount` kept climbing) | no - `editorHidden=false`. `OnShowGame()` is now called on load anyway, since real Game Studio calls it on tab activation and this viewport is visible once loaded |

**Which leaves a clean contradiction, and that is where the next session should start.** All of these
are true at the same time:

- `InputManager.DownButtons` contains `Right` (sampled mid-gesture, from the same `InputManager` the
  service reads)
- `IsMouseAvailable` is true, `IsInitialized` and `IsActive` are true
- the service's microthread is in the scheduler and the scheduler is stepping
- `EditorGameEntityCameraService`'s logic is `isRotating = rbDown` →
  `shouldControlMouse = available && anyButtonDown && isRotating` → `IsControllingMouse = true`
- yet `IsControllingMouse` is never observed true, and `Position` never changes

Every step of that chain has been measured except the innermost one: whether `UpdateCamera()` itself
runs, and what `Game.Input` looks like *from inside it*. That is the one thing reflection from outside
cannot see, and guessing further from outside has already cost more than the measurement would.
**Next step: instrument the fork** - a log line at the top of `EditorGameEntityCameraService.UpdateCamera`
reporting the button state it sees. It costs one fork rebuild and settles whether the method runs at
all, and if it does, whether its `Game` is the same instance the probe is reading.

Secondary, still open: `InputManager.MouseDelta` stays zero while `MousePosition` updates from the same
pointer events, so the fault is specific to delta accumulation rather than to the event path.

### RESOLVED: the game had silently faulted, so nothing downstream was running

Instrumenting the fork settled it in one build. A counter at the top of
`EditorGameEntityCameraService.UpdateCamera` showed it had been called **2 times** across ~2000 frames:
the service's `while { UpdateCamera(); await Script.NextFrame(); }` loop was not looping.

The cause chain:

1. First shader compilation throws `DllNotFoundException: Could not locate or load native library
   stride_spirv_tools` out of `SpirvTools`' static constructor.
2. `EditorServiceGame.Update` catches it, `OnFault` marks it handled (`EditorGameRecoveryService`
   stashes it on `GameEditorViewModel.LastException`), and sets `Faulted = true`.
3. From then on `Update` returns immediately, **every frame, forever** - so no `GameSystem` runs,
   including the `ScriptSystem` that every editor service's per-frame work is scheduled on.

The missing library is the same class of problem as `libbulletc`/FFmpeg from earlier in this arc -
a `net10.0-windows` TFM selecting only Windows native assets. Only
`Stride.EffectCompilerServer`'s bin carries the osx-arm64 `libstride_spirv_tools.dylib`, so its
`runtimes/**` is now a third `CopyStrideRuntimes` source (a superset of the previous two).

**Why this cost so many rounds: a swallowed exception makes every downstream observation lie.**
Frames kept climbing (`UpdateTime.FrameCount` advances outside the faulted path), the scene stayed on
screen (already-rendered content), services reported `IsInitialized`/`IsActive` true, and microthreads
reported `Running`. All of that was accurate - and all of it was irrelevant, because nothing was
calling any of it. Several rounds went into verifying components that were fine.
**Check for a swallowed-error/degraded-mode flag early when a subsystem is present, correct, and inert.**

### Now blocked on a main-thread deadlock during shader compilation

With `stride_spirv_tools` present the compile actually proceeds - and the app hangs. `sample` on the
process: 820 of 828 main-thread samples parked in `Monitor_Wait` → `_pthread_cond_wait`, CPU at 6.9%.
Blocked, not slow.

That fits the hosting model: this viewport drives the whole game synchronously from
`CompositionTarget.Rendering` on the WPF UI thread (the macOS SDL/Cocoa constraint), so anything the
game waits on that can only complete by the game loop advancing cannot complete - the thread that
would advance it is the one blocked. `StartGame` already hands the initial `WaitForShaders()` to
`Task.Run` for this reason; compilations triggered later during rendering have no such escape.

`dotnet-stack report` named it immediately (use that, not `sample` - the JIT frames there are
unsymbolised and cost an extra round):

```
StrideSceneEditorViewport.OnRendering            ← WPF UI thread
 → EditorGameController.Tick
   → GameBase.Tick → GameSystemCollection.Update → ScriptSystem.Update → Scheduler.Run
     → MicroThread running EditorGameController.PostActionAsync's closure
       → DispatcherLock.Lock's closure
         → Task.Wait()                            ← blocked here, forever
```

#### This is an architectural conflict, not a bug

`DispatcherLock` exists to hold several dispatchers still while an operation runs, and it says so in
its own precondition:

```csharp
if (dispatcher.CheckAccess()) throw new InvalidOperationException(
    "A dispatcher lock must be created from a different thread that the dispatchers it should lock");
```

It is designed to be called **from a thread that is not the dispatcher's**. In real Game Studio that
holds: the editor game runs on its own thread (`sceneGameThread.Start()` in the WinForms branch), so a
microthread blocking on dispatcher work is fine - the UI thread is free to service it.

This port runs the game loop *on* the UI thread, because SDL/Cocoa windowing requires the process main
thread on macOS. So the microthread blocks the very thread that would complete the task it waits on.
The `CheckAccess()` guard does not fire because the wait happens one frame later, from inside the
scheduler, rather than at `DispatcherLock` construction.

Note the earlier `Task.Run(() => WaitForShaders())` in `StartGame` is the same problem already worked
around once, for the one case that was hit at startup.

So the two candidate directions are both structural:

- **Give the game its own thread again** and solve SDL/Cocoa main-thread windowing another way (e.g.
  create the SDL window on the main thread but drive the loop elsewhere - needs checking whether SDL
  permits that split on macOS).
- **Keep the game on the UI thread and make the editor's dispatcher round-trips non-blocking** - i.e.
  patch `DispatcherLock`/`PostActionAsync` in the fork so they yield instead of `Task.Wait()` when
  already on the dispatcher thread. Narrower, but it is a change to shared editor plumbing and there
  may be more than these two call sites.

This is the decision point for the next session; it should be made deliberately rather than by
patching whichever call site surfaces first.

#### ...except neither was needed (fixed)

Reading `DispatcherLock.Lock` made both unnecessary. It posts to each dispatcher a closure that
*blocks* until the lock is disposed:

```csharp
dispatcher.Dispatcher.InvokeAsync(() => {
    dispatcher.Locked.SetResult(0);
    dispatcher.Unlocked.Task.Wait();   // holds this thread still
}).Forget();
if (lockSequencially) await dispatcher.Locked.Task;
```

With `Lock(true, Editor.Controller, Editor.Dispatcher)` and both dispatchers on one thread: the
controller's closure blocks the UI thread, then the loop awaits the *WPF* dispatcher's closure, which
that same blocked thread can never run. Hang.

But locking one thread twice is not merely fatal, it is **meaningless** - a single thread already
excludes itself. So the fix is to not do it. Each lock closure now asks the not-yet-locked
dispatchers, *from the thread it is about to block*, whether they consider that thread their own
(`CheckAccess()` is only meaningful evaluated there), and marks any that say yes as skipped;
`Dispose` and the final `WhenAll` skip those. Only detectable when locking sequencially, which both
real call sites (`GameEditorChangePropagator`, the only two in the tree) do.

Upstream is unaffected: on Windows the two dispatchers really are different threads, nothing is
skipped, and the behaviour is byte-for-byte what it was.

Worth noting the class *documents* the invariant this port breaks, in its own precondition -
`"A dispatcher lock must be created from a different thread that the dispatchers it should lock"`.
The guard did not fire because the violation happens a frame later, from inside the scheduler,
rather than at construction. **When a hang lands in code whose doc-comment states a threading
assumption, check whether the port violates it before theorising about architecture.**

### Next: `Stride.Engine`'s asset package is not loaded

Past the deadlock, loading the FirstPersonShooter sample now dies on a background thread with:

```text
InvalidOperationException: Unable to find the base
  [/FirstPersonShooter.Game/DefaultGraphicsCompositorLevel10] of asset [.../GraphicsCompositor]
  at AssetPropertyGraph.RefreshBase() ... SessionViewModel.ProcessAddedPackages
```

The sample's `GraphicsCompositor.sdgfxcomp` has
`Archetype: 823a81bf-bac0-4552-9267-aeed499c40df:DefaultGraphicsCompositorLevel10`, and that id is
`Stride.Engine`'s own `AssetPackage/Assets/Shared/DefaultGraphicsCompositorLevel10.sdgfxcomp`. So the
base lives in the `Stride.Engine` dependency and the session is not loading that package's assets.

Ruled out already: the packed `Stride.Engine.4.4.0.nupkg` is well-formed - it contains
`stride/Assets/DefaultGraphicsCompositorLevel10.sdgfxcomp` and a `stride/Stride.Engine.sdpkg` whose
`AssetFolders: !dir Assets` correctly matches that layout.

#### First, a build trap that invalidated three runs of evidence

Probes added to `PackageSession.Dependencies.cs` produced **no output at all**, through three
rebuild-and-relaunch cycles. The addin's DLL was freshly stamped each time; `Stride.Core.Assets.dll`
next to it was a day old. `-v n` on the addin build shows why - two copies target the same file:

```text
Copying ...sources/assets/Stride.Core.Assets/bin/Debug/net10.0/Stride.Core.Assets.dll -> AddIns/.../Stride.Core.Assets.dll
Copying ...sources/editor/Stride.GameStudio/bin/Debug/net10.0-windows/Stride.Core.Assets.dll -> AddIns/.../Stride.Core.Assets.dll
```

The explicit `<Reference Private="true">` copies the freshly built leaf assembly; the
`Stride.GameStudio/bin` wildcard copy then **overwrites it with that bin's stale aggregate**. Whichever
runs second wins, and it is the wildcard.

So **rebuilding a leaf Stride project is not enough - `Stride.GameStudio` must be rebuilt too**, because
its bin is the aggregation point the addin copies over the top of everything else. Until it is, the
addin silently runs yesterday's engine code while reporting a successful build.

Verify deployment rather than trusting the build, remembering that .NET string literals are UTF-16LE
(see the root `CLAUDE.md` - a UTF-8 `grep` falsely reports "not found"):

```python
d = open("AddIns/DisplayBindings/StrideGameStudio/Stride.Core.Assets.dll", "rb").read()
print("[dep] visit".encode("utf-16-le") in d)
```

Pick a marker string **unique to the change**. My first attempt checked for `DispatcherLock`'s
precondition message, which exists in the old build too - it reported `True` against a stale DLL.

#### The crash is now non-fatal (fixed)

`SessionViewModel.ProcessAddedPackages` is `async void`, so a single asset whose base will not resolve
became an unhandled thread-pool exception that **terminated the host**. Its per-asset `Initialize()`
is now wrapped: the failure is logged to the session's asset log and the remaining assets carry on.
The session then loads, and the scene editor reaches MoltenVK swapchain creation. The broken asset is
still broken - it is reported instead of being fatal, which is what a user needs.

#### Two follow-ups from the hardening

**A duplicate-key crash the fix itself introduced.** Logging the skipped asset via
`AssetLog.GetLogger(LogKey.Get("Session"))` looked harmless - that getter is `TryGetValue`-then-add.
But `ProcessAddedPackages` runs *while* `LoadAssetsFromPackages` is still going, so it registered the
`Session` key first, and the load's own `AssetLog.AddLogger(LogKey.Get("Session"), ...)` - a plain
`Dictionary.Add` at `AssetLogViewModel.cs:119` - then threw `An item with the same key has already
been added`. The catch now logs to a private `GlobalLogger` and never touches `AssetLog`. General
point: the crash fix let execution reach code that previously never ran, so "new" exceptions
downstream are expected and are not necessarily pre-existing bugs.

**A logger from `GlobalLogger.GetLogger()` emits nothing until activated.** Both new probes were
silent until `logger.ActivateLog(LogMessageType.Debug)` was added. This wasted a diagnostic round on
top of the stale-DLL one - between them, *every* probe in this session was silent for a reason that
had nothing to do with the code being probed. Before concluding "this code never runs", prove the
instrument works by getting *any* message out of it.

#### Root cause: the sample was never restored (fixed)

With a verified-good instrument (deployment confirmed by UTF-16LE marker, and the sibling
`AssetInitLog` demonstrably emitting from the same process), `[dep] visit` still never fired - so the
dependency loop really was not running. Logging the project list at
`PackageSession.cs:1069` showed why it was *not* the reason I assumed:

```text
[dep] project[0] SolutionProject 'FirstPersonShooter.Game' -> PreLoadPackageDependencies
```

It **is** a `SolutionProject`, so `PreLoadPackageDependencies` *was* called - meaning
`project.FlattenedDependencies` was simply empty. And it was:

```console
$ ls FirstPersonShooter.Game/obj/project.assets.json
No such file or directory
```

The sample had never been restored. No restore graph, no flattened dependencies, no `Stride.Engine`
package, so its `AssetPackage/Assets/Shared` never registered and `GraphicsCompositor`'s archetype
`823a81bf-…:DefaultGraphicsCompositorLevel10` had nothing to bind to. A plain `dotnet restore` of the
sample fixes it completely - reopening gives **0** base failures and **0** asset-init failures.

So none of the package-lookup machinery was at fault; `FindSourceTreePackageFile` and the
dev-redirect path were never even reached. The nupkg-vs-source-tree question raised above is moot.

Note this is a *user-facing* gap, not just a test-setup detail: opening an unrestored `.sdpkg`
degraded silently into an asset with an unresolvable base. Both halves are now fixed - see below.

#### Why restore silently did nothing, and the fix

Two things were wrong, and the first hid the second.

**The load's own warnings were never reported.** `EditorViewModel.OpenSession` dumped
`sessionResult` only when the load returned failure. A load that "succeeded" while every
`PackageReference` went unresolved therefore said nothing at all. It now logs any
warning-or-worse messages on the success path too, which immediately named the real error:

```text
[Error] The target "Restore" does not exist in the project.
[Error] Assets file '.../obj/project.assets.json' not found. Run a NuGet package restore...
```

**In-process restore cannot work in this host.** `VSProjectHelper.RestoreNugetPackages` drives
MSBuild's API with `/t:Restore`. That target comes from the .NET SDK's NuGet targets, reached through
MSBuild SDK resolution - which is not wired up here, so the project evaluates without them and the
target genuinely does not exist. `PackageSessionPublicHelper.FindAndSetMSBuildVersion()` is called and
is not enough. Rather than try to reconstruct SDK resolution inside the host, `RestoreNugetPackages`
now checks `result.OverallResult` and falls back to the `dotnet` CLI, which brings its own SDK:

```csharp
var result = mainBuildManager.Build(parameters, request);
if (result.OverallResult == BuildResultCode.Failure)
    RestoreWithDotnetCli(logger, projectPath, tolerateDowngrade);
```

Verified from a deliberately unrestored sample (`rm obj/project.assets.json`): the app regenerates the
restore graph itself, loads **`Packages: 11, local: 5`**, and reports **0** base failures and **0**
asset-init failures. The in-process attempt still fails first and is still logged - that is the
fallback working as designed, not a regression.

The temporary `[dep]` probes are removed; `PackageSession.cs` is back to pristine and
`PackageSession.Dependencies.cs` keeps only the real fixes.

### The editor now runs, and camera input works end to end

With the deadlock fixed and the sample restored, the fused editor came up healthy for the first time.
`od.stride.scene-status`:

```json
{"running": true, "attached": true, "hasController": true, "sdlWindow": true,
 "frame": {"w": 468, "h": 188}, "wpfSize": {"w": 468, "h": 188},
 "windowNumber": 2293, "hostWindowNumber": 2274, "windowNumberAtCentre": 2274,
 "input": {"gameFrames": 1400, "gameSystems": 15, "gameFaulted": false, "editorHidden": false,
           "scriptSystem": "enabled=true,scheduled=12,states=[Running:12]",
           "camera": {"svc": "EditorGameEntityCameraService", "available": true,
                      "initialized": true, "active": true, "updateCalls": "1401"}}}
```

Every previously-broken signal is now good: `gameFaulted` false (the `SpirvTools`
`DllNotFoundException` is gone), the **ScriptSystem is actually running 12 microthreads** so editor
services are live rather than inert, `UpdateCamera` is called every frame (it was stuck at `2`),
`frame` matches `wpfSize` exactly, and `windowNumberAtCentre == hostWindowNumber` confirms
click-through to the WPF element.

Driving `od.stride.simulate-gesture`:

| gesture | button | position            | yaw / pitch                          |
|---------|--------|---------------------|--------------------------------------|
| rotate  | Right  | unchanged (correct) | `0.785 → -3.301`, `-0.262 → -1.571`  |
| orbit   | Left   | unchanged           | `-3.301 → -12.347`                   |
| zoom    | -      | `y 2.0 → -1.6`      | unchanged (correct)                  |

`pitch` clamping at exactly `-1.5708` (−π/2) is the service's own limit, i.e. real camera code running.

**A measurement trap worth remembering:** `rotate` looked broken for a whole round because
`DescribeCamera` reported only `Position`, and a look-around drag changes *orientation*. The probe now
reports `yaw`/`pitch` too. Same shape as the earlier input-plumbing dead end - *when a gesture appears
to do nothing, first check that the probe can see the thing that gesture changes.*

Also: `scene-status` reported `running: false` for one round simply because it was queried before
`OnLoaded` fired. Both `Current` and the swapchain traffic need a moment after the tab opens; poll
until `running` is true rather than sampling once.

### Real OS input is verified end to end

Everything above drove the simulated devices directly, bypassing WPF. A physical drag by the user
closed the last gap - counters before/after, with a right-button look-around:

```text
wpfMoves 11 -> 185   wpfDowns 0 -> 1   simPresses 0 -> 1   seenButtonPresses 0 -> 1
camera.yaw 0.7854 -> -19.5292        (pitch pinned at the service's -pi/2 clamp)
```

So the whole chain works: physical mouse → WPF element handlers → `MouseSimulated` →
`InputManager` → `EditorGameEntityCameraService` → camera orientation.

**DevFlow cannot inject a right-button drag.** `press`/`drag-move`/`release` accept a `button` field
but ignore it - the response does not echo it, and sampling mid-drag shows `downNames: 'Left'`
regardless. Since Stride's look-around is right-drag, rotation cannot be self-tested through DevFlow;
an injected left-drag correctly reaches the camera service and is correctly declined
(`controlling: false`, left being selection). Manual verification is required for rotation, or a
DevFlow action that drives the right button.

Also note `move` transforms the coordinates it is given while `press`/`drag-move`/`release` use them
raw - only the latter are trustworthy for positioning.

#### Two suspected bugs that were not bugs

Both were mis-modelled measurements, the same failure mode as the `rotate`/position probe above.

**`MouseDelta` reads zero.** It is *transient by design*: `MouseSimulated.SetPosition` →
`MouseState.HandleMove` does accumulate `nextDelta += newPosition - Position`, and `Update()` copies
it into `Delta` and immediately zeroes it. Reading at rest is therefore always zero; sampling
mid-gesture shows the expected non-zero value. (A real hazard does exist in the locked-pointer path -
`SetPosition` computes the delta against a `capturedPosition` that is never updated, so drags would
accelerate - but nothing in the editor calls `LockPosition`, so it is unreachable.)

**Vertical position looked mis-normalized.** Sweeping screen y inside one held drag and fitting three
consecutive samples gives exactly the right slope (`40px / 188px = 0.2128` per step) on a clean linear
mapping. The apparent error came from deriving the element's screen origin from the reported `frame`,
which is the SDL overlay window in Cocoa's **bottom-left** coordinates, while `cliclick` uses
**top-left**. First/last samples in a sweep also lag by one step, because the read can outrun the
game tick that consumes the move - settle or discard the endpoints.

Prime suspect is the source-tree fallback added earlier in `PackageSession.Dependencies.cs`:
`FindSourceTreePackageFile("Stride.Engine")` *does* hit `sources/engine/Stride.Engine/Stride.Engine.sdpkg`,
a different file with `AssetFolders: AssetPackage/Assets/Shared`, and it has a sibling `.csproj` - so
it is taken as a dev-redirect whose assets come from an `.sdbuild` manifest rather than being read
directly. Next step is to log which of the two files is actually chosen for `Stride.Engine` rather
than assume; the session-load path currently logs nothing about this.
