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

fail() {
  echo "::error::macOS bundle verification: $*" >&2
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
}
trap cleanup EXIT

[[ -d "$app_path" ]] || fail "bundle does not exist: $app_path"
[[ -f "$app_path/Contents/MacOS/OpenDevelop.dll" ]] || fail "bundle has no OpenDevelop.dll"
command -v curl >/dev/null || fail 'curl is required'
command -v jq >/dev/null || fail 'jq is required'

base_url="http://127.0.0.1:$port"

claim_lease() {
  curl --fail --silent --show-error --max-time 10 \
    -X POST "$base_url/api/v1/agent/lease" \
    -H 'Content-Type: application/json' \
    --data "$(jq -nc --arg id "$lease_id" '{action:"claim", leaseId:$id, holderKind:"release-bundle-check", label:"macOS release bundle verification", force:true}')" \
    | jq -e '.allowed == true' >/dev/null
}

# Prints the parsed inner JSON returned by a DevFlow action.  Claim immediately
# before every request because the agent deliberately uses a short mutation
# lease, which prevents a slow earlier check from invalidating the next one.
invoke() {
  local action=$1
  shift
  local payload response
  payload=$(jq -nc '$ARGS.positional' --args "$@")
  claim_lease
  response=$(curl --fail --silent --show-error --max-time 90 \
    -X POST "$base_url/api/v1/invoke/actions/$action" \
    -H 'Content-Type: application/json' \
    -H "X-DevFlow-Lease: $lease_id" \
    --data "$(jq -nc --argjson args "$payload" '{args:$args}')") || fail "$action request failed"
  jq -e '.success == true' <<<"$response" >/dev/null || fail "$action returned an unsuccessful envelope: $response"
  jq -er '.returnValue | fromjson' <<<"$response"
}

echo '==> Starting the packaged OpenDevelop app with DevFlow enabled...'
DEVFLOW_ENABLE=1 DEVFLOW_AGENT_PORT="$port" open -n "$app_path" --args "-devflow:$port" "-configdir:$config_dir"
agent_started=0
for _ in $(seq 1 45); do
  if status=$(curl --silent --show-error --fail --max-time 2 "$base_url/api/v1/agent/status" 2>/dev/null) \
    && jq -e '.running == true' <<<"$status" >/dev/null; then
    agent_started=1
    break
  fi
  sleep 2
done
[[ "$agent_started" == 1 ]] || fail 'DevFlow agent did not start'
app_pid=$(pgrep -nf "${app_path}/Contents/MacOS/OpenDevelop.*-devflow:$port" || true)
[[ -n "$app_pid" ]] || fail 'could not identify the launched app process'

# The agent listens before the docking workbench has finished constructing.
# Require two successful workbench-backed reads before driving add-ins.
invoke od.active-view >/dev/null
sleep 2
invoke od.active-view >/dev/null

echo '==> Checking solution loading, Roslyn and build...'
solution="$repo_root/tests/fixtures/SolutionExplorerFixture/SolutionExplorerFixture.slnx"
invoke od.open-solution "$solution" | jq -e --arg solution "$solution" '.success == true and .currentSolution == $solution' >/dev/null \
  || fail 'solution fixture did not open'
invoke od.build-solution | jq -e '.success == true and .result == "Success" and .errorCount == 0' >/dev/null \
  || fail 'solution fixture did not build cleanly'

echo '==> Checking terminal shell, output, and both split directions...'
terminal=$(invoke od.terminal.new /bin/sh)
printf '%s' "$terminal" | jq -e '.success == true and .pid > 0' >/dev/null || fail 'terminal shell did not start'
invoke od.terminal.send $'printf \'bundle-terminal-ok\\n\'\r' | jq -e '.success == true' >/dev/null || fail 'terminal input failed'
for _ in $(seq 1 25); do
  terminal_text=$(invoke od.terminal.text)
  jq -e '.text | contains("bundle-terminal-ok")' <<<"$terminal_text" >/dev/null && break
  sleep 1
done
jq -e '.text | contains("bundle-terminal-ok")' <<<"$terminal_text" >/dev/null || fail 'terminal did not render shell output'
invoke od.terminal.split | jq -e '.success == true' >/dev/null || fail 'horizontal terminal split failed'
invoke od.terminal.split-vertical | jq -e '.success == true' >/dev/null || fail 'vertical terminal split failed'
invoke od.terminal.list | jq -e '.sessions | length >= 3' >/dev/null || fail 'terminal splits did not create three sessions'

echo '==> Checking the hosted ILSpy add-in...'
invoke od.ilspy.open-assembly "$app_path/Contents/MacOS/OpenDevelop.dll" | jq -e '.opened == true' >/dev/null \
  || fail 'ILSpy could not open the packaged assembly'
invoke od.ilspy.status | jq -e '([.loadedAssemblies[]] | index("OpenDevelop")) != null and (.panes | length >= 3)' >/dev/null \
  || fail 'ILSpy panes or decompiler dependencies are unavailable'

echo '==> Checking the LibreWPF designer child host and its rendered frame...'
wpf_solution="$repo_root/tests/fixtures/WpfHotReloadFixture/WpfHotReloadFixture.slnx"
wpf_xaml="$repo_root/externals/vscode-wpf/sample/net6.0/MainWindow.xaml"
[[ -f "$wpf_xaml" ]] || fail "linked WPF fixture source is missing: $wpf_xaml"
invoke od.open-solution "$wpf_solution" | jq -e '.success == true' >/dev/null || fail 'WPF fixture solution did not open'
invoke od.open-file "$wpf_xaml" | jq -e '.opened == true' >/dev/null || fail 'WPF fixture XAML did not open'
invoke od.wpf-designer.activate-design | jq -e '.success == true' >/dev/null || fail 'WPF design view did not activate'
for _ in $(seq 1 30); do
  wpf_status=$(invoke od.wpf-designer.status)
  jq -e '.backend == "LibreWPF" and .hostAlive == true and .designerLoaded == true and .hasRenderFrame == true and .rootItemType == "Window"' <<<"$wpf_status" >/dev/null && break
  sleep 2
done
jq -e '.backend == "LibreWPF" and .hostAlive == true and .designerLoaded == true and .hasRenderFrame == true and .rootItemType == "Window"' <<<"$wpf_status" >/dev/null \
  || fail "WPF designer did not load a rendered frame: $wpf_status"

echo '==> Checking debugger launch and breakpoint stop...'
debugger_addin="$app_path/Contents/MacOS/AddIns/Debugger"
[[ -f "$debugger_addin/SharpDbg.Cli.dll" ]] || fail 'debugger add-in is missing SharpDbg.Cli.dll'
darwin_major=$(uname -r | cut -d. -f1)
if [[ "${OD_BUNDLE_VERIFY_REQUIRE_DEBUGGER:-0}" == 1 || "$darwin_major" -lt 27 ]]; then
  debug_solution="$repo_root/tests/fixtures/DebugTestApp/DebugTestApp.slnx"
  debug_project="$repo_root/tests/fixtures/DebugTestApp/DebugTestApp.csproj"
  debug_source="$repo_root/tests/fixtures/DebugTestApp/Program.cs"
  debug_line=$(awk '/var message = ComputeGreeting\("World"\);/ { print NR; exit }' "$debug_source")
  [[ -n "$debug_line" ]] || fail 'debug fixture breakpoint source line was not found'
  invoke od.open-solution "$debug_solution" | jq -e '.success == true' >/dev/null || fail 'debug fixture solution did not open'
  invoke od.debug.clear-breakpoints >/dev/null
  invoke od.debug.set-breakpoint "$debug_source" "$debug_line" | jq -e --argjson line "$debug_line" '.success == true and (.lines | index($line)) != null' >/dev/null \
    || fail 'debug breakpoint was not set'
  debug_started=0
  if debug_start=$(invoke od.debug.start "$debug_project" true 45); then
    if jq -e --argjson line "$debug_line" '.started == true and .stopped == true and .currentLine == $line' <<<"$debug_start" >/dev/null; then
      debug_started=1
    fi
  fi
  debug_output=$(invoke od.debug.output || true)
  invoke od.debug.stop >/dev/null || true
  [[ "$debug_started" == 1 ]] || fail "debugger did not stop at the fixture breakpoint: ${debug_start:-no result}; output: ${debug_output:-unavailable}"
else
  # macOS 27 presently regresses CoreCLR's debugger-startup semaphore handshake. The
  # release workflow forces this branch on its macOS 15 runner; developers on the
  # affected host still verify that the packaged adapter and every other path load.
  # https://github.com/dotnet/runtime/issues/116545
  echo "::warning::Skipping live debugger assertion on Darwin $darwin_major; set OD_BUNDLE_VERIFY_REQUIRE_DEBUGGER=1 to enforce it."
fi

echo 'Packaged macOS feature verification passed.'
