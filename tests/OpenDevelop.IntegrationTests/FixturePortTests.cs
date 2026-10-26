using System.Net;
using System.Net.Sockets;
using Xunit;

namespace OpenDevelop.IntegrationTests;

public sealed class FixturePortTests
{
    [Fact]
    public void PortProbe_RejectsLiveListener_AndAcceptsReleasedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        if (!OperatingSystem.IsWindows())
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try {
            Assert.True(OpenDevelopAppFixture.IsPortInUse(port));
            Assert.True(listener.Server.IsBound);
        } finally {
            listener.Stop();
        }
        Assert.False(OpenDevelopAppFixture.IsPortInUse(port));
    }

    [Fact]
    public async Task PortProbe_AcceptsPortAfterListenerClosesAnActiveConnection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        if (!OperatingSystem.IsWindows())
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using (var accepted = await listener.AcceptTcpClientAsync()) {
            accepted.Close();
        }
        listener.Stop();
        Assert.False(OpenDevelopAppFixture.IsPortInUse(port));
    }

    [Fact]
    public void PortProbe_RejectsIpv6OnlyListener()
    {
        if (!Socket.OSSupportsIPv6)
            return;
        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        try {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.True(OpenDevelopAppFixture.IsPortInUse(port));
        } finally {
            listener.Stop();
        }
    }

    [Fact]
    public async Task StartupFailure_RemovesOnlyItsPrivateConfiguration()
    {
        bool keep = Environment.GetEnvironmentVariable("OD_TEST_KEEP_ARTIFACTS") is { Length: > 0 } value && value != "0";
        var fixture = new OpenDevelopAppFixture();
        Directory.CreateDirectory(fixture.ConfigDirectory);
        var marker = Path.Combine(fixture.ConfigDirectory, "partial-startup.txt");
        await File.WriteAllTextAsync(marker, "test");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.StartWithCleanupOnFailureAsync(
            () => Task.FromException(new InvalidOperationException("startup failed"))));
        Assert.Equal("startup failed", error.Message);
        Assert.Equal(keep, Directory.Exists(fixture.ConfigDirectory));
        if (keep)
            Directory.Delete(fixture.ConfigDirectory, recursive: true);
    }

    [Fact]
    public void AbandonedConfigSweep_PreservesLiveUnmarkedAndRetainedDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "od-sweep-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            string dead = CreateMarked(int.MaxValue, old: true);
            string live = CreateMarked(Environment.ProcessId, old: true);
            string recent = CreateMarked(int.MaxValue, old: false);
            string retained = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(retained); // no marker, as with explicitly retained artifacts
            var now = DateTime.UtcNow;

            OpenDevelopAppFixture.SweepAbandonedConfigDirectories(root, now, ideRunning: true);
            Assert.True(Directory.Exists(dead));
            OpenDevelopAppFixture.SweepAbandonedConfigDirectories(root, now, ideRunning: false);
            Assert.False(Directory.Exists(dead));
            Assert.True(Directory.Exists(live));
            Assert.True(Directory.Exists(recent));
            Assert.True(Directory.Exists(retained));
        } finally {
            Directory.Delete(root, recursive: true);
        }

        string CreateMarked(int pid, bool old)
        {
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var marker = Path.Combine(directory, ".opendevelop-test-owner");
            File.WriteAllLines(marker, ["OpenDevelop integration fixture v1", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            if (old)
                File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddDays(-2));
            return directory;
        }
    }
}
