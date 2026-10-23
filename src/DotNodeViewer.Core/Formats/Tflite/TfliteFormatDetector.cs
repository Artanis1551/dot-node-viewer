using System.Buffers.Binary;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Tflite;

/// <summary>
/// Detects TensorFlow Lite (<c>TFL3</c>) FlatBuffer model streams by validating the <c>TFL3</c> file identifier at bytes <c>4..8</c>
/// and the root table/vtable offsets.
/// </summary>
internal sealed class TfliteFormatDetector : IModelFormatDetector
{
    /// <summary>
    /// Gets a shared singleton instance of <see cref="TfliteFormatDetector"/>.
    /// </summary>
    public static TfliteFormatDetector Instance { get; } = new();

    /// <inheritdoc/>
    public ModelFormatInfo Format { get; } = new(ModelFormatId.Tflite, "TensorFlow Lite");

    /// <inheritdoc/>
    public bool CanRead(ModelProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        ReadOnlySpan<byte> header = probe.Header;
        if (header.Length < 12 || probe.StreamLength < 12)
        {
            return false;
        }

        // Check "TFL3" file identifier at offset 4..8
        if (!header.Slice(4, 4).SequenceEqual("TFL3"u8))
        {
            return false;
        }

        uint rootUOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        if (rootUOffset < 8 || rootUOffset + 4 > probe.StreamLength)
        {
            return false;
        }

        // Verify that the root table's vtable soffset points to a valid vtable within stream bounds
        Span<byte> soffsetBytes = stackalloc byte[4];
        if (probe.ReadAt(rootUOffset, soffsetBytes) != 4)
        {
            return false;
        }

        int vtableSOffset = BinaryPrimitives.ReadInt32LittleEndian(soffsetBytes);
        long vtableOffset = (long)rootUOffset - vtableSOffset;
        if (vtableOffset < 4 || vtableOffset + 4 > probe.StreamLength)
        {
            return false;
        }

        Span<byte> vtableHeader = stackalloc byte[4];
        if (probe.ReadAt(vtableOffset, vtableHeader) != 4)
        {
            return false;
        }

        ushort vtableLength = BinaryPrimitives.ReadUInt16LittleEndian(vtableHeader[..2]);
        ushort tableInlineSize = BinaryPrimitives.ReadUInt16LittleEndian(vtableHeader.Slice(2, 2));

        return vtableLength >= 4
            && (vtableLength & 1) == 0
            && vtableOffset + vtableLength <= probe.StreamLength
            && tableInlineSize >= 4
            && rootUOffset + tableInlineSize <= probe.StreamLength;
    }
}

