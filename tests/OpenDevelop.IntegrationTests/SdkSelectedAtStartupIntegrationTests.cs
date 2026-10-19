using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Issue #19 on the path that actually failed: the .NET SDK a user selected under Tools > Options is
/// already persisted when OpenDevelop STARTS, so the IDE's in-process MSBuild is initialised from it
/// once, early in startup, and then stays latched to that SDK for the whole session. The
/// shared app instance of this suite is long running and its in-process MSBuild is already latched
/// to SDK 10, so changing the selection there at run time (as
/// <see cref="NewProjectDialogIntegrationTests"/> does) never reaches that path.
///
/// This test therefore starts its OWN IDE process, on its own DevFlow port and in its own config
/// directory, with a REAL SDK 9 (installed by dotnetup, see <see cref="DotnetupSdkProvider"/>) already
/// selected and discoverable - the exact state of the report. On the pre-fix code that instance
/// evaluated net10.0 projects under SDK 9 and failed with "The imported project
/// C:\Microsoft.CSharp.Core.targets was not found"; the fixed IDE must keep using SDK 10+.
///
/// <c>OD_IT_IDE_DLL</c> points the test at another build of the IDE (an <c>OpenDevelop.dll</c>, for
/// example a build of the pre-fix commit) to confirm the test really detects the defect.
/// </summary>
// Its own named collection so it is ordered like every other class (collections sort by display name,
// "90" after the "00".."60" ones); it starts its own IDE and does not touch the shared app's state.
[Collection("90 SDK selected at startup (isolated IDE)")]
public sealed class SdkSelectedAtStartupIntegrationTests
{
    [Fact]
    public async Task ProjectLoadsAndBuilds_WhenAnUnusableSdk9WasSelectedBeforeStartup()
    {
        string sdk9Root = await DotnetupSdkProvider.EnsureSdkAsync("9.0");

        string work = Path.Combine(Path.GetTempPath(), "opendevelop-sdk-at-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        IsolatedIde? ide = null;
        try
        {
            // A plain net10.0 console project in a solution, the shape File > New > Solution makes.
            string projectDir = Path.Combine(work, "project");
            Directory.CreateDirectory(projectDir);
            await File.WriteAllTextAsync(Path.Combine(projectDir, "Repro.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n" +
                "    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
            await File.WriteAllTextAsync(Path.Combine(projectDir, "Program.cs"), "System.Console.WriteLine(\"Hello\");\n");
            string solution = Path.Combine(projectDir, "Repro.slnx");
            await File.WriteAllTextAsync(solution, "<Solution>\n  <Project Path=\"Repro.csproj\" />\n</Solution>\n");

            // The persisted selection, in the properties file the IDE reads at startup. SDK 9 is made
            // discoverable as a CUSTOM root (what Tools > Options > .NET SDK stores), so the child's PATH
            // and environment stay untouched and the machine's own SDK 10 keeps its normal discovery.
            string configDir = Path.Combine(work, "config");
            Directory.CreateDirectory(configDir);
            await File.WriteAllTextAsync(Path.Combine(configDir, "OpenDevelopProperties.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Properties>\n" +
                "  <Property key=\"SharpDevelop.Sdk.SelectedRootPath\">" + System.Security.SecurityElement.Escape(sdk9Root) + "</Property>\n" +
                "  <Array key=\"SharpDevelop.Sdk.CustomRoots\">\n" +
                "    <Element>" + System.Security.SecurityElement.Escape(sdk9Root) + "</Element>\n" +
                "  </Array>\n" +
                "</Properties>\n");

            ide = await IsolatedIde.StartAsync(configDir);

            // The setup really is "unusable SDK selected and known": stored, and discovered as SDK 9.
            var list = await ide.InvokeAsync("od.sdk.list");
            Assert.Equal(TrimRoot(sdk9Root), TrimRoot(list.GetProperty("selectedRootPath").GetString()));
            Assert.Contains(list.GetProperty("sdks").EnumerateArray(), s =>
                TrimRoot(s.GetProperty("RootPath").GetString()) == TrimRoot(sdk9Root)
                && s.GetProperty("HighestSdkVersion").GetString()!.StartsWith("9."));

            // The three behaviours that must hold are collected, not thrown one by one, so a single run
            // against a broken IDE reports every symptom (effective SDK, evaluation, build) at once.
            var failures = new List<string>();

            // 1. The IDE must not use the unusable SDK.
            var effective = list.GetProperty("effective");
            string? effectiveVersion = effective.ValueKind == JsonValueKind.Object
                ? effective.GetProperty("HighestSdkVersion").GetString()
                : null;
            if (effective.ValueKind != JsonValueKind.Object)
                failures.Add("A .NET 10+ SDK is installed on this machine, but the IDE reports no effective SDK.");
            else if (!(int.TryParse(effectiveVersion?.Split('.')[0], out var major) && major >= 10))
                failures.Add($"With SDK 9 selected at startup the IDE's effective SDK is {effectiveVersion}; it must be .NET 10 or newer.");

            // 2. The first evaluation latches in-process MSBuild: the project must load WITH its items.
            var open = await ide.InvokeAsync("od.open-solution", solution);
            if (!open.GetProperty("success").GetBoolean())
                failures.Add("od.open-solution failed: " + open);

            JsonElement tree = default;
            bool evaluated = false;
            // Bounded by time, not by attempts: each solution-tree call can itself take seconds.
            var evaluationDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (!evaluated && DateTime.UtcNow < evaluationDeadline)
            {
                tree = await ide.InvokeAsync("od.solution-tree");
                evaluated = tree.GetProperty("projects").EnumerateArray()
                    .Any(p => p.GetProperty("name").GetString() == "Repro" && p.GetProperty("files").GetArrayLength() > 0);
                if (!evaluated)
                    await Task.Delay(500);
            }
            // A project that fails to load is reported in the Output pad's Build category (ErrorProject
            // writes the ProjectLoadException there) - the place a user sees issue #19's message.
            var buildPad = await ide.InvokeAsync("od.output-text", "Build");
            string buildPadText = buildPad.GetProperty("text").GetString() ?? "";
            if (System.Text.RegularExpressions.Regex.IsMatch(buildPadText, @"Microsoft\.CSharp(\.Core)?\.targets"))
                failures.Add("Issue #19 is back: the Output pad's Build channel reports the project was evaluated under SDK 9 and its Roslyn targets were not found: "
                    + Truncate(buildPadText, 1200));
            if (!evaluated)
                failures.Add("The project loaded without any items (an evaluation failure). Solution tree: " + tree);

            // 3. And it must build.
            // "rebuild": a plain build can answer Success from the fast up-to-date check without compiling.
            var build = await ide.InvokeAsync("od.build-solution", "Repro", "rebuild");
            if (!build.GetProperty("success").GetBoolean() || build.GetProperty("result").GetString() != "Success")
                failures.Add("od.build-solution did not succeed: " + Truncate(build.ToString(), 1500));

            Assert.True(failures.Count == 0,
                string.Join(Environment.NewLine, failures.Select((f, i) => $"{i + 1}. {f}"))
                + Environment.NewLine + "--- IDE output (tail) ---" + Environment.NewLine + Truncate(ide.Output, 4000, tail: true));
        }
        finally
        {
            ide?.Dispose();
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { /* a lingering handle; the temp folder is swept eventually */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    static string Truncate(string text, int max, bool tail = false) =>
        text.Length <= max ? text : tail ? "..." + text[^max..] : text[..max] + "...";

    static string TrimRoot(string? path) => (path ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
