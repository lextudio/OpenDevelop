#!/usr/bin/env bash
mkdir -p "$RUNNER_TEMP/od-test-logs"
swiftc -o "$RUNNER_TEMP/window-list" "$GITHUB_WORKSPACE/.github/ci/macos-window-list.swift"
nohup bash -c 'n=0; while true; do { echo "== $(date +%H:%M:%S)"; "$0"; } >> "$1"; if [ $((n % 3)) -eq 0 ]; then screencapture -x "$2/macos-$(date +%H%M%S).png"; fi; n=$((n + 1)); sleep 10; done' "$RUNNER_TEMP/window-list" "$RUNNER_TEMP/od-test-logs/macos-windows.log" "$RUNNER_TEMP/od-test-logs" > /dev/null 2>&1 &
disown
sleep 2
cat "$RUNNER_TEMP/od-test-logs/macos-windows.log"
