#!/usr/bin/env bash
#
# dist.macos.sh — thin wrapper. All packaging logic lives in dist.macos.ps1 so the
# flow stays reviewable next to launch.ps1 and can later be shared with Windows.
#
# Usage: ./dist.macos.sh [--skip-publish] [--debug]
#   --skip-publish  reuse existing publish output (faster iteration on bundle/dmg)
#   --debug         package the Debug configuration instead of Release.#
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

pwsh_bin="$(command -v pwsh 2>/dev/null || true)"
if [[ -z "${pwsh_bin}" ]]; then
  for c in /opt/homebrew/bin/pwsh /usr/local/bin/pwsh; do
    if [[ -x "${c}" ]]; then pwsh_bin="${c}"; break; fi
  done
fi
if [[ -z "${pwsh_bin}" ]]; then
  echo "dist.macos.sh: cannot find pwsh (PowerShell). Install it with: brew install --cask powershell" >&2
  exit 1
fi

args=()
for a in "$@"; do
  case "${a}" in
    # Explicit map: PowerShell does NOT bind "-skip-publish" to "-SkipPublish" (dashes
    # must go), and --debug is a PowerShell COMMON parameter name, so the ps1 exposes
    # -Configuration instead. Unrecognized flags fall through to PS's strict binding.
    --skip-publish) args+=("-SkipPublish") ;;
    --debug)        args+=("-Configuration" "Debug") ;;
    --*)            args+=("-${a#--}") ;;
    *)              args+=("${a}") ;;
  esac
done

# macOS's default bash (3.2) treats "${arr[@]}" on an EMPTY array as an unbound-variable
# error under `set -u` - guard explicitly instead of relying on `${arr[@]:-}`-style
# workarounds that read oddly for an array (same trick as rebuild-all.sh).
exec "${pwsh_bin}" -NoProfile -File "${repo_root}/dist.macos.ps1" ${args[@]+"${args[@]}"}
