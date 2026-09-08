using ICSharpCode.SharpDevelop.Templates;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class AspNetCoreSdkTemplateTests
{
    [Fact]
    public async Task DiscoversAndInstantiatesCurrentSdkWebApiTemplate()
    {
        using var service = new TemplateDiscoveryService();
        var cancellationToken = TestContext.Current.CancellationToken;
        var templates = await service.GetInstalledTemplatesAsync(cancellationToken);
        // Short names intentionally span SDK major versions when several targeting packs are
        // installed. This test runs on net10.0, so select its concrete template identity.
        var webApi = Assert.Single(templates, t => t.Identity == "Microsoft.Web.WebApi.CSharp.10.0");
        var directory = Path.Combine(Path.GetTempPath(), "opendevelop-webapi-template-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await service.InstantiateAsync(webApi, "ModernWebApi", directory,
                new Dictionary<string, string?> { ["no-https"] = "true", ["no-openapi"] = "true" }, cancellationToken);
            Assert.True(result.Success, result.ErrorMessage);
            var project = Assert.Single(Directory.EnumerateFiles(directory, "*.csproj"));
            Assert.Contains("Microsoft.NET.Sdk.Web", await File.ReadAllTextAsync(project, cancellationToken));
            Assert.True(File.Exists(Path.Combine(directory, "Program.cs")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
