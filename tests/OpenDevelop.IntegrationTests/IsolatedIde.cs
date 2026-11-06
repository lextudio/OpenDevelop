using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>One IDE process of its own: own config directory, own DevFlow port, own output capture.</summary>
internal sealed class IsolatedIde : IDisposable
{
    readonly Process _process;
    // No client-level timeout: a per-request budget (InvokeAsync's default, 4 minutes) is what bounds each action.
    // od.debug.start on a device gets a larger one - the action does the build and install before answering, and
    // HttpClient's default 100 s gave the desktop journey its "operation was canceled" on the first device build.
    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly StringBuilder _output = new();
    readonly string _baseUrl;

    IsolatedIde(Process process, int port)
    {
        _process = process;
        _baseUrl = $"http://localhost:{port}";
    }

    /// <summary>Everything the IDE has written to stdout and stderr so far.</summary>
    public string Output
    {
        get { lock (_output) return _output.ToString(); }
    }

    public static async Task<IsolatedIde> StartAsync(string configDir)
    {
        string dll = ResolveIdeDll();
        int port = FreePort();
        var psi = CreateStartInfo(dll, configDir, port);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + dll);
        var ide = new IsolatedIde(process, port);
        process.OutputDataReceived += (_, e) => ide.Append(e.Data);
        process.ErrorDataReceived += (_, e) => ide.Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await ide.WaitForAgentAsync();
        }
        catch
        {
            ide.Dispose();
            throw;
        }
        return ide;
    }

    /// <summary>
    /// Starts one IDE process, waits for it to exit, and returns its exit code and output. For the
    /// startup paths that are supposed to end the process - a missing .NET SDK - which
    /// <see cref="StartAsync"/> would otherwise report as a failure to become ready.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunToExitAsync(
        string configDir, IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
    {
        string dll = ResolveIdeDll();
        var psi = CreateStartInfo(dll, configDir, FreePort());
        foreach (var pair in environment)
            psi.Environment[pair.Key] = pair.Value;

        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null)
                return;
            lock (output)
                output.AppendLine(line);
        }

        string Output()
        {
            lock (output)
                return output.ToString();
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + dll);
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                Assert.Fail($"The IDE did not exit within {timeout}.\n{Output()}");
            }
            return (process.ExitCode, Output());
        }
        finally
        {
            process.Dispose();
        }
    }

    static string ResolveIdeDll()
    {
        string dll = Environment.GetEnvironmentVariable("OD_IT_IDE_DLL") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Path.GetDirectoryName(OpenDevelopAppFixture.LocateOpenDevelopProject())!,
                "bin", "Debug", "net10.0-windows", "OpenDevelop.dll");
        Assert.True(File.Exists(dll),
            $"The IDE build '{dll}' was not found; building tests/OpenDevelop.IntegrationTests builds it (or set OD_IT_IDE_DLL).");
        return dll;
    }

    static ProcessStartInfo CreateStartInfo(string dll, string configDir, int port)
    {
        string host = OpenDevelopAppFixture.ResolveDotNetHost();
        var psi = new ProcessStartInfo(host)
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("-configdir:" + configDir);
        psi.Environment["DEVFLOW_AGENT_PORT"] = port.ToString();
        psi.Environment["OD_TEST_MODE"] = "1";
        psi.Environment["LANG"] = "en_US.UTF-8";
        psi.Environment["LC_ALL"] = "en_US.UTF-8";
        return psi;
    }

    void Append(string? line)
    {
        if (line is null)
            return;
        lock (_output)
        {
            _output.AppendLine(line);
            if (_output.Length > 200_000)
                _output.Remove(0, _output.Length - 200_000);
        }
    }

    /// <summary>
    /// Ready means the same thing it does for the suite's shared instance (see
    /// OpenDevelopAppFixture.WaitForAgentAsync / WaitForWorkbenchReadyAsync): the DevFlow agent
    /// answers its status endpoint AND a cheap workbench-backed action (od.active-view) answers
    /// successfully twice in a row. The agent binds long before the workbench, add-ins and
    /// pads exist; actions issued on agent-up alone race the tail of startup.
    /// </summary>
    async Task WaitForAgentAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        bool agentUp = false;
        int stableReads = 0;
        string? previous = null;
        string lastError = "";
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
                Assert.Fail($"The IDE exited during startup (code {_process.ExitCode}).\n{Output}");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                if (!agentUp)
                {
                    using var status = await _http.GetAsync(_baseUrl + "/api/v1/agent/status", cts.Token);
                    agentUp = status.IsSuccessStatusCode;
                }
                if (agentUp)
                {
                    using var content = new StringContent("{\"args\":[]}", Encoding.UTF8, "application/json");
                    using var response = await _http.PostAsync(_baseUrl + "/api/v1/invoke/actions/od.active-view", content, cts.Token);
                    string body = await response.Content.ReadAsStringAsync();
                    bool success = false;
                    try { success = JsonDocument.Parse(body).RootElement.GetProperty("success").GetBoolean(); }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
                    if (response.IsSuccessStatusCode && success)
                    {
                        stableReads = body == previous ? stableReads + 1 : 0;
                        previous = body;
                        if (stableReads >= 1)
                            return;
                    }
                    else
                    {
                        lastError = body;
                        stableReads = 0;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                lastError = ex.Message;
                stableReads = 0;
            }
            await Task.Delay(500);
        }
        Assert.Fail($"The IDE did not become ready on {_baseUrl} within 3 minutes (agent up: {agentUp}; last od.active-view response: {lastError}).\n{Output}");
    }

    /// <summary>Invoke a DevFlow action with the default 4-minute request budget.</summary>
    public async Task<JsonElement> InvokeAsync(string action, params object[] args)
        => await InvokeAsync(TimeSpan.FromMinutes(4), action, args);

    /// <summary>Invoke a DevFlow action with an explicit request budget. Actions whose watchdog bounds real work (a
    /// device build inside od.debug.start) need one that outlives it.</summary>
    public async Task<JsonElement> InvokeAsync(TimeSpan timeout, string action, params object[] args)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var content = new StringContent(JsonSerializer.Serialize(new { args }), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{_baseUrl}/api/v1/invoke/actions/{action}", content, cts.Token);
        Assert.True(response.IsSuccessStatusCode,
            $"Action '{action}' failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}\n{Output}");
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        string? raw = envelope.TryGetProperty("returnValue", out var value) ? value.GetString() : null;
        Assert.False(string.IsNullOrEmpty(raw), $"Action '{action}' returned no value: {envelope}\n{Output}");
        return JsonDocument.Parse(raw!).RootElement.Clone();
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            _process.WaitForExit(15_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone, or the OS refused the kill: do not let it skip the temp-folder cleanup
        }
        _process.Dispose();
        _http.Dispose();
    }

    static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
