# Stride Game Studio on Linux/macOS via LibreWPF

Tracking follow-up for [stride3d/stride#1922](https://github.com/stride3d/stride/issues/1922)
("Add Linux support for the Stride Game Studio"). This technote plans and records OUR slice of
that effort: evaluating and driving **LibreWPF as the execution substrate** for the existing
WPF-based editor, instead of (or ahead of) the upstream Avalonia migration.

Status: planning (2026-08-24). No code landed yet.

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
|---|---|---|
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
|---|---|---|---|---|
| G1 | `EmbeddedGameForm : GameForm` fails CS0246 on macOS — engine's WinForms `GameForm` only exists in the Windows-TFM build, while the editor unconditionally targets `net10.0-windows` and resolves the engine's net10.0 (SDL-only) output | `sources/editor/Stride.Editor/Engine/EmbeddedGameForm.cs(14)` | stride fork | **fixed 2026-08-24** — platform split behind `STRIDE_EDITOR_WINFORMS` (set per host OS in `sources/Directory.Build.props`); non-Windows gets headless twins |
| G2 | Drag-drop plumbing (`EditorGameController.DragDrop.cs`) is WinForms OLE bound to the game form's HWND | same file family | stride fork | **deferred no-op** on non-Windows (`EditorGameController.DragDrop.Headless.cs`: `DoDragDrop` reports None, drop-target enable/disable no-op); real WPF-routed drops land with the frame-presenter input slice |
| G3 | Cursor plumbing: adorners used WinForms `Cursors`; `ChangeCursor` pushed them to `GameForm.Cursor` | `UIEditor/Adorners/*`, `UIEditorGameAdornerService.Events.cs`, controller | stride fork | **fixed 2026-08-24** — adorners now use WPF `System.Windows.Input.Cursors` (same names); Windows maps WPF→WinForms cursors in `ToFormsCursor`; headless no-op until input slice |
| G4 | Scene-editor viewport presentation | `HeadlessGameHostView`, `SceneGameRunThread`'s `GameContextHeadless()` | stride fork (+ editor-side Cocoa `addChildWindow` glue) | **bridge implemented, not yet live-verified**: headless+readback route built and worked (milestones 2/2.1) but proved unviable (leaking/crashing GPU→CPU copy — see below); pivoted to windowed-surface (`GameContextSDL`), whose platform blocker (macOS drawable-doubling) is **resolved** (`SkipBackBufferClampToWindow`) and whose composition bridge (`addChildWindow` overlay) is **implemented and building** in the addin's own `StrideSdlViewport` (fusion milestone 3, below) — but that is the addin's standalone placeholder-scene viewport, NOT yet wired into the stride fork's own `EditorGameController`/`SceneGameRunThread` (still `GameContextHeadless()` there) or through input re-plumbing; live-open-a-.sdpkg verification also still pending (DevFlow didn't respond in the session that implemented this) |

### G4 feasibility probe — MEASURED blocker (2026-08-24)

Before building the frame presenter, a bounded GPU probe (`StrideGpuProbe`, scratch console
referencing the built `net10.0-macos` Stride.Graphics) answered the make-or-break question:
**does headless GPU render + readback work on this macOS host?**

| Step | Result |
|---|---|
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
|---|---|
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

##### Next slice (not yet started): wire `GameContextSDL` into the editor's SDL branch

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

**Status: builds clean, not yet live-verified.** The addin project and the whole solution build
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

**Follow-up: confirmed the DevFlow gap is a real, pre-existing environment issue, not a launch
mistake.** Ran the project's OWN xUnit integration-test fixture (not a manual replication) —
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
tracks docking/resize) remains the next concrete step**, blocked on fixing or working around the
DevFlow startup gap in a follow-up session — not on any remaining design or implementation
question in the viewport work itself.

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
|---|---|
| `Stride.Core.Assets` + `Stride.Core.Assets.Editor` | The `.sdpkg` asset package system, asset types, YAML serialization, Quantum object graph — the entire content model. Not replaceable; OpenDevelop has nothing analogous |
| Asset pipeline (headless tools): `Stride.AssetCompiler`, effect/shader compiler (`Stride.Shaders`), `Stride.TextureConverter`, model importers, `EffectCompilerServer`, `ConnectionRouter` | Already CLI/child processes; OpenDevelop invokes them exactly like it invokes `dotnet build`. Zero UI to discard |
| Scene/entity-hierarchy editor (`EntityHierarchyEditor*`, `SceneEditor*`) + `EditorGameController` family | The product value: live scene editing against a running engine. Our headless seam (G1–G4) lives here |
| `GameStudioPreviewService` + preview compilation context | Asset previews (materials/models/prefabs) render headlessly — same seam as above |
| `Stride.Editor.Build` (`GameStudioBuilderService`, shader cache coordination) | Build orchestration the editors depend on; drives the kept CLI tools |
| Game/project TEMPLATES (`Stride.Templates.*`) | Feed OpenDevelop's existing new-project dialog instead of GameStudio's start page |
| Editor undo (`EditorUndoRedoService`/Quantum transactions) | Asset-domain undo inside editor views; OpenDevelop's document undo doesn't cover assets |

### ADAPT — keep the logic, replace the shell surface

| Component | Adaptation |
|---|---|
| Asset editor VIEWS (material/sprite/prefab/UI editors' XAML + viewmodels) | Stay as embedded views (their internal Quantum PropertyGrid and styles ship with them — scoping their ResourceDictionaries to those views, not global), but each becomes an OpenDevelop secondary display binding over the owning asset file, opening in the workbench tab area |
| Project model bridge | Stride games ARE MSBuild csproj + an `.sdpkg`; OpenDevelop's project system hosts the csproj natively, while the `.sdpkg` package mounts into the Solution Explorer via a small adapter addin (tree nodes → open asset editors). No fork of either side's model |
| Debug/log pages (`EditorDebugTools.CreateLogDebugPage`) | Route Stride loggers into OpenDevelop's LoggingService/Error List instead of in-app debug pages; keep the logger plumbing, discard the page UI |
| Engine-host isolation (LONG-TERM) | Today the editor runs the engine IN-process on background threads. OpenDevelop's designer red line says project/user assemblies never load into the IDE — and `EditorContentLoader` DOES load user game assemblies. Acceptable for the feasibility slice; end-state moves the `EditorGameController`+engine island out-of-process (same DDP direction as the other designers) |

### DISCARD — OpenDevelop already is that thing

| Component | Why it goes |
|---|---|
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
- **2026-08-25 (DevFlow gap confirmed via the project's own test fixture)** — Ran
  `dotnet test tests/OpenDevelop.IntegrationTests --filter-query "/*/*/AddInTests/OpenAssembly*"`
  (the project's real integration-test path, not a manual replication) to rule out "launched it
  wrong": `OpenDevelopAppFixture.WaitForAgentAsync` timed out after ~2 minutes, same symptom as
  the manual runs — workbench fully loads, DevFlow never binds port 9299
  (`lsof -a -p <pid> -iTCP -sTCP:LISTEN` empty throughout). Confirms this is a real, standing
  environment gap (or an unrelated regression), not something wrong with how this session drove
  the app. Stopping here per instruction; live verification of the windowed viewport is unblocked
  design-wise and waiting only on this DevFlow startup issue being resolved separately.
