#!/usr/bin/env bash
mkdir -p "$RUNNER_TEMP/od-test-logs"
echo "user=$(id -un) console=$(stat -f %Su /dev/console)"; who || true
echo "frontmost: $(lsappinfo front 2>&1 | head -1) $(lsappinfo info -only name "$(lsappinfo front)" 2>&1 | head -1)"
echo "screen locked flag: $(ioreg -n Root -d1 -w0 2>/dev/null | grep -o 'CGSSessionScreenIsLocked[^,]*' | head -1)"
echo "cliclick position: $(cliclick p 2>&1)"
cliclick m:200,200 2>&1 || true; sleep 0.5
echo "cliclick position after m:200,200: $(cliclick p 2>&1)"
echo "System Events: $(osascript -e 'tell application "System Events" to count processes' 2>&1 | head -2)"
for db in "/Library/Application Support/com.apple.TCC/TCC.db" "$HOME/Library/Application Support/com.apple.TCC/TCC.db"; do
  echo "== $db"
  sudo sqlite3 "$db" "select service, client, auth_value from access where service in ('kTCCServiceAccessibility','kTCCServicePostEvent','kTCCServiceListenEvent','kTCCServiceScreenCapture')" 2>&1 | head -40
done
echo "SIP: $(csrutil status 2>&1)"
screencapture -x "$RUNNER_TEMP/od-test-logs/macos-desktop.png" 2>&1 || true
