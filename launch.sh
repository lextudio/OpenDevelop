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
dotnet_candidates=(
  "/usr/local/share/dotnet/dotnet"
  "/opt/homebrew/bin/dotnet"
)
dotnet=""
for c in "${dotnet_candidates[@]}"; do
  if [[ -x "${c}" ]]; then
    dotnet="${c}"
    break
  fi
done
if [[ -z "${dotnet}" ]]; then
  dotnet="$(command -v dotnet 2>/dev/null || true)"
fi
if [[ -z "${dotnet}" || ! -x "${dotnet}" ]]; then
  echo "launch.sh: cannot find dotnet (checked ${dotnet_candidates[*]} and PATH)" >&2
  exit 1
fi
dotnet="$(readlink -f "${dotnet}")"

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

  # Some upstream projects use packages.lock.json. NuGet packages can be re-signed or
  # republished by a feed without changing their ID/version; in that case the lock file's
  # old content hash causes NU1403 even after the global package cache is cleared.
  # Re-evaluate lock files against the configured feeds, then keep the actual build offline
  # from restore so every project uses that single, consistent dependency graph.
  echo "==> Restoring packages and refreshing package content hashes..."
  "${dotnet}" restore "${sln}" --force-evaluate -v minimal

  # AddIn projects write directly to the shared AddIns/ tree. Since it was removed above,
  # an incremental build is not sufficient: MSBuild may consider a project up-to-date based
  # on obj/ and skip recreating its shared output. --no-incremental forces all projects in
  # the solution to rebuild and therefore republishes every current addin file.
  echo "==> Rebuilding OpenDevelop.Mvp.sln and all addins..."
  "${dotnet}" build "${sln}" --no-restore --no-incremental -v minimal

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

# MSBuildSDKsPath and related overrides are only needed for SharpDevelop's
# in-process MSBuild hosting — they interfere with `dotnet build` (which
# respects global.json), so apply them only before launching.
source "${repo_root}/dotnet-env.sh"
setup_dotnet_env "${dotnet}"

echo "==> Launching OpenDevelop..."
exec "${dotnet}" run --project "${exe_project}" --no-build
