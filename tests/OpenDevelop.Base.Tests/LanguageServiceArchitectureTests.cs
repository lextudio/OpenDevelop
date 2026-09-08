using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using ICSharpCode.SharpDevelop.LanguageServices.Roslyn;
using Xunit;

namespace OpenDevelop.Base.Tests;

public class LanguageServiceArchitectureTests
{
    [Fact]
    public async Task LocalLifecycleAcceptsPathsWithoutDependingOnIdeServices()
    {
        using var service = new CSharpVBLanguageService();
        var directory = Path.Combine(Path.GetTempPath(), "RoslynClose-" + Guid.NewGuid());
        var owned = new DocumentId(Path.Combine(directory, "Owned.cs"));
        var unrelated = new DocumentId(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"));
        var token = TestContext.Current.CancellationToken;
        await service.UpsertDocumentAsync(owned, "class Owned {}", token);
        await service.UpsertDocumentAsync(unrelated, "class Unrelated {}", token);
        await service.CloseSolutionAsync(directory, token);
        Assert.False(service.ContainsDocument(owned));
        Assert.True(service.ContainsDocument(unrelated));
    }

    [Fact]
    public void DeployedHostHasNoTransitiveIdeOrWindowingRuntime()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "RoslynHost", "ICSharpCode.Roslyn.Host.deps.json");
        Assert.True(File.Exists(path), "The test project must deploy the host dependency manifest.");
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        foreach (var library in manifest.RootElement.GetProperty("libraries").EnumerateObject())
        {
            var name = library.Name.Split('/')[0];
            Assert.False(name.StartsWith("LibreWPF", StringComparison.Ordinal)
                || name.StartsWith("ProGPU", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Build", StringComparison.Ordinal)
                || name is "ICSharpCode.Core" or "ICSharpCode.SharpDevelop" or "PresentationFramework" or "WindowsBase",
                "Unexpected host runtime dependency: " + name);
        }
    }

    [Fact]
    public void PortableAssembliesHaveNoIdeOrWindowingDependency()
    {
        var contracts = typeof(ILanguageService).Assembly;
        var implementation = typeof(CSharpVBLanguageService).Assembly;
        Assert.Equal("ICSharpCode.LanguageServices.Contracts", contracts.GetName().Name);
        Assert.Equal("ICSharpCode.LanguageServices.Roslyn", implementation.GetName().Name);
        Assert.Same(contracts, typeof(LanguageServiceProjectSnapshot).Assembly);
        Assert.Same(contracts, typeof(IRoslynLanguageProtocol).Assembly);
        foreach (var assembly in new[] { contracts, implementation })
        {
            Assert.Equal(".NETCoreApp,Version=v10.0", assembly.GetCustomAttributes(false)
                .OfType<System.Runtime.Versioning.TargetFrameworkAttribute>().Single().FrameworkName);
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name is "ICSharpCode.SharpDevelop" or "ICSharpCode.Core" or "PresentationFramework" or "WindowsBase"
                || reference.Name!.StartsWith("Microsoft.Build", StringComparison.Ordinal)
                || reference.Name.StartsWith("LibreWPF", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(contracts.GetReferencedAssemblies(), reference => reference.Name!.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal));
    }
}
