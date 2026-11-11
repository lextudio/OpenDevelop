using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// The Debug toolbar's launch-target drop-down (Visual Studio style): a multi-target project lists
/// the launchSettings.json profiles linked to its target frameworks, hides a framework that has no
/// profile, and can add one. The journey goes through the same LaunchTargetService the toolbar's
/// combo box uses, via od.launch-target.*, on a copy that is edited on disk.
/// </summary>
[Collection("20 General workbench fixture")]
public sealed class LaunchTargetTests : IAsyncDisposable
{
    // Comments are the point: adding a profile must not rewrite the user's file.
    const string LaunchSettings = """
        {
          // the team's launch profiles
          "profiles": {
            "App (iOS)": { "commandName": "Project", "compatibleTargetFramework": "ios" },
            "App (Desktop WSL2)": { "commandName": "WSL2", "compatibleTargetFramework": "desktop" }
          }
        }
        """;

    readonly OpenDevelopAppFixture _app;
    readonly string _dir;
    readonly string _slnx;
    string ProjectPath => Path.Combine(_dir, "App.csproj");

    public LaunchTargetTests(OpenDevelopAppFixture app)
    {
        _app = app;
        _dir = Path.Combine(Path.GetTempPath(), "LaunchTargetTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "Properties"));
        // Only the evaluated TargetFrameworks matter; nothing here is built.
        File.WriteAllText(Path.Combine(_dir, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0-ios;net10.0-android;net10.0-desktop</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_dir, "Properties", "launchSettings.json"), LaunchSettings);
        _slnx = Path.Combine(_dir, "App.slnx");
        File.WriteAllText(_slnx, "<Solution><Project Path=\"App.csproj\" /></Solution>");
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task LaunchTarget_ListsProfilesPerFramework_HidesFrameworksWithoutOne_AndAddsOne()
    {
        var open = await _app.ReopenSolutionAsync(_slnx);
        Assert.True(open.GetProperty("success").GetBoolean(), open.ToString());

        // Only the iOS profile fits a framework of the project; WSL2 is not something the IDE starts, and
        // android/desktop have no profile yet, so they are offered for adding instead of listed.
        var status = await StatusAsync();
        Assert.True(status.GetProperty("visible").GetBoolean(), status.ToString());
        Assert.Equal(new[] { "Profile:App (iOS)", "AddProfile:net10.0-android", "AddProfile:net10.0-desktop" }, Entries(status));
        Assert.Equal("net10.0-ios", status.GetProperty("activeTargetFramework").GetString());
        Assert.Equal("App (iOS)", SelectedEntry(status));
        Assert.Equal("App (iOS)", status.GetProperty("launchProfile").GetString());
        // The selected profile's platform lists its devices, "Automatic" first.
        Assert.StartsWith("Automatic", DeviceEntries(status).First());

        // Choosing a device stores only the device preference: profile and framework stay. Automatic (id null) clears it;
        // a real simulator, when the machine has one, is stored by its simctl UDID.
        var simulator = DeviceRows(status).FirstOrDefault(row => row.Id != null);
        if (simulator.Id != null) {
            var pick = await _app.InvokeAsync("od.launch-target.select", simulator.Label, ProjectPath);
            Assert.True(pick.GetProperty("success").GetBoolean(), pick.ToString());
            status = await StatusAsync();
            Assert.Equal(simulator.Id, status.GetProperty("iosSimulatorUdid").GetString());
            Assert.Equal(simulator.Id, DeviceRows(status).Single(row => row.Selected).Id);
        }
        var automatic = await _app.InvokeAsync("od.launch-target.select", DeviceRows(status).First().Label, ProjectPath);
        Assert.True(automatic.GetProperty("success").GetBoolean(), automatic.ToString());
        status = await StatusAsync();
        Assert.Equal(string.Empty, status.GetProperty("iosSimulatorUdid").GetString());
        Assert.Null(DeviceRows(status).Single(row => row.Selected).Id);
        Assert.Equal("net10.0-ios", status.GetProperty("activeTargetFramework").GetString());
        Assert.Equal("App (iOS)", SelectedEntry(status));
        Assert.Equal("App (iOS)", status.GetProperty("launchProfile").GetString());
        Assert.All(DeviceRows(status), row => Assert.True(row.Platform == "ios"));

        // Choosing the "add" entry for android writes its profile, selects it and switches the framework.
        var add = await _app.InvokeAsync("od.launch-target.select", "net10.0-android", ProjectPath);
        Assert.True(add.GetProperty("success").GetBoolean(), add.ToString());
        status = await StatusAsync();
        Assert.Equal("net10.0-android", status.GetProperty("activeTargetFramework").GetString());
        Assert.Equal("App (Android)", status.GetProperty("selectedProfile").GetString());
        Assert.Equal("App (Android)", SelectedEntry(status));
        Assert.Equal("App (Android)", status.GetProperty("launchProfile").GetString());
        Assert.Equal(new[] { "Profile:App (iOS)", "Profile:App (Android)", "AddProfile:net10.0-desktop" }, Entries(status));
        Assert.StartsWith("Automatic", DeviceEntries(status).First());

        // The file was edited in place: the comment and the WSL2 profile are untouched.
        string text = File.ReadAllText(Path.Combine(_dir, "Properties", "launchSettings.json"));
        Assert.StartsWith(LaunchSettings.Substring(0, LaunchSettings.IndexOf("\"App (Desktop WSL2)\"", StringComparison.Ordinal)), text);
        Assert.Contains("// the team's launch profiles", text);
        Assert.Contains("\"App (Android)\": {", text);
        Assert.Contains("\"compatibleTargetFramework\": \"android\"", text);

        // Choosing an existing profile switches the framework back.
        var back = await _app.InvokeAsync("od.launch-target.select", "App (iOS)", ProjectPath);
        Assert.True(back.GetProperty("success").GetBoolean(), back.ToString());
        status = await StatusAsync();
        Assert.Equal("net10.0-ios", status.GetProperty("activeTargetFramework").GetString());
        Assert.Equal("App (iOS)", SelectedEntry(status));
        Assert.Equal("App (iOS)", status.GetProperty("launchProfile").GetString());

        var unknown = await _app.InvokeAsync("od.launch-target.select", "No such entry", ProjectPath);
        Assert.False(unknown.GetProperty("success").GetBoolean(), unknown.ToString());
    }

    // The profile Run/Debug apply (launchProfile, from DotNetStartBehavior's own lookup) is the one shown as selected,
    // even when a profile of another framework is first in the file and nothing is stored yet.
    [Fact]
    public async Task LaunchProfile_AgreesWithTheDropDown_WhenAForeignProfileIsFirstAndNothingIsStored()
    {
        string dir = Path.Combine(_dir, "Foreign");
        Directory.CreateDirectory(Path.Combine(dir, "Properties"));
        File.WriteAllText(Path.Combine(dir, "Foreign.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net10.0-android;net10.0-ios</TargetFrameworks></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Properties", "launchSettings.json"), """
            { "profiles": {
              "Foreign (Android)": { "commandName": "Project", "compatibleTargetFramework": "android" },
              "Foreign (iOS)": { "commandName": "Project", "compatibleTargetFramework": "ios" }
            } }
            """);
        string slnx = Path.Combine(dir, "Foreign.slnx");
        File.WriteAllText(slnx, "<Solution><Project Path=\"Foreign.csproj\" /></Solution>");
        var open = await _app.ReopenSolutionAsync(slnx);
        Assert.True(open.GetProperty("success").GetBoolean(), open.ToString());
        string project = Path.Combine(dir, "Foreign.csproj");

        // Active framework is the first one, android: its profile is shown and launched.
        var status = await _app.InvokeAsync("od.launch-target.status", project);
        Assert.Equal("net10.0-android", status.GetProperty("activeTargetFramework").GetString());
        Assert.Equal("Foreign (Android)", SelectedEntry(status));
        Assert.Equal("Foreign (Android)", status.GetProperty("launchProfile").GetString());

        // Switching the framework through the drop-down moves both to iOS.
        var select = await _app.InvokeAsync("od.launch-target.select", "Foreign (iOS)", project);
        Assert.True(select.GetProperty("success").GetBoolean(), select.ToString());
        status = await _app.InvokeAsync("od.launch-target.status", project);
        Assert.Equal("Foreign (iOS)", SelectedEntry(status));
        Assert.Equal("Foreign (iOS)", status.GetProperty("launchProfile").GetString());
    }

    // An invalid launchSettings.json hides the drop-down (starting the project reports the error); the status
    // action says so instead of throwing.
    [Fact]
    public async Task LaunchTarget_IsHiddenAndReportsTheError_WhenLaunchSettingsIsNotValidJson()
    {
        File.WriteAllText(Path.Combine(_dir, "Properties", "launchSettings.json"), "{ \"profiles\": { \"A\": ");
        var open = await _app.ReopenSolutionAsync(_slnx);
        Assert.True(open.GetProperty("success").GetBoolean(), open.ToString());

        var status = await StatusAsync();

        Assert.False(status.GetProperty("visible").GetBoolean(), status.ToString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("launchProfile").ValueKind);
        Assert.Contains("not valid JSON", status.GetProperty("launchError").GetString());
    }

    // Several frameworks but none with a platform and no profiles: nothing to choose or add, so no empty drop-down.
    [Theory]
    [InlineData("net10.0;net8.0")]
    [InlineData("net10.0")]
    public async Task LaunchTarget_IsHidden_WhenThereIsNothingToChooseOrAdd(string targetFrameworks)
    {
        string dir = Path.Combine(_dir, "Plain");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Plain.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>" + targetFrameworks + "</TargetFrameworks></PropertyGroup></Project>");
        string slnx = Path.Combine(dir, "Plain.slnx");
        File.WriteAllText(slnx, "<Solution><Project Path=\"Plain.csproj\" /></Solution>");
        var open = await _app.ReopenSolutionAsync(slnx);
        Assert.True(open.GetProperty("success").GetBoolean(), open.ToString());

        var status = await _app.InvokeAsync("od.launch-target.status", Path.Combine(dir, "Plain.csproj"));

        Assert.True(status.GetProperty("success").GetBoolean(), status.ToString());
        Assert.False(status.GetProperty("visible").GetBoolean(), status.ToString());
        Assert.Empty(status.GetProperty("entries").EnumerateArray());
    }

    // A desktop-only Uno project: one profile names a platform, but F5 can only start it, so there is nothing to choose.
    [Fact]
    public async Task LaunchTarget_IsHidden_ForASingleDesktopProfile()
    {
        string dir = Path.Combine(_dir, "DesktopOnly");
        Directory.CreateDirectory(Path.Combine(dir, "Properties"));
        File.WriteAllText(Path.Combine(dir, "DesktopOnly.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-desktop</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dir, "Properties", "launchSettings.json"),
            "{ \"profiles\": { \"DesktopOnly (Desktop)\": { \"commandName\": \"Project\", \"compatibleTargetFramework\": \"desktop\" } } }");
        string slnx = Path.Combine(dir, "DesktopOnly.slnx");
        File.WriteAllText(slnx, "<Solution><Project Path=\"DesktopOnly.csproj\" /></Solution>");
        var open = await _app.ReopenSolutionAsync(slnx);
        Assert.True(open.GetProperty("success").GetBoolean(), open.ToString());

        var status = await _app.InvokeAsync("od.launch-target.status", Path.Combine(dir, "DesktopOnly.csproj"));

        Assert.True(status.GetProperty("success").GetBoolean(), status.ToString());
        Assert.False(status.GetProperty("visible").GetBoolean(), status.ToString());
    }

    async Task<JsonElement> StatusAsync()
    {
        var status = await _app.InvokeAsync("od.launch-target.status", ProjectPath);
        Assert.True(status.GetProperty("success").GetBoolean(), status.ToString());
        return status;
    }

    // Device rows depend on the simulators and adb devices of the machine, so they are checked apart.
    static string[] Entries(JsonElement status) => status.GetProperty("entries").EnumerateArray()
        .Where(entry => entry.GetProperty("kind").GetString() != "Device")
        .Select(entry => entry.GetProperty("kind").GetString() + ":" + entry.GetProperty("label").GetString()).ToArray();

    static string[] DeviceEntries(JsonElement status) => status.GetProperty("entries").EnumerateArray()
        .Where(entry => entry.GetProperty("kind").GetString() == "Device")
        .Select(entry => entry.GetProperty("label").GetString()!).ToArray();

    static (string Label, string? Id, bool Selected, string Platform)[] DeviceRows(JsonElement status) => status.GetProperty("entries").EnumerateArray()
        .Where(entry => entry.GetProperty("kind").GetString() == "Device")
        .Select(entry => (entry.GetProperty("label").GetString()!,
            entry.GetProperty("deviceId").ValueKind == JsonValueKind.Null ? null : entry.GetProperty("deviceId").GetString(),
            entry.GetProperty("isSelected").GetBoolean(), entry.GetProperty("platform").GetString()!)).ToArray();

    static string? SelectedEntry(JsonElement status) => status.GetProperty("entries").EnumerateArray()
        .Where(entry => entry.GetProperty("kind").GetString() == "Profile")
        .Where(entry => entry.GetProperty("isSelected").GetBoolean())
        .Select(entry => entry.GetProperty("label").GetString()).SingleOrDefault();
}
