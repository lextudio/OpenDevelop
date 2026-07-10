using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("OpenDevelop app")]
public sealed class DevFlowAddInsTests
{
    readonly OpenDevelopAppFixture _app;

    public DevFlowAddInsTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task AddInsList_ContainsSharpDevelopAddIns()
    {
        var result = await _app.InvokeAsync("od.addins");

        var addins = result.GetProperty("addins").EnumerateArray().ToList();

        // "name" is the AddIn's display Name attribute (e.g. "SharpDevelop"), not its manifest
        // Identity/file name, so match on fileName instead of assuming "name" carries the
        // "ICSharpCode.SharpDevelop" identity string.
        Assert.Contains(addins, a => a.GetProperty("fileName").GetString()!.Contains("ICSharpCode.SharpDevelop.addin"));
    }
}