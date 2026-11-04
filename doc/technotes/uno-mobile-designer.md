# Uno designer: iOS Simulator and Android Emulator previews

Status: project charter and design (2026-11-03). Nothing in this document is implemented yet except the shared device layer it
builds on. Related: `winui-designer.md` (the Uno/WinUI runtimes and the out-of-process decision),
`designer-common.md` ("Machine-wide devices and the Device Manager", DDP), and the MAUI Designer's design
(`MAUI-Designer/opendevelop-addin/docs/design.md`), which this project follows and reuses.

## 1. Goal

Let a developer open a page of an Uno Platform project and preview and edit it **inside the real iOS or Android runtime**, not
only in the desktop Skia host: native controls and fonts, safe areas, density, conditional XAML namespaces (`ios:`, `android:`),
with the same device picker, Device Manager, progress card and preflight the MAUI designer already has.

Non-goals for the first delivery: touch/gesture input forwarding into the device, hot reload into a running app (the existing
Uno hot reload service is separate), and non-Apple/Android targets (WebAssembly, Windows, Linux are covered by the desktop host).

## 2. What exists (verified in the tree)

- **Desktop host.** `WinUIXamlDesigner.UnoHost` is an Uno.Sdk 6.5.31 `net10.0-desktop` Skia app. It installs a headless
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

## 5. Risks and open questions

1. **Workloads and build weight.** Mobile TFMs need the `ios`/`android` workloads and Xcode/Android SDK. Opt-in TFMs keep the add-in
   buildable without them; CI needs a macOS lane with both. Xcode 27 currently trails the installed iOS workload (see the
   workspace `AGENTS.md`): the MAUI iOS host builds with `-p:ValidateXcodeVersion=false`; Uno will need the same.
2. **Frame capture on mobile.** `RenderTargetBitmap` behaviour on Uno iOS/Android (renderer choice: native vs Skia, pixel scale,
   timing of the first frame) is unverified; the phase-1 spike decides, and may force the Skia renderer for the mobile heads.
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
