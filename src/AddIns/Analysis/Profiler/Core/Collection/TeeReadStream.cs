namespace OpenDevelop.Profiler.Collection;

/// <summary>The copy a <see cref="TeeReadStream"/> makes could not be written, e.g. the disk is full.</summary>
internal sealed class SinkWriteException(Exception inner) : Exception(inner.Message, inner);

/// <summary>
/// A forward-only read stream that copies every byte it hands out to a second stream. A failure to write the copy is
/// reported as <see cref="SinkWriteException"/>, so it cannot be mistaken for the source stream ending.
/// </summary>
internal sealed class TeeReadStream(Stream source, Stream sink) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (read > 0)
            Copy(buffer.AsSpan(offset, read));
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = source.Read(buffer);
        if (read > 0)
            Copy(buffer[..read]);
        return read;
    }

    void Copy(ReadOnlySpan<byte> bytes)
    {
        try
        {
            sink.Write(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
        {
            throw new SinkWriteException(exception);
        }
    }

    public override void Flush() => sink.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
