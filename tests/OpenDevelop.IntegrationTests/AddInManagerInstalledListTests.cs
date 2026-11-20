using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// The AddIn Manager's Installed list in a real IDE. The add-ins the application ships (the Project Browser
/// among them) are preinstalled and offer no Uninstall, whether or not their manifest says so; an add-in in
/// the user add-in directory — where an installed .odaddin ends up — is user-installed and stays uninstallable.
/// </summary>
public class AddInManagerInstalledListTests
{
    const string ProjectBrowser = "ICSharpCode.SharpDevelop.ProjectBrowser";
    const string UserAddIn = "Vendor.User";

    [Fact]
    public async Task InstalledList_SeparatesTheApplicationAddInsFromTheUsersOwn()
    {
        string work = Path.Combine(Path.GetTempPath(), "od-it-addin-installed-" + Guid.NewGuid().ToString("N"));
        string config = Directory.CreateDirectory(Path.Combine(work, "config")).FullName;
        WriteUserAddIn(Path.Combine(config, "AddIns", UserAddIn));
        try
        {
            using var ide = await IsolatedIde.StartAsync(
                config,
                new Dictionary<string, string?> { ["OD_TEST_USER_ADDINS"] = "1" });

            var shown = await ide.InvokeAsync("od.addin-manager.show");
            Assert.True(shown.GetProperty("success").GetBoolean(), shown.ToString());

            // The window starts without the application's own add-ins, exactly like every other built-in one,
            // while the user's add-in is already listed.
            var state = await ide.InvokeAsync("od.addin-manager.state");
            Assert.DoesNotContain(Entries(state), e => e.GetProperty("id").GetString() == ProjectBrowser);
            Assert.Contains(Entries(state), e => e.GetProperty("id").GetString() == UserAddIn);

            var toggled = await ide.InvokeAsync("od.addin-manager.show-preinstalled", true);
            Assert.True(toggled.GetProperty("success").GetBoolean(), toggled.ToString());
            state = await WaitAsync(ide, s => Entries(s).Any(e => e.GetProperty("id").GetString() == ProjectBrowser),
                "the Project Browser to be listed with the preinstalled add-ins");

            JsonElement browser = Entry(state, ProjectBrowser);
            Assert.True(browser.GetProperty("installed").GetBoolean(), browser.ToString());
            Assert.True(browser.GetProperty("preinstalled").GetBoolean(), browser.ToString());
            Assert.False(browser.GetProperty("uninstallable").GetBoolean(), browser.ToString());
            Assert.False(browser.GetProperty("uninstall").GetBoolean(), browser.ToString());

            JsonElement user = Entry(state, UserAddIn);
            Assert.True(user.GetProperty("installed").GetBoolean(), user.ToString());
            Assert.False(user.GetProperty("preinstalled").GetBoolean(), user.ToString());
            Assert.True(user.GetProperty("uninstallable").GetBoolean(), user.ToString());
            Assert.True(user.GetProperty("uninstall").GetBoolean(), user.ToString());
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    /// <summary>A manifest in the user add-in directory, the shape an installed package takes after a restart.</summary>
    static void WriteUserAddIn(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, UserAddIn + ".addin"), """
            <AddIn name="User Add-in" author="x" copyright="x" url="https://example.com/" description="Installed by the user">
              <Manifest><Identity name="Vendor.User" version="1.0.0" /><Dependency addin="SharpDevelop" version="0.0-99.0" /></Manifest>
              <Runtime><Import assembly=":ICSharpCode.SharpDevelop" /></Runtime>
            </AddIn>
            """);
    }

    static IEnumerable<JsonElement> Entries(JsonElement state) =>
        state.GetProperty("lists").EnumerateArray()
            .Single(l => l.GetProperty("type").GetString() == "InstalledAddInsViewModel")
            .GetProperty("entries").EnumerateArray();

    static JsonElement Entry(JsonElement state, string id) =>
        Entries(state).Single(e => e.GetProperty("id").GetString() == id);

    static async Task<JsonElement> WaitAsync(IsolatedIde ide, Func<JsonElement, bool> condition, string what)
    {
        JsonElement last = default;
        bool ok = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            last = await ide.InvokeAsync("od.addin-manager.state");
            return condition(last);
        }, TimeSpan.FromSeconds(60));
        Assert.True(ok, $"Timed out waiting for {what}: {last}\n{ide.Output}");
        return last;
    }
}
