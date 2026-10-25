namespace Olivia.Core.Export;

/// <summary>Private bounded output sink. No partial output escapes when a write fails or is cancelled.</summary>
internal sealed class BoundedExportStream(int maximum, CancellationToken cancellationToken) : Stream
{
    private byte[] _buffer = [];
    private int _length;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }

    public byte[] Complete()
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _buffer.AsSpan(0, _length).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length > maximum - _length)
            throw new SnapshotExportLimitException(SnapshotExportLimit.EncodedBytes, maximum);
        var required = _length + buffer.Length;
        if (_buffer.Length < required)
        {
            var capacity = Math.Min(maximum, Math.Max(required, Math.Max(256, _buffer.Length * 2)));
            Array.Resize(ref _buffer, capacity);
        }
        buffer.CopyTo(_buffer.AsSpan(_length));
        _length = required;
    }

    public override void Flush() => cancellationToken.ThrowIfCancellationRequested();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
