# Mobile debugging: status and remaining work

**Status: work in progress, updated 2026-11-05.** iOS Simulator debugging works in the IDE for the tested
.NET 11 CoreCLR configuration. Android debugging works end to end on an API 35 ARM64 emulator with the
NativeAOT agent hosted on SharpDbg's own MIT `SharpDbg.DapProtocol`, restored from the published agent
package. Other ABIs, physical devices, CI/release packaging and several failure paths are not yet
exercised.

This note is the current status for mobile debugging. The experiments and
historical implementation details are in [uno-mobile-designer.md, sections 4c
and 4d](uno-mobile-designer.md#4c-ios-simulator-debugging-on-net-11-coreclr-spike-2026-11-04-breakpoint-hit).
Some statements in those sections describe earlier spikes and precede the work
below.

## Scope and architecture

The tested mobile runtime is .NET 11 CoreCLR. Mono and physical iOS devices are
not covered. This is a C# NativeAOT agent, not a C++ debugger agent. DAP is the
transport between the IDE and the device-side debugger; SharpDbg and ICorDebug
perform the actual managed debugging in the target process.

```text
iOS simulator:  OpenDevelop -> host SharpDbg -> dbgshim/ICorDebug -> app
Android:        OpenDevelop -> adb forward -> target-side DAP -> SharpDbg/ICorDebug -> app
```

On the iOS Simulator, OpenDevelop launches the app stopped, supplies the app
container's `tmp` directory to the host-side adapter, and attaches using
retargeted copies of the target runtime's simulator DBI/DAC libraries. The
implementation and device-selection details are in section 4c of the linked
note. A physical-device path and signing remain future work.

On Android, a C# NativeAOT shared library is loaded into the app as a CoreCLR
profiler and hosts SharpDbg in its own NativeAOT runtime. A session token and
port are placed in app-private storage, and the host connects to device
loopback through an `adb forward`. The startup gate lets the adapter attach and
bind startup breakpoints before application code runs. The implementation uses
Silhouette's profiler callbacks and a C# class factory with the correct native
`int` ABI for `IClassFactory.LockServer`.

## Verified results

- **iOS:** on macOS ARM64, F5 on a .NET 11 Uno iOS Simulator app hit a
  `MainPage` constructor breakpoint; the stack resolved and Stop terminated the
  app. The plain iOS integration test also hit a `FinishedLaunching` breakpoint.
- **Android agent:** on macOS ARM64 with an API 35 ARM64 emulator,
  .NET SDK `11.0.100-rc.3.26522.148`, and Android workload
  `37.2.0-rc.2.84`, `AndroidDebuggingIntegrationTests.AndroidApp_F5StopsStepsEvaluatesAndStopsCleanly`
  passed (1/1, no skips). It selected the adb serial through the IDE, built a
  project using custom output directories, hit the startup breakpoint, stepped,
  evaluated `answer + 1` to `124`, and stopped. The agent is built for
  `android-arm64` by the SharpDbg package workflow, with no GitHub token needed in
  the environment. This replaced an earlier locally written target-side DAP server.
- A separate Android device experiment exercised exception filter
  configuration and a conditional breakpoint. The agent was built for
  `android-arm64`, `android-x64`, and `android-arm`; only ARM64 has been
  exercised end to end on a device/emulator. Android NativeAOT and the .NET 11
  workload used here are experimental/pre-release.
- The fork's protocol smoke test passed on macOS ARM64 and on the Android ARM64
  emulator for initialization, attach with a UTF-8 extension property, a
  stopped event, breakpoints, variables, evaluation, threads, stack frames,
  scopes, stepping, invalid input, and a raw JSON extension request. Its Android
  test uses a small temporary native loader to invoke the C# test library; it is
  not shipped as an agent. These are DAP protocol tests with test handlers, not
  an actual CLR debugging session.
- Fork regression suites: DAP 446 passed and 1 skipped; JSON-RPC 159 passed and
  9 skipped. The package consumer was also published and run as Linux NativeAOT
  in the fork's publishing runs.

## DAP protocol implementation

The desktop adapter and the Android agent both use SharpDbg's own
`SharpDbg.DapProtocol`: a MIT implementation generated from the official
`debugAdapterProtocol.json`, with a source-generated JSON context so it works
under NativeAOT. It replaced lextudio's fork of `OmniSharp.Extensions.DebugAdapter`
(the five `OmniSharp.Extensions.*` packages from
[lextudio/csharp-language-server-protocol](https://github.com/lextudio/csharp-language-server-protocol)).
That fork needed `buildTransitive` targets to preserve reflection metadata for
DryIoc and handler discovery, and an earlier revision failed under NativeAOT on
argument-less requests (`JsonSerializerIsReflectionDisabled`); `SharpDbg.DapProtocol`
has neither problem.

The migration removed the fork from the adapter and agent closures, so
`Directory.Packages.props` no longer defines `LextudioDapForkVersion`, the local
feed no longer carries the five packages, and `iac/pack-opendevelop-android-agent.ps1`
no longer needs a DAP fork feed. The Android transport test
(`SharpDbg.AndroidAgent.Tests`) drives the new implementation over a TCP stream and
passes 5/5, including token/attach gating, sanitized error text, exception filter
capabilities and 0/1-based line handling.

Neither the Android agent nor the desktop SharpDbg adapter references Microsoft's
`Microsoft.VisualStudio.Shared.VSCodeDebugProtocol`. That package remains only in the
adapter's test project, as its DAP client, and is not part of any payload. See
[licensing-audit.md](licensing-audit.md). No decompiled `clrdbg` or `ICorDebugSharp`
source was imported.

## Remaining work

1. Failure paths now covered by tests (all pass): a wrong session token is refused by the agent
   (`SharpDbg.AndroidAgent.Tests`) and reported by the IDE without sending `configurationDone`
   (`RemoteDapSessionTests`); cancellation while launching cleans up everything the session owns, and
   cancellation before anything is owned leaves the device untouched; a repeated session starts from a clean
   state; a second concurrent session is refused without disturbing the first; device selection never guesses
   or falls back (`AndroidDebugSupportTests`, fake `adb`). The integration journey stops and starts the app
   twice and checks that no `mDebugApp` setting or new `adb forward` is left (forwards that were already there
   are left alone). Still open: locals/watch panes, exception-filter configuration from the IDE, cancellation
   while MSBuild runs, selecting between two real devices, and a physical device.
2. The `x86_64` and `armeabi-v7a` agents build and pass the ELF/notice verification but have not run on a
   device. NativeAOT trimming warnings and reflection-dependent features need targeted coverage.
3. The agents and their SPDX/notices are published once by the `sharpdbg` repository's
   `publish-opendevelop-packages.yml`; OpenDevelop restores `LeXtudio.SharpDbg.AndroidAgent`, verifies
   the three ABIs after its build, and the debugging journeys run in the `mobile-android` and
   `mobile-ios` lanes of `.github/workflows/ci.yml` (see [`ci-test-plan.md`](ci-test-plan.md),
   "Device journeys"). The release packaged-app check still has no Android device, so it covers the
   agent's files and metadata only. The lanes start `observe` with `allowAllFailures`, so read their
   per-entry results rather than their colour: a journey whose device or workload was missing skips and
   still reports a passing total.
4. Remove or replace the desktop adapter's Microsoft debug-protocol dependency, if the product migrates.
5. Physical iOS devices and signing remain future work.

Android startup temporarily sets the system's debug-app setting to avoid a startup ANR while waiting for
attach. The code refuses to overwrite an existing setting and cleans up its own. A freshly booted device has
no `mDebugApp` line in `dumpsys activity processes` until one is set; a complete dump (`mProcessesReady=true`)
without that line means "unset", while an incomplete dump is an error. Verify cancellation and cleanup on the
target devices rather than assuming this covers every Android version.
