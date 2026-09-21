# Runtime XAML Hot Reload

## Status

**WPF (LibreWPF) is the active adapter.** Uno is paused and kept as the reference case for the
framework-driven shape; WinUI remains research. The contract, registry, WPF adapter, Apply/Stop
commands and Hot Reload output channel are in place and proven end to end. What is still
missing against Visual Studio's own surfaces is the toolbar drop-down (restart, apply-on-save
toggle, settings) and output verbosity - see "UX, security, and observability".

The Uno DevServer work reached "the server computes a delta, the application never applies it"; see
"Uno Platform: paused" below for the measured findings.

### Why WPF is viable on LibreWPF (verified 2026-09-20)

The vscode-wpf agent design depends on WPF's diagnostic infrastructure, so the first question was
whether LibreWPF actually implements it or merely carries the signatures. It implements it, and the
implementation is wired into the real XAML loading path - not a stub:

| API | LibreWPF | Evidence |
| --- | --- | --- |
| `XamlSourceInfoHelper` (+ `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO`) | real | `PresentationCore/System/Windows/Diagnostics/XamlSourceInfoHelper.cs`, backed by a `ConditionalWeakTable` |
| `SetXamlSourceInfo` actually called during XAML load | yes | `PresentationFramework/System/Windows/Markup/WpfXamlLoader.cs:102`, plus `FrameworkTemplate.cs`, `TemplateContent.cs`, `XamlReader.cs` |
| `VisualDiagnostics.GetXamlSourceInfo` | real | `VisualDiagnostics.cs:96`, delegates to the helper |
| `ResourceDictionaryDiagnostics.GetResourceDictionariesForSource` | real | `ResourceDictionaryDiagnostics.cs:143` |

So the source-mapping, visual-tree and resource-dictionary hooks the agent needs are all present,
and the reference implementation in `externals/vscode-wpf` (`src/WpfHotReload.Runtime`,
`docs/HOTRELOAD.md`) can be followed rather than re-invented.

### Agent proof-of-concept against a live LibreWPF app (done)

`src/Main/HotReload/WpfHotReload.Agent` builds vscode-wpf's agent for `net10.0-windows` against
`LibreWPF.Sdk`, source-linking `WpfHotReloadAgent.cs` and `StartupHook.cs` rather than copying them
so the agent keeps its own licence and a fix lands in one place. The agent reaches the WPF
diagnostic APIs by reflection, so the same source serves LibreWPF and Microsoft WPF.

It was verified by injecting it into **OpenDevelop itself**, which is a real, large LibreWPF
application - a better first target than a toy sample precisely because scale is what breaks things:

```bash
DOTNET_STARTUP_HOOKS=<...>/WpfHotReload.Agent.dll \
WPF_HOTRELOAD_PIPE=od-hr WPF_HOTRELOAD_LOG=$TMPDIR/hr-agent.log \
ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1 \
dotnet run --project src/Main/SharpDevelop/SharpDevelop.csproj -f net10.0-windows --no-build
```

Results: the hook loads, the pipe listener starts, both diagnostic methods resolve
(`Resolved VisualDiagnostics.GetXamlSourceInfo: True`,
`Resolved ResourceDictionaryDiagnostics.GetResourceDictionariesForSource: True`), the source map
builds with **1011 entries** from the live tree, and an apply round-trip answers
`ok: window updated` in 0.3 s.

Two mechanics worth knowing before driving the pipe from a test:

- .NET named pipes are Unix domain sockets here: the endpoint is
  `$TMPDIR/CoreFxPipe_<pipe name>`, not `/tmp`.
- The agent serves **one request per connection** and recreates the listener afterwards, so the
  socket briefly does not exist between requests. A client must retry `connect` rather than treat
  `FileNotFoundError` as a dead agent.

### WPF adapter: built and proven end to end (phases 1-3)

`src/Main/Base/Project/HotReload/` now holds the contract (`HotReloadContracts.cs`), the registry
(`HotReloadService.cs`), the capability-driven command decisions (`HotReloadWorkflow.cs`), the
commands and output channel (`HotReloadCommands.cs`), and the WPF adapter under
`HotReload/Wpf/`. Adapters are contributed at `/SharpDevelop/HotReload/Adapters`.

#### Agent variants: LibreWPF vs Microsoft WPF

The agent is source-linked once (`src/Main/HotReload/WpfHotReload.Agent.Shared.props`) and built
twice, because the two runtimes need their own assembly even though the agent source is identical:

| Variant | SDK | Deployed to | Runs on |
| --- | --- | --- | --- |
| `WpfHotReload.Agent` | `LibreWPF.Sdk` | `HotReload/librewpf/` | LibreWPF (macOS/Linux, and Windows) |
| `WpfHotReload.Agent.Microsoft` | `Microsoft.NET.Sdk` | `HotReload/microsoft/` | Windows Desktop (Microsoft WPF) |

`WpfApplicationHotReloadAdapter` routes on `XamlFrameworkDetector`'s `Runtime`
(`XamlRuntimeKind.LibreWpf` / `MicrosoftWpf`) and refuses (`CanHandle` → false) when the matching
agent is not deployed, so a Microsoft WPF debuggee is never handed the portable agent.

The Microsoft variant sets `EnableWindowsTargeting` so it restores and **compiles** on macOS,
catching compile errors locally - but running it needs Windows, and the end-to-end test is the only
thing that proves that path. The current E2E run (`WpfHotReloadEndToEndTests`) exercises the
LibreWPF variant on macOS; a Windows run against a `Microsoft.NET.Sdk` fixture is still outstanding
and must live in CI, not on a macOS dev machine.


The abstraction's acceptance test is met: `grep UnoHotReloadService src/Main/Base/Project/Src/`
returns nothing, and `DefaultProjectBehavior` names no framework.

`WpfHotReloadEndToEndTests` launches the fixture for real, waits for the adapter's session to
report Ready, applies an edit, and then asks the *running* application what it displays -
`Sample pane` becomes `Hot reloaded` in the same process, with nothing rebuilt or restarted. It
runs in about 7 seconds. Verification deliberately bypasses the session: an apply that reports
success while the UI is unchanged is precisely the failure the test exists to catch.

The fixture, `tests/fixtures/WpfHotReloadFixture`, is only an SDK wrapper. It source-links
vscode-wpf's own sample, because the agent's built-in queries (`PaneTitle.Text`, `PaneBody.Text`,
`PrimaryButton.Background`, `PaneList.SelectedIndex`) name the elements in exactly those files and
a copy would drift away from the agent that reads it. The one thing it changes is the SDK: the
sample targets `Microsoft.NET.Sdk` and so needs the Windows Desktop runtime, while this test has to
run the application for real on macOS.

#### macOS trap: the pipe name has to be short

A named pipe is a Unix domain socket at `<TMPDIR>/CoreFxPipe_<name>`, and the whole path is capped
at 104 characters. macOS spends ~48 of those on `$TMPDIR` alone, so the original
`od-wpf-hotreload-<32-char GUID>` overflowed it. The failure is invisible from the IDE side: the
`ArgumentOutOfRangeException` is thrown on the *agent's* listener thread inside the target process,
so the symptom is an application that starts normally and an agent that simply never answers. The
name is now `odhr` plus 16 hex characters - still unguessable, which is the only thing protecting
an unauthenticated local endpoint.

Related: the pipe client retries every failure except cancellation. Enumerating the "expected"
exception types got this wrong once - an unlisted exception escaped the retry loop and the session
reported "never became ready" after 84 ms, which reads like a dead agent rather than one that was
still starting.

#### Third bug fixed in the reference agent: the overlay badge was positioned at NaN

The agent injects a small status badge over the host application's title bar, and it placed itself
from `Window.Left`/`Window.Top` plus Win32 frame metrics. On the portable (LibreWPF) host
`Window.Left`/`Top` are **NaN**, and NaN propagates silently: the badge was assigned `Left=NaN,
Top=NaN`, went nowhere, and the agent still logged "Overlay injected into title bar."

It now anchors on `PointToScreen`, which is accurate on both hosts, and derives the unit scale from
a known width rather than assuming one:

```text
measured on macOS at 2x: client=(50,61) actualWidth=800
PointToScreen(0,0)=(50,61)  PointToScreen(800,0)=(850,61)  =>  scale 1, while the DPI scale is 2
```

So `PointToScreen` returns device pixels under Microsoft WPF but device-independent units on the
portable host - dividing by the DPI scale would have been wrong here. Deriving the factor from
`(p1.X - p0.X) / ActualWidth` is correct on both, and the caption band sits immediately above the
client area on both, so the badge lands top-centre of the title bar with no platform branch and no
macOS native API. Verified: `(439.875, 41.18)` for an 800-wide window whose title bar spans
y ∈ [38, 61], i.e. horizontally centred and vertically centred in the caption.

This also retired the `GetSystemMetrics`/`SM_CYSIZEFRAME` and `GetDpiScale` helpers, which were
only there to correct frame metrics the client-origin anchor never needs - and which were exactly
the calls the portable host cannot answer reliably.

#### Bug found and fixed in the reference agent: `EnumerateDescendants` had no visited set

`BuildSourceMap` walks both the visual and the logical tree, and those overlap heavily, so every
element was enqueued once per path that reached it. On vscode-wpf's small samples that only wasted
work; against OpenDevelop's docked workbench the walk never finished, and because `BuildSourceMap`
runs inside a `Dispatcher.Invoke` it took the **UI thread** down with it - the application hung
with no exception and no log line, and the pipe thread sat in `WaitHandle_WaitOneCore` (confirmed
with macOS `sample`). Fixed in `externals/vscode-wpf/.../WpfHotReloadAgent.cs` by tracking visited
nodes with a `HashSet<object>(ReferenceEqualityComparer.Instance)`. This is not LibreWPF-specific;
it would bite any sufficiently large WPF application.

#### Second bug found and fixed in the reference agent: queries ran off the UI thread

The pipe loop dispatched `preview` requests to the UI thread but read `query` results straight from
the pipe thread, so every query that touches a live element failed with "The calling thread cannot
access this object because a different thread owns it." It looked like a per-query quirk rather
than a threading bug, because the constant-valued queries (`agent.ready`, the diagnostics and
source-map counters) do not touch WPF and kept working. Fixed by dispatching `QueryValue` through
`Application.Current.Dispatcher`.

## Entry point and launch semantics

Hot Reload is an explicit application-session mode, not an implicit side effect of every Run or
Debug command. The workbench provides a dedicated **Start Hot Reload** command, represented by a
distinct toolbar icon in the same discoverable place as Visual Studio's Hot Reload controls.

| Command | Launches application | Enables Hot Reload | Debugger |
| --- | --- | --- | --- |
| Run | Yes | No | No |
| Debug | Yes | No | Yes |
| Start Hot Reload | Yes | Yes | No |
| Debug with Hot Reload | Yes | Yes, when the adapter supports it | Yes |

`Start Hot Reload` creates the session, selects a compatible adapter, and then launches the
startup project with only that adapter's launch settings. It is disabled when no startup project
is selected or the active target cannot support Hot Reload. A later session-management increment
will turn the command into **Stop Hot Reload** while the process is alive; stopping will terminate
only the Hot Reload session and its helper process, not the application. A separate **Restart with
Hot Reload** command is offered when a change needs a build or restart.

The existing Run and Debug commands preserve their ordinary behaviour. They never start a
DevServer, inject a runtime agent, or set metadata-update environment variables unless the user
chose a Hot Reload command. This avoids unexpected extra processes, makes failures visible at the
correct command boundary, and lets the status UI distinguish a normal debugging session from a
Hot Reload-enabled one.

## Boundary

Runtime Hot Reload updates a user-launched application from the XAML editor. It is not the
designer's DDP protocol.

| Concern | Designer refresh | Runtime Hot Reload |
| --- | --- | --- |
| Target | Isolated design host | User application process |
| Transport | DDP / JSON-RPC | Framework-specific runtime adapter |
| Input | Full design snapshot | Saved or explicitly applied source edit |
| Goal | Design-time preview | Preserve live application state |

The two features may share source normalisation and XAML change classification, but must not share
their transport, process ownership, security boundary, or lifecycle.

## Goals

- Offer one workbench command, session model, and status experience for WPF, WinUI 3, and Uno.
- Apply XAML-only edits without build/restart when the selected runtime supports it.
- Report applied, degraded, build-required, restart-required, unsupported, and disconnected
  outcomes explicitly.
- Attach only to applications OpenDevelop launched with an enabled agent/endpoint.
- Reuse framework-maintained infrastructure where available; do not copy private IDE protocols.

## Non-goals for the MVP

- A universal .NET metadata-delta or Edit-and-Continue implementation.
- Arbitrary process attach, elevated-process support, or remote-process support.
- Treating every C# change as safely reloadable.
- Reimplementing Uno DevServer/Studio or Visual Studio's private WinUI debugger channel.

## Common workbench contract

### The design constraint: adapters do not share one flow

The first version of this contract assumed every adapter exposes `ApplyXamlAsync`, i.e. that the
IDE detects the edit and pushes it. Building two adapters showed that is wrong, and an interface
built on that assumption forces an adapter to lie about what it does. The frameworks fall into
distinct shapes, and the difference is *who owns change detection and who performs the apply*:

| Shape | Who watches for changes | Who applies | Needs the file saved? | Example |
| --- | --- | --- | --- | --- |
| **IDE-driven agent** | the IDE (editor buffer) | in-process agent, over a private channel | no - unsaved buffer text can be sent | WPF / LibreWPF, via `DOTNET_STARTUP_HOOKS` + named pipe |
| **Framework-driven sidecar** | the framework's own server | the framework | **yes** - it watches the filesystem | Uno, via DevServer |
| **Debugger-mediated** | the debugger/IDE | the debugger channel | n/a | WinUI (research) |

Two consequences drive everything below:

- A framework-driven adapter has no meaningful `ApplyXamlAsync`. For Uno, "Apply" can only mean
  "save the document and wait"; OpenDevelop never transmits an edit. Modelling that as the same
  method as the WPF agent's real apply would produce an adapter whose success return value means
  nothing.
- "Started" is not "ready". Measured on Uno: the DevServer accepts connections long before its
  Roslyn workspace is loaded, and an edit saved in that window is silently lost. Readiness must be
  something an adapter *reports*, never something the workbench infers from a started process.

### Contract

Live in a workbench service outside `Designer.Remote`, in `ICSharpCode.SharpDevelop.Project.HotReload`.

```csharp
public interface IApplicationHotReloadAdapter
{
    string Framework { get; }                 // "WPF", "Uno", "WinUI" - also the output channel name

    // Selection. Must be cheap and side-effect free: it runs on every command-enablement query.
    bool CanHandle(HotReloadLaunchContext context, out string diagnostic);
    HotReloadCapabilities GetCapabilities(HotReloadLaunchContext context);

    // Launch participation. The adapter mutates the ProcessStartInfo the normal Run/Debug path
    // already built (env vars, startup hooks, arguments) and may start sidecar processes it owns.
    // It never launches the application itself - that stays with the project's launch path, so
    // build-before-run, debugger attach and the launch fingerprint keep working unchanged.
    Task<IHotReloadSession> StartAsync(HotReloadLaunchContext context, ProcessStartInfo startInfo,
                                       CancellationToken token);
}

public interface IHotReloadSession : IAsyncDisposable
{
    string Framework { get; }
    HotReloadSessionState State { get; }              // see state machine below
    event EventHandler<HotReloadSessionState> StateChanged;

    // Only meaningful when Capabilities.Delivery == IdePushesEdits; a framework-driven adapter
    // returns Unsupported rather than pretending. The workbench asks the capability first.
    Task<HotReloadApplyResult> ApplyAsync(HotReloadDocumentChange change, CancellationToken token);
}

public enum HotReloadChangeDelivery { IdePushesEdits, FrameworkWatchesFiles }

public sealed record HotReloadCapabilities(
    HotReloadChangeDelivery Delivery,
    bool RequiresSavedFile,            // Uno: true. WPF agent: false.
    bool SupportsUnsavedBuffer,
    ImmutableArray<HotReloadChangeKind> SupportedChanges,
    bool ReportsStatePreservation);
```

`HotReloadDocumentChange` carries the absolute source path, previous accepted text, current text,
document version, and a conservative classification. `HotReloadApplyResult` carries a stable
outcome code, a user-facing message, the framework diagnostic, and the state-preservation result.
**Sending a request is never reported as success**, and neither is saving a file.

### Session state machine

This is the one thing every adapter must express identically, because it is what the UI binds to
and what a test can wait on:

```text
NotStarted → Starting → Connecting → Ready ⇄ Applying → Applied
                            │                    ├→ Degraded         (applied, state not preserved)
                            │                    ├→ RestartRequired  (x:Class, incompatible codegen)
                            │                    └→ Failed           (framework rejected it)
                            ↓
                    Unsupported / Disconnected / Stopped
```

- `Ready` means the framework can actually accept a change now. Uno reaches it when its DevServer
  reports it is watching the project; WPF reaches it when the agent answers on its pipe.
- `Applied`/`Degraded`/`Failed` are per-change and return to `Ready`.
- `Disconnected` is terminal for the session: application exit, debugger detach, solution close, or
  a launch-fingerprint change.

### Registry and selection

Adapters register through the AddIn tree (`/SharpDevelop/HotReload/Adapters`), so a new framework
is a new AddIn contribution and touches no shared file. `IHotReloadService` resolves the adapter
for a startup project by asking each `CanHandle` in registration order and returns the first match
plus its capabilities.

This replaces the current hard-wiring, which is the concrete debt to pay off: `UnoHotReloadService`
is called statically from `HotReloadSupportedConditionEvaluator`, `Commands.Execute.Run` and
`DefaultProjectBehavior.StartCore`. Each of those becomes a call to the service:

| Today | Becomes |
| --- | --- |
| `UnoHotReloadService.CanConfigureLaunch(project, out _)` in the condition evaluator | `SD.GetService<IHotReloadService>().FindAdapter(project) is not null` |
| `UnoHotReloadService.CanConfigureLaunch(...)` in `Execute.Run` | the service selects the adapter; the command only decides *whether* Hot Reload was requested |
| `UnoHotReloadService.TryConfigureLaunch(project, psi)` in `DefaultProjectBehavior.StartCore` | `session = await service.StartAsync(context, psi)`, with the session owned by the workbench |

`DefaultProjectBehavior` then names no framework at all, which is the test that the abstraction is
real.

### Common UI and workflow

One surface for every framework; the adapter only supplies data:

- **Commands**: Start/Stop Hot Reload, and Apply. Enablement comes from the resolved adapter's
  capabilities, not from a framework-specific condition evaluator. For an adapter whose
  `Delivery == FrameworkWatchesFiles`, the Apply command is presented as **Save** (and is a no-op
  beyond saving), because that genuinely is the trigger - the UI must not offer an action the
  adapter cannot perform.
- **Status**: framework, process id, session state, last outcome, state-preservation. Bound to
  `StateChanged`, so it is the state machine above that is displayed, not per-framework strings.
- **Output**: a single Hot Reload channel, as in VS, carrying the active adapter's own diagnostics
  - for Uno the DevServer log, for WPF the agent log - with the framework named in each message.
  One command, one session at a time, one place to look.
- **Auto-apply on save**: opt-in, debounced, cancellable, serialized per session. For a
  framework-driven adapter it is implicitly always on, since saving is what triggers it; the UI
  should say so rather than showing a toggle that does nothing.

### What each existing adapter looks like under this model

| | WPF / LibreWPF | Uno | WinUI |
| --- | --- | --- | --- |
| `Delivery` | `IdePushesEdits` | `FrameworkWatchesFiles` | debugger-mediated (unmodelled until proven) |
| `StartAsync` does | sets `DOTNET_STARTUP_HOOKS`, random pipe name, `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1` | starts the DevServer sidecar, sets `UNO_DEV_SERVER_*`, `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, `--metadata-updates true` | selects/validates debugger channel |
| Reaches `Ready` when | the agent answers on its pipe | the DevServer reports it is watching | n/a |
| `ApplyAsync` | sends XAML over the pipe, returns the agent's outcome | returns `Unsupported`; saving is the trigger | n/a |
| `RequiresSavedFile` | no | yes | n/a |

### Common change classifier

Classification is an optimisation hint, not a promise that a runtime can apply the edit. It only
applies to `IdePushesEdits` adapters: when the framework watches the files itself there is nothing
to classify, because OpenDevelop never sees the change request.

| Kind | Examples | Adapter action |
| --- | --- | --- |
| `Property` | Text, Margin, Background on an existing element | Direct patch when supported |
| `Subtree` | Child add/remove/reorder | Replace smallest safe subtree |
| `Resource` | Dictionary, style, template, theme | Reload live resources where supported |
| `FullDocument` | Complex/root structural edit | Framework full-file fallback |
| `CodeDelta` | Supported C#/VB delta | Delegate to framework/.NET implementation |
| `RestartRequired` | x:Class or incompatible generated code | Explain and offer restart |

Retain the complete new XAML as fallback and update `previous accepted text` only after a successful
apply. Unsaved buffer text is applied only through explicit Apply or Save. Automatic-on-save is
opt-in, debounced, cancellable, and serialized per application session.

## Framework adapters

### WPF: planned adapter

WPF is first because `externals/vscode-wpf` already contains a working reference implementation.
It injects `WpfHotReload.Runtime` through a startup hook (or .NET Framework AppDomain manager),
uses a random named pipe, and lets the in-process agent use WPF diagnostic source information to
find live objects. It supports property, resource, subtree, and full-file fallback strategies.

1. Package/reference the runtime helper as an OpenDevelop artifact while preserving its licence and
   dependency boundary.
2. Implement `WpfApplicationHotReloadAdapter`, which launches the app with an authenticated random
   pipe and `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1`.
3. Extract the newline-delimited JSON pipe client into a runtime-hot-reload transport library, not
   `Designer.Remote`.
4. Map the WPF agent result into `HotReloadApplyResult`.
5. Test properties, insertion/removal, resources, full-file fallback, rejected x:Class/event edits,
   delayed agent startup, process exit, and reconnect handling.

The runtime agent may use WPF diagnostic APIs only inside the application process; OpenDevelop must
not mutate a running application's visual tree directly.

### WinUI 3: debugger-backed research adapter

WinUI 3 supports XAML Hot Reload under supported debugging tooling, but Visual Studio's transport is
not a public general-purpose application API. Loose-XAML parsing is not an equivalent replacement:
it loses compiled-XAML semantics such as `x:Bind`, generated metadata, and native WinAppSDK state.

1. Study Windows App SDK and WinUI XAML source to identify public extension points and debugger-
   private mechanisms.
2. Implement capability detection and lifecycle integration for an OpenDevelop-launched debugger.
3. Bridge a supported debugger protocol only if one is stable and documented; do not reverse
   engineer a private Visual Studio channel as a product dependency.
4. Require the exact active TFM, RID, Windows App SDK graph, and architecture used to launch the
   app. A mismatch is a hard failure.
5. Validate with `x:Bind`, resource dictionaries, and a third-party compiled control.

### Uno Platform: paused, and the reference case for a framework-driven adapter

Active work stopped by decision, in favour of concentrating on WPF. It is not deleted: it is the
only worked example of the `FrameworkWatchesFiles` shape, so it is the candidate for the phase 5
step that validates the abstraction against a second adapter. Its launch integration already
exists, which makes it the cheapest way to prove that shape is expressible without special-casing.

What follows is the original plan plus what was actually measured. The findings are real defects
that would otherwise cost the same investigation twice:

- **`--metadata-updates true` was never passed to the DevServer.** Without it,
  `ServerHotReloadProcessor.InitializeMetadataUpdater` takes its other branch and logs
  "Metadata updater **NOT** initialized", whose own comment reads *"We are relying on IDE, we won't
  have any other hot-reload initialization steps"* - the DevServer then expects the IDE to carry
  hot reload over its own debugger channel, which OpenDevelop does not do. Passing the flag
  (`UnoHotReloadService.StartSession`) fixed that: the log becomes "Metadata updater initialized"
  and a saved edit really does produce `Found 1 metadata updates`.
- **The DevServer is not ready when it starts listening.** It accepts connections long before its
  Roslyn workspace is loaded, and only then logs "Observing ... project directories for metadata
  changes". An edit saved before that point is silently never seen, which is indistinguishable from
  a hot reload that did nothing. `od.hot-reload.status` exists to observe this.
- **The licensing denials are a red herring for Hot Reload.** The DevServer log is full of
  `Denied access to feature 'uno.hotreload'` because no Uno Platform account is signed in, and
  Uno's own docs say sign-in unlocks Hot Reload - but the open-source `FileUpdateProcessor` /
  `ServerHotReloadProcessor` never consult the licensing service, and with the flag above the
  server produced a metadata delta with no account at all. The denials come from the closed-source
  `uno.settings.devserver` plugin and gate Hot **Design**. Do not conclude "no licence, no hot
  reload" from those lines.
- Where it stopped: the server computes the delta, but the running application never applies it.
  The next thing to check would have been whether the delta is actually transmitted to, and
  accepted by, the debuggee (`DOTNET_MODIFIABLE_ASSEMBLIES=debug` does reach it, via
  `WindowsDebugger`'s `hotReloadEnvironment`), and whether a debugger-attached process expects the
  IDE rather than the runtime to apply it.

The original plan, for reference:

#### Uno Platform: DevServer/Studio adapter (macOS MVP)

Uno has its own metadata-update and UI-update pipeline, with target- and SDK-version-specific
behaviour. Its published integration point is UI update handling. OpenDevelop should integrate the
matching Uno SDK/DevServer endpoint instead of duplicating Uno's compiler, type replacement, or UI
update engine.

The first implementation targets Uno desktop apps on macOS only. It uses Uno's public runtime
environment override rather than changing the generated project configuration:

1. Detect an Uno project with `XamlFrameworkDetector` at Run/Debug launch.
2. Read `obj/project.assets.json` to locate the project's exact `Uno.WinUI.DevServer` version in
   the local NuGet package cache, then start that host for the saved solution. A cache-version
   fallback is only for incomplete restores.
3. Allocate a loopback port and inject `UNO_DEV_SERVER_HOST=127.0.0.1` and
   `UNO_DEV_SERVER_PORT=<port>` plus `DOTNET_MODIFIABLE_ASSEMBLIES=debug` into the launched
   application only. Uno's `RemoteControlClient`
   gives these variables precedence over generated `ServerEndpointAttribute` values, so no
   `.csproj.user` write and no pre-launch rebuild are necessary.
4. Let the DevServer's own workspace and file watchers observe saves, generate Roslyn metadata
   deltas, and apply Uno's existing UI update pipeline. OpenDevelop neither sends DDP frames nor
   implements metadata delta generation.
5. Expand only after a real Skia/macOS session confirms the app connects and a saved XAML update
   is accepted. WebAssembly, Android, iOS, and WinAppSDK remain outside this MVP.

The next increment adds a workbench-facing session/status model by consuming DevServer diagnostics.
That is deliberately separate from launch enablement: a successful process start is not reported as
a successful hot reload.

Uno projects targeting native WinAppSDK are evaluated by the WinUI adapter when they use that
runtime, not automatically by the Uno DevServer adapter.

## Build and launch integration

### User-journey acceptance test

Visual Studio's runtime XAML Hot Reload workflow starts with **F5 / Start Debugging**.
After the application and Hot Reload are ready, the user edits XAML; when the
"Apply XAML Hot Reload on document save" option is enabled, **Ctrl+S** applies the edit.
**Shift+F5** ends the debugging session. The Hot Reload button / **Alt+F10** applies
changes to an existing session; it is not a separate application launch command.
See [Microsoft's XAML Hot Reload documentation](https://learn.microsoft.com/en-us/visualstudio/xaml-tools/xaml-hot-reload).

Acceptance coverage must drive the real command bindings for launch, save and stop,
verify readiness before editing, and read the current application's visual tree after
saving. Assert that the process ID and unrelated application state survive the edit.
An action which constructs ProcessStartInfo and calls Process.Start directly only
tests the adapter transport; it does not cover the toolbar, F5, build-before-run or
debugger lifecycle. The current `od.hot-reload.start` test is in that latter category.
For a launch adapter that declares Hot Reload support, F5 must configure that adapter
before starting the normal debugger session. The explicit Start Hot Reload command
remains useful as an intentional menu/toolbar entry point, but must not be presented
as the only Visual Studio-style workflow.

For Uno, the sample must call `Window.UseStudio()` in Debug to register its window
for UI updates, and its DevFlow probe must inspect the current window rather than
retain a page that Hot Reload can replace. macOS temporary fixtures must resolve
directory symlinks before building: MSBuild AdditionalFiles can use `/private/var`
while a watcher uses `/var`, and Uno's literal path comparison then misses the XAML.

### Known gaps: portable-host F5 journey (2026-09-21, updated 2026-09-20 session)

The adapter transport portion is verified independently: the temporary Uno fixture
starts with `Window.UseStudio()`, a real native **Ctrl+S** save produces a visible
XAML update, and the probe confirms that both the process ID and a process-level
session token survive it. This is not yet evidence that the complete F5/Ctrl+S/
Shift+F5 journey works.

- The deployed build was actually broken at session start, unrelated to the Hot Reload
  code itself: several `AddIns/**` folders had a build's `.pdb` files but not the
  matching `.dll`s, producing `FileLoadException: ICSharpCode.Core, Version=... does not
  match the assembly reference` at startup - the version-mismatch trap this repo's
  AGENTS.md already documents. A full `dotnet build OpenDevelop.Mvp.slnx` followed by a
  shell rebuild fixed it; the app now starts cleanly with zero `Cannot find class`
  warnings. Always rule this out first before treating an F5/Hot Reload symptom as a
  feature bug.
- **Root cause found and fixed**: Ctrl+S was not a genuine "reaches the editor fine"
  case as previously assumed. Live reproduction (open the fixture XAML, edit it via
  `od.file.replace-text`, send a real native `kd:ctrl kp:s ku:ctrl` through
  `CliclickSharp`, then read the file back from disk) showed the file staying
  unchanged after repeated native Ctrl+S presses, even though the document was
  genuinely dirty and still the active view. Same underlying cause as the F5 gap below:
  `Window.IsActive` stays `false` on this portable host even while the window is
  genuinely frontmost (`od.activate`'s `foregrounded` is `true`), so WPF's
  `InputBindings`/`CommandBindings` key routing - which depends on WPF's own notion of
  focused element/active window - silently does nothing instead of erroring. Fixed by
  adding an explicit Ctrl+S fallback beside the existing F5 family in
  `WpfWorkbench.OnPreviewKeyDown` (`src/Main/SharpDevelop/Workbench/WpfWorkbench.cs`),
  calling `ICSharpCode.SharpDevelop.Commands.SaveFile` directly the same way the F5
  fallback calls the debugger commands directly, bypassing the unreliable
  InputBindings path entirely.
- The current end-to-end test has not passed with native F5 and Shift+F5. On the
  portable macOS WPF host, a synthetic function key did not activate the menu
  InputBinding. The workbench has an unhandled-key fallback which calls the same Run,
  Start Without Debugging, and Stop commands for F5, Ctrl+F5, and Shift+F5. The portable
  host may report a function key as `Key.System` with its physical value in
  `SystemKey`, so the fallback normalizes that representation. Native F5 delivery
  itself is now confirmed to work at least intermittently (one full session run got a
  native F5 all the way through debugger start, DevServer connect, and the "before"
  probe), but repeated same-session reruns then consistently missed the test's 45s
  `WaitForDebuggerAsync` window. This still needs a genuinely green native-key
  integration run before the F5 half can be called fixed; it must not be claimed as
  fixed merely because the command code compiles.
- A F5 run using the normal `BuildBeforeExecute` setting became unresponsive while its
  internal MSBuild workers were active, preventing the UI-thread DevFlow status action
  from observing launch progress. The integration test temporarily selects
  `BuildDetection.DoNotBuild` only after rebuilding its isolated fixture, and restores
  the previous preference in cleanup. This isolates keyboard/debugger/Hot Reload
  coverage; it does not validate OpenDevelop's build-before-run responsiveness.
  **New finding**: even with `od.build.set-on-execute DoNotBuild` confirmed set, a live
  repro still logged a real ~19s `[TimingDiag] BuildEngine.BuildAsync detection=
  RegularBuild` build after opening the fixture's XAML file. `BuildOptions.BuildOnExecute`
  (`src/Main/Base/Project/Project/Build/BuildOptions.cs`) only gates
  `Commands.BuildBeforeExecute`/`BuildProjectBeforeExecute` (`BuildCommands.cs`); it is a
  separate, independent build path in `DesignerBuildCoordinator.cs`
  (`src/Main/Base/Project/Designer/DesignerBuildCoordinator.cs`) - triggered by opening a
  designer-backed XAML document - that also calls `SD.BuildService.BuildAsync` and is not
  gated by that preference at all. On a machine already busy with other builds, this
  designer-triggered build alone can consume most or all of the test's 45s window before
  F5's own (correctly skipped) build-before-run step or the actual launch even get a
  chance to run. Not yet fixed: either `DesignerBuildCoordinator` needs its own
  `BuildDetection`-aware skip for automated/hot-reload journeys, or the test needs to
  either close the XAML document (or open it after F5, not before) to avoid triggering
  the designer's own build, or the timeout needs to account for it.
- **Blocker found: Uno's DevServer refuses Hot Reload without a signed-in Uno Platform
  account.** With everything else working end to end - F5 launches the debuggee, the
  DevServer starts (`Uno Hot Reload: using DevServer on 127.0.0.1:<port>`), the edit is
  saved to disk, and the live probe answers - the running application never receives the
  update. The DevServer's own log (`$TMPDIR/od-uno-devserver-<pid>.log`, path available
  from `UnoHotReloadService.GetDevServerLogFile`) says why:

  ```text
  dbug: Uno.Licensing.Sdk.LicensesStore[2]     No logged-in user, no licenses to get.
  dbug: Uno.Licensing.Sdk.LicensingService[2]  Denied access to feature 'uno.hotreload'.
  dbug: ServerHotReloadProcessor[0]            Metadata updater **NOT** initialized.
  ```

  This is by design on Uno's side, not a defect here. Uno's own documentation
  (`doc/articles/get-started-licensing.md` in the Uno repo) states: "Sign in with your Uno
  Platform account directly in your favorite IDE ... to unlock powerful tools like Hot
  Reload." The transport is open source (`Uno.UI.RemoteControl.*` in the Uno repo), but the
  gate is not: `Uno.Licensing.Common/Sdk/Sdk.Contracts` ship as binaries inside the
  `uno.settings.devserver` package, authenticate against `https://platform.uno/` over OIDC,
  and cache a token under `~/Library/Application Support/Uno Platform/Licensing SDK/Cache`
  (empty here, hence the denial). Registration is free, but it is a hard prerequisite.

  Consequences for this work:
  - The Uno adapter cannot be validated end to end on a machine with no Uno sign-in, and
    neither can this integration test. Treat a "saved, but the app did not update" result
    as a licensing check first; read the DevServer log before suspecting our launch code.
  - Supporting Uno Hot Reload properly means OpenDevelop also has to carry the IDE side of
    that sign-in handshake, which is what the Visual Studio / VS Code / Rider extensions
    do. That is a separate piece of work and is not in this increment.
  - The test should be skipped, not failed, when the DevServer reports the feature denied,
    so it does not read as an OpenDevelop regression.
- The test-only probe port is written into the copied fixture's output directory before
  F5. It is intentionally not a product launch override: the normal F5 command still
  owns the process and DevServer environment. The fixture uses this rendezvous solely
  so the test can query the live application's visual tree after launch.
- The completion gate is one green integration run which sends native F5, waits for the
  debugger-backed Uno process and DevServer readiness, edits and saves XAML with native
  Ctrl+S, verifies an in-process visual-tree change with stable PID/session token, then
  sends native Shift+F5 and verifies debugger/process termination. Until then,
  `od.hot-reload.start` remains adapter coverage only.

The dedicated Hot Reload launch pipeline creates a hot-reload session. It reuses the normal
project launch/build decision; supported adapters participate in F5 / Start Debugging rather
than requiring a framework-specific launch patch. `DesignerBuildCoordinator` is not the runtime
feature, but its evaluated target identity selects the correct executable, TFM, RID, architecture,
output directory, and dependency graph.

- Initial launch follows the normal build decision and records an immutable launch fingerprint.
- XAML-only updates do not build before apply.
- `CodeDelta` updates ask the active adapter whether a supported code-update path exists; otherwise
  the outcome is build/restart required.
- Configuration or runtime-graph changes terminate the session and require relaunch.

## UX, security, and observability

Visual Studio has **no dedicated Hot Reload tool window**, and neither should we. Its status lives
in exactly two places, and that is the model to follow (checked against Microsoft's documentation,
not from memory - an earlier draft of this note invented a "status pad" that does not exist):

1. a **Hot Reload drop-down button** on the toolbar, carrying restart-application, a "Hot Reload on
   save" toggle, and a link to settings;
2. an **Output window pane** named Hot Reload, carrying status messages, with a logging-verbosity
   setting.

(VS's Live Visual Tree *is* a tool window, but it inspects the running visual tree - it is the
counterpart of a designer/element inspector, not of Hot Reload status.)

So:

- Provide the dedicated Start/Stop Hot Reload command and toolbar icon alongside Run/Debug. **Done.**
- Show a per-document Apply/Hot Reload action only for a compatible running session. **Done** -
  and it is labelled Save for an adapter whose framework watches the files, since Apply would be a
  lie there.
- Report framework, process id, connection state, last outcome and state-preservation through the
  output channel and the status bar - not a pad. Currently the output channel carries the outcome
  per apply; process id and connection state are not surfaced yet.
- **One** Hot Reload output channel, as in VS. **Done.** An earlier draft called for one channel
  per framework; that was wrong. There is a single Hot Reload command and a single session at a
  time - the framework follows from the startup project - so per-framework panes would only make
  somebody asking "why did my reload not work" guess which pane to open. The framework is named in
  the message instead.
- Still outstanding, in VS-parity order: the toolbar drop-down (restart application, apply-on-save
  toggle, settings), the apply-on-save option itself, and output verbosity.
- Generate unguessable local endpoints and authenticate every request.
- Dispose sessions on app exit, debugger detach, solution close, or launch-fingerprint change.
- Do not attach to arbitrary existing PIDs in the MVP.

## Delivery phases

Ordered so the abstraction is proven by a second adapter rather than designed in the abstract. The
one-adapter phases deliberately come first: a contract validated against a single framework is how
the previous `ApplyXamlAsync`-for-everyone mistake happened.

1. **Foundation**: the contract, session state machine, registry and `IHotReloadService`; the
   launch-participation hook in `DefaultProjectBehavior` (which must then name no framework); the
   launch fingerprint; fake-adapter unit tests covering the state machine and outcome reporting.
2. **WPF MVP**: package the agent (done - `src/Main/HotReload/WpfHotReload.Agent`), the transport
   library, authenticated random pipe, `WpfApplicationHotReloadAdapter`, manual Apply, property and
   full-file updates, integration tests against a launched sample.
3. **Common UI**: Start/Stop command and toolbar icon, Hot Reload output channel, capability-
   driven command enablement (done), then VS parity for the rest - a Hot Reload drop-down button
   (restart application, apply-on-save toggle, settings) and output verbosity. No tool window: VS
   does not have one for Hot Reload either.
4. **WPF quality**: resource/subtree updates, opt-in save trigger, reconnection, and
   state-preservation reporting.
5. **Second adapter to validate the abstraction**: re-introduce a framework-driven adapter (Uno is
   the obvious candidate, given the launch integration already exists) purely to prove that
   `FrameworkWatchesFiles`, `RequiresSavedFile` and reported readiness are enough to express it
   without special-casing. If the common UI needs a framework-specific branch to support it, the
   abstraction is wrong and gets fixed here, not worked around.
6. **WinUI research/probe**: capability adapter and verified debugger-backed prototype.
7. **Code updates** only after each adapter has a reliable supported delta path.

## WPF MVP acceptance criteria

- Launching a WPF app through OpenDevelop establishes one authenticated runtime session.
- A valid property-only edit updates the running UI without build or restart.
- Unsupported edits report `RestartRequired` or `Unsupported`, never false success.
- Closing the application removes the session and disables Apply without error spam.
- Multiple editor documents targeting one process serialize through one session.
- Integration tests use an application test endpoint to verify observable state rather than relying
  on screenshots alone.
