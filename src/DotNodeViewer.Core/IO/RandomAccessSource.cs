using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using DotNodeViewer.Core.Abstractions;

namespace DotNodeViewer.Core.IO;

/// <summary>
/// Provides random-access reading over a seekable <see cref="Stream"/> backed by an LRU cache of 64 KB <see cref="ArrayPool{T}"/> pages.
/// </summary>
internal sealed class RandomAccessSource : IDisposable
{
    /// <summary>
    /// The size of each cached page in bytes (65,536 bytes).
    /// </summary>
    public const int PageSize = 64 * 1024;

    /// <summary>
    /// The default maximum number of 64 KB pages retained simultaneously in the LRU cache.
    /// </summary>
    public const int DefaultMaxPages = 16;

    private const int PageShift = 16;
    private const int PageMask = PageSize - 1;

    private readonly Stream _stream;
    private readonly int _maxPages;
    private readonly PageSlot[] _slots;
    private int _slotCount;
    private long _accessCounter;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RandomAccessSource"/> class over the specified seekable <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">The readable, seekable stream to wrap.</param>
    /// <param name="maxPages">The maximum number of 64 KB pages to keep in the LRU cache.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPages"/> is less than 1.</exception>
    /// <exception cref="ModelParseException"><paramref name="stream"/> is not readable or not seekable.</exception>
    public RandomAccessSource(Stream stream, int maxPages = DefaultMaxPages)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPages, 1);

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ModelParseException("Model stream must be readable and seekable for random-access parsing.");
        }

        _stream = stream;
        _maxPages = maxPages;
        _slots = new PageSlot[maxPages];
        Length = stream.Length;
    }

    /// <summary>
    /// Gets the total length of the underlying stream in bytes.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Gets the number of page-cache hits recorded by this instance.
    /// </summary>
    public long CacheHitCount { get; private set; }

    /// <summary>
    /// Gets the number of page-cache misses (underlying stream reads) recorded by this instance.
    /// </summary>
    public long CacheMissCount { get; private set; }

    /// <summary>
    /// Reads the exact number of bytes required to fill <paramref name="destination"/> starting at <paramref name="offset"/>.
    /// </summary>
    /// <param name="offset">The zero-based byte offset in the stream.</param>
    /// <param name="destination">The span to fill with bytes from the stream.</param>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
    /// <exception cref="ModelParseException">The requested byte range falls outside the bounds of the stream.</exception>
    public void ReadExactly(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRange(offset, destination.Length);

        int remaining = destination.Length;
        long currentOffset = offset;
        int destOffset = 0;

        while (remaining > 0)
        {
            long pageIndex = currentOffset >> PageShift;
            int offsetInPage = (int)(currentOffset & PageMask);
            ref PageSlot page = ref GetOrLoadPage(pageIndex);

            int availableInPage = page.ValidLength - offsetInPage;
            if (availableInPage <= 0)
            {
                throw new ModelParseException(
                    $"Unexpected end of stream while reading at offset 0x{currentOffset:X}.",
                    fileOffset: currentOffset);
            }

            int toCopy = Math.Min(remaining, availableInPage);
            page.Buffer.AsSpan(offsetInPage, toCopy).CopyTo(destination.Slice(destOffset, toCopy));

            currentOffset += toCopy;
            destOffset += toCopy;
            remaining -= toCopy;
        }
    }

    /// <summary>
    /// Returns a <see cref="ReadOnlySpan{T}"/> over <paramref name="length"/> bytes at <paramref name="offset"/>, slicing directly
    /// into the cached page when the range does not cross a 64 KB page boundary, or copying into <paramref name="scratch"/> otherwise.
    /// </summary>
    /// <param name="offset">The zero-based byte offset in the stream.</param>
    /// <param name="length">The number of bytes to view.</param>
    /// <param name="scratch">A caller-provided scratch buffer of at least <paramref name="length"/> bytes used when the range straddles a page boundary.</param>
    /// <returns>A read-only span of <paramref name="length"/> bytes.</returns>
    public ReadOnlySpan<byte> GetSpan(long offset, int length, Span<byte> scratch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateRange(offset, length);

        if (length == 0)
        {
            return [];
        }

        long startPage = offset >> PageShift;
        long endPage = (offset + length - 1) >> PageShift;
        if (startPage == endPage)
        {
            int offsetInPage = (int)(offset & PageMask);
            ref PageSlot page = ref GetOrLoadPage(startPage);
            if (offsetInPage + length > page.ValidLength)
            {
                throw new ModelParseException(
                    $"Unexpected end of stream while reading {length} bytes at offset 0x{offset:X}.",
                    fileOffset: offset);
            }

            return page.Buffer.AsSpan(offsetInPage, length);
        }

        if (scratch.Length < length)
        {
            throw new ArgumentException("Scratch buffer is smaller than the requested span length.", nameof(scratch));
        }

        Span<byte> slice = scratch[..length];
        ReadExactly(offset, slice);
        return slice;
    }

    /// <summary>
    /// Reads a single unsigned byte at <paramref name="offset"/>.
    /// </summary>
    public byte ReadByte(long offset)
    {
        Span<byte> buf = stackalloc byte[1];
        ReadOnlySpan<byte> span = GetSpan(offset, 1, buf);
        return span[0];
    }

    /// <summary>
    /// Reads a 16-bit signed little-endian integer at <paramref name="offset"/>.
    /// </summary>
    public short ReadInt16LittleEndian(long offset)
    {
        Span<byte> buf = stackalloc byte[2];
        return BinaryPrimitives.ReadInt16LittleEndian(GetSpan(offset, 2, buf));
    }

    /// <summary>
    /// Reads a 16-bit unsigned little-endian integer at <paramref name="offset"/>.
    /// </summary>
    public ushort ReadUInt16LittleEndian(long offset)
    {
        Span<byte> buf = stackalloc byte[2];
        return BinaryPrimitives.ReadUInt16LittleEndian(GetSpan(offset, 2, buf));
    }

    /// <summary>
    /// Reads a 32-bit signed little-endian integer at <paramref name="offset"/>.
    /// </summary>
    public int ReadInt32LittleEndian(long offset)
    {
        Span<byte> buf = stackalloc byte[4];
        return BinaryPrimitives.ReadInt32LittleEndian(GetSpan(offset, 4, buf));
    }

    /// <summary>
    /// Reads a 32-bit unsigned little-endian integer at <paramref name="offset"/>.
    /// </summary>
    public uint ReadUInt32LittleEndian(long offset)
    {
        Span<byte> buf = stackalloc byte[4];
        return BinaryPrimitives.ReadUInt32LittleEndian(GetSpan(offset, 4, buf));
    }

    /// <summary>
    /// Reads a 64-bit signed little-endian integer at <paramref name="offset"/>.
    /// </summary>
    public long ReadInt64LittleEndian(long offset)
    {
        Span<byte> buf = stackalloc byte[8];
        return BinaryPrimitives.ReadInt64LittleEndian(GetSpan(offset, 8, buf));
    }

    /// <summary>
    /// Reads a 64-bit unsigned little-endian integer at <paramref name="offset"/>.
    /// </summary>
    public ulong ReadUInt64LittleEndian(long offset)
    {
        Span<byte> buf = stackalloc byte[8];
        return BinaryPrimitives.ReadUInt64LittleEndian(GetSpan(offset, 8, buf));
    }

    /// <summary>
    /// Reads a 32-bit IEEE 754 little-endian floating-point value at <paramref name="offset"/>.
    /// </summary>
    public float ReadSingleLittleEndian(long offset)
    {
        Span<byte> buf = stackalloc byte[4];
        return BinaryPrimitives.ReadSingleLittleEndian(GetSpan(offset, 4, buf));
    }

    /// <summary>
    /// Reads a 64-bit IEEE 754 little-endian floating-point value at <paramref name="offset"/>.
    /// </summary>
    public double ReadDoubleLittleEndian(long offset)
    {
        Span<byte> buf = stackalloc byte[8];
        return BinaryPrimitives.ReadDoubleLittleEndian(GetSpan(offset, 8, buf));
    }

    /// <summary>
    /// Decodes a UTF-8 string of <paramref name="byteLength"/> bytes starting at <paramref name="offset"/>.
    /// </summary>
    public string ReadUtf8String(long offset, int byteLength)
    {
        if (byteLength < 0)
        {
            throw new ModelParseException($"Invalid negative string byte length ({byteLength}).", fileOffset: offset);
        }

        ValidateRange(offset, byteLength);
        if (byteLength == 0)
        {
            return string.Empty;
        }

        if (byteLength <= 256)
        {
            Span<byte> scratch = stackalloc byte[byteLength];
            ReadOnlySpan<byte> span = GetSpan(offset, byteLength, scratch);
            return Encoding.UTF8.GetString(span);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(byteLength);
        try
        {
            Span<byte> slice = rented.AsSpan(0, byteLength);
            ReadExactly(offset, slice);
            return Encoding.UTF8.GetString(slice);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Returns all pooled 64 KB page buffers to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int i = 0; i < _slotCount; i++)
        {
            byte[]? buffer = _slots[i].Buffer;
            if (buffer is not null)
            {
                _slots[i] = default;
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        _slotCount = 0;
    }

    private void ValidateRange(long offset, long count)
    {
        if (offset < 0 || count < 0 || offset > Length || count > Length - offset)
        {
            throw new ModelParseException(
                $"Attempted to read {count} byte(s) at offset 0x{offset:X}, which is outside the stream bounds (length 0x{Length:X}).",
                fileOffset: offset);
        }
    }

    private ref PageSlot GetOrLoadPage(long pageIndex)
    {
        for (int i = 0; i < _slotCount; i++)
        {
            if (_slots[i].PageIndex == pageIndex)
            {
                CacheHitCount++;
                _slots[i].LastAccessTick = ++_accessCounter;
                return ref _slots[i];
            }
        }

        CacheMissCount++;
        int targetSlot;
        byte[] buffer;

        if (_slotCount < _maxPages)
        {
            targetSlot = _slotCount++;
            buffer = ArrayPool<byte>.Shared.Rent(PageSize);
        }
        else
        {
            targetSlot = 0;
            long oldestTick = _slots[0].LastAccessTick;
            for (int i = 1; i < _slotCount; i++)
            {
                if (_slots[i].LastAccessTick < oldestTick)
                {
                    oldestTick = _slots[i].LastAccessTick;
                    targetSlot = i;
                }
            }

            buffer = _slots[targetSlot].Buffer;
        }

        long pageOffset = pageIndex << PageShift;
        int expectedBytes = (int)Math.Min(PageSize, Length - pageOffset);
        _stream.Position = pageOffset;

        int totalRead = 0;
        while (totalRead < expectedBytes)
        {
            int n = _stream.Read(buffer, totalRead, expectedBytes - totalRead);
            if (n == 0)
            {
                break;
            }

            totalRead += n;
        }

        _slots[targetSlot] = new PageSlot
        {
            PageIndex = pageIndex,
            Buffer = buffer,
            ValidLength = totalRead,
            LastAccessTick = ++_accessCounter,
        };

        return ref _slots[targetSlot];
    }

    private struct PageSlot
    {
        public long PageIndex;
        public byte[] Buffer;
        public int ValidLength;
        public long LastAccessTick;
    }
}

