#!/usr/bin/env bash
#
# Initialises the submodules the build reaches. Every job that compiles calls this, so the list lives in one place.
#   bash .github/ci/init-submodules.sh
#
# The integration test project's ProjectReference closure reaches AvalonEdit, AvalonDock, cps, WPF Toolkit and vscode-wpf
# (with its nested WpfDesigner/wxsg); the debugger, coverage and XAML submodules are small and kept in case a build target
# uses them directly. The AspNetCore add-in compiles four source files straight out of externals/monodevelop (a Compile
# Include, which a ProjectReference closure does not show), so that one is initialised shallow and without its own
# submodules; its sources stay out of the workspace archive. monodevelop-nuget-extensions is not used.
set -euo pipefail

git submodule update --init \
  src/Libraries/AvalonEdit src/Libraries/AvalonDock \
  externals/wpftoolkit externals/sharpdbg externals/xamlstudio externals/altcover externals/vsmac-codecoverage
git submodule update --init --recursive externals/cps externals/vscode-wpf
git submodule update --init --depth 1 externals/monodevelop
