using System.Diagnostics;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// A throw-away .NET root that exposes exactly one installed SDK of a chosen major version, built from the SDK
/// the machine already has. The IDE selects an SDK by its root, and a root holds every SDK installed beside it
/// (CI installs 10 and 11 side by side), so selecting "the SDK 10" needs a root that contains nothing else.
/// Nothing is downloaded or copied: the host files are linked and only the muxer, a few kilobytes, is copied
/// - the muxer must be a real file in the new root, because a link to it would resolve to the original
/// directory and show every SDK again.
/// </summary>
sealed class IsolatedSdkRoot : IDisposable
{
    readonly List<string> _links = new();

    IsolatedSdkRoot(string path, string sdkVersion)
    {
        Path = path;
        SdkVersion = sdkVersion;
    }

    /// <summary>The new root: holds <c>dotnet</c> and an <c>sdk</c> folder with <see cref="SdkVersion"/> only.</summary>
    public string Path { get; }

    /// <summary>The one SDK the root exposes, for example <c>10.0.200</c>.</summary>
    public string SdkVersion { get; }

    /// <summary>
    /// Builds the root under <paramref name="parentDirectory"/>, or skips the test when the machine has no stable SDK
    /// of that major version (a missing prerequisite, not a product failure).
    /// </summary>
    public static IsolatedSdkRoot Create(int major, string parentDirectory)
    {
        string? sourceRoot = null;
        string? version = null;
        foreach (string candidate in SdkRootCandidates.WithSdk(major))
        {
            version = Directory.GetDirectories(System.IO.Path.Combine(candidate, "sdk"))
                .Select(System.IO.Path.GetFileName)
                .Where(name => name != null && name.StartsWith(major + ".", StringComparison.Ordinal) && !name.Contains('-'))
                .OrderBy(name => Version.TryParse(name, out var parsed) ? parsed : new Version(0, 0))
                .LastOrDefault();
            if (version != null)
            {
                sourceRoot = candidate;
                break;
            }
        }
        Assert.SkipWhen(sourceRoot == null || version == null, $"No stable .NET SDK {major} is installed on this machine.");

        string muxerName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string root = System.IO.Path.Combine(parentDirectory, "dotnet-sdk-" + major);
        Directory.CreateDirectory(System.IO.Path.Combine(root, "sdk"));
        File.Copy(System.IO.Path.Combine(sourceRoot!, muxerName), System.IO.Path.Combine(root, muxerName), overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(System.IO.Path.Combine(root, muxerName),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var isolated = new IsolatedSdkRoot(root, version!);
        foreach (string shared in new[] { "host", "shared", "packs", "templates" })
        {
            string target = System.IO.Path.Combine(sourceRoot!, shared);
            if (Directory.Exists(target))
                isolated.Link(System.IO.Path.Combine(root, shared), target);
        }
        isolated.Link(System.IO.Path.Combine(root, "sdk", version!), System.IO.Path.Combine(sourceRoot!, "sdk", version!));
        return isolated;
    }

    void Link(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            // A junction needs no privilege, unlike a symbolic link.
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            })!;
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"Could not link '{link}' to '{target}': {process.StandardError.ReadToEnd()}");
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
        _links.Add(link);
    }

    public void Dispose()
    {
        // Remove the links first so cleaning the folder can never reach the installed SDK behind them.
        foreach (string link in _links)
        {
            try { Directory.Delete(link, recursive: false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
