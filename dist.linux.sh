#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
pwsh_bin="$(command -v pwsh || true)"
[[ -n "$pwsh_bin" ]] || { echo 'dist.linux.sh: pwsh 7+ is required.' >&2; exit 1; }
args=()
for a in "$@"; do case "$a" in --skip-publish) args+=(-SkipPublish);; --debug) args+=(-Configuration Debug);; --*) args+=("-${a#--}");; *) args+=("$a");; esac; done
export NUGET_PACKAGES="$root/artifacts/nuget-packages"
"$pwsh_bin" -NoProfile -File "$root/sync-openavalon-feed.ps1"
exec "$pwsh_bin" -NoProfile -File "$root/dist.ps1" "${args[@]}"
