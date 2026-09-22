# Packing LibreWPF locally for OpenDevelop

OpenDevelop doesn't reference LibreWPF (the portable/cross-platform WPF fork that ProGPU.Wpf
renders through, checked out at `wpf-tools/openavalon/LibreWPF`) via ProjectReference — it
consumes it as NuGet packages from a local feed. This means every time you change LibreWPF
source, you must repack it and refresh OpenDevelop's copy before the change is visible.

## Two version tracks, and they are not the same number

The feed carries **two independent preview versions**, and conflating them is the most common
way to produce a feed nothing consumes:

| Track | Default | Packages |
|---|---|---|
| LibreWPF layer (`PROGPU_WPF_DEV_PACKAGE_VERSION`) | `0.1.0-preview.57` | `LibreWPF.Transport`, `LibreWPF.ProGPU`, `LibreWPF.Sdk`, the `LibreWinForms.*` dev packages |
| ProGPU layer (`PROGPU_WPF_PROGPU_PACKAGE_VERSION`) | `0.1.0-preview.62` | every `ProGPU.*` package **and** `LibreWPF.Interop` |

`OpenDevelop/global.json` pins only the first one, as the `LibreWPF.Sdk` MSBuild SDK version; the
SDK itself carries the ProGPU version it wants (`ProGpuRuntimePackageVersion` in
`packaging/ProGPU.Wpf.Sdk/ProGPU.Wpf.Sdk.ArchNeutral.csproj`). Bump them as one coherent graph —
see `openavalon/AGENTS.md`, which is the authoritative note on the local feed.

Historical note: the feed used to use a single floating `11.0.0-dev` version for everything. That
scheme is gone. The assemblies inside the packages are still stamped `11.0.0.0` — assembly
version and package version are deliberately unrelated here, so don't "fix" one to match the
other.

## How to repack: `openavalon/dist.local.sh` owns the feed

Build the feed from the provider repo, not by hand:

```bash
bash ../openavalon/dist.local.sh          # the whole package graph, ~25 min on this workstation
```

It builds and packs everything in one coherent pass — the ProGPU packages, the ProGPU projects
`LibreWPF.Sdk` depends on, the LibreWPF transport/bridge/SDK, and the canonical LibreWinForms
integration — and writes it all to `openavalon/artifacts/local-feed`.

From OpenDevelop's side, `./repack-librewpf.sh` is a thin wrapper: it calls `dist.local.sh`, then
does the consumer-side half the provider deliberately skips — dropping the stale
`~/.nuget/packages/librewpf.*` + `progpu.*` entries and re-restoring `OpenDevelop.Mvp.slnx`.
`./rebuild-all.sh` chains that with the build + launch step.

Do **not** re-introduce a hand-rolled per-project `dotnet pack` loop here. That is what the old
version of `repack-librewpf.sh` did, with its own hardcoded version string, and it silently
produced a feed of packages nothing consumed once the provider's version scheme moved on.

## How the local feed is wired up

`OpenDevelop/nuget.config` maps the feed by a **relative** path, so the same entry works on every
machine and on both the flat and workspace layouts:

```xml
<add key="librewpf-local" value="../openavalon/artifacts/local-feed" />
```

Forward slashes matter here — a backslash is not a separator on macOS/Linux, and NuGet then
reports "No packages exist with this id in source(s): librewpf-local" instead of saying the folder
is missing. The `nuget.config` comment block spells this out.

`SharpDevelop.csproj` (and other OpenDevelop projects) pull in LibreWPF through the `LibreWPF.Sdk`
MSBuild SDK — declared as a bare `<Project Sdk="LibreWPF.Sdk">`, with the version supplied by
`global.json`, never pinned per project. The packages you'll touch most often
(paths relative to `openavalon/LibreWPF`):

| Package ID | Built from | Contains |
|---|---|---|
| `LibreWPF.ProGPU` | `src/ProGPU.Wpf/ProGPU.Wpf.csproj` | `ProGpuWpfWindowHost`, `WpfPortableWindowActivation`, `WpfPortablePopupActivation`, the Silk.NET-backed windowing/render/input layer |
| `LibreWPF.Transport` | `packaging/Microsoft.DotNet.Wpf.GitHub/Microsoft.DotNet.Wpf.GitHub.ArchNeutral.csproj` | The real WPF assemblies (`PresentationFramework.dll`, `PresentationCore.dll`, `WindowsBase.dll`, ...) — this is where `Popup.cs`, `PortableWindowActivationService.cs`, `MenuItem.cs`, etc. live |
| `LibreWPF.Interop` | `external/ProGPU/src/ProGPU.Wpf.Interop/ProGPU.Wpf.Interop.csproj` | The portable service-registration contracts (`PortablePopupActivationCallbacks`, `PortableWindowActivationCallbacks`, ...) that bridge the two layers above without a circular assembly reference. **Versioned on the ProGPU track**, not the LibreWPF one |
| `LibreWPF.Sdk` | `packaging/ProGPU.Wpf.Sdk/ProGPU.Wpf.Sdk.ArchNeutral.csproj` | The MSBuild SDK itself (rarely needs repacking — only if the SDK's own targets/props change) |

`LibreWPF.ProGPU` and `LibreWPF.Transport` both consume `LibreWPF.Interop`, so a callback
signature change there ripples through all three. `dist.local.sh` rebuilds the graph as a unit
precisely so you never have to reason about which subset is enough.

## The trap: NuGet's global package cache

`~/.nuget/packages/<id>/<version>/` caches whatever was restored the **first time** that exact
version string was ever pulled down, and normal `dotnet restore` will happily keep serving that
stale copy forever. Because the local feed rebuilds the *same* preview version over and over
(`0.1.0-preview.57` / `0.1.0-preview.62`) rather than bumping it, this bites on every repack.
Repacking the `.nupkg` in the local feed is not enough by itself — you must also delete the
matching folder(s) under `~/.nuget/packages/` before OpenDevelop's next restore, or it won't pick
up your changes. `repack-librewpf.sh` does this for you (by package ID, so it stays correct
across version bumps).

Cache folder names are the NuGet package ID lowercased with dots kept as-is, e.g.
`LibreWPF.ProGPU` → `~/.nuget/packages/librewpf.progpu/`.

## A second trap: stale `obj`/`bin` in OpenDevelop's own projects

Clearing the NuGet cache isn't the only staleness trap — an OpenDevelop project's own `obj`/`bin`
can also go stale and MSBuild won't notice, because incremental build only tracks source-file
timestamps, not the *identity* of the SDK or packages a project builds against. Concretely: after
`ICSharpCode.Core.Presentation.csproj`'s `Sdk=` attribute was switched from `ProGPU.Wpf.Sdk`
to `LibreWPF.Sdk` (and a batch of `Resources/VS2026/*.png` icons were added in the same
commit), the project kept silently reusing an `obj/.../ICSharpCode.Core.Presentation.g.resources`
built *before* that change — no icons embedded — for who knows how many sessions afterward,
producing a wall of `Could not load PNG icon '...' — Cannot locate resource '...'` warnings at
startup. The WPF resource pipeline itself was never broken; a clean rebuild of that one project
embeds all the icons correctly. Nothing in the source or SDK targets needed fixing — the stale
`obj`/`bin` did.

**Symptom:** things that "should already be fixed" (an icon, a resource, a behavior tied to a
recent SDK/package change) still misbehave, especially after switching a project's `Sdk=`
attribute or after a LibreWPF/ProGPU package rename — even though the relevant source/config
looks correct on inspection.

**Fix:** delete `obj`/`bin` for the affected project(s) — or, if unsure which one, nuke them all:

```bash
cd <repo-root>
find src -maxdepth 3 -type d \( -name obj -o -name bin \) -print0 | xargs -0 rm -rf
./launch.ps1
```

This is slower than an incremental `launch.ps1` (everything rebuilds from scratch), so reach for it
specifically when a fix "should have landed" and hasn't — not as routine practice.

## A third trap: `dotnet pack` ships **Release**, but plain `dotnet build` builds **Debug**

The `LibreWPF.*` packages are packed with `-c Release` (see "How to repack" above), so
`dotnet pack` bundles the assemblies from `artifacts/bin/<Project>/Release/net10.0/`. A bare
`dotnet build src/.../PresentationFramework.csproj` (no `-c`) builds the **Debug** configuration
into `artifacts/bin/<Project>/Debug/...` and leaves the Release output **untouched**. So this
sequence silently ships stale bits:

```bash
dotnet build .../PresentationFramework.csproj            # builds Debug — Release is now stale
dotnet pack  .../Microsoft.DotNet.Wpf.GitHub.ArchNeutral.csproj -c Release   # packs the OLD Release dll
```

The symptom is maddening: your source change compiles fine, the package "repacks" successfully,
the NuGet cache is cleared, OpenDevelop restores — and your change still isn't there, because the
`.nupkg` was built from a Release assembly that predates your edit. A quick confirmation that the
new code is actually in the packed/deployed assembly (method names are UTF-8 metadata and *do*
show up in `strings`, unlike string *literals* which are UTF-16 and won't):

```bash
strings artifacts/bin/PresentationFramework/Release/net10.0/PresentationFramework.dll | grep -x YourNewMethodName
strings ~/.nuget/packages/librewpf.transport/0.1.0-preview.57/lib/net10.0/PresentationFramework.dll | grep -x YourNewMethodName
```

**Rule:** always build the **same configuration you pack** — either build with `-c Release`
explicitly, or skip the standalone build entirely and let `dotnet pack -c Release` do the build.
For `LibreWPF.Transport` specifically, note it aggregates several assemblies
(`PresentationFramework`, `PresentationCore`, `WindowsBase`, …); a change in `PresentationCore`
(e.g. `MouseDevice.cs`) requires its Release build to be current too — building
`PresentationFramework.csproj -c Release` pulls the whole dependency chain, so prefer that over
building a single leaf project.

## A fourth trap: the arch-neutral pack is a silent no-op under an architecture `Platform`

`packaging/Directory.Build.props` turns `IsPackable` **off** when `$(Platform)` is an
architecture (x64/arm64) and `$(CreateArchNeutralPackage)` is true, so that a multi-architecture
build emits the bait-and-switch packages exactly once, on the AnyCPU/x86 pass. `LibreWPF.Transport`
and `LibreWPF.Sdk` are both `*.ArchNeutral.csproj`, so packing them with `-p:Platform=x64`
produces **nothing** — and `dotnet pack` still exits 0, because a skipped target is not an error.

`dist.local.sh` deletes the previous `.nupkg` before packing, so for a while this combination
quietly *removed* `LibreWPF.Transport` and `LibreWPF.Sdk` from the feed instead of refreshing
them. `pack_wpf_project` now selects `AnyCPU` for `*ArchNeutral.csproj` and asserts the artifact
exists afterwards rather than trusting the exit code. Keep both halves of that guard.

## A fifth trap: the transport staging folder is additive-only

`LibreWPF.Transport` is not packed from a project's build output — it zips a staging tree at
`artifacts/packaging/Release/LibreWPF.Transport/`, which the *build* phase populates. Its cleanup
target (`RemoveStaleLibreWpfTransportPayload`) deliberately **excludes** the current TFM's folder,
so anything that lands in `lib/net10.0/` and later stops being copied just stays there forever
and keeps getting shipped.

That is not hypothetical: `ProGPU.Wpf.Interop.dll`, `System.Private.Windows.Core.dll` and
`Accessibility.dll` sat there for two weeks after the rest of the payload moved on, so the package
shipped a `PresentationFramework.dll` that called an interop API the bundled
`ProGPU.Wpf.Interop.dll` did not have. OpenDevelop then died at startup inside
`ProGpuWpfSdkPortableBootstrap.Initialize()` with
`MissingMethodException: ... PortableWpfServiceRegistry.add_NativeInputPumpChanged`, which reads
like a version-pin problem and is not one.

**Diagnose it by date, not by version**: list the staging folder and look for files older than
the rest.

```bash
cd <openavalon>/LibreWPF/artifacts/packaging/Release/LibreWPF.Transport/lib/net10.0
find . -maxdepth 1 -type f ! -newermt "$(date +%Y-%m-%d)" -printf "%f\n"   # stale leftovers
```

Anything listed there that the current build no longer produces must be deleted before packing.

## A sixth trap: an architecture-stamped assembly in a RID-neutral `lib/` folder

A managed assembly under `lib/<tfm>/` is RID-neutral and **must be AnyCPU**. The CLR does not
JIT its way around a wrong-architecture managed assembly — the load simply fails, and the
exception names a file that is sitting right there on disk:

```
FileNotFoundException: Could not load file or assembly 'ProGPU.Wpf, Version=0.1.0.0, ...'.
The system cannot find the file specified.
   at ProGPU.Wpf.Sdk.ProGpuWpfSdkPortableBootstrap.Initialize()
```

`dist.local.sh` used to pass a hardcoded `-p:Platform=x64` to every pack, which on an ARM64
workstation shipped an **x64** `ProGPU.Wpf.dll` inside `LibreWPF.ProGPU/lib/net10.0`. It now packs
AnyCPU and runs a feed-wide audit ("Checking RID-neutral payload is AnyCPU") that lists any
`lib/**/*.dll` whose PE machine field is not `0x014C`.

Genuinely architecture-specific payload is fine — it belongs under `runtimes/<rid>/`, which is how
`PresentationCore` and the C++/CLI `DirectWriteForwarder` ship, and why
`Sync-LibreWpfDevelopmentRuntime` copies exactly those two from the RID folder last.

**Check it directly** (0x014C = AnyCPU, 0x8664 = x64, 0xAA64 = arm64):

```powershell
$fs=[IO.File]::OpenRead($dll); $br=New-Object IO.BinaryReader($fs)
$fs.Position=0x3C; $o=$br.ReadInt32(); $fs.Position=$o+4; '0x{0:X4}' -f $br.ReadUInt16()
```

Known still-offending (separate fix): the canonical WinForms graph emits arm64
`WindowsFormsIntegration.dll` and `ProGPU.DirectX.dll`.

## A seventh trap: `librewinforms-pack.sh` demands an output directory of its own

It validates that its output folder holds **exactly** the LibreWinForms preview bundle and
rejects anything else ("Unexpected current-version package artifact: ..."). That is a reasonable
purity gate for a release bundle, but it cannot be pointed at the shared dev feed, which by that
point also holds `LibreWPF.Transport`/`ProGPU`/`Sdk`. `dist.local.sh` gives it a private
`artifacts/librewinforms-feed` and copies the result into `local-feed` afterwards.

Two more things it checks, both of which bite when they are wrong:

- `LIBREWINFORMS_CANONICAL_WFI_COMMIT` must be the **LibreWPF** commit, not the LibreWinForms one.
  WindowsFormsIntegration is built from the LibreWPF tree, so SourceLink records LibreWPF's HEAD.
- LibreWinForms must pin the **same ProGPU submodule commit** LibreWPF does, or the canonical
  integration gate aborts before WindowsFormsIntegration is ever built.

Both of these abort *after* the script has already deleted the packages it was about to
republish, so a failure here leaves the feed **missing** `LibreWPF.Interop` and
`LibreWinForms.WindowsFormsIntegration` — and OpenDevelop's next restore fails with NU1101 for
packages that were there an hour ago. If you see that, re-run the pack; do not go hunting for a
NuGet source problem.

## Full repack + relaunch workflow

```bash
cd <repo-root>
./repack-librewpf.sh     # dist.local.sh + cache clear + restore
./launch.ps1             # or ./rebuild-all.sh to chain repack + build + run
```

`repack-librewpf.sh` is a wrapper around `openavalon/dist.local.sh`; see
"How to repack" above. Use the system-installed .NET 10 SDK for OpenDevelop — `global.json` pins
SDK resolution to 10.x. (`dist.local.sh` internally uses LibreWPF's own pinned
`.dotnet/dotnet`, which is required for the 11.0-preview parts of that build; don't override it.)

## Use the system .NET 10 SDK

The old workflow used `librewpf/.dotnet/dotnet` because LibreWPF temporarily required an
11.0-preview SDK. That is obsolete now: use the system-installed .NET 10 SDK for LibreWPF pack,
OpenDevelop restore, and OpenDevelop build/run.

One caveat remains: .NET resolves `global.json` from the current working directory. The LibreWPF
checkout *is* preview-pinned (it needs its own `.dotnet/dotnet`, an 11.0-preview SDK, for the
native/transport build), so run OpenDevelop-side commands from the OpenDevelop repo root and let
`dist.local.sh` use LibreWPF's pinned SDK for the provider side.

## Faster iteration: skip OpenDevelop entirely with a throwaway repro app

For LibreWPF changes that don't need OpenDevelop's full addin/workbench stack (popup placement,
window activation, input routing, rendering), a tiny standalone WPF app iterates much faster than
the full OpenDevelop solution — no AvalonDock/addin tree to restore or build. Give it its own
**scoped** `NuGet.config` so it doesn't touch (or get blocked by) your global package cache:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="./nuget-cache" />
  </config>
  <packageSources>
    <clear />
    <add key="ProGPUWpfLocalArtifacts" value="../openavalon/artifacts/local-feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

With `globalPackagesFolder` scoped to the repro app's own `./nuget-cache`, repacking LibreWPF only
requires clearing *that* folder's stale entries (`rm -rf nuget-cache/librewpf.<id>`) — it never
touches `~/.nuget/packages`, so it can't go stale from (or interfere with) OpenDevelop's own cache,
and vice versa.

## Verifying a fix without a working screenshot pipeline

DevFlow's `/api/v1/ui/screenshot` endpoint isn't wired up for OpenDevelop the way it is for some
other sample apps, so don't rely on it here. Use macOS's own `screencapture` instead, driving the
app with `cliclick` (`brew install cliclick`) for mouse moves/clicks:

```bash
screencapture -x /tmp/shot.png          # -x: no camera shutter sound
cliclick c:100,50                       # click at (x, y) in *logical points*, not device pixels
cliclick m:100,50                       # move only (no click) - useful for hover-only interactions
```

Screenshots come back at the display's full device-pixel resolution; `cliclick` coordinates are in
logical points (e.g. half that, on a 2x Retina display). To bring the app to the foreground first
so it's actually visible in the capture (and so clicks land on it instead of whatever else has
focus):

```bash
PID=$(pgrep -f "bin/Debug/net10.0-windows/SharpDevelop$" | head -1)
osascript -e "tell application \"System Events\" to set frontmost of first process whose unix id is $PID to true"
```

## Portable drag-and-drop (`PortableDragDropOperation`) — findings, 2026-08-12

Moved to [`wpf-designer.md`](wpf-designer.md) (the WPF designer's dedicated technote), where the
re-entrancy fix, the sparse-`DragOver` analysis, and the instrumentation notes live.
