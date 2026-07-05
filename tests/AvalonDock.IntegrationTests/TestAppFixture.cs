using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AvalonDock.IntegrationTests;

// Launches TestApp once per test collection, waits for the DevFlow agent (port 9223),
// and exposes helpers to invoke actions. Disposing kills the app.
public sealed class TestAppFixture : IAsyncLifetime
{
    static readonly int Port = int.TryParse(
        Environment.GetEnvironmentVariable("DEVFLOW_AGENT_PORT"), out var p) && p > 0 ? p : 9223;
    static readonly string BaseUrl = $"http://localhost:{Port}";

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly StringBuilder _appOutput = new();
    Process? _app;

    public string TestAppProjectPath { get; } = LocateTestAppProject();
    public string DotNetPath { get; } = LocateDotNet();

    public async ValueTask InitializeAsync()
    {
        StopApp();
        await WaitForPortFreeAsync(TimeSpan.FromSeconds(30));
        await StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        StopApp();
        _http.Dispose();
        await Task.CompletedTask;
    }

    async Task StartAsync()
    {
        await BuildTestAppAsync();

        var psi = new ProcessStartInfo(DotNetPath)
        {
            WorkingDirectory = Path.GetDirectoryName(TestAppProjectPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["DEVFLOW_AGENT_PORT"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
        foreach (var a in new[] { "run", "--project", TestAppProjectPath, "-f", "net11.0-windows", "--no-build" })
            psi.ArgumentList.Add(a);

        _app = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start TestApp");
        _app.OutputDataReceived += (_, e) => AppendAppOutput(e.Data);
        _app.ErrorDataReceived += (_, e) => AppendAppOutput(e.Data);
        _app.BeginOutputReadLine();
        _app.BeginErrorReadLine();

        await WaitForAgentAsync(TimeSpan.FromSeconds(120));
    }

    void StopApp()
    {
        try { if (_app is { HasExited: false }) _app.Kill(entireProcessTree: true); } catch { }
        try { foreach (var proc in Process.GetProcessesByName("TestApp")) { try { proc.Kill(true); } catch { } } } catch { }
        _app = null;
    }

    async Task WaitForAgentAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var resp = await _http.GetAsync($"{BaseUrl}/api/v1/agent/status");
                if (resp.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(1000);
        }
        throw new TimeoutException(
            $"DevFlow agent did not respond on {BaseUrl} within {timeout}.{Environment.NewLine}{GetAppOutput()}");
    }

    async Task BuildTestAppAsync()
    {
        var psi = new ProcessStartInfo(DotNetPath)
        {
            WorkingDirectory = Path.GetDirectoryName(TestAppProjectPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
        foreach (var a in new[] { "build", TestAppProjectPath, "-f", "net11.0-windows", "-v:minimal" })
            psi.ArgumentList.Add(a);

        using var build = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start TestApp build.");
        var stdout = await build.StandardOutput.ReadToEndAsync();
        var stderr = await build.StandardError.ReadToEndAsync();
        await build.WaitForExitAsync();

        if (build.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"TestApp build failed with exit code {build.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }
    }

    async Task WaitForPortFreeAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName("TestApp").Length == 0 && !IsPortInUse(Port))
                return;
            await Task.Delay(500);
        }
    }

    static bool IsPortInUse(int port)
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(ep => ep.Port == port);
        }
        catch { return false; }
    }

    public async Task<JsonElement> InvokeAsync(string action, params object[] args)
    {
        var body = JsonSerializer.Serialize(new { args });
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{BaseUrl}/api/v1/invoke/actions/{action}", content);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Action '{action}' failed ({(int)resp.StatusCode}): {err}");
        }
        var envelope = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var raw = envelope.TryGetProperty("returnValue", out var rv) ? rv.GetString() : null;
        if (string.IsNullOrEmpty(raw))
            throw new InvalidOperationException($"Action '{action}' returned no value: {envelope}");
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    public async Task<JsonElement> GetStatusAsync()
    {
        using var resp = await _http.GetAsync($"{BaseUrl}/api/v1/agent/status");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetUITreeAsync()
    {
        using var resp = await _http.GetAsync($"{BaseUrl}/api/v1/ui/tree");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    static string LocateTestAppProject()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "src", "Libraries", "AvalonDock", "source", "TestApp", "TestApp.csproj");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate TestApp.csproj by walking up from " + AppContext.BaseDirectory);
    }

    static string LocateDotNet()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath))
        {
            return hostPath;
        }

        var repoRoot = LocateRepoRoot();
        var candidate = Path.GetFullPath(Path.Combine(repoRoot, "..", "librewpf", ".dotnet", "dotnet"));
        if (File.Exists(candidate))
        {
            return candidate;
        }

        return "dotnet";
    }

    static string LocateRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "global.json")) &&
                Directory.Exists(Path.Combine(dir, "src", "Libraries", "AvalonDock")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            "Could not locate OpenDevelop repo root by walking up from " + AppContext.BaseDirectory);
    }

    void AppendAppOutput(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_appOutput)
        {
            _appOutput.AppendLine(line);
        }
    }

    string GetAppOutput()
    {
        lock (_appOutput)
        {
            return _appOutput.ToString();
        }
    }
}

[CollectionDefinition("AvalonDock app")]
public sealed class TestAppCollection : ICollectionFixture<TestAppFixture> { }
