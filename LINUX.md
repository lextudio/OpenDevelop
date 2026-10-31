Launching OpenDevelop on Linux
==============================

Linux support is new: the tar.xz is built and smoke-tested on x64 and ARM64 runners, but it is the
least exercised platform. If something below does not match your distribution, please open an
issue at https://github.com/lextudio/OpenDevelop/issues.

Extract the archive and run one of,

``` sh
tar -xf OpenDevelop-linux-x64.tar.xz
./OpenDevelop-linux-x64/OpenDevelop
```

or, from inside the extracted directory, `dotnet OpenDevelop.dll`.

`OpenDevelop` is a small shell wrapper around `dotnet OpenDevelop.dll`, so both forms need a .NET
runtime the program can find. The rest of this page lists what usually goes wrong first.

Missing .NET Runtime
--------------------

The payload is framework-dependent and needs the **.NET 10 runtime**. Check what is installed with
`dotnet --info`, then run the app directly to see which locations it searched:

``` sh
$ ./OpenDevelop
You must install .NET to run this application.

App: /home/user/Downloads/OpenDevelop-linux-x64/OpenDevelop.dll
Architecture: x64
App host version: 10.0.1
.NET location: Not found
```

Install the runtime from https://dotnet.microsoft.com/download/dotnet/10.0, or point the app at it:

``` sh
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
./OpenDevelop
```

Distributions that install .NET through Snap keep it under `/var/snap/dotnet/common/dotnet`, which
is not one of the locations the runtime host probes. A runtime installed with the official script
avoids that:

``` sh
curl -sSL https://dot.net/v1/dotnet-install.sh -o ~/dotnet-install.sh
chmod +x ~/dotnet-install.sh
~/dotnet-install.sh --channel 10.0 --runtime dotnet --install-dir ~/.dotnet

~/.dotnet/dotnet --info
~/.dotnet/dotnet ~/Downloads/OpenDevelop-linux-x64/OpenDevelop.dll
```

Missing native libraries
------------------------

The window and the docking layout need X11, xkbcommon, GLFW and OpenGL:

``` sh
sudo apt-get install -y libx11-xcb1 libxkbcommon0 libgl1
```

`libglfw.so.3` is shipped inside the archive (`runtimes/linux-x64/native/` and next to `OpenDevelop`)
because Silk.NET looks for it there. If GLFW still cannot be loaded, the error names the file it
tried; check with,

``` sh
ldd libglfw.so.3
```

No GPU adapter or a black window
--------------------------------

The windowing backend goes through wgpu, which needs a Vulkan driver. On a machine without a GPU -
a container, a VM, a CI runner - install the software driver:

``` sh
sudo apt-get install -y mesa-vulkan-drivers
```

For a headless machine, run under a virtual display:

``` sh
xvfb-run -a ./OpenDevelop
```

The GTK designer needs GTK 4
----------------------------

The GTK 4 designer renders its preview in an isolated child process that loads GTK 4. Without it the
designer reports that the host could not start, while the rest of the IDE keeps working:

``` sh
sudo apt-get install -y libgtk-4-1        # Debian/Ubuntu
sudo dnf install gtk4                     # Fedora
```

The classic WinForms designer is not available on Linux; its host is Windows-only.
