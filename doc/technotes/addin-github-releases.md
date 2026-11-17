# AddIn distribution through GitHub Releases (design)

**Status (2026-11-16): `PackOdAddin` (the SDK packaging target) exists; everything else here, including
the `.odaddin` support in `AddInSetup`, is design only.** Replaces the NuGet-based online gallery of
AddIn Manager 2 as the way users discover, install and update add-ins.

## Why replace the NuGet gallery

- The only default source is the hard-coded `https://www.myget.org/F/sharpdevelop/`
  (`PackageRepositories.DefaultRepositorySource`), the old SharpDevelop team's feed. It holds none
  of today's add-ins.
- The legacy `NuGet.Core` HTTP engine cannot run on Unix (its settings lock uses a Windows named
  mutex), so on macOS and Linux every remote source is replaced by
  `UnavailableRemotePackageRepository` and reports no packages. Online install and update work only
  on Windows.
- There is no trust model: one global source, no notion of "the publishers I trust".

## Model

1. Each add-in author owns a GitHub repository.
2. Each version is a GitHub **Release** whose tag is `vMAJOR.MINOR.PATCH[-suffix]` and whose assets
   include exactly one `*.odaddin` file.
3. A user keeps a list of **trusted repositories** (`owner/repo`) in Tools > Options.
4. The AddIn Manager lists the add-ins published by those repositories, offers *Install* for new
   ones and *Update* when a newer release exists for an installed add-in.

No server, index or registry is operated by us. NuGet and dependency resolution are not involved.

## The `.odaddin` asset

`.odaddin` is a zip. It **replaces** `.sdaddin`, which is dropped, not kept alongside: packages built
for SharpDevelop reference assemblies (`ICSharpCode.*` on .NET Framework, WPF, old AvalonEdit) that
the current host no longer provides, so an old `.sdaddin` cannot load and accepting it would only
move the failure from install time to load time. The new extension also marks a package that
`PackOdAddin` produced and that always carries a host range and a tag-matching version.

Code to change: in `AddInSetup.InstallAddIn` ([AddInSetup.cs](../../src/Main/Base/Project/Src/AddInManager/AddInSetup.cs))
replace `case ".sdaddin": case ".zip":` with `case ".odaddin":` alone (`LoadAddInFromZip` stays; a bare `.zip` has no host range or tag check, so it is dropped too);
change the install dialog's filter `AddInManager2.SDAddInFileFilter` in every
`StringResources.*.resx` to `*.odaddin`; update `AddInManager2.Tests/AddInSetupTests.cs`, whose
offline-package test installs a `*.sdaddin`. Opening an `.sdaddin` then reports an unsupported
package type instead of trying to load it. Layout:

```
<Name>.odaddin (zip)
  <Name>.addin        # manifest, exactly one, at the root
  *.dll, resources    # everything the manifest's <Runtime> needs
```

- A bare `.addin` is not a valid release asset: it is only the manifest, the code is the DLLs.
- A release with zero or more than one `*.odaddin` asset is skipped and logged, never guessed at.
- Dependencies on other add-ins are declared with the manifest's `<Dependency>` element, which
  `AddInManager` already evaluates. A dependency that is not installed blocks the install with a
  message; it is not fetched automatically in the first version. `PackOdAddin` packages only
  `InProcess` add-ins; a supporting library (`InProcessDependency`) is never published on its own,
  it ships inside the package of the add-in that needs it.

## Identity and versioning

- The manifest's `<Identity name>` is the add-in's unique key. The repository is only where it comes
  from.
- The release tag is the version source of truth. Before installing, the package's
  `<Identity version>` must equal the tag (without the leading `v`); a mismatch rejects the install.
- *Update available* means: the installed add-in with that identity has a version lower than the
  newest eligible release of a trusted repository. Reuse `IAddInSetup.CompareAddInToPackageVersion`
  semantics.
- Releases flagged *prerelease* (or drafts) are ignored unless `AddInManager2.ShowPrereleases` is on.
  Drafts are never shown.

## Host compatibility

The in-process designer protocol changes between OpenDevelop versions and breaks out-of-tree
add-ins at runtime (`../AGENTS.md`, "Keeping an external add-in compatible"). The manifest therefore
declares the host range it was built for, using the existing `<Manifest>`/`<Dependency
addin="SharpDevelop" version="...">` mechanism. The manager shows an add-in whose range excludes
the running host as *incompatible* and disables Install/Update for it, instead of letting the IDE
fail on the next start.

## Trust and security

An add-in is arbitrary code in the IDE process with the user's full rights. Trusting a repository
means trusting its owner's whole release pipeline. First-version rules:

- Adding a repository shows a warning saying exactly that and requires confirmation.
- Only `owner/repo` is accepted (no arbitrary URLs); all traffic is HTTPS to `api.github.com` and
  GitHub's asset download hosts.
- Before extraction, the downloaded file's SHA-256 must match the `digest` field GitHub reports for
  the asset. A missing digest blocks the install (no silent downgrade to "unverified").
- Extraction is confined to the add-in's own folder (no `..`, no absolute entries, no symlinks).
- Not in the first version: package signing, publisher verification, a revocation list. State this
  limit in the Options panel text and in the user docs.

## Packaging: `PackOdAddin` in the Addin SDK

The `.odaddin` is produced by a new target in `src/SDK/OpenDevelop.Addin.Sdk/Sdk/Sdk.targets`, not by
a script per add-in repository. The SDK already deploys the `.addin` manifest and trims
host-provided assemblies from `$(OutputPath)` (`OpenDevelopTrimAddinOutput`), so the trimmed output
folder is exactly the content of the package.

`dotnet build -t:PackOdAddin` (name provisional):

- requires `-p:OpenDevelopDistributionBuild=true` (errors otherwise) so `ref/`, XML documentation and
  foreign-platform runtimes are pruned; pick the runtimes to keep with `OpenDevelopDistributionRidFamily`
  or `OpenDevelopDistributionRuntimeIdentifier`;
- zips `$(OutputPath)` (from the add-in's own output, never from `$(OpenDevelopRoot)AddIns/...`,
  which CoreWF and Stride dirty) into `<Name>.odaddin`, the asset name without a version (one asset
  per release; the version lives in the tag);
- fails the build unless: the add-in kind is `InProcess`, exactly one `.addin` is at the root,
  `<Identity version>` is a literal equal to the version being released, and the build has no
  RuntimeIdentifier (implemented);
- **planned, not implemented yet:** check that the host range is present, and write the host version
  the add-in was trimmed against into the manifest (the SDK knows it from the host that supplied
  `OpenDevelop.host-assemblies.txt`), so authors do not maintain the range by hand;
- **planned, not implemented yet:** support add-ins that carry an out-of-process child host folder
  (MAUI's native host bundles are ~100 MB each; check against GitHub's 2 GiB asset limit and warn
  above a threshold). Today `PackOdAddin` refuses every kind except `InProcess`.

### The SDK needs no separate distribution

A machine that has OpenDevelop installed already has the SDK. **Verified 2026-11-16** on macOS
against `/Applications/OpenDevelop.app`: a project with
`<Project Sdk="Microsoft.NET.Sdk;OpenDevelop.Addin.Sdk">` and no version builds and deploys its
`.addin`, once MSBuild is told where the installed resolver is:

```bash
export MSBUILDADDITIONALSDKRESOLVERSFOLDER=/Applications/OpenDevelop.app/Contents/MacOS/SdkResolvers
dotnet build --disable-build-servers
```

Both `MSBUILDADDITIONALSDKRESOLVERSFOLDER` and the `_NET` variant used by `build/common.psm1` work.
Without the variable the build fails with `MSB4236: The SDK 'OpenDevelop.Addin.Sdk' specified could
not be found`. **Pitfall:** a running MSBuild build server keeps its first answer, so a build
started before the variable was set kept failing after it was set; use `--disable-build-servers`
(or `dotnet build-server shutdown`) in CI and in any diagnosis.

A release workflow therefore does: install the matching OpenDevelop (download the release's
`OpenDevelop-linux-x64.tar.xz` on a Linux runner, the cheapest), export the variable, `dotnet build`,
`PackOdAddin`, `gh release create vX.Y.Z <Name>.odaddin`. The `.odaddin` is thus tied to the
OpenDevelop version it was built with; a new host version means a new add-in release.

Gaps found while verifying:

- The **Windows payload does not appear to ship** `Sdks/` and `SdkResolvers/OpenDevelop.Addin.SdkResolver`
  (macOS `build-application-bundle.sh` and Linux `build-application-payload.sh` do; `dist.ps1` only
  handles the NuGet resolver manifest). Not yet checked against a real Windows payload.
- CoreWF, Stride and MAUI Designer import `Sdk.props`/`Sdk.targets` by path from `$(OpenDevelopRoot)`
  (a source checkout), not through `Sdk=`. To publish from an installed OpenDevelop they must switch
  to the `Sdk=` form (or point `OpenDevelopRoot` at the install's `Sdks/` layout).

## Rate limits and caching

Unauthenticated GitHub API access is 60 requests/hour/IP, which a handful of repositories plus
background update checks can exhaust.

- Conditional requests: store the `ETag` per repository; a `304` does not count against the limit.
- Cache the parsed release list on disk; background check at most once per day, manual *Refresh*
  always allowed.
- Optional GitHub token in Options (stored with the other credentials mechanism, never in plain
  settings) raises the limit to 5000/hour and allows private repositories.
- Rate-limit exhaustion is a visible, non-fatal state ("limit reached, retry after HH:mm"), never a
  crash or an empty list shown as success.

## Settings

Reuse the PropertyService pattern of `AddInManagerSettings`:

| Key | Value |
|---|---|
| `AddInManager2.TrustedRepositories` | `string[]` of `owner/repo` |
| `AddInManager2.GitHubToken` | optional, secret storage |
| `AddInManager2.ShowPrereleases`, `AutoSearchForUpdates`, `ShowPreinstalledAddIns` | unchanged |

The old `AddInManager2.PackageRepositories` key is ignored once the NuGet path is removed; no
migration is attempted (the only entry users could have is the dead MyGet default).

## Code plan

**NuGet is replaced, not abstracted.** An earlier plan put a small `IAddInSource` in front of the
NuGet code and kept NuGet behind it. That was dropped after reading the code: NuGet types run through
the whole manager, not only the source layer, so an adapter would be written and then deleted.

- `IAddInSetup` signatures carry `IPackage` (`InstallAddIn(IPackage, ...)`,
  `CompareAddInToPackageVersion`, `GetNuGetPackageForAddIn`, `GetAddInForNuGetPackage`).
- `AddInSetup` (929 lines) tracks installed state through NuGet's local repository
  (`RemoveUnreferencedNuGetPackages`) and patches manifests from package metadata
  (`PatchAddInManifest`, `PatchAddInProperties`).
- `INuGetPackageManager` exposes `IPackageManager`, `PackageOperation`, `IPackageOperationResolver`
  (dependency resolution, which is out of scope here).
- Eleven AddInManager2 ViewModels use NuGet types; `NuGetPackageViewModel` and
  `NuGetAddInsViewModelBase` are built around `IPackage` and package operations.

New model, no NuGet types anywhere (in `src/Main/Base/Project/Src/AddInManager/`, shared with
UnoDevelop as today):

- `AddInRelease`: identity, version, tag, asset name and URL, digest, notes, prerelease flag.
- `IAddInSource` and `GitHubReleaseSource`: list releases of a trusted repository (ETag, cache,
  rate-limit state), download an asset.
- `AddInSetup` keeps what is source-independent (install from `.odaddin`, uninstall, enable, suspend,
  manifest loading, pending operations) and loses everything NuGet. Installed state is "the installed
  add-in's Identity and version"; *update available* compares it with the newest eligible release.

Steps, each its own reviewed commit:

0. **Done.** `PackOdAddin` in the Addin SDK, `.odaddin` replaces `.sdaddin` and `.zip`, filters,
   renamed test packages.
1. **Model.** `AddInRelease`, `IAddInSource`, `GitHubReleaseSource` (with a fake `HttpMessageHandler`
   test set) and the tag/manifest/digest/host-range checks. Added beside the old code, unused.
2. **Setup.** Strip NuGet from `AddInSetup` and its interface; install from a downloaded `.odaddin`.
3. **UI.** New Available and Updates view models and the Options panel "Trusted add-in repositories";
   `UpdateNotifier` on the new source; same for UnoDevelop's `AddInManagerDialog`.
4. **Removal.** Delete `NuGetPackageManager`, `PackageRepositories`,
   `UnavailableRemotePackageRepository`, `NuGetPackageViewModel` and friends, the `NuGet.Core` and
   `SharpZipLib` references and the MyGet default.

Steps 2-4 leave the online tab without function until step 3 lands, so they go on a branch and merge
when the new tab works. Verification: Base tests where logic is UI-free; for step 3 one DevFlow
journey against a local fake GitHub endpoint (add repository, list, install a fixture `.odaddin`,
see it update), never the real API.

## Open questions

- Whether the three LeXtudio add-ins (Workflow Designer, Stride, MAUI Designer) become the first
  published releases, and who owns the release workflow that builds the `.odaddin`.
- Whether a host-version range is already expressible in the current `.addin` schema or needs a
  small extension to the manifest reader.
- Secret storage for the token on each platform.
