namespace Gdrive.Mcp;

/// <summary>
/// A forward-only, read-only pass-through over a source stream that enforces a hard byte
/// ceiling. Backs <c>upload_from_url</c>: the Drive resumable uploader reads the source
/// sequentially, and this wrapper throws once more than <c>maxBytes</c> have been read — so a
/// source that omits or lies about its <c>Content-Length</c> still cannot push an unbounded
/// upload into Drive. Non-seekable by design (the resumable uploader buffers each chunk itself,
/// so it never needs to seek the source), which also keeps memory constant.
/// </summary>
internal sealed class BoundedStream(Stream inner, long maxBytes) : Stream
{
    private long _read;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct);
        Accumulate(n);
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        Accumulate(n);
        return n;
    }

    private void Accumulate(int n)
    {
        if (n <= 0) return;
        _read += n;
        if (_read > maxBytes)
            throw new IOException($"source exceeded max {maxBytes} bytes (GDRIVE_MCP_UPLOAD_MAX_BYTES)");
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
