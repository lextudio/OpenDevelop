using System;
using System.IO;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Where a generated mobile app gets the OpenAvalon <c>ProGPU.*</c> packages. A developer machine has them in
/// the synchronized <c>artifacts/openavalon-feed</c>. CI never synchronizes that folder: it restores the same
/// closure from lextudio GitHub Packages with the token its workflow puts in
/// <c>NuGetPackageSourceCredentials_lextudio_github</c>, so a journey that insisted on the local folder was
/// skipped there and the lane's green result said nothing.
/// </summary>
internal static class OpenAvalonMobilePackageSource
{
    const string CredentialVariable = "NuGetPackageSourceCredentials_lextudio_github";

    /// <summary>The NuGet.config text for the generated app, or null with the reason no source can serve <paramref name="packageId"/> (a skip locally; on GitHub Actions it throws, because a skipped journey passes).</summary>
    public static string? TryCreateConfig(string repositoryRoot, string packageId, string packageVersion, out string? skipReason)
        => TryCreateConfig(repositoryRoot, packageId, packageVersion, Environment.GetEnvironmentVariable, out skipReason);

    /// <summary><see cref="TryCreateConfig(string, string, string, out string?)"/> with the environment passed in, so a test needs no process-wide variable.</summary>
    public static string? TryCreateConfig(string repositoryRoot, string packageId, string packageVersion, Func<string, string?> getEnvironmentVariable, out string? skipReason)
    {
        skipReason = null;
        string feed = Path.Combine(repositoryRoot, "artifacts", "openavalon-feed");
        if (File.Exists(Path.Combine(feed, packageId + "." + packageVersion + ".nupkg")))
        {
            return $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources><clear /><add key="opendevelop-openavalon-local" value="{{feed}}" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
                  <packageSourceMapping><packageSource key="opendevelop-openavalon-local"><package pattern="ProGPU.*" /></packageSource><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
                </configuration>
                """;
        }
        if (!string.IsNullOrEmpty(getEnvironmentVariable(CredentialVariable)))
        {
            // ProGPU.Fonts.* are published on nuget.org only (404 on the lextudio feed); the longer pattern wins.
            return """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources><clear /><add key="lextudio_github" value="https://nuget.pkg.github.com/lextudio/index.json" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
                  <packageSourceMapping>
                    <packageSource key="lextudio_github"><package pattern="ProGPU.*" /></packageSource>
                    <packageSource key="nuget.org"><package pattern="ProGPU.Fonts.*" /><package pattern="*" /></packageSource>
                  </packageSourceMapping>
                </configuration>
                """;
        }
        string reason = "The OpenAvalon mobile package closure is neither in artifacts/openavalon-feed (pwsh ./sync-openavalon-feed.ps1) nor reachable through " + CredentialVariable + ".";
        // A skip counts as a pass: on CI the journey would then report green without having run.
        if (string.Equals(getEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(reason);
        skipReason = reason;
        return null;
    }
}
