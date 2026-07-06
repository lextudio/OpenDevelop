using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// Coverage for the "open a project and build it" flow: drives od.build-solution (backed by
// MinimalMSBuildEngine, a real `dotnet build` child process - see that class's comments for why
// it isn't the in-process Microsoft.Build.Execution API) and od.output-text (the real Output pad,
// wired up via CompilerMessageView - see doc/technotes session notes on both fixes).
[Collection("OpenDevelop app")]
public sealed class BuildTests
{
    readonly OpenDevelopAppFixture _app;

    public BuildTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task BuildSolution_FixtureProjectBuildsSuccessfully()
    {
        await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);

        var result = await _app.InvokeAsync("od.build-solution");

        Assert.True(result.GetProperty("success").GetBoolean(), "od.build-solution reported an infrastructure failure, not a build failure");
        Assert.Equal("Success", result.GetProperty("result").GetString());
        Assert.Equal(0, result.GetProperty("errorCount").GetInt32());
        Assert.Equal(0, result.GetProperty("warningCount").GetInt32());
        Assert.Empty(result.GetProperty("diagnostics").EnumerateArray());
    }

    [Fact]
    public async Task BuildSolution_OutputPadCapturesRealBuildLog()
    {
        await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
        await _app.InvokeAsync("od.build-solution");

        var output = await _app.InvokeAsync("od.output-text");

        Assert.Equal("Build", output.GetProperty("category").GetString());
        string text = output.GetProperty("text").GetString()!;
        Assert.Contains("Build started.", text);
        Assert.Contains("Build succeeded.", text);
        Assert.Contains("SampleApp", text);
    }

    [Fact]
    public async Task BuildSolution_UnknownProjectNameReturnsError()
    {
        await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);

        var result = await _app.InvokeAsync("od.build-solution", "NoSuchProject");

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("NoSuchProject", result.GetProperty("error").GetString());
    }
}
