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
dotnet="/Users/lextm/uno-tools/librewpf/.dotnet/dotnet"
sln="${repo_root}/OpenDevelop.Mvp.slnx"
exe_project="${repo_root}/src/Main/SharpDevelop/SharpDevelop.csproj"
export DOTNET_ROOT="$(dirname "${dotnet}")"
export DOTNET_HOST_PATH="${dotnet}"
sdk_dir="$(find "${DOTNET_ROOT}/sdk" -mindepth 1 -maxdepth 1 -type d | sort | tail -n 1)"
export MSBuildSDKsPath="${sdk_dir}/Sdks"
export MSBuildExtensionsPath="${sdk_dir}"
export MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET="${sdk_dir}/SdkResolvers"
export MSBUILD_NUGET_PATH="${sdk_dir}"
# The bundled preview SDK's workload manifest/resolver setup only works through the `dotnet` CLI
# muxer; SharpDevelop's in-process MSBuild hosting (used to evaluate opened projects) doesn't get
# that and intermittently fails project loads with "ProjectLoadException: The SDK
# 'Microsoft.NET.SDK.WorkloadAutoImportPropsLocator' specified could not be found." Not needed for
# plain console/class-library projects.
export MSBuildEnableWorkloadResolver=false

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
