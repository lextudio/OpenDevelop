#!/usr/bin/env bash
#
# Initialises the submodules the build reaches. Every job that compiles calls this, so the list lives in one place.
#   bash .github/ci/init-submodules.sh
#
# src/Libraries/VSEditorCore compiles the Microsoft.VisualStudio.Text.* contracts straight out of externals/vs-editor-api
# (MIT) instead of taking Microsoft's NuGet packages, so every build that reaches OpenDevelop.VSEditorCompat needs it.
# The integration test project's ProjectReference closure reaches cps, WPF Toolkit, the standalone WpfDesigner
# and WinAppVSCE submodules; the coverage and XAML submodules are small and kept in case a build target uses them
# directly. externals/sharpdbg is deliberately absent: the debugger add-in consumes the adapter and the Android agents
# as LeXtudio.SharpDbg.* packages, so no job builds that checkout. The AspNetCore add-in compiles four source files
# straight out of externals/monodevelop (a Compile
# Include, which a ProjectReference closure does not show), so that one is initialised shallow and without its own
# submodules; its sources stay out of the workspace archive. monodevelop-nuget-extensions is not used.
set -euo pipefail

git submodule update --init \
  externals/wpftoolkit externals/xamlstudio externals/altcover externals/vsmac-codecoverage externals/vs-editor-api
git submodule update --init --recursive externals/cps externals/WpfDesigner externals/winappvsce
git submodule update --init --depth 1 externals/monodevelop
