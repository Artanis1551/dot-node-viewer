using System.Buffers.Binary;
using System.Text;
using DotNodeViewer.Core.Abstractions;

namespace DotNodeViewer.Core.Delegates.EthosU;

/// <summary>
/// Provides zero-allocation validation and parsing for ExecuTorch <c>vela_bin_stream</c> blocks.
/// </summary>
internal static class VelaBinStreamReader
{
    private const int HeaderSize = 32;
    private const int NameLength = 16;
    private const string StreamHeaderName = "vela_bin_stream";
    private const string StreamFooterName = "vela_end_stream";

    /// <summary>
    /// Parses a sequence of 16-byte-aligned Vela blocks from a byte span.
    /// </summary>
    /// <param name="span">The payload byte span containing the binary stream.</param>
    /// <param name="baseFileOffset">The absolute file offset where this span begins, for diagnostic reporting.</param>
    /// <returns>A parsed <see cref="VelaBinStream"/> instance.</returns>
    /// <exception cref="ModelParseException">Thrown if the stream header, footer, or block boundaries are malformed.</exception>
    public static VelaBinStream Read(ReadOnlySpan<byte> span, long baseFileOffset = 0)
    {
        if (span.Length < HeaderSize)
        {
            throw new ModelParseException(
                $"Vela binary stream length ({span.Length} bytes) is smaller than the minimum block header size ({HeaderSize} bytes).",
                baseFileOffset,
                "VelaBinStream");
        }

        var blocks = new List<VelaBlock>();
        VelaBlock? cmdData = null;
        VelaBlock? weightData = null;
        var inputs = new List<VelaIoTensor>();
        var outputs = new List<VelaIoTensor>();
        uint scratchSize = 0;

        int cursor = 0;
        bool seenHeader = false;
        bool seenFooter = false;

        while (cursor + HeaderSize <= span.Length)
        {
            int blockStart = cursor;
            ReadOnlySpan<byte> headerSpan = span.Slice(cursor, HeaderSize);

            // 1. Block name: 16 bytes ASCII null-padded
            string name = ExtractBlockName(headerSpan[..NameLength]);
            if (string.IsNullOrEmpty(name))
            {
                throw new ModelParseException(
                    $"Encountered empty or null block name in Vela binary stream at offset 0x{baseFileOffset + blockStart:X8}.",
                    baseFileOffset + blockStart,
                    "VelaBinStream");
            }

            // 2. Block length: uint32 little-endian
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(headerSpan.Slice(16, 4));
            if (length > int.MaxValue)
            {
                throw new ModelParseException(
                    $"Block '{name}' length 0x{length:X8} exceeds maximum supported 31-bit size.",
                    baseFileOffset + blockStart,
                    "VelaBinStream");
            }
            int intLength = (int)length;

            // 3. External reference flag: 1 byte
            byte isExternal = headerSpan[20];

            // 4. Verify stream header is first
            if (!seenHeader)
            {
                if (name != StreamHeaderName)
                {
                    throw new ModelParseException(
                        $"Vela binary stream must begin with '{StreamHeaderName}' block, found '{name}' at offset 0x{baseFileOffset:X8}.",
                        baseFileOffset,
                        "VelaBinStream");
                }
                seenHeader = true;
            }

            cursor += HeaderSize;

            if (cursor + intLength > span.Length)
            {
                throw new ModelParseException(
                    $"Block '{name}' payload ({intLength} bytes) exceeds available stream length ({span.Length - cursor} bytes) at offset 0x{baseFileOffset + cursor:X8}.",
                    baseFileOffset + cursor,
                    "VelaBinStream");
            }

            long payloadOffset = baseFileOffset + cursor;
            var block = new VelaBlock(name, payloadOffset, intLength, isExternal != 0);
            blocks.Add(block);

            ReadOnlySpan<byte> payloadSpan = span.Slice(cursor, intLength);

            switch (name)
            {
                case "cmd_data":
                    cmdData = block;
                    break;

                case "weight_data":
                    weightData = block;
                    break;

                case "scratch_size":
                    if (intLength >= 4)
                    {
                        scratchSize = BinaryPrimitives.ReadUInt32LittleEndian(payloadSpan);
                    }
                    break;

                case "inputs":
                    ParseIoDescriptors(payloadSpan, inputs, baseFileOffset + cursor, "inputs");
                    break;

                case "outputs":
                    ParseIoDescriptors(payloadSpan, outputs, baseFileOffset + cursor, "outputs");
                    break;

                case StreamFooterName:
                    seenFooter = true;
                    break;
            }

            // Move cursor past payload and 16-byte alignment padding
            int alignedLength = intLength == 0 ? 0 : (intLength + 15) & ~15;
            cursor += alignedLength;

            if (seenFooter)
            {
                break;
            }
        }

        if (!seenFooter)
        {
            throw new ModelParseException(
                $"Vela binary stream is missing terminating '{StreamFooterName}' block.",
                baseFileOffset + cursor,
                "VelaBinStream");
        }

        return new VelaBinStream(blocks, cmdData, weightData, inputs, outputs, scratchSize);
    }

    private static string ExtractBlockName(ReadOnlySpan<byte> bytes)
    {
        int nullIdx = bytes.IndexOf((byte)0);
        ReadOnlySpan<byte> trimmed = nullIdx >= 0 ? bytes[..nullIdx] : bytes;
        return Encoding.ASCII.GetString(trimmed).Trim();
    }

    private static void ParseIoDescriptors(
        ReadOnlySpan<byte> span,
        List<VelaIoTensor> destination,
        long offset,
        string context)
    {
        if (span.Length < 4)
        {
            return;
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(span[..4]);
        if (count < 0 || count > 1024)
        {
            throw new ModelParseException(
                $"Invalid tensor count {count} in Vela '{context}' block.",
                offset,
                "VelaBinStream");
        }

        const int TensorDescriptorSize = 36; // 6D shape (24 bytes) + elem_size (4) + offset (4) + region (4)
        int expectedLength = 4 + count * TensorDescriptorSize;
        if (span.Length < expectedLength)
        {
            throw new ModelParseException(
                $"Truncated '{context}' block: expected {expectedLength} bytes for {count} tensors, got {span.Length} bytes.",
                offset,
                "VelaBinStream");
        }

        int pos = 4;
        for (int i = 0; i < count; i++)
        {
            int d0 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4));
            int d1 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 4, 4));
            int d2 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 8, 4));
            int d3 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 12, 4));
            int d4 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 16, 4));
            int d5 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 20, 4));
            int elemSize = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 24, 4));
            int tensorOffset = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 28, 4));
            int region = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 32, 4));

            var shape = new int[] { d0, d1, d2, d3, d4, d5 };
            destination.Add(new VelaIoTensor(shape, elemSize, tensorOffset, region));
            pos += TensorDescriptorSize;
        }
    }
}

