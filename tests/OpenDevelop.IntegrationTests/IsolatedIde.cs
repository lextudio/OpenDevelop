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
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(4) };
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
        string dll = Environment.GetEnvironmentVariable("OD_IT_IDE_DLL") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Path.GetDirectoryName(OpenDevelopAppFixture.LocateOpenDevelopProject())!,
                "bin", "Debug", "net10.0-windows", "OpenDevelop.dll");
        Assert.True(File.Exists(dll),
            $"The IDE build '{dll}' was not found; building tests/OpenDevelop.IntegrationTests builds it (or set OD_IT_IDE_DLL).");

        int port = FreePort();
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
                if (!agentUp)
                {
                    using var status = await _http.GetAsync(_baseUrl + "/api/v1/agent/status");
                    agentUp = status.IsSuccessStatusCode;
                }
                if (agentUp)
                {
                    using var content = new StringContent("{\"args\":[]}", Encoding.UTF8, "application/json");
                    using var response = await _http.PostAsync(_baseUrl + "/api/v1/invoke/actions/od.active-view", content);
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
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
                stableReads = 0;
            }
            await Task.Delay(500);
        }
        Assert.Fail($"The IDE did not become ready on {_baseUrl} within 3 minutes (agent up: {agentUp}; last od.active-view response: {lastError}).\n{Output}");
    }

    public async Task<JsonElement> InvokeAsync(string action, params object[] args)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { args }), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{_baseUrl}/api/v1/invoke/actions/{action}", content);
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
