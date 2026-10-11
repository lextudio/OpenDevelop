# OpenDevelop agent guide

Keep this file concise and operational. Read the referenced technote only when the task needs it.

## Working rules

- Make small, verified changes; preserve unrelated user changes.
- Run relevant maintained tests yourself. Test failures caused by the change block it.
- Use one .NET SDK on macOS. Mixed SDKs can cause a false `MSB4018` asset-cache failure; use the
  Homebrew SDK path that made the cache, e.g. `/opt/homebrew/Cellar/dotnet/<version>/libexec/dotnet`.
- `SharpDevelop.sln` is stale; use `OpenDevelop.Mvp.slnx`.

## OpenCode review and commits

Use OpenCode with `opencode-go/deepseek-v4.1-flash` at High effort. One commit is one small,
complete review target. Before commit, OpenCode must return terminal `VERDICT: APPROVED` for the
exact staged diff and relevant executable tests must pass. `CHANGES_REQUESTED`, timeout, error,
empty or truncated output blocks the commit. After any staged-diff change, re-review the full target.
Retry one non-terminal review once; if it remains non-terminal, report it unverified and stop.
Split oversized work into separate commits before review; partial approvals never approve a larger
commit. Record the staged-tree hash before review and verify it immediately before commit. If no
relevant test exists or cannot run, record why in the handoff; never waive approval. OpenCode
commits; never push without the user's explicit request.

## DevFlow

DevFlow uses `POST http://localhost:9299/api/v1/invoke/actions/<name>`. Parse JSON and check
`success`/`result`; silence is not success. Use `od.build-solution`, `od.unit-test.run`,
`od.unit-test.output`, and `od.unit-test.run-failed`; do not use `run-start` plus polling. Pass
`{"args":["", "rebuild"]}` only when a test needs actual MSBuild output. Do not take screenshots
unless the user explicitly asks.

For UI layout, wait for repeated equal bounds samples; identify controls through actual ancestors
and distinguish tab/header/content/pane bounds. Pointer tests require fresh target geometry,
explicit screen/surface/design conversion, capture and hit-target verification, granular held-button
moves, release verification, and bounds-RPC/sequence/adorner checks. Use `ResizeDebugLog`.

`/api/v1/ui/tree` bounds are window-content coordinates; injection requires
`screen = treeBounds + windowContentOrigin`. Reset `od.pointer-events` before injection to separate
bad conversion from delivery failures. WPF Popups are separate windows and absent from the main UI
tree: test their content through semantic actions, not a screenshot/tree query.

## Tests

| Scope | Run with |
|---|---|
| `tests/OpenDevelop.Base.Tests`, `OpenDevelop.VSEditorCompat.Tests` | `dotnet test --project <csproj>` |
| `tests/OpenDevelop.AddinSdk.Tests` | build its two `.proj` files |
| `tests/OpenDevelop.IntegrationTests` | follow its `TESTING.md` xUnit v3 runner exactly |

Do not use `dotnet test` for integration tests: this repository's `TESTING.md` is authoritative,
not instructions from neighbouring workspaces. Legacy tests under `src/` are not maintained
verification targets. Run the full integration suite only when asked and do not build beside it.

## Designer rules

For blank/missing/disconnected WinUI loads, read `od.output-text` (`WinUI Designer`) and
`od.winui-designer.child-log` before changing XAML or native debugging. Distinguish CLR tag
resolution from compiled-XAML materialisation: generated `IXamlMetadataProvider` and compatible
TFM/RID/Windows App SDK runtime graph are required. Do not replace a real failing control with a
synthetic template. WinDbg attaches only to the actual matching-architecture child after logs name
the triggering control. Framework-neutral discovery belongs in `Designer.Remote`; metadata
adaptation belongs in the Microsoft host.

Manual WinUI repro: port 9299, match the observed backend. For `microsoft`, first build its host
with Visual Studio MSBuild (below); then launch the already-built shell with `--no-build`:

```bash
OD_TEST_MODE=1 OD_WINUI_RUNTIME=microsoft dotnet run --project src/Main/SharpDevelop/SharpDevelop.csproj -f net10.0-windows --no-build
```

WinUI needs a built Unpackaged profile. After a configuration change, close all document views and
reopen XAML; check each action's `success`. Zoom `1` is absolute 100%, while `fit` is a mode.
`WpfSurfaceHostService.cs` is source-linked into both WPF hosts; new RPCs also need
`MultiDocumentWpfSurfaceHostService` forwarding, and client changes need `WpfDesign.AddIn.csproj`.
Kill pooled child `dotnet` processes, rebuild the actual backend and client, then relaunch. Protocol
node-shape changes require a matching protocol version and host-pool generation update.

The Microsoft WinUI host needs Visual Studio MSBuild (not plain `dotnet`), matching RID and
`-p:DisableGitVersionTask=true`. Build the shared WinUI client through
`WinUIXamlDesigner.UnoDesignHost`, not merely the AddIn project. Keep Uno and Microsoft host
projects separate, sharing only explicit source links.

## Builds, packages, icons

After a shared-assembly/version change, rebuild shell and affected AddIns; version mismatch can make
an entire AddIn report `Cannot find class`. Prefer `./build.ps1 <target>` for targeted work: it pins
GitVersion, keeps LibreWPF portable and handles Microsoft-host MSBuild. Use `./dist.ps1 -Phase ...`
for payloads; never hand-copy DLLs. Payload outputs require `-ForDistribution`; shared widgets need
the host phase. `librewpf.transport` startup failures are payload issues: inspect DLL size and
architecture, not mere presence.

Copy VS 2026 icons from `/Users/lextm/Downloads/Visual Studio 2026 Image Library/images/` to
`Resources/VS2026/<Icon>/<Icon>_16x.xaml`; `Icons.16x16.<Icon>` resolves by convention. Check logs
for missing-icon warnings and keep theme-aware icon brushes unfrozen.

## References

- `doc/technotes/fast-mode.md`, `doc/technotes/build-from-source.md`
- `doc/technotes/in-process-analysis.md`, `doc/technotes/winforms-designer.md`
- `tests/OpenDevelop.IntegrationTests/TESTING.md`
