# NuGet Package Management — Status

## Current state

**The addin works end-to-end on macOS/arm64, for both offline/local feeds and real nuget.org.**
`tests/OpenDevelop.IntegrationTests/NuGetAddInTests.cs` (`SearchAndInstallPackage_UpdatesProjectFile`)
passes:

```bash
dotnet build src/Main/SharpDevelop/SharpDevelop.csproj -c Debug
dotnet build tests/OpenDevelop.IntegrationTests/OpenDevelop.IntegrationTests.csproj -c Debug
dotnet exec tests/OpenDevelop.IntegrationTests/bin/Debug/net10.0/OpenDevelop.IntegrationTests.dll \
  -class "OpenDevelop.IntegrationTests.NuGetAddInTests"
# => Total: 1, Errors: 0, Failed: 0
```

It opens a project, loads the addin, points the package source at a local offline feed, drives the
real search box (`SearchCommand`), asserts real result rows, installs via the real per-row
`AddPackageCommand`, and confirms the `.csproj` on disk actually gained the `PackageReference`. A
live search against real nuget.org (`https://api.nuget.org/v3/index.json`, term
`newtonsoft.json`) was also manually verified this session (see "How search was fixed" below) — not
kept as an automated test, since this repo avoids network-dependent tests in the suite.

This document previously described an 8-slice plan to replace the entire legacy `NuGet.Core` engine
with modern `NuGet.Client` libraries, on the premise that the addin was fundamentally broken on
non-Windows hosts. Most of that premise turned out to be already resolved by earlier (untracked)
work; the two remaining real gaps — live search, and the PowerShell Console pad being built but
disconnected — were both fixed this session. Details below.

## What already worked (from earlier, untracked work)

1. **Crash #1 (Windows-only `NuGet.Console.Types.dll` PE32 assembly, PowerShell Console pad)** —
   fixed by removing that reference. A *separate*, cross-platform PowerShell Package Console pad
   was wired up this session instead — see "PowerShell Console + EnvDTE" below.
2. **Crash #2 (legacy `NuGet.Settings`'s named-`Mutex` constructor for `SettingsProvider`)** —
   worked around by `Src/PortableNuGetSettings.cs`, a hand-rolled `NuGet.Core.ISettings`
   implementation (plain `XDocument` load-modify-save, no `Mutex`). Not the "real"
   `NuGet.Configuration.Settings.LoadDefaultSettings` swap originally planned, but it works.
3. **Install writes the real `PackageReference`** — `Src/SdkStylePackageReferenceService.cs` edits
   the MSBuild project file directly (`ProjectItemElement`/`ProjectItemGroupElement`, matching how
   `dotnet add package` itself edits a `.csproj`), saves it, refreshes the Solution Explorer tree,
   then shells out to `dotnet restore` (optionally `--source <feed>`). Wired in from
   `PackageManagementProject.cs`. Package content is fetched by that `dotnet restore` process
   (modern, correct protocol handling), never by the legacy in-process engine — this is why install
   already worked even before this session's search fix.
4. **Restore command already uses `dotnet restore`** — `RestorePackagesCommand.cs` +
   `NuGetPackageRestoreCommandLine.cs`, not the legacy `NuGet.exe`/`NuGet.Core` restore path this
   doc's old slice 7 was written to replace.

## How search was fixed (this session)

Verified live nuget.org search was actually broken in three layered ways, each masking the next:

1. Legacy NuGet.Core's OData V2 repository (`DataServicePackageRepository`, used for any real
   HTTP(S) source) needs `System.Data.Services.Client`, which has no package for modern .NET at all
   (confirmed: not on nuget.org, not vendored in `RequiredLibraries/`). **Fix**: search no longer
   goes through legacy `IPackageRepository.Search(...)` at all. New
   `Src/NuGetPackageSearchService.cs` calls real, current `NuGet.Protocol`
   (`Repository.Factory.GetCoreV3(source)` → `PackageSearchResource.SearchAsync(...)`, same approach
   as UnoDevelop's own `NuGetPackageSearchService.cs`), and new `Src/NuGetSearchResultPackage.cs`
   adapts each `IPackageSearchMetadata` result back into the legacy `IPackage` shape the rest of the
   pipeline (`PackagesViewModel`/`PackageViewModel`/`PackageFromRepository`) still expects — so only
   `AvailablePackagesViewModel.GetAllPackages` changed, not the whole 124-file legacy-`IPackage`
   surface.
2. Merely reading `.Source` on a legacy `DataServicePackageRepository` (for an http(s) source)
   *also* throws: it lazily resolves an HTTP redirect via `NuGet.RedirectedHttpClient`, which
   initializes legacy NuGet.Core's own static `NuGet.ProxyCache`, which constructs a legacy
   `NuGet.Settings` using the same Windows-only named-`Mutex` syntax as crash #2 above — a *third*,
   independent instance of that bug family, inside repository access itself rather than settings or
   search. **Fix**: `AvailablePackagesViewModel.GetAllPackages` now reads the source URL from
   `RegisteredPackageRepositories.ActivePackageSource.Source` (the plain `PackageSource` string) and
   never touches `repository.Source`. `RegisteredPackageRepositories.CreateActiveRepository()` also
   got a try/catch fallback to a new minimal `Src/NuGetRemoteSourceRepository.cs` (an `IPackageRepository`
   that only carries `.Source`) in case *construction* itself ever throws for some other source type
   — construction itself turned out fine for `https://api.nuget.org/v3/index.json`, only `.Source`
   access was the trap, but the fallback costs nothing and guards the same class of bug elsewhere.
3. Pinning `NuGet.Protocol` to a fresh version (tried 6.15.1→resolved 7.0.0, then 7.6.0) caused a
   `FileLoadException` ("manifest definition does not match") at runtime: the main app's own MSBuild
   internals (`src/Main/Base/Project/Src/Project/MSBuildInternals.cs`) already load `NuGet.Common`/
   `NuGet.Protocol`/`NuGet.Configuration` transitively at version **6.13.2** (verified via
   `project.assets.json`), and loading a byte-different assembly under the same strong name from this
   addin collides with that already-loaded copy. **Fix**: pinned `PackageManagement.csproj`'s
   `NuGet.Protocol` reference to exactly `6.13.2` to match.

All three were found the hard way — by actually running a live search against nuget.org (not just
the offline fixture) and reading through the full exception each time, not by inspecting code. The
offline `LocalPackageRepository` path never exercised any of the three, which is exactly why
`NuGetAddInTests` alone didn't already catch this.

## PowerShell Console + EnvDTE (this session)

**EnvDTE was already fine** — `SharpDevelop.EnvDTE.vbproj` (86 files) already builds clean and is
already referenced from `PackageManagement.csproj`; nothing needed changing.

**The PowerShell Package Console pad was ~90% already built and just never wired in.** Found two
whole sibling projects sitting disconnected from the solution and from `PackageManagement.csproj`:
`PowerShell/Project/PackageManagement.PowerShell.csproj` (a `PSHost`/`IPowerShellHost` implementation
on `Microsoft.PowerShell.SDK` 7.4.0 — cross-platform PowerShell 7/Core embedded in-process, no
external PowerShell install needed) and `Cmdlets/Project/PackageManagement.Cmdlets.csproj` (a full
`Install-Package`/`Uninstall-Package`/`Get-Package`/`Get-Project`/`Update-Package`/
`Invoke-InitializePackages`/`Invoke-UpdateWorkingDirectory` cmdlet set). The WPF console pad UI itself
(`Src/Scripting/PackageManagementConsolePad.cs`/`PackageManagementConsoleView.xaml`, AvalonEdit-based)
was also already complete. What was missing:

1. `PackageManagement.csproj` had no `ProjectReference` to `PackageManagement.PowerShell.csproj`, and
   excluded `ICmdletLogger.cs`/`PackageManagementConsoleHost.cs`/`PackageManagementConsoleHostLogger.cs`
   from compilation. **Fix**: added the reference, re-included those 3 files (left
   `ConsoleInitializer.cs`/`VisualStudio/ComponentModel.cs` excluded — they're VS SDK/MEF-service
   shims for hosting inside real Visual Studio's service container, referencing an `IConsoleInitializer`
   type that only ever existed in the removed `NuGet.Console.Types.dll`; not needed for our own pad).
2. `Src/Scripting/PowerShellDetection.cs` checked the Windows registry for a system-installed Windows
   PowerShell 2.0 (`Microsoft.Win32.Registry`, Windows-only, and obsolete even on Windows).
   **Fix**: rewritten to just return `true` — PowerShell is embedded via the SDK package, not
   externally installed, so there's nothing to detect.
3. `PackageManagementConsoleHostProvider.CreateConsoleHost()` unconditionally constructed
   `PowerShellMissingConsoleHost` (a stub that prints "PowerShell is not installed"), ignoring the
   detection result entirely. **Fix**: constructs the real `PackageManagementConsoleHost` when
   `IPowerShellDetection` says PowerShell is available (now always).
4. `IPackageManagementConsoleHost` was missing the `CreateLogger(ICmdletLogger)` member both concrete
   host classes already implemented, so `Cmdlets.csproj` failed to build against the interface.
   **Fix**: added it to the interface (and to `PowerShellMissingConsoleHost`'s stub implementation).
5. `PackageManagement.Cmdlets.csproj`'s `Install/Uninstall/UpdatePackageCmdlet.cs` had an ambiguous
   `SemanticVersion` reference (`System.Management.Automation.SemanticVersion`, introduced by the
   PowerShell SDK, vs. `NuGet.SemanticVersion`). **Fix**: explicit `using SemanticVersion =
   NuGet.SemanticVersion;` alias in those 3 files.
6. No `<Pad>` codon existed in `PackageManagement.addin` for `PackageManagementConsolePad` (the
   string resource `AddIns.PackageManagement.ConsolePad.Title` already existed, unused). **Fix**:
   added the Pad entry under `/SharpDevelop/Workbench/Pads` (icon `PadIcons.Output`, matching the
   existing Output pad).
7. Added both projects to `OpenDevelop.Mvp.slnx` for IDE visibility (MSBuild already picked them up
   transitively via the new `ProjectReference` regardless).

Verified this session: `PackageManagement.csproj`, `PackageManagement.PowerShell.csproj`,
`PackageManagement.Cmdlets.csproj`, and the full `SharpDevelop.csproj` app all build with 0 errors;
`NuGetAddInTests` still passes (no regression); and a temporary DevFlow-driven smoke test (`od.pads`
→ `od.show-pad` on `ICSharpCode.PackageManagement.Scripting.PackageManagementConsolePad`) confirmed
the pad registers and activates without crashing — which exercises the full real path (constructs
the WPF view → binds the view model → creates the real console host → spins up an actual PowerShell
runspace on a background thread), not just registration. Not independently verified: that typed
PowerShell commands (`Install-Package`, etc.) actually execute correctly end-to-end — the smoke test
only confirms the host starts without throwing.

## What's still genuinely legacy (not blocking)

- **Install internals are still legacy `NuGet.Core`** for the small remaining surface that isn't
  `SdkStylePackageReferenceService` (~124 files still `using NuGet;` overall, most of it
  `IPackage`/`IPackageManagementProject` plumbing that search no longer touches). Not a correctness
  problem today — install writes the real `PackageReference` and `dotnet restore` does the real
  fetch — but a full swap to `NuGet.PackageManagement.NuGetPackageManager`/`NuGetProject` (this doc's
  old slices 3/5) would be the "no legacy engine left at all" version, if ever wanted.
- **Update tab** (`UpdatedPackagesViewModel`/`UpdatedPackages`) is real, non-stub code using the
  legacy engine's diff logic — not independently exercised by `NuGetAddInTests`, not re-verified this
  session.
- **`RegisteredPackageSourcesView`/`PackageManagementOptionsView`** (Tools > Options panels) still
  bind to legacy `PackageSource`/`RegisteredPackageSources` — not re-verified this session, but
  nothing in the passing test path touches them; `PackageManagementDevFlowActions.SetLocalFeed`
  manages sources directly through `RegisteredPackageRepositories` instead.
- **PowerShell Console pad command coverage** — the pad now loads and runs a real PowerShell
  runspace (see above), but only the cmdlets already written in `Cmdlets/Project/Src/` are
  available (`Install-Package`/`Uninstall-Package`/`Get-Package`/`Get-Project`/`Update-Package`/
  `Invoke-InitializePackages`/`Invoke-UpdateWorkingDirectory`) — no `install.ps1`/`uninstall.ps1`
  package-script execution was added or verified this session.
- **EnvDTE compatibility shim** (`Src/EnvDTE/`, 86 files) — confirmed already building cleanly and
  already wired into `PackageManagement.csproj`; not otherwise touched or independently re-verified
  beyond compiling.
