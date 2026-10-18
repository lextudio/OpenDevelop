using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Installs a specific .NET SDK for a test with dotnetup (https://github.com/dotnet/sdk,
/// documentation/general/dotnetup), so a test can use an SDK the machine does not have - issue #19
/// needs SDK 9 on a machine whose default is SDK 10+. dotnetup is a public preview.
///
/// Everything lands under one cache directory in the temp folder and is reused by later runs: the
/// bootstrapper script, the dotnetup binary, and each SDK (installed with <c>--untracked</c> and a
/// private manifest, so nothing is written to the developer's own dotnetup state, PATH, or
/// machine-wide installation).
///
/// What is and is not verified: the bootstrapper script is downloaded over TLS from aka.ms and run
/// as-is - it is neither pinned nor signature-checked here; the script itself downloads the dotnetup
/// binary and checks its SHA-512 against Microsoft's published checksum. The cached binary is then
/// trusted by the SHA-256 recorded next to it at install time, which catches later local tampering
/// or a half-written file, not a malicious first download.
///
/// Failing to ACQUIRE the SDK (offline, blocked aka.ms, a changed script or dotnetup option) skips
/// the test instead of failing it: that is a missing prerequisite, not a product regression. A
/// completed install that is missing files still fails.
/// </summary>
static class DotnetupSdkProvider
{
    const string GetDotnetupScriptUrl = "https://aka.ms/dotnet/dotnetup/daily/get-dotnetup.ps1";
    static readonly SemaphoreSlim Gate = new(1, 1);

    static string CacheRoot => Path.Combine(Path.GetTempPath(), "opendevelop-it-dotnetup");

    /// <summary>
    /// Returns the root directory of a dotnetup-installed SDK for <paramref name="channel"/>
    /// (for example "9.0"), installing it first when it is not cached. The returned root holds
    /// <c>dotnet.exe</c> and an <c>sdk</c> folder with a complete SDK of that channel.
    /// </summary>
    public static async Task<string> EnsureSdkAsync(string channel)
    {
        // Windows only for now: the bootstrapper is a PowerShell script and the SDK root layout
        // below assumes dotnet.exe. Skipped, not failed, elsewhere.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The dotnetup bootstrap in the integration tests is wired for Windows only.");

        await Gate.WaitAsync();
        try
        {
            string sdkRoot = Path.Combine(CacheRoot, "sdk-" + channel);
            if (FindCompleteSdkVersion(sdkRoot, channel) is not null)
                return sdkRoot;

            // Anything here is a killed or failed earlier install: start clean so it can never be adopted.
            SafeDelete(sdkRoot);
            if (Directory.Exists(sdkRoot))
                Assert.Skip($"The stale SDK cache '{sdkRoot}' could not be removed (a file is probably still in use); delete it and rerun.");

            string dotnetup = await EnsureDotnetupAsync();
            Directory.CreateDirectory(sdkRoot);
            string? failure = await RunAsync(dotnetup,
                ["sdk", "install", channel,
                 "--install-path", sdkRoot,
                 "--untracked",
                 "--manifest-path", Path.Combine(CacheRoot, "manifest-" + channel + ".json"),
                 "--interactive", "false",
                 "--no-progress"],
                TimeSpan.FromMinutes(10));
            if (failure is not null)
            {
                SafeDelete(sdkRoot);
                Assert.Skip($"dotnetup could not install SDK {channel}: {failure}");
            }

            // A reported success with an incomplete tree is a real problem, not a missing prerequisite.
            Assert.True(FindCompleteSdkVersion(sdkRoot, channel) is not null,
                $"dotnetup reported success but '{sdkRoot}' holds no complete SDK for channel {channel}.");
            return sdkRoot;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// The newest SDK version under <paramref name="sdkRoot"/> that belongs to the channel AND looks
    /// completely installed (MSBuild.dll and the Microsoft.NET.Sdk props are what a finished install
    /// guarantees), or null.
    /// </summary>
    public static string? FindCompleteSdkVersion(string sdkRoot, string channel)
    {
        string sdkDir = Path.Combine(sdkRoot, "sdk");
        if (!File.Exists(Path.Combine(sdkRoot, "dotnet.exe")) || !Directory.Exists(sdkDir))
            return null;
        return Directory.GetDirectories(sdkDir)
            .Where(d => Path.GetFileName(d).StartsWith(channel + ".", StringComparison.Ordinal)
                && File.Exists(Path.Combine(d, "MSBuild.dll"))
                && File.Exists(Path.Combine(d, "Sdks", "Microsoft.NET.Sdk", "Sdk", "Sdk.props")))
            .Select(Path.GetFileName)
            .OrderBy(v => Version.TryParse(v!.Split('-')[0], out var parsed) ? parsed : new Version(0, 0))
            .LastOrDefault();
    }

    static async Task<string> EnsureDotnetupAsync()
    {
        string binDir = Path.Combine(CacheRoot, "bin");
        string exe = Path.Combine(binDir, "dotnetup.exe");
        string hashFile = exe + ".sha256";
        if (File.Exists(exe) && File.Exists(hashFile) && HashOf(exe) == (await File.ReadAllTextAsync(hashFile)).Trim())
            return exe;

        // A cached binary without a matching recorded hash is not trusted: bootstrap it again.
        SafeDelete(binDir);
        Directory.CreateDirectory(binDir);

        // The script lives apart from the install directory it writes to.
        string scriptDir = Path.Combine(CacheRoot, "scripts");
        Directory.CreateDirectory(scriptDir);
        string script = Path.Combine(scriptDir, "get-dotnetup.ps1");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            await File.WriteAllBytesAsync(script, await http.GetByteArrayAsync(GetDotnetupScriptUrl));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Could not fetch or store {GetDotnetupScriptUrl}: {ex.Message}");
        }

        // The script downloads the dotnetup binary from aka.ms and verifies its SHA-512 checksum.
        // RemoteSigned is enough: the file we wrote carries no Zone.Identifier mark.
        string? failure = await RunAsync("powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "RemoteSigned", "-File", script, "-InstallDir", binDir],
            TimeSpan.FromMinutes(3));
        if (failure is not null || !File.Exists(exe))
            Assert.Skip($"get-dotnetup.ps1 did not produce '{exe}': {failure ?? "no binary was created"}");

        await File.WriteAllTextAsync(hashFile, HashOf(exe));
        return exe;
    }

    static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    static void SafeDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { /* best effort: the next run starts clean again */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Runs a process; returns null on exit code 0, otherwise a description including its output.</summary>
    static async Task<string?> RunAsync(string fileName, string[] arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        string command = $"'{fileName} {string.Join(' ', arguments)}'";
        Process? started;
        try
        {
            started = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // e.g. powershell.exe missing or blocked by policy: a missing prerequisite.
            return $"{command} could not be started: {ex.Message}";
        }
        if (started is null)
            return $"{command} could not be started.";
        using var process = started;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return $"{command} did not finish within {timeout}.";
        }

        return process.ExitCode == 0
            ? null
            : $"{command} exited with {process.ExitCode}.{Environment.NewLine}{await stdout}{Environment.NewLine}{await stderr}";
    }
}
