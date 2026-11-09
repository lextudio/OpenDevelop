# Licensing audit

This audit is scoped to material that is actually compiled, embedded, copied
to a release payload, or used only by tests. A submodule's top-level license
does not apply automatically to every OpenDevelop release.

## Source-linked material

| Consumer | Exact upstream material | License evidence | Release treatment |
| --- | --- | --- | --- |
| `AspNetCore.csproj` | `src/AddIns/BackendBindings/AspNetCore/LaunchProfiles/ILaunchProfile.cs`, `LaunchProfile.cs` (verbatim copies from `mono/monodevelop` `MonoDevelop.AspNetCore`; the monodevelop submodule has been removed) | Both files state `Copyright (c) Microsoft` and `Apache-2.0`. MonoDevelop's top-level `main/COPYING` is LGPL-2.1, but it is not the license evidence for these two copied files. | `SOURCE-LICENSES/MonoDevelop.AspNetCore-Apache-2.0.txt`. |
| `CodeCoverage.csproj` | Eight explicit files under `externals/vsmac-codecoverage/VSMac-CodeCoverage/CodeCoverage.Core` | `externals/vsmac-codecoverage/LICENSE`, MIT, Copyright Arthur Demanuele. | `SOURCE-LICENSES/VSMac-CodeCoverage-MIT.txt`. |
| Microsoft WPF Designer hosts | The `Project/**` source trees of `WpfDesign`, `WpfDesign.XamlDom`, and `WpfDesign.Designer`; the latter also links XAML and images. | `externals/vscode-wpf/external/WpfDesigner/LICENSE`, MIT, Copyright ICSharpCode. | `SOURCE-LICENSES/WpfDesigner-MIT.txt`. |
| `src/Libraries/VSEditorCore` (`Microsoft.VisualStudio.CoreUtility`, `.Text.Data`, `.Text.Logic`, `.Text.UI`) | The `src/Editor/Core/Def`, `src/Editor/Text/Def/TextData`, `TextLogic`, and `TextUI` source trees of `externals/vs-editor-api`, plus one local 17.14-compatibility file (`Microsoft.VisualStudio.Text.Data/TextDocumentFileActionEventArgs.cs`). | `externals/vs-editor-api/LICENSE`, MIT, Copyright Microsoft Corporation. These four projects replace the NuGet packages of the same names, whose LICENSE is "MICROSOFT SOFTWARE LICENSE TERMS - MICROSOFT VISUAL STUDIO ADD-ONs and EXTENSIONS": use is limited to Visual Studio, and it forbids combining/providing the assemblies inside another application for others to use. | `SOURCE-LICENSES/vs-editor-api-MIT.txt`. |

`src/Libraries/VSEditorCore` is a build-only replacement: the assemblies keep their
`Microsoft.VisualStudio.*` names (the compatibility layer's public types are those contracts) but
are unsigned and versioned `16.0.0.0`. Only `OpenDevelop.VSEditorCompat` consumes them, and it is
rebuilt against these copies in the same build, so no binary expects Microsoft's public key token.

The submodule commits are the Gitlinks recorded by the OpenDevelop commit.
The audit must be repeated when a project adds or changes a `Compile`, `Page`,
`Resource`, project reference, or payload-copy path under `externals/`.

## Not part of the released product without a separate traced use

`cps`, `wpftoolkit`, and
`xamlstudio` have repository-level licenses but no direct
source-link in the application projects found by this audit. They may be build
or test dependencies. A future payload copy, source link, or bundled binary
must add a file-level entry above before release.

`SharpDbg.Cli` is not source-linked: `Debugger.AddIn.csproj` restores it as the
`LeXtudio.SharpDbg.Cli` package and copies its complete out-of-process adapter
closure into the application payload. Its MIT license is copied as
`THIRD-PARTY-NOTICES/SharpDbg-LICENSE.txt` from the restored package.

The Android debug agents are the three NativeAOT binaries in the
`LeXtudio.SharpDbg.AndroidAgent` package, built by the SharpDbg repository's
`publish-opendevelop-packages.yml` workflow from `src/SharpDbg.AndroidAgent`
and the SharpDbg infrastructure project. Its profiler callbacks
use the NuGet packages `Silhouette` 4.2.0 and `NativeObjects` 1.4.0; both packages'
nuspec files declare MIT. The agent uses `ICorDebugSharp` and the target .NET runtime's
debugging library; it does not include decompiled `ICorDebugSharp` or `clrdbg` code.
Its DAP transport is SharpDbg's own MIT `SharpDbg.DapProtocol`, generated from the
official DAP specification (MIT code, CC BY 4.0 documentation), with a
source-generated JSON context for NativeAOT. The agent package records each ABI's NuGet graph, carries the actual NativeAOT
runtime and Android runtime pack license and third-party notices alongside each binary,
and supplies package metadata for the distribution SPDX manifest. OpenDevelop no longer
builds the agents, so no build job needs the .NET 11 preview SDK, the Android workload
or an NDK; the packaged-app verifiers require these runtime notices with every agent
binary.

**Resolved:** `SharpDbg.Application` (the desktop adapter), `SharpDbg.Cli`, the packed
`SharpDbg.InMemory` package and the Android agent no longer depend on
`Microsoft.VisualStudio.Shared.VSCodeDebugProtocol` 18.0.10427.1, whose nuspec requires
acceptance of Microsoft's separate, non-open-source
[software license](https://go.microsoft.com/fwlink/?linkid=832965); they use SharpDbg's own
`SharpDbg.DapProtocol` (generated from the DAP specification). That package remains only in `sharpdbg/tests/SharpDbg.Cli.Tests`,
where it is the protocol client of the adapter tests; a test project is not part of any
payload. The packaged-app verifiers fail when a payload still contains
`Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.dll` (an old development output folder can
hold a stale copy because the copy targets never delete).

**Excluded commercial components:** `Uno.Sdk` 6.7.30 injects `Uno.UI.HotDesign` and
`Uno.UI.App.Mcp` into every Exe project. Neither package declares a license (the nuspec has
only `Copyright ... all rights reserved`), and Hot Design is a commercial Uno Platform Studio
feature, so they cannot be redistributed in OpenDevelop's payload.
`WinUIXamlDesigner.UnoHost.csproj` sets `UnoDisableHotDesign` and `UnoDisableMCPSupport`;
the Uno designer host no longer restores, builds or ships either package.
`Uno.Sdk.Extras` and `Uno.Settings.DevServer` are `PrivateAssets="all"` build tooling and are
not part of the payload. Distribution staging fails if `Uno.UI.HotDesign*.dll` or
`Uno.UI.App.Mcp*.dll` reappear under the Uno designer host.

`externals/vscode-wpf/sample` is used by integration tests only and is not
part of the runtime payload.

## NuGet and native payloads

The generated `THIRD-PARTY.spdx.json` inventories the resolved host, AddIn and
SharpDbg adapter NuGet graphs. The SharpDbg closure comes from the
`spdx/project.assets.json` that the restored `LeXtudio.SharpDbg.Cli` package carries; the
adapter is not an OpenDevelop project, so it would otherwise appear in no
`src/**/project.assets.json` and its packages would be missing from the manifest. It records declared nuspec expressions
where available and uses `NOASSERTION` where metadata is absent; it is an
inventory, not a legal conclusion. Package-contained LICENSE, COPYING, and NOTICE files are copied
under `THIRD-PARTY-NOTICES/NuGet/`, with a per-package notice catalog.

The distribution packaging step also copies the `LibGhostty.Net`
`THIRD-PARTY-NOTICES` directory. It contains the Ghostty and Windows Terminal
notices for the native terminal assets. Packaging fails if that directory is
absent.

## Release gate

Every release payload must contain `LICENSE`, `THIRD-PARTY-NOTICES.md`,
`THIRD-PARTY.spdx.json`, the source-linked `SOURCE-LICENSES` texts, and all
native or bundled-program notices. The Windows ZIP validator and macOS bundle
step check all of them before an archive is created.
