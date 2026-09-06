# Repo-specific notes

## Finding and adding VS toolbar/menu icons

Source library: `/Users/lextm/Downloads/VS2017 Image Library/VS2017/<IconName>/` — one folder per
icon, each containing `<IconName>_16x.xaml` (+ `.png`/`.svg`/`.bmp` variants at other sizes). This
is the official Visual Studio 2017 Image Library; search it by folder name for a concept (e.g.
`Label`, `DisplayName`, `ShowTemplateRegionLabel`, `AlignLefts`).

```bash
find "/Users/lextm/Downloads/VS2017 Image Library/VS2017" -maxdepth 1 -iname "*label*"
```

### How an icon gets resolved at runtime

`PresentationResourceService.GetImageSource("Icons.16x16.<Key>")`
(`src/Main/ICSharpCode.Core.Presentation/PresentationResourceService.cs`) resolves `<Key>` to a
resource path by, in order:
1. An explicit entry in `xamlResourceMap` (full path override).
2. An explicit entry in `xamlResourceAliases` (`<Key>` → a plain icon name, still goes through step 3).
3. **The default convention**: take the last `.`-separated segment of `<Key>`, strip a trailing
   `Icon` suffix if present, and look up
   `Resources/VS2017/<IconName>/<IconName>_16x.xaml` embedded in the
   `ICSharpCode.Core.Presentation` assembly.

So `"Icons.16x16.FormsDesigner.AlignLefts"` → icon name `AlignLefts` → embedded resource
`Resources/VS2017/AlignLefts/AlignLefts_16x.xaml` — no alias entry needed for a straightforward
name; only add one when the desired `<Key>` doesn't already end in the real icon folder's name.

A missing/unregistered icon does **not** throw — `GetImageSource` just returns `null` (blank
icon), logged as a `WARN "Could not load XAML icon ... Cannot locate resource ..."`. That warning
in the app log is the tell that an icon needs to be added, not a real error to chase.

### Adding a new icon

1. Copy the whole icon folder (just the `_16x.xaml` is required; the rest is unused) from the VS
   Image Library into `src/Main/ICSharpCode.Core.Presentation/Resources/VS2017/<IconName>/`.
2. No csproj change needed — `ICSharpCode.Core.Presentation.csproj` already globs the whole
   `Resources\VS2017\**\*.xaml` folder as embedded `<Resource>` items.
3. Reference it as `"Icons.16x16.<IconName>"` (or any key whose last segment/alias resolves to
   `<IconName>`) via `IconService.GetImageSource(...)` /
   `PresentationResourceService.GetImageSource(...)`.
4. Verify live: open the feature in the running app and check the app log for the "Could not load
   XAML icon" warning — its absence confirms the resource was actually found and loaded.

## Driving the WinUI/Uno designer canvas manually via DevFlow

OpenDevelop.exe embeds its own DevFlow agent, separate from the UnoRichText sample's — pinned to
port **9299** (`DevFlowPort.cs`), not the default 9223. Launch it standalone (no test harness) to
manually reproduce a designer bug and inspect state step by step:

```bash
OD_TEST_MODE=1 OD_WINUI_RUNTIME=microsoft dotnet run --project src/Main/SharpDevelop/SharpDevelop.csproj -f net10.0-windows --no-build
```

- `OD_TEST_MODE=1` stops the window from stealing focus (`ShowActivated=false`) — harmless to leave on.
- `OD_WINUI_RUNTIME=microsoft` selects the real Microsoft WinUI 3 child host (`WinUIXamlDesigner.MicrosoftHost`) instead of the default Uno one. **Always match the backend the user actually observed the issue in** before spending time on a repro — but note the two share far more than their names suggest: `DesignHost.cs`/`DesignRpc.cs` are source-linked into both, and only the bootstrap (`Program.cs`) and dispatcher differ. A bug can therefore be in shared code yet reproduce on only one host, because the hosts differ in whether the design root is parented in a live visual tree. Confirm which by reading `Program.cs`, not by assuming.
- The `backend` field in `od.winui-designer.status` says which host answered. `od.winui-designer.draw-calls` replying `"not applicable (out-of-process Uno host)"` means an out-of-process host is in use (true for the Microsoft backend too — the message names the shared client class, not the runtime), so the in-process ProGPU host is **not** what you are looking at.
- Build the app first (`dotnet build src/Main/SharpDevelop/SharpDevelop.csproj -c Debug`) — `dotnet run --no-build` needs the prior build to already exist. Per `tests/OpenDevelop.IntegrationTests/TESTING.md`, never use `dotnet test` in this repo — use `dotnet run --project tests/OpenDevelop.IntegrationTests/... -- -method <FQN>` for the automated version of the same scenario.

Once up, drive it exactly like the integration tests do, via `od.*` DevFlowActions
(`WinUIXamlDesignerDevFlowActions.cs`) over the same REST API documented in the top-level
`uno-tools/CLAUDE.md`, just on port 9299:

```bash
curl -s -X POST http://localhost:9299/api/v1/invoke/actions/od.open-solution -d '{"args":["<path>.slnx"]}'
curl -s -X POST http://localhost:9299/api/v1/invoke/actions/od.open-file -d '{"args":["<path>/MainPage.xaml"]}'
curl -s -X POST http://localhost:9299/api/v1/invoke/actions/od.winui-designer.activate-design -d '{"args":[]}'
curl -s -X POST http://localhost:9299/api/v1/invoke/actions/od.winui-designer.select -d '{"args":["ElementName"]}'
curl -s -X POST http://localhost:9299/api/v1/invoke/actions/od.winui-designer.surface-geometry -d '{"args":[]}'
```

### `od.winui-designer.view "zoom panX panY"` is NOT a literal zoom percentage

This was the actual trap: `od.winui-designer.view "1 0 0"` looks like "100%, no pan" but is
**not** — it sets `zoomFactor = 1.0`, which is also what `"fit"` sets internally
(`UnoDesignSurfaceControl.FitView()`, `zoomFactor = 1.0`). `zoomFactor` is a multiplier on top of
a separately-computed fit-to-pane baseline scale (`DesignViewport.Fit(...)`), so `zoomFactor=1.0`
just reproduces "Fit", not "100%" — the same trap the actual ZoomCombo UI resolves by looking up
`zoomFactor = 1.0 / fitScale` for its "100%" entry (`UnoDesignSurfaceControl.cs`,
`UpdateZoomCombo`/`OnZoomSelectionChanged`, `ZoomPresets`). To get **true 1:1** (1 render pixel =
1 screen pixel) so a visual offset is actually visible/measurable:

1. Query geometry once at `zoomFactor=1.0` (`"1 0 0"`) — note `frame.width` from
   `od.winui-designer.surface-geometry`. This is `designWidth * fitScale`.
2. Compute `fitScale = frame.width / <rendered design width, from od.winui-designer.status's
   "Rendered by ... (WxH)" message>`.
3. Set `zoomFactor = 1 / fitScale` via `od.winui-designer.view "<that number> 0 0"`. Re-check
   `surface-geometry` — `frame.width` should now equal the raw rendered design width exactly
   (scale = 1.0 confirmed).

Driving the real `ZoomCombo` WPF control through generic `/api/v1/ui/tap` (open dropdown, tap the
"100%" `ComboBoxItem`) was tried first and did **not** reliably reproduce the SelectionChanged
commit within a scripted curl sequence — prefer the `od.winui-designer.view` computation above for
scripted repro; only fall back to `ui/tap` on the combo if a test needs to exercise the actual UI
control.

### Numeric geometry vs. actual pixels can disagree — always screenshot to confirm

`od.winui-designer.surface-geometry`'s `selection`/`element`/`frame` numbers are all derived from
the *same* reported element bounds (for the out-of-process hosts, `DesignHost.BuildTree`'s tree),
so **they will always agree with each other even when that shared source itself is wrong**. A
numeric-only check (`selection == element`) can pass while the rendered bitmap is visibly wrong —
this is precisely what hid the collapsed-tree bug below. Confirm with an actual screenshot, cropped/magnified with PowerShell
`System.Drawing`, and only trust the numbers once the picture matches them:

```powershell
curl.exe -s http://localhost:9299/api/v1/ui/screenshot -o shot.png
# then crop/scale with System.Drawing.Graphics.DrawImage(dest, srcRect) + NearestNeighbor
# interpolation before reading it back with the Read tool - the raw screenshot is too small
# to see sub-pixel misalignment at native size.
```

To compare two elements rather than one, `od.winui-designer.describe-element <name>` prints
`bounds=(x,y) WxH` straight from the reported tree — the fastest way to see a whole tree collapsed
onto one origin.

**Subtract the window origin before cropping.** The geometry actions (`surface-geometry`,
`query-control-screen-bounds`, ...) report **screen** coordinates — they are meant for synthetic
mouse input — while `/api/v1/ui/screenshot` is a bitmap of the main window alone. Cropping at the
reported coordinates lands somewhere else entirely (in one case the status bar rather than the
design surface, which looked like a rendering bug). Get the origin and subtract it:

```powershell
# GetWindowRect on (Get-Process OpenDevelop).MainWindowHandle -> e.g. L=42 T=200
# crop at (screenX - L, screenY - T)
```

The window is not at 0,0 just because the screenshot's size matches the window's.

**Synthetic clicks must be aimed with server geometry, never arithmetic.** "Just inside the border"
computed from a parent's rect put a click one pixel inside the child that covers it — a `TabControl`
is the worst case, since its pages cover nearly all of it. Ask for the real rect
(`od.forms-designer.query-tab-header-screen-bounds` returns a tab header's own `GetTabRect` plus the
`centerX`/`centerY` to click). Tab headers are sized to their text, so "divide the strip into N
equal parts" is wrong on the first tab.

### DevFlow cannot see a WPF popup — not in a screenshot, not in the UI tree

A `ContextMenu`, a smart-tag popup, or any other WPF `Popup` is its own top-level window: it is
outside the main window's render AND rooted in a separate `PopupRoot`, so it appears in neither
`/api/v1/ui/screenshot` nor `/api/v1/ui/tree`. A right-click a human observer watched open a menu
produced **zero** `ContextMenu` nodes in the tree. (The `MenuItem` count is a trap — it grows as the
main menu bar builds lazily, and those nodes all have `text: null`.)

Do not try to verify a popup by opening it. Split the feature at the popup boundary and assert the
content through an action that builds the menu without showing it — see
`od.forms-designer.describe-context-menu` and the fuller writeup in
`doc/technotes/winforms-designer.md`. Whether the popup itself *appears* is something only a human
can confirm; ask.

### Case study: the collapsed-tree selection offset (fixed 2026-08-30)

Worth reading before touching designer positioning, because almost every intuition here was wrong.

**Symptom.** At true 100% zoom under `OD_WINUI_RUNTIME=microsoft`, selecting a `StackPanel`'s
second child (`PrimaryButton`, after `TitleText`) drew the selection outline and handles one row
*above* the rendered button, while the rendered bitmap stacked the children correctly.

**Root cause.** `DesignHost.FinishLayoutAsync` called `BuildTree` immediately after its own
`Measure`/`Arrange`. In the Microsoft host the design root is parented in a real offscreen window,
so **the window owns its layout and that direct `Arrange` is discarded** — at that moment the root
still reported `ActualWidth/Height` of 0 and every element's `ActualOffset` and layout slot were
still `(0,0)`. `DesiredSize` *was* already committed, which is exactly why sizes looked perfect
and only positions were wrong. Fix: read the tree **after** `RenderAsync`, since rendering is what
drives the pending layout pass to completion. Uno is unaffected (unparented root, synchronous
`Measure`/`Arrange`), so the shared file needs no per-host branch.

**Blind alleys — do not repeat.** Every attempt to take layout into our own hands broke rendering:

| Attempt | Result |
|---|---|
| `root.UpdateLayout()` after Arrange | Positions right, **bitmap stretched ~12x vertically** |
| Size host `Grid` (and/or root) to design size + `UpdateLayout` | Same stretch |
| Detach root, Arrange unparented (mimic Uno), re-attach | Tree comes back with **zero sizes** |

The stretch is `RenderTargetBitmap`: it rasterizes an element's **content extent**, and the
`RenderAsync(element, w, h)` overload **scales** that content to fill the requested box. So a root
arranged taller than its children gets smeared across the bitmap. Do not pass anything other than
the element's own `RenderSize` to that overload.

**Diagnostics.** `DesignHost` has an opt-in positioning log, off by default:

```bash
OD_DESIGNHOST_BOUNDS_LOG=1   # -> %TEMP%\opendevelop-designhost-bounds.log
```

It dumps, per element, the `ActualOffset`/layout-slot walk that produces each reported rectangle,
plus the requested-vs-actual root size and the real `RenderTargetBitmap` dimensions and buffer
length. It settled this investigation after screenshots alone had produced several wrong theories —
reach for it before guessing. The child process inherits it from the parent's environment
(`UseShellExecute = false`), so setting it on the `dotnet run` line is enough.

**Regression cover.** `AddInTests.WinUIXamlDesigner_ResizeDrag_SelectionAndHandleTrackRenderedElement`
now compares *two different elements* (`TitleText` vs `PrimaryButton`). Its pre-existing
`selection == element` check could never have caught this: both values derive from the same
reported bounds, so they agree with each other even when that shared source is wrong for every
element at once. **Any new designer-geometry assertion must compare independent elements** for the
same reason.

## Writing a DevFlow action: it runs ON the UI thread

There is no marshalling in the action helpers because none is needed — actions are dispatched on the
UI thread, so UI objects can be touched directly. The consequence is that **`GetAwaiter().GetResult()`
in an action deadlocks**: it blocks the very thread the awaited continuation is posted back to, and
the action just times out with no error. Push the async work to the thread pool
(`Task.Run(() => ...).GetAwaiter().GetResult()` — inside `Task.Run` there is no
`SynchronizationContext` to return to) and keep only the UI work on the calling thread.

## `Cannot find class` at startup means an assembly VERSION mismatch

**Every git commit changes the assembly version, which invalidates everything built before it.**
`GitVersion.yml` runs in `ContinuousDeployment` mode off git tags, so the revision is the commit
count since the last tag: `git describe --tags --long` returning `v5.5.2-17-g08cce1ef` produces
`5.5.3.17`. These assemblies are strong-named (`PublicKeyToken=f829da5c02be14ee`), so version
binding is exact — a `FormsDesigner.dll` built at `.17` referencing `ICSharpCode.SharpDevelop,
Version=5.5.3.17` **cannot** load against a shell built at `.16`. The whole AddIn fails to load and
every class in it is reported missing:

```
ERROR Cannot find class: ICSharpCode.FormsDesigner.FormsDesignerViewContent
WARN  WindowActiveCondition: cannot find Type ICSharpCode.FormsDesigner.FormsDesignerViewContent
```

Nothing in that message hints at versions, and the type really is in the deployed DLL — which is why
this is easy to misdiagnose as stale output, a corrupt deploy, or build noise.

**First tell the two causes of `Cannot find class` apart, by counting how many classes are missing:**

| Missing | Cause |
|---|---|
| **Every** class of one AddIn (and its DevFlow actions answer "Action not found") | This one — a version mismatch stopped the whole assembly from loading. |
| **One or a few** named classes | An `.addin` references a class whose sources are excluded from compilation. A real, unrelated bug — see `doc/technotes/solution-explorer.md`, which fixes several by redirecting the `class` attribute rather than deleting the declaration. |

For the first case, **diagnose by comparing versions, not timestamps or symbols:**

```powershell
[System.Reflection.AssemblyName]::GetAssemblyName("$root\src\Main\SharpDevelop\bin\Debug\net10.0-windows\ICSharpCode.SharpDevelop.dll").Version
[System.Reflection.Assembly]::LoadFrom("$root\AddIns\DisplayBindings\FormsDesigner\FormsDesigner.dll").GetReferencedAssemblies() |
  Where-Object { $_.Name -eq "ICSharpCode.SharpDevelop" }   # must match the above exactly
```

So: **after any commit (yours or the user's), and after any change to a shared assembly, rebuild
everything — the shell included.** A partial rebuild is what creates the mismatch.

```bash
dotnet build OpenDevelop.Mvp.slnx -c Debug                                   # ~4-7 min
dotnet build src/Main/SharpDevelop/SharpDevelop.csproj -c Debug              # make sure the shell is at the same version
```

The shared-assembly half of the problem compounds it: `ICSharpCode.Designer.Presentation.dll` is
copied into **every AddIn folder that references it** — nine copies — and a single-AddIn build
refreshes exactly one, so the app can load a stale one and report `TypeLoadException: Could not load
type ...` for a type plainly present in the source.

- **`SharpDevelop.sln` is stale** — it references projects that no longer exist (`Mono.Cecil`,
  `ICSharpCode.Decompiler`, `SubversionAddIn`, ...) and fails with `MSB3202`. Use the `.slnx`.
- Parallel full builds can race on `ICSharpCode.Core.Presentation.dll` (42x `MC1000`) and
  `ICSharpCode.Data.Core.dll` (`CS0006`); building those two projects first breaks the race.
- `LibreWPF.Sdk` occasionally fails to resolve (`MSB4236`) as a transient — retry once before
  investigating.
- Under `OD_TEST_MODE=1` a startup error dialog is suppressed and only logged, so a scripted run can
  look clean while a manual run hits a modal dialog. **`grep -c "Cannot find class"` on the run log
  after every launch** — the app otherwise appears to start fine, and its DevFlow actions simply
  answer "Action not found" for the AddIn that failed to load.
- `taskkill /F` on the app leaves `.git/index.lock` behind if GitAddIn was mid-operation. Do not
  delete it reflexively — check for live `git.exe` processes first (the user's IDE has its own).
  `git show HEAD:<path> > <path>` restores a file's content without touching the index.

## Building `WinUIXamlDesigner.MicrosoftHost`

`dotnet build` **cannot** build this project: `UseWinUI` pulls in `MrtCore.PriGen.targets`, whose
tasks ship only with Visual Studio, so it fails with `MSB4062 ... Microsoft.Build.Packaging.Pri.Tasks.dll`.
PRI generation cannot simply be turned off either — the csproj header explains that the host then
compiles but dies at startup inside `Application.Start` with a bare WinRT stowed exception. Use VS's
MSBuild:

```bash
"/c/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" \
  src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.MicrosoftHost/WinUIXamlDesigner.MicrosoftHost.csproj \
  -p:Configuration=Debug -p:RuntimeIdentifier=win-arm64 -p:DisableGitVersionTask=true -v:m
```

- Locate MSBuild with `vswhere`: `"/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe" -latest -products '*' -requires Microsoft.Component.MSBuild -find "MSBuild/**/Bin/MSBuild.exe"`.
- `-p:RuntimeIdentifier=` must match the machine — an unpackaged WinUI 3 app is built RID-specific,
  and the `DeployToAddIns` target copies `$(TargetDir)` (the RID subfolder) to
  `AddIns/DisplayBindings/WinUIXamlDesigner/MicrosoftHost/`, which is the only location the parent
  probes. Check that deployed copy's timestamp to confirm a build actually landed.
- `-p:DisableGitVersionTask=true` avoids `MSB4216`: GitVersion wants an x86 .NET task host that
  isn't present.
- The main app still builds normally with `dotnet build src/Main/SharpDevelop/SharpDevelop.csproj -c Debug`.
