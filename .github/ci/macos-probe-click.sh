#!/usr/bin/env bash
mkdir -p "$RUNNER_TEMP/od-test-logs"
swiftc -o "$RUNNER_TEMP/click-probe" "$GITHUB_WORKSPACE/.github/ci/macos-click-probe.swift"
log="$RUNNER_TEMP/od-test-logs/click-probe.log"
"$RUNNER_TEMP/click-probe" > "$log" 2>&1 &
probe=$!
for _ in $(seq 1 60); do grep -q READY "$log" && break; sleep 0.5; done
cat "$log"
read -r _ x y _ < <(grep -m1 READY "$log" || echo "READY 400 400")
echo "frontmost: $(lsappinfo info -only name "$(lsappinfo front)" 2>&1 | head -1)"
screencapture -x "$RUNNER_TEMP/od-test-logs/click-probe-before.png" 2>&1 || true
for attempt in "c:$x,$y" "m:$x,$y dd:$x,$y du:$x,$y"; do
  grep -q DOWN "$log" && break
  echo "cliclick $attempt"; cliclick $attempt 2>&1 || true
  for _ in $(seq 1 12); do grep -q DOWN "$log" && break; sleep 0.25; done
done
screencapture -x "$RUNNER_TEMP/od-test-logs/click-probe-after.png" 2>&1 || true
cat "$log"
grep -q DOWN "$log" && echo "RESULT: the click reached the probe window" || echo "RESULT: NO mouse-down reached the probe window"
kill "$probe" 2>/dev/null || true
