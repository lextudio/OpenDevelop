# Packing LibreWPF locally for OpenDevelop

OpenDevelop doesn't reference LibreWPF (`librewpf` repo, the portable/cross-platform WPF fork
that ProGPU.Wpf renders through) via ProjectReference — it consumes it as NuGet packages from a
local feed, pinned to a single floating dev version (`11.0.0-dev`). This means every time you
change LibreWPF source, you must repack it and refresh OpenDevelop's copy before the change is
visible.

## Fast path — don't use `eng/progpu-wpf-sdk-ci.sh`

`librewpf` ships a "do everything" script, `eng/progpu-wpf-sdk-ci.sh`, that rebuilds every
ProGPU/WPF package from scratch and runs a battery of smoke-test harnesses before packing anything.
**It takes 15-20 minutes per run** and, as of this writing, one of its harness steps
(`ProGPU.Wpf.RealApplicationRunHarness`) fails on an unrelated pre-existing reflection signature
mismatch — so it doesn't even reliably finish. Don't reach for it while iterating.

Instead, `dotnet pack` **only the specific project(s) whose source you changed**, directly, and
skip straight to clearing the cache and restoring. This takes on the order of **15-30 seconds**
total for a one-project change (`ProGPU.Wpf` alone builds+packs in ~3s once its dependencies are
already built once):

```bash
cd /Users/lextm/uno-tools/librewpf
dotnet="./.dotnet/dotnet"
package_output="artifacts/packages/Release/NonShipping"

rm -f "${package_output}/LibreWPF.ProGPU.11.0.0-dev.nupkg" "${package_output}/LibreWPF.ProGPU.11.0.0-dev.snupkg"
"${dotnet}" pack "src/ProGPU.Wpf/ProGPU.Wpf.csproj" -c Release -o "${package_output}" -v:minimal

rm -rf ~/.nuget/packages/librewpf.progpu
```

Then restore + relaunch OpenDevelop (step 4/5 below). The full multi-project workflow further down
is only for when you've changed several LibreWPF projects at once, or want to run the test suite
before packing — reach for it, not the CI script.

`eng/progpu-wpf-sdk-ci.sh` still has its place: run it (accepting the ~20 min cost, and ignoring
the one known-broken harness) before landing a change for real, since it's the only thing that
exercises the full package/SDK surface end-to-end. Don't use it as your everyday inner loop.

## How the local feed is wired up

`OpenDevelop/NuGet.config` has a package source pointing at LibreWPF's own build output:

```xml
<add key="local-librewpf" value="/Users/lextm/uno-tools/librewpf/artifacts/packages/Release/NonShipping" />
```

`SharpDevelop.csproj` (and other OpenDevelop projects) pull in LibreWPF through the
`LibreWPF.Sdk/11.0.0-dev` MSBuild SDK, which in turn depends on a handful of `LibreWPF.*` /
`ProGPU.*` packages, all built from `librewpf` at the same fixed dev version. The packages you'll
touch most often when iterating on LibreWPF:

| Package ID | Built from | Contains |
|---|---|---|
| `LibreWPF.ProGPU` | `librewpf/src/ProGPU.Wpf/ProGPU.Wpf.csproj` | `ProGpuWpfWindowHost`, `WpfPortableWindowActivation`, `WpfPortablePopupActivation`, the Silk.NET-backed windowing/render/input layer |
| `LibreWPF.Transport` | `librewpf/packaging/Microsoft.DotNet.Wpf.GitHub/Microsoft.DotNet.Wpf.GitHub.ArchNeutral.csproj` | The real WPF assemblies (`PresentationFramework.dll`, `PresentationCore.dll`, `WindowsBase.dll`, ...) — this is where `Popup.cs`, `PortableWindowActivationService.cs`, `MenuItem.cs`, etc. live |
| `LibreWPF.Interop` | `librewpf/external/ProGPU/src/ProGPU.Wpf.Interop/ProGPU.Wpf.Interop.csproj` | The portable service-registration contracts (`PortablePopupActivationCallbacks`, `PortableWindowActivationCallbacks`, ...) that bridge the two layers above without a circular assembly reference |
| `LibreWPF.Sdk` | `librewpf/packaging/ProGPU.Wpf.Sdk/ProGPU.Wpf.Sdk.ArchNeutral.csproj` | The MSBuild SDK itself (rarely needs repacking — only if the SDK's own targets/props change) |

Only repack the package(s) whose *source* you actually changed. If you only touched files under
`librewpf/src/Microsoft.DotNet.Wpf/...`, you only need `LibreWPF.Transport`. If you only touched
`librewpf/src/ProGPU.Wpf/...`, you only need `LibreWPF.ProGPU`. If you touched
`librewpf/external/ProGPU/src/ProGPU.Wpf.Interop/...` (e.g. changing a callback delegate
signature), you need `LibreWPF.Interop` — and because `LibreWPF.ProGPU` and `LibreWPF.Transport`
both consume it, a signature change there usually means repacking all three.

## The trap: NuGet's global package cache

`~/.nuget/packages/<id>/11.0.0-dev/` caches whatever was restored the **first time** that exact
version string was ever pulled down, and normal `dotnet restore` will happily keep serving that
stale copy forever since the version number never changes. Repacking the `.nupkg` in the local
feed is not enough by itself — you must also delete the matching folder(s) under
`~/.nuget/packages/` before OpenDevelop's next restore, or it won't pick up your changes.

Cache folder names are the NuGet package ID lowercased with dots kept as-is, e.g.
`LibreWPF.ProGPU` → `~/.nuget/packages/librewpf.progpu/`.

## A second trap: stale `obj`/`bin` in OpenDevelop's own projects

Clearing the NuGet cache isn't the only staleness trap — an OpenDevelop project's own `obj`/`bin`
can also go stale and MSBuild won't notice, because incremental build only tracks source-file
timestamps, not the *identity* of the SDK or packages a project builds against. Concretely: after
`ICSharpCode.Core.Presentation.csproj`'s `Sdk=` attribute was switched from `ProGPU.Wpf.Sdk/11.0.0-dev`
to `LibreWPF.Sdk/11.0.0-dev` (and a batch of `Resources/VS2017/*.png` icons were added in the same
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
cd /Users/lextm/uno-tools/OpenDevelop
find src -maxdepth 3 -type d \( -name obj -o -name bin \) -print0 | xargs -0 rm -rf
./launch.sh
```

This is slower than an incremental `launch.sh` (everything rebuilds from scratch), so reach for it
specifically when a fix "should have landed" and hasn't — not as routine practice.

## Full repack + relaunch workflow

Run from `/Users/lextm/uno-tools/librewpf`, using its bundled preview SDK (not whatever `dotnet`
resolves to on `PATH` — OpenDevelop targets `net11.0-windows`, which the system-wide SDK doesn't
support yet):

```bash
cd /Users/lextm/uno-tools/librewpf
dotnet="./.dotnet/dotnet"
package_output="artifacts/packages/Release/NonShipping"

# 1. Build only the project(s) you changed (skip ones you didn't touch)
"${dotnet}" build src/ProGPU.Wpf/ProGPU.Wpf.csproj -c Release -v:minimal
"${dotnet}" build src/Microsoft.DotNet.Wpf/src/PresentationFramework/PresentationFramework.csproj -c Release -v:minimal

# (optional but recommended) run the test suite for whatever you touched
cd src/ProGPU.Wpf.Tests && dotnet test && cd ../..

# 2. Repack — delete the old nupkg first so a failed/partial pack can't leave a stale one behind
rm -f "${package_output}/LibreWPF.ProGPU.11.0.0-dev.nupkg" "${package_output}/LibreWPF.ProGPU.11.0.0-dev.snupkg"
"${dotnet}" pack "src/ProGPU.Wpf/ProGPU.Wpf.csproj" -c Release -o "${package_output}" -v:minimal

rm -f "${package_output}/LibreWPF.Transport.11.0.0-dev.nupkg" "${package_output}/LibreWPF.Transport.11.0.0-dev.snupkg"
"${dotnet}" pack "packaging/Microsoft.DotNet.Wpf.GitHub/Microsoft.DotNet.Wpf.GitHub.ArchNeutral.csproj" -c Release -o "${package_output}" -v:minimal

# If you touched external/ProGPU/src/ProGPU.Wpf.Interop:
rm -f "${package_output}/LibreWPF.Interop.11.0.0-dev.nupkg" "${package_output}/LibreWPF.Interop.11.0.0-dev.snupkg"
"${dotnet}" pack "external/ProGPU/src/ProGPU.Wpf.Interop/ProGPU.Wpf.Interop.csproj" -c Release -o "${package_output}" -v:minimal

# 3. Blow away the matching global cache entries so OpenDevelop's restore can't serve stale copies
rm -rf ~/.nuget/packages/librewpf.progpu ~/.nuget/packages/librewpf.transport ~/.nuget/packages/librewpf.interop

# 4. Force OpenDevelop to re-pull from the local feed
cd /Users/lextm/uno-tools/OpenDevelop
export DOTNET_ROOT="$(dirname "${dotnet}")"   # dotnet var from step 1, still pointing at librewpf/.dotnet/dotnet
export DOTNET_HOST_PATH="${dotnet}"
sdk_dir="$(find "${DOTNET_ROOT}/sdk" -mindepth 1 -maxdepth 1 -type d | sort | tail -n 1)"
export MSBuildSDKsPath="${sdk_dir}/Sdks"
export MSBuildExtensionsPath="${sdk_dir}"
export MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET="${sdk_dir}/SdkResolvers"
export MSBUILD_NUGET_PATH="${sdk_dir}"
export MSBuildEnableWorkloadResolver=false
"${dotnet}" restore OpenDevelop.Mvp.slnx --force --no-cache

# 5. Relaunch (launch.sh kills any stale instance and frees DevFlow's port 9223 first)
pkill -f "SharpDevelop" 2>/dev/null
./launch.sh
```

`launch.sh` already exports the same `DOTNET_ROOT`/`MSBuild*` environment variables internally, so
once you've restored, you can just run `./launch.sh` (or `./launch.sh --no-build` to skip
rebuilding OpenDevelop itself if you only changed LibreWPF and already restored).

## Why the bundled `.dotnet` instead of the system SDK

`librewpf`'s projects (and by extension anything consuming `LibreWPF.Sdk`) target
`net11.0-windows`/`net11.0`, a preview TFM the system-installed .NET SDK doesn't recognize. All
`dotnet build`/`dotnet pack`/`dotnet restore` calls above must go through
`librewpf/.dotnet/dotnet` (and the matching `DOTNET_ROOT`/`MSBuild*` env vars), not whatever
`dotnet` resolves to on `PATH` — otherwise you'll hit `NETSDK1045: The current .NET SDK does not
support targeting .NET 11.0`.

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
    <add key="ProGPUWpfLocalArtifacts" value="/Users/lextm/uno-tools/librewpf/artifacts/packages/Release/NonShipping" />
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
PID=$(pgrep -f "bin/Debug/net11.0-windows/SharpDevelop$" | head -1)
osascript -e "tell application \"System Events\" to set frontmost of first process whose unix id is $PID to true"
```
