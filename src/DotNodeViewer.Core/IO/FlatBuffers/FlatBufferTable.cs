using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.IO.FlatBuffers;

/// <summary>
/// Provides a bounds-checked, zero-allocation reader over a FlatBuffers table backed by a <see cref="RandomAccessSource"/>.
/// </summary>
internal readonly struct FlatBufferTable
{
    private readonly RandomAccessSource _source;
    private readonly long _tableOffset;
    private readonly long _vtableOffset;
    private readonly int _vtableByteLength;
    private readonly int _tableInlineByteSize;

    /// <summary>
    /// Initializes a new instance of the <see cref="FlatBufferTable"/> struct at the specified <paramref name="tableOffset"/>.
    /// </summary>
    /// <param name="source">The random-access byte source containing the FlatBuffer.</param>
    /// <param name="tableOffset">The absolute byte offset of the table in <paramref name="source"/>.</param>
    /// <exception cref="ModelParseException">The table or its vtable offset falls outside the bounds of <paramref name="source"/>.</exception>
    public FlatBufferTable(RandomAccessSource source, long tableOffset)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (tableOffset < 0 || tableOffset + 4 > source.Length)
        {
            throw new ModelParseException(
                $"FlatBuffers table offset 0x{tableOffset:X} is outside stream bounds (0x{source.Length:X}).",
                fileOffset: tableOffset);
        }

        int vtableSOffset = source.ReadInt32LittleEndian(tableOffset);
        long vtableOffset = tableOffset - vtableSOffset;
        if (vtableOffset < 0 || vtableOffset + 4 > source.Length)
        {
            throw new ModelParseException(
                $"FlatBuffers vtable offset 0x{vtableOffset:X} (from table 0x{tableOffset:X}, soffset {vtableSOffset}) is outside stream bounds (0x{source.Length:X}).",
                fileOffset: tableOffset);
        }

        ushort vtableByteLength = source.ReadUInt16LittleEndian(vtableOffset);
        ushort tableInlineByteSize = source.ReadUInt16LittleEndian(vtableOffset + 2);

        if (vtableByteLength < 4 || (vtableByteLength & 1) != 0 || vtableOffset + vtableByteLength > source.Length)
        {
            throw new ModelParseException(
                $"Malformed FlatBuffers vtable length ({vtableByteLength}) at offset 0x{vtableOffset:X}.",
                fileOffset: vtableOffset);
        }

        if (tableInlineByteSize < 4 || tableOffset + tableInlineByteSize > source.Length)
        {
            throw new ModelParseException(
                $"Malformed FlatBuffers table inline size ({tableInlineByteSize}) at offset 0x{tableOffset:X}.",
                fileOffset: tableOffset);
        }

        _source = source;
        _tableOffset = tableOffset;
        _vtableOffset = vtableOffset;
        _vtableByteLength = vtableByteLength;
        _tableInlineByteSize = tableInlineByteSize;
    }

    /// <summary>
    /// Gets the underlying <see cref="RandomAccessSource"/>.
    /// </summary>
    public RandomAccessSource Source => _source;

    /// <summary>
    /// Gets the absolute byte offset of this table in <see cref="Source"/>.
    /// </summary>
    public long TableOffset => _tableOffset;

    /// <summary>
    /// Reads the root <see cref="FlatBufferTable"/> from a FlatBuffer starting at <paramref name="bufferStartOffset"/>.
    /// </summary>
    /// <param name="source">The random-access byte source.</param>
    /// <param name="bufferStartOffset">The starting byte offset of the FlatBuffer container (default <c>0</c>).</param>
    /// <param name="expectedIdentifier">An optional 4-byte ASCII file identifier expected at <c>bufferStartOffset + 4</c> (such as <c>"TFL3"</c>).</param>
    /// <returns>The root <see cref="FlatBufferTable"/>.</returns>
    public static FlatBufferTable ReadRoot(
        RandomAccessSource source,
        long bufferStartOffset = 0,
        ReadOnlySpan<byte> expectedIdentifier = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        long minHeaderBytes = expectedIdentifier.IsEmpty ? 4 : 8;
        if (bufferStartOffset < 0 || bufferStartOffset + minHeaderBytes > source.Length)
        {
            throw new ModelParseException(
                $"Stream is too short ({source.Length} bytes) to contain a valid FlatBuffer header at offset 0x{bufferStartOffset:X}.",
                fileOffset: bufferStartOffset);
        }

        if (!expectedIdentifier.IsEmpty)
        {
            Span<byte> idScratch = stackalloc byte[4];
            ReadOnlySpan<byte> actualId = source.GetSpan(bufferStartOffset + 4, 4, idScratch);
            if (!actualId.SequenceEqual(expectedIdentifier))
            {
                throw new ModelParseException(
                    $"FlatBuffer file identifier mismatch at offset 0x{bufferStartOffset + 4:X}.",
                    fileOffset: bufferStartOffset + 4);
            }
        }

        uint rootUOffset = source.ReadUInt32LittleEndian(bufferStartOffset);
        long rootTableOffset = bufferStartOffset + rootUOffset;
        return new FlatBufferTable(source, rootTableOffset);
    }

    /// <summary>
    /// Determines whether the field at zero-based <paramref name="fieldIndex"/> is present in this table's vtable.
    /// </summary>
    /// <param name="fieldIndex">The zero-based FlatBuffers field index.</param>
    /// <returns><see langword="true"/> if the field is present; otherwise, <see langword="false"/>.</returns>
    public bool HasField(int fieldIndex) => TryGetFieldOffset(fieldIndex, requiredByteSize: 1, out _);

    /// <summary>
    /// Reads a boolean field at <paramref name="fieldIndex"/>, or returns <paramref name="defaultValue"/> if absent.
    /// </summary>
    public bool ReadBool(int fieldIndex, bool defaultValue = false) =>
        TryGetFieldOffset(fieldIndex, 1, out long pos) ? _source.ReadByte(pos) != 0 : defaultValue;

    /// <summary>
    /// Reads an unsigned 8-bit integer field at <paramref name="fieldIndex"/>, or returns <paramref name="defaultValue"/> if absent.
    /// </summary>
    public byte ReadByte(int fieldIndex, byte defaultValue = 0) =>
        TryGetFieldOffset(fieldIndex, 1, out long pos) ? _source.ReadByte(pos) : defaultValue;

    /// <summary>
    /// Reads a signed 8-bit integer field at <paramref name="fieldIndex"/>, or returns <paramref name="defaultValue"/> if absent.
    /// </summary>
    public sbyte ReadSByte(int fieldIndex, sbyte defaultValue = 0) =>
        TryGetFieldOffset(fieldIndex, 1, out long pos) ? unchecked((sbyte)_source.ReadByte(pos)) : defaultValue;

    /// <summary>
    /// Reads a signed 16-bit integer field at <paramref name="fieldIndex"/>, or returns <paramref name="defaultValue"/> if absent.
    /// </summary>
    public short ReadInt16(int fieldIndex, short defaultValue = 0) =>
        TryGetFieldOffset(fieldIndex, 2, out long pos) ? _source.ReadInt16LittleEndian(pos) : defaultValue;

    /// <summary>
    /// Reads an unsigned 16-bit integer field at <paramref name="fieldIndex"/>, or returns <paramref name="defaultValue"/> if absent.
    /// </summary>
    public ushort ReadUInt16(int fieldIndex, ushort defaultValue = 0) =>
        TryGetFieldOffset(fieldIndex, 2, out long pos) ? _source.ReadUInt16LittleEndian(pos) : defaultValue;

    /// <summary>
    /// Reads a signed 32-bit integer field at <paramref name="fieldIndex"/>, or returns <paramref name="defaultValue"/> if absent.
    /// </summary>
    public int ReadInt32(int fieldIndex, int defaultValue = 0) =>
        TryGetFieldOffset(fieldIndex, 4, out long pos) ? _source.ReadInt32LittleEndian(pos) : defaultValue;

    /// <summary>
    /// Reads an unsigned 32-bit integer field at <paramref name="fieldIndex"/>, or returns <paramref name="defaultValue"/> if absent.
    /// </summary>
    public uint ReadUInt32(int fieldIndex, uint defaultValue = 0) =>
        TryGetFieldOffset(fieldIndex, 4, out long pos) ? _source.ReadUInt32LittleEndian(pos) : defaultValue;

    /// <summary>
    /// Reads a signed 64-bit integer field at <paramref name="fieldIndex"/>, or returns <paramref name="defaultValue"/> if absent.
    /// </summary>
    public long ReadInt64(int fieldIndex, long defaultValue = 0) =>
        TryGetFieldOffset(fieldIndex, 8, out long pos) ? _source.ReadInt64LittleEndian(pos) : defaultValue;

    /// <summary>
    /// Reads an unsigned 64-bit integer field at <paramref name="fieldIndex"/>, or returns <paramref name="defaultValue"/> if absent.
    /// </summary>
    public ulong ReadUInt64(int fieldIndex, ulong defaultValue = 0) =>
        TryGetFieldOffset(fieldIndex, 8, out long pos) ? _source.ReadUInt64LittleEndian(pos) : defaultValue;

    /// <summary>
    /// Reads a 32-bit IEEE 754 floating-point field at <paramref name="fieldIndex"/>, or returns <paramref name="defaultValue"/> if absent.
    /// </summary>
    public float ReadSingle(int fieldIndex, float defaultValue = 0f) =>
        TryGetFieldOffset(fieldIndex, 4, out long pos) ? _source.ReadSingleLittleEndian(pos) : defaultValue;

    /// <summary>
    /// Reads a 64-bit IEEE 754 floating-point field at <paramref name="fieldIndex"/>, or returns <paramref name="defaultValue"/> if absent.
    /// </summary>
    public double ReadDouble(int fieldIndex, double defaultValue = 0.0) =>
        TryGetFieldOffset(fieldIndex, 8, out long pos) ? _source.ReadDoubleLittleEndian(pos) : defaultValue;

    /// <summary>
    /// Reads a UTF-8 string field at <paramref name="fieldIndex"/>, or returns <see langword="null"/> if absent.
    /// </summary>
    public string? ReadString(int fieldIndex)
    {
        if (!TryDereferenceUOffset(fieldIndex, out long stringPos))
        {
            return null;
        }

        return ReadStringAt(_source, stringPos);
    }

    /// <summary>
    /// Reads a nested <see cref="FlatBufferTable"/> field at <paramref name="fieldIndex"/>, or returns <see langword="null"/> if absent.
    /// </summary>
    public FlatBufferTable? ReadTable(int fieldIndex)
    {
        if (!TryDereferenceUOffset(fieldIndex, out long nestedTablePos))
        {
            return null;
        }

        return new FlatBufferTable(_source, nestedTablePos);
    }

    /// <summary>
    /// Reads a union <see cref="FlatBufferTable"/> field at <paramref name="tableFieldIndex"/> when its union discriminator is non-zero.
    /// </summary>
    public FlatBufferTable? ReadUnionTable(int tableFieldIndex) => ReadTable(tableFieldIndex);

    /// <summary>
    /// Gets the number of elements in the vector field at <paramref name="fieldIndex"/>, or <c>0</c> if the field is absent.
    /// </summary>
    public int GetVectorLength(int fieldIndex)
    {
        if (!TryDereferenceUOffset(fieldIndex, out long vectorPos))
        {
            return 0;
        }

        return ReadVectorElementCount(vectorPos, elementByteSize: 1);
    }

    /// <summary>
    /// Reads the <paramref name="index"/>-th <see cref="FlatBufferTable"/> from a vector of tables at <paramref name="fieldIndex"/>.
    /// </summary>
    public FlatBufferTable GetVectorTable(int fieldIndex, int index)
    {
        if (!TryDereferenceUOffset(fieldIndex, out long vectorPos))
        {
            throw new ModelParseException(
                $"FlatBuffers table vector field {fieldIndex} is absent at table 0x{_tableOffset:X}.",
                fileOffset: _tableOffset);
        }

        int count = ReadVectorElementCount(vectorPos, elementByteSize: 4);
        if ((uint)index >= (uint)count)
        {
            throw new ModelParseException(
                $"FlatBuffers table vector index {index} is out of range (count={count}) at 0x{vectorPos:X}.",
                fileOffset: vectorPos);
        }

        long elemOffsetPos = vectorPos + 4 + ((long)index * 4);
        uint elemUOffset = _source.ReadUInt32LittleEndian(elemOffsetPos);
        if (elemUOffset < 4)
        {
            throw new ModelParseException(
                $"Invalid FlatBuffers table element uoffset ({elemUOffset}) at 0x{elemOffsetPos:X}.",
                fileOffset: elemOffsetPos);
        }

        long targetTablePos = elemOffsetPos + elemUOffset;
        return new FlatBufferTable(_source, targetTablePos);
    }

    /// <summary>
    /// Reads a vector of 32-bit signed integers at <paramref name="fieldIndex"/>, or returns <see langword="null"/> if the field is absent.
    /// </summary>
    public int[]? ReadInt32Vector(int fieldIndex)
    {
        if (!TryDereferenceUOffset(fieldIndex, out long vectorPos))
        {
            return null;
        }

        int count = ReadVectorElementCount(vectorPos, elementByteSize: 4);
        if (count == 0)
        {
            return [];
        }

        int[] result = new int[count];
        long dataPos = vectorPos + 4;
        for (int i = 0; i < count; i++)
        {
            result[i] = _source.ReadInt32LittleEndian(dataPos + ((long)i * 4));
        }

        return result;
    }

    /// <summary>
    /// Reads a vector of 64-bit signed integers at <paramref name="fieldIndex"/>, or returns <see langword="null"/> if the field is absent.
    /// </summary>
    public long[]? ReadInt64Vector(int fieldIndex)
    {
        if (!TryDereferenceUOffset(fieldIndex, out long vectorPos))
        {
            return null;
        }

        int count = ReadVectorElementCount(vectorPos, elementByteSize: 8);
        if (count == 0)
        {
            return [];
        }

        long[] result = new long[count];
        long dataPos = vectorPos + 4;
        for (int i = 0; i < count; i++)
        {
            result[i] = _source.ReadInt64LittleEndian(dataPos + ((long)i * 8));
        }

        return result;
    }

    /// <summary>
    /// Reads a vector of 32-bit floating-point values at <paramref name="fieldIndex"/>, or returns <see langword="null"/> if the field is absent.
    /// </summary>
    public float[]? ReadSingleVector(int fieldIndex)
    {
        if (!TryDereferenceUOffset(fieldIndex, out long vectorPos))
        {
            return null;
        }

        int count = ReadVectorElementCount(vectorPos, elementByteSize: 4);
        if (count == 0)
        {
            return [];
        }

        float[] result = new float[count];
        long dataPos = vectorPos + 4;
        for (int i = 0; i < count; i++)
        {
            result[i] = _source.ReadSingleLittleEndian(dataPos + ((long)i * 4));
        }

        return result;
    }

    /// <summary>
    /// Reads a vector of 64-bit floating-point values at <paramref name="fieldIndex"/>, or returns <see langword="null"/> if the field is absent.
    /// </summary>
    public double[]? ReadDoubleVector(int fieldIndex)
    {
        if (!TryDereferenceUOffset(fieldIndex, out long vectorPos))
        {
            return null;
        }

        int count = ReadVectorElementCount(vectorPos, elementByteSize: 8);
        if (count == 0)
        {
            return [];
        }

        double[] result = new double[count];
        long dataPos = vectorPos + 4;
        for (int i = 0; i < count; i++)
        {
            result[i] = _source.ReadDoubleLittleEndian(dataPos + ((long)i * 8));
        }

        return result;
    }

    /// <summary>
    /// Reads a vector of boolean values at <paramref name="fieldIndex"/>, or returns <see langword="null"/> if the field is absent.
    /// </summary>
    public bool[]? ReadBoolVector(int fieldIndex)
    {
        if (!TryDereferenceUOffset(fieldIndex, out long vectorPos))
        {
            return null;
        }

        int count = ReadVectorElementCount(vectorPos, elementByteSize: 1);
        if (count == 0)
        {
            return [];
        }

        bool[] result = new bool[count];
        long dataPos = vectorPos + 4;
        for (int i = 0; i < count; i++)
        {
            result[i] = _source.ReadByte(dataPos + i) != 0;
        }

        return result;
    }

    /// <summary>
    /// Captures a FlatBuffers <c>[ubyte]</c> / <c>[uint8]</c> byte vector field at <paramref name="fieldIndex"/> as a lazy <see cref="TensorDataSlice"/>
    /// without reading or allocating its payload bytes.
    /// </summary>
    /// <param name="fieldIndex">The zero-based field index of the byte vector.</param>
    /// <param name="dataType">The tensor element data type stored in the byte vector.</param>
    /// <returns>A <see cref="TensorDataSlice"/> if the byte vector is present and non-empty; otherwise, <see langword="null"/>.</returns>
    public TensorDataSlice? CaptureByteVectorSlice(int fieldIndex, DataType dataType)
    {
        if (!TryDereferenceUOffset(fieldIndex, out long vectorPos))
        {
            return null;
        }

        int byteLength = ReadVectorElementCount(vectorPos, elementByteSize: 1);
        if (byteLength <= 0)
        {
            return null;
        }

        long payloadOffset = vectorPos + 4;
        return new TensorDataSlice(payloadOffset, byteLength, dataType, TensorDataEncoding.Raw);
    }

    private static string ReadStringAt(RandomAccessSource source, long stringPos)
    {
        if (stringPos < 0 || stringPos + 4 > source.Length)
        {
            throw new ModelParseException(
                $"FlatBuffers string offset 0x{stringPos:X} is outside stream bounds (0x{source.Length:X}).",
                fileOffset: stringPos);
        }

        uint byteLength = source.ReadUInt32LittleEndian(stringPos);
        long payloadOffset = stringPos + 4;
        if (byteLength > int.MaxValue || payloadOffset + byteLength > source.Length)
        {
            throw new ModelParseException(
                $"FlatBuffers string length ({byteLength} bytes) at 0x{stringPos:X} exceeds stream bounds (0x{source.Length:X}).",
                fileOffset: stringPos);
        }

        return source.ReadUtf8String(payloadOffset, (int)byteLength);
    }

    private int ReadVectorElementCount(long vectorPos, int elementByteSize)
    {
        if (vectorPos < 0 || vectorPos + 4 > _source.Length)
        {
            throw new ModelParseException(
                $"FlatBuffers vector offset 0x{vectorPos:X} is outside stream bounds (0x{_source.Length:X}).",
                fileOffset: vectorPos);
        }

        uint count = _source.ReadUInt32LittleEndian(vectorPos);
        long totalByteSize = (long)count * elementByteSize;
        long dataStart = vectorPos + 4;
        if (count > int.MaxValue || totalByteSize < 0 || dataStart + totalByteSize > _source.Length)
        {
            throw new ModelParseException(
                $"FlatBuffers vector of {count} element(s) (elementSize={elementByteSize}) at 0x{vectorPos:X} exceeds stream bounds (0x{_source.Length:X}).",
                fileOffset: vectorPos);
        }

        return (int)count;
    }

    private bool TryDereferenceUOffset(int fieldIndex, out long targetOffset)
    {
        targetOffset = 0;
        if (!TryGetFieldOffset(fieldIndex, requiredByteSize: 4, out long fieldPos))
        {
            return false;
        }

        uint uoffset = _source.ReadUInt32LittleEndian(fieldPos);
        if (uoffset == 0)
        {
            return false;
        }

        long resolved = fieldPos + uoffset;
        if (uoffset < 4 || resolved < 0 || resolved >= _source.Length)
        {
            throw new ModelParseException(
                $"FlatBuffers field {fieldIndex} uoffset (0x{uoffset:X}) at 0x{fieldPos:X} points outside stream bounds (0x{_source.Length:X}).",
                fileOffset: fieldPos);
        }

        targetOffset = resolved;
        return true;
    }

    private bool TryGetFieldOffset(int fieldIndex, int requiredByteSize, out long fieldOffset)
    {
        fieldOffset = 0;
        if (fieldIndex < 0)
        {
            return false;
        }

        int vtableEntryOffset = 4 + (fieldIndex * 2);
        if (vtableEntryOffset + 2 > _vtableByteLength)
        {
            return false;
        }

        ushort offsetInTable = _source.ReadUInt16LittleEndian(_vtableOffset + vtableEntryOffset);
        if (offsetInTable == 0)
        {
            return false;
        }

        if (offsetInTable < 4 || offsetInTable + requiredByteSize > _tableInlineByteSize)
        {
            throw new ModelParseException(
                $"FlatBuffers field {fieldIndex} inline offset ({offsetInTable}) + size ({requiredByteSize}) exceeds table inline size ({_tableInlineByteSize}) at 0x{_tableOffset:X}.",
                fileOffset: _tableOffset);
        }

        long resolved = _tableOffset + offsetInTable;
        if (resolved + requiredByteSize > _source.Length)
        {
            throw new ModelParseException(
                $"FlatBuffers field {fieldIndex} at 0x{resolved:X} exceeds stream bounds (0x{_source.Length:X}).",
                fileOffset: resolved);
        }

        fieldOffset = resolved;
        return true;
    }
}
