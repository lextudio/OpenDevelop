#!/usr/bin/env bash
#
# The journeys every packaged-payload check runs, shared by build/macos/verify-application-bundle.sh and
# build/linux/verify-application-payload.sh (source it; it is not an entry point).  The Windows check
# (build/windows/verify-packaged-app.ps1) drives the same DevFlow actions in the same order, so a journey added or
# tightened here belongs there too.  Only what differs per platform stays in the callers: how the app is launched,
# diagnostics on failure, and cleanup.
#
# The caller defines, before sourcing: repo_root, base_url, lease_id, verify_label (a short name for the lease), and a
# fail() that reports and exits.  It may define agent_exited(), a predicate that is true once the app process has died.

claim_lease() {
  local lease
  # A lease request that cannot reach the agent is the first sign the application is gone; route it through fail() so
  # that becomes a dump of the process/log rather than a bare curl exit code.
  if ! lease=$(curl --fail --silent --show-error --max-time 10 \
      -X POST "$base_url/api/v1/agent/lease" \
      -H 'Content-Type: application/json' \
      --data "$(jq -nc --arg id "$lease_id" --arg label "$verify_label" '{action:"claim", leaseId:$id, holderKind:"release-payload-check", label:$label, force:true}')"); then
    fail 'could not claim a DevFlow lease (the application may have exited)'
  fi
  jq -e '.allowed == true' <<<"$lease" >/dev/null || fail "the DevFlow agent refused the lease: $lease"
}

# Prints the parsed inner JSON returned by a DevFlow action.  Claim immediately before every request because the agent
# deliberately uses a short mutation lease, which prevents a slow earlier check from invalidating the next one.
invoke() {
  local action=$1
  shift
  local args response
  args=$(jq -nc '$ARGS.positional' --args "$@")
  claim_lease
  response=$(curl --fail --silent --show-error --max-time 120 \
    -X POST "$base_url/api/v1/invoke/actions/$action" \
    -H 'Content-Type: application/json' \
    -H "X-DevFlow-Lease: $lease_id" \
    --data "$(jq -nc --argjson args "$args" '{args:$args}')") || fail "$action request failed"
  jq -e '.success == true' <<<"$response" >/dev/null || fail "$action returned an unsuccessful envelope: $response"
  jq -er '.returnValue | fromjson' <<<"$response"
}

# wait_for_agent <attempts>: polls the status endpoint every 2 s, then requires two workbench-backed reads, because the
# agent listens before the docking workbench has finished constructing.
wait_for_agent() {
  local attempts=$1 status
  for _ in $(seq 1 "$attempts"); do
    if status=$(curl --silent --show-error --fail --max-time 2 "$base_url/api/v1/agent/status" 2>/dev/null) \
      && jq -e '.running == true' <<<"$status" >/dev/null; then
      invoke od.active-view >/dev/null
      sleep 2
      invoke od.active-view >/dev/null
      return 0
    fi
    if declare -F agent_exited >/dev/null && agent_exited; then fail 'the application exited before its DevFlow agent answered'; fi
    sleep 2
  done
  fail 'DevFlow agent did not start'
}

verify_solution_build() {
  echo '==> Checking solution loading and build...'
  local solution="$repo_root/tests/fixtures/SolutionExplorerFixture/SolutionExplorerFixture.slnx"
  invoke od.open-solution "$solution" | jq -e --arg solution "$solution" '.success == true and .currentSolution == $solution' >/dev/null \
    || fail 'solution fixture did not open'
  invoke od.build-solution | jq -e '.success == true and .result == "Success" and .errorCount == 0' >/dev/null \
    || fail 'solution fixture did not build cleanly'
}

verify_roslyn() {
  echo '==> Checking the packaged Roslyn child host and project workspace...'
  local csharp_source="$repo_root/tests/fixtures/SolutionExplorerFixture/SampleApp/Program.cs" roslyn_status=''
  invoke od.open-file "$csharp_source" | jq -e '.opened == true' >/dev/null || fail 'C# fixture source did not open'
  for _ in $(seq 1 30); do
    roslyn_status=$(invoke od.language-workspace.status "$csharp_source")
    jq -e '.supported == true and .tracked == true and .metadataReferenceCount > 0' <<<"$roslyn_status" >/dev/null && break
    sleep 2
  done
  jq -e '.supported == true and .tracked == true and .metadataReferenceCount > 0' <<<"$roslyn_status" >/dev/null \
    || fail "Roslyn child host did not provide a project workspace: $roslyn_status"
  invoke od.roslyn-legacy-workspace.status | jq -e '.remoteHostMode == true and .workspaceCreated == false' >/dev/null \
    || fail 'Roslyn fell back to the deprecated in-process workspace'
}

verify_terminal() {
  echo '==> Checking terminal shell, output, and both split directions...'
  local terminal terminal_text=''
  terminal=$(invoke od.terminal.new /bin/sh)
  jq -e '.success == true and .pid > 0' <<<"$terminal" >/dev/null || fail 'terminal shell did not start'
  # The marker is assembled by the shell: the echoed command line contains "terminal-%s", only the output contains
  # "terminal-ok", so the check cannot pass on the typed input alone.
  invoke od.terminal.send $'printf \'terminal-%s\\n\' ok\r' | jq -e '.success == true' >/dev/null || fail 'terminal input failed'
  for _ in $(seq 1 25); do
    terminal_text=$(invoke od.terminal.text)
    jq -e '.text | contains("terminal-ok")' <<<"$terminal_text" >/dev/null && break
    sleep 1
  done
  jq -e '.text | contains("terminal-ok")' <<<"$terminal_text" >/dev/null || fail 'terminal did not render shell output'
  invoke od.terminal.split | jq -e '.success == true' >/dev/null || fail 'horizontal terminal split failed'
  invoke od.terminal.split-vertical | jq -e '.success == true' >/dev/null || fail 'vertical terminal split failed'
  invoke od.terminal.list | jq -e '.sessions | length >= 3' >/dev/null || fail 'terminal splits did not create three sessions'
}

# verify_ilspy <path to the packaged OpenDevelop.dll>
verify_ilspy() {
  echo '==> Checking the hosted ILSpy add-in...'
  invoke od.ilspy.open-assembly "$1" | jq -e '.opened == true' >/dev/null || fail 'ILSpy could not open the packaged assembly'
  invoke od.ilspy.status | jq -e '([.loadedAssemblies[]] | index("OpenDevelop")) != null and (.panes | length >= 3)' >/dev/null \
    || fail 'ILSpy panes or decompiler dependencies are unavailable'
}

verify_wpf_designer() {
  echo '==> Checking the LibreWPF designer child host and its rendered frame...'
  local wpf_solution="$repo_root/tests/fixtures/WpfHotReloadFixture/WpfHotReloadFixture.slnx"
  local wpf_xaml="$repo_root/externals/vscode-wpf/sample/net6.0/MainWindow.xaml" xaml_outline='' wpf_status=''
  [[ -f "$wpf_xaml" ]] || fail "linked WPF fixture source is missing: $wpf_xaml"
  invoke od.open-solution "$wpf_solution" | jq -e '.success == true' >/dev/null || fail 'WPF fixture solution did not open'
  invoke od.open-file "$wpf_xaml" | jq -e '.opened == true' >/dev/null || fail 'WPF fixture XAML did not open'
  invoke od.xaml.language-server "$wpf_xaml" | jq -e --arg workspace "$(dirname "$wpf_solution")" \
    '.success == true and .key == "LibreWpf" and .server == "librewpf-xaml-ls.dll" and .workspace == $workspace' >/dev/null \
    || fail 'XAML file was not routed to the packaged LibreWPF language server'
  for _ in $(seq 1 30); do
    xaml_outline=$(invoke od.xaml-outline.status)
    jq -e '.active == true and .rootChildCount > 0 and any(.outlineNames[]; startswith("Button [PrimaryButton]"))' <<<"$xaml_outline" >/dev/null && break
    sleep 2
  done
  jq -e '.active == true and .rootChildCount > 0 and any(.outlineNames[]; startswith("Button [PrimaryButton]"))' <<<"$xaml_outline" >/dev/null \
    || fail "LibreWPF XAML language server did not return the fixture outline: $xaml_outline"
  invoke od.wpf-designer.activate-design | jq -e '.success == true' >/dev/null || fail 'WPF design view did not activate'
  for _ in $(seq 1 30); do
    wpf_status=$(invoke od.wpf-designer.status)
    jq -e '.backend == "LibreWPF" and .hostAlive == true and .designerLoaded == true and .hasRenderFrame == true and .rootItemType == "Window"' <<<"$wpf_status" >/dev/null && break
    sleep 2
  done
  jq -e '.backend == "LibreWPF" and .hostAlive == true and .designerLoaded == true and .hasRenderFrame == true and .rootItemType == "Window"' <<<"$wpf_status" >/dev/null \
    || fail "WPF designer did not load a rendered frame: $wpf_status"
}

# verify_debugger <path to the packaged AddIns/Debugger> <require: 1 to run the live breakpoint journey, 0 to only check
# the adapter is packaged>.  The caller decides `require`: a platform whose runtime is known to break the debugger
# handshake may skip the live part, but the add-in's files are checked everywhere.
verify_debugger() {
  local debugger_addin=$1 require=$2
  echo '==> Checking debugger launch and breakpoint stop...'
  [[ -f "$debugger_addin/SharpDbg.Cli.dll" ]] || fail 'debugger add-in is missing SharpDbg.Cli.dll'
  # The adapter's DAP transport is the MIT OmniSharp fork. Microsoft's debug protocol package is under a license that cannot be
  # redistributed, and a stale development output folder can still hold a copy (the copy targets never delete).
  [[ -z "$(find "$debugger_addin" -name 'Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.dll' -print -quit)" ]] || fail 'debugger add-in ships Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.dll (not redistributable; the adapter uses the OmniSharp fork)'
  for abi in arm64-v8a x86_64 armeabi-v7a; do
    [[ -f "$debugger_addin/android/$abi/libSharpDbg.AndroidAgent.so" ]] || fail "debugger add-in is missing the Android agent for $abi"
    for runtime in dotnet android; do
      [[ -f "$debugger_addin/android/$abi/licenses/$runtime/LICENSE.TXT" && -f "$debugger_addin/android/$abi/licenses/$runtime/THIRD-PARTY-NOTICES.TXT" ]] || fail "Android agent $abi is missing $runtime runtime notices"
    done
  done
  if [[ "$require" != 1 ]]; then
    echo '::warning::Skipping the live debugger assertion (set OD_VERIFY_REQUIRE_DEBUGGER=1 to enforce it).'
    return 0
  fi
  local fixture="$repo_root/tests/fixtures/DebugTestApp" debug_line debug_start debug_output debug_started=0
  debug_line=$(awk '/var message = ComputeGreeting\("World"\);/ { print NR; exit }' "$fixture/Program.cs")
  [[ -n "$debug_line" ]] || fail 'debug fixture breakpoint source line was not found'
  # The check starts the fixture binary, not the project, so a checkout that never built the test tree has nothing to
  # run ("DebugTestApp does not exist and cannot be started").
  if [[ ! -x "$fixture/bin/Debug/net10.0/DebugTestApp" ]]; then
    echo '==> Building the debug fixture the check launches...'
    dotnet build "$fixture/DebugTestApp.csproj" -c Debug -v q -nologo || fail 'the debug fixture did not build'
  fi
  invoke od.open-solution "$fixture/DebugTestApp.slnx" | jq -e '.success == true' >/dev/null || fail 'debug fixture solution did not open'
  invoke od.debug.clear-breakpoints >/dev/null || fail 'could not clear breakpoints'
  invoke od.debug.set-breakpoint "$fixture/Program.cs" "$debug_line" | jq -e --argjson line "$debug_line" '.success == true and (.lines | index($line)) != null' >/dev/null \
    || fail 'debug breakpoint was not set'
  if debug_start=$(invoke od.debug.start "$fixture/DebugTestApp.csproj" true 60); then
    if jq -e --argjson line "$debug_line" '.started == true and .stopped == true and .currentLine == $line' <<<"$debug_start" >/dev/null; then
      debug_started=1
    fi
  fi
  debug_output=$(invoke od.debug.output || true)
  invoke od.debug.stop >/dev/null || true
  [[ "$debug_started" == 1 ]] || fail "debugger did not stop at the fixture breakpoint: ${debug_start:-no result}; output: ${debug_output:-unavailable}"
}
