#!/usr/bin/env bash
#
# Initialises the submodules the build reaches. Every job that compiles calls this, so the list lives in one place.
#   bash .github/ci/init-submodules.sh
#
# The integration test project's ProjectReference closure reaches cps, the standalone WpfDesigner
# and WinAppVSCE submodules; the coverage submodule is small and kept in case a build target uses it
# directly. xamlstudio was removed on 2026-11-14: its renderer belonged to the ProGPU profile, which
# was deleted on 2026-11-09, and no project references it any more.
# sharpdbg is deliberately absent: it is a sibling checkout (../sharpdbg), not a submodule, and the
# debugger add-in consumes the adapter and the Android agents as LeXtudio.SharpDbg.* packages.
set -euo pipefail

git submodule update --init externals/vsmac-codecoverage
git submodule update --init --recursive externals/cps externals/WpfDesigner externals/winappvsce
