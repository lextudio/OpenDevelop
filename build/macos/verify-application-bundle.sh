#!/usr/bin/env bash
#
# Verifies that a freshly assembled macOS .app is usable beyond the bootstrap
# smoke test.  The bundle is intentionally tested in place: the purpose is to
# catch a packaging/deduplication error which normal project-output integration
# tests cannot see (missing AddIns, child-host dependencies, or native dylibs).
#
# Requires the repository fixtures because the checks drive the app through its
# DevFlow automation endpoint.  It is run by the release workflow after
# dist.macos.sh and before the DMG is uploaded.

set -euo pipefail

repo_root=$(cd "$(dirname "$0")/../.." && pwd)
app_path=${1:-"$repo_root/OpenDevelop.app"}
port=${OD_BUNDLE_VERIFY_PORT:-9471}
config_dir=$(mktemp -d "${TMPDIR:-/tmp}/opendevelop-bundle-verify.XXXXXX")
lease_id="bundle-verify-$(uuidgen)"
app_pid=''

# A failed request says only that the endpoint stopped answering, which is exactly what a crashed
# application looks like from here. Print what the process and its own log say before leaving, so a
# red release names the cause instead of "curl: (52) Empty reply from server".
dump_diagnostics() {
  local status report log dir dump
  status=$(curl --silent --max-time 2 "${base_url:-http://127.0.0.1:$port}/api/v1/agent/status" 2>/dev/null || true)
  echo '--- macOS bundle verification diagnostics -----------------------' >&2
  echo "agent status: ${status:-<no answer>}" >&2
  if [[ -n "${app_pid:-}" ]] && kill -0 "$app_pid" 2>/dev/null; then
    # A zombie still answers kill -0: it was reaped by the system but nothing has collected it, which
    # is a different failure from a live-but-unresponsive application.
    echo "app process: alive (pid $app_pid): $(ps -o stat=,etime=,command= -p "$app_pid" 2>/dev/null || true)" >&2
  else
    echo "app process: not running (pid ${app_pid:-<unknown>})" >&2
    echo "remaining OpenDevelop processes: $(pgrep -fl "${app_path}/Contents/MacOS/OpenDevelop" 2>/dev/null | tr '\n' ' ' || true)" >&2
    echo "leftover dotnet children: $(pgrep -fl 'dotnet' 2>/dev/null | tr '\n' ' ' || true)" >&2
  fi
  for log in "${TMPDIR:-/tmp}/OpenDevelop.log" "$HOME/Library/Logs/OpenDevelop/OpenDevelop.log"; do
    if [[ -f "$log" ]]; then
      echo "--- $log (last 80 lines) ---" >&2
      tail -n 80 "$log" >&2 || true
    fi
  done
  # ReportCrash writes asynchronously, so a rushed check reports "no crash report" for a process
  # that did crash. Give it a moment, then look at every report created since this launch - the
  # name is not necessarily an OpenDevelop one (JetsamEvent names the killer, not the victim).
  sleep 3
  report=''
  for dir in "$HOME/Library/Logs/DiagnosticReports" "/Library/Logs/DiagnosticReports"; do
    [[ -d "$dir" ]] || continue
    while IFS= read -r -d '' dump; do
      if [[ -z "$report" || "$dump" -nt "$report" ]]; then
        report=$dump
      fi
    done < <(find "$dir" -maxdepth 1 \( -name '*.ips' -o -name 'JetsamEvent*' \) \
               -newer "${launch_marker:-/dev/null}" -print0 2>/dev/null)
    if [[ -n "$report" ]]; then break; fi
  done
  if [[ -n "$report" ]]; then
    echo "--- crash report written since launch: $report ---" >&2
    head -n 60 "$report" >&2 || true
  else
    echo '--- no crash or JetsamEvent report was written for this launch ---' >&2
  fi
  # A CLR fatal error leaves a triage dump; its absence means the runtime did not abort, i.e. the
  # process was killed from outside or exited through Application.Shutdown.
  local dumps=0
  for dump in "${TMPDIR:-/tmp}"/opendevelop-verify-crash-*.dmp; do
    [[ -f "$dump" ]] || continue
    dumps=$((dumps + 1))
    echo "--- CLR crash dump: $dump ($(du -h "$dump" 2>/dev/null | cut -f1)) ---" >&2
  done
  [[ "$dumps" -gt 0 ]] || echo '--- no CLR crash dump (the runtime did not abort) ---' >&2
  # The unified log carries the CLR's own fatal message and the kernel's reason for a kill.
  if command -v log >/dev/null 2>&1; then
    echo '--- unified log, OpenDevelop, last 5 minutes ---' >&2
    log show --last 5m --style compact \
      --predicate 'process == "OpenDevelop" OR eventMessage CONTAINS "OpenDevelop"' 2>/dev/null \
      | tail -n 40 >&2 || true
  fi
  echo '-----------------------------------------------------------------' >&2
}

fail() {
  echo "::error::macOS bundle verification: $*" >&2
  dump_diagnostics
  exit 1
}

cleanup() {
  # `open` has no PID output.  A failed readiness check can happen before the
  # launcher's process was discovered, so resolve this invocation's unique
  # DevFlow port here as a final cleanup fallback.
  if [[ -z "$app_pid" ]]; then
    app_pid=$(pgrep -nf "${app_path}/Contents/MacOS/OpenDevelop.*-devflow:$port" || true)
  fi
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid" 2>/dev/null || true
    for _ in $(seq 1 10); do
      kill -0 "$app_pid" 2>/dev/null || break
      sleep 1
    done
  fi
  rm -rf -- "$config_dir"
  if [[ -n "${launch_marker:-}" ]]; then rm -f -- "$launch_marker"; fi
}
trap cleanup EXIT

[[ -d "$app_path" ]] || fail "bundle does not exist: $app_path"
[[ -f "$app_path/Contents/MacOS/OpenDevelop.dll" ]] || fail "bundle has no OpenDevelop.dll"
command -v curl >/dev/null || fail 'curl is required'
command -v jq >/dev/null || fail 'jq is required'

base_url="http://127.0.0.1:$port"

verify_label='macOS release bundle verification'
# shellcheck source=../verify-packaged-common.sh
source "$repo_root/build/verify-packaged-common.sh"

echo '==> Starting the packaged OpenDevelop app with DevFlow enabled...'
# A CLR fatal error (unhandled exception outside our handlers, FailFast, stack overflow) writes a
# small triage dump; its presence or absence in a failure tells "the runtime aborted" apart from
# "something else killed the process", which no log line can.
crash_dump_pattern="${TMPDIR:-/tmp}/opendevelop-verify-crash-%p.dmp"
launch_marker=$(mktemp "${TMPDIR:-/tmp}/opendevelop-verify-launch.XXXXXX")
touch "$launch_marker"
DEVFLOW_ENABLE=1 DEVFLOW_AGENT_PORT="$port" \
  DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=4 DOTNET_DbgMiniDumpName="$crash_dump_pattern" \
  open -n "$app_path" --args "-devflow:$port" "-configdir:$config_dir"
wait_for_agent 45
app_pid=$(pgrep -nf "${app_path}/Contents/MacOS/OpenDevelop.*-devflow:$port" || true)
[[ -n "$app_pid" ]] || fail 'could not identify the launched app process'

verify_solution_build
verify_roslyn
verify_terminal
verify_ilspy "$app_path/Contents/MacOS/OpenDevelop.dll"
verify_wpf_designer

# macOS 27 presently regresses CoreCLR's debugger-startup semaphore handshake, so a developer on that host only gets the
# packaged-adapter check; the release workflow forces the live journey on its macOS 15 runner.
# https://github.com/dotnet/runtime/issues/116545
darwin_major=$(uname -r | cut -d. -f1)
require_debugger=${OD_VERIFY_REQUIRE_DEBUGGER:-${OD_BUNDLE_VERIFY_REQUIRE_DEBUGGER:-0}}
if [[ "$darwin_major" -lt 27 ]]; then require_debugger=1; fi
verify_debugger "$app_path/Contents/MacOS/AddIns/Debugger" "$require_debugger"

echo 'Packaged macOS feature verification passed.'
