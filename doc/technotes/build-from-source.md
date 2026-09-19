# Build From Source

How to build OpenDevelop from this checkout, and how to reuse the release scripts
(`dist.windows.bat` / `dist.macos.sh`, and `dist.ps1` underneath them) while developing and testing -
not just when cutting a release.

Everything here also applies to the automated test suite; for the authoritative command list and the
rules for running it, see [integration-testing.md](integration-testing.md). The two documents are
kept in sync deliberately: **when a build command changes, update both.**

## The scripts at a glance

All the logic lives in PowerShell (`pwsh` 7+); the `.bat` / `.sh` files are thin wrappers that only
locate `pwsh` and translate POSIX-style flags (`--skip-publish` → `-SkipPublish`, `--debug` →
`-Configuration Debug`). Windows PowerShell 5.1 is deliberately not accepted - both wrappers say so.

| Script | What it is for |
|---|---|
| `build/common.psm1` | Shared helpers used by `build.ps1` / `launch.ps1` / `dist.ps1`: `Find-DotNetHost`, `Find-VsMsBuild`, `Invoke-Native` (fail-on-non-zero, tees stdout to a temp log), `Set-DotNetEnv`, `Clear-RepoAddIns`, `Restore-Solution`, `Build-Solution`, `Get-PinnedGitVersionProperties`, `Remove-StaleMsBuildAssets`. |
| `build.ps1` | **The inner loop.** Build ONE project (or a shortcut/fuzzy name) correctly. See below. |
| `launch.ps1` / `launch.sh` | Build the whole solution and run the app; `-NoBuild`, `-BuildOnly`, `-Configuration`, files to open. |
| `dist.ps1` | The publish/packaging pipeline, split into selectable phases. Also the fastest way to refresh a payload after a one-project change. |
| `dist.windows.bat` / `dist.macos.sh` | Release entry points; wrappers around `dist.ps1` that also set up the local toolchain (macOS pins `SDKROOT` when the default `cc` cannot link). |
| `rebuild-all.sh` | macOS: repack LibreWPF → restore → build → run in one command (`--fast`, `--no-repack`, `--build-only`). |
| `repack-librewpf.ps1` / `repack-librewpf.sh` | Build + pack the local LibreWPF/ProGPU packages into the feed and clear the stale `~/.nuget/packages` copies. Only needed when changing the LibreWPF fork itself. |
| `release.macos.ps1` / `release.macos.sh` | Cuts a macOS DMG release (draft GitHub release); bundles the Addin SDK. See [addin-sdk.md](addin-sdk.md). |

Ignored on purpose: `debugbuild.bat`, `releasebuild.bat`, `clean.bat`, `buildSetupAndRunTests.bat`
and the repo-root `SharpDevelop.sln` are pre-`.slnx` legacy (hard-coded MSBuild 12.0 / `SharpDevelop.sln`)
and no longer describe this tree. Use the `.slnx` and the scripts above.

## Prerequisites

- **.NET 10 SDK.** `global.json` pins the SDK band and the `LibreWPF.Sdk` MSBuild SDK version; build
  with the `dotnet` that resolves through it (the scripts call `Find-DotNetHost` for exactly this),
  not with an arbitrary host on `PATH`.
- **Submodules.** `git submodule update --init --recursive`. `externals/*` (WpfDesigner, cps,
  wpftoolkit, XamlToCSharpGenerator, ...) are compiled sources; nothing builds without them.
- **Windows: Visual Studio with the MSBuild component.** Projects whose path contains `MicrosoftHost`
  cannot be built by `dotnet build` (below), and `Find-VsMsBuild` locates the right MSBuild via
  `vswhere`.
- **PowerShell 7+** for any `.ps1`; the `.bat`/`.sh` wrappers only need `pwsh` on `PATH` (or in the
  well-known Homebrew/Program Files locations).
- **Local LibreWPF packages.** `NuGet.config` maps `LibreWPF.*`, `LibreWinForms.*`, `ProGPU.*` and
  `LeXtudio.*` to the sibling `../openavalon/artifacts/local-feed` and clears every other source.
  Without that feed present AND populated, restore fails with "No packages exist with this id in
  source(s): librewpf-local".
- **macOS:** `rsync` (the bundle script requires it), plus a working C toolchain. `LibreWPF.Sdk`
  compiles a native Win32-compat shim with `cc -dynamiclib`; `dist.macos.sh` probes for a linkable
  macOS SDK and pins `SDKROOT` if the default one is too old for the linker.

## Why not a bare `dotnet build` - the three traps

`build.ps1` is not a convenience wrapper; a naive `dotnet build <csproj>` silently produces a
**broken** assembly in this repo in three distinct ways (each has cost a debugging session):

1. **GitVersion drift → version mismatch.** Every project links `src/Main/GlobalAssemblyInfo.cs`,
   which the GitVersion MSBuild task regenerates on *every* invocation and can compute a different
   revision between two runs minutes apart. The rebuilt assembly then references a host version that
   no longer exists on disk, so the AddIn silently fails to load and every one of its classes is
   reported as *"Cannot find class …"*. `build.ps1` pins `-p:GitVersion_*` to the values already in
   `GlobalAssemblyInfo.cs` (`-NoPinVersion` opts out).
2. **Architecture stamping.** Without `-p:ProGpuWpfUseCurrentRuntimeIdentifier=false`,
   `LibreWPF.Sdk` falls back to the **build machine's** RID and emits an architecture-stamped
   assembly. It loads on the machine that built it, then throws *"The assembly architecture is not
   compatible with the current process architecture"* elsewhere - including in the x64 OpenDevelop
   process on an ARM64 machine, where it takes the app down at startup. `build.ps1` passes this by
   default; `-NativeRid` opts out.
3. **Projects `dotnet build` cannot build at all.** Anything under a `MicrosoftHost` folder is
   routed to Visual Studio's MSBuild: WinUI 3's `UseWinUI` pulls in `MrtCore.PriGen.targets`, whose
   tasks ship only with Visual Studio (`MSB4062` otherwise), and an unpackaged WinUI 3 app must be
   built RID-specific (one build per architecture). `build.ps1` detects this and builds both
   `win-x64` and `win-arm64` for the WinUI child, RID-less for the WinForms host - the same thing
   `dist.ps1`'s `designer-hosts` phase does.

## Inner loop: `build.ps1`

```bash
./build.ps1 shell                       # the IDE host (src/Main/SharpDevelop)
./build.ps1 base -Then shell            # a shared assembly, then the host, one pinned pass
./build.ps1 unodesignhost -Kill         # one AddIn, killing file locks first
./build.ps1 winui -List                 # what does this fuzzy name match? (builds nothing)
./build.ps1 sln                         # the whole OpenDevelop.Mvp.slnx
./build.ps1 shell -Configuration Release
```

Shortcuts: `shell`/`host`, `base`, `core`, `sln`/`all`. Anything else is resolved as a path or a
fuzzy match over every `*.csproj` under `src/`, preferring a project whose name *ends with* what you
typed (so `unodesignhost` picks the project, not its `.Remote`/`.Tests` siblings).

Rules that bite when you ignore them:

- **Shared assemblies are copied into every AddIn folder that references them.** Rebuilding
  `Main/Base`, `Main/Core`, `Main/Designer` or `ICSharpCode.SharpDevelop.Widgets` refreshes only the
  shared copy, so the app can still load a stale per-AddIn copy. `build.ps1` prints a warning; the
  reliable fix is a full `./build.ps1 sln`.
- **A shared widget is not an AddIn.** `DesignerCanvas` and friends reach the distribution through
  the **host publish**, not through `AddIns/`, so `./build.ps1 <addin>` cannot refresh them (the app
  then dies at document-open with a signature-level `MissingMethodException`). Use
  `./dist.ps1 -From host`.
- **Build the *UnoDesignHost* project to change the WinUI designer client.**
  `UnoDesignSurfaceControl.cs` / `UnoDesignRuntimeHost.cs` live in `WinUIXamlDesigner.UnoDesignHost`,
  whose `OutputPath` is the deployed AddIn folder; the dependency edge runs
  `UnoDesignHost -> AddIn` (the opposite of the folder layout), so building the AddIn project alone
  leaves an old client DLL in place. Build `unodesignhost`.
- **A WPF designer change touches several assemblies.** `WpfSurfaceHostService.cs` is source-linked
  into two hosts (`WpfDesign.SurfaceHost` and `MicrosoftWpfDesign.SurfaceHost`), there is a third
  hand-maintained RPC wrapper (`MultiDocumentWpfSurfaceHostService.cs`), and the client AddIn is a
  plugin not referenced by the shell. See the matching sections of
  [wpf-designer.md](wpf-designer.md).
- **Kill before rebuilding deployed DLLs.** Out-of-process designer hosts outlive the IDE by design
  (`SharedDesignerHostPool`); a live process makes the copy step fail with `MSB3021`/`MSB3027`
  *after* a successful compile. `-Kill` stops `OpenDevelop`, `OpenDevelopARM64`, `dotnet` and
  `MSBuild` first.

`-ForDistribution` is required when the output will be dropped into a payload: it adds
`OpenDevelopDistributionBuild` / `OpenDevelopDistributionRidFamily` /
`ProGpuWpfCopyPackageRuntimeAssets`, without which the Addin SDK leaves other platforms' native
assets (`runtimes/linux-*/native/libglfw.so.3`, …) in `AddIns/` and the payload validator rejects the
result with *"Distribution payload contains build-only or foreign assets"*.

## Build and run the app: `launch.ps1` / `launch.sh`

```bash
./launch.ps1                       # restore, build the solution, then run
./launch.ps1 -NoBuild              # just (re)run the last build output
./launch.ps1 -BuildOnly            # build, do not launch (what the integration tests expect)
./launch.ps1 -Configuration Release
./launch.ps1 SomeFile.xaml         # files to open on launch
./launch.sh --build-only           # macOS wrapper for the same thing
DEVFLOW_DISABLE=1 ./launch.sh      # run without the DevFlow agent
```

In order, it: clears the repo-root `AddIns/` (so stale output from earlier revisions cannot be
loaded), restores the solution, builds the **host project first** (so the AddIn baseline manifest
exists before any AddIn's post-build trim runs), builds the solution, removes stale MSBuild
`.targets`/`.props` copies that would confuse in-process evaluation, re-applies the restore-selected
LibreWPF transport runtime, sets the MSBuild environment variables the app's in-process MSBuild
hosting needs, and finally `dotnet run --no-build`s the host.

Two things it deliberately does **not** do:

- It does not build the **Microsoft designer hosts** (`FormsDesigner/MicrosoftHost`,
  `WinUIXamlDesigner.MicrosoftHost`). Those need Visual Studio's MSBuild and come from
  `./dist.ps1 -Phase designer-hosts` (Windows) or `./build.ps1 <MicrosoftHost project>`. Tests that
  assert the Microsoft backends require them.
- It does not produce a distribution. `launch.ps1` output is a **development** tree, correct for the
  machine/architecture that built it. Packaging is `dist.ps1` (next).

To open the running app under the test agent's conditions, set `OD_TEST_MODE=1` (the window shows
without stealing focus) - see [devflow.md](devflow.md) and
[integration-testing.md](integration-testing.md).

## Using `dist.ps1` as a development tool

`dist.ps1` is the release pipeline, but it is also the fastest way to rebuild and re-verify a
payload. It runs as ordered phases:

```
restore -> host -> addins -> designer-hosts (Windows only) -> payload -> smoke -> zip
```

```bash
./dist.ps1 -ListPhases                 # phases for this platform, plus worked examples
./dist.ps1 -Configuration Debug        # package a Debug build for local testing
./dist.ps1 -Phase payload              # re-assemble OpenDevelop-win from what is already built
./dist.ps1 -Phase payload,smoke        # ...and verify it actually starts
./dist.ps1 -From payload               # payload, smoke and zip
./dist.ps1 -Phase addins,payload       # rebuild all AddIns, then re-assemble
./dist.ps1 -Phase host                 # just the host publish
./dist.windows.bat --phase payload     # the .bat wrapper forwards flags too
./dist.windows.bat --debug             # Debug configuration
./dist.macos.sh --skip-publish         # macOS equivalent
```

- `-Phase` and `-From` are mutually exclusive; `-Phase` always executes in canonical order regardless
  of the order you list names in.
- `-SkipPublish` is kept as a synonym for `-From payload` ("reuse existing build output, just
  package it").
- `-Kill` stops anything holding the payload open first (a smoke run can leave a
  `dotnet exec OpenDevelop.dll` behind; designer hosts outlive the IDE), otherwise the next payload
  phase fails with an access-denied that now explains itself.
- Phases that consume earlier artifacts fail with an actionable message
  (*"Run './dist.ps1 -Phase host' first"*) instead of assembling around a missing host.

**The patch workflow** - roughly a minute instead of a full ~15-minute `dist.windows.bat`:

```bash
./build.ps1 unodesignhost -ForDistribution -Configuration Release
./dist.ps1 -Phase payload,smoke -Kill
```

Never hand-copy a DLL into `OpenDevelop-win/` instead: that skips the payload's by-name dedup and
its out-of-process-host exemptions, and has repeatedly produced a payload that looks patched but
crashes. The `payload` phase compiles nothing but the launchers and applies those rules every time.

The Windows payload is architecture-neutral (`OpenDevelop.dll` is started as
`dotnet exec OpenDevelop.dll`) except for the one thing that must be architecture-specific: the
launcher the user double-clicks. `Build-Launchers` publishes `src/Main/OpenDevelop.Launcher` once per
Windows RID, producing `OpenDevelop.exe` (win-x64) and `OpenDevelopARM64.exe` (win-arm64) plus the
shared `OpenDevelop.Bootstrap.*` companion files. macOS packages `OpenDevelop.app` and then the DMG.

## Local LibreWPF packages (only when changing the fork)

OpenDevelop consumes LibreWPF as NuGet packages from the sibling
`../openavalon/artifacts/local-feed` feed (`NuGet.config` maps every `LibreWPF.*`, `LibreWinForms.*`,
`ProGPU.*` and `LeXtudio.*` id there). The packages are produced from the `openavalon` checkout,
whose `LibreWPF` submodule is the fork of dotnet/wpf. Only when you change that fork (the SDK, the
transport package layout, or the ProGPU packages) do you need to repack and re-restore:

```bash
./repack-librewpf.ps1            # Windows
./repack-librewpf.ps1 -Fast      # ProGPU-only; do NOT use if src/Microsoft.DotNet.Wpf/src changed
./repack-librewpf.sh             # macOS
./rebuild-all.sh                 # macOS: repack, restore, build, run in one step
```

Because the repacked packages keep the *published* id and version (routed to the local feed by
`NuGet.config`'s `packageSourceMapping`), the matching `~/.nuget/packages/<id>/<version>` entries
must be deleted or restore just reuses the previously extracted copy; the scripts do that clearing.

## Release (brief)

- **Windows:** `dist.windows.bat` (Release) → `OpenDevelop-win.zip`, built from the phase pipeline
  above. `dist.ps1` validates the payload (addin manifests present, per-RID designer hosts present,
  no PDBs/`ref/`/foreign platform runtimes) before zipping.
- **macOS:** `release.macos.ps1` / `release.macos.sh --version <v>` cuts a draft GitHub release,
  uploads the DMG, then publishes the draft. The Addin SDK ships inside the bundle rather than on
  NuGet.org - see [addin-sdk.md](addin-sdk.md).

## Building before running the integration suite

[integration-testing.md](integration-testing.md) is the authoritative source for the suite's
prerequisites and invocation; the summary here must stay identical to its "Prerequisites" and
"Running the suite" sections.

The suite drives the **real** app as a child process, so it needs a runnable development tree, not a
distribution:

```bash
./launch.ps1 -BuildOnly                                                    # app + shared assemblies
dotnet build tests/fixtures/SampleTestProject/SampleTestProject.csproj     # baseline fixture
./dist.ps1 -Phase designer-hosts                                           # Windows: Microsoft backends
```

Some test classes need their own fixture built first (for example `IlSpyAddInTests` needs
`tests/fixtures/DebugTestApp/DebugTestApp.csproj`), and the WinUI/WPF Gallery corpora need their
external checkouts built (`OD_WINUI_GALLERY_ROOT`, `OD_WPF_GALLERY_ROOT`, and the opt-in
`OD_*_GALLERY_BUILD=1`); each test file documents its own prerequisites at the top.

Run it with the xunit v3 / Microsoft.Testing.Platform runner flags, **never** `dotnet test
--filter`:

```bash
dotnet run --project tests/OpenDevelop.IntegrationTests --no-build -- -class "OpenDevelop.IntegrationTests.GitAddInTests"
dotnet run --project tests/OpenDevelop.IntegrationTests --no-build -- -method "OpenDevelop.IntegrationTests.GitAddInTests.AddInsList_ContainsGitAddIn"
```

Never run two invocations concurrently - they share one app instance and one DevFlow port.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `MSB3021` / `MSB3027` "file in use" while building an AddIn under `AddIns/` | A live OpenDevelop or an out-of-process designer host holds the deployed DLL. `-Kill`, or stop `OpenDevelop`/`dotnet`/`MSBuild`. |
| AddIn loads but *"Cannot find class …"* everywhere | GitVersion drift - a partial rebuild produced a version that no longer matches the host. Rebuild the whole solution (`./build.ps1 sln`). |
| *"The assembly architecture is not compatible …"* | A build stamped the machine's RID. Rebuild with `-p:ProGpuWpfUseCurrentRuntimeIdentifier=false` (the scripts' default). |
| `MSB4236` "The SDK 'LibreWPF.Sdk' specified could not be found" | Known transient; retry once before investigating. |
| `MSB3202` project file not found | You used the stale root `SharpDevelop.sln`; use `OpenDevelop.Mvp.slnx` / the `.slnx`. |
| `MSB4062` `Microsoft.Build.Packaging.Pri.Tasks.dll` | You ran `dotnet build` on a `MicrosoftHost` project; use VS MSBuild (`build.ps1`/`dist.ps1` do this). |
| Payload validator: *"contains build-only or foreign assets"* | The AddIn was rebuilt without `-ForDistribution` (or the `runtimes/` tree was hand-edited). Rebuild with `-ForDistribution`, then `./dist.ps1 -Phase payload`. |
| `NU1301` / "No packages exist with this id in source(s): librewpf-local" | The sibling `openavalon/artifacts/local-feed` is missing or empty (and the feed path must use forward slashes - a backslash is not a path separator on macOS). Run `repack-librewpf`. |
| Full parallel build races (`MC1000` on `ICSharpCode.Core.Presentation.dll`, `CS0006` on `ICSharpCode.Data.Core.dll`) | Build those two projects first, or build with `-m:1` (the scripts do). |
| 12-byte corrupt `.g.resources` crashing at boot | Clear `src/Main/ICSharpCode.Core.Presentation/{obj,bin}` and rebuild (what `dist.ps1`'s host phase does). |

## Related

- [integration-testing.md](integration-testing.md) - the automated suite, its fixture model, and how
  to run one test (kept in sync with this document).
- [addin-sdk.md](addin-sdk.md) - AddIn deployment/trimming rules, the AnyCPU rule, and release
  packaging of the SDK.
- [devflow.md](devflow.md) - the DevFlow agent the tests drive.
- [wpf-designer.md](wpf-designer.md) / [winui-designer.md](winui-designer.md) /
  [designer-common.md](designer-common.md) - what to rebuild for each design host.
- `CLAUDE.md` - the short-form agent notes (overlaps this document; this one is the reference).
