#!/usr/bin/env bash
#
# launch.sh — build the latest OpenDevelop and run it.
#
# Usage:
#   ./launch.sh                build OpenDevelop.Mvp.sln, then run OpenDevelop
#   ./launch.sh --no-build     skip the build, just (re)run the last build output
#   ./launch.sh --build-only   build but do NOT launch (used by rebuild-all.sh --build-only and
#                              by the integration tests, which start their own app instance)
#   DEVFLOW_DISABLE=1 ./launch.sh   run without the DevFlow debugging agent
#
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
sln="${repo_root}/OpenDevelop.Mvp.slnx"
exe_project="${repo_root}/src/Main/SharpDevelop/SharpDevelop.csproj"

do_build=1
do_run=1
case "${1:-}" in
  --no-build)   do_build=0 ;;
  --build-only) do_run=0 ;;
  "")           ;;
  *) echo "launch.sh: unknown flag '${1}'" >&2; exit 2 ;;
esac

# OpenDevelop and LibreWPF both target net10.0/net10.0-windows now, so the system .NET 10 SDK
# builds and runs the app.
dotnet="$(readlink -f "$(command -v dotnet)")"
source "${repo_root}/dotnet-env.sh"
setup_dotnet_env "${dotnet}"

# Kill any previously running instance so DevFlow's port (9223) is free and we
# don't end up staring at a stale window.
pkill -f "OpenDevelop.dll" 2>/dev/null || true
sleep 1

if [[ "${do_build}" -eq 1 ]]; then
  # Several AddIn projects (UnitTesting, Debugger.AddIn, ...) build directly INTO this shared
  # repo-root AddIns/<Category>/<Name> tree via their own <OutputPath> (an old-style SharpDevelop
  # convention, not a per-project bin folder), and SharpDevelop.csproj's DeployAddInsToRepoRoot
  # target copies the two top-level *.addin files here too. A normal incremental build only adds/
  # updates files - it never removes ones an addin project stopped producing (a renamed .addin
  # fragment, a deleted helper .dll, a dropped satellite-resource culture folder) - so this
  # directory silently accumulates leftovers from earlier revisions of whatever addin you're
  # actively reworking, and AddInTree loads whatever it finds here at startup, indiscriminately.
  # Wipe it before every full build so only what the CURRENT project set actually produces is
  # ever present. Skipped under --no-build, since nothing would repopulate it there.
  echo "==> Clearing AddIns/ to drop stale output from previous builds..."
  rm -rf "${repo_root}/AddIns"

  echo "==> Building OpenDevelop.Mvp.sln..."
  "${dotnet}" build "${sln}" -v minimal

  # Microsoft.Build.Runtime 18.0.2 copies MSBuild .targets/.props files to every
  # project's output directory via contentFiles/CopyToOutputDirectory=PreserveNewest.
  # These stale copies confuse SharpDevelop's in-process MSBuild evaluation, which
  # can load the wrong Microsoft.Common.CrossTargeting.targets and mis-resolve
  # $(MSBuildToolsPath) to the output directory instead of the SDK directory.
  # Remove them after build so only the SDK's own versions are visible.
  find "${repo_root}/src" -path "*/bin/Debug/*" \( -name "*.targets" -o -name "*.props" \) \
    ! -name "*.dll" ! -name "*.exe" -delete 2>/dev/null || true
else
  echo "==> Skipping build (--no-build)."
fi

if [[ "${do_run}" -eq 0 ]]; then
  echo "==> Build only (--build-only); not launching."
  exit 0
fi

echo "==> Launching OpenDevelop..."
exec "${dotnet}" run --project "${exe_project}" --no-build
