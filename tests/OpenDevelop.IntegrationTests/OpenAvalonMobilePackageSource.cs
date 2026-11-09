using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Where a generated mobile app gets the OpenAvalon <c>ProGPU.*</c> packages. A developer machine has them in
/// the synchronized <c>artifacts/openavalon-feed</c>. CI never synchronizes that folder: it restores the same
/// closure from lextudio GitHub Packages with the token its workflow puts in
/// <c>NuGetPackageSourceCredentials_lextudio_github</c>, so a journey that insisted on the local folder was
/// skipped there and the lane's green result said nothing.
/// Only the packages OpenAvalon publishes (the exact <c>ProGPU.*</c> ids the repository's NuGet.config maps to
/// <c>lextudio_github</c>) come from that source; every other package, including ProGPU ones such as
/// ProGPU.Fonts.* and ProGPU.Media.Editing that are published on nuget.org only, comes from nuget.org.
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
            return Create("opendevelop-openavalon-local", feed, ClosureIds(repositoryRoot));
        if (!string.IsNullOrEmpty(getEnvironmentVariable(CredentialVariable)))
            return Create("lextudio_github", "https://nuget.pkg.github.com/lextudio/index.json", ClosureIds(repositoryRoot));

        string reason = "The OpenAvalon mobile package closure is neither in artifacts/openavalon-feed (pwsh ./sync-openavalon-feed.ps1) nor reachable through " + CredentialVariable + ".";
        // A skip counts as a pass: on CI the journey would then report green without having run.
        if (string.Equals(getEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(reason);
        skipReason = reason;
        return null;
    }

    /// <summary>The exact ProGPU.* ids OpenAvalon publishes, as the repository's NuGet.config lists them for the lextudio_github source.</summary>
    static string[] ClosureIds(string repositoryRoot)
    {
        var config = XDocument.Load(Path.Combine(repositoryRoot, "NuGet.config"));
        return config.Descendants("packageSource")
            .Where(source => (string?)source.Attribute("key") == "lextudio_github")
            .Elements("package")
            .Select(package => (string?)package.Attribute("pattern"))
            .OfType<string>()
            .Where(pattern => pattern.StartsWith("ProGPU.", StringComparison.Ordinal) && !pattern.Contains('*'))
            .ToArray();
    }

    static string Create(string sourceKey, string sourceValue, string[] closureIds)
    {
        if (closureIds.Length == 0)
            throw new InvalidOperationException("NuGet.config lists no ProGPU.* package for the lextudio_github source.");
        string patterns = string.Concat(closureIds.Select(id => $"<package pattern=\"{id}\" />"));
        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources><clear /><add key="{{sourceKey}}" value="{{sourceValue}}" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
              <packageSourceMapping>
                <packageSource key="{{sourceKey}}">{{patterns}}</packageSource>
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """;
    }
}
