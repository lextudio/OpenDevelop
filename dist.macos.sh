#!/usr/bin/env bash
# Build a universal macOS .dmg for local testing.
# Mirrors the CI steps in .github/workflows/package.yml.
# Usage: ./dist.macos.sh [--skip-publish] [--debug]
#   --skip-publish  reuse existing publish output (faster iteration on bundle/dmg)
#   --debug         package the Debug configuration instead of Release. Rarely needed
#                   anymore: Release crashed at startup because RID-specific `dotnet
#                   publish` drops LibreWPF's win32 shim dlls (kernel32/user32/gdi32/
#                   shell32/uxtheme/dwmapi/comdlg32), which AvalonDock's window hook
#                   resolves at DockingManager load time. build-application-bundle.sh
#                   now copies them into the payload, so Release works. Keep the flag
#                   for fast debug iteration.

set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
host_dir="$script_dir/src/Main/SharpDevelop/SharpDevelop.csproj"

dotnet_candidates=(
  "/usr/local/share/dotnet/dotnet"
  "/opt/homebrew/bin/dotnet"
)
dotnet=""
for c in "${dotnet_candidates[@]}"; do
  if [[ -x "${c}" ]]; then
    dotnet="${c}"
    break
  fi
done
if [[ -z "${dotnet}" ]]; then
  dotnet="$(command -v dotnet 2>/dev/null || true)"
fi
if [[ -z "${dotnet}" || ! -x "${dotnet}" ]]; then
  echo "dist.macos.sh: cannot find dotnet (checked ${dotnet_candidates[*]} and PATH)" >&2
  exit 1
fi
dotnet="$(readlink -f "${dotnet}")"

skip_publish=0
config="Release"
for arg in "$@"; do
  [[ "$arg" == "--skip-publish" ]] && skip_publish=1
  [[ "$arg" == "--debug" ]] && config="Debug"
done

# Ensure clean state for ICSharpCode.Core.Presentation — its .g.resources
# (WPF theme resource blob) can otherwise stale-cross from a previous build
# and produce a 12-byte corrupt file that crashes at boot with
# EndOfStreamException in FindResource / LoadThemedDictionary.
core_pres_obj="${script_dir}/src/Main/ICSharpCode.Core.Presentation"
rm -rf "${core_pres_obj}/obj/${config}" "${core_pres_obj}/bin/${config}"

# The AppHost (native executable entry point) is cached in
# obj/<config>/net10.0-windows/apphost (shared across RIDs, not per-RID),
# because the net10.0-windows TFM lacks a RID-specific host pack on macOS.
# Without clearing it, the second publish reuses the first RID's AppHost
# and both RIDs end up with the same architecture.  Clear it before each
# publish so the SDK regenerates it for the correct RID.
#
# Similarly, the managed assembly is cached in obj/<config>/net10.0-windows/
# (not RID-specific).  The PE machine type (Amd64 vs ARM64) must match the
# running process, so we clean intermediate build outputs between RIDs to
# force recompilation for the correct target — arm64 first (native on this
# hardware) then x64.
host_obj="${script_dir}/src/Main/SharpDevelop/obj/${config}"

publish_for_rid() {
  local rid="$1"
  echo "==> Cleaning intermediate outputs for ${rid}…"
  rm -rf "$host_obj/net10.0-windows"
  echo "==> Publishing ${rid} (${config})…"
  "${dotnet}" publish "$host_dir" -r "${rid}" -c "${config}"
}

if [[ "$skip_publish" -eq 0 ]]; then
  publish_for_rid osx-arm64
  publish_for_rid osx-x64
else
  echo "==> Skipping publish (--skip-publish)"
fi

echo "==> Building .app bundle (universal, ${config})…"
DIST_CONFIG="${config}" "$script_dir/build/macos/build-application-bundle.sh" osx-universal

echo "==> Building .dmg…"
"$script_dir/build/macos/build-dmg.sh" OpenDevelop.app OpenDevelop-macos-universal.dmg

echo ""
echo "Done: $(pwd)/OpenDevelop-macos-universal.dmg"
