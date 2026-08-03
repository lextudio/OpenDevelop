// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

// Consolidated DevFlow agent/API integration tests (originally DevFlowAgentTests and
// DevFlowAddInsTests).

using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("OpenDevelop app")]
public sealed class DevFlowTests
{
    readonly OpenDevelopAppFixture _app;

    public DevFlowTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task AgentStatus_ReturnsValidJson()
    {
        var status = await _app.GetStatusAsync();

        Assert.True(status.TryGetProperty("name", out _), "status missing 'name'");
        Assert.True(status.TryGetProperty("id", out _), "status missing 'id'");
        Assert.True(status.TryGetProperty("framework", out _), "status missing 'framework'");
    }

    [Fact]
    public async Task AgentStatus_FrameworkIsWpf()
    {
        var status = await _app.GetStatusAsync();

        var framework = status.GetProperty("framework").GetString();
        Assert.Equal("wpf", framework);
    }

    [Fact]
    public async Task UITree_ReturnsNonEmpty()
    {
        var tree = await _app.GetUITreeAsync();

        // The visual tree should have at least a root node.
        Assert.True(tree.GetProperty("elements").GetArrayLength() > 0, "UI tree is empty");
    }

    [Fact]
    public async Task InvokeActions_ListsRegisteredActions()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var resp = await http.GetAsync($"{_app.DevFlowBaseUrl}/api/v1/invoke/actions");
        resp.EnsureSuccessStatusCode();

        var envelope = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // The endpoint wraps the list in {"actions": [...]}, not a bare array.
        Assert.Equal(JsonValueKind.Array, envelope.GetProperty("actions").ValueKind);
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

    [Fact]
    public async Task UnitTestsPad_DefaultsVisibleInLeftPane()
    {
        var result = await _app.InvokeAsync("od.pads");
        var pads = result.EnumerateArray().ToList();

        var testsPad = Assert.Single(pads, p =>
            p.GetProperty("className").GetString() == "ICSharpCode.UnitTesting.UnitTestsPad");

        Assert.Equal("Left", testsPad.GetProperty("defaultPosition").GetString());
        Assert.Equal("Unit Tests", testsPad.GetProperty("title").GetString());
    }
}
