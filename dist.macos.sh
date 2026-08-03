#!/usr/bin/env bash
# Build an Apple Silicon (arm64) macOS .dmg for local testing.
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
# Clear it before each publish so the SDK regenerates it for the correct RID.
#
# Similarly, the managed assembly is cached in obj/<config>/net10.0-windows/
# (not RID-specific).  The PE machine type must match the running process, so
# we clean intermediate build outputs before publishing to force
# recompilation for the target (arm64 — this is an Apple Silicon build).
host_obj="${script_dir}/src/Main/SharpDevelop/obj/${config}"

if [[ "$skip_publish" -eq 0 ]]; then
  echo "==> Cleaning intermediate outputs for osx-arm64…"
  rm -rf "$host_obj/net10.0-windows"
  echo "==> Publishing osx-arm64 (${config})…"
  "${dotnet}" publish "$host_dir" -r osx-arm64 -c "${config}"
else
  echo "==> Skipping publish (--skip-publish)"
fi

echo "==> Building .app bundle (arm64, ${config})…"
DIST_CONFIG="${config}" "$script_dir/build/macos/build-application-bundle.sh" osx-arm64

echo "==> Building .dmg…"
"$script_dir/build/macos/build-dmg.sh" OpenDevelop.app OpenDevelop-macos-arm64.dmg

echo ""
echo "Done: $(pwd)/OpenDevelop-macos-arm64.dmg"
