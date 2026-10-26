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

`externals/vscode-wpf/sample` is used by integration tests only and is not
part of the runtime payload.

## NuGet and native payloads

The generated `THIRD-PARTY.spdx.json` inventories the resolved host and AddIn
NuGet graphs. It records declared nuspec expressions where available and uses
`NOASSERTION` where metadata is absent; it is an inventory, not a legal
conclusion.

The distribution packaging step also copies the `LibGhostty.Net`
`THIRD-PARTY-NOTICES` directory. It contains the Ghostty and Windows Terminal
notices for the native terminal assets. Packaging fails if that directory is
absent.

## Release gate

Every release payload must contain `LICENSE`, `THIRD-PARTY-NOTICES.md`,
`THIRD-PARTY.spdx.json`, the source-linked `SOURCE-LICENSES` texts, and all
native or bundled-program notices. The Windows ZIP validator and macOS bundle
step check all of them before an archive is created.
