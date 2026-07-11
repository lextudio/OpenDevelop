#!/usr/bin/env bash
#
# rebuild-all.sh — the single entry point for the LibreWPF -> OpenDevelop pipeline.
#
# The pipeline has two halves that MUST run in order and were previously two separate scripts you
# had to remember to chain by hand:
#   1. repack-librewpf.sh  — build + pack LibreWPF (WPF fork + ProGPU) into the local feed
#                            (/Users/lextm/uno-tools/librewpf/artifacts/packages/Release/NonShipping,
#                            registered in NuGet.config as "local-librewpf"), clear the stale
#                            ~/.nuget/packages/librewpf.* + progpu.* caches, then restore
#                            OpenDevelop against the fresh packages. Uses the system .NET 10 SDK.
#   2. launch.sh           — build OpenDevelop.Mvp.slnx and run SharpDevelop. Also uses the
#                            system .NET 10 SDK.
#
# Running only half is exactly how a freshly-packed LibreWPF fix (new nupkg in the feed) ends up
# NOT in the running app: the pack updates the feed, but without the restore-and-rebuild the app's
# bin keeps the previously-restored assembly. This script makes the whole sequence one command.
#
# Usage:
#   ./rebuild-all.sh                full LibreWPF repack + restore + build OpenDevelop + run
#   ./rebuild-all.sh --fast         fast (ProGPU-only) repack, otherwise identical
#   ./rebuild-all.sh --no-repack    skip repack; just build + run (equivalent to ./launch.sh)
#   ./rebuild-all.sh --build-only   repack + build, but do NOT launch (for the integration tests,
#                                   which start their own app instance via --no-build)
#   flags combine, e.g.  ./rebuild-all.sh --fast --build-only
#
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

do_repack=1
do_run=1
repack_args=()

for arg in "$@"; do
  case "${arg}" in
    --no-repack)          do_repack=0 ;;
    --fast)               repack_args+=(--fast) ;;
    --build-only|--no-run) do_run=0 ;;
    *) echo "rebuild-all.sh: unknown flag '${arg}'" >&2; exit 2 ;;
  esac
done

if [[ "${do_repack}" -eq 1 ]]; then
  echo "==> [1/2] Repacking LibreWPF + restoring OpenDevelop..."
  # macOS's default bash (3.2) treats "${arr[@]}" on an EMPTY array as an unbound-variable error
  # under `set -u`, even though bash 4+ treats it as expanding to nothing - guard explicitly
  # instead of relying on `${arr[@]:-}`-style workarounds that read oddly for an array.
  if [[ "${#repack_args[@]}" -gt 0 ]]; then
    "${repo_root}/repack-librewpf.sh" "${repack_args[@]}"
  else
    "${repo_root}/repack-librewpf.sh"
  fi
else
  echo "==> [1/2] Skipping LibreWPF repack (--no-repack)."
fi

if [[ "${do_run}" -eq 1 ]]; then
  echo "==> [2/2] Building + launching OpenDevelop..."
  exec "${repo_root}/launch.sh"
else
  echo "==> [2/2] Building OpenDevelop (--build-only; no launch)..."
  exec "${repo_root}/launch.sh" --build-only
fi
