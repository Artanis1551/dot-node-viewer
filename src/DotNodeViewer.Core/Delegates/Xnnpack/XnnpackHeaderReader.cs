using System.Buffers.Binary;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Delegates.Xnnpack;

/// <summary>
/// Reads and bounds-checks the optional 30-byte <c>XH00</c> header at the start of an XNNPACK delegate payload.
/// </summary>
internal static class XnnpackHeaderReader
{
    /// <summary>
    /// The fixed size in bytes of an <c>XH00</c> header.
    /// </summary>
    public const int ExpectedHeaderLength = 30;

    /// <summary>
    /// Reads and validates the <see cref="XnnpackHeader"/> for <paramref name="processedSlice"/> without buffering the full payload.
    /// </summary>
    /// <param name="modelStream">The seekable model stream containing the delegate payload.</param>
    /// <param name="processedSlice">The slice locating the processed delegate payload in <paramref name="modelStream"/>.</param>
    /// <returns>The validated <see cref="XnnpackHeader"/>.</returns>
    /// <exception cref="ModelParseException">The payload is truncated, has an unrecognized magic identifier, or declares out-of-bounds offsets or lengths.</exception>
    public static XnnpackHeader Read(Stream modelStream, TensorDataSlice processedSlice)
    {
        ArgumentNullException.ThrowIfNull(modelStream);
        ArgumentNullException.ThrowIfNull(processedSlice);

        if (!modelStream.CanRead || !modelStream.CanSeek)
        {
            throw new ModelParseException("Model stream must be readable and seekable to inspect XNNPACK delegate payloads.", processedSlice.StreamOffset);
        }

        if (processedSlice.StreamOffset < 0 ||
            processedSlice.ByteLength < 8 ||
            processedSlice.StreamOffset > modelStream.Length ||
            processedSlice.ByteLength > modelStream.Length - processedSlice.StreamOffset)
        {
            throw new ModelParseException(
                $"XNNPACK delegate payload slice [0x{processedSlice.StreamOffset:X}..+0x{processedSlice.ByteLength:X}) is invalid or outside stream bounds (0x{modelStream.Length:X}).",
                fileOffset: processedSlice.StreamOffset);
        }

        int probeLength = (int)Math.Min(ExpectedHeaderLength, processedSlice.ByteLength);
        Span<byte> headerBytes = stackalloc byte[ExpectedHeaderLength];
        Span<byte> probeSpan = headerBytes[..probeLength];

        long savedPosition = modelStream.Position;
        try
        {
            modelStream.Position = processedSlice.StreamOffset;
            modelStream.ReadExactly(probeSpan);
        }
        catch (EndOfStreamException ex)
        {
            throw new ModelParseException(
                "Unexpected end of stream while reading XNNPACK delegate header.",
                fileOffset: processedSlice.StreamOffset,
                innerException: ex);
        }
        finally
        {
            modelStream.Position = savedPosition;
        }

        ReadOnlySpan<byte> magic = probeSpan.Slice(4, 4);
        if (magic.SequenceEqual("XH00"u8))
        {
            if (probeLength < ExpectedHeaderLength)
            {
                throw new ModelParseException(
                    $"Truncated XNNPACK XH00 header ({probeLength} bytes; expected at least {ExpectedHeaderLength} bytes).",
                    fileOffset: processedSlice.StreamOffset);
            }

            ushort headerLength = BinaryPrimitives.ReadUInt16LittleEndian(probeSpan.Slice(8, 2));
            uint flatbufferOffset = BinaryPrimitives.ReadUInt32LittleEndian(probeSpan.Slice(10, 4));
            uint flatbufferSize = BinaryPrimitives.ReadUInt32LittleEndian(probeSpan.Slice(14, 4));
            uint constantDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(probeSpan.Slice(18, 4));
            ulong constantDataSize = BinaryPrimitives.ReadUInt64LittleEndian(probeSpan.Slice(22, 8));

            if (headerLength < ExpectedHeaderLength || headerLength > processedSlice.ByteLength)
            {
                throw new ModelParseException(
                    $"Invalid XNNPACK XH00 header length ({headerLength} bytes).",
                    fileOffset: processedSlice.StreamOffset + 8);
            }

            if (flatbufferSize < 8 || flatbufferOffset < headerLength)
            {
                throw new ModelParseException(
                    $"Invalid XNNPACK FlatBuffer region (offset={flatbufferOffset}, size={flatbufferSize}).",
                    fileOffset: processedSlice.StreamOffset + 10);
            }

            long fbEnd = (long)flatbufferOffset + flatbufferSize;
            if (fbEnd > processedSlice.ByteLength)
            {
                throw new ModelParseException(
                    $"XNNPACK FlatBuffer region [{flatbufferOffset}..{fbEnd}) exceeds delegate payload length ({processedSlice.ByteLength} bytes).",
                    fileOffset: processedSlice.StreamOffset + 10);
            }

            if (constantDataOffset < fbEnd)
            {
                throw new ModelParseException(
                    $"XNNPACK constant data offset ({constantDataOffset}) overlaps FlatBuffer region ending at {fbEnd}.",
                    fileOffset: processedSlice.StreamOffset + 18);
            }

            if (constantDataSize > (ulong)long.MaxValue ||
                (long)constantDataOffset > processedSlice.ByteLength ||
                (long)constantDataSize > processedSlice.ByteLength - constantDataOffset)
            {
                throw new ModelParseException(
                    $"XNNPACK constant data region [offset={constantDataOffset}, size={constantDataSize}] exceeds delegate payload length ({processedSlice.ByteLength} bytes).",
                    fileOffset: processedSlice.StreamOffset + 18);
            }

            return new XnnpackHeader(
                HasHeader: true,
                FlatBufferOffset: flatbufferOffset,
                FlatBufferSize: flatbufferSize,
                ConstantDataOffset: constantDataOffset,
                ConstantDataSize: (long)constantDataSize);
        }

        if (magic.SequenceEqual("XN01"u8) || magic.SequenceEqual("XN00"u8))
        {
            return new XnnpackHeader(
                HasHeader: false,
                FlatBufferOffset: 0,
                FlatBufferSize: processedSlice.ByteLength,
                ConstantDataOffset: processedSlice.ByteLength,
                ConstantDataSize: 0);
        }

        throw new ModelParseException(
            "Unrecognized XNNPACK delegate payload identifier at offset 4 (expected 'XH00', 'XN01', or 'XN00').",
            fileOffset: processedSlice.StreamOffset + 4);
    }
}
