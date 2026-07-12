# Multi-targeting support

OpenDevelop treats a multi-targeted SDK project as one project with several MSBuild inner-build
slices. The project file remains the source of truth: `TargetFrameworks`, imported properties,
conditions, default SDK items, `Remove`, `Update`, `Exclude`, and `Link` metadata are evaluated by
MSBuild rather than reproduced by directory scanning.

## Active target framework

Each loaded project has an active target framework. `ProjectTargetFrameworkService` reads the
declared frameworks, selects the first framework by default, and stores an explicit user selection
in the project's per-user preferences. The selection is not written into the project file.

The active framework controls IDE views that require one unambiguous compilation context:

- The Projects pad obtains evaluated items from the selected inner build. This prevents files with
  TFM-specific `Compile`, `None`, or resource conditions from appearing in the wrong context.
- The C# document navigation bar displays `TFM | Type | Member` for multi-targeted projects. The TFM
  selector is hidden for zero- or single-target projects.
- Roslyn document lists, references, preprocessor symbols, language version, and nullable settings
  are evaluated from the selected TFM slice.
- `OutputAssemblyFullPath`, Run, and Debug resolve the selected TFM's output directory.

Changing the selection raises `ProjectTargetFrameworkService.ActiveTargetFrameworkChanged`.
Projects pad refreshes its tree and the Roslyn workspace marks that project dirty before the next
semantic operation.

## Build behavior

Build and Rebuild do not use the active TFM. They remain MSBuild outer builds and therefore build
every target in `TargetFrameworks`. The active TFM is an IDE browsing and launch selection, not a
way to silently narrow a normal project build.

Run and Debug are different: after a successful all-target build, they must choose one executable
or managed assembly. They resolve that artifact using the project's active TFM.

## Unit tests

Test discovery intentionally ignores the active project TFM and processes every TFM declared by a
test project. `MtpTestProject` resolves each inner build's managed output, starts a separate
Microsoft.Testing.Platform server for it, and retains the discovered nodes with their TFM.

The Unit Tests tree is:

```
Project
  TargetFramework
    Namespace
      Class
        Test
```

Selected tests are grouped by TFM before execution. Each group runs against its own output assembly
and MTP host. Result matching uses both TFM and display name, so the same fully-qualified test in
`net8.0` and `net10.0` represents two independent executions and cannot update the other slice's
tree node. Debugging tests is restricted to one TFM per debugger launch.

This follows the multi-TFM experiment in UnoDevelop's `TestService` and `DotNetTestRunner`, adapted
to OpenDevelop's existing `MtpTestProject`, `TestCollection`, and MTP server model.

## Main implementation points

- `src/Main/Base/Project/Src/Project/ProjectTargetFrameworkService.cs`
- `src/Main/Base/Project/Src/Project/MSBuildBasedProject.cs`
- `src/Main/Base/Project/Roslyn/RoslynWorkspaceHelper.cs`
- `src/Main/SharpDevelop/Services/ProjectDisplayItems.cs`
- `src/AddIns/DisplayBindings/AvalonEdit.AddIn/Src/QuickClassBrowser.*`
- `src/AddIns/Analysis/UnitTesting/MtpTestProject.cs`
- `src/AddIns/Analysis/UnitTesting/Mtp/MtpTargetFramework.cs`
- `src/AddIns/Analysis/UnitTesting/MtpTestRunner.cs`
- `src/AddIns/Analysis/UnitTesting/MtpTestDebugger.cs`

Project Browser renders linked-file and source-control adornments independently. The linked-file
shortcut occupies the lower-right corner; Git/SVN status occupies the lower-left corner. This is
important for linked files whose physical path belongs to a Git submodule: both states remain
visible on the same file icon.

## Verification

Obfuscar is the current real-world fixture. Its multi-targeted `Obfuscar.csproj` has no `Compile`
items in the outer build, while an inner build such as `TargetFramework=net10.0` returns the SDK
default source items. `Baml.csproj` additionally verifies that explicit external `Compile` items
retain their `Link` metadata.

The migrated AvalonEdit addin, UnitTesting addin, Base project, and OpenDevelop executable must all
build successfully. The legacy `UnitTesting.Tests` project still targets .NET Framework 4.5 and is
not yet a runnable regression suite in the current LibreWPF/net10 build; a modern test project is
still needed for isolated automated coverage of the TFM tree and duplicate-name result routing.
