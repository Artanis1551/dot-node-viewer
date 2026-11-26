using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;

namespace DotNodeViewer.Core.Formats.Gguf;

/// <summary>
/// Hard safety and memory bounds enforced during GGUF header and metadata parsing.
/// </summary>
internal static class GgufLimits
{
    public const ulong MaxMetadataKvCount = 1_000_000;
    public const ulong MaxTensorCount = 1_000_000;
    public const ulong MaxStringByteLength = 16 * 1024 * 1024;
    public const uint MaxTensorDimensions = 8;
    public const int MaxArrayNestingDepth = 8;
    public const int MaxMaterializedArrayElements = 64;
    public const int MaxSampledStringElements = 4;
    public const uint DefaultAlignment = 32;
    public const uint MaxAlignment = 64 * 1024 * 1024;
}

internal sealed record GgufHeader(
    uint Version,
    bool IsBigEndian,
    ulong TensorCount,
    ulong MetadataKvCount);

internal sealed record GgufTensorRecord(
    string Name,
    ulong[] RawDims,
    GgmlTypeInfo TypeInfo,
    ulong RelativeOffset,
    long AbsoluteOffset,
    long ByteLength);

internal sealed record GgufParsedFile(
    GgufHeader Header,
    Dictionary<string, string> Metadata,
    Dictionary<string, long> IntegerMetadata,
    Dictionary<string, double> FloatMetadata,
    List<GgufTensorRecord> Tensors,
    long DataBaseOffset,
    uint Alignment);

/// <summary>
/// Zero-allocation streaming GGUF v1–v3 binary metadata and tensor-table reader backed by <see cref="RandomAccessSource"/>.
/// </summary>
internal sealed class GgufBinaryReader
{
    private const uint TypeUInt8 = 0;
    private const uint TypeInt8 = 1;
    private const uint TypeUInt16 = 2;
    private const uint TypeInt16 = 3;
    private const uint TypeUInt32 = 4;
    private const uint TypeInt32 = 5;
    private const uint TypeFloat32 = 6;
    private const uint TypeBool = 7;
    private const uint TypeString = 8;
    private const uint TypeArray = 9;
    private const uint TypeUInt64 = 10;
    private const uint TypeInt64 = 11;
    private const uint TypeFloat64 = 12;

    private readonly RandomAccessSource _source;
    private readonly bool _isBigEndian;
    private readonly uint _version;
    private long _position;

    private GgufBinaryReader(RandomAccessSource source, uint version, bool isBigEndian, long startPosition)
    {
        _source = source;
        _version = version;
        _isBigEndian = isBigEndian;
        _position = startPosition;
    }

    /// <summary>
    /// Parses the GGUF header, metadata KV section, tensor info table, and aligned data offsets without reading tensor weights.
    /// </summary>
    public static GgufParsedFile Read(Stream stream, CancellationToken cancellationToken = default)
    {
        using RandomAccessSource source = new(stream);
        if (source.Length < 16)
        {
            throw new ModelParseException("GGUF stream is too short to contain a valid header.", fileOffset: 0);
        }

        Span<byte> magicBuf = stackalloc byte[8];
        source.ReadExactly(0, magicBuf);
        if (magicBuf[0] != (byte)'G' || magicBuf[1] != (byte)'G' || magicBuf[2] != (byte)'U' || magicBuf[3] != (byte)'F')
        {
            throw new ModelParseException("Invalid GGUF magic header (expected 'GGUF').", fileOffset: 0);
        }

        uint leVersion = BinaryPrimitives.ReadUInt32LittleEndian(magicBuf.Slice(4, 4));
        bool isBigEndian;
        uint version;

        if (leVersion is 1 or 2 or 3)
        {
            isBigEndian = false;
            version = leVersion;
        }
        else if (BinaryPrimitives.ReadUInt32BigEndian(magicBuf.Slice(4, 4)) == 3)
        {
            isBigEndian = true;
            version = 3;
        }
        else
        {
            throw new ModelParseException(
                $"Unsupported GGUF format version (raw LE=0x{leVersion:X8}). Supported versions: 1, 2, 3.",
                fileOffset: 4);
        }

        GgufBinaryReader reader = new(source, version, isBigEndian, startPosition: 8);
        ulong tensorCount = reader.ReadCount("tensor_count");
        ulong kvCount = reader.ReadCount("metadata_kv_count");

        if (tensorCount > GgufLimits.MaxTensorCount)
        {
            throw new ModelParseException(
                $"GGUF tensor count ({tensorCount}) exceeds safety limit ({GgufLimits.MaxTensorCount}).",
                fileOffset: 8);
        }

        if (kvCount > GgufLimits.MaxMetadataKvCount)
        {
            throw new ModelParseException(
                $"GGUF metadata KV count ({kvCount}) exceeds safety limit ({GgufLimits.MaxMetadataKvCount}).",
                fileOffset: version == 1 ? 12 : 16);
        }

        GgufHeader header = new(version, isBigEndian, tensorCount, kvCount);
        Dictionary<string, string> metadata = new(StringComparer.Ordinal);
        Dictionary<string, long> intMetadata = new(StringComparer.Ordinal);
        Dictionary<string, double> floatMetadata = new(StringComparer.Ordinal);

        for (ulong i = 0; i < kvCount; i++)
        {
            if ((i & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string key = reader.ReadString("metadata_key");
            uint valueType = reader.ReadUInt32(key);
            reader.ReadAndRecordMetadataValue(key, valueType, metadata, intMetadata, floatMetadata, depth: 0);
        }

        List<PendingTensorInfo> pendingTensors = new((int)Math.Min(tensorCount, 65536));
        for (ulong i = 0; i < tensorCount; i++)
        {
            if ((i & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            long entryOffset = reader._position;
            string name = reader.ReadString("tensor_name");
            uint nDims = reader.ReadUInt32(name);
            if (nDims > GgufLimits.MaxTensorDimensions)
            {
                throw new ModelParseException(
                    $"GGUF tensor '{name}' dimension rank ({nDims}) exceeds limit ({GgufLimits.MaxTensorDimensions}).",
                    fileOffset: entryOffset,
                    context: name);
            }

            ulong[] rawDims = nDims == 0 ? [] : new ulong[nDims];
            for (int d = 0; d < rawDims.Length; d++)
            {
                rawDims[d] = reader.ReadCount(name);
            }

            long typeOffset = reader._position;
            uint ggmlTypeCode = reader.ReadUInt32(name);
            if (!GgmlTypeCatalog.TryGet(ggmlTypeCode, out GgmlTypeInfo typeInfo))
            {
                throw new ModelParseException(
                    $"Unsupported GGML tensor data type code {ggmlTypeCode} on tensor '{name}'.",
                    fileOffset: typeOffset,
                    context: name);
            }

            ulong relativeOffset = reader.ReadUInt64(name);
            long byteLength = GgmlTypeCatalog.ComputeTensorByteLength(typeInfo, rawDims, name, entryOffset);

            pendingTensors.Add(new PendingTensorInfo(name, rawDims, typeInfo, relativeOffset, byteLength, entryOffset));
        }

        uint alignment = GgufLimits.DefaultAlignment;
        if (intMetadata.TryGetValue("general.alignment", out long rawAlign))
        {
            if (rawAlign <= 0 || rawAlign > GgufLimits.MaxAlignment || (rawAlign & (rawAlign - 1)) != 0)
            {
                throw new ModelParseException(
                    $"Invalid GGUF 'general.alignment' value {rawAlign}; expected a positive power of two <= {GgufLimits.MaxAlignment}.",
                    fileOffset: reader._position,
                    context: "general.alignment");
            }

            alignment = (uint)rawAlign;
        }

        long dataBaseOffset = AlignUp(reader._position, alignment, source.Length);

        List<GgufTensorRecord> tensors = new(pendingTensors.Count);
        for (int i = 0; i < pendingTensors.Count; i++)
        {
            PendingTensorInfo pending = pendingTensors[i];
            UInt128 absOffset128 = (UInt128)dataBaseOffset + (UInt128)pending.RelativeOffset;
            UInt128 endOffset128 = absOffset128 + (UInt128)pending.ByteLength;

            if (absOffset128 > (UInt128)long.MaxValue || endOffset128 > (UInt128)source.Length)
            {
                throw new ModelParseException(
                    $"GGUF tensor '{pending.Name}' payload range [0x{absOffset128:X}..0x{endOffset128:X}) exceeds stream length 0x{source.Length:X}.",
                    fileOffset: pending.EntryOffset,
                    context: pending.Name);
            }

            tensors.Add(new GgufTensorRecord(
                pending.Name,
                pending.RawDims,
                pending.TypeInfo,
                pending.RelativeOffset,
                (long)absOffset128,
                pending.ByteLength));
        }

        return new GgufParsedFile(header, metadata, intMetadata, floatMetadata, tensors, dataBaseOffset, alignment);
    }

    private void ReadAndRecordMetadataValue(
        string key,
        uint valueType,
        Dictionary<string, string> metadata,
        Dictionary<string, long> intMetadata,
        Dictionary<string, double> floatMetadata,
        int depth)
    {
        switch (valueType)
        {
            case TypeUInt8:
            {
                byte v = ReadByte(key);
                metadata[key] = v.ToString(CultureInfo.InvariantCulture);
                intMetadata[key] = v;
                break;
            }
            case TypeInt8:
            {
                sbyte v = unchecked((sbyte)ReadByte(key));
                metadata[key] = v.ToString(CultureInfo.InvariantCulture);
                intMetadata[key] = v;
                break;
            }
            case TypeUInt16:
            {
                ushort v = ReadUInt16(key);
                metadata[key] = v.ToString(CultureInfo.InvariantCulture);
                intMetadata[key] = v;
                break;
            }
            case TypeInt16:
            {
                short v = ReadInt16(key);
                metadata[key] = v.ToString(CultureInfo.InvariantCulture);
                intMetadata[key] = v;
                break;
            }
            case TypeUInt32:
            {
                uint v = ReadUInt32(key);
                metadata[key] = v.ToString(CultureInfo.InvariantCulture);
                intMetadata[key] = v;
                break;
            }
            case TypeInt32:
            {
                int v = ReadInt32(key);
                metadata[key] = v.ToString(CultureInfo.InvariantCulture);
                intMetadata[key] = v;
                break;
            }
            case TypeFloat32:
            {
                float v = ReadSingle(key);
                metadata[key] = v.ToString("G7", CultureInfo.InvariantCulture);
                floatMetadata[key] = v;
                break;
            }
            case TypeBool:
            {
                bool v = ReadBool(key);
                metadata[key] = v ? "true" : "false";
                break;
            }
            case TypeString:
            {
                metadata[key] = ReadString(key);
                break;
            }
            case TypeUInt64:
            {
                ulong v = ReadUInt64(key);
                metadata[key] = v.ToString(CultureInfo.InvariantCulture);
                if (v <= long.MaxValue)
                {
                    intMetadata[key] = (long)v;
                }
                break;
            }
            case TypeInt64:
            {
                long v = ReadInt64(key);
                metadata[key] = v.ToString(CultureInfo.InvariantCulture);
                intMetadata[key] = v;
                break;
            }
            case TypeFloat64:
            {
                double v = ReadDouble(key);
                metadata[key] = v.ToString("G15", CultureInfo.InvariantCulture);
                floatMetadata[key] = v;
                break;
            }
            case TypeArray:
            {
                metadata[key] = ReadOrSummarizeArray(key, depth);
                break;
            }
            default:
                throw new ModelParseException(
                    $"Unknown GGUF metadata value type {valueType} for key '{key}'.",
                    fileOffset: _position,
                    context: key);
        }
    }

    private string ReadOrSummarizeArray(string key, int depth)
    {
        if (depth >= GgufLimits.MaxArrayNestingDepth)
        {
            throw new ModelParseException(
                $"GGUF array nesting depth exceeded limit ({GgufLimits.MaxArrayNestingDepth}) for key '{key}'.",
                fileOffset: _position,
                context: key);
        }

        uint elemType = ReadUInt32(key);
        ulong count = ReadCount(key);

        if (elemType == TypeString)
        {
            if (count <= GgufLimits.MaxMaterializedArrayElements)
            {
                int n = (int)count;
                string[] items = new string[n];
                for (int i = 0; i < n; i++)
                {
                    items[i] = ReadString(key);
                }

                return $"[{string.Join(", ", items)}]";
            }

            int sampleCount = (int)Math.Min(count, (ulong)GgufLimits.MaxSampledStringElements);
            string[] samples = new string[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                string s = ReadString(key);
                samples[i] = s.Length > 32 ? $"{s[..32]}..." : s;
            }

            for (ulong i = (ulong)sampleCount; i < count; i++)
            {
                SkipString(key);
            }

            return $"[{count.ToString(CultureInfo.InvariantCulture)} strings: {string.Join(", ", samples)}, ...]";
        }

        if (elemType == TypeArray)
        {
            if (count <= GgufLimits.MaxMaterializedArrayElements)
            {
                int n = (int)count;
                string[] nested = new string[n];
                for (int i = 0; i < n; i++)
                {
                    nested[i] = ReadOrSummarizeArray(key, depth + 1);
                }

                return $"[{string.Join(", ", nested)}]";
            }

            for (ulong i = 0; i < count; i++)
            {
                SkipArray(key, depth + 1);
            }

            return $"[{count.ToString(CultureInfo.InvariantCulture)} nested arrays]";
        }

        int elemSize = GetFixedScalarSize(elemType, key);
        UInt128 totalBytes;
        try
        {
            totalBytes = checked((UInt128)count * (UInt128)elemSize);
        }
        catch (OverflowException ex)
        {
            throw new ModelParseException(
                $"GGUF array byte length overflow for key '{key}'.",
                fileOffset: _position,
                context: key,
                innerException: ex);
        }

        if ((UInt128)_position + totalBytes > (UInt128)_source.Length)
        {
            throw new ModelParseException(
                $"GGUF array '{key}' ({count} elements, {totalBytes} bytes) extends past end of stream.",
                fileOffset: _position,
                context: key);
        }

        if (count > GgufLimits.MaxMaterializedArrayElements)
        {
            _position += (long)totalBytes;
            return $"[{count.ToString(CultureInfo.InvariantCulture)} {GetTypeDisplayName(elemType)} values]";
        }

        int itemCount = (int)count;
        string[] formatted = new string[itemCount];
        for (int i = 0; i < itemCount; i++)
        {
            formatted[i] = ReadScalarAsString(elemType, key);
        }

        return $"[{string.Join(", ", formatted)}]";
    }

    private void SkipArray(string key, int depth)
    {
        if (depth >= GgufLimits.MaxArrayNestingDepth)
        {
            throw new ModelParseException(
                $"GGUF array nesting depth exceeded limit ({GgufLimits.MaxArrayNestingDepth}) for key '{key}'.",
                fileOffset: _position,
                context: key);
        }

        uint elemType = ReadUInt32(key);
        ulong count = ReadCount(key);

        if (elemType == TypeString)
        {
            for (ulong i = 0; i < count; i++)
            {
                SkipString(key);
            }

            return;
        }

        if (elemType == TypeArray)
        {
            for (ulong i = 0; i < count; i++)
            {
                SkipArray(key, depth + 1);
            }

            return;
        }

        int elemSize = GetFixedScalarSize(elemType, key);
        UInt128 totalBytes;
        try
        {
            totalBytes = checked((UInt128)count * (UInt128)elemSize);
        }
        catch (OverflowException ex)
        {
            throw new ModelParseException(
                $"GGUF nested array byte length overflow for key '{key}'.",
                fileOffset: _position,
                context: key,
                innerException: ex);
        }

        if ((UInt128)_position + totalBytes > (UInt128)_source.Length)
        {
            throw new ModelParseException(
                $"GGUF nested array '{key}' extends past end of stream.",
                fileOffset: _position,
                context: key);
        }

        _position += (long)totalBytes;
    }

    private string ReadScalarAsString(uint elemType, string key) => elemType switch
    {
        TypeUInt8 => ReadByte(key).ToString(CultureInfo.InvariantCulture),
        TypeInt8 => unchecked((sbyte)ReadByte(key)).ToString(CultureInfo.InvariantCulture),
        TypeUInt16 => ReadUInt16(key).ToString(CultureInfo.InvariantCulture),
        TypeInt16 => ReadInt16(key).ToString(CultureInfo.InvariantCulture),
        TypeUInt32 => ReadUInt32(key).ToString(CultureInfo.InvariantCulture),
        TypeInt32 => ReadInt32(key).ToString(CultureInfo.InvariantCulture),
        TypeFloat32 => ReadSingle(key).ToString("G7", CultureInfo.InvariantCulture),
        TypeBool => ReadBool(key) ? "true" : "false",
        TypeUInt64 => ReadUInt64(key).ToString(CultureInfo.InvariantCulture),
        TypeInt64 => ReadInt64(key).ToString(CultureInfo.InvariantCulture),
        TypeFloat64 => ReadDouble(key).ToString("G15", CultureInfo.InvariantCulture),
        _ => throw new ModelParseException($"Unsupported GGUF scalar type {elemType} for '{key}'.", fileOffset: _position, context: key)
    };

    private int GetFixedScalarSize(uint elemType, string key) => elemType switch
    {
        TypeUInt8 or TypeInt8 or TypeBool => 1,
        TypeUInt16 or TypeInt16 => 2,
        TypeUInt32 or TypeInt32 or TypeFloat32 => 4,
        TypeUInt64 or TypeInt64 or TypeFloat64 => 8,
        _ => throw new ModelParseException(
            $"Unknown GGUF array element type {elemType} for key '{key}'.",
            fileOffset: _position,
            context: key)
    };

    private static string GetTypeDisplayName(uint elemType) => elemType switch
    {
        TypeUInt8 => "UInt8",
        TypeInt8 => "Int8",
        TypeUInt16 => "UInt16",
        TypeInt16 => "Int16",
        TypeUInt32 => "UInt32",
        TypeInt32 => "Int32",
        TypeFloat32 => "Float32",
        TypeBool => "Bool",
        TypeUInt64 => "UInt64",
        TypeInt64 => "Int64",
        TypeFloat64 => "Float64",
        _ => $"Type({elemType})"
    };

    private ulong ReadCount(string context) =>
        _version == 1 ? ReadUInt32(context) : ReadUInt64(context);

    private string ReadString(string context)
    {
        long lenOffset = _position;
        ulong rawLen = ReadCount(context);
        if (rawLen > GgufLimits.MaxStringByteLength)
        {
            throw new ModelParseException(
                $"GGUF string length ({rawLen}) exceeds safety limit ({GgufLimits.MaxStringByteLength}) while reading '{context}'.",
                fileOffset: lenOffset,
                context: context);
        }

        int len = (int)rawLen;
        EnsureAvailable(len, context);
        string result = _source.ReadUtf8String(_position, len);
        _position += len;
        return result;
    }

    private void SkipString(string context)
    {
        long lenOffset = _position;
        ulong rawLen = ReadCount(context);
        if (rawLen > GgufLimits.MaxStringByteLength)
        {
            throw new ModelParseException(
                $"GGUF string length ({rawLen}) exceeds safety limit ({GgufLimits.MaxStringByteLength}) while skipping '{context}'.",
                fileOffset: lenOffset,
                context: context);
        }

        int len = (int)rawLen;
        EnsureAvailable(len, context);
        _position += len;
    }

    private byte ReadByte(string context)
    {
        EnsureAvailable(1, context);
        byte value = _source.ReadByte(_position);
        _position += 1;
        return value;
    }

    private bool ReadBool(string context)
    {
        byte raw = ReadByte(context);
        return raw != 0;
    }

    private ushort ReadUInt16(string context)
    {
        EnsureAvailable(2, context);
        Span<byte> buf = stackalloc byte[2];
        _source.ReadExactly(_position, buf);
        _position += 2;
        return _isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(buf)
            : BinaryPrimitives.ReadUInt16LittleEndian(buf);
    }

    private short ReadInt16(string context) => unchecked((short)ReadUInt16(context));

    private uint ReadUInt32(string context)
    {
        EnsureAvailable(4, context);
        Span<byte> buf = stackalloc byte[4];
        _source.ReadExactly(_position, buf);
        _position += 4;
        return _isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(buf)
            : BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }

    private int ReadInt32(string context) => unchecked((int)ReadUInt32(context));

    private ulong ReadUInt64(string context)
    {
        EnsureAvailable(8, context);
        Span<byte> buf = stackalloc byte[8];
        _source.ReadExactly(_position, buf);
        _position += 8;
        return _isBigEndian
            ? BinaryPrimitives.ReadUInt64BigEndian(buf)
            : BinaryPrimitives.ReadUInt64LittleEndian(buf);
    }

    private long ReadInt64(string context) => unchecked((long)ReadUInt64(context));

    private float ReadSingle(string context)
    {
        EnsureAvailable(4, context);
        Span<byte> buf = stackalloc byte[4];
        _source.ReadExactly(_position, buf);
        _position += 4;
        return _isBigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(buf)
            : BinaryPrimitives.ReadSingleLittleEndian(buf);
    }

    private double ReadDouble(string context)
    {
        EnsureAvailable(8, context);
        Span<byte> buf = stackalloc byte[8];
        _source.ReadExactly(_position, buf);
        _position += 8;
        return _isBigEndian
            ? BinaryPrimitives.ReadDoubleBigEndian(buf)
            : BinaryPrimitives.ReadDoubleLittleEndian(buf);
    }

    private void EnsureAvailable(int count, string context)
    {
        if (count < 0 || _position > _source.Length - count)
        {
            throw new ModelParseException(
                $"Unexpected end of GGUF stream reading {count} byte(s) for '{context}' at offset 0x{_position:X}.",
                fileOffset: _position,
                context: context);
        }
    }

    private static long AlignUp(long position, uint alignment, long streamLength)
    {
        UInt128 pos = (UInt128)position;
        UInt128 align = alignment;
        UInt128 rem = pos % align;
        UInt128 aligned = rem == 0 ? pos : pos + (align - rem);

        if (aligned > (UInt128)streamLength || aligned > (UInt128)long.MaxValue)
        {
            throw new ModelParseException(
                $"GGUF tensor data alignment offset (0x{aligned:X}) exceeds stream length (0x{streamLength:X}).",
                fileOffset: position,
                context: "data_base_offset");
        }

        return (long)aligned;
    }

    private readonly record struct PendingTensorInfo(
        string Name,
        ulong[] RawDims,
        GgmlTypeInfo TypeInfo,
        ulong RelativeOffset,
        long ByteLength,
        long EntryOffset);
}
