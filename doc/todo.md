# TODO — v5.7.1 size work

Written 2026-10-31. Baseline: master `10a581d`, tag `v5.7.1-beta.17` (release run `41184771107`).
Background detail and measurements: `doc/technotes/v5.7.1-size-work-state.md`.

## Where the release chain stands

Verified on real artifacts (v5.7.1-beta.15 and beta.16):

| asset | v5.7.0 | now |
|---|---|---|
| `OpenDevelop-win.7z` | 828 MB (zip) | 229.3 MB |
| `OpenDevelop-linux-x64.tar.xz` | 498 MB (zip) | 105.7 MB |
| `OpenDevelop-linux-arm64.tar.xz` | — | 102 MB |
| `OpenDevelop-macos.dmg` | 246 MB | 234.5 MB |

Windows extracted size is 1830 MB with sharing off and ~1647 MB with it on (measured; the sharing
run passed the packaged designer check in beta.16).

## 1. macOS bundle verification dies during `od.build-solution` (blocks a four-platform draft)

Status: NuGet crash fixed and confirmed; a second, unexplained intermittent macOS exit remains open.

Symptom: `curl: (52) Empty reply from server`, then `agent status: <no answer>`, `app process: not
running`. Intermittent — beta.3, beta.6 and beta.15 passed; beta.16 and beta.17 failed at the first
`od.build-solution` (the app disappeared ~2 s after its build child started).

The captured stack (beta.16/beta.17) named the real path — it is the AddIn Manager's automatic
update lookup, not the build:

```
System.IO.IOException: The filename, directory name, or volume label syntax is incorrect. :
  'Global\7DTH6CZ/XNHQTXHVPP8R/A7LDPQ0IOYHRQT9MOVIPA0='
  at System.Threading.Mutex.CreateMutexCore
  at NuGet.Settings.ExecuteSynchronized -> NuGet.Settings..ctor
  at NuGet.ProxyCache..cctor -> get_Instance
  at NuGet.HttpClient.GetResponse -> NuGet.RedirectedHttpClient.get_Uri
  at NuGet.DataServicePackageRepository.get_Source
  at ICSharpCode.AddInManager2.ViewModel.UpdatedAddInsViewModel.GetUpdatedPackages()
```

The default add-in source is an HTTP feed, so legacy NuGet.Core builds a `DataServicePackageRepository`;
reading its `.Source` (the update view model does that for identity) initialises `NuGet.ProxyCache`,
whose settings lock is `new Mutex("Global\\<random base64 token>")`. `System.Threading.Mutex` on Unix
rejects a name containing a path separator, and that random token contains `/` in roughly half of all
processes — which is exactly why the failure is intermittent. Beta.15's macOS run had no
`NuGet.ProxyCache` frame at all; beta.16 and beta.17 did.

Fixed (not merely caught):
- `PackageRepositories` (Base) now maps an HTTP(S) add-in source to
  `UnavailableRemotePackageRepository` on non-Windows platforms, so no legacy HTTP client is ever
  constructed (it also has no useful loss: the default myget feed is dead). Local `file:` sources
  keep using the real engine.
- `AddInManagerVisualInitializationCommand` wraps the startup lookup, so an optional background
  feature can no longer take the session down.
- `verify-application-bundle.sh` diagnostics now report the process state, remaining OpenDevelop
  processes and the newest crash *or* `JetsamEvent` report, because a process that vanishes without
  a managed unhandled-exception line was killed natively or by the kernel.

Verified on macOS 2026-10-31: rebuilt Release bundle (`dist.ps1 -From host`), then
`build/macos/verify-application-bundle.sh OpenDevelop.app` green; the app log prints the new
"source is remote ... reported as having no packages" line and contains no `NuGet.ProxyCache` frame
(the pre-fix local run reproduced the exception; a local run that hit it did not always die, which is
why removing the code path matters more than the catch). `OpenDevelop.Base.Tests`: 509 total, 0 failed.

beta.19 (run `41216636654`, commit `78a8f0f7`) is green on all four platforms and made draft
`v5.7.1-beta.19` (win 232.5 MB, macos 245.9 MB, linux-x64 110.8 MB, linux-arm64 107.0 MB). The macOS
bundle verification passed in 79 s, so the silent exit did not recur there and is still unexplained;
the diagnostics below are what will name it if it does.

beta.18 confirms both fixes on the two platforms that had them: Windows passes the packaged designer
journey under the shared closure (the identity-conflict fix), and linux-x64 no longer dies on disk.
macOS still fails, but differently and no longer at NuGet: the app now survives startup, opens the
solution and starts the build, then disappears ~1.6 s later, between
`perf: solution-open/roslyn-snapshots-built` and `roslyn-projects-pushed` — i.e. while the first
Roslyn project push starts as the build child is still restoring. There is no managed
unhandled-exception line, no "Leaving RunApplication()", and no crash report at the old check. Locally
the same input (fixture `obj/` deleted, SDK 10.0.403, seven runs) never reproduces it; the app is
faster, so the window is smaller.

What the diagnostics now collect for the next occurrence (they run on any `fail`, including a lease
request that cannot reach the agent, which previously exited with a bare curl status):
- process/zombie state, leftover `dotnet` children, the app log tail;
- every `.ips` or `JetsamEvent` report written since the launch (ReportCrash is asynchronous, so the
  check waits 3 s and looks in both DiagnosticReports folders);
- whether a CLR triage dump exists (`DOTNET_DbgEnableMiniDump`, type 4) — its presence means the
  runtime aborted, its absence means something killed the process or it shut down;
- `log show` for OpenDevelop over the last 5 minutes, which carries the kill decision and any native
  death.

## 2. Bitness journeys do not test bitness yet

`od.debug.start` starts `<name>.dll`, so the debuggee was always the runner's own `dotnet`; the RID
under test never took effect (observed: `> Debugging: BitnessFixture.dll`).

Done: `tests/fixtures/BitnessFixture/BitnessFixture.csproj` now sets `<UseAppHost>true</UseAppHost>`.

To do:
- Change `build/windows/verify-packaged-app.ps1` so each journey asserts the process the IDE
  started is the per-RID `.exe` (PE machine type `0x14c` / `0x8664` / `0xAA64`), not the `.dll`.
- Keep staging the RID output where the IDE looks (`bin/Debug/net10.0/`), which is already there.
- Then remove `-SkipDebugJourneys` from `.github/workflows/release.yml` and let the journeys gate
  releases again.
- Expected matrix: x64 runner → `win-x64`, `win-x86`; ARM64 runner → `win-arm64`, `win-x64`,
  `win-x86`. Runtimes are installed by the "Install the runtimes the bitness journeys debug" step.

## 3. Shared managed closure — keep an eye on it

`Share-ManagedPayloadAssets` in `dist.ps1` is enabled in the release workflow. It builds
`shared/managed/<library>/<version>/<asset>` from every identity that maps to a removed file (that
fix is what made beta.16 green on Windows) and `DesignerHostLaunchSpec` passes
`--additionalprobingpath` to every designer child.

beta.17 (Windows) is red again on exactly this: the packaged `WpfDesign.SurfaceHost` child died with
`Could not load file or assembly 'ICSharpCode.Designer.Remote, Version=5.7.1.0'`, while beta.16's
Windows job passed and both builds report the same store (`331 probe paths in 89.2 MB, 272.0 MB of
host copies removed`). Cause: the store path is the library identity, and one identity can resolve to
**two** contents across hosts (`ICSharpCode.Designer.Remote/1.0.0` vs
`ICSharpCode.Designer.Remote.Reference/5.7.0.0`; the local bundle's `WpfDesign.SurfaceHost.deps.json`
declares both for the same file). Whichever host the `Get-ChildItem -Recurse` enumeration saw first
put its bytes in the store, and the other host's copy was deleted because its bytes were duplicated
elsewhere. MSBuild's parallel AddIn builds do not stabilise that directory order, so the same tree
passes or fails per build.

Fixed: an identity that resolves to more than one content hash is never shared - every host keeps its
own copy - and the payload build says how many it kept. `build/windows/test-share-managed-payload.ps1`
pins both rules against a synthetic payload (it slices the real function out of `dist.ps1`), and the
release workflow's Windows job runs it before packaging: on the pre-fix function it reports
`conflicting identity lost per-host copies: A,B,C,D`.

Rules learned the hard way, do not undo:
- A `.lib`/`.exp` deleted from the payload must also be stripped from every `*.deps.json`
  (`Remove-LinkTimeDepsAssets`), or hostpolicy refuses to start every child host.
- Never share a file name that resolves to more than one content hash in one directory.
- Build the store from every identity that maps to the file, not from the identities inside one
  content-hash group.
- Only `Host` / `MicrosoftHost` directories with their own `runtimeconfig.json` are eligible —
  an in-process add-in's deps describes a directory loaded into the IDE itself.

## 4. Housekeeping

- The CI workflow itself is `disabled_manually` on GitHub, so no `ci.yml` run exists for any commit
  after `baa694e9` and the "green CI required" gate can never be satisfied. Re-enable it (`gh workflow
  enable ci.yml`) and get one green run before the real `v5.7.1`.
- Delete the repository variable `RELEASE_SKIP_CI_GATE` after the rehearsal tags are done (it is
  currently `true`, which bypasses the "green CI required" gate).
- Only one draft release should exist; delete older beta drafts and their tags.
- Release notes: the file must be named `doc/releases/<tag>.md`; the beta.17 tag has no matching
  file (it points at the apphost commit), so that draft falls back to GitHub-generated bullets.
- Before a real `v5.7.1`: `doc/releases/v5.7.1.md` with the bullets, a green `ci.yml` run for the
  exact commit (no gate variable), then tag, then publish the draft with
  `gh release edit v5.7.1 --draft=false`.

## 5. Linux x64 runner ran out of disk (beta.17)
`Package Linux (linux-x64)` (run `41184771107`) died inside `Build and smoke-test the native Linux
payload` with the runner's own
`Unhandled exception. System.IO.IOException: No space left on device :
'/home/runner/actions-runner/cached/.../_diag/Worker_...log'`.
`linux-arm64` in the same run was green, so it is the x64 build tree plus payload, not the product.
The job now drops the image caches this build never uses (`/usr/local/lib/android`, `/opt/ghc`,
`/opt/hostedtoolcache/CodeQL`) and prints `df -h /` before and after, so the next occurrence (or its
absence) is visible in the log.

## 6. Linux had no packaged-artifact verification (closed)

Windows and macOS each verify the packaged artifact with DevFlow journeys
(`build/windows/verify-packaged-app.ps1`, `build/macos/verify-application-bundle.sh`). Linux had
only `dist.ps1`'s `smoke` phase - start `dotnet OpenDevelop.dll` under Xvfb, wait for
`startup/main-window-rendered`, kill - so a pruned `runtimes/` tree, a missing deps entry or a child
host that cannot start reached users, and no CI platform runs Linux at all (`test-plan.json` has
windows-x64, windows-arm64, macos-arm64 only).

`build/linux/verify-application-payload.sh` now runs the macOS journeys against the payload
directory (solution build, Roslyn child host and workspace, terminal plus both splits, ILSpy,
LibreWPF designer frame; the debugger journey behind `OD_VERIFY_REQUIRE_DEBUGGER=1`), and the
release workflow's Linux jobs run it under `xvfb-run` after the build. Verified locally against a
payload directory end to end; the first real check is the next release run. beta.19 (run `41216636654`) ran it under `xvfb-run` on
both linux-x64 and linux-arm64 and both passed: solution build, Roslyn child host, terminal splits,
ILSpy and the LibreWPF designer frame now hold on a packaged Linux payload.

## Known-good constraints (do not regress)

- Windows and Linux payloads must not ship `.lib`/`.exp`, `CliclickSharp*`, `linux-arm`, or the
  host mirror's own `win-x86` tree. Add-in `runtimes/win-x86` trees stay: the debugger and the
  designers load them for 32-bit applications.
- The payload root keeps its flat managed closure (users may run `dotnet OpenDevelop.dll` without a
  probing path); only the out-of-process hosts share.
- `tar.xz` hardlink entries are only restored by GNU tar / libarchive. 7-Zip extracts them as 0-byte
  files, which is why `LINUX.md` must keep saying `tar -xf`.
