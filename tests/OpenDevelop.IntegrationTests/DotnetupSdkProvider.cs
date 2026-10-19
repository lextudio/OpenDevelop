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
/// machine-wide installation). A file lock keeps overlapping test processes from clearing each
/// other's cache.
///
/// Trust: this uses dotnetup's documented <b>preview</b> channel, not <c>daily</c> (daily builds are
/// not code-signed and are documented as for short-lived testing only). The bootstrapper script is
/// downloaded over TLS and must carry a valid Authenticode signature from Microsoft before it runs;
/// the script then downloads the dotnetup binary and checks its SHA-512, and the binary must carry
/// a valid Microsoft Authenticode signature too. The cached binary is afterwards trusted only while
/// the SHA-256 recorded next to it still matches AND it still carries that signature, so a cache
/// left by an older version of this helper (which used the unsigned daily channel) is never adopted:
/// its directory has a different name, and is deleted.
///
/// Failing to ACQUIRE the SDK (offline, blocked aka.ms, a changed script or dotnetup option, a held
/// cache lock) skips the test instead of failing it: that is a missing prerequisite, not a product
/// regression. A completed install that is missing files, or a script or binary whose signature is
/// not Microsoft's, still fails.
/// </summary>
static class DotnetupSdkProvider
{
    const string GetDotnetupScriptUrl = "https://aka.ms/dotnetup/get-dotnetup.ps1";
    // Every step of one cold acquisition has its own budget; a waiting process must outlast their
    // sum (plus margin), or it would give up on a holder that is still within its limits.
    static readonly TimeSpan ScriptFetchTimeout = TimeSpan.FromMinutes(2);
    static readonly TimeSpan SignatureCheckTimeout = TimeSpan.FromMinutes(1);
    static readonly TimeSpan ScriptRunTimeout = TimeSpan.FromMinutes(3);
    static readonly TimeSpan SdkInstallTimeout = TimeSpan.FromMinutes(10);
    static readonly TimeSpan CacheLockTimeout =
        ScriptFetchTimeout + SignatureCheckTimeout + ScriptRunTimeout + SignatureCheckTimeout + SdkInstallTimeout + TimeSpan.FromMinutes(3);
    static readonly SemaphoreSlim Gate = new(1, 1);

    // The "-preview" suffix is a trust version: an earlier helper bootstrapped dotnetup from the
    // unsigned daily channel into "opendevelop-it-dotnetup" with no signature check, and a cache
    // from it must not be reused. Bump the suffix whenever the trust rules change.
    static string CacheRoot => Path.Combine(Path.GetTempPath(), "opendevelop-it-dotnetup-preview");
    static string LegacyCacheRoot => Path.Combine(Path.GetTempPath(), "opendevelop-it-dotnetup");

    /// <summary>
    /// Returns the root directory of a dotnetup-installed SDK for <paramref name="channel"/>
    /// (for example "9.0"), installing it first when it is not cached. The returned root holds
    /// <c>dotnet.exe</c> and an <c>sdk</c> folder with a complete, stable SDK of that channel.
    /// </summary>
    public static async Task<string> EnsureSdkAsync(string channel)
    {
        // Windows only for now: the bootstrapper is a PowerShell script and the SDK root layout
        // below assumes dotnet.exe. Skipped, not failed, elsewhere.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The dotnetup bootstrap in the integration tests is wired for Windows only.");

        await Gate.WaitAsync();
        try
        {
            using var cacheLock = await AcquireCacheLockAsync();
            SafeDelete(LegacyCacheRoot);

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
                SdkInstallTimeout);
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
    /// The newest STABLE SDK version under <paramref name="sdkRoot"/> that belongs to the channel AND
    /// looks completely installed (MSBuild.dll and the Microsoft.NET.Sdk props are what a finished
    /// install guarantees), or null. Pre-release folders are ignored, as the product ignores them
    /// when it describes an SDK root (DotNetSdkService.TryDescribeRoot).
    /// </summary>
    internal static string? FindCompleteSdkVersion(string sdkRoot, string channel)
    {
        string sdkDir = Path.Combine(sdkRoot, "sdk");
        if (!File.Exists(Path.Combine(sdkRoot, "dotnet.exe")) || !Directory.Exists(sdkDir))
            return null;
        return Directory.GetDirectories(sdkDir)
            .Select(d => (Dir: d, Name: Path.GetFileName(d)))
            .Where(s => s.Name.StartsWith(channel + ".", StringComparison.Ordinal)
                && !s.Name.Contains('-')
                && Version.TryParse(s.Name, out _)
                && File.Exists(Path.Combine(s.Dir, "MSBuild.dll"))
                && File.Exists(Path.Combine(s.Dir, "Sdks", "Microsoft.NET.Sdk", "Sdk", "Sdk.props")))
            .Select(s => s.Name)
            .OrderBy(Version.Parse)
            .LastOrDefault();
    }

    /// <summary>
    /// A file lock in the cache root, so two test processes (a second run started while the first is
    /// still installing) cannot delete or half-read each other's cache. The in-process semaphore
    /// above only serialises one process.
    /// </summary>
    static async Task<FileStream> AcquireCacheLockAsync()
    {
        string lockPath = Path.Combine(CacheRoot, ".cache.lock");
        try
        {
            Directory.CreateDirectory(CacheRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"The dotnetup cache directory '{CacheRoot}' cannot be created: {ex.Message}");
        }

        var deadline = DateTime.UtcNow + CacheLockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (UnauthorizedAccessException ex)
            {
                // Not contention (that is a sharing-violation IOException): the path is unusable
                // (a directory, an ACL denial). Retrying would only wait and then mis-report it.
                Assert.Skip($"The dotnetup cache lock '{lockPath}' cannot be opened: {ex.Message}");
            }
            catch (IOException ex) when (!IsSharingViolation(ex))
            {
                // Disk full, path too long, handle exhaustion ...: not contention, so do not wait it out.
                Assert.Skip($"The dotnetup cache lock '{lockPath}' cannot be opened: {ex.Message}");
            }
            catch (IOException ex)
            {
                // Another process holds the lock: wait for it, up to the length of one cold acquisition.
                if (DateTime.UtcNow >= deadline)
                    Assert.Skip($"Another process has held the dotnetup cache lock '{lockPath}' for {CacheLockTimeout}: {ex.Message}");
                await Task.Delay(500);
            }
        }
    }

    static async Task<string> EnsureDotnetupAsync()
    {
        string binDir = Path.Combine(CacheRoot, "bin");
        string exe = Path.Combine(binDir, "dotnetup.exe");
        string hashFile = exe + ".sha256";
        if (await IsCachedBinaryTrustedAsync(exe, hashFile))
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
            using var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
            using var http = new HttpClient(handler) { Timeout = ScriptFetchTimeout };
            byte[] content = await http.GetByteArrayAsync(GetDotnetupScriptUrl);
            // A proxy or captive portal answering 200 with a block page is "aka.ms is blocked", a
            // missing prerequisite - not an untrusted script. Anything else that is not signed by
            // Microsoft still fails the signature check below.
            if (LooksLikeHtml(content))
                Assert.Skip($"{GetDotnetupScriptUrl} returned an HTML page instead of a script (a proxy or captive portal?).");
            await File.WriteAllBytesAsync(script, content);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Could not fetch or store {GetDotnetupScriptUrl}: {ex.Message}");
        }
        await RequireMicrosoftSignatureAsync(script);

        // The script downloads the dotnetup binary from aka.ms and verifies its SHA-512 checksum.
        // RemoteSigned is enough: the file we wrote carries no Zone.Identifier mark.
        string? failure = await RunAsync("powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "RemoteSigned", "-File", script, "-InstallDir", binDir],
            ScriptRunTimeout);
        if (failure is not null || !File.Exists(exe))
            Assert.Skip($"get-dotnetup.ps1 did not produce '{exe}': {failure ?? "no binary was created"}");
        await RequireMicrosoftSignatureAsync(exe);

        try
        {
            await File.WriteAllTextAsync(hashFile, HashOf(exe));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The binary is still usable for this run; without a recorded hash the next run simply
            // bootstraps it again.
        }
        return exe;
    }

    /// <summary>ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33): another process has the file open.</summary>
    internal static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    internal static bool LooksLikeHtml(byte[] content)
    {
        var text = System.Text.Encoding.UTF8.GetString(content, 0, Math.Min(content.Length, 512)).TrimStart('﻿', ' ', '\t', '\r', '\n');
        // "<#" opens a PowerShell block comment (comment-based help), not markup.
        return text.StartsWith("<", StringComparison.Ordinal) && !text.StartsWith("<#", StringComparison.Ordinal);
    }

    internal static async Task<bool> IsCachedBinaryTrustedAsync(string exe, string hashFile)
    {
        try
        {
            if (!File.Exists(exe) || !File.Exists(hashFile)
                || HashOf(exe) != (await File.ReadAllTextAsync(hashFile)).Trim())
                return false;
            // The hash only proves it is the file we recorded; the signature proves whose file it is.
            // Not Microsoft's, or not verifiable right now: not trusted, so it is bootstrapped (and
            // checked) again.
            var (verdict, _) = await CheckMicrosoftSignatureAsync(exe);
            return verdict == SignatureVerdict.Microsoft;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable (for example still held by a scanner): not trusted, bootstrap again.
            return false;
        }
    }

    // Exit codes of the signature check script below.
    const int SignatureNotMicrosoft = 2;
    const int SignatureUnverifiable = 3;

    /// <summary>
    /// Three outcomes: Microsoft-signed (returns); unsigned, tampered, or signed by someone else
    /// (FAILS - something other than Microsoft's file was served or cached); or the check could not
    /// run or reach a verdict - powershell.exe missing or blocked, a timeout, an unreachable
    /// revocation server (Assert.Skip: a missing prerequisite, like every other acquisition problem
    /// in this file). A downloaded HTML page is handled earlier, before any signature check.
    /// </summary>
    static async Task RequireMicrosoftSignatureAsync(string path)
    {
        var (verdict, detail) = await CheckMicrosoftSignatureAsync(path);
        if (verdict == SignatureVerdict.Unverifiable)
            Assert.Skip($"Could not verify the signature of '{path}': {detail}");
        Assert.True(verdict == SignatureVerdict.Microsoft, $"'{path}' is not validly signed by Microsoft: {detail}");
    }

    internal enum SignatureVerdict { Microsoft, NotMicrosoft, Unverifiable }

    /// <summary>The verdict for <paramref name="path"/> plus the diagnostic text; see <see cref="RequireMicrosoftSignatureAsync"/>.</summary>
    internal static async Task<(SignatureVerdict Verdict, string Detail)> CheckMicrosoftSignatureAsync(string path)
    {
        string escaped = path.Replace("'", "''");
        var result = await RunForResultAsync("powershell.exe",
            ["-NoProfile", "-Command",
             "$s = Get-AuthenticodeSignature -LiteralPath '" + escaped + "'; " +
             "$subject = [string]$s.SignerCertificate.Subject; " +
             "if ($s.Status -eq 'Valid' -and $subject -match 'O=Microsoft Corporation') { exit 0 }; " +
             "Write-Output ('signature: ' + $s.Status + ' ' + $subject); " +
             "if (@('Valid','NotSigned','HashMismatch') -contains [string]$s.Status) { exit " + SignatureNotMicrosoft + " } " +
             "else { exit " + SignatureUnverifiable + " }"],
            SignatureCheckTimeout);

        if (!result.Ran || (result.ExitCode != 0 && result.ExitCode != SignatureNotMicrosoft))
            return (SignatureVerdict.Unverifiable, result.Description);
        return result.ExitCode == 0
            ? (SignatureVerdict.Microsoft, "")
            : (SignatureVerdict.NotMicrosoft, result.Description);
    }

    internal static string HashOf(string path)
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

    /// <summary>
    /// The outcome of <see cref="RunForResultAsync"/>. <c>Ran</c> is false when the process never
    /// started or was killed on timeout; <c>Description</c> carries the command and, on a non-zero
    /// exit, its output.
    /// </summary>
    readonly record struct ProcessResult(bool Ran, int ExitCode, string Description);

    /// <summary>Runs a process; returns null on exit code 0, otherwise a description including its output.</summary>
    static async Task<string?> RunAsync(string fileName, string[] arguments, TimeSpan timeout)
    {
        var result = await RunForResultAsync(fileName, arguments, timeout);
        return result.Ran && result.ExitCode == 0 ? null : result.Description;
    }

    static async Task<ProcessResult> RunForResultAsync(string fileName, string[] arguments, TimeSpan timeout)
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
            return new ProcessResult(false, -1, $"{command} could not be started: {ex.Message}");
        }
        if (started is null)
            return new ProcessResult(false, -1, $"{command} could not be started.");
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
            return new ProcessResult(false, -1, $"{command} did not finish within {timeout}.");
        }

        return new ProcessResult(true, process.ExitCode,
            process.ExitCode == 0
                ? ""
                : $"{command} exited with {process.ExitCode}.{Environment.NewLine}{await stdout}{Environment.NewLine}{await stderr}");
    }
}
