namespace DotNodeViewer.Core.Delegates.Xnnpack;

/// <summary>
/// Provides a seekable, read-only bounded window over an underlying seekable <see cref="Stream"/> without closing it on disposal.
/// </summary>
internal sealed class BoundedReadOnlyStream : Stream
{
    private readonly Stream _inner;
    private readonly long _baseOffset;
    private readonly long _length;
    private long _position;

    /// <summary>
    /// Initializes a new instance of the <see cref="BoundedReadOnlyStream"/> class.
    /// </summary>
    /// <param name="inner">The underlying readable, seekable stream.</param>
    /// <param name="baseOffset">The inclusive starting byte offset in <paramref name="inner"/>.</param>
    /// <param name="length">The total byte length of the window.</param>
    public BoundedReadOnlyStream(Stream inner, long baseOffset, long length)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _inner = inner;
        _baseOffset = baseOffset;
        _length = length;
        _position = 0;
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => true;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => _length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length || buffer.IsEmpty)
        {
            return 0;
        }

        int maxToRead = (int)Math.Min(buffer.Length, _length - _position);
        _inner.Position = _baseOffset + _position;
        int read = _inner.Read(buffer[..maxToRead]);
        _position += read;
        return read;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        ArgumentOutOfRangeException.ThrowIfNegative(target);
        _position = target;
        return _position;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
