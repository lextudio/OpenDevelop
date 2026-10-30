![Downloads](https://img.shields.io/github/downloads/{{REPO_LOWER}}/{{TAG}}/total?label=Downloads&logo=github)

{{CHANGES_HEADING}}

{{CHANGES}}

## Highlight

This project relies on many important open-source projects 

- [LibreWPF](https://github.com/wieslawsoltes/LibreWPF/) and [LibreWinForms](https://github.com/wieslawsoltes/LibreWinForms) to run on macOS and Linux 
- [DevFlow](https://github.com/lextudio/ui-labs) for WinForms, WPF, WinUI, and Uno Platform, command line tools to enable AI workflows for UI development and diagnostics

Check them out if you are migrating your own WPF/WinForms apps to such new platforms.

## Known issues

- On all supported OS, .NET 10 runtime is needed for the executable to run.
- On Windows, use `OpenDevelop.exe` to launch the x64 build, and `OpenDevelopARM64.exe` to launch the ARM64 build.
- On macOS, after copying the app to the Applications folder, please use `xattr -d com.apple.quarantine /Applications/OpenDevelop.app` to unblock the binaries.
- On Linux there can be many issues. Please read [the troubleshooting guide](https://github.com/lextudio/OpenDevelop/blob/master/LINUX.md) for more details.

{{FULL_CHANGELOG}}
