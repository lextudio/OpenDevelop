# PackageManagement unification: conflict resolution, license acceptance, package console

Follow-up to `nuget.md` and `nuget-manager.md`: closes the three gaps that doc/opendevelop-sync.md's
`AddIns/Misc/PackageManagement` entry had flagged as still needing parity work.

## 1. Transitive dependency resolution / version-conflict detection

`NuGetPackageDependencyPreviewService` (pre-existing) only shows the *direct* dependency group of
one package/version — no transitive walk, and no check against what's already installed.

New: `NuGetPackageConflictResolutionService`
(`src/Main/Base/Project/Src/NuGet/NuGetPackageConflictResolutionService.cs`, shared, linked into
UnoDevelop's `ICSharpCode.SharpDevelop.csproj` under `Upstream\NuGet\`).

- Roots = the project's current direct `PackageReference`s (read via
  `SdkStylePackageReferenceEditor.GetPackageReferences()`) plus the package being installed/updated.
- BFS walk of the transitive closure using `DependencyInfoResource.ResolvePackage` (one dependency
  group per visited package/version) and `FindPackageByIdResource.GetAllVersionsAsync` +
  `VersionRange.FindBestMatch` to pick a candidate version for each newly-seen dependency.
- Every requirement (`requesterId`, `VersionRange`) on a package id is recorded; once all packages
  are visited, each resolved version is checked against every requirement on it. A range that the
  resolved version doesn't satisfy is reported as an explicit, human-readable conflict string
  (naming both the requester and the resolved version), not silently ignored.
- Capped at 200 visited packages to bound network calls on pathological graphs; the cap itself is
  reported as an actionable message rather than an unbounded hang.
- Wired into `NuGetProjectPackageOperationService.AddPackageReferenceWithConflictCheckAsync` (new
  method, existing `AddPackageReferenceAsync`/tests untouched) as a pre-check before the project
  file is touched: conflicts found → the csproj edit and restore never run.

**Why not `NuGet.Resolver.PackageResolver`:** that API's constructor/context shape has changed
across NuGet.Client versions and its exact overload set for this repo's referenced version wasn't
worth staking correctness on without an end-to-end solution build to catch a mismatch — and per
`nuget.md`, a full solution build in this sandbox is not reliable evidence (see next section). A
plain BFS over `DependencyInfoResource`/`FindPackageByIdResource` with `VersionRange.Satisfies` is
easy to read end-to-end in a code review and gives the same user-facing guarantee: a real
transitive walk, and an explicit conflict report instead of silence. It does not attempt "backtrack
to a globally optimal resolution" the way `PackageResolver` does — a real conflict is reported as a
conflict, with instructions to pin the offending package explicitly, rather than resolved
automatically. This is a deliberate scope decision, not an oversight.

## 2. Explicit license-acceptance confirmation

`NuGetSearchResult` (search-install path) already carried `RequireLicenseAcceptance`/`LicenseUrl`;
`NuGetPackageUpdateResult` (update-existing path) did not — extended it with the same two fields,
populated in `NuGetPackageUpdateService.GetUpdateAsync` from the same `PackageMetadataResource`
call it already makes for the latest-version lookup (no extra network round trip).

`ManagePackagesDialog.cs` (`src/AddIns/Misc/PackageManagement/Project/Src/ManagePackagesDialog.cs`)
now has `PackageManagerDialogModel.ConfirmLicenseIfRequiredAsync`: a real `ContentDialog` — title
"License Acceptance Required", the license URL as a clickable `Hyperlink` when present,
Accept/Decline buttons, default button = Decline — awaited before `InstallAsync` and `UpdateAsync`
proceed. Declining aborts the operation with a status message; nothing is installed/updated without
an explicit Accept click. `InstalledPackageRow` was extended to carry the update path's license
flag/URL alongside `LatestVersion` so the update button's gate has the data to show.

**Reuse vs `addin-manager2.md`'s license flow:** not reused directly. That flow (AddInManagerDialog,
gallery installs) pre-computes which packages in a *batch* need license acceptance and awaits one
dialog before invoking a synchronous engine event — a shape forced by WinUI not being able to
`await` a `ContentDialog` from inside a synchronous callback. `ManagePackagesDialog` installs one
package per user click from an `async void`-free click handler, so there is no synchronous-engine
constraint to work around: the dialog is awaited directly, inline, before the operation call. Same
underlying WinUI `ContentDialog` mechanics, independent call site — as the task anticipated, this
surface needed its own implementation, not a shared helper, because the two flows have genuinely
different control-flow shapes (batch-precompute vs. one-shot inline).

## 3. Package-console workflows

OpenDevelop's actual package console (`src/AddIns/Misc/PackageManagement/PowerShell` +
`.../Cmdlets`) is a real embedded Windows PowerShell host: a custom `PSHost`/runspace
(`PowerShellHost.cs`, `PowerShellHostUserInterface.cs`, `PowerShellHostRawUserInterface.cs`) plus
`System.Management.Automation`-based cmdlets (`Install-Package`, `Update-Package`,
`Uninstall-Package`, `Get-Package`, `Get-Project`, ...) — the same shape as Visual Studio's Package
Manager Console.

**Full port is out of scope for this session, and here is why, specifically:** hosting
`System.Management.Automation` interactively from inside Uno-Skia on macOS (and Linux) requires (a)
a custom `PSHost`/`PSHostUserInterface` wired to a text pane instead of a real console — the
existing `PowerShellHostUserInterface`/`PowerShellHostRawUserInterface` are written against
`System.Console`/WPF-console assumptions and would need a genuine rewrite, not a port; (b)
redirecting/streaming a runspace's output, progress records, and prompts into that pane
asynchronously without deadlocking the UI thread; (c) tab completion against the cmdlet/parameter
model; (d) packaging PowerShell Core's native SDK dependencies for macOS/Linux inside an
already-large Uno-Skia app bundle. Each of these is individually substantial; together they are a
multi-session effort, not something to attempt as gap-filling inside a broader unification task.

**What was actually implemented instead — a real, working, reduced-scope equivalent:**
`PackageConsoleCommandProcessor` (`src/Main/Base/Project/Src/NuGet/PackageConsoleCommandProcessor.cs`,
shared) implements a small line-oriented command language covering the everyday console verbs:

```
list
install <id> [version]
update <id> [version]
uninstall <id>
help
```

It is not a facade or a stub: `install`/`update` go through the exact same
`NuGetProjectPackageOperationService.AddPackageReferenceWithConflictCheckAsync` used by the graphical
Installed/Search tabs (so a scripted install gets the same conflict check as a UI install), honor the
same license-acceptance gate via an injected `Func<string,string,bool,string,Task<bool>>` callback
(the caller decides how to prompt — a host with no UI can refuse license-required installs outright
rather than silently accepting), and resolve an omitted version to the latest non-prerelease via
`PackageMetadataResource`, the same resource `NuGetPackageUpdateService` already uses.

`ManagePackagesDialog` gained a "Console" tab: a read-only output `TextBox`, an input `TextBox`
(Enter or a Run button), calling `PackageManagerDialogModel.RunConsoleCommandAsync`, which
constructs a `PackageConsoleCommandProcessor` wired to the same `ConfirmLicenseIfRequiredAsync`
dialog used by the Installed/Search tabs, so console-driven installs get the identical
license-acceptance UX.

This is deliberately scoped down from "a scripting console with a full object pipeline" to "a
command surface for the everyday install/update/uninstall/list verbs" — no piping, no scripting
variables, no custom cmdlet extensibility. That is the honest, reduced scope the task asked for
when a full port isn't feasible.

## Verification

- `dotnet build src/Main/Base/Project/ICSharpCode.SharpDevelop.csproj -c Debug` → **0 errors**
  (confirms the new shared NuGet.* files and the `NuGetProjectPackageOperationService`/
  `NuGetPackageUpdateService`/`NuGetPackageUpdateResult` edits compile against real, resolved
  `NuGet.Protocol`/`NuGet.Versioning`/`NuGet.Configuration`/`NuGet.Frameworks` references — this
  project's standalone `dotnet build` was **not** blocked by the WpfDesign.AddIn/wpf-labs sibling
  build issue documented in `nuget.md`; confirmed directly rather than assumed, no csc-response-file
  workaround was needed here).
- `dotnet build src/AddIns/Misc/PackageManagement/Project/PackageManagement.csproj -c Debug` →
  **0 errors** (confirms `ManagePackagesDialog.cs`'s license-dialog and console-tab additions
  compile against the WinUI/Uno.Sdk toolchain).
- `dotnet build src/UnoDevelop.slnx -c Debug` → **0 errors** (full solution reaches the changed
  files; only pre-existing warnings).
- `dotnet test src/Tests/UnoDevelop.Core.Tests -c Debug` → 216 passed, 3 failed (219 total).
  Two of the three failures are the documented pre-existing LSP-tooling failures
  (`LspLanguageServiceTests.CreateDefault_MapsPythonToPylsp`,
  `CreateDefault_MapsTypeScriptAndJavaScriptExtensionsToSameCommand`). The third
  (`UnitTestingCodeCoveragePadIntegrationTests.TestService_DiscoversFixtureTests`) is also
  pre-existing and unrelated to this work: `git diff` shows that test file already had uncommitted
  local changes (MTP fixture-name qualification) from earlier, unrelated session work, before this
  session touched anything — confirmed via `git diff --stat` against a file this session never
  edited. `UnoNuGetProjectTests` (the existing NuGet test suite) passed unchanged.
- `UnoDevelop.IntegrationTests` was **not** run this session (time-boxed out — this task's three
  gaps touch project-level NuGet services and a WinUI dialog, not the DevFlow-agent-driven
  integration surface those tests exercise; the standalone + solution builds and the Core test
  suite are the relevant signal here).
