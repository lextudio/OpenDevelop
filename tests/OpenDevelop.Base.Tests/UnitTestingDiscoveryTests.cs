using System.Text.Json;
using ICSharpCode.UnitTesting.Mtp;
using ICSharpCode.UnitTesting.Simple;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class UnitTestingDiscoveryTests
{
    [Fact]
    public void ApproximateScannerOnlyScansFilesProvidedByEvaluatedCompileItems()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OpenDevelop-UnitTesting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var included = Path.Combine(directory, "IncludedTests.cs");
            var excluded = Path.Combine(directory, "ExcludedTests.cs");
            File.WriteAllText(included, "class IncludedTests { [Xunit.Fact] public void Included() {} }");
            File.WriteAllText(excluded, "class ExcludedTests { [Xunit.Fact] public void Excluded() {} }");

            var candidates = RoslynTestScanner.ScanFiles(new[] { included });

            Assert.Single(candidates);
            Assert.Equal("IncludedTests.Included", candidates[0].DisplayName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DiscoveryRefreshReplacesSyntheticNodeUidOnReusedMethod()
    {
        var approximate = CreateNode(string.Empty);
        var discovered = CreateNode("real-mtp-uid");
        var method = new MtpTestMethod(null!, approximate, "net10.0");

        method.UpdateNode(discovered);

        Assert.Equal("real-mtp-uid", method.Uid);
        Assert.Same(discovered, method.Node);
    }

    static MtpTestNode CreateNode(string uid)
    {
        var json = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["uid"] = uid,
            ["display-name"] = "Tests.Sample.Works",
            ["node-type"] = "action",
            ["location.type"] = "Tests.Sample",
            ["location.method"] = "Works",
        });
        return MtpTestNode.FromJson(json);
    }
}
