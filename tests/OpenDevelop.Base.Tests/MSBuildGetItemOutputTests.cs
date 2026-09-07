using System.Text.Json;
using ICSharpCode.SharpDevelop.Project;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Covers the parser behind out-of-process assembly-reference resolution
/// (MinimalMSBuildEngine.ResolveAssemblyReferences). The payload shapes below are taken from real
/// `dotnet msbuild <project> -t:ResolveReferences -getItem:ReferencePath` output.
/// </summary>
public class MSBuildGetItemOutputTests
{
    // Trimmed from a real run against tests/fixtures/SampleTestProject (179 entries).
    const string RealOutput = """
        {
          "Items": {
            "ReferencePath": [
              {
                "Identity": "/Users/x/.nuget/packages/xunit.v3.core/3.0.0/lib/net10.0/xunit.v3.core.dll",
                "HintPath": "/Users/x/.nuget/packages/xunit.v3.core/3.0.0/lib/net10.0/xunit.v3.core.dll",
                "ReferenceSourceTarget": "ResolveAssemblyReference",
                "NuGetPackageId": "xunit.v3.core",
                "CopyLocal": "false"
              },
              {
                "Identity": "/usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/10.0.0/ref/net10.0/System.Runtime.dll",
                "ReferenceSourceTarget": "ResolveAssemblyReference"
              }
            ]
          }
        }
        """;

    [Fact]
    public void ParseItemIdentities_ReturnsEveryIdentityInOrder()
    {
        var paths = MSBuildGetItemOutput.ParseItemIdentities(RealOutput, "ReferencePath");

        Assert.Equal(2, paths.Count);
        Assert.EndsWith("xunit.v3.core.dll", paths[0]);
        Assert.EndsWith("System.Runtime.dll", paths[1]);
    }

    [Fact]
    public void ParseItemIdentities_ReturnsEmpty_ForAnUnrestoredProjectThatResolvedNothing()
    {
        // MSBuild still emits the envelope with an empty array; this is the ordinary outcome for a
        // project that has never been restored, not an error.
        var paths = MSBuildGetItemOutput.ParseItemIdentities(
            """{"Items":{"ReferencePath":[]}}""", "ReferencePath");

        Assert.Empty(paths);
    }

    [Theory]
    // No output at all (process produced nothing).
    [InlineData("")]
    [InlineData("   ")]
    // Valid JSON, but not the -getItem envelope.
    [InlineData("""{"Properties":{"TargetFramework":"net10.0"}}""")]
    // The envelope, but without the item that was asked for.
    [InlineData("""{"Items":{"Compile":[{"Identity":"Program.cs"}]}}""")]
    // Right shape, wrong types - must not throw.
    [InlineData("""{"Items":{"ReferencePath":"not-an-array"}}""")]
    [InlineData("""{"Items":"not-an-object"}""")]
    [InlineData("[]")]
    public void ParseItemIdentities_ReturnsEmpty_RatherThanThrowing(string json)
    {
        Assert.Empty(MSBuildGetItemOutput.ParseItemIdentities(json, "ReferencePath"));
    }

    [Fact]
    public void ParseItemIdentities_SkipsEntriesWithNoUsableIdentity()
    {
        var paths = MSBuildGetItemOutput.ParseItemIdentities("""
            {"Items":{"ReferencePath":[
              {"HintPath":"/only/metadata.dll"},
              {"Identity":""},
              {"Identity":null},
              "a bare string",
              {"Identity":"/kept.dll"}
            ]}}
            """, "ReferencePath");

        Assert.Equal(new[] { "/kept.dll" }, paths);
    }

    [Fact]
    public void ParseItemIdentities_Throws_WhenMSBuildPrintedSomethingThatIsNotJson()
    {
        // A crash or a stray diagnostic ahead of the payload is worth surfacing: silently treating
        // it as "no references" is what made the missing implementation invisible for so long.
        // ThrowsAny, not Throws: the contract is the JsonException family (the concrete type is
        // JsonReaderException today), not one specific derived type.
        Assert.ThrowsAny<JsonException>(
            () => MSBuildGetItemOutput.ParseItemIdentities("MSB4062: task could not be loaded", "ReferencePath"));
    }
}
