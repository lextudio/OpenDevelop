#!/usr/bin/env bash
#
# launch.sh — thin wrapper. All build/run logic lives in launch.ps1 so Windows and
# macOS share one implementation (see also dist.macos.sh / dist.ps1).
#
# Usage:
#   ./launch.sh                build OpenDevelop.Mvp.sln, then run OpenDevelop
#   ./launch.sh --no-build     skip the build, just (re)run the last build output
#   ./launch.sh --build-only   build but do NOT launch (used by rebuild-all.sh --build-only and
#                              by the integration tests, which start their own app instance)
#   DEVFLOW_DISABLE=1 ./launch.sh   run without the DevFlow debugging agent
#
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

pwsh_bin="$(command -v pwsh 2>/dev/null || true)"
if [[ -z "${pwsh_bin}" ]]; then
  for c in /opt/homebrew/bin/pwsh /usr/local/bin/pwsh; do
    if [[ -x "${c}" ]]; then pwsh_bin="${c}"; break; fi
  done
fi
if [[ -z "${pwsh_bin}" ]]; then
  echo "launch.sh: cannot find pwsh (PowerShell). Install it with: brew install --cask powershell" >&2
  exit 1
fi

# Translate historical flag spellings to PowerShell parameters (PowerShell does NOT
# bind "-no-build" to "-NoBuild" - dashes must go). Unrecognized flags fall through to
# PowerShell's own strict binding error.
args=()
for a in "$@"; do
  case "${a}" in
    --no-build)    args+=("-NoBuild") ;;
    --build-only)  args+=("-BuildOnly") ;;
    --*)           args+=("-${a#--}") ;;
    *)             args+=("${a}") ;;
  esac
done

# macOS's default bash (3.2) treats "${arr[@]}" on an EMPTY array as an unbound-variable
# error under `set -u` - guard explicitly instead of relying on `${arr[@]:-}`-style
# workarounds that read oddly for an array (same trick as rebuild-all.sh).
exec "${pwsh_bin}" -NoProfile -File "${repo_root}/launch.ps1" ${args[@]+"${args[@]}"}
