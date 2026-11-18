using System;
using System.IO;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// The Git add-in tests' project template, written from code into a fresh temporary folder instead of copied from
/// checked-in files. It used to live in tests/fixtures/GitFixture and a CI runner once lacked GitFixture.sln, which
/// failed every AddInTests case in its constructor; a template that cannot be missing cannot fail that way.
/// </summary>
static class GitFixtureTemplate
{
    const string ProjectGuid = "{2B3C4D5E-2222-4B33-8D44-1F2A3B4C5D6E}";

    public static void Write(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "GitFixtureApp"));
        WriteFile(Path.Combine(directory, "GitFixture.sln"), Solution);
        WriteFile(Path.Combine(directory, "GitFixtureApp", "GitFixtureApp.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n  <PropertyGroup>\r\n    <TargetFramework>net8.0</TargetFramework>\r\n  </PropertyGroup>\r\n</Project>\r\n");
        foreach (var name in new[] { "Added", "Clean", "Modified", "Untracked" })
        {
            WriteFile(Path.Combine(directory, "GitFixtureApp", name + ".cs"),
                $"namespace GitFixtureApp;\r\n\r\npublic class {name}\r\n{{\r\n    public static void Run()\r\n    {{\r\n    }}\r\n}}\r\n");
        }
    }

    static void WriteFile(string path, string text) => File.WriteAllText(path, text);

    static readonly string Solution = string.Join("\r\n", new[]
    {
        "",
        "Microsoft Visual Studio Solution File, Format Version 12.00",
        "# Visual Studio Version 17",
        "VisualStudioVersion = 17.0.31903.59",
        "MinimumVisualStudioVersion = 10.0.40219.1",
        "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"GitFixtureApp\", \"GitFixtureApp\\GitFixtureApp.csproj\", \"" + ProjectGuid + "\"",
        "EndProject",
        "Global",
        "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution",
        "\t\tDebug|Any CPU = Debug|Any CPU",
        "\t\tRelease|Any CPU = Release|Any CPU",
        "\tEndGlobalSection",
        "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution",
        "\t\t" + ProjectGuid + ".Debug|Any CPU.ActiveCfg = Debug|Any CPU",
        "\t\t" + ProjectGuid + ".Debug|Any CPU.Build.0 = Debug|Any CPU",
        "\t\t" + ProjectGuid + ".Release|Any CPU.ActiveCfg = Release|Any CPU",
        "\t\t" + ProjectGuid + ".Release|Any CPU.Build.0 = Release|Any CPU",
        "\tEndGlobalSection",
        "\tGlobalSection(SolutionProperties) = preSolution",
        "\t\tHideSolutionNode = FALSE",
        "\tEndGlobalSection",
        "EndGlobal",
        ""
    });
}
