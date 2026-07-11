#!/usr/bin/env bash
#
# dotnet-env.sh — shared dotnet/MSBuild environment setup, sourced by repack-librewpf.sh,
# launch.sh, and rebuild-all.sh.
#
# Shared setup for the dotnet/MSBuild host used by LibreWPF packing and OpenDevelop build/run.
# LibreWPF now supports net10.0, so the normal system .NET 10 SDK is sufficient. Keeping the
# environment derivation here avoids drift between direct Homebrew installs and bundled dotnet
# layouts.
#
# Usage:  source dotnet-env.sh ; setup_dotnet_env /path/to/dotnet

setup_dotnet_env() {
  local dotnet="$1"
  if [[ -z "${dotnet}" || ! -x "${dotnet}" ]]; then
    echo "setup_dotnet_env: dotnet host '${dotnet}' not found or not executable" >&2
    return 1
  fi

  local dotnet_bin_dir
  dotnet_bin_dir="$(dirname "${dotnet}")"

  # Homebrew's dotnet is a bin/dotnet symlink whose real SDK/runtime tree lives in a sibling
  # libexec/ dir; bundled dotnet layouts have sdk/ directly under the binary's own dir. Detect
  # which layout this host uses instead of assuming either.
  if [[ -d "${dotnet_bin_dir}/sdk" ]]; then
    export DOTNET_ROOT="${dotnet_bin_dir}"
  elif [[ -d "${dotnet_bin_dir}/../libexec/sdk" ]]; then
    export DOTNET_ROOT="$(cd "${dotnet_bin_dir}/../libexec" && pwd)"
  else
    echo "setup_dotnet_env: cannot locate an 'sdk' dir for host '${dotnet}'" >&2
    return 1
  fi

  export DOTNET_HOST_PATH="${dotnet}"

  local sdk_dir
  sdk_dir="$(find "${DOTNET_ROOT}/sdk" -mindepth 1 -maxdepth 1 -type d | sort | tail -n 1)"
  export MSBuildSDKsPath="${sdk_dir}/Sdks"
  export MSBuildExtensionsPath="${sdk_dir}"
  export MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET="${sdk_dir}/SdkResolvers"
  export MSBUILD_NUGET_PATH="${sdk_dir}"
  # In-process MSBuild hosting does not need workload resolution for these projects, and disabling
  # it avoids SDK resolver noise from optional workload manifests.
  export MSBuildEnableWorkloadResolver=false
}
