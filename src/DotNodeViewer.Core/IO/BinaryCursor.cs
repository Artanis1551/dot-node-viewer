using System.Buffers.Binary;
using System.Text;
using DotNodeViewer.Core.Abstractions;

namespace DotNodeViewer.Core.IO;

/// <summary>
/// Provides a bounds-checked, zero-allocation sequential reader over a <see cref="ReadOnlySpan{T}"/> of bytes.
/// </summary>
internal ref struct BinaryCursor
{
    private readonly ReadOnlySpan<byte> _buffer;
    private readonly long _baseFileOffset;
    private int _position;

    /// <summary>
    /// Initializes a new instance of the <see cref="BinaryCursor"/> struct over the specified byte span.
    /// </summary>
    /// <param name="buffer">The byte span to read from.</param>
    /// <param name="baseFileOffset">The zero-based file offset corresponding to index 0 of <paramref name="buffer"/> for diagnostic reporting.</param>
    public BinaryCursor(ReadOnlySpan<byte> buffer, long baseFileOffset = 0)
    {
        _buffer = buffer;
        _baseFileOffset = baseFileOffset;
        _position = 0;
    }

    /// <summary>
    /// Gets the current zero-based byte index within the underlying span.
    /// </summary>
    public readonly int Position => _position;

    /// <summary>
    /// Gets the current absolute byte offset in the source file (<c>baseFileOffset + Position</c>).
    /// </summary>
    public readonly long FileOffset => _baseFileOffset + _position;

    /// <summary>
    /// Gets the number of unread bytes remaining in the span.
    /// </summary>
    public readonly int Remaining => _buffer.Length - _position;

    /// <summary>
    /// Gets a value indicating whether the cursor has reached the end of the span.
    /// </summary>
    public readonly bool IsAtEnd => _position >= _buffer.Length;

    /// <summary>
    /// Reads a single unsigned byte and advances the cursor by 1 byte.
    /// </summary>
    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _buffer[_position++];
    }

    /// <summary>
    /// Reads a single signed byte and advances the cursor by 1 byte.
    /// </summary>
    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

    /// <summary>
    /// Reads a 16-bit signed little-endian integer and advances the cursor by 2 bytes.
    /// </summary>
    public short ReadInt16LittleEndian() => BinaryPrimitives.ReadInt16LittleEndian(ReadBytes(2));

    /// <summary>
    /// Reads a 16-bit unsigned little-endian integer and advances the cursor by 2 bytes.
    /// </summary>
    public ushort ReadUInt16LittleEndian() => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(2));

    /// <summary>
    /// Reads a 32-bit signed little-endian integer and advances the cursor by 4 bytes.
    /// </summary>
    public int ReadInt32LittleEndian() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4));

    /// <summary>
    /// Reads a 32-bit unsigned little-endian integer and advances the cursor by 4 bytes.
    /// </summary>
    public uint ReadUInt32LittleEndian() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));

    /// <summary>
    /// Reads a 64-bit signed little-endian integer and advances the cursor by 8 bytes.
    /// </summary>
    public long ReadInt64LittleEndian() => BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(8));

    /// <summary>
    /// Reads a 64-bit unsigned little-endian integer and advances the cursor by 8 bytes.
    /// </summary>
    public ulong ReadUInt64LittleEndian() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8));

    /// <summary>
    /// Reads a 16-bit signed big-endian integer and advances the cursor by 2 bytes.
    /// </summary>
    public short ReadInt16BigEndian() => BinaryPrimitives.ReadInt16BigEndian(ReadBytes(2));

    /// <summary>
    /// Reads a 16-bit unsigned big-endian integer and advances the cursor by 2 bytes.
    /// </summary>
    public ushort ReadUInt16BigEndian() => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(2));

    /// <summary>
    /// Reads a 32-bit signed big-endian integer and advances the cursor by 4 bytes.
    /// </summary>
    public int ReadInt32BigEndian() => BinaryPrimitives.ReadInt32BigEndian(ReadBytes(4));

    /// <summary>
    /// Reads a 32-bit unsigned big-endian integer and advances the cursor by 4 bytes.
    /// </summary>
    public uint ReadUInt32BigEndian() => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));

    /// <summary>
    /// Reads a 64-bit signed big-endian integer and advances the cursor by 8 bytes.
    /// </summary>
    public long ReadInt64BigEndian() => BinaryPrimitives.ReadInt64BigEndian(ReadBytes(8));

    /// <summary>
    /// Reads a 64-bit unsigned big-endian integer and advances the cursor by 8 bytes.
    /// </summary>
    public ulong ReadUInt64BigEndian() => BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(8));

    /// <summary>
    /// Reads a 32-bit IEEE 754 little-endian floating-point number and advances the cursor by 4 bytes.
    /// </summary>
    public float ReadSingleLittleEndian() => BinaryPrimitives.ReadSingleLittleEndian(ReadBytes(4));

    /// <summary>
    /// Reads a 64-bit IEEE 754 little-endian floating-point number and advances the cursor by 8 bytes.
    /// </summary>
    public double ReadDoubleLittleEndian() => BinaryPrimitives.ReadDoubleLittleEndian(ReadBytes(8));

    /// <summary>
    /// Reads a slice of <paramref name="length"/> bytes and advances the cursor by <paramref name="length"/>.
    /// </summary>
    /// <param name="length">The number of bytes to read.</param>
    /// <returns>A read-only span over the consumed bytes.</returns>
    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        EnsureAvailable(length);
        ReadOnlySpan<byte> slice = _buffer.Slice(_position, length);
        _position += length;
        return slice;
    }

    /// <summary>
    /// Reads a UTF-8 string of <paramref name="byteLength"/> bytes and advances the cursor by <paramref name="byteLength"/>.
    /// </summary>
    /// <param name="byteLength">The length of the UTF-8 byte sequence.</param>
    /// <returns>The decoded string.</returns>
    public string ReadUtf8String(int byteLength)
    {
        ReadOnlySpan<byte> bytes = ReadBytes(byteLength);
        return bytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Advances the cursor by <paramref name="count"/> bytes without copying.
    /// </summary>
    /// <param name="count">The number of bytes to skip.</param>
    public void Skip(int count)
    {
        EnsureAvailable(count);
        _position += count;
    }

    /// <summary>
    /// Reads a sub-cursor of <paramref name="length"/> bytes and advances this cursor past that range.
    /// </summary>
    /// <param name="length">The length of the sub-cursor in bytes.</param>
    /// <returns>A new <see cref="BinaryCursor"/> bounded to the consumed range.</returns>
    public BinaryCursor Slice(int length)
    {
        long subOffset = FileOffset;
        ReadOnlySpan<byte> subSpan = ReadBytes(length);
        return new BinaryCursor(subSpan, subOffset);
    }

    private readonly void EnsureAvailable(int count)
    {
        if (count < 0 || count > _buffer.Length - _position)
        {
            throw new ModelParseException(
                $"Unexpected end of buffer: requested {count} byte(s) at offset 0x{FileOffset:X}, but only {Remaining} byte(s) remain.",
                fileOffset: FileOffset);
        }
    }
}

