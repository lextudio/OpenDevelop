using System.Diagnostics;
using System.Xml.Linq;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("20 General workbench fixture")]
public sealed class ProjectOptionsIntegrationTests
{
    readonly OpenDevelopAppFixture _app;

    public ProjectOptionsIntegrationTests(OpenDevelopAppFixture app) => _app = app;

    [Fact]
    public async Task CSharpProjectOptions_RepresentativeSettingOnEachPagePersists()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opendevelop-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "OptionsUnderTest.csproj");
        var marker = Path.Combine(directory, "prebuild-marker.txt");
        var preBuild = "echo option-event > \"" + marker + "\"";
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(directory, "Class1.cs"), "public class Class1 { }");
        try
        {
            var opened = await _app.ReopenSolutionAsync(projectPath);
            Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
            // Exercise the Project Browser command path for the selected project.
            var view = await _app.InvokeAsync("od.project-browser.open-project-options", "OptionsUnderTest");
            Assert.True(view.GetProperty("success").GetBoolean(), view.ToString());
            Assert.True(view.GetProperty("projectOptionsOpen").GetBoolean(), view.ToString());

            await SetPage("Application", "OptionsRenamed");
            await SetPage("Reference Paths", "lib");
            await SetPage("Build Events", preBuild);
            await SetPage("Custom Tool", "Class1.cs");

            var xml = XDocument.Load(projectPath);
            Assert.Equal("OptionsRenamed", xml.Descendants("AssemblyName").Last().Value);
            Assert.Equal("lib", xml.Descendants("ReferencePath").Last().Value);
            Assert.Equal(preBuild, xml.Descendants("PreBuildEvent").Last().Value);

            using (var build = Process.Start(new ProcessStartInfo("dotnet") {
                ArgumentList = { "build", projectPath, "-c", "Debug", "--nologo" },
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!)
            {
                var output = await build.StandardOutput.ReadToEndAsync();
                var error = await build.StandardError.ReadToEndAsync();
                await build.WaitForExitAsync();
                Assert.True(build.ExitCode == 0, output + error);
            }
            Assert.Equal("option-event", File.ReadAllText(marker).Trim());
            Assert.True(File.Exists(Path.Combine(directory, "bin", "Debug", "net10.0", "OptionsRenamed.dll")));

            await SetPage("Signing", "True");
            Assert.Equal("true", XDocument.Load(projectPath).Descendants("SignAssembly").Last().Value.ToLowerInvariant());

            await _app.ReopenSolutionAsync(projectPath);
            view = await _app.InvokeAsync("od.project-browser.open-project-options", "OptionsUnderTest");
            Assert.True(view.GetProperty("success").GetBoolean(), view.ToString());
            Assert.True(view.GetProperty("projectOptionsOpen").GetBoolean(), view.ToString());
            await CheckPage("Application", "OptionsRenamed");
            await CheckPage("Reference Paths", "lib");
            await CheckPage("Build Events", preBuild);
            await CheckPage("Signing", "True");
            await CheckPage("Custom Tool", "Class1.cs");
        }
        finally
        {
            await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
            Directory.Delete(directory, recursive: true);
        }

        async Task SetPage(string page, string value)
        {
            var result = await _app.InvokeAsync("od.project-options.exercise-page", page, value);
            Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
            Assert.Equal(value, result.GetProperty("value").GetString());
        }

        async Task CheckPage(string page, string value)
        {
            var result = await _app.InvokeAsync("od.project-options.exercise-page", page);
            Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
            Assert.Equal(value, result.GetProperty("value").GetString());
        }
    }
}
