# Contributing to OpenDevelop

Thanks for helping to improve OpenDevelop.

## Building from source

Read [Building OpenDevelop](README.md#building-opendevelop) in the README first. In short: install the
.NET 11 SDK, PowerShell 7+ and the submodules, make a GitHub token with `read:packages` available to
`pwsh ./sync-openavalon-feed.ps1` (the GitHub CLI counts, a token in `OD_GITHUB_TOKEN` works too), then build
with `pwsh ./build.ps1 <target>` or run with `./launch.sh`. Do not start from Visual Studio on a fresh clone:
its restore does not synchronize the package feed and fails with `401 (Unauthorized)` from
`nuget.pkg.github.com/lextudio`.

In order for your changes to be accepted you can either sign the [Joint Copyright Assignment](http://www.icsharpcode.net/TechNotes/JointCopyrightAssignment.pdf) or add the following statement to your pull request:

_I certify that I own, and have sufficient rights to contribute, all source code and related material intended to be compiled or integrated with the source code for the #develop open source product (the "Contribution"). My Contribution is licensed under the MIT License._

Unless we have a Joint Copyright Agreement on file or this statement is in your pull request, we cannot accept it.

More information is available on [joining the team](https://github.com/icsharpcode/SharpDevelop/wiki/Joining-the-Team).
