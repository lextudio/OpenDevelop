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

## UI framework and editors

OpenDevelop consumes LibreWPF, LibreWinForms, ProGPU, AvalonEdit and AvalonDock
as NuGet packages. Their exact versions and sources are pinned in
`Directory.Packages.props` and constrained by `NuGet.config` package-source
mappings. Distribution packaging copies package-contained LICENSE, COPYING,
and NOTICE files into `THIRD-PARTY-NOTICES/NuGet/`, with a catalog generated
from the exact resolved package graph.

## Microsoft components

Microsoft Windows App SDK, Visual Studio editor APIs, .NET runtime components
and Windows SDK assets are governed by the license metadata and notices shipped
with their exact packages. They must not be treated as MIT merely because they
are Microsoft packages.

## Release requirement

A releasable Windows ZIP or macOS application must include this file, the root
`LICENSE`, the generated third-party package manifest, and all notices required
by native assets copied into that payload. Distribution staging validates
their presence before archiving, and the Windows ZIP is validated afterwards.

The source-linked-submodule audit, including the exact files rather than only
repository-level licenses, is maintained in `doc/technotes/licensing-audit.md`.
