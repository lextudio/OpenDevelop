using OpenDevelop.Profiler.Heap;
using OpenDevelop.Profiler.Sessions;
using Xunit;

namespace OpenDevelop.Profiler.Tests;

public sealed class SessionStoreTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "od-profiler-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Finish_ReplacesTheIndexWithoutLeavingATemporaryFile()
    {
        var store = new ProfilingSessionStore(root);
        var session = store.Create(SessionKind.Cpu, "app", 1);

        store.Finish(session, SessionStatus.Failed, "boom");

        Assert.Empty(Directory.GetFiles(session.Directory, "*.tmp"));
        Assert.Equal(SessionStatus.Failed, Assert.Single(store.List()).Info.Status);
    }

    [Fact]
    public void Prune_RemovesTheOldestFinishedSessionsBeyondTheLimitAndSparesRunningOnes()
    {
        var store = new ProfilingSessionStore(root);
        var oldest = store.Create(SessionKind.Cpu, "oldest", 1);
        store.Finish(oldest, SessionStatus.Completed);
        Thread.Sleep(1100);
        var running = store.Create(SessionKind.Cpu, "still running", 2);
        Thread.Sleep(1100);
        var middle = store.Create(SessionKind.Heap, "middle", 3);
        store.Finish(middle, SessionStatus.Failed, "boom");
        Thread.Sleep(1100);
        var newest = store.Create(SessionKind.Cpu, "newest", 4);
        store.Finish(newest, SessionStatus.Completed);

        var removed = store.Prune(keep: 1);

        // Only the newest is within the limit; the running one is older than it but is not finished, so it stays.
        Assert.Equal(2, removed);
        Assert.Equal([newest.Info.Id, running.Info.Id], store.List().Select(s => s.Info.Id));
        Assert.False(Directory.Exists(oldest.Directory));
    }

    [Fact]
    public void Prune_WithNothingToRemoveRemovesNothing()
    {
        var store = new ProfilingSessionStore(root);
        store.Finish(store.Create(SessionKind.Cpu, "only", 1), SessionStatus.Completed);

        Assert.Equal(0, store.Prune(keep: 5));
        Assert.Single(store.List());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(199L * 1024 * 1024, false)]
    [InlineData(200L * 1024 * 1024, true)]
    [InlineData(5L * 1024 * 1024 * 1024, true)]
    public void IsLarge_WarnsFromTwoHundredMegabytes(long bytes, bool expected) =>
        Assert.Equal(expected, ReportLoader.IsLarge(bytes));

    [Fact]
    public void Create_WritesAnIndexAndNamesTheArtifact()
    {
        var store = new ProfilingSessionStore(root);

        var session = store.Create(SessionKind.Cpu, "SampleApp", 1234);

        Assert.True(File.Exists(Path.Combine(session.Directory, "session.json")));
        Assert.Equal(Path.Combine(session.Directory, "cpu.nettrace"), session.ArtifactPath);
        Assert.Equal(SessionStatus.Running, session.Info.Status);
    }

    [Fact]
    public void List_ReturnsNewestFirstAndRoundTripsFields()
    {
        var store = new ProfilingSessionStore(root);
        var first = store.Create(SessionKind.Cpu, "first", 1);
        Thread.Sleep(1100); // ids are second-resolution timestamps
        var second = store.Create(SessionKind.Heap, "second", 2);
        store.Finish(second, SessionStatus.Failed, "boom");

        var listed = store.List();

        Assert.Equal([second.Info.Id, first.Info.Id], listed.Select(s => s.Info.Id));
        Assert.Equal(SessionKind.Heap, listed[0].Info.Kind);
        Assert.Equal(SessionStatus.Failed, listed[0].Info.Status);
        Assert.Equal("boom", listed[0].Info.Error);
        Assert.Equal("heap.nettrace", listed[0].Info.Artifact);
    }

    [Fact]
    public void List_SkipsDamagedAndForeignFolders()
    {
        var store = new ProfilingSessionStore(root);
        var good = store.Create(SessionKind.Cpu, "good", null);
        var damaged = Directory.CreateDirectory(Path.Combine(root, "damaged"));
        File.WriteAllText(Path.Combine(damaged.FullName, "session.json"), "{ not json");
        Directory.CreateDirectory(Path.Combine(root, "foreign"));

        var listed = store.List();

        Assert.Equal(good.Info.Id, Assert.Single(listed).Info.Id);
    }

    [Fact]
    public void List_IsEmptyBeforeAnythingWasCaptured() =>
        Assert.Empty(new ProfilingSessionStore(root).List());

    [Fact]
    public void Delete_RemovesTheSessionFolder()
    {
        var store = new ProfilingSessionStore(root);
        var session = store.Create(SessionKind.Cpu, "x", null);

        store.Delete(session);

        Assert.False(Directory.Exists(session.Directory));
    }

    [Fact]
    public void Delete_RefusesAFolderOutsideTheStore()
    {
        var store = new ProfilingSessionStore(root);
        var outside = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "od-profiler-outside-" + Guid.NewGuid().ToString("N")));
        try
        {
            var foreign = new SessionHandle(new SessionInfo { Id = "x", Artifact = "cpu.nettrace" }, outside.FullName);

            Assert.Throws<InvalidOperationException>(() => store.Delete(foreign));
            Assert.True(outside.Exists);
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }
}

public class HeapSnapshotTests
{
    static HeapSnapshot Snapshot(params (string Name, long Count, long Size)[] types) =>
        new(types.Select(t => new HeapTypeStat(t.Name, t.Count, t.Size)).ToList());

    [Fact]
    public void Totals_SumAllTypesAndTypesAreSortedBySize()
    {
        var snapshot = Snapshot(("A", 2, 20), ("B", 1, 500), ("C", 10, 100));

        Assert.Equal(13, snapshot.ObjectCount);
        Assert.Equal(620, snapshot.HeapSize);
        Assert.Equal(["B", "C", "A"], snapshot.Types.Select(t => t.Name));
    }

    [Fact]
    public void Compare_ReportsGrowthShrinkAppearanceAndDisappearance()
    {
        var before = Snapshot(("Same", 5, 50), ("Grows", 1, 10), ("Gone", 3, 30));
        var after = Snapshot(("Same", 5, 50), ("Grows", 11, 110), ("New", 2, 20));

        var diff = after.Compare(before);

        Assert.Equal(["Grows", "Gone", "New"], diff.Select(d => d.Name));
        var grows = diff[0];
        Assert.Equal((11L, 10L, 110L, 100L), (grows.Count, grows.CountDiff, grows.Size, grows.SizeDiff));
        Assert.Equal((0L, -3L, 0L, -30L), (diff[1].Count, diff[1].CountDiff, diff[1].Size, diff[1].SizeDiff));
        Assert.DoesNotContain(diff, d => d.Name == "Same");
    }

    [Fact]
    public void Compare_OfIdenticalSnapshotsIsEmpty()
    {
        var snapshot = Snapshot(("A", 1, 1));

        Assert.Empty(snapshot.Compare(snapshot));
    }
}
