# OpenDevelop Addin SDK

## Status

The first usable SDK slice is implemented under `src/SDK/OpenDevelop.Addin.Sdk/` and packs as
`OpenDevelop.Addin.Sdk` `0.1.0-preview.1`. It is intentionally a *composable* MSBuild SDK: it adds
OpenDevelop deployment behavior to the project's chosen compile SDK instead of assuming that every
addin uses `Microsoft.NET.Sdk`. A WPF addin will ultimately use:

```xml
<Project Sdk="LibreWPF.Sdk;OpenDevelop.Addin.Sdk/0.1.0-preview.1">
  <PropertyGroup>
    <OpenDevelopAddin>true</OpenDevelopAddin>
    <OpenDevelopAddinKind>InProcess</OpenDevelopAddinKind>
  </PropertyGroup>
</Project>
```

Until the package is published to the repository feed, `Directory.Build.targets` is a compatibility
shim: it discovers legacy addins from an `OutputPath` under `AddIns/` and imports the exact same SDK
targets. This avoids maintaining a second implementation during migration.

## Problem

The repo-root `AddIns/<Category>/<Name>/` deployment tree carries **~3.4 GB of managed
DLLs**, of which only **~300 MB is unique content** (823 distinct assemblies). Measured
waste: **89%** — thousands of byte-identical copies of Roslyn (×32), the WPF framework
assemblies (×33), MSBuild, Xceed.Wpf.Toolkit, etc.

Two distinct consumers share this tree, with conflicting needs:

| Consumer | Loads via | Needs local copies of |
|---|---|---|
| In-process addins | AddInTree → default AssemblyLoadContext | Nothing the base app provides (app-base resolution covers it) |
| Out-of-process hosts (`*/Host/` folders: FormsDesigner.Host, WpfDesign SurfaceHost, UnoHost) | Own process + own `deps.json` | Their full dependency closure (but NOT shared-framework assemblies, which the local `dotnet` install provides) |

A post-build hardlink dedup could hide the waste, but it treats the symptom: files are
written (multi-GB IO per full build) and then linked away. The right fix is to not emit
them at all.

## Existing infrastructure (do not duplicate)

`Directory.Build.targets` already contains `RemoveHostProvidedFilesFromAddInCopyLocal`,
used by the DISTRIBUTION flow only (`dist.macos.ps1` passes
`-p:OpenDevelopDistributionBuild=true -p:OpenDevelopHostPublishDir=<snapshot>`): while
addin copy-local items are gathered, anything whose filename+extension matches the
published app snapshot is removed. Semantics: **filename+extension match, version-blind,
fail-open** (snapshot absent ⇒ no trim).

Phase 1 reuses exactly these semantics, extended to developer builds and hardened with
two exclusions the dist flow never needed.

## Design

### Consumption model

* **New projects:** compose `OpenDevelop.Addin.Sdk` with `LibreWPF.Sdk`,
  `Microsoft.NET.Sdk`, or another compile SDK. The Addin SDK owns deployment only.
* **Legacy projects:** the repository compatibility shim opts them in based on their existing
  `AddIns/` output path. Each project can then migrate without changing behavior.
* **Out-of-process programs:** set `OpenDevelopAddinKind=OutOfProcessHost`. They are never subjected
  to in-process host trimming because they have their own runtime/deps graph.
* **Supporting libraries deployed into an AddIn directory:** use
  `OpenDevelopAddinKind=InProcessDependency`. They are trimmed like the AddIn but are not required
  to own or deploy a `.addin` manifest.

The SDK also discovers and deploys `*.addin` beside the project, removing the repeated
`CopyAddInManifest` target from individual projects. An explicit `OpenDevelopAddinManifest` item is
supported for nonstandard layouts.

### F5 / debugger loop for in-process addins

An in-process addin is normally a class library, so it has no executable start target of its own.
The SDK now makes it startable without a project-system special case. For a project with
`OpenDevelopAddin=true` and `OpenDevelopAddinKind=InProcess`, it supplies the ordinary project
start properties when the project has not already set them:

| Property | SDK value | Purpose |
|---|---|---|
| `StartAction` | `Program` | Makes an addin library startable from Run/Debug. |
| `StartProgram` | `$(OpenDevelopDebugHost)` | Starts a second OpenDevelop instance. The default resolves to `OpenDevelop` in `$(OpenDevelopHostBin)`. |
| `StartWorkingDirectory` | `$(OpenDevelopHostBin)` | Gives the child its normal host probing context. |
| `StartArguments` | `-addindir:<addin output> -configdir:<project>/.od-experimental-instance -devflow:off` | Loads the just-built addin, isolates the experimental profile, and avoids competing with the parent IDE's DevFlow port. |

Consequently, opening an addin project and pressing F5 starts an isolated OpenDevelop child that
loads the build output directly. Starting under the debugger also supports pending breakpoints in
the addin: the debugger binds them when the child loads the addin assembly. This works for an
addin living outside the OpenDevelop checkout as long as it supplies `OpenDevelopHostBin` (or the
consuming repository's `Directory.Build.targets` supplies the equivalent host location).

Every value is conditionally assigned. An addin may override `OpenDevelopDebugHost`,
`OpenDevelopDebugConfigDir`, `StartProgram`, `StartArguments`, or `StartWorkingDirectory`; the SDK
does not overwrite an explicit project choice. This loop intentionally applies only to
`InProcess` addins: an `OutOfProcessHost` already has its own executable and launch contract.

For an integration suite that must drive the child, set `OpenDevelopDebugDevFlowPort` (the Stride
suite supplies it through `OPENDEVELOP_ADDIN_TEST_DEVFLOW_PORT`). The SDK then emits
`-devflow:<port>` instead of `-devflow:off`; the child instance uses that dedicated endpoint while
the parent keeps its own port. This is an explicit test opt-in—ordinary F5 remains isolated and
does not expose DevFlow.

The real Stride addin exercises this path in
`StrideGameStudioIntegrationTests.StrideAddInProject_IsStartable_AndDebuggingBreaksInsideTheAddIn`.
The test opens the addin project, checks the evaluated start properties, sets a breakpoint in its
autostart command before launching, and verifies that a debug session stops in the addin source.
It needs only the Stride checkout and an installed OpenDevelop selected by `OPENDEVELOP_APP_PATH`:
the addin is deliberately **not** copied into the installed application's `AddIns` tree. The SDK
passes the addin output to the debug child through `-addindir:`.

### Baseline manifest

The host build writes `$(TargetDir)OpenDevelop.host-assemblies.txt` — newline-separated filenames
of every assembly in the app output. `TargetDir` is used deliberately: composing
`MSBuildProjectDirectory` with `OutDir` breaks when `OutDir` is already absolute. Addins require the
manifest as the readiness marker and use the corresponding host directory as the source of truth.
If it does not exist yet, trimming fails open; the repository build entry point builds the host
before the addin graph so ordinary full builds do not take that path.

### Trim rules (per addin project, `AfterTargets="Build"`)

Delete from the project's `$(OutputPath)` top level any `.dll` whose filename appears in
the baseline manifest, unless:

1. **Host-process exemption** — the output folder is a standalone-host deployment
   (`…/Host/`). Hosts must stay self-contained; instead the SDK *later* trims only
   shared-framework assemblies from them (Phase 2, see below).
2. **Explicit allow-list** — `<OpenDevelopAlwaysCopy Include="SomeAssembly.dll"/>` opts a
   specific assembly back in (version-conflict escape hatch).
3. **Opt-out** — `-p:OpenDevelopTrimHostAssemblies=false` disables the SDK directly;
   the legacy `OpenDevelopTrimAddinCopyLocal` property is mapped by the repository shim.

Matching is filename+extension, version-blind — same as the dist target. Risk: an addin
needing a DIFFERENT version of a base-provided assembly silently loses it. Mitigation:
the escape list in (2); plus the integration suite exercises real load paths for every
major addin, and a mismatch surfaces immediately at AddInTree load.

### Why post-Build delete instead of filtering copy-local items?

Filtering item lists (RAR output / `GetCopyToOutputDirectoryItems`) requires running
before copy-local *and* guarantees the app bin is already populated — but addins do not
all depend on the exe project, so build order gives no such guarantee inside one
`dotnet build`. A delete-pass after each addin's `Build` is order-independent (fails
open when the manifest is absent), covers transitive content flow (`GetCopyToOutputDirectoryItems`
propagation — the reason `ICSharpCode.WpfDesign.AddIn.dll` once appeared in six unrelated
folders) with zero extra targets, and costs nothing measurable next to the multi-GB copy
itself.

### Assembly conflicts

The default load context makes a private copy with the same simple assembly name as a host assembly
misleading: the host copy normally wins regardless of what sits in the addin folder. Therefore a
future SDK task must compare assembly identity and file hashes and fail with a useful diagnostic on
a mismatch. `OpenDevelopAlwaysCopy` is retained only as a temporary compatibility escape hatch; a
real side-by-side dependency requires an isolated `AssemblyLoadContext`, not merely another DLL.

### Host processes (out-of-process)

`…/Host/` folders remain exempt from parent-host assembly trimming. Distribution builds do,
however, remove compiler XML documentation, `ref/` assemblies, and runtime-native trees for other
operating systems. Portable LibreWPF implementations are restored from `lib/net10.0` before a WPF
child is deployed; reference-pack DLLs are not executable substitutes, and a child process cannot
resolve them from the parent application's base directory. Cross-host assembly dedup stays out of
scope until version unification and a shared probing path are proven.

## Rollout

| Phase | Scope | Expected effect |
|---|---|---|
| 1 (implemented) | Packable composable SDK, legacy shim, host manifest, automatic `.addin` deployment, in-process trim, host exemption, deployment test | Stop duplicating host assemblies |
| 2 (migration implemented) | Publish SDK; all 57 projects that deploy into `AddIns/` now explicitly declare their deployment kind; add assembly identity/hash conflict diagnostics | Remove per-project boilerplate safely |
| 3 (safe asset pruning implemented) | RID-aware native pruning, XML/reference removal, portable WPF child-runtime restoration; optional package/deployment manifest remains | Host folders shrink without breaking deps.json |

## Regression safety net

* The three out-of-process hosts are exercised end-to-end by existing integration tests
  (`FormsDesigner_*`, `WpfDesigner_*`, `WinUIDesigner_*`) — a broken host deployment
  fails loudly.
* In-process addin loading is exercised by nearly every suite fact (AddInTree parses
  every deployed `.addin` at startup; a missing assembly surfaces on first use).
* `StrideAddInProject_IsStartable_AndDebuggingBreaksInsideTheAddIn` covers the SDK's development
  loop against an out-of-repository addin: F5/debug starts an isolated child with `-addindir:`,
  retains a separate config directory, and binds a pending breakpoint when the addin module loads.
  It is conditional only on the local Stride checkout and installed OpenDevelop being available;
  the installed host does not carry a Stride addin payload.
* Bisection hatch: `-p:OpenDevelopTrimAddinCopyLocal=false`.

The focused build-level regression test is:

```bash
dotnet msbuild tests/OpenDevelop.AddinSdk.Tests/OpenDevelop.AddinSdk.Tests.proj -t:Build
```

It constructs a fake host and addin deployment, then asserts that a host-provided DLL is removed,
an addin-private DLL and runtime XML remain, the `.addin` manifest is deployed, and XML docs,
reference assemblies, and foreign native runtimes are removed. `dotnet pack
src/SDK/OpenDevelop.Addin.Sdk/OpenDevelop.Addin.Sdk.csproj` additionally verifies the NuGet/MSBuild
SDK package layout.

## Coordinated macOS release

`release.macos.ps1` (or `release.macos.sh`) releases a macOS DMG containing the local
`OpenDevelop.Addin.Sdk`. It creates a GitHub **draft** release, uploads the DMG, and only then
publishes the draft. The SDK is not pushed to NuGet.org: the installed application bundles both its
SDK files and an MSBuild SDK resolver, so external projects can use `Sdk="OpenDevelop.Addin.Sdk"`
against the matching installed IDE.

```sh
./release.macos.sh --version 0.1.0-preview.2 --prepare-only
./release.macos.sh --version 0.1.0-preview.2
```

The first form performs no remote mutation. The second requires an authenticated `gh` session (or
`GITHUB_TOKEN`). Releases reject a dirty worktree and an existing versioned artifact directory by
default; use `-AllowDirtyWorktree` only for an explicit exception.

## macOS distribution acceptance (2026-08-22)

The complete `dist.macos.sh` flow was rebuilt in Release mode, its packaged application passed the
10-second startup smoke test, and the DMG was regenerated. Compared with the previous checked local
artifact, the DMG changed from 223,329,764 to 173,454,366 bytes: 49,875,398 bytes (22.33%) smaller.
The second safe-asset-pruning pass alone reduced the preceding 197,868,390-byte DMG by another
24,414,024 bytes (12.34%). The resulting application is 454,132 KiB and its `AddIns/` payload is
318,656 KiB. It contains no `ref/` tree or foreign Windows/Linux runtime tree and retains only one
runtime XML file (`Decompiler/Layouts/ILSpy.xml`).
