#!/usr/bin/env bash
#
# Verifies the packaged Linux payload the way a user drives it, not the development tree.
#
# The Windows and macOS release jobs have had this step for a while: `dist.ps1`'s own smoke phase
# only proves that the application starts and paints its first workbench frame, so everything a
# payload can get wrong after that - a pruned `runtimes/` tree, a missing deps.json entry, a native
# library the child hosts need, the XAML/Roslyn/terminal/ILSpy add-ins inside the bundle - reached
# Linux users unverified. This runs the same journeys the macOS bundle check runs, against the
# payload directory `dist.linux.sh` produced.
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

claim_lease() {
  local lease
  if ! lease=$(curl --fail --silent --show-error --max-time 10 \
      -X POST "$base_url/api/v1/agent/lease" \
      -H 'Content-Type: application/json' \
      --data "$(jq -nc --arg id "$lease_id" '{action:"claim", leaseId:$id, holderKind:"release-payload-check", label:"Linux release payload verification", force:true}')"); then
    fail 'could not claim a DevFlow lease (the application may have exited)'
  fi
  jq -e '.allowed == true' <<<"$lease" >/dev/null || fail "the DevFlow agent refused the lease: $lease"
}

# Prints the parsed inner JSON returned by a DevFlow action. Claim immediately before every request
# because the agent deliberately uses a short mutation lease.
invoke() {
  local action=$1
  shift
  local payload_args response
  payload_args=$(jq -nc '$ARGS.positional' --args "$@")
  claim_lease
  response=$(curl --fail --silent --show-error --max-time 120 \
    -X POST "$base_url/api/v1/invoke/actions/$action" \
    -H 'Content-Type: application/json' \
    -H "X-DevFlow-Lease: $lease_id" \
    --data "$(jq -nc --argjson args "$payload_args" '{args:$args}')") || fail "$action request failed"
  jq -e '.success == true' <<<"$response" >/dev/null || fail "$action returned an unsuccessful envelope: $response"
  jq -er '.returnValue | fromjson' <<<"$response"
}

echo '==> Starting the packaged OpenDevelop payload with DevFlow enabled...'
app_log="$config_dir/OpenDevelop.stdio.log"
DEVFLOW_ENABLE=1 DEVFLOW_AGENT_PORT="$port" \
  dotnet "$payload/OpenDevelop.dll" "-devflow:$port" "-configdir:$config_dir" \
  >"$app_log" 2>&1 &
app_pid=$!

agent_started=0
for _ in $(seq 1 60); do
  if status=$(curl --silent --show-error --fail --max-time 2 "$base_url/api/v1/agent/status" 2>/dev/null) \
    && jq -e '.running == true' <<<"$status" >/dev/null; then
    agent_started=1
    break
  fi
  kill -0 "$app_pid" 2>/dev/null || fail 'the payload exited before its DevFlow agent answered'
  sleep 2
done
[[ "$agent_started" == 1 ]] || fail 'DevFlow agent did not start'

# The agent listens before the docking workbench has finished constructing.
invoke od.active-view >/dev/null
sleep 2
invoke od.active-view >/dev/null

echo '==> Checking solution loading, Roslyn and build...'
solution="$repo_root/tests/fixtures/SolutionExplorerFixture/SolutionExplorerFixture.slnx"
invoke od.open-solution "$solution" | jq -e --arg solution "$solution" '.success == true and .currentSolution == $solution' >/dev/null \
  || fail 'solution fixture did not open'
invoke od.build-solution | jq -e '.success == true and .result == "Success" and .errorCount == 0' >/dev/null \
  || fail 'solution fixture did not build cleanly'

echo '==> Checking the packaged Roslyn child host and project workspace...'
csharp_source="$repo_root/tests/fixtures/SolutionExplorerFixture/SampleApp/Program.cs"
invoke od.open-file "$csharp_source" | jq -e '.opened == true' >/dev/null || fail 'C# fixture source did not open'
roslyn_status=''
for _ in $(seq 1 30); do
  roslyn_status=$(invoke od.language-workspace.status "$csharp_source")
  jq -e '.supported == true and .tracked == true and .metadataReferenceCount > 0' <<<"$roslyn_status" >/dev/null && break
  sleep 2
done
jq -e '.supported == true and .tracked == true and .metadataReferenceCount > 0' <<<"$roslyn_status" >/dev/null \
  || fail "Roslyn child host did not provide a project workspace: $roslyn_status"
invoke od.roslyn-legacy-workspace.status | jq -e '.remoteHostMode == true and .workspaceCreated == false' >/dev/null \
  || fail 'Roslyn fell back to the deprecated in-process workspace'

echo '==> Checking terminal shell, output, and both split directions...'
terminal=$(invoke od.terminal.new /bin/sh)
jq -e '.success == true and .pid > 0' <<<"$terminal" >/dev/null || fail 'terminal shell did not start'
invoke od.terminal.send $'printf \'linux-payload-ok\\n\'\r' | jq -e '.success == true' >/dev/null || fail 'terminal input failed'
terminal_text=''
for _ in $(seq 1 25); do
  terminal_text=$(invoke od.terminal.text)
  jq -e '.text | contains("linux-payload-ok")' <<<"$terminal_text" >/dev/null && break
  sleep 1
done
jq -e '.text | contains("linux-payload-ok")' <<<"$terminal_text" >/dev/null || fail 'terminal did not render shell output'
invoke od.terminal.split | jq -e '.success == true' >/dev/null || fail 'horizontal terminal split failed'
invoke od.terminal.split-vertical | jq -e '.success == true' >/dev/null || fail 'vertical terminal split failed'
invoke od.terminal.list | jq -e '.sessions | length >= 3' >/dev/null || fail 'terminal splits did not create three sessions'

echo '==> Checking the hosted ILSpy add-in...'
invoke od.ilspy.open-assembly "$payload/OpenDevelop.dll" | jq -e '.opened == true' >/dev/null \
  || fail 'ILSpy could not open the packaged assembly'
invoke od.ilspy.status | jq -e '([.loadedAssemblies[]] | index("OpenDevelop")) != null and (.panes | length >= 3)' >/dev/null \
  || fail 'ILSpy panes or decompiler dependencies are unavailable'

echo '==> Checking the LibreWPF designer child host and its rendered frame...'
wpf_solution="$repo_root/tests/fixtures/WpfHotReloadFixture/WpfHotReloadFixture.slnx"
wpf_xaml="$repo_root/externals/vscode-wpf/sample/net6.0/MainWindow.xaml"
[[ -f "$wpf_xaml" ]] || fail "linked WPF fixture source is missing: $wpf_xaml"
invoke od.open-solution "$wpf_solution" | jq -e '.success == true' >/dev/null || fail 'WPF fixture solution did not open'
invoke od.open-file "$wpf_xaml" | jq -e '.opened == true' >/dev/null || fail 'WPF fixture XAML did not open'
invoke od.xaml.language-server "$wpf_xaml" | jq -e --arg workspace "$(dirname "$wpf_solution")" \
  '.success == true and .key == "LibreWpf" and .server == "librewpf-xaml-ls.dll" and .workspace == $workspace' >/dev/null \
  || fail 'XAML file was not routed to the packaged LibreWPF language server'
xaml_outline=''
for _ in $(seq 1 30); do
  xaml_outline=$(invoke od.xaml-outline.status)
  jq -e '.active == true and .rootChildCount > 0 and any(.outlineNames[]; startswith("Button [PrimaryButton]"))' <<<"$xaml_outline" >/dev/null && break
  sleep 2
done
jq -e '.active == true and .rootChildCount > 0 and any(.outlineNames[]; startswith("Button [PrimaryButton]"))' <<<"$xaml_outline" >/dev/null \
  || fail "LibreWPF XAML language server did not return the fixture outline: $xaml_outline"
invoke od.wpf-designer.activate-design | jq -e '.success == true' >/dev/null || fail 'WPF design view did not activate'
wpf_status=''
for _ in $(seq 1 30); do
  wpf_status=$(invoke od.wpf-designer.status)
  jq -e '.backend == "LibreWPF" and .hostAlive == true and .designerLoaded == true and .hasRenderFrame == true and .rootItemType == "Window"' <<<"$wpf_status" >/dev/null && break
  sleep 2
done
jq -e '.backend == "LibreWPF" and .hostAlive == true and .designerLoaded == true and .hasRenderFrame == true and .rootItemType == "Window"' <<<"$wpf_status" >/dev/null \
  || fail "WPF designer did not load a rendered frame: $wpf_status"

if [[ "${OD_VERIFY_REQUIRE_DEBUGGER:-0}" == 1 ]]; then
  echo '==> Checking debugger launch and breakpoint stop...'
  [[ -f "$payload/AddIns/Debugger/SharpDbg.Cli.dll" ]] || fail 'debugger add-in is missing SharpDbg.Cli.dll'
  debug_solution="$repo_root/tests/fixtures/DebugTestApp/DebugTestApp.slnx"
  debug_project="$repo_root/tests/fixtures/DebugTestApp/DebugTestApp.csproj"
  debug_source="$repo_root/tests/fixtures/DebugTestApp/Program.cs"
  debug_line=$(awk '/var message = ComputeGreeting\("World"\);/ { print NR; exit }' "$debug_source")
  [[ -n "$debug_line" ]] || fail 'debug fixture breakpoint source line was not found'
  if [[ ! -x "$repo_root/tests/fixtures/DebugTestApp/bin/Debug/net10.0/DebugTestApp" ]]; then
    echo '==> Building the debug fixture the check launches...'
    dotnet build "$debug_project" -c Debug -v q -nologo || fail 'the debug fixture did not build'
  fi
  debug_binary="$repo_root/tests/fixtures/DebugTestApp/bin/Debug/net10.0/DebugTestApp"
  invoke od.open-solution "$debug_solution" | jq -e '.success == true' >/dev/null || fail 'debug fixture solution did not open'
  invoke od.debug.clear-breakpoints >/dev/null || fail 'could not clear breakpoints'
  invoke od.debug.set-breakpoint "$debug_source" "$debug_line" | jq -e --argjson line "$debug_line" '.success == true and (.lines | index($line)) != null' >/dev/null \
    || fail 'the debugger breakpoint was not set'
  started=$(invoke od.debug.start "$debug_project" true 60) || fail 'the debugger did not start'
  jq -e --argjson line "$debug_line" '.started == true and .stopped == true and .currentLine == $line' <<<"$started" >/dev/null \
    || fail "the debugger did not stop at the fixture breakpoint: $started"
  invoke od.debug.stop >/dev/null || true
else
  echo '==> Skipping the debugger journey (set OD_VERIFY_REQUIRE_DEBUGGER=1 to enforce it).'
fi

echo 'Packaged Linux payload verification passed.'
