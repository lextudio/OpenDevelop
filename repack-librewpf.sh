#!/usr/bin/env bash
#
# Repack LibreWPF into the local feed, then refresh OpenDevelop's view of it.
#
# The build-and-pack half is NOT implemented here: openavalon/dist.local.sh owns the whole
# package graph and is the only place that knows how the versions line up -
# LibreWPF.Transport/ProGPU/Sdk at $PROGPU_WPF_DEV_PACKAGE_VERSION, every ProGPU.* package and
# LibreWPF.Interop at $PROGPU_WPF_PROGPU_PACKAGE_VERSION, plus the canonical LibreWinForms
# integration - and it writes them to openavalon/artifacts/local-feed, the feed nuget.config
# maps as "librewpf-local".
#
# This script used to duplicate that build/pack logic with its own hardcoded "11.0.0-dev"
# version. Once the provider moved to the 0.1.0-preview.* scheme that silently produced a feed
# full of packages no OpenDevelop project consumes: the script "succeeded", nothing changed, and
# the mismatch only surfaced much later as an unexplained stale assembly. Delegating removes the
# second version scheme entirely.
#
# What is left here is the consumer-side half dist.local.sh deliberately does not do: drop the
# stale global-cache entries (NuGet keeps serving the first copy it ever extracted for a given
# version string) and re-restore OpenDevelop against the fresh packages.
#
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ "${1:-}" == "--fast" ]]; then
  # dist.local.sh has no partial mode; it is one coherent package graph. Say so rather than
  # quietly doing a full rebuild under a flag that promises a short one.
  echo "repack-librewpf.sh: --fast is no longer supported (openavalon/dist.local.sh builds the" >&2
  echo "  package graph as one unit). Running the full repack." >&2
  shift
fi

# The provider checkout: the sibling `openavalon` (it used to be called `openwpf`). OPENAVALON_ROOT
# wins, then LIBREWPF_ROOT's parent for callers that still set the old variable, then the siblings.
openavalon_root="${OPENAVALON_ROOT:-}"
if [[ -z "${openavalon_root}" && -n "${LIBREWPF_ROOT:-}" && -f "${LIBREWPF_ROOT}/../dist.local.sh" ]]; then
  openavalon_root="${LIBREWPF_ROOT}/.."
fi
if [[ -z "${openavalon_root}" ]]; then
  for _candidate in "${repo_root}/../openavalon" "${repo_root}/../openwpf"; do
    if [[ -f "${_candidate}/dist.local.sh" ]]; then openavalon_root="${_candidate}"; break; fi
  done
fi
if [[ -z "${openavalon_root}" || ! -f "${openavalon_root}/dist.local.sh" ]]; then
  echo "repack-librewpf.sh: cannot find openavalon/dist.local.sh; set OPENAVALON_ROOT to the" >&2
  echo "  openavalon checkout (the one containing dist.local.sh)." >&2
  exit 1
fi
openavalon_root="$(cd "${openavalon_root}" && pwd)"

cd "${repo_root}"

# Run the provider FIRST, in a clean environment. dist.local.sh drives LibreWPF's own pinned
# .dotnet (an 11.0-preview SDK) for the native/transport graph, and dotnet-env.sh exports
# DOTNET_ROOT/MSBuildSDKsPath for the system 10.x SDK - pointing those at the wrong SDK is the
# same interference launch.ps1 calls out. Set the OpenDevelop environment up afterwards, for the
# restore only.
echo "==> Building the local feed via ${openavalon_root}/dist.local.sh ..."
bash "${openavalon_root}/dist.local.sh"

dotnet="$(readlink -f "$(command -v dotnet)")"
source "${repo_root}/dotnet-env.sh"
setup_dotnet_env "${dotnet}"

# Clearing by package ID (not by "<id>/<version>") keeps this correct across version bumps: the
# provider decides the version, and whatever it just wrote is what the next restore must extract.
echo "==> Dropping stale LibreWPF/ProGPU entries from the global package cache ..."
nuget_root="${NUGET_PACKAGES:-${HOME}/.nuget/packages}"
if [[ -d "${nuget_root}" ]]; then
  for _dir in "${nuget_root}"/librewpf.* "${nuget_root}"/progpu.*; do
    [[ -d "${_dir}" ]] && rm -rf "${_dir}"
  done
fi

# Re-extract the freshly-packed (and just cache-cleared) packages into OpenDevelop's restore
# graph. launch.sh/rebuild-all.sh would re-pull via their own implicit restore, but doing it here
# keeps `repack-librewpf.sh` correct when run standalone.
echo "==> Restoring OpenDevelop against the fresh feed ..."
"${dotnet}" restore OpenDevelop.Mvp.slnx --force --no-cache
