using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// The Android SDK Manager window end-to-end, against a fake <c>sdkmanager</c> in a temporary SDK root:
/// open the real window, type a location (the editable ComboBox fix), see the parsed package trees
/// (SharpTreeView row template and tab order), tick a package, apply the change, and close it.
/// The sdkmanager output is the same table the parser unit tests use, so no Android SDK or Java is needed.
/// </summary>
public sealed class AndroidSdkManagerIntegrationTests
{
    const string ListTable = """
Loading package information...
Installed packages:
  Path                                  | Version | Description                    | Location
  -------                               | ------- | -------                        | -------
  build-tools;34.0.0                    | 34.0.0  | Android SDK Build-Tools 34     | build-tools/34.0.0
  platforms;android-36                  | 2       | Android SDK Platform 36        | platforms/android-36
Available Packages:
  Path                                  | Version | Description
  -------                               | ------- | -------
  platforms;android-10                  | 2       | Android SDK Platform 10
  build-tools;34.0.0                    | 35.0.0  | Android SDK Build-Tools 34
Available Updates:
  ID                                    | Installed | Available
  -------                               | -------   | -------
  build-tools;34.0.0                    | 34.0.0    | 35.0.0
""";

    [Fact]
    public async Task SdkManager_ListsPackages_TogglesTheCheckbox_AndAppliesTheInstall()
    {
        string work = Path.Combine(Path.GetTempPath(), "od-it-androidsdk-" + Guid.NewGuid().ToString("N"));
        string config = Directory.CreateDirectory(Path.Combine(work, "config")).FullName;
        string sdk = CreateFakeSdk(Path.Combine(work, "sdk"));
        string calls = Path.Combine(sdk, "sdkmanager-calls.txt");
        try
        {
            using var ide = await IsolatedIde.StartAsync(config);

            var opened = await ide.InvokeAsync("od.androidsdk.open-window");
            Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

            // The window's own initialization runs first with the empty saved location and publishes no
            // tree; list-packages must stay answerable in that state (it is the polling action below).
            var before = await ide.InvokeAsync("od.androidsdk.list-packages");
            Assert.True(before.GetProperty("open").GetBoolean(), before.ToString());
            Assert.Empty(before.GetProperty("packages").EnumerateArray());

            var set = await ide.InvokeAsync("od.androidsdk.set-location", sdk);
            Assert.True(set.GetProperty("success").GetBoolean(), set.ToString());

            var packages = await WaitAsync(ide,
                state => state.GetProperty("packages").GetArrayLength() == 3,
                "the three packages from the fake sdkmanager --list output");

            JsonElement ById(string id) => packages.GetProperty("packages").EnumerateArray()
                .Single(p => p.GetProperty("id").GetString() == id);

            JsonElement buildTools = ById("build-tools;34.0.0");
            Assert.True(buildTools.GetProperty("isInstalled").GetBoolean(), buildTools.ToString());
            Assert.True(buildTools.GetProperty("hasUpdate").GetBoolean(), buildTools.ToString());
            Assert.True(buildTools.GetProperty("isChecked").GetBoolean(), buildTools.ToString());

            JsonElement platform36 = ById("platforms;android-36");
            Assert.True(platform36.GetProperty("isInstalled").GetBoolean(), platform36.ToString());
            Assert.True(platform36.GetProperty("isChecked").GetBoolean(), platform36.ToString());

            JsonElement platform10 = ById("platforms;android-10");
            Assert.False(platform10.GetProperty("isInstalled").GetBoolean(), platform10.ToString());
            Assert.False(platform10.GetProperty("isChecked").GetBoolean(), platform10.ToString());

            // Tick the available platform the way the user clicks its checkbox.
            var checkedResult = await ide.InvokeAsync("od.androidsdk.set-checked", "platforms;android-10", true);
            Assert.True(checkedResult.GetProperty("success").GetBoolean(), checkedResult.ToString());
            packages = await WaitAsync(ide,
                state => state.GetProperty("packages").EnumerateArray()
                    .Single(p => p.GetProperty("id").GetString() == "platforms;android-10")
                    .GetProperty("isChecked").GetBoolean(),
                "the ticked platform row");

            var applied = await ide.InvokeAsync("od.androidsdk.apply-changes");
            Assert.True(applied.GetProperty("success").GetBoolean(), applied.ToString());

            bool installed = await OpenDevelopAppFixture.PollUntilAsync(async () =>
            {
                try {
                    return File.Exists(calls) &&
                        File.ReadAllText(calls).Contains("--install") &&
                        File.ReadAllText(calls).Contains("platforms;android-10");
                } catch (IOException) {
                    return false;
                }
            }, TimeSpan.FromSeconds(30));
            Assert.True(installed, $"The fake sdkmanager never received --install for the ticked package.\n{ide.Output}");

            var closed = await ide.InvokeAsync("od.androidsdk.close-window");
            Assert.True(closed.GetProperty("success").GetBoolean(), closed.ToString());
            var afterClose = await ide.InvokeAsync("od.androidsdk.list-packages");
            Assert.False(afterClose.GetProperty("open").GetBoolean(), afterClose.ToString());
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    static async Task<JsonElement> WaitAsync(IsolatedIde ide, Func<JsonElement, bool> condition, string what)
    {
        JsonElement last = default;
        bool ok = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            last = await ide.InvokeAsync("od.androidsdk.list-packages");
            return last.TryGetProperty("open", out var open) && open.GetBoolean() && condition(last);
        }, TimeSpan.FromSeconds(60));
        Assert.True(ok, $"Timed out waiting for {what}: {last}\n{ide.Output}");
        return last;
    }

    /// <summary>
    /// A temporary SDK root shaped like a real one: the service finds
    /// <c>cmdline-tools/latest/bin/sdkmanager</c> and runs it with <c>ANDROID_HOME</c> set to the root.
    /// The executable answers <c>--list</c> with the canned table and records every other invocation.
    /// </summary>
    static string CreateFakeSdk(string root)
    {
        string bin = Directory.CreateDirectory(Path.Combine(root, "cmdline-tools", "latest", "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "sdkmanager-list.txt"), ListTable);

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(bin, "sdkmanager.bat"), """
@echo off
if "%~1"=="--list" (
  type "%~dp0sdkmanager-list.txt"
) else (
  echo %*>>"%ANDROID_HOME%\sdkmanager-calls.txt"
)
""");
            return root;
        }

        string script = Path.Combine(bin, "sdkmanager");
        File.WriteAllText(script, """
#!/bin/sh
if [ "$1" = "--list" ]; then
  cat "$(dirname "$0")/sdkmanager-list.txt"
else
  printf '%s\n' "$@" >> "$ANDROID_HOME/sdkmanager-calls.txt"
fi
exit 0
""");
        File.SetUnixFileMode(script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        return root;
    }
}
