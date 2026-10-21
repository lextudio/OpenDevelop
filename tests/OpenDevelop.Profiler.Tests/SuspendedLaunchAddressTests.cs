using OpenDevelop.Profiler.Collection;
using Xunit;

namespace OpenDevelop.Profiler.Tests;

/// <summary>
/// The platform-dependent part of a startup launch is the diagnostic port address. These run everywhere, so a
/// Windows build still checks what macOS and Linux would be given.
/// </summary>
public class SuspendedLaunchAddressTests
{
    // A real macOS per-user temp folder, and a long one such as a CI runner's.
    const string MacTemp = "/var/folders/zz/zyxvpxvq6csfxvn_n0000000000000/T/";
    const string LongTemp = "/home/runner/work/_temp/an/unusually/deep/temporary/folder/structure/that/goes/on/and/on/";

    [Fact]
    public void Windows_UsesAPlainPipeName()
    {
        var address = SuspendedLaunch.CreatePortAddress(windows: true, @"C:\Users\someone\AppData\Local\Temp\");

        Assert.StartsWith("odp-", address);
        Assert.DoesNotContain('\\', address);
        Assert.DoesNotContain('/', address);
    }

    [Theory]
    [InlineData(MacTemp)]
    [InlineData("/tmp/")]
    [InlineData("/tmp")]
    public void Unix_PutsTheSocketInTheTempFolderWithinThePathLimit(string temp)
    {
        var address = SuspendedLaunch.CreatePortAddress(windows: false, temp);

        Assert.EndsWith(".sock", address);
        Assert.StartsWith(temp.TrimEnd('/'), address);
        Assert.True(address.Length <= 100, $"{address.Length} characters: {address}");
    }

    [Fact]
    public void Unix_FallsBackToTmpWhenTheTempFolderIsTooLongForASocketPath()
    {
        var address = SuspendedLaunch.CreatePortAddress(windows: false, LongTemp);

        // Path.Combine uses the separator of the machine the test runs on, so only the folder is compared.
        Assert.StartsWith("/tmp", address);
        Assert.DoesNotContain("structure", address);
        Assert.True(address.Length <= 100);
    }

    [Fact]
    public void Unix_CountsBytesNotCharactersAgainstTheLimit()
    {
        // 30 characters, 90 bytes: with the name and extension it is over the limit in bytes only.
        var temp = "/" + new string((char)0x4e2d, 30) + "/";

        var address = SuspendedLaunch.CreatePortAddress(windows: false, temp);

        Assert.StartsWith("/tmp", address);
    }

    [Fact]
    public void EveryLaunchGetsItsOwnAddress() =>
        Assert.NotEqual(
            SuspendedLaunch.CreatePortAddress(windows: false, "/tmp/"),
            SuspendedLaunch.CreatePortAddress(windows: false, "/tmp/"));
}
