using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using Xunit;

namespace OpenDevelop.Base.Tests;

public class PersistentSyntaxIndexTests
{
    static RoslynHostProcessTransport Host() => new(Path.Combine(AppContext.BaseDirectory, "RoslynHost", "ICSharpCode.Roslyn.Host.dll"), TimeSpan.FromSeconds(60));

    [Fact]
    public async Task IndexSurvivesHostReplacementAndRebuildsAfterCorruptionOrSameTimestampEdits()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("PersistentRoslyn-");
        try
        {
            var path = Path.Combine(directory.FullName, "Wanted.cs");
            await File.WriteAllTextAsync(path, "class Wanted { public void Run() {} }", token);
            var snapshot = new LanguageServiceProjectSnapshot(Path.Combine(directory.FullName, "Wanted.csproj"),
                "C#", new[] { path }, new[] { typeof(object).Assembly.Location }, Array.Empty<string>(), Array.Empty<string>(), null, null)
                .WithSolutionDirectory(directory.FullName);
            async Task<(WorkspaceStatus Status, IReadOnlyList<NavigationTarget> Targets)> Query(string type)
            {
                using var host = Host();
                using var protocol = new RemoteRoslynLanguageProtocol(host);
                await protocol.RoslynProjectLoadAsync(snapshot, token);
                var result = await protocol.RoslynFindMemberAsync(type, "Run", 0, token);
                return (await protocol.RoslynStatusAsync(token), result.Value);
            }

            var cold = await Query("Wanted");
            Assert.Equal(path, Assert.Single(cold.Targets).FileName);
            Assert.Equal(1, cold.Status.SyntaxIndexBuilds);
            Assert.Equal(0, cold.Status.SyntaxIndexDiskHits);
            var warm = await Query("Wanted");
            Assert.Equal(path, Assert.Single(warm.Targets).FileName);
            Assert.Equal(0, warm.Status.SyntaxIndexBuilds);
            Assert.Equal(1, warm.Status.SyntaxIndexDiskHits);

            var cache = Path.Combine(directory.FullName, ".od", "roslyn-index", "v1");
            var entry = Assert.Single(Directory.GetFiles(cache, "*.json"));
            await File.WriteAllTextAsync(entry, "{broken json", token);
            var repaired = await Query("Wanted");
            Assert.Single(repaired.Targets);
            Assert.Equal(1, repaired.Status.SyntaxIndexBuilds);

            var stamp = File.GetLastWriteTimeUtc(path);
            await File.WriteAllTextAsync(path, "class Changed { public void Run() {} }", token);
            File.SetLastWriteTimeUtc(path, stamp);
            var changed = await Query("Changed");
            Assert.Single(changed.Targets);
            Assert.Equal(1, changed.Status.SyntaxIndexBuilds);
            Assert.Empty((await Query("Wanted")).Targets);
            Assert.Empty(Directory.GetFiles(cache, "*.tmp"));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task ConcurrentHostsProduceOneReadableChecksumEntry()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("PersistentRoslynConcurrent-");
        try
        {
            var path = Path.Combine(directory.FullName, "Concurrent.cs");
            await File.WriteAllTextAsync(path, "class Concurrent { public void Run() {} }", token);
            var snapshot = new LanguageServiceProjectSnapshot(Path.Combine(directory.FullName, "Concurrent.csproj"),
                "C#", new[] { path }, new[] { typeof(object).Assembly.Location }, Array.Empty<string>(), Array.Empty<string>(), null, null)
                .WithSolutionDirectory(directory.FullName);
            async Task Query()
            {
                using var host = Host();
                using var protocol = new RemoteRoslynLanguageProtocol(host);
                await protocol.RoslynProjectLoadAsync(snapshot, token);
                Assert.Single((await protocol.RoslynFindMemberAsync("Concurrent", "Run", 0, token)).Value);
            }

            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Query()));
            var cache = Path.Combine(directory.FullName, ".od", "roslyn-index", "v1");
            Assert.Single(Directory.GetFiles(cache, "*.json"));
            Assert.Empty(Directory.GetFiles(cache, "*.tmp"));

            // A fresh process must accept the entry rather than rebuilding after the concurrent writes.
            using var verifyingHost = Host();
            using var verifyingProtocol = new RemoteRoslynLanguageProtocol(verifyingHost);
            await verifyingProtocol.RoslynProjectLoadAsync(snapshot, token);
            Assert.Single((await verifyingProtocol.RoslynFindMemberAsync("Concurrent", "Run", 0, token)).Value);
            var status = await verifyingProtocol.RoslynStatusAsync(token);
            Assert.Equal(1, status.SyntaxIndexDiskHits);
            Assert.Equal(0, status.SyntaxIndexBuilds);
        }
        finally { directory.Delete(true); }
    }
}
