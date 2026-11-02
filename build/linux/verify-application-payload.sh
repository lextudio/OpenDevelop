#!/usr/bin/env bash
#
# Verifies the packaged Linux payload the way a user drives it, not the development tree.
#
# The Windows and macOS release jobs have had this step for a while: `dist.ps1`'s own smoke phase
# only proves that the application starts and paints its first workbench frame, so everything a
# payload can get wrong after that - a pruned `runtimes/` tree, a missing deps.json entry, a native
# library the child hosts need, the XAML/Roslyn/terminal/ILSpy add-ins inside the bundle - reached
# Linux users unverified. This runs the same journeys the macOS bundle check runs (they share
# build/verify-packaged-common.sh), against the payload directory `dist.linux.sh` produced.
#
# Usage (the release job wraps it in xvfb-run, which is what provides DISPLAY):
#   xvfb-run -a build/linux/verify-application-payload.sh OpenDevelop-linux-x64
#
# Optional:
#   OD_VERIFY_PORT=9472                  DevFlow port (one job per runner, so it does not collide
#                                        with the macOS/macOS-design and Windows checks).
#   OD_VERIFY_REQUIRE_DEBUGGER=1         Also run the packaged debugger journey.

set -euo pipefail

repo_root=$(cd "$(dirname "$0")/../.." && pwd)
payload=${1:-"$repo_root/OpenDevelop-linux-$(uname -m | sed 's/x86_64/x64/; s/aarch64/arm64/')/}"}
port=${OD_VERIFY_PORT:-9472}
config_dir=$(mktemp -d "${TMPDIR:-/tmp}/opendevelop-linux-verify.XXXXXX")
lease_id="linux-payload-verify-$(uuidgen 2>/dev/null || cat /proc/sys/kernel/random/uuid)"
app_pid=''
app_log=''

fail() {
  echo "::error::packaged Linux payload verification: $*" >&2
  dump_diagnostics
  exit 1
}

dump_diagnostics() {
  echo '--- Linux payload verification diagnostics ---------------------' >&2
  echo "agent status: $(curl --silent --max-time 2 "${base_url:-http://127.0.0.1:$port}/api/v1/agent/status" 2>/dev/null || true)" >&2
  if [[ -n "${app_pid:-}" ]] && kill -0 "$app_pid" 2>/dev/null; then
    echo "app process: alive (pid $app_pid): $(ps -o stat=,etime=,command= -p "$app_pid" 2>/dev/null || true)" >&2
  else
    echo "app process: not running (pid ${app_pid:-<unknown>})" >&2
  fi
  echo "leftover dotnet children: $(pgrep -af 'dotnet' 2>/dev/null | tr '\n' ' ' || true)" >&2
  local log
  for log in "${app_log:-}" "${TMPDIR:-/tmp}/OpenDevelop.log" /tmp/OpenDevelop.log; do
    [[ -n "$log" && -f "$log" ]] || continue
    echo "--- $log (last 80 lines) ---" >&2
    tail -n 80 "$log" >&2 || true
    break
  done
  # A kernel OOM kill leaves no trace in the application's own log; the ring buffer is the only
  # place that names it.
  if command -v journalctl >/dev/null 2>&1; then
    echo '--- kernel log, last 2 minutes ---' >&2
    journalctl -k --since '2 minutes ago' --no-pager 2>/dev/null | tail -n 20 >&2 || true
  fi
  echo '-----------------------------------------------------------------' >&2
}

cleanup() {
  if [[ -n "${app_pid:-}" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid" 2>/dev/null || true
    for _ in $(seq 1 10); do
      kill -0 "$app_pid" 2>/dev/null || break
      sleep 1
    done
    kill -9 "$app_pid" 2>/dev/null || true
  fi
  rm -rf -- "$config_dir"
}
trap cleanup EXIT

[[ -d "$payload" ]] || fail "payload directory does not exist: $payload"
[[ -f "$payload/OpenDevelop.dll" ]] || fail "payload has no OpenDevelop.dll: $payload"
command -v curl >/dev/null || fail 'curl is required'
command -v jq >/dev/null || fail 'jq is required'
command -v dotnet >/dev/null || fail 'dotnet (the host the payload runs on) is required'
# No display means no workbench: fail with the reason instead of a page of WPF errors. The release
# job runs this through xvfb-run, which sets DISPLAY for the whole script.
[[ -n "${DISPLAY:-}" ]] || fail 'DISPLAY is not set; run this through xvfb-run (the release job does)'

base_url="http://127.0.0.1:$port"

verify_label='Linux release payload verification'
agent_exited() { ! kill -0 "$app_pid" 2>/dev/null; }
# shellcheck source=../verify-packaged-common.sh
source "$repo_root/build/verify-packaged-common.sh"

echo '==> Starting the packaged OpenDevelop payload with DevFlow enabled...'
app_log="$config_dir/OpenDevelop.stdio.log"
DEVFLOW_ENABLE=1 DEVFLOW_AGENT_PORT="$port" \
  dotnet "$payload/OpenDevelop.dll" "-devflow:$port" "-configdir:$config_dir" \
  >"$app_log" 2>&1 &
app_pid=$!

wait_for_agent 60

verify_solution_build
verify_roslyn
verify_terminal
verify_ilspy "$payload/OpenDevelop.dll"
verify_wpf_designer
verify_debugger "$payload/AddIns/Debugger" "${OD_VERIFY_REQUIRE_DEBUGGER:-0}"

echo 'Packaged Linux payload verification passed.'
