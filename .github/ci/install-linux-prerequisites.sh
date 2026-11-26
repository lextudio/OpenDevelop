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
  # The GTK designer needs the GTK 4/libadwaita runtime, the development packages' XML GIR
  # metadata to validate and instantiate controls, glib-compile-resources for project .gresource
  # manifests, and Blueprint's compiler for the .blp compile/decompile/language-server journey.
  # ubuntu-latest happens to include most of these, but the native Ubuntu ARM64 image does not;
  # install the complete toolchain explicitly so designer lanes do not depend on image drift.
  # libglfw3: the LibreWinForms designer child (FormsDesigner.Host) creates its windows with Silk.NET over GLFW. It ships
  # its own libglfw.so.3, but GLFW dlopens the X11 client libraries (libXrandr, libXinerama, libXcursor, libXi, ...) at
  # init, and without them every window fails with "Couldn't find a suitable window platform (GlfwPlatform - not
  # applicable)". The system package pulls exactly those in; LibreWinForms' own CI installs it for the same reason.
  # xdotool, x11-utils (xwininfo): diagnostics only. When a native XTest press never reaches the IDE window, the
  # integration tests report the pointer position, the window under it and the X window tree
  # (OpenDevelopAppFixture.X11PointerDiagnostics).
  sudo apt-get install -y libglfw3 xvfb xdotool x11-utils libgtk-3-0 libgtk-4-1 libadwaita-1-0 libgtk-4-dev libadwaita-1-dev libglib2.0-dev-bin blueprint-compiler libx11-xcb1 libxkbcommon0 libgl1 libvulkan1 mesa-vulkan-drivers
fi
