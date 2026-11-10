using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>The package-source choice of the mobile debugging journeys; none of these starts the IDE.</summary>
public sealed class OpenAvalonMobilePackageSourceTests : IDisposable
{
    readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "od-mobile-source-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    static Func<string, string?> Env(params (string Name, string Value)[] variables)
        => name => variables.Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault();

    static string PatternsOf(XDocument config, string source)
        => string.Join(",", config.Descendants("packageSourceMapping").Single().Elements("packageSource")
            .Single(s => (string?)s.Attribute("key") == source).Elements("package").Select(p => (string?)p.Attribute("pattern")));

    [Fact]
    public void LocalFeedWithThePackage_IsUsedEvenWhenTheCredentialIsSet()
    {
        string feed = Directory.CreateDirectory(Path.Combine(root, "artifacts", "openavalon-feed")).FullName;
        File.WriteAllText(Path.Combine(feed, "ProGPU.iOS.1.2.3.nupkg"), "");

        string? config = OpenAvalonMobilePackageSource.TryCreateConfig(root, "ProGPU.iOS", "1.2.3",
            Env(("NuGetPackageSourceCredentials_lextudio_github", "x")), out string? skip);

        Assert.Null(skip);
        var xml = XDocument.Parse(config!);
        Assert.Equal(feed, (string?)xml.Descendants("add").Single(a => (string?)a.Attribute("key") == "opendevelop-openavalon-local").Attribute("value"));
        // Every ProGPU.* package is local, so none of them falls to nuget.org's "*".
        Assert.Equal("ProGPU.*", PatternsOf(xml, "opendevelop-openavalon-local"));
        Assert.Equal("*", PatternsOf(xml, "nuget.org"));
    }

    [Fact]
    public void WithoutALocalFeed_TheCredentialSelectsGitHubPackages_ForProGpuPackagesOnly()
    {
        string? config = OpenAvalonMobilePackageSource.TryCreateConfig(root, "ProGPU.iOS", "1.2.3",
            Env(("NuGetPackageSourceCredentials_lextudio_github", "Username=u;Password=p")), out string? skip);

        Assert.Null(skip);
        var xml = XDocument.Parse(config!);
        // The key must be the one the credential variable is named after.
        Assert.Equal("https://nuget.pkg.github.com/lextudio/index.json",
            (string?)xml.Descendants("add").Single(a => (string?)a.Attribute("key") == "lextudio_github").Attribute("value"));
        Assert.Equal("ProGPU.*", PatternsOf(xml, "lextudio_github"));
        Assert.Equal("*", PatternsOf(xml, "nuget.org"));
    }

    [Fact]
    public void NoSourceAtAll_SkipsLocally()
    {
        string? config = OpenAvalonMobilePackageSource.TryCreateConfig(root, "ProGPU.iOS", "1.2.3", Env(), out string? skip);

        Assert.Null(config);
        Assert.Contains("artifacts/openavalon-feed", skip);
    }

    [Fact]
    public void NoSourceAtAll_FailsOnGitHubActions_BecauseASkipWouldPass()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            OpenAvalonMobilePackageSource.TryCreateConfig(root, "ProGPU.iOS", "1.2.3", Env(("GITHUB_ACTIONS", "true")), out _));

        Assert.Contains("NuGetPackageSourceCredentials_lextudio_github", failure.Message);
    }
}
