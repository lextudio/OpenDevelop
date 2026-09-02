# DevFlow

DevFlow is an in-process HTTP automation/introspection server embedded in every OpenDevelop
build (Debug and Release). It lets tests, scripts, and diagnostic tools drive the real IDE —
open solutions, run builds, execute tests, inspect the visual tree, click buttons, read pad
state — without relying on OS-level accessibility or mocking anything.

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│  OpenDevelop.exe (WPF/LibreWPF process)                 │
│                                                          │
│  App.xaml.cs ──► AddWpfDevFlowAgent(AgentOptions)        │
│       │                                                  │
│       ▼                                                  │
│  LeXtudio.DevFlow.Agent.Core (in-process HTTP server)    │
│       │                                                  │
│       ├── GET  /api/v1/agent/status                      │
│       ├── GET  /api/v1/invoke/actions                    │
│       ├── POST /api/v1/invoke/actions/{name}             │
│       ├── GET  /api/v1/ui/tree                           │
│       └── POST /api/v1/ui/actions/{click,press,...}      │
│                                                          │
│  Action classes (discovered via reflection):              │
│    [DevFlowUIThread]                                     │
│    static class FooDevFlowActions                         │
│    {                                                     │
│        [DevFlowAction("od.foo.bar")]                     │
│        public static string Bar() { ... }                │
│    }                                                     │
└──────────────────────────────────────────────────────────┘
```

The agent binds to a TCP port (default 9299, pinned in `DevFlowPort.cs` via assembly metadata).
Concurrent IDE sessions use different ports — the env var `DEVFLOW_AGENT_PORT` or the
`-devflow:<port>` command-line flag override the default.

## Enable / disable logic

`App.xaml.cs:IsDevFlowEnabled()` determines whether the agent starts:

| Condition | Result |
|---|---|
| `DEVFLOW_DISABLE=1` | **Off** — agent not started |
| `-devflow:off` on command line | **Off** — for child IDE instances that must not bind the same port |
| `-devflow:<port>` on command line | **On** — agent starts on the specified port |
| `DEVFLOW_ENABLE=1` | **On** — forces agent even in Release |
| Debug build | **On** (default) |
| Release build, no env vars | **Off** — keeps the unauthenticated endpoint out of ordinary release sessions |

The intent: every Debug build has DevFlow active by default. Release builds stay quiet unless
the user or a test harness explicitly opts in via `DEVFLOW_ENABLE=1` or `-devflow:<port>`.

## Package references

`LeXtudio.DevFlow.Agent.Core` is referenced **unconditionally** by every project that defines
DevFlow actions. There is no `Condition="'$(Configuration)' == 'Debug'"` — DevFlow ships in
Release builds. The `LeXtudio.DevFlow.Agent.LibreWpf` package is referenced by the host
project (`SharpDevelop.csproj`).

## Writing DevFlow actions

### Anatomy of an action class

```csharp
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

[DevFlowUIThread]
public static class MyAddinDevFlowActions
{
    [DevFlowAction("od.myaddin.do-thing",
        Description = "Does something useful for integration tests")]
    public static string DoThing(string input)
    {
        // Must run on the UI thread — [DevFlowUIThread] ensures that.
        // Return a JSON-serializable string; the framework wraps it in
        // { "returnValue": <your string> }.
        return JsonSerializer.Serialize(new { result = input.ToUpperInvariant() });
    }
}
```

### Discovery

The DevFlow agent discovers action classes once, **before** AddIn autostart commands load
lazy assemblies. It scans all loaded assemblies for:

1. Classes annotated with `[DevFlowUIThread]`
2. Static methods annotated with `[DevFlowAction("action.name")]`

The action name string (`"od.myaddin.do-thing"`) is the name used in
`POST /api/v1/invoke/actions/od.myaddin.do-thing`.

### Registration timing

Actions in the **host** assembly (`OpenDevelopDevFlowActions.cs`) are always available.
Actions in **addin** assemblies require the addin to be loaded before discovery runs. If an
addin loads lazily (e.g. on first document open), its actions may not be discovered until
the next IDE restart. To work around this, addins can register a `RegisterDevFlowActionsCommand`
(extends `AbstractCommand`) that forces the assembly into `AppDomain.CurrentDomain.GetAssemblies()`
before the first DevFlow request.

### Thread safety

All `[DevFlowAction]` methods on a `[DevFlowUIThread]` class are dispatched to the WPF
Dispatcher. The agent's HTTP listener runs on a background thread; the Dispatcher marshal
happens inside `LeXtudio.DevFlow.Agent.Core`. Action methods can freely access WPF
controls, PAD state, and any other UI-thread-only API.

## API surface

### Agent status

```
GET /api/v1/agent/status
→ { "ready": true }
```

### List actions

```
GET /api/v1/invoke/actions
→ { "actions": ["od.build-solution", "od.unit-test.run", ...] }
```

### Invoke an action

```
POST /api/v1/invoke/actions/{name}
Content-Type: application/json

{ "args": ["arg1", "arg2"] }

→ { "returnValue": <JSON string or primitive> }
```

The `returnValue` is whatever the action method returned. If it returned a JSON string,
parse it twice (the outer envelope wraps it again).

### Visual tree

```
GET /api/v1/ui/tree
→ { "root": { ... } }   // deep JSON tree — raise JsonSerializer.MaxDepth for docking layouts
```

### Synthetic input

```
POST /api/v1/ui/actions/{click,press,drag,drag-move,release,move}
Content-Type: application/json

{ "x": 100, "y": 200, "global": true, "clickCount": 1 }
```

Works even where OS accessibility cannot reach the target (WPF-on-macOS LibreWPF apps).

## Build / test actions

These are the primary actions used for automated build→test→verify workflows:

| Action | Description |
|---|---|
| `od.build-solution` | Build the loaded solution. Returns `{ success, result, errorCount, warningCount, diagnostics[], buildLog }` |
| `od.unit-test.run` | Run all tests, wait for completion. Returns `{ started, completed, timedOut, passed, failed, skipped, failedTests[] }` |
| `od.unit-test.run-failed` | Rerun only previously-failed tests. Same return shape. |
| `od.unit-test.tree` | Full test tree with display names and results. |
| `od.unit-test.output` | Raw UnitTesting output pad text. |
| `od.unit-test.cancel` | Cancel an in-progress test run. |
| `od.unit-test.refresh` | Refresh the test tree from the loaded projects. |

### Recommended workflow

```
1. POST od.build-solution → check result=="Success", errorCount==0
2. POST od.unit-test.run  → check failed==0, failedTests is empty
3. On failure: POST od.unit-test.output → extract error details
4. Optional: POST od.unit-test.run-failed → rerun just the failures
```

## Force DevFlow in Release builds

For debugging or diagnostics on an installed release build:

```bash
# Via environment variable:
DEVFLOW_ENABLE=1 open /Applications/OpenDevelop.app

# Via command line:
open /Applications/OpenDevelop.app --args -devflow:9300

# Override the port:
DEVFLOW_AGENT_PORT=9300 open /Applications/OpenDevelop.app
```

This lets you hit the API on the specified port and drive the release build exactly like a
Debug build. The release binary contains all DevFlow action code — nothing is compiled out.

## Integration with testing

### `tests/OpenDevelop.IntegrationTests`

The integration test suite (`tests/OpenDevelop.IntegrationTests`) launches OpenDevelop as a
child process, waits for `GET /api/v1/agent/status` to return `ready: true`, then drives
the IDE through the DevFlow API. Each test class shares one app instance via
`[Collection("...")]`. See `doc/technotes/integration-testing.md` for the full fixture
contract.

### `tests/fixtures/DebugTestApp`

A minimal app that embeds the DevFlow agent for testing agent-level behavior in isolation
without loading the full IDE. Uses `DEVFLOW_AGENT_PORT` to pick a free port.

## File inventory

| File | Role |
|---|---|
| `src/Main/SharpDevelop/DevFlowPort.cs` | Pinned port (9299) via `AssemblyMetadata` |
| `src/Main/SharpDevelop/Startup/App.xaml.cs` | Enable/disable logic, agent bootstrap |
| `src/Main/SharpDevelop/DevFlow/OpenDevelopDevFlowActions.cs` | Core host actions (~200 actions) |
| `src/AddIns/.../DevFlowActions.cs` | Per-addin action classes (forms designer, WPF designer, WinUI, ILSpy, NuGet, etc.) |
| `src/AddIns/.../RegisterDevFlowActionsCommand.cs` | Forces addin assembly into discovery before first request |
| `Directory.Packages.props` | `LeXtudio.DevFlow.Agent.Core` and `LeXtudio.DevFlow.Agent.LibreWpf` versions |
