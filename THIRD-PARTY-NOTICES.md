# Third-party notices

OpenDevelop is MIT-licensed. Its distributions also contain independently
licensed third-party components. This file identifies the principal components
that are copied into a released payload; it does not change their licenses.

## Terminal

`LibGhostty.Net` is MIT-licensed. Its native assets include Ghostty's terminal
engine and, on Windows, Windows Terminal's `conpty.dll` and `OpenConsole.exe`.
The LibGhostty.Net package supplies their complete notices under
`THIRD-PARTY-NOTICES/`; release packaging must preserve that directory beside
the application payload.

## Debugger

The bundled `SharpDbg.Cli` debug adapter is MIT-licensed. Its license is
included as `THIRD-PARTY-NOTICES/SharpDbg-LICENSE.txt` in every release
payload.

The desktop adapter and the Android debug agent implement the Debug Adapter
Protocol with SharpDbg's own MIT-licensed `SharpDbg.DapProtocol`, generated from
the DAP specification (MIT for the code, CC BY 4.0 for the documentation).
Microsoft's `Microsoft.VisualStudio.Shared.VSCodeDebugProtocol` and the
`OmniSharp.Extensions.*` packages are no longer bundled.

## UI framework and editors

OpenDevelop consumes LibreWPF, LibreWinForms, ProGPU, AvalonEdit and AvalonDock
as NuGet packages. Their exact versions and sources are pinned in
`Directory.Packages.props` and constrained by `NuGet.config` package-source
mappings. Distribution packaging copies package-contained LICENSE, COPYING,
and NOTICE files into `THIRD-PARTY-NOTICES/NuGet/`, with a catalog generated
from the exact resolved package graph.

## Toolkit for WPF

`LeXtudio.Wpf.Toolkit` (version 3.7.2, from the `lextudio/wpftoolkit` fork, published to the
lextudio GitHub Packages feed) is the Community Edition of the open-source Toolkit for WPF
originally published by Xceed Software Inc., licensed under the Microsoft Public License
(Ms-PL); the package carries the license text (`license.md`) and the sources keep their
copyright notices. It is not affiliated with or endorsed by Xceed Software Inc.; the Xceed
name is a trademark of its owner, and the package id does not use it. The assembly is still
named `Xceed.Wpf.Toolkit` for compatibility with existing code. The license text is
copied from the package as `SOURCE-LICENSES/Toolkit-for-WPF-Ms-PL.txt`.

## Visual Studio editor API

`Microsoft.VisualStudio.CoreUtility`, `Microsoft.VisualStudio.Text.Data`,
`Microsoft.VisualStudio.Text.Logic` and `Microsoft.VisualStudio.Text.UI` are
built from the MIT-licensed `microsoft/vs-editor-api` sources by the
`lextudio/vs-editor-api` fork and consumed as the NuGet packages it publishes
to the lextudio GitHub Packages feed under the same ids (version 17.3.198.1),
not from Microsoft's NuGet packages. The upstream license is copied from the
package as `SOURCE-LICENSES/vs-editor-api-MIT.txt`.

## Microsoft components

Microsoft Windows App SDK, .NET runtime components and Windows SDK assets are
governed by the license metadata and notices shipped with their exact packages;
so are the native SQL Server SNI and Management Infrastructure runtimes pulled
in by `Microsoft.Data.SqlClient` and `Microsoft.PowerShell.SDK`. They must not be
treated as MIT merely because they are Microsoft packages.

## Release requirement

A releasable Windows ZIP or macOS application must include this file, the root
`LICENSE`, the generated third-party package manifest, and all notices required
by native assets copied into that payload. Distribution staging validates
their presence before archiving, and the Windows ZIP is validated afterwards.

The source-linked-submodule audit, including the exact files rather than only
repository-level licenses, is maintained in `doc/technotes/licensing-audit.md`.
