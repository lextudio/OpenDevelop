# Licensing audit

This audit is scoped to material that is actually compiled, embedded, copied
to a release payload, or used only by tests. A submodule's top-level license
does not apply automatically to every OpenDevelop release.

## Source-linked material

| Consumer | Exact upstream material | License evidence | Release treatment |
| --- | --- | --- | --- |
| `AspNetCore.csproj` | `externals/monodevelop/main/src/addins/MonoDevelop.AspNetCore/MonoDevelop.AspNetCore/ILaunchProfile.cs`, `LaunchProfile.cs` | Both files state `Copyright (c) Microsoft` and `Apache-2.0`. MonoDevelop's top-level `main/COPYING` is LGPL-2.1, but it is not the license evidence for these two source-linked files. | `SOURCE-LICENSES/MonoDevelop.AspNetCore-Apache-2.0.txt`. |
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

`altcover`, `cps`, `wpftoolkit`, `xamlstudio`, and
`monodevelop-nuget-extensions` have repository-level licenses but no direct
source-link in the application projects found by this audit. They may be build
or test dependencies. A future payload copy, source link, or bundled binary
must add a file-level entry above before release.

`SharpDbg.Cli` is not source-linked, but `Debugger.AddIn.csproj` builds and
copies its complete out-of-process adapter closure into the application
payload. Its MIT license is therefore copied as
`THIRD-PARTY-NOTICES/SharpDbg-LICENSE.txt`.

The Android debug agent is built from `externals/sharpdbg/src/SharpDbg.AndroidAgent`
and the SharpDbg infrastructure project. Its profiler callbacks
use the NuGet packages `Silhouette` 4.2.0 and `NativeObjects` 1.4.0; both packages'
nuspec files declare MIT. The agent uses `ICorDebugSharp` and the target .NET runtime's
debugging library; it does not include decompiled `ICorDebugSharp` or `clrdbg` code.
Its DAP transport is lextudio's NativeAOT-capable build of the OmniSharp DAP server
(`OmniSharp.Extensions.DebugAdapter`, `.DebugAdapter.Server`, `.DebugAdapter.Shared` and
`.JsonRpc`, from `lextudio/csharp-language-server-protocol`, a fork of
`OmniSharp/csharp-language-server-protocol`; the packages ship the upstream MIT `LICENSE`
file), pinned as `LextudioDapForkVersion` in `Directory.Packages.props` and restored from
lextudio GitHub Packages (CI) or the local feed (developers; see `AGENTS.md`).
The agent build records its NuGet graphs, copies the actual NativeAOT runtime and
Android runtime pack license and third-party notices alongside each ABI, and supplies
package metadata for the distribution SPDX manifest. The packaged-app verifiers
require these runtime notices with every agent binary.

**Resolved:** `SharpDbg.Application` (the desktop adapter), `SharpDbg.Cli`, the packed
`SharpDbg.InMemory` package and the Android agent no longer depend on
`Microsoft.VisualStudio.Shared.VSCodeDebugProtocol` 18.0.10427.1, whose nuspec requires
acceptance of Microsoft's separate, non-open-source
[software license](https://go.microsoft.com/fwlink/?linkid=832965); they use the OmniSharp
fork above. That package remains only in `externals/sharpdbg/tests/SharpDbg.Cli.Tests`,
where it is the protocol client of the adapter tests; a test project is not part of any
payload. The packaged-app verifiers fail when a payload still contains
`Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.dll` (an old development output folder can
hold a stale copy because the copy targets never delete).

`externals/vscode-wpf/sample` is used by integration tests only and is not
part of the runtime payload.

## NuGet and native payloads

The generated `THIRD-PARTY.spdx.json` inventories the resolved host and AddIn
NuGet graphs. It records declared nuspec expressions where available and uses
`NOASSERTION` where metadata is absent; it is an inventory, not a legal
conclusion. Package-contained LICENSE, COPYING, and NOTICE files are copied
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
