using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// "Load the previous solution on startup" in a real IDE of its own. The solution is opened once the window
/// has rendered, in the background; if it then fails to load, the user must still get the start page, as when
/// it was opened before the window existed. A valid previous solution is opened as before.
/// </summary>
public class StartupPreviousSolutionTests
{
    [Fact]
    public async Task APreviousSolutionThatFailsToLoad_LeavesTheStartPage()
    {
        string work = Directory.CreateTempSubdirectory("od-it-startup-broken-").FullName;
        try
        {
            string broken = Path.Combine(work, "Broken.slnx");
            File.WriteAllText(broken, "this is not a solution file");
            string config = WriteConfig(work, broken);

            using var ide = await IsolatedIde.StartAsync(config);
            var view = await WaitForActiveViewAsync(ide, "ICSharpCode.StartPage.StartPageViewContent");
            Assert.Equal("ICSharpCode.StartPage.StartPageViewContent", view);
            var status = await ide.InvokeAsync("od.solution.status");
            Assert.Equal(JsonValueKind.Null, status.GetProperty("path").ValueKind);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task AValidPreviousSolution_IsOpened()
    {
        string work = Directory.CreateTempSubdirectory("od-it-startup-valid-").FullName;
        try
        {
            string solution = FixtureSolution();
            string config = WriteConfig(work, solution);

            using var ide = await IsolatedIde.StartAsync(config);
            var watch = Stopwatch.StartNew();
            string? path = null;
            while (watch.Elapsed < TimeSpan.FromSeconds(90) && path != solution)
            {
                var status = await ide.InvokeAsync("od.solution.status");
                path = status.GetProperty("path").ValueKind == JsonValueKind.String ? status.GetProperty("path").GetString() : null;
                if (path != solution)
                    await Task.Delay(250);
            }
            Assert.Equal(solution, path);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A config directory whose properties ask for the previous solution and name it as the most recent.</summary>
    static string WriteConfig(string work, string previousSolution)
    {
        string config = Directory.CreateDirectory(Path.Combine(work, "config")).FullName;
        File.WriteAllText(Path.Combine(config, "OpenDevelopProperties.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Properties>
              <Property key="SharpDevelop.LoadPrevProjectOnStartup">True</Property>
              <Properties key="RecentOpen"><Array key="Projects"><Element>{System.Security.SecurityElement.Escape(previousSolution)}</Element></Array></Properties>
            </Properties>
            """);
        return config;
    }

    static async Task<string?> WaitForActiveViewAsync(IsolatedIde ide, string expected)
    {
        var watch = Stopwatch.StartNew();
        string? typeName = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(90))
        {
            var view = await ide.InvokeAsync("od.active-view");
            typeName = view.ValueKind == JsonValueKind.Object && view.TryGetProperty("typeName", out var name) ? name.GetString() : null;
            if (typeName == expected)
                return typeName;
            await Task.Delay(250);
        }
        return typeName;
    }

    static string FixtureSolution()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "tests", "fixtures")))
            directory = Path.GetDirectoryName(directory);
        Assert.NotNull(directory);
        return Path.Combine(directory!, "tests", "fixtures", "SolutionExplorerFixture", "SolutionExplorerFixture.slnx");
    }
}
