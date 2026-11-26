using System.Buffers.Binary;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Gguf;

/// <summary>
/// Non-destructive format detector for GGUF (<c>.gguf</c>) v1–v3 model containers.
/// </summary>
internal sealed class GgufFormatDetector : IModelFormatDetector
{
    /// <summary>
    /// Gets the shared singleton instance of <see cref="GgufFormatDetector"/>.
    /// </summary>
    public static GgufFormatDetector Instance { get; } = new();

    private GgufFormatDetector()
    {
    }

    /// <inheritdoc />
    public ModelFormatInfo Format { get; } = new(ModelFormatId.Gguf, "GGUF");

    /// <inheritdoc />
    public bool CanRead(ModelProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        ReadOnlySpan<byte> h = probe.Header;
        if (h.Length < 16)
        {
            return false;
        }

        // Magic bytes: "GGUF"
        if (h[0] != (byte)'G' || h[1] != (byte)'G' || h[2] != (byte)'U' || h[3] != (byte)'F')
        {
            return false;
        }

        uint leVersion = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(4, 4));
        bool isBigEndian;
        uint version;

        if (leVersion is 1 or 2 or 3)
        {
            isBigEndian = false;
            version = leVersion;
        }
        else if (BinaryPrimitives.ReadUInt32BigEndian(h.Slice(4, 4)) == 3)
        {
            isBigEndian = true;
            version = 3;
        }
        else
        {
            return false;
        }

        if (version == 1)
        {
            uint tensorCount = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(8, 4));
            uint kvCount = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(12, 4));
            return tensorCount <= GgufLimits.MaxTensorCount
                && kvCount <= GgufLimits.MaxMetadataKvCount;
        }

        if (h.Length < 24)
        {
            return false;
        }

        ulong countTensors = isBigEndian
            ? BinaryPrimitives.ReadUInt64BigEndian(h.Slice(8, 8))
            : BinaryPrimitives.ReadUInt64LittleEndian(h.Slice(8, 8));
        ulong countKv = isBigEndian
            ? BinaryPrimitives.ReadUInt64BigEndian(h.Slice(16, 8))
            : BinaryPrimitives.ReadUInt64LittleEndian(h.Slice(16, 8));

        return countTensors <= GgufLimits.MaxTensorCount
            && countKv <= GgufLimits.MaxMetadataKvCount;
    }
}
