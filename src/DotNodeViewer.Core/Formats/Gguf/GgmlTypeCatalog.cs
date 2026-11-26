using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Gguf;

/// <summary>
/// Describes a GGML tensor element or quantization block format.
/// </summary>
internal readonly record struct GgmlTypeInfo(
    uint Code,
    string Name,
    int BlockSize,
    int TypeSizeBytes,
    bool IsQuantized,
    DataType DataType,
    int NominalBits);

/// <summary>
/// Authoritative catalog of GGML tensor element types, block sizes, and checked byte-length calculations.
/// </summary>
internal static class GgmlTypeCatalog
{
    /// <summary>
    /// Attempts to look up metadata for a GGML <c>ggml_type</c> numeric code.
    /// </summary>
    public static bool TryGet(uint code, out GgmlTypeInfo info)
    {
        switch (code)
        {
            case 0:
                info = new GgmlTypeInfo(0, "F32", 1, 4, false, DataType.Float32, 32);
                return true;
            case 1:
                info = new GgmlTypeInfo(1, "F16", 1, 2, false, DataType.Float16, 16);
                return true;
            case 2:
                info = new GgmlTypeInfo(2, "Q4_0", 32, 18, true, DataType.UInt8, 4);
                return true;
            case 3:
                info = new GgmlTypeInfo(3, "Q4_1", 32, 20, true, DataType.UInt8, 4);
                return true;
            case 6:
                info = new GgmlTypeInfo(6, "Q5_0", 32, 22, true, DataType.UInt8, 5);
                return true;
            case 7:
                info = new GgmlTypeInfo(7, "Q5_1", 32, 24, true, DataType.UInt8, 5);
                return true;
            case 8:
                info = new GgmlTypeInfo(8, "Q8_0", 32, 34, true, DataType.UInt8, 8);
                return true;
            case 9:
                info = new GgmlTypeInfo(9, "Q8_1", 32, 36, true, DataType.UInt8, 8);
                return true;
            case 10:
                info = new GgmlTypeInfo(10, "Q2_K", 256, 84, true, DataType.UInt8, 2);
                return true;
            case 11:
                info = new GgmlTypeInfo(11, "Q3_K", 256, 110, true, DataType.UInt8, 3);
                return true;
            case 12:
                info = new GgmlTypeInfo(12, "Q4_K", 256, 144, true, DataType.UInt8, 4);
                return true;
            case 13:
                info = new GgmlTypeInfo(13, "Q5_K", 256, 176, true, DataType.UInt8, 5);
                return true;
            case 14:
                info = new GgmlTypeInfo(14, "Q6_K", 256, 210, true, DataType.UInt8, 6);
                return true;
            case 15:
                info = new GgmlTypeInfo(15, "Q8_K", 256, 292, true, DataType.UInt8, 8);
                return true;
            case 16:
                info = new GgmlTypeInfo(16, "IQ2_XXS", 256, 66, true, DataType.UInt8, 2);
                return true;
            case 17:
                info = new GgmlTypeInfo(17, "IQ2_XS", 256, 74, true, DataType.UInt8, 2);
                return true;
            case 18:
                info = new GgmlTypeInfo(18, "IQ3_XXS", 256, 98, true, DataType.UInt8, 3);
                return true;
            case 19:
                info = new GgmlTypeInfo(19, "IQ1_S", 256, 50, true, DataType.UInt8, 1);
                return true;
            case 20:
                info = new GgmlTypeInfo(20, "IQ4_NL", 32, 18, true, DataType.UInt8, 4);
                return true;
            case 21:
                info = new GgmlTypeInfo(21, "IQ3_S", 256, 110, true, DataType.UInt8, 3);
                return true;
            case 22:
                info = new GgmlTypeInfo(22, "IQ2_S", 256, 82, true, DataType.UInt8, 2);
                return true;
            case 23:
                info = new GgmlTypeInfo(23, "IQ4_XS", 256, 136, true, DataType.UInt8, 4);
                return true;
            case 24:
                info = new GgmlTypeInfo(24, "I8", 1, 1, false, DataType.Int8, 8);
                return true;
            case 25:
                info = new GgmlTypeInfo(25, "I16", 1, 2, false, DataType.Int16, 16);
                return true;
            case 26:
                info = new GgmlTypeInfo(26, "I32", 1, 4, false, DataType.Int32, 32);
                return true;
            case 27:
                info = new GgmlTypeInfo(27, "I64", 1, 8, false, DataType.Int64, 64);
                return true;
            case 28:
                info = new GgmlTypeInfo(28, "F64", 1, 8, false, DataType.Float64, 64);
                return true;
            case 29:
                info = new GgmlTypeInfo(29, "IQ1_M", 256, 56, true, DataType.UInt8, 1);
                return true;
            case 30:
                info = new GgmlTypeInfo(30, "BF16", 1, 2, false, DataType.BFloat16, 16);
                return true;
            case 34:
                info = new GgmlTypeInfo(34, "TQ1_0", 256, 54, true, DataType.UInt8, 2);
                return true;
            case 35:
                info = new GgmlTypeInfo(35, "TQ2_0", 256, 66, true, DataType.UInt8, 2);
                return true;
            case 39:
                info = new GgmlTypeInfo(39, "MXFP4", 32, 17, true, DataType.UInt8, 4);
                return true;
            default:
                info = default;
                return false;
        }
    }

    /// <summary>
    /// Computes the exact byte length of a GGML tensor from its raw innermost-first dimensions using checked 128-bit arithmetic.
    /// </summary>
    public static long ComputeTensorByteLength(
        GgmlTypeInfo typeInfo,
        ReadOnlySpan<ulong> rawDims,
        string tensorName,
        long fileOffset)
    {
        if (rawDims.Length == 0)
        {
            if (typeInfo.BlockSize > 1)
            {
                throw new ModelParseException(
                    $"Block-quantized GGML tensor '{tensorName}' ({typeInfo.Name}) cannot be rank-0 scalar.",
                    fileOffset: fileOffset,
                    context: tensorName);
            }

            return typeInfo.TypeSizeBytes;
        }

        for (int i = 0; i < rawDims.Length; i++)
        {
            if (rawDims[i] == 0)
            {
                throw new ModelParseException(
                    $"GGUF tensor '{tensorName}' has zero dimension at axis {i}.",
                    fileOffset: fileOffset,
                    context: tensorName);
            }
        }

        ulong blockSize = (ulong)typeInfo.BlockSize;
        if (rawDims[0] % blockSize != 0)
        {
            throw new ModelParseException(
                $"GGUF tensor '{tensorName}' ({typeInfo.Name}) innermost dimension {rawDims[0]} is not divisible by block size {typeInfo.BlockSize}.",
                fileOffset: fileOffset,
                context: tensorName);
        }

        try
        {
            checked
            {
                UInt128 blocksInRow = (UInt128)(rawDims[0] / blockSize);
                UInt128 totalBytes = blocksInRow * (UInt128)typeInfo.TypeSizeBytes;

                for (int i = 1; i < rawDims.Length; i++)
                {
                    totalBytes *= (UInt128)rawDims[i];
                }

                if (totalBytes > (UInt128)long.MaxValue)
                {
                    throw new ModelParseException(
                        $"GGUF tensor '{tensorName}' byte length ({totalBytes}) exceeds maximum supported signed 64-bit stream length.",
                        fileOffset: fileOffset,
                        context: tensorName);
                }

                return (long)totalBytes;
            }
        }
        catch (OverflowException ex)
        {
            throw new ModelParseException(
                $"Arithmetic overflow computing byte length for GGUF tensor '{tensorName}' ({typeInfo.Name}).",
                fileOffset: fileOffset,
                context: tensorName,
                innerException: ex);
        }
    }
}
