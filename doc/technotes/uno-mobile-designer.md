# Uno designer: iOS Simulator and Android Emulator previews

Status: project charter and design (2026-11-03), updated 2026-11-04. Implemented so far: the shared device layer, the shared
iOS/Android host-process classes in `Designer.Remote`, the Uno 6.7.30 baseline, an opt-in Skia-based iOS head of the Uno design host, and the
IDE side for iOS and Android: the Uno designer offers the shared device picker and progress card for a project that targets iOS or Android, and
previews on a simulator or an emulator end to end (see section 4a). Also done: Cancel, portrait/landscape and a generic device body (Frame checkbox) for device previews. Not started: project assemblies on a device,
debugging on a device (direction in section 4b), and packaging. The mobile runtime is expected to be CoreCLR from .NET 11; Mono is not a target. Related: `winui-designer.md` (the Uno/WinUI runtimes and the out-of-process decision),
`designer-common.md` ("Machine-wide devices and the Device Manager", DDP), and the MAUI Designer's design
(`MAUI-Designer/opendevelop-addin/docs/design.md`), which this project follows and reuses.

## 1. Goal

Let a developer open a page of an Uno Platform project and preview and edit it **inside the real iOS or Android runtime**, not
only in the desktop Skia host: native controls and fonts, safe areas, density, conditional XAML namespaces (`ios:`, `android:`),
with the same device picker, Device Manager, progress card and preflight the MAUI designer already has.

Non-goals for the first delivery: touch/gesture input forwarding into the device, hot reload into a running app (the existing
Uno hot reload service is separate), and non-Apple/Android targets (WebAssembly, Windows, Linux are covered by the desktop host).

## 2. What exists (verified in the tree)

- **Desktop host.** `WinUIXamlDesigner.UnoHost` is an Uno.Sdk 6.7.30 (baseline raised from 6.5.31 on 2026-11-04) `net10.0-desktop` Skia app. It installs a headless
  dispatcher (`HeadlessDispatcher`), renders an element that belongs to no window with `RenderTargetBitmap`, encodes the frame
  through SkiaSharp, and speaks DDP through `DesignerChildHost.Run(...)`. It preloads the project's assemblies from `--appbin`.
- **Client.** `WinUIXamlDesigner.UnoDesignHost/UnoDesignClient` (`RecoverableDesignerDocumentHostClient`, DDP method mapping)
  on top of `DesignerHostProcessClient` (`Designer.Remote`: spawn, token handshake, log pump, timeouts, shutdown). Children are
  pooled per `CompatibilityKey` (runtime config, deps file, host dll, app bin path, architecture and dotnet host path; `UnoDesignClient.cs`) and shared by documents.
- **IDE side.** `WinUIXamlDesignerViewContent` (2.4k lines) has no preview-target concept: no device picker, no device frame,
  no session overlay.
- **Shared device layer** (done, in OpenDevelop): `Device`/`DeviceService`/providers, `DevicePreferences`, `DevicePickerBar`,
  `DeviceManagerDialog`, `DesignerSession` + `SessionOverlay`, and the canvas's optional device body (`DesignerDeviceChrome.cs` and `DesignSurface.DeviceChrome` in `src/AddIns/DisplayBindings/DesignerCanvas`).
- **What the MAUI add-in still owns** (and Uno must not copy): the simulator/emulator launch connections, the preview-target
  model and preflight, the device-frame builder and the connection orchestration (generation counter, retry, cancel).

## 3. Design

### 3.1 One host source, three heads

The DDP child logic (`DesignHost`, `DesignRpc`, XAML repair/normalisation helpers) is platform-neutral Uno/WinUI code; only the
process entry and the dispatcher differ. Compile it for three targets:

| Head | TFM | Window | Frame source |
|---|---|---|---|
| Desktop (today) | `net10.0-desktop` | none (headless dispatcher) | off-screen `RenderTargetBitmap` |
| iOS | `net10.0-ios` (simulator RID) | a real `Window` created at start, hosting the design root | `RenderTargetBitmap` of the root |
| Android | `net10.0-android` | a real `Window`/`Activity` | `RenderTargetBitmap` of the root |

Mobile heads cannot be headless: Uno on iOS/Android needs an application window. The host creates one and puts the design root
in it; the app is not interactive, only a render target. **Decision: multi-target `WinUIXamlDesigner.UnoHost`** (Uno's single-project
model) with the mobile TFMs **opt-in** (`UnoDesignHostMobile`, on by default only when the iOS/Android workloads are present), so
a machine without those workloads still builds the add-in and previews on desktop. Platform code sits behind `#if`/partial
classes next to `Program.cs`.

### 3.2 Getting a child into a device

A device child cannot be spawned directly: it is installed into the simulator/emulator and told where to call back. This is
already solved twice in the MAUI client (`IosSimulatorConnection`, the Android connection): `simctl bootstatus/install/launch`
with `--port N --token T` plus a watcher script for the app's lifetime; `adb install`, `am start --es port --es token` and
`adb reverse tcp:N tcp:N`, removing the mapping on exit. **These move to `Designer.Remote`** as designer-neutral
`IosSimulatorHostProcess` and `AndroidEmulatorHostProcess` (subclasses of `DesignerHostProcessClient`, parameterised by bundle
path and bundle id / package and activity). MAUI is changed to use them; Uno uses them from day one.

### 3.3 Client and pooling

`UnoDesignClient` already separates the Uno-specific method mapping from the process lifecycle. Give it a connection factory
so a device connection can replace the desktop one. The pool key gains the device id (a child per (device, project) shared by
documents, like today), so two documents on the same simulator do not boot two apps.

### 3.4 IDE side: reuse, don't copy

`WinUIXamlDesignerViewContent` gains the same row and overlay the MAUI view has: `DevicePickerBar` (with "Manage devices..."),
orientation toggle, `SessionOverlay` over the canvas, device chrome. Everything that is not MAUI-specific in the MAUI add-in
moves to OpenDevelop **before** Uno needs it (see phase 0), so the two designers share one implementation:

- the preview-target model (platform + device + orientation, key format, remembered per project) and its policy;
- preflight (tooling problem, device still exists, host bundle present);
- the device-frame builder (`DeviceChromeProvider`);
- connection orchestration: connection generation, retry, cancel, host-exit recovery, session phase reporting, written as a
  small coordinator that takes "start a client" as a delegate, so each designer supplies only its own client.

The Uno project's targets come from its `TargetFrameworks`/`UnoFeatures` (Uno.Sdk single project): iOS and Android previews are
offered only when the project targets them, exactly as MAUI does.

### 3.5 Project assemblies on a device (the hard part)

The desktop host loads the project's compiled assemblies (`--appbin`) so custom controls and user types render. A device child
runs a different runtime (`net10.0-ios`/`-android`) and needs assemblies built for that TFM.

- **Phase 1 delivers framework-only previews**: XAML with Uno/WinUI controls and the host's own resources render. How an element of an
  unknown project type is shown (a diagnostic on that element, or a stand-in) is **not yet defined for the device host**: the
  desktop host's repair and substitution helpers (`XamlDocumentRepair`, `CompiledXamlControlSubstituter`) are the starting
  point, and the phase-1 spike must decide it. The limit is stated in the preflight/status either way.
- **Phase 3 adds project types**: build the project for the device TFM (can be minutes; cached by project version), then make
  the assemblies reachable: the iOS simulator shares the Mac filesystem, so the child loads them from the build output path;
  an Android emulator needs `adb push` into the app's files directory. Loading reuses the desktop host's preload logic.

## 4. Phasing

| Phase | Content | Exit criterion |
|---|---|---|
| 0 | **Shared extraction, MAUI behaviour unchanged**: host processes (3.2), preview-target model + preflight + remembered target, device-frame builder, connection coordinator move to OpenDevelop; MAUI consumes them | MAUI unit tests and its macOS/iOS/Android/Device-Manager journeys unchanged |
| 1 | **Uno iOS**: iOS head spike first (feasibility: a window, a render, DDP over the simulator loopback), then `UnoDeviceConnection`, picker/overlay in the view content, framework-only previews | A journey opens an Uno page on a simulator, gets a frame, edits a property, switches back to desktop |
| 2 | **Uno Android**: Android head, adb path, emulator | Same journey on an emulator |
| 3 | **Project assemblies** on both platforms (3.5) | A page using a project control renders it on a device |
| 4 | Parity: orientation and real device metrics/safe areas, conditional `ios:`/`android:` namespaces, theme, device frame | Matrix below is green |
| 5 | CI lanes, packaging of the mobile heads (release and CI builds must set `UnoDesignHostMobile=true` explicitly, or the packaged add-in has no device child), docs | Release checklist |

Phase 1 starts with the spike because it answers the biggest unknown (does a real-window Uno iOS head render
`RenderTargetBitmap` frames reliably and start quickly enough) before the rest is built on it.

## 4a. Progress and spike results (2026-11-04)

**Phase 0, host processes: partly done.** iOS is complete and verified; Android is pending (the shared Android class exists and is unit-tested, but MAUI still uses its own copy). The rest of phase 0 (preview-target model, preflight, device-frame builder, connection coordinator) is not started. `IosSimulatorHostProcess` and `AndroidEmulatorHostProcess` are in `Designer.Remote`
(subclasses of `DesignerHostProcessClient`; `DesignerHostProcessClient` gained `OnDisposed`, `RaiseOutputLine`, a settable
`Handshake`, and release-once cleanup that also runs when a start fails or the host is terminated). The MAUI iOS client uses the iOS
class (its iOS and macOS journeys pass); the MAUI Android client still has its own copy until the Android journey can be verified
again. Reviews of this code found real defects worth remembering for any future device host: the watcher's `grep` matched its own
command line (use the `[-]-port` bracket form), `ps` output must not be truncated (`-ww`), an EXIT trap does not run when the
script is killed (remove the `adb reverse` bridge in `OnDisposed` too), and device-side cleanup must not depend on `Dispose` alone.

**Phase 1 spike (iOS): the head is feasible.** A scratch Uno 6.5.31 (native iOS renderer) `net10.0-ios` app (single project, `SkiaRenderer`) was built,
installed into an iOS 26.x simulator, launched, created a `Window` and rendered its content with `RenderTargetBitmap` about 1.5 s
after launch: `1206x2622` pixels (a full-screen frame at 3x) from `RenderAsync`/`GetPixelsAsync`. This answers risk 2 below for iOS:
frame capture works in a real window, so a mobile head can use the same `RenderTargetBitmap` path as the desktop host.

What the spike also showed (each cost time, so they are recorded):

- **Workloads.** Building `net10.0-ios` needs the iOS workload for the SDK that builds it. The system `dotnet` (10.0.200 and
  11.0.100-rc.1) had no `ios` workload; installing it into the user-level dotnet (`~/.dotnet`, SDK 10.0.4xx) works. The newest
  workload (27.1) needs Xcode 27.1; with Xcode 27.0 use the 26.5 manifests (a rollback file such as
  `{"microsoft.net.sdk.ios": "26.5.10315/10.0.100"}` with `--from-rollback-file`) and pass `-p:ValidateXcodeVersion=false`, as the MAUI
  host builds do. `SupportedOSPlatformVersion` must be at least 15.0.
- **Launch screen.** Without a launch storyboard the Apple SDK writes `UILaunchStoryboardName=LaunchScreen` into the app and the app
  dies at start ("Could not find a storyboard named 'LaunchScreen'"). An empty `UILaunchScreen` dict alone did not stop that here;
  a minimal `LaunchScreen.storyboard` (as `InterfaceDefinition`) did. (The MAUI iOS host works with just the empty dict; the
  difference is Uno.Sdk's single-project defaults.) An empty launch screen is also what keeps iOS out of legacy 320x480 mode.
- **Noise.** `Uno.UI.Runtime.Skia.AppleUIKit` logs a `TypeLoadException` on registration at start-up; rendering still works.

**Direction change: Skia-only, Uno 6.7 baseline (2026-11-04).** Uno 7 is expected to be Skia-only; the local `Uno.WinUI` 7.0.0-dev packages
have no `net10.0-ios`/`-android` lib flavours (6.5 has them), which supports that. The spike above used the native iOS renderer, so it is not
the shape to ship. The real iOS head is Skia-based from the start, and the Uno baseline is now 6.7.30 (central pin
`Microsoft.Extensions.DependencyInjection.Abstractions` raised to 10.0.12, because Uno 6.7 brings `Microsoft.Extensions.Logging.Console`
10.0.12). What that changes in practice: with `SkiaRenderer`, `Application` is not a `UIApplicationDelegate`, so `UIApplication.Main(typeof(App))`
fails ("No class named App is loaded", found the hard way when a multi-targeted build silently switched flavours); the entry point is
`UnoPlatformHostBuilder.Create().App(() => new App()).UseAppleUIKit().Build().Run()`, and the managed `Main` args are empty, so
`--port`/`--token` come from `NSProcessInfo.ProcessInfo.Arguments`. The same host-builder pattern will be used for Android.

**Phase 1, iOS head: working end to end (commits `9cdd64144a`, `67254aed11`).** `WinUIXamlDesigner.UnoHost` now multi-targets
`net10.0-desktop` (always) and `net10.0-ios` (opt-in, `-p:UnoDesignHostMobile=true`; needs the iOS workload). `DesignHost` talks to a swappable
`IHostDispatcher` (desktop: the unchanged headless pump; mobile: the window's `DispatcherQueue`) and the mobile head parks the design root in
a real window through the existing `HostVisualRoot`/`HostCommitLayout`/`HostAwaitFrame` hooks. Driven by a scratch program with the shared
`IosSimulatorHostProcess`: installed into an iOS simulator, DDP connected in 8.6 s, `session/open` accepted, a 1170x155 frame at dpi 3 rendered,
clean disposal. Desktop regression check: `WinUIXamlDesigner.UnoHost.Tests` 27 passed, 3 skipped on 6.7.30. Also found on the way: a built app's
`Info.plist` is binary, which the shared bundle-id reader could not read (fixed with a `plutil` conversion).
Caveats: the Windows-only `WinUIXamlDesigner.MicrosoftHost` source-links `DesignHost.cs` and now also links `HostDispatcher.cs`; it could not be
built on the macOS machine used here, so it needs a Windows build before release. Packaging has no mobile path yet (the deploy target is
desktop-only). The iOS head has no automated test yet (the driver was a scratch program).

**Phase 1, IDE side for iOS: working (commits `f85aca3be9`, `ef6002db9b`).** `UnoDesignClient.AdoptDeviceHost` wraps a host that
`IosSimulatorHostProcess` already started (not pooled, no automatic crash recovery; `HostExited` tells the view). `ProjectTargets`
(`Designer.Shell`) reads the project's target frameworks, so the picker only appears for a project that targets iOS. In
`UnoDesignRuntimeHost` (new partial `UnoDesignRuntimeHost.Devices.cs`) the canvas is wrapped in `DevicePickerBar` + `SessionOverlay`
(`IWinUIXamlChrome`; the view and DevFlow reach the device state through `IWinUIXamlDevicePreview`). Switching disposes the current client, runs a
preflight (iOS head bundle present, provider available, device exists), boots/installs/launches/connects through the shared host process, and
the session reaches Ready on the first frame; the device is remembered per project; a device that is hidden or deleted while selected falls
back to the computer. The iOS head ships as an app bundle beside the desktop head (`UnoHost.iOS`, copied by a mac-only deploy target). A
connection generation guards overlapping switches (found in review: the remembered-simulator restore raced the constructor's first desktop
connection and could leave the adopted host leaked). Verified by the integration journey
`AddInTests.WinUIDesigner_UnoOnIosSimulator_RendersOnTheDeviceAndSwitchesBack` against the real IDE and a real simulator (device preview with a
390-wide frame, then back to the 1280-wide desktop preview); it needs `-p:BuildIntegrationFixtures=false` when run locally because the fixtures
otherwise build in the same MSBuild process as the new Uno.Sdk. Known limits: framework controls only on the device, the Android entry in the
picker reads "not available yet" (superseded by the Android work below), and the remembered-device restore path has no test. (Cancel and a
portrait/landscape toggle were added afterwards and are covered by the same journeys: the status text must show the rotated width, and a start that is cancelled
must leave a working desktop preview.)

**Phase 2, Android: working.** The same host source builds as `net10.0-android` (opt-in with the other mobile heads). Skia-based Uno on Android
runs as `Microsoft.UI.Xaml.NativeApplication` + `ApplicationActivity`; the IDE starts the activity with `am start --es port N --es token T` (via
`AndroidEmulatorHostProcess`) and the activity turns the extras into the `--port/--token` arguments the other heads get on their command line. Things
that cost time and are worth remembering: `ApplicationActivity` throws "You need to use a Theme.AppCompat theme" without an AppCompat theme
(`Platforms/Android/Resources/values/Styles.xml`); the all-ABI debug APK is about 130 MB and was slow to install, so the head is built for one ABI
(`android-arm64` on an arm64 machine; 74 MB, installs in about 20 s); `EmbedAssembliesIntoApk` is needed for a plain `adb install`; and a small
emulator (1.5 GB AVD) takes tens of seconds to start the Mono app and to render the first frame, which exceeded the host-process defaults (30 s
connect, 30 s request) and made a healthy host look dead, so device hosts now carry their own connect and request timeouts. The IDE side
(`UnoDesignRuntimeHost.Devices.cs`) offers the AVDs of the Android Device Manager provider for a project that targets Android (macOS/Linux; the host
process is a Unix script), starts an emulator that is not running and waits for `sys.boot_completed`, installs the APK deployed beside the desktop
head (`UnoHost.Android`), and uses the same session phases, overlay, remembered device and generation guard as iOS. Verified by the integration
journey `AddInTests.WinUIDesigner_UnoOnAndroidEmulator_RendersOnTheDeviceAndSwitchesBack` (real IDE, real emulator: Ready with a 412-wide frame, then back
to the 1280-wide desktop preview), skipped unless an emulator is running and the APK is deployed. Known limits are the iOS ones (framework controls
only) plus: the preview target for Android defaults to 412x915 rather than the AVD's own screen size.

**Device body.** Device previews draw a generic body around the canvas (`DesignSurface.DeviceChrome`, Frame checkbox in the picker row): a rounded
bezel, a Dynamic-Island cut-out and home indicator for an iPhone, a plain rounded body for Android phones, a squarer, thicker one for tablets. Unlike
MAUI's builder it reads no device database (no Xcode/AVD screen shapes), so the shapes are plausible defaults, not each device's own; landscape keeps the
portrait shapes. The journeys assert only that a body is set on a device preview and not on the computer's (`frame` in `od.winui-designer.device`); there is
no pixel test of how it looks.

**Machine note (2026-11-04).** On the 16 GB development Mac the memory pressure (swap about 7 of 8 GB) came mostly from two booted iOS simulators
(about 3.5 GB across 356 processes), Spotlight's `mds_stores` (about 1.1 GB) and the emulator; inside the 1.5 GB AVD the Google apps of the
`google_apis` image (Wellbeing, Gboard, Messages, quick search) use about 1 GB that a design host does not need. A healthy emulator for the journeys
wants either more AVD RAM or those apps disabled.

**Not yet started:** project assemblies on a device, preview-target/preflight extraction from
MAUI (the Uno code duplicates a little of it), device debugging (see below), and Windows packaging. The macOS bundle script and `dist.ps1` keep the `UnoHost.iOS` and `UnoHost.Android` folders intact (an app bundle
and an APK must not go through the by-basename dedup). Verified on macOS (Debug, 2026-11-04): `DOTNET_ROOT=$HOME/.dotnet UnoDesignHostMobile=true pwsh ./dist.ps1 -Configuration Debug`
produced `AddIns/DisplayBindings/WinUIXamlDesigner/UnoHost.iOS/WinUIXamlDesigner.UnoHost.app` (413 files) and `UnoHost.Android/WinUIXamlDesigner.UnoHost.apk` (73 MB) in the bundle.
The mobile heads only exist when the env var / `-p:UnoDesignHostMobile=true` is set, so a release must set it explicitly. The SDK root must carry the ios/android
workloads; `dist.ps1` now honours `DOTNET_ROOT` (it otherwise prefers Homebrew's dotnet), and the heads set `OpenDevelopAllowRidSpecificAddin` because they are RID-specific apps, not AnyCPU add-ins. Release configuration and Windows are not exercised.

## 4b. Debugging on a device (direction, 2026-11-04; nothing implemented)

Asked for alongside the Android work: run and debug the user's Uno app on an iOS Simulator or an Android emulator from the IDE. Direction given
by the project owner: **.NET 11 moves the mobile platforms to CoreCLR, and the Mono runtime is no longer a target.** So the earlier idea of a second
debugger backend over Mono's soft debugger (SDB, `Mono.Debugging.Soft`) is dropped; device debugging is the existing CoreCLR route (`Debugger.AddIn`
speaking DAP to SharpDbg over `ICorDebug`, `doc/technotes/debugging.md`) pointed at a process that lives in a simulator or emulator. This section only
records what that needs; nothing is built. What was observed on this machine is marked "observed"; the rest is general knowledge and **unverified**.

**Observed.** The .NET 10 builds produced by this tree run on Mono (the Android APK has `libmonosgen-2.0.so`, the iOS bundle
`libmono-component-debugger.dylib`), so they cannot be used to prototype CoreCLR device debugging: that needs the .NET 11 workloads and a Uno
build for .NET 11 (see "Testing on .NET 11" below).

**What a device debug session needs (each is a work item):**

1. *Build and deploy the user's app for the device TFM* with the .NET 11 workloads; the IDE's build system has no mobile path today (the same cost
   as the "project assemblies on a device" item).
2. *Launch it so the CoreCLR debugger can reach it* (unverified): the runtime's diagnostics IPC (a diagnostic port) has to be reachable from the Mac,
   through `adb forward` for Android and loopback for the simulator, and the process started suspended until the debugger attaches. The shared
   `IosSimulatorHostProcess`/`AndroidEmulatorHostProcess` already own install, launch and lifetime, so a debug launch is an extension of them.
3. *The attach path in `Debugger.AddIn`/SharpDbg:* today it attaches to or launches local processes through dbgshim; attaching to a runtime on a
   device is an open question (whether `dbgshim`/`ICorDebug` can be driven over the diagnostic channel the way remote CoreCLR debugging is, and what
   SharpDbg would need). This is the main unknown and decides the cost.
4. *IDE hook-up:* a Run/Debug target picker for "this simulator / this emulator" (the shared device picker and Device Manager already list them),
   symbols (portable PDBs next to the build output), stop/restart lifecycle, DevFlow test hooks.

**Order.** (a) Get a .NET 11 Uno build running on a device (the blocker below). (b) A spike outside the IDE: launch that app suspended on the
Android emulator and attach a CoreCLR debugger to it. (c) Only then the SharpDbg/`Debugger.AddIn` work and the picker. Hot reload
(`UnoHotReloadService`) is a separate existing feature.

### iOS debugging spike on .NET 11 (2026-11-04)

Asked for as the first debugging target while Android on .NET 11 is blocked. The iOS Simulator is the better first target: its apps are ordinary macOS
processes, so no `adb forward`-style tunnel is needed. A scratch Uno 7 app on `net11.0-ios` (iOS workload `27.0.12212-net11-rc.2`, Xcode 27.0, entry via
`UnoPlatformHostBuilder.UseAppleUIKit()`, no code changes beyond the Uno 7 host-builder entry the iOS heads here already use) builds and runs in the
simulator. Results, all observed unless marked:

- **The runtime is CoreCLR.** `RuntimeInformation.FrameworkDescription` reports `.NET 11.0.0-rc.3.26522.148`, there is no `Mono.Runtime` type, and the
  bundle contains `libcoreclr.dylib` and `libxamarin-dotnet-coreclr-debug.dylib`. Cold start to the first managed code took about 15 s on this loaded machine.
- **The app is a plain Mac process** (its command line is under `CoreSimulator/Devices/<udid>/data/Containers/Bundle/Application`), and its CoreCLR debugger
  transport pipes (`clr-debug-pipe-<pid>-<id>-in/out`) are created in the **app container's `tmp`**, not the Mac's `$TMPDIR`. SharpDbg finds them only if the adapter is
  started with `TMPDIR=<container>/tmp` (`xcrun simctl get_app_container <udid> <bundle id> data`).
- **Attach by pid is blocked by the debug components, not by the protocol.** With that `TMPDIR`, `attach` succeeds at the DAP level and then SharpDbg's
  `RegisterForRuntimeStartup` fails with `0x80131C3C` ("A debug component is not installed"): the app bundle has no `libmscordbi`/`libmscordaccore`. The runtime pack
  `microsoft.netcore.app.runtime.iossimulator-arm64` has them, but they are **iOS-simulator-platform Mach-O** binaries (observed with `vtool`), which a macOS debugger
  process cannot `dlopen`, so putting them in the bundle does not help. Putting the **macOS** `libmscordbi/libmscordaccore` of the same runtime version
  (`~/.dotnet/shared/Microsoft.NETCore.App/11.0.0-rc.3.../`) into the bundle makes the app fail to start.
- **The remote-port path exists but did not produce a debugger.** The iOS CoreCLR debug library reads `MonoTouchDebugConfiguration.txt` in the bundle (`IP: 127.0.0.1`,
  `Port: 10000`; observed) and connects out to the "IDE" at start (observed: `Socket error while connecting to IDE on 127.0.0.1:10000`), so the debugger is the
  listener. SharpDbg's DAP `attach` accepts `coreClrMobileDebuggerOptions` (`address`, `port`, `platform`, `isServer`, `mscordbiPath`, `assembliesPath`; this is its
  mobile path, `ClrDebugExtensions.Mobile`, `RegisterForRuntimeStartupRemotePort`). With a host-platform (macOS) `mscordbi` it returned success but a **null `ICorDebug`**
  for every platform string tried (`ios`, `iossimulator`, `osx`) and with the iOS-simulator `mscordbi` it failed with `0x80131C3C` as above. SharpDbg bundles
  `Microsoft.Diagnostics.DbgShim 9.0.661903`; whether a dbgshim matching the .NET 11 runtime, different `isServer`/platform semantics, or an app-side setting is
  needed is **unknown** and is the next thing to find out (the dbgshim remote-port code is the place to read).
- **Not reached:** a breakpoint hit, stepping, variables. The harness used (a DAP script driving `AddIns/Debugger/SharpDbg.Cli.dll`, with a probe app whose
  `Tick()` runs once a second as the breakpoint target) is a scratch artefact and is not in the tree.

So iOS device debugging is **plausible but not demonstrated**: every layer up to "debugger attached over the remote port" exists in SharpDbg and the iOS runtime,
and the missing piece is getting a usable `ICorDebug` from `RegisterForRuntimeStartupRemotePort` against a .NET 11 simulator runtime. Estimate after the next step.

### Testing on .NET 11 (2026-11-04)

The project owner asked to start testing on .NET 11. Steps taken and where it stops (all on the machine used for this work, user-level dotnet
`~/.dotnet`, SDK `11.0.100-rc.3.26522.148`):

- The Android workload for that SDK installed (`37.2.0-rc.2.84`); the .NET 11 Android build needs API level 37, installed through the build's own
  `InstallAndroidDependencies` target with the SDK license accepted by the user.
- A scratch Uno project on `Uno.Sdk/7.0.0-dev.81` (`Uno.WinUI 7.0.0-dev.2347`) targeting `net11.0-android` restores and reaches the C# compiler.
  Two things differ from Uno 6.7 and the heads in this tree need updating when the baseline moves: the Android entry types moved to
  `Uno.UI.Runtime.Android` (`NativeApplication` now has an abstract `CreateHost()` that builds the host with
  `UnoPlatformHostBuilder.Create().App(...).UseAndroid().Build()`; `ApplicationActivity` is in the same namespace), as iOS already uses the host builder.
- **It does not build yet:** `Uno.UI.Runtime.Android` from that dev package was compiled against `Java.Interop 99.61`, newer than the one in the newest
  published Android workload for this band (`Java.Interop 11.0`, error CS1705). No newer workload manifest is published on nuget.org (only
  `37.2.0-rc.2.84`, 2026-10-13). An older Uno 7 build does **not** help: `Uno.Sdk 7.0.0-dev.75` (published the same day, `Uno.WinUI 7.0.0-dev.2282`) fails
  the same way, so Uno 7's Android runtime is built against dotnet/android main (`99.x`), and building it needs an Android workload from that nightly feed
  (its source was not tried: it is not published where this machine looks).
- iOS on .NET 11 works (see the spike above): `net11.0-ios` + Uno 7 dev builds and runs on CoreCLR in a simulator. Uno 6.7 does **not** support .NET 11: its
  `Uno.WinUI` package (and 6.8 dev) carries only `net9.0`/`net10.0` and their platform TFMs; `net11.0` first appears in the Uno 7 dev packages. Moving the
  heads in this tree to .NET 11 therefore means moving to Uno 7 and its host-builder entry on every platform.

## 4c. iOS simulator debugging on .NET 11 CoreCLR (spike, 2026-11-04: breakpoint hit)

Scope decision: debugging targets **.NET 11 only**, where iOS runs on CoreCLR, so the existing SharpDbg/`ICorDebug` path applies and the
soft-debugger backend in 4b is not needed. Everything below was observed on this machine (Xcode 27.0, `~/.dotnet` SDK
`11.0.100-rc.3.26522.148`, iOS SDK pack `net11.0_27.0`), with a plain `dotnet new ios` app (not yet Uno) attached in a running state.

- A `net11.0-ios` simulator build (`-r iossimulator-arm64`; the spike also passed `-p:ValidateXcodeVersion=false`, which turned out unnecessary, see below) ships `libcoreclr.dylib`, `libmscordbi.dylib` and
  `libmscordaccore.dylib` in the bundle, no Mono. No provisioning profile is needed for the simulator; a *non-empty* `Entitlements.plist`
  (for example adding `get-task-allow`) makes the build demand one, and is not needed (below).
- The simulator runtime has only the pipe transport (`DbgTransportPipeChannel`). It creates `clr-debug-pipe-<pid>-*` in the **app container's**
  `tmp/` (`simctl get_app_container <udid> <bundle> data`), not the IDE's `$TMPDIR`. Starting the adapter with `TMPDIR=<container>/tmp/` is enough
  for dbgshim/mscordbi to find it.
- dbgshim loads mscordbi from the target runtime's directory, which holds iOS-simulator-platform dylibs a macOS process cannot load
  (`CORDBG_E_DEBUG_COMPONENT_MISSING`). SharpDbg's attach now takes `debuggerLibraryDirectory` and registers through
  `RegisterForRuntimeStartup3` with an `ICLRDebuggingLibraryProvider3` that serves mscordbi/DAC from that directory.
- The host's macOS mscordbi/DAC of the **same commit** load but fail in `DebugActiveProcess` with `CORDBG_E_READVIRTUAL_FAILURE`
  (the iOS runtime build's layout differs). What works: the bundle's own simulator `libmscordbi`/`libmscordaccore`, retargeted with
  `vtool -set-build-version macos 15.0 15.0 -replace`, their `$ORIGIN` rpath changed to `@loader_path` (`install_name_tool -rpath`), and
  re-signed ad hoc.
- Memory access is not a blocker: the `dotnet` host carries `com.apple.security.cs.debugger`, and `task_for_pid` on the simulator app succeeds
  from it without `get-task-allow` on the app (a plain process without that entitlement fails for any target, so it is no control).
- Result: attach -> breakpoint bound -> `stopped` (reason `breakpoint`) with the right frame `Spike.AppDelegate.Tick()` and source line.
- **Start-up breakpoints.** The diagnostic-port route does not work here: `SIMCTL_CHILD_DOTNET_DefaultDiagnosticPortSuspend=1` neither suspends the
  app nor creates a diagnostic socket, and the Xamarin native launcher's own IDE handshake (`MonoTouchDebugConfiguration.txt`,
  `127.0.0.1:10000`) logs "debugger loading not implemented for CoreCLR". What works is `simctl launch --wait-for-debugger`: the process starts
  stopped (`ps` state `T`) before the runtime loads, SharpDbg registers for runtime startup, then sends `SIGCONT` (new attach option
  `continueStoppedProcess`). A breakpoint on `return true;` in `FinishedLaunching` was hit this way.

Attach arguments used: `{"processId": <pid>, "debuggerLibraryDirectory": "<retargeted DBI dir>", "continueStoppedProcess": true}`, adapter
started with `TMPDIR=<container>/tmp/`.
SharpDbg (`externals/sharpdbg`, commit `53930b0`) rejects `continueStoppedProcess` off macOS (signal numbers differ) and
`debuggerLibraryDirectory` on Windows (the provider serves Unix libraries only); a restart replays the library directory but not the
continue. Its `tests/SharpDbg.Cli.Tests/AttachOptionsTests` cover both options; there the library test names the runtime's own directory,
because the adapter runs inside the test host and a second mscordbi copy from another directory deadlocks the tests that follow.

- **A Uno app.** `dotnet new unoapp -platforms ios -presentation none -tfm net10.0` (Uno.Sdk 6.7.30), retargeted to `net11.0-ios`, builds for
  the simulator with three changes: `SupportedOSPlatformVersion` 15.0 (the net11 iOS SDK's minimum; the template says 14.2),
  `UnoDisableHotDesign=true` (otherwise the Debug-only `Uno.UI.HotDesign` brings `uno.ai.xamlgeneration`'s `Anthropic.SDK.dll` into the app and
  the iOS linker fails in `CoreTypeMap` with MT2390), and the template's `#if DEBUG MainWindow.UseStudio();` removed (it needs Hot Design). The
  bundle is CoreCLR, and its `libmscordbi`/`libmscordaccore` are byte-identical to the plain iOS app's, so one retargeted DBI directory serves
  every app on that runtime version. The same suspended launch hit a breakpoint in `UnoSpike.MainPage..ctor()`.

**In the IDE (2026-11-04).** F5 on a project whose *active* target framework is `net11.0-ios` or later (the project's TFM picker,
`ProjectTargetFrameworkService`) debugs it in the iOS Simulator; verified with the real F5 command (`od.workbench.invoke-shortcut f5`, IDE
build-before-run included) on UnoSpike: breakpoint in `MainPage..ctor()` hit 67 s after F5, call stack resolved, Stop terminates the app.
- `Debugger.AddIn/Service/Ios/IosSimulatorProjectBehavior` (registered under `/SharpDevelop/Workbench/ProjectBehaviors`, condition
  `IosSimulatorProject`: macOS and a declared net11+ iOS TFM) handles `Start` when the active TFM is iOS and defers to the normal behavior
  otherwise. It builds with the project's SDK CLI (`dotnet build -f <tfm> -r iossimulator-arm64 -c <config>`; the in-process build does no
  device RID), picks a simulator through `DeviceService` (project preference `IosSimulatorUdid`, else a booted iPhone, else the newest
  iPhone; `DeviceService` ids are `ios:<udid>`, simctl takes the bare UDID), boots and installs, and launches stopped. Ctrl+F5 launches
  normally (the `od.debug.run-without-debugging` action bypasses project behaviors and does not cover this).
- `IosSimulatorDebugSupport` caches the retargeted DBI/DAC under `~/Library/Caches/OpenDevelop/ios-simulator-dbi/<hash>` (hash of the two
  dylibs), reads the bundle id with `plutil`, and reads the container `tmp/`.
- `WindowsDebugger.StartIosSimulatorDebugging` -> `AttachAsync` with the SharpDbg attach options above; `DapSession.StartAsync` gained
  `attachOptions` and `adapterEnvironment` (the adapter's `TMPDIR`).
- **SDK choice.** The IDE's build picked `/usr/local/share/dotnet` (SDK 11 rc.1, no iOS workload) over the project's `global.json` pin
  (rc.3, only in `~/.dotnet`) and failed with NETSDK1147. `DotNetSdkService.ResolveSdkForProject` now applies the nearest `global.json`'s
  `sdk.version` when the framework-based choice lacks it (`ApplyGlobalJsonPin`, reason "the project's global.json pins this SDK version");
  every other choice is unchanged.
- No `ValidateXcodeVersion=false` is needed: the net11 iOS SDK (27.0) accepts Xcode 27.
- One build per F5: the IDE's build-before-run already writes the `iossimulator-arm64` bundle (the iOS SDK's default RID on Apple
  silicon), so the behavior records each project's last successful IDE build (`IBuildService.BuildStarted/BuildFinished`) and skips its CLI
  build when that build finished within two minutes and the bundle's executable is newer than its start (`IsFreshBuild`). Measured on
  UnoSpike: F5 to breakpoint 31 s instead of 52-108 s. `od.debug.start` (no IDE build first) still builds.
- Debug > **iOS Simulator** (shown when the startup project declares net11+ iOS) lists "Automatic" and every simulator from `DeviceService`,
  checking the stored one; a pick writes the `IosSimulatorUdid` project preference. DevFlow: `od.ios-simulator.choices`,
  `od.ios-simulator.select <udid|"">` (same code as the menu). Verified: selecting the booted iPad made F5 run the app on it.
- Ctrl+F5 verified (`od.workbench.invoke-shortcut ctrl+f5`): builds, installs and starts the app normally, no debug session.
- A failed attach is reported: SharpDbg sends an output event with the reason and `terminated`, so the IDE stops the session instead of
  showing one that never attaches (verified with a corrupted cached `libmscordbi.dylib`). The app then keeps running without a debugger.
  A corrupted cache entry is not detected or rebuilt; delete `~/Library/Caches/OpenDevelop/ios-simulator-dbi` to recover.
- Integration test `IosSimulatorDebuggingIntegrationTests.IosApp_DebugsInTheSimulator_AndStopsAtABreakpointInFinishedLaunching`: `dotnet new
  ios` from a discovered SDK 11 root that has the iOS workload, breakpoint in `FinishedLaunching`, `od.debug.start`; skips without macOS,
  such a root or an iPhone simulator (so it skips on CI). 134 s here.

Next steps: the device path (TCP
`RegisterForRuntimeStartupRemotePort`, already in SharpDbg, and a local Xcode Personal Team profile for signing).

### 4d. Android CoreCLR debugging: NativeAOT target-side DAP spike

**Status: integrated locally; release packaging and cross-platform verification remain.** On a macOS ARM64 host, an Android API 35 ARM64 emulator,
and .NET SDK `11.0.100-rc.3.26522.148` with Android workload `37.2.0-rc.2.84`, a .NET 11 Android app was debugged with a C# NativeAOT
shared library loaded as a CoreCLR profiler. The library hosted the existing SharpDbg engine and DAP adapter in its own NativeAOT
runtime, while the app continued to use CoreCLR. The adapter listened on device loopback and a DAP test client connected through
`adb forward`.

The end-to-end test submitted a source breakpoint before attaching, observed it become verified, hit it, received the correct
source stack, stepped from line 54 to 55, read the local `testValue = 123`, and received a successful `continue` response. The app
subsequently hit the breakpoint again, showing that execution resumed. This demonstrates a C# agent without a separate custom
debugger RPC. The initial native agent experiment also created `ICorDebug`, attached, handled callbacks, and answered TCP requests
while the target CoreCLR was stopped.

The OpenDevelop integration journey now selects the connected adb serial, builds a clean Android project with a temporary targets
import, deploys the agent and matching modules, and verifies F5, a source breakpoint, step, watch expression and Stop. A separate
device test also passed an exception-filter configuration and a conditional breakpoint (`testValue == 123`). ARM64 is the only
device ABI exercised end to end so far.

A further experiment held the profiler's startup callback until the DAP client had connected and the debugger had attached. With
the client connecting promptly, a breakpoint on the first statement of `MainActivity.OnCreate` was hit before that statement ran.
When the client connected too late, Android terminated the app for failing to complete startup (ANR), before the 25-second spike
timeout expired. Startup coordination therefore needs a bounded, prompt connection and an explicit failure path; indefinitely
blocking the Android main thread is not viable.

In a controlled follow-up, `adb shell am set-debug-app <package>` before launch allowed the profiler to wait 17 seconds for a DAP
client and still hit the startup breakpoint. Without that setting, the same 17-second delay produced an Android startup ANR after
about 15 seconds. Once attached, a 20-second stop at `OnCreate` resumed both with and without that setting. These are single-device
experiments; the launch workflow must restore any prior debug-app setting, bound its wait, and test cancellation and other devices.

This was not an IDE journey or a release-ready implementation:

- The first spike used a hand-written profiler COM vtable that did not validate interface IDs or every method signature. A later
  Android ARM64 test replaced it with the Silhouette 4.2.0 NuGet package and again passed breakpoint, step, local, watch, and
  continue. Its NuGet metadata and that of its NativeObjects 1.4.0 dependency declare MIT licenses. NativeAOT reported that
  Silhouette's `IClassFactory.LockServer` export uses a non-blittable `bool` and will throw on .NET 11. An experimental class factory
  using the correct `int` ABI and Silhouette's callback implementation passed the same test; it still needs a complete COM/lifetime
  review and tests. The Android NativeAOT build is experimental and other ABIs and build hosts are unverified.
- The existing SharpDbg DAP library initially failed in the NativeAOT dynamic binder. Rooting `Microsoft.CSharp`, Newtonsoft.Json,
  the DAP assembly and SharpDbg assemblies, and setting `IlcGenerateCompleteTypeMetadata=true`, made the tested DAP messages work.
  AOT and trimming warnings remain. A watch expression `testValue + 1` initially failed because `System.Private.CoreLib` metadata
  was unavailable and its reported name lacked the `.dll` suffix expected by SharpDbg. Supplying the runtime DLL and adjusting the
  two name checks initialized the evaluator. A subsequent error exposed missing NativeAOT metadata for a DAP enum array; rooting that
  array restored error serialization. Roslyn then tried to read `typeof(object).Assembly.Location`, which is empty in NativeAOT.
  Replacing it experimentally with the runtime DLL path got further, but its emit operation aborted the app in the NativeAOT OpenSSL
  initializer: the experimental `linux-bionic-arm64` build links the Linux OpenSSL shim, unavailable in this Android app. Building
  the same agent as `net11.0-android` for `android-arm64` selected Android's crypto implementation and avoided the crash. With the
  matching current app DLL/PDB deployed, the DAP watch expression returned `124`, and execution continued. This used a temporary
  source modification and manual deployment; other expressions and dynamic/reflection-dependent features remain unvalidated. The
  original Linux-Bionic shared library was approximately 48 MiB for one ABI.
- The first attach used a timer and reflection into a private SharpDbg method. A later temporary SharpDbg modification removed both:
  `attach`, `setBreakpoints`, and `configurationDone` drove an in-process attach, and both the `OnCreate` startup breakpoint and
  the step/watch/continue test passed. The startup gate also released after a DAP reader failure. A production adapter still needs
  a reviewed public API, bounded startup, and verified disconnect/cancellation cleanup.
- Android loaded the app assembly from memory. The experiment manually pushed the matching DLL/PDB into the app's private files
  directory and modified a temporary SharpDbg clone to read that copy by simple name. Product code must validate assembly identity
  and PDB matching, handle duplicate names and framework assemblies, and deploy/clean up its symbol files automatically.
- OpenDevelop has a TCP DAP session and Android build/install/device selection, profiler injection, forwarding and teardown. Agent
  production and release packaging still need to be wired into the build pipelines and verified across host platforms and ABIs.

## 5. Risks and open questions

1. **Workloads and build weight.** Mobile TFMs need the `ios`/`android` workloads and Xcode/Android SDK. Opt-in TFMs keep the add-in
   buildable without them; CI needs a macOS lane with both. Xcode 27 currently trails the installed iOS workload (see the
   workspace `AGENTS.md`): the MAUI iOS host builds with `-p:ValidateXcodeVersion=false`; Uno will need the same.
2. **Frame capture on mobile.** Resolved for iOS and Android: the Skia-based heads render `RenderTargetBitmap` frames in a simulator and an
   emulator (section 4a; Skia is the only renderer Uno 7 is expected to offer on mobile).
3. **Startup time.** A cold simulator or emulator takes tens of seconds to over a minute; the progress card and Cancel already
   cover the UX. The Android test AVD has been seen to starve (2 GB), so Android journeys need a healthy emulator.
4. **Conditional XAML.** Uno XAML uses `ios:`/`android:` prefixes resolved at build time; the desktop host strips them
   (`ConditionalXmlnsStripper`). A device host should evaluate them for its own platform instead; this is a phase-4 item and
   needs a design of its own.
5. **Platform limits.** iOS previews need macOS; the provider already reports that as "unavailable" instead of hiding the platform.
6. **Cross-repository migration.** The MAUI add-in builds against an OpenDevelop checkout (`OPENDEVELOP_ROOT`), so each phase-0
   extraction lands in OpenDevelop first and MAUI changes only after it; a MAUI commit that needs an OpenDevelop type must say which
   commit it requires (as the device-layer commits already do), and the add-ins' `LibreWPF.Sdk` pins must stay in step.
7. **Licensing and packaging.** The mobile heads ship inside the add-in; check Uno's redistribution terms before packaging.

## 6. Acceptance matrix (target for phase 4)

| Capability | Desktop host | iOS | Android |
|---|---|---|---|
| Open, render, edit property, undo | done | phase 1 | phase 2 |
| Device picker, Device Manager, progress card, Cancel/Retry | n/a | phase 1 | phase 2 |
| Project custom controls | done | phase 3 | phase 3 |
| Device metrics, orientation, frame | n/a | phase 4 | phase 4 |
| Conditional namespaces per platform | stripped | phase 4 | phase 4 |
| Theme (Light/Dark) | done | phase 4 | phase 4 |

## 7. Decisions taken (change them here if they are wrong)

- Multi-target the existing `UnoHost` project with opt-in mobile TFMs, rather than separate head projects.
- Phase 1 is framework-only; project assemblies come in phase 3.
- Extract the shared pieces from MAUI to OpenDevelop first (phase 0), then build Uno on them; MAUI is the first consumer and
  must stay green throughout.
