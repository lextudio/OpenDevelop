using System.Runtime.InteropServices;
using ICSharpCode.SharpDevelop.Project.Sdk;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The ".NET SDK check" block is shown twice: in the Build pad at startup, and in the modal error
/// the workbench raises when this process has no usable .NET SDK at all (issue #23). Both read this
/// one builder, so what a user is told at startup is what the pad keeps for the session.
/// </summary>
public sealed class DotNetSdkAvailabilityReportTests
{
    // The in-process check is architecture-sensitive (Microsoft.Build.NuGetSdkResolver.dll is a
    // ReadyToRun image), so a "healthy" fixture has to name this process's own architecture - the
    // test host is arm64 on macOS and x64 on Windows.
    static Architecture Process => RuntimeInformation.ProcessArchitecture;

    static Architecture Other(Architecture architecture)
        => architecture == Architecture.Arm64 ? Architecture.X64 : Architecture.Arm64;

    static DotNetSdkInfo Sdk(string root, string version, Architecture architecture) => new()
    {
        Label = "System (.NET SDK " + version + ")",
        RootPath = root,
        DotnetExecutablePath = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
        InstalledSdkVersions = new[] { version },
        HighestSdkVersion = version,
        Origin = DotNetSdkOrigin.System,
        Architecture = architecture
    };

    [Fact]
    public void NoSdk_ReportsWhatIsMissingAndHowToInstallIt()
    {
        var lines = DotNetSdkService.DescribeAvailabilityReport(
            Array.Empty<DotNetSdkInfo>(), preferredRoot: "", noCompatibleInProcessSdkFound: true, Process);

        Assert.Contains(lines, line => line.Contains(".NET SDK 10 or newer: NOT FOUND"));
        Assert.Contains(lines, line => line.Contains($"In-process SDK (must match this {Process} process): NONE"));
        Assert.Contains(lines, line => line.Contains("winget install Microsoft.DotNet.SDK.10 --architecture"));
        // Nothing is installed, so there is no list to print - the shape issue #21's follow-up had.
        Assert.DoesNotContain(lines, line => line.Contains("Installed .NET SDK roots:"));
    }

    [Fact]
    public void Sdk9Only_IsListedButNotUsed()
    {
        // The machine from issue #19: an SDK is installed, just not one this IDE may load.
        var lines = DotNetSdkService.DescribeAvailabilityReport(
            new[] { Sdk(@"C:\Program Files\dotnet", "9.0.320", Process) },
            preferredRoot: "", noCompatibleInProcessSdkFound: true, Process);

        Assert.Contains(lines, line => line.Contains(".NET SDK 10 or newer: NOT FOUND"));
        Assert.Contains(lines, line => line.Contains($"In-process SDK (must match this {Process} process): NONE"));
        Assert.Contains(lines, line => line.Contains(@"C:\Program Files\dotnet") && line.Contains("9.0.320"));
    }

    [Fact]
    public void ArchitectureMismatch_SaysWhyTheInstalledSdkIsIgnored()
    {
        var lines = DotNetSdkService.DescribeAvailabilityReport(
            new[] { Sdk(@"C:\Program Files\dotnet", "10.0.403", Other(Process)) },
            preferredRoot: "", noCompatibleInProcessSdkFound: true, Process);

        Assert.Contains(lines, line => line.Contains($"In-process SDK (must match this {Process} process): NONE"));
        Assert.Contains(lines, line => line.Contains("10.0.403") && line.Contains(Other(Process).ToString()));
    }

    [Fact]
    public void HealthySetup_HasNothingToSay()
    {
        var lines = DotNetSdkService.DescribeAvailabilityReport(
            new[] { Sdk(@"C:\Program Files\dotnet", "10.0.403", Process) },
            preferredRoot: "", noCompatibleInProcessSdkFound: false, Process);

        Assert.Empty(lines);
    }
}
