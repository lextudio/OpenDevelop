#!/usr/bin/env bash
#
# What a hosted Ubuntu runner needs before OpenDevelop can build or start on it. Shared by ci.yml (Linux build and
# lanes) and release.yml (Linux packaging), so the two never disagree about the Linux environment.
#
#   bash .github/ci/install-linux-prerequisites.sh [--reclaim-disk] [--display]
#
# --reclaim-disk  The x64 payload build once died with the runner's own "No space left on device" while the arm64 job on
#                 the same commit passed: the image's free space, not the product. The images ship multi-gigabyte caches
#                 no build here uses; drop them and print the numbers so a future space problem is visible.
# --display       Xvfb and the native libraries the IDE needs to open a window. The hosted runners have no GPU, and
#                 wgpu-native needs a graphics backend to create the window surface: without a Vulkan loader and a
#                 software driver (Mesa's lavapipe) it has no instance at all and aborts with SIGABRT (status 134) at
#                 wgpuInstanceCreateSurface.
set -euo pipefail

reclaim=0
display=0
for argument in "$@"; do
  case "$argument" in
    --reclaim-disk) reclaim=1 ;;
    --display) display=1 ;;
    *) echo "unknown option: $argument" >&2; exit 2 ;;
  esac
done

if [[ "$reclaim" == 1 ]]; then
  df -h /
  sudo rm -rf /usr/local/lib/android /opt/ghc /opt/hostedtoolcache/CodeQL || true
  sudo apt-get clean || true
  df -h /
fi

if [[ "$display" == 1 ]]; then
  sudo apt-get update
  sudo apt-get install -y xvfb libgtk-3-0 libx11-xcb1 libxkbcommon0 libgl1 libvulkan1 mesa-vulkan-drivers
fi
