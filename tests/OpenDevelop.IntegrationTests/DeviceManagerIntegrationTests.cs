using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// The shared Device Manager window in a real IDE: open it, read the same snapshot it shows, refresh
/// every provider, and close it. Device contents are platform-dependent (this computer always, iOS
/// simulators on macOS, AVDs only where an Android SDK exists), so the journey pins the snapshot's
/// shape and the host row instead of a machine-specific list.
/// </summary>
public sealed class DeviceManagerIntegrationTests
{
    [Fact]
    public async Task DeviceManager_OpensListsRefreshesAndCloses()
    {
        string work = Path.Combine(Path.GetTempPath(), "od-it-devices-" + Guid.NewGuid().ToString("N"));
        string config = Directory.CreateDirectory(Path.Combine(work, "config")).FullName;
        try
        {
            using var ide = await IsolatedIde.StartAsync(config);

            var opened = await ide.InvokeAsync("od.devices.open-window");
            Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

            var initial = await ide.InvokeAsync("od.devices.list");
            Assert.True(initial.GetProperty("open").GetBoolean(), initial.ToString());
            var devices = initial.GetProperty("devices").EnumerateArray().ToList();
            Assert.Contains(devices, IsHost);
            AssertEveryRowHasItsFields(devices);

            var refreshed = await ide.InvokeAsync("od.devices.refresh");
            Assert.True(refreshed.GetProperty("success").GetBoolean(), refreshed.ToString());
            int count = refreshed.GetProperty("count").GetInt32();
            Assert.True(count >= 1, $"The host computer must always be listed: {refreshed}");

            // The window refreshes on load as well; wait until the published snapshot is the one the
            // refresh just reported instead of racing the two.
            JsonElement published = default;
            bool settled = await OpenDevelopAppFixture.PollUntilAsync(async () =>
            {
                published = await ide.InvokeAsync("od.devices.list");
                return published.GetProperty("devices").GetArrayLength() == count;
            }, TimeSpan.FromSeconds(30));
            Assert.True(settled, $"The published device list never reached {count} rows: {published}\n{ide.Output}");

            devices = published.GetProperty("devices").EnumerateArray().ToList();
            Assert.Contains(devices, IsHost);
            AssertEveryRowHasItsFields(devices);

            var ids = devices.Select(d => d.GetProperty("id").GetString()).ToList();
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

            var closed = await ide.InvokeAsync("od.devices.close-window");
            Assert.True(closed.GetProperty("success").GetBoolean(), closed.ToString());
            var afterClose = await ide.InvokeAsync("od.devices.list");
            Assert.False(afterClose.GetProperty("open").GetBoolean(), afterClose.ToString());
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    static bool IsHost(JsonElement device) =>
        device.GetProperty("platform").GetString() == "Host" &&
        device.GetProperty("id").GetString() == "host";

    static void AssertEveryRowHasItsFields(IEnumerable<JsonElement> devices)
    {
        foreach (JsonElement device in devices)
        {
            Assert.False(string.IsNullOrEmpty(device.GetProperty("platform").GetString()), device.ToString());
            Assert.False(string.IsNullOrEmpty(device.GetProperty("id").GetString()), device.ToString());
            Assert.False(string.IsNullOrEmpty(device.GetProperty("name").GetString()), device.ToString());
            Assert.False(string.IsNullOrEmpty(device.GetProperty("runtime").GetString()), device.ToString());
            Assert.False(string.IsNullOrEmpty(device.GetProperty("state").GetString()), device.ToString());
        }
    }
}
