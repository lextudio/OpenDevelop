#!/usr/bin/env bash
#
# launch.sh — build the latest OpenDevelop (SharpDevelop.exe) and run it.
#
# Usage:
#   ./launch.sh              build OpenDevelop.Mvp.sln, then run SharpDevelop.exe
#   ./launch.sh --no-build   skip the build, just (re)run the last build output
#   DEVFLOW_DISABLE=1 ./launch.sh   run without the DevFlow debugging agent
#
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet="/Users/lextm/uno-tools/wpf-progpu/.dotnet/dotnet"
sln="${repo_root}/OpenDevelop.Mvp.sln"
exe_project="${repo_root}/src/Main/SharpDevelop/SharpDevelop.csproj"

# Kill any previously running instance so DevFlow's port (9223) is free and we
# don't end up staring at a stale window.
pkill -f "SharpDevelop.dll" 2>/dev/null || true
sleep 1

if [[ "${1:-}" != "--no-build" ]]; then
  echo "==> Building OpenDevelop.Mvp.sln..."
  "${dotnet}" build "${sln}" -v minimal
else
  echo "==> Skipping build (--no-build)."
fi

echo "==> Launching SharpDevelop..."
exec "${dotnet}" run --project "${exe_project}" --no-build
