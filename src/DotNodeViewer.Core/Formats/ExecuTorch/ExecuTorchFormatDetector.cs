using System.Buffers.Binary;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.ExecuTorch;

/// <summary>
/// Detects ExecuTorch (<c>.pte</c>) FlatBuffer model streams by validating the <c>ET12</c> or <c>ET1x</c> file identifier at bytes <c>4..8</c>
/// and validating root table/vtable bounds.
/// </summary>
internal sealed class ExecuTorchFormatDetector : IModelFormatDetector
{
    /// <summary>
    /// Gets a shared singleton instance of <see cref="ExecuTorchFormatDetector"/>.
    /// </summary>
    public static ExecuTorchFormatDetector Instance { get; } = new();

    /// <inheritdoc/>
    public ModelFormatInfo Format { get; } = new(ModelFormatId.ExecuTorch, "ExecuTorch");

    /// <inheritdoc/>
    public bool CanRead(ModelProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        ReadOnlySpan<byte> header = probe.Header;
        if (header.Length < 8 || probe.StreamLength < 8)
        {
            return false;
        }

        // Check file identifier at offset 4..8: must be "ET12" or "ET1x"
        ReadOnlySpan<byte> id = header.Slice(4, 4);
        bool isEt = id[0] == (byte)'E' && id[1] == (byte)'T' && id[2] == (byte)'1';
        if (!isEt)
        {
            return false;
        }

        uint rootUOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        if (rootUOffset < 8 || rootUOffset + 4 > probe.StreamLength)
        {
            return false;
        }

        // Verify root table vtable offset
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

        if (vtableLength < 4 || (vtableLength & 1) != 0 || vtableOffset + vtableLength > probe.StreamLength)
        {
            return false;
        }

        if (tableInlineSize < 4 || rootUOffset + tableInlineSize > probe.StreamLength)
        {
            return false;
        }

        // If extended header "eh00" is present at offset 8, check its minimum size
        if (header.Length >= 40 && header.Slice(8, 4).SequenceEqual("eh00"u8))
        {
            uint ehLength = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4));
            if (ehLength < 24)
            {
                return false;
            }
        }

        return true;
    }
}

