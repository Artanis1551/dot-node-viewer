using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.IO;

/// <summary>
/// Specifies the wire encoding type of a Protocol Buffers field tag.
/// </summary>
internal enum ProtoWireType : byte
{
    /// <summary>
    /// Base-128 variable-length integer (<c>int32</c>, <c>int64</c>, <c>uint32</c>, <c>uint64</c>, <c>sint32</c>, <c>sint64</c>, <c>bool</c>, <c>enum</c>).
    /// </summary>
    Varint = 0,

    /// <summary>
    /// 64-bit little-endian fixed-width value (<c>fixed64</c>, <c>sfixed64</c>, <c>double</c>).
    /// </summary>
    Fixed64 = 1,

    /// <summary>
    /// Length-delimited payload (<c>string</c>, <c>bytes</c>, embedded messages, packed repeated fields).
    /// </summary>
    LengthDelimited = 2,

    /// <summary>
    /// Deprecated group start marker (unsupported).
    /// </summary>
    StartGroup = 3,

    /// <summary>
    /// Deprecated group end marker (unsupported).
    /// </summary>
    EndGroup = 4,

    /// <summary>
    /// 32-bit little-endian fixed-width value (<c>fixed32</c>, <c>sfixed32</c>, <c>float</c>).
    /// </summary>
    Fixed32 = 5,
}

/// <summary>
/// Represents a decoded Protocol Buffers field tag consisting of a 1-based field number and a <see cref="ProtoWireType"/>.
/// </summary>
/// <param name="FieldNumber">The 1-based field number, or <c>0</c> if the reader reached the end of the current message limit.</param>
/// <param name="WireType">The wire encoding type of the field.</param>
internal readonly record struct ProtoTag(uint FieldNumber, ProtoWireType WireType)
{
    /// <summary>
    /// Gets a value indicating whether this tag represents the end of the current message limit (<see cref="FieldNumber"/> is <c>0</c>).
    /// </summary>
    public bool IsEnd => FieldNumber == 0;
}

/// <summary>
/// Provides a forward-only, bounds-checked Protocol Buffers wire reader over a <see cref="RandomAccessSource"/>
/// with nested-message limit tracking and zero-allocation tensor slice capture.
/// </summary>
internal sealed class ProtoReader
{
    /// <summary>
    /// The default maximum nested message recursion depth.
    /// </summary>
    public const int DefaultMaxRecursionDepth = 64;

    private readonly RandomAccessSource _source;
    private readonly long _maxMetadataBytes;
    private readonly int _maxRecursionDepth;
    private long _position;
    private long _currentLimit;
    private int _recursionDepth;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtoReader"/> class over the specified <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The random-access byte source to read from.</param>
    /// <param name="startOffset">The initial byte offset in <paramref name="source"/>.</param>
    /// <param name="length">The optional byte length of the top-level message; defaults to the remainder of <paramref name="source"/>.</param>
    /// <param name="maxMetadataBytes">The maximum byte length allowed for a single allocated string metadata field.</param>
    /// <param name="maxRecursionDepth">The maximum allowed nested message recursion depth.</param>
    public ProtoReader(
        RandomAccessSource source,
        long startOffset = 0,
        long? length = null,
        long maxMetadataBytes = 64L * 1024L * 1024L,
        int maxRecursionDepth = DefaultMaxRecursionDepth)
    {
        ArgumentNullException.ThrowIfNull(source);

        long effectiveLength = length ?? (source.Length - startOffset);
        if (startOffset < 0 || effectiveLength < 0 || startOffset > source.Length || effectiveLength > source.Length - startOffset)
        {
            throw new ModelParseException(
                $"Invalid ProtoReader bounds (offset=0x{startOffset:X}, length={effectiveLength}, sourceLength=0x{source.Length:X}).",
                fileOffset: startOffset);
        }

        _source = source;
        _position = startOffset;
        _currentLimit = startOffset + effectiveLength;
        _maxMetadataBytes = maxMetadataBytes;
        _maxRecursionDepth = maxRecursionDepth;
    }

    /// <summary>
    /// Gets the current zero-based byte offset in the underlying stream.
    /// </summary>
    public long Position => _position;

    /// <summary>
    /// Gets the exclusive end byte offset of the current message limit.
    /// </summary>
    public long CurrentLimit => _currentLimit;

    /// <summary>
    /// Gets the number of unread bytes remaining before <see cref="CurrentLimit"/>.
    /// </summary>
    public long RemainingInLimit => _currentLimit - _position;

    /// <summary>
    /// Gets a value indicating whether the reader has reached <see cref="CurrentLimit"/>.
    /// </summary>
    public bool IsAtEnd => _position >= _currentLimit;

    /// <summary>
    /// Gets the current nested message recursion depth.
    /// </summary>
    public int RecursionDepth => _recursionDepth;

    /// <summary>
    /// Reads the next field tag in the current message, or returns a default <see cref="ProtoTag"/> (<c>FieldNumber == 0</c>) when <see cref="IsAtEnd"/> is <see langword="true"/>.
    /// </summary>
    /// <returns>The decoded <see cref="ProtoTag"/>, or <c>default</c> at the end of the current limit.</returns>
    public ProtoTag ReadTag()
    {
        if (_position >= _currentLimit)
        {
            return default;
        }

        long tagOffset = _position;
        ulong rawTag = ReadRawVarint64();
        ulong fieldNumber = rawTag >> 3;
        ProtoWireType wireType = (ProtoWireType)(rawTag & 0x07);

        if (fieldNumber == 0 || fieldNumber > int.MaxValue)
        {
            throw new ModelParseException(
                $"Invalid Protobuf field number {fieldNumber} in tag 0x{rawTag:X}.",
                fileOffset: tagOffset);
        }

        if ((byte)wireType > (byte)ProtoWireType.Fixed32
            || wireType == ProtoWireType.StartGroup
            || wireType == ProtoWireType.EndGroup)
        {
            throw new ModelParseException(
                $"Unsupported or invalid Protobuf wire type {(int)wireType} for field {fieldNumber}.",
                fileOffset: tagOffset);
        }

        return new ProtoTag((uint)fieldNumber, wireType);
    }

    /// <summary>
    /// Reads a base-128 varint and returns its 64-bit unsigned value.
    /// </summary>
    public ulong ReadRawVarint64()
    {
        long startOffset = _position;
        ulong result = 0;

        for (int shift = 0; shift < 64; shift += 7)
        {
            if (_position >= _currentLimit)
            {
                throw new ModelParseException(
                    "Unexpected end of stream while reading Protobuf varint.",
                    fileOffset: _position);
            }

            byte b = _source.ReadByte(_position++);
            if (shift == 63 && (b & 0xFE) != 0)
            {
                throw new ModelParseException(
                    "Malformed Protobuf varint exceeds 64-bit integer precision.",
                    fileOffset: startOffset);
            }

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new ModelParseException(
            "Malformed Protobuf varint exceeds maximum 10-byte length.",
            fileOffset: startOffset);
    }

    /// <summary>
    /// Reads a varint-encoded 32-bit signed integer (including 10-byte sign-extended negative values).
    /// </summary>
    public int ReadInt32() => unchecked((int)ReadRawVarint64());

    /// <summary>
    /// Reads a varint-encoded 32-bit unsigned integer.
    /// </summary>
    public uint ReadUInt32() => unchecked((uint)ReadRawVarint64());

    /// <summary>
    /// Reads a varint-encoded 64-bit signed integer.
    /// </summary>
    public long ReadInt64() => unchecked((long)ReadRawVarint64());

    /// <summary>
    /// Reads a varint-encoded 64-bit unsigned integer.
    /// </summary>
    public ulong ReadUInt64() => ReadRawVarint64();

    /// <summary>
    /// Reads a varint-encoded boolean value.
    /// </summary>
    public bool ReadBool() => ReadRawVarint64() != 0;

    /// <summary>
    /// Reads a 32-bit little-endian unsigned fixed integer and advances the position by 4 bytes.
    /// </summary>
    public uint ReadFixed32()
    {
        EnsureAvailable(4);
        long offset = _position;
        _position += 4;
        return _source.ReadUInt32LittleEndian(offset);
    }

    /// <summary>
    /// Reads a 32-bit little-endian signed fixed integer and advances the position by 4 bytes.
    /// </summary>
    public int ReadSFixed32()
    {
        EnsureAvailable(4);
        long offset = _position;
        _position += 4;
        return _source.ReadInt32LittleEndian(offset);
    }

    /// <summary>
    /// Reads a 32-bit IEEE 754 little-endian floating-point value and advances the position by 4 bytes.
    /// </summary>
    public float ReadFloat()
    {
        EnsureAvailable(4);
        long offset = _position;
        _position += 4;
        return _source.ReadSingleLittleEndian(offset);
    }

    /// <summary>
    /// Reads a 64-bit little-endian unsigned fixed integer and advances the position by 8 bytes.
    /// </summary>
    public ulong ReadFixed64()
    {
        EnsureAvailable(8);
        long offset = _position;
        _position += 8;
        return _source.ReadUInt64LittleEndian(offset);
    }

    /// <summary>
    /// Reads a 64-bit little-endian signed fixed integer and advances the position by 8 bytes.
    /// </summary>
    public long ReadSFixed64()
    {
        EnsureAvailable(8);
        long offset = _position;
        _position += 8;
        return _source.ReadInt64LittleEndian(offset);
    }

    /// <summary>
    /// Reads a 64-bit IEEE 754 little-endian floating-point value and advances the position by 8 bytes.
    /// </summary>
    public double ReadDouble()
    {
        EnsureAvailable(8);
        long offset = _position;
        _position += 8;
        return _source.ReadDoubleLittleEndian(offset);
    }

    /// <summary>
    /// Reads and validates the varint byte length prefix of a <see cref="ProtoWireType.LengthDelimited"/> field.
    /// </summary>
    /// <returns>The validated non-negative byte length of the length-delimited payload.</returns>
    public long ReadLengthPrefix()
    {
        long lengthOffset = _position;
        ulong rawLength = ReadRawVarint64();
        if (rawLength > (ulong)RemainingInLimit)
        {
            throw new ModelParseException(
                $"Protobuf length-delimited field length ({rawLength} bytes) exceeds remaining message limit ({RemainingInLimit} bytes).",
                fileOffset: lengthOffset);
        }

        return (long)rawLength;
    }

    /// <summary>
    /// Reads a length-delimited UTF-8 string field.
    /// </summary>
    /// <returns>The decoded UTF-8 string.</returns>
    public string ReadString()
    {
        long lengthOffset = _position;
        long byteLength = ReadLengthPrefix();
        if (byteLength > _maxMetadataBytes || byteLength > int.MaxValue)
        {
            throw new ModelParseException(
                $"Protobuf string length ({byteLength} bytes) exceeds maximum metadata budget ({_maxMetadataBytes} bytes).",
                fileOffset: lengthOffset);
        }

        long stringOffset = _position;
        _position += byteLength;
        return _source.ReadUtf8String(stringOffset, (int)byteLength);
    }

    /// <summary>
    /// Reads a length prefix and pushes a nested message limit onto the reader, returning the previous limit to pass to <see cref="PopLimit"/>.
    /// </summary>
    /// <returns>The previous message limit to restore via <see cref="PopLimit"/>.</returns>
    public long PushMessageLimit()
    {
        long byteLength = ReadLengthPrefix();
        return PushLimit(byteLength);
    }

    /// <summary>
    /// Pushes a nested message limit of <paramref name="byteLength"/> bytes from the current position, returning the previous limit to pass to <see cref="PopLimit"/>.
    /// </summary>
    /// <param name="byteLength">The length in bytes of the nested message or packed field.</param>
    /// <returns>The previous message limit to restore via <see cref="PopLimit"/>.</returns>
    public long PushLimit(long byteLength)
    {
        if (byteLength < 0 || byteLength > RemainingInLimit)
        {
            throw new ModelParseException(
                $"Invalid Protobuf nested limit of {byteLength} byte(s) (remaining: {RemainingInLimit}).",
                fileOffset: _position);
        }

        if (_recursionDepth >= _maxRecursionDepth)
        {
            throw new ModelParseException(
                $"Protobuf message nesting exceeded maximum depth of {_maxRecursionDepth}.",
                fileOffset: _position);
        }

        _recursionDepth++;
        long previousLimit = _currentLimit;
        _currentLimit = _position + byteLength;
        return previousLimit;
    }

    /// <summary>
    /// Verifies that the nested message was consumed completely and restores the previous limit returned by <see cref="PushLimit"/> or <see cref="PushMessageLimit"/>.
    /// </summary>
    /// <param name="previousLimit">The outer limit returned when the nested limit was pushed.</param>
    public void PopLimit(long previousLimit)
    {
        if (_position != _currentLimit)
        {
            throw new ModelParseException(
                $"Protobuf nested message ended at offset 0x{_position:X}, expected limit 0x{_currentLimit:X}.",
                fileOffset: _position);
        }

        _currentLimit = previousLimit;
        _recursionDepth--;
    }

    /// <summary>
    /// Captures the stream offset and byte length of a length-delimited payload and advances the reader past the payload without reading or allocating its bytes.
    /// </summary>
    /// <returns>A tuple containing the zero-based <c>StreamOffset</c> and <c>ByteLength</c> of the payload.</returns>
    public (long StreamOffset, long ByteLength) CaptureLengthDelimitedRange()
    {
        long byteLength = ReadLengthPrefix();
        long streamOffset = _position;
        _position += byteLength;
        return (streamOffset, byteLength);
    }

    /// <summary>
    /// Captures a length-delimited payload as a <see cref="TensorDataSlice"/> and advances the reader past the payload without reading or allocating its bytes.
    /// </summary>
    /// <param name="dataType">The tensor element data type associated with the slice.</param>
    /// <param name="encoding">The encoding of the elements within the slice.</param>
    /// <returns>A <see cref="TensorDataSlice"/> pointing to the payload range in the model stream.</returns>
    public TensorDataSlice CaptureLengthDelimitedSlice(
        DataType dataType,
        TensorDataEncoding encoding = TensorDataEncoding.Raw)
    {
        (long streamOffset, long byteLength) = CaptureLengthDelimitedRange();
        return new TensorDataSlice(streamOffset, byteLength, dataType, encoding);
    }

    /// <summary>
    /// Advances the reader by <paramref name="count"/> bytes without reading or copying the skipped bytes.
    /// </summary>
    /// <param name="count">The number of bytes to skip.</param>
    public void SkipBytes(long count)
    {
        EnsureAvailable(count);
        _position += count;
    }

    /// <summary>
    /// Skips the value of a field with the specified <paramref name="wireType"/>.
    /// </summary>
    /// <param name="wireType">The wire type of the field to skip.</param>
    public void SkipField(ProtoWireType wireType)
    {
        switch (wireType)
        {
            case ProtoWireType.Varint:
                _ = ReadRawVarint64();
                break;

            case ProtoWireType.Fixed64:
                SkipBytes(8);
                break;

            case ProtoWireType.LengthDelimited:
                long length = ReadLengthPrefix();
                SkipBytes(length);
                break;

            case ProtoWireType.Fixed32:
                SkipBytes(4);
                break;

            default:
                throw new ModelParseException(
                    $"Cannot skip unsupported Protobuf wire type {(int)wireType}.",
                    fileOffset: _position);
        }
    }

    /// <summary>
    /// Reads one or more 64-bit signed integers from either a packed <see cref="ProtoWireType.LengthDelimited"/> field or an unpacked <see cref="ProtoWireType.Varint"/> field and appends them to <paramref name="target"/>.
    /// </summary>
    /// <param name="wireType">The wire type of the current tag.</param>
    /// <param name="target">The list receiving the decoded 64-bit integer values.</param>
    /// <param name="fieldContext">A descriptive context name for error messages.</param>
    public void ReadRepeatedInt64(ProtoWireType wireType, List<long> target, string fieldContext)
    {
        if (wireType == ProtoWireType.Varint)
        {
            target.Add(ReadInt64());
            return;
        }

        if (wireType == ProtoWireType.LengthDelimited)
        {
            long oldLimit = PushMessageLimit();
            while (!IsAtEnd)
            {
                target.Add(ReadInt64());
            }

            PopLimit(oldLimit);
            return;
        }

        throw new ModelParseException(
            $"Unexpected wire type {wireType} for repeated int64 field '{fieldContext}'.",
            fileOffset: _position,
            context: fieldContext);
    }

    /// <summary>
    /// Reads one or more 32-bit floating-point values (converted to <see cref="double"/>) from either a packed <see cref="ProtoWireType.LengthDelimited"/> field or an unpacked <see cref="ProtoWireType.Fixed32"/> field and appends them to <paramref name="target"/>.
    /// </summary>
    /// <param name="wireType">The wire type of the current tag.</param>
    /// <param name="target">The list receiving the decoded floating-point values.</param>
    /// <param name="fieldContext">A descriptive context name for error messages.</param>
    public void ReadRepeatedFloatAsDouble(ProtoWireType wireType, List<double> target, string fieldContext)
    {
        if (wireType == ProtoWireType.Fixed32)
        {
            target.Add(ReadFloat());
            return;
        }

        if (wireType == ProtoWireType.LengthDelimited)
        {
            long byteLength = ReadLengthPrefix();
            if ((byteLength & 3) != 0)
            {
                throw new ModelParseException(
                    $"Packed float field '{fieldContext}' has byte length {byteLength}, which is not a multiple of 4.",
                    fileOffset: _position,
                    context: fieldContext);
            }

            long oldLimit = PushLimit(byteLength);
            while (!IsAtEnd)
            {
                target.Add(ReadFloat());
            }

            PopLimit(oldLimit);
            return;
        }

        throw new ModelParseException(
            $"Unexpected wire type {wireType} for repeated float field '{fieldContext}'.",
            fileOffset: _position,
            context: fieldContext);
    }

    /// <summary>
    /// Ensures the current wire type matches <paramref name="expected"/>, throwing a <see cref="ModelParseException"/> otherwise.
    /// </summary>
    /// <param name="actual">The actual wire type encountered.</param>
    /// <param name="expected">The expected wire type.</param>
    /// <param name="context">The field or message context name.</param>
    public void ExpectWireType(ProtoWireType actual, ProtoWireType expected, string context)
    {
        if (actual != expected)
        {
            throw new ModelParseException(
                $"Expected Protobuf wire type {expected} for '{context}', but found {actual}.",
                fileOffset: _position,
                context: context);
        }
    }

    private void EnsureAvailable(long count)
    {
        if (count < 0 || count > _currentLimit - _position)
        {
            throw new ModelParseException(
                $"Unexpected end of Protobuf message: requested {count} byte(s) at offset 0x{_position:X}, but only {RemainingInLimit} byte(s) remain in limit.",
                fileOffset: _position);
        }
    }
}
