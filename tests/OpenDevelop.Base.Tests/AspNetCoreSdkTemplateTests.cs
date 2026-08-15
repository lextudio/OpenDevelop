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
        var webApi = Assert.Single(templates, t => t.ShortName == "webapi" &&
            t.Tags.TryGetValue("language", out var language) && language.Equals("C#", StringComparison.OrdinalIgnoreCase));
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
