using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// The AddIn Manager against a fake GitHub, in a real IDE of its own: trust a repository in the options panel,
/// see its add-in in the Available list, install it, and see a newer release offered as an update. The IDE is
/// told where "GitHub" is through the OD_TEST_MODE-only OD_ADDIN_GITHUB_API / OD_ADDIN_GITHUB_DOWNLOAD overrides.
/// </summary>
public class AddInGitHubReleasesJourneyTests
{
    const string Repository = "acme/sample-addin";

    [Fact]
    public async Task TrustedRepository_ShowsItsAddIn_InstallsIt_AndOffersTheNewerReleaseAsAnUpdate()
    {
        string work = Path.Combine(Path.GetTempPath(), "od-it-addin-gh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        using var github = new FakeGitHub();
        try
        {
            byte[] v1 = BuildPackage("1.0.0");
            byte[] v11 = BuildPackage("1.1.0");
            github.Releases = ReleasesJson(github.Base, ("v1.0.0", v1));
            github.Assets["/" + Repository + "/releases/download/v1.0.0/Sample.odaddin"] = v1;
            github.Assets["/" + Repository + "/releases/download/v1.1.0/Sample.odaddin"] = v11;

            using var ide = await IsolatedIde.StartAsync(
                Directory.CreateDirectory(Path.Combine(work, "config")).FullName,
                new Dictionary<string, string?>
                {
                    ["OD_TEST_USER_ADDINS"] = "1",
                    ["OD_ADDIN_GITHUB_API"] = github.Base.ToString(),
                    ["OD_ADDIN_GITHUB_DOWNLOAD"] = github.Base.ToString(),
                });

            // 1. Nothing is trusted yet: the window says so and lists nothing available.
            var shown = await ide.InvokeAsync("od.addin-manager.show");
            Assert.True(shown.GetProperty("success").GetBoolean(), shown.ToString());
            Assert.Empty(github.ApiRequests);
            Assert.Empty(Entries(await ide.InvokeAsync("od.addin-manager.state"), "AvailableAddInsViewModel"));

            // 2. Trust the repository through the real options panel, then press Refresh.
            // Trusting asks "an add-in runs with all your rights - trust it?"; test mode needs the answer queued.
            await ide.InvokeAsync("od.test.queue-dialog-answer", "question", "[\"yes\"]");
            var trusted = await ide.InvokeAsync("od.addin-manager.trust", Repository);
            Assert.True(trusted.GetProperty("success").GetBoolean(), trusted.ToString());
            Assert.True((await ide.InvokeAsync("od.addin-manager.refresh")).GetProperty("success").GetBoolean());
            JsonElement state = await WaitAsync(ide, s => Entries(s, "AvailableAddInsViewModel").Any(), "the add-in to be listed as available");
            var available = Entries(state, "AvailableAddInsViewModel").Single();
            Assert.Equal("1.0.0", available.GetProperty("version").GetString());
            Assert.True(available.GetProperty("installable").GetBoolean());
            string name = available.GetProperty("name").GetString()!;
            Assert.All(github.ApiRequests.Concat(github.AssetRequests), r => Assert.Null(r.Authorization));

            // 3. Install it: the file is downloaded, verified and placed for the next start.
            Assert.True((await ide.InvokeAsync("od.addin-manager.install", "Available", name)).GetProperty("success").GetBoolean());
            state = await WaitAsync(ide, s => Entries(s, "InstalledAddInsViewModel").Any(e => e.GetProperty("name").GetString() == name),
                "the add-in to appear in the Installed list");
            // A package the user installed is user-installed, not one of the application's own: it stays uninstallable.
            JsonElement installedEntry = Entries(state, "InstalledAddInsViewModel").Single(e => e.GetProperty("name").GetString() == name);
            Assert.True(installedEntry.GetProperty("installed").GetBoolean(), installedEntry.ToString());
            Assert.False(installedEntry.GetProperty("preinstalled").GetBoolean(), installedEntry.ToString());
            Assert.True(installedEntry.GetProperty("uninstallable").GetBoolean(), installedEntry.ToString());
            string staged = Path.Combine(work, "config");
            Assert.True(Directory.EnumerateFiles(staged, "Vendor.Sample.addin", SearchOption.AllDirectories).Any()
                    || Directory.EnumerateFiles(staged, "Sample.dll", SearchOption.AllDirectories).Any(),
                "the installed package's files were not placed under the configuration directory");
            Assert.Contains(Repository, github.AssetRequests.Select(r => r.Path).First());

            // 4. A newer release is published: Refresh offers it in Updates.
            github.Releases = ReleasesJson(github.Base, ("v1.0.0", v1), ("v1.1.0", v11));
            await ide.InvokeAsync("od.addin-manager.refresh");
            state = await WaitAsync(ide, s => Entries(s, "UpdatedAddInsViewModel").Any(), "the update to be offered");
            Assert.Equal("1.1.0", Entries(state, "UpdatedAddInsViewModel").Single().GetProperty("version").GetString());
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    static IEnumerable<JsonElement> Entries(JsonElement state, string listType) =>
        state.GetProperty("lists").EnumerateArray()
            .Single(l => l.GetProperty("type").GetString() == listType)
            .GetProperty("entries").EnumerateArray();

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

    static byte[] BuildPackage(string version)
    {
        string manifest = $"""
            <AddIn name="Sample" author="x" copyright="x" url="https://example.com/" description="A sample add-in">
              <Manifest><Identity name="Vendor.Sample" version="{version}" /><Dependency addin="SharpDevelop" version="0.0-99.0" /></Manifest>
              <Runtime><Import assembly="Sample.dll" /></Runtime>
            </AddIn>
            """;
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (entryName, text) in new[] { ("Vendor.Sample.addin", manifest), ("Sample.dll", "dll " + version) })
            {
                using var writer = new StreamWriter(zip.CreateEntry(entryName).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }
        return stream.ToArray();
    }

    static string ReleasesJson(Uri downloadBase, params (string Tag, byte[] Content)[] releases) =>
        JsonSerializer.Serialize(releases.Reverse().Select(r => new Dictionary<string, object?>
        {
            ["tag_name"] = r.Tag,
            ["prerelease"] = false,
            ["draft"] = false,
            ["body"] = "notes for " + r.Tag,
            ["published_at"] = "2026-11-01T10:00:00Z",
            ["assets"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "Sample.odaddin",
                    ["state"] = "uploaded",
                    ["size"] = r.Content.Length,
                    ["digest"] = "sha256:" + Convert.ToHexString(SHA256.HashData(r.Content)).ToLowerInvariant(),
                    ["browser_download_url"] = new Uri(downloadBase, $"{Repository}/releases/download/{r.Tag}/Sample.odaddin").ToString(),
                },
            },
        }));

    /// <summary>api.github.com and the asset host in one loopback server: releases JSON plus the package bytes.</summary>
    sealed class FakeGitHub : IDisposable
    {
        readonly HttpListener listener = new();
        public Uri Base { get; }
        public string Releases { get; set; } = "[]";
        public Dictionary<string, byte[]> Assets { get; } = new();
        public List<(string Path, string? Authorization)> ApiRequests { get; } = new();
        public List<(string Path, string? Authorization)> AssetRequests { get; } = new();

        public FakeGitHub()
        {
            int port;
            using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            Base = new Uri($"http://127.0.0.1:{port}/");
            listener.Prefixes.Add(Base.ToString());
            listener.Start();
            _ = Task.Run(Serve);
        }

        async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception) { return; }
                try
                {
                    string path = context.Request.Url!.AbsolutePath;
                    string? auth = context.Request.Headers["Authorization"];
                    if (path == "/repos/" + Repository + "/releases")
                    {
                        lock (ApiRequests) ApiRequests.Add((path, auth));
                        context.Response.ContentType = "application/json";
                        context.Response.OutputStream.Write(Encoding.UTF8.GetBytes(Releases));
                    }
                    else if (Assets.TryGetValue(path, out var bytes))
                    {
                        lock (AssetRequests) AssetRequests.Add((path, auth));
                        context.Response.OutputStream.Write(bytes);
                    }
                    else
                        context.Response.StatusCode = 404;
                }
                catch (Exception) { context.Response.StatusCode = 500; }
                finally { context.Response.Close(); }
            }
        }

        public void Dispose() => listener.Close();
    }
}
