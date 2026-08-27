#!/usr/bin/env bash
# Thin POSIX entry point for release.macos.ps1.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
pwsh_bin="$(command -v pwsh 2>/dev/null || true)"
if [[ -z "${pwsh_bin}" ]]; then
  for candidate in /opt/homebrew/bin/pwsh /usr/local/bin/pwsh; do
    if [[ -x "${candidate}" ]]; then pwsh_bin="${candidate}"; break; fi
  done
fi
if [[ -z "${pwsh_bin}" ]]; then
  echo "release.macos.sh: cannot find pwsh; install it with: brew install --cask powershell" >&2
  exit 1
fi
args=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) args+=("-Version" "$2"); shift 2 ;;
    --release-tag) args+=("-ReleaseTag" "$2"); shift 2 ;;
    --debug) args+=("-Configuration" "Debug"); shift ;;
    --skip-publish) args+=("-SkipPublish"); shift ;;
    --prepare-only) args+=("-PrepareOnly"); shift ;;
    --allow-dirty-worktree) args+=("-AllowDirtyWorktree"); shift ;;
    *) args+=("$1"); shift ;;
  esac
done
exec "${pwsh_bin}" -NoProfile -File "${repo_root}/release.macos.ps1" "${args[@]}"
