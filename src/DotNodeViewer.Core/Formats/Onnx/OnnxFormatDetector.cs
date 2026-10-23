using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Onnx;

/// <summary>
/// Detects Open Neural Network Exchange (ONNX) <c>ModelProto</c> streams using a bounded, non-destructive Protocol Buffers tag walk.
/// </summary>
internal sealed class OnnxFormatDetector : IModelFormatDetector
{
    private const int MaxTopLevelFields = 64;

    /// <summary>
    /// Gets a shared singleton instance of <see cref="OnnxFormatDetector"/>.
    /// </summary>
    public static OnnxFormatDetector Instance { get; } = new();

    /// <inheritdoc/>
    public ModelFormatInfo Format { get; } = new(ModelFormatId.Onnx, "ONNX");

    /// <inheritdoc/>
    public bool CanRead(ModelProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (probe.StreamLength < 4 || probe.IsZipArchive)
        {
            return false;
        }

        ReadOnlySpan<byte> header = probe.Header;
        if (HasKnownNonOnnxSignature(header))
        {
            return false;
        }

        long offset = 0;
        long streamLength = probe.StreamLength;
        int fieldsSeen = 0;
        bool hasIrVersion = false;
        bool hasOpsetImport = false;
        bool hasGraph = false;

        while (offset < streamLength && fieldsSeen < MaxTopLevelFields)
        {
            fieldsSeen++;
            if (!TryReadVarint64(probe, ref offset, streamLength, out ulong rawTag))
            {
                return false;
            }

            ulong fieldNumber = rawTag >> 3;
            ProtoWireType wireType = (ProtoWireType)(rawTag & 0x07);

            // ONNX ModelProto fields are within 1..25; reject invalid field numbers or group wire types.
            if (fieldNumber == 0 || fieldNumber > 100)
            {
                return false;
            }

            if ((byte)wireType > (byte)ProtoWireType.Fixed32
                || wireType == ProtoWireType.StartGroup
                || wireType == ProtoWireType.EndGroup)
            {
                return false;
            }

            switch (fieldNumber)
            {
                case 1: // int64 ir_version
                    if (wireType != ProtoWireType.Varint
                        || !TryReadVarint64(probe, ref offset, streamLength, out ulong irVersion)
                        || irVersion == 0
                        || irVersion > 50)
                    {
                        return false;
                    }

                    hasIrVersion = true;
                    break;

                case 2: // string producer_name
                case 3: // string producer_version
                case 4: // string domain
                case 6: // string doc_string
                case 14: // repeated StringStringEntryProto metadata_props
                case 20: // repeated TrainingInfoProto training_info
                case 25: // repeated FunctionProto functions
                    if (wireType != ProtoWireType.LengthDelimited
                        || !TryReadLengthDelimitedBounds(probe, ref offset, streamLength, out _, out _))
                    {
                        return false;
                    }

                    break;

                case 5: // int64 model_version
                    if (wireType != ProtoWireType.Varint
                        || !TryReadVarint64(probe, ref offset, streamLength, out _))
                    {
                        return false;
                    }

                    break;

                case 7: // GraphProto graph
                    if (wireType != ProtoWireType.LengthDelimited
                        || !TryReadLengthDelimitedBounds(probe, ref offset, streamLength, out long graphStart, out long graphLen)
                        || graphLen <= 0
                        || !ValidateGraphProtoStart(probe, graphStart, graphLen))
                    {
                        return false;
                    }

                    hasGraph = true;
                    break;

                case 8: // repeated OperatorSetIdProto opset_import
                    if (wireType != ProtoWireType.LengthDelimited
                        || !TryReadLengthDelimitedBounds(probe, ref offset, streamLength, out long opsetStart, out long opsetLen)
                        || !ValidateOpsetImport(probe, opsetStart, opsetLen))
                    {
                        return false;
                    }

                    hasOpsetImport = true;
                    break;

                default:
                    if (!TrySkipField(probe, wireType, ref offset, streamLength))
                    {
                        return false;
                    }

                    break;
            }
        }

        if (offset != streamLength && fieldsSeen < MaxTopLevelFields)
        {
            return false;
        }

        if (!hasGraph)
        {
            return false;
        }

        bool hasExtensionTieBreaker = string.Equals(probe.FileExtension, ".onnx", StringComparison.OrdinalIgnoreCase);
        return hasIrVersion || hasOpsetImport || hasExtensionTieBreaker;
    }

    private static bool HasKnownNonOnnxSignature(ReadOnlySpan<byte> header)
    {
        // HDF5 magic: \x89HDF\r\n\x1a\n
        if (header.Length >= 4
            && header[0] == 0x89
            && header[1] == (byte)'H'
            && header[2] == (byte)'D'
            && header[3] == (byte)'F')
        {
            return true;
        }

        // FlatBuffers TFL3 / ET12 magic at offset 4..8
        if (header.Length >= 8)
        {
            ReadOnlySpan<byte> id = header.Slice(4, 4);
            if (id.SequenceEqual("TFL3"u8) || id.SequenceEqual("ET12"u8))
            {
                return true;
            }
        }

        // Python pickle PROTO opcode 0x80
        if (header.Length >= 2 && header[0] == 0x80 && header[1] >= 2 && header[1] <= 5)
        {
            return true;
        }

        return false;
    }

    private static bool ValidateOpsetImport(ModelProbe probe, long startOffset, long length)
    {
        long offset = startOffset;
        long endOffset = startOffset + length;
        while (offset < endOffset)
        {
            if (!TryReadVarint64(probe, ref offset, endOffset, out ulong rawTag))
            {
                return false;
            }

            ulong fieldNumber = rawTag >> 3;
            ProtoWireType wireType = (ProtoWireType)(rawTag & 0x07);
            if (fieldNumber == 1 && wireType == ProtoWireType.LengthDelimited)
            {
                if (!TryReadLengthDelimitedBounds(probe, ref offset, endOffset, out _, out _))
                {
                    return false;
                }
            }
            else if (fieldNumber == 2 && wireType == ProtoWireType.Varint)
            {
                if (!TryReadVarint64(probe, ref offset, endOffset, out ulong ver) || ver == 0)
                {
                    return false;
                }
            }
            else if (!TrySkipField(probe, wireType, ref offset, endOffset))
            {
                return false;
            }
        }

        return offset == endOffset;
    }

    private static bool ValidateGraphProtoStart(ModelProbe probe, long graphStart, long graphLength)
    {
        long offset = graphStart;
        long endOffset = graphStart + graphLength;
        if (!TryReadVarint64(probe, ref offset, endOffset, out ulong firstTag))
        {
            return false;
        }

        ulong fieldNumber = firstTag >> 3;
        ProtoWireType wireType = (ProtoWireType)(firstTag & 0x07);

        // All standard GraphProto fields (node=1, name=2, initializer=5, doc_string=10, input=11, output=12, value_info=13, quantization_annotation=14, sparse_initializer=15) are LengthDelimited.
        return wireType == ProtoWireType.LengthDelimited
            && fieldNumber is 1 or 2 or 5 or 10 or 11 or 12 or 13 or 14 or 15
            && TryReadLengthDelimitedBounds(probe, ref offset, endOffset, out _, out _);
    }

    private static bool TryReadLengthDelimitedBounds(
        ModelProbe probe,
        ref long offset,
        long limit,
        out long payloadStart,
        out long payloadLength)
    {
        payloadStart = 0;
        payloadLength = 0;
        if (!TryReadVarint64(probe, ref offset, limit, out ulong rawLen))
        {
            return false;
        }

        if (rawLen > (ulong)(limit - offset))
        {
            return false;
        }

        payloadStart = offset;
        payloadLength = (long)rawLen;
        offset += payloadLength;
        return true;
    }

    private static bool TrySkipField(ModelProbe probe, ProtoWireType wireType, ref long offset, long limit)
    {
        switch (wireType)
        {
            case ProtoWireType.Varint:
                return TryReadVarint64(probe, ref offset, limit, out _);

            case ProtoWireType.Fixed64:
                if (limit - offset < 8)
                {
                    return false;
                }

                offset += 8;
                return true;

            case ProtoWireType.LengthDelimited:
                return TryReadLengthDelimitedBounds(probe, ref offset, limit, out _, out _);

            case ProtoWireType.Fixed32:
                if (limit - offset < 4)
                {
                    return false;
                }

                offset += 4;
                return true;

            default:
                return false;
        }
    }

    private static bool TryReadVarint64(ModelProbe probe, ref long offset, long limit, out ulong value)
    {
        value = 0;
        ReadOnlySpan<byte> header = probe.Header;
        Span<byte> scratch = stackalloc byte[1];

        for (int shift = 0; shift < 64; shift += 7)
        {
            if (offset >= limit)
            {
                return false;
            }

            byte b;
            if (offset < header.Length)
            {
                b = header[(int)offset];
            }
            else
            {
                if (probe.ReadAt(offset, scratch) != 1)
                {
                    return false;
                }

                b = scratch[0];
            }

            offset++;
            if (shift == 63 && (b & 0xFE) != 0)
            {
                return false;
            }

            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }
}
