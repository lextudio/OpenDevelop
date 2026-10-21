using OpenDevelop.Profiler.Collection;
using Xunit;

namespace OpenDevelop.Profiler.Tests;

public class TeeReadStreamTests
{
    sealed class FailingSink : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("disk full");
    }

    [Fact]
    public void CopiesEverythingItReads()
    {
        var sink = new MemoryStream();
        using var tee = new TeeReadStream(new MemoryStream([1, 2, 3, 4]), sink);

        var buffer = new byte[4];
        Assert.Equal(4, tee.Read(buffer, 0, 4));

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, sink.ToArray());
    }

    [Fact]
    public void TheSpanOverloadCopiesToo()
    {
        var sink = new MemoryStream();
        using var tee = new TeeReadStream(new MemoryStream([9, 8, 7]), sink);

        Assert.Equal(3, tee.Read(new byte[3].AsSpan()));

        Assert.Equal(new byte[] { 9, 8, 7 }, sink.ToArray());
    }

    [Fact]
    public void AFailingSinkIsReportedAsASinkWriteNotAsTheEndOfTheSource()
    {
        using var tee = new TeeReadStream(new MemoryStream([1, 2, 3]), new FailingSink());

        var exception = Assert.Throws<SinkWriteException>(() => tee.Read(new byte[3], 0, 3));

        Assert.IsType<IOException>(exception.InnerException);
    }
}

public class HeapCaptureJudgementTests
{
    static string? Judge(bool timedOut = false, bool sawSnapshot = true, bool snapshotDone = true, bool stopRequested = true) =>
        HeapCapture.Judge(timedOut, sawSnapshot, snapshotDone, stopRequested, 1, TimeSpan.FromSeconds(5));

    [Fact]
    public void ACompleteSnapshotHasNoProblem() => Assert.Null(Judge());

    [Fact]
    public void ATargetThatEndsMidDumpIsIncomplete() =>
        Assert.Contains("incomplete", Judge(snapshotDone: false, stopRequested: false));

    [Fact]
    public void AStreamEndedByOurOwnStopIsNotATruncation() => Assert.Null(Judge(snapshotDone: false, stopRequested: true));

    [Fact]
    public void NoSnapshotEventsMeansTheTargetEndedFirst() => Assert.Contains("before it reported", Judge(sawSnapshot: false));

    [Fact]
    public void ATimeoutIsReportedAsOne() => Assert.Contains("did not finish", Judge(timedOut: true));
}
