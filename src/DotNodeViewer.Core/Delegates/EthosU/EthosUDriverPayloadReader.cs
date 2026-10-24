using System.Buffers.Binary;
using DotNodeViewer.Core.Abstractions;

namespace DotNodeViewer.Core.Delegates.EthosU;

/// <summary>
/// Provides zero-allocation parsing and validation for Arm Ethos-U <c>COP1</c> driver action payloads.
/// </summary>
internal static class EthosUDriverPayloadReader
{
    private const uint Cop1Magic = 0x31504F43; // "COP1" in little-endian ASCII ('C','O','P','1')
    private const int MaxCommandWords = 4 * 1024 * 1024; // 4 million words (16 MB) safety guard

    private enum DaCommand
    {
        Reserved = 0x00,
        Config = 0x01,
        CmdStream = 0x02,
        ReadApb = 0x03,
        DumpShram = 0x04,
        Nop = 0x05,
    }

    /// <summary>
    /// Reads and validates the driver action header from a byte span.
    /// </summary>
    /// <param name="span">The driver payload bytes.</param>
    /// <param name="baseFileOffset">The source file offset for error context.</param>
    /// <returns>A parsed <see cref="EthosUDriverPayload"/> instance.</returns>
    /// <exception cref="ModelParseException">Thrown if the magic bytes mismatch or the payload is truncated.</exception>
    public static EthosUDriverPayload Read(ReadOnlySpan<byte> span, long baseFileOffset = 0)
    {
        if (span.Length < 8)
        {
            throw new ModelParseException(
                $"Ethos-U driver payload is too small ({span.Length} bytes). Minimum COP1 header is 8 bytes.",
                baseFileOffset,
                "EthosUDriverPayload");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(span[..4]);
        if (magic != Cop1Magic)
        {
            throw new ModelParseException(
                $"Invalid Ethos-U driver payload magic: expected 'COP1' (0x{Cop1Magic:X8}), found 0x{magic:X8}.",
                baseFileOffset,
                "EthosUDriverPayload");
        }

        int cursor = 4;
        int product = 0;
        int macsPerCycle = 128;
        int shramSizeKb = 32;
        int cmdStreamVersion = 0;
        string arch = "Ethos-U55-128";

        while (cursor + 4 <= span.Length)
        {
            uint tagWord = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cursor, 4));
            cursor += 4;

            int commandId = (int)(tagWord & 0xFF);
            int reserved = (int)((tagWord >> 8) & 0xFF);
            int param = (int)((tagWord >> 16) & 0xFFFF);

            var cmd = (DaCommand)commandId;

            switch (cmd)
            {
                case DaCommand.Config:
                    if (cursor + 8 > span.Length)
                    {
                        throw new ModelParseException(
                            "Truncated DACommands.Config block in Ethos-U driver payload.",
                            baseFileOffset + cursor,
                            "EthosUDriverPayload");
                    }

                    uint configWord = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cursor, 4));
                    uint idWord = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cursor + 4, 4));
                    cursor += 8;

                    int log2Macs = (int)(configWord & 0x0F);
                    cmdStreamVersion = (int)((configWord >> 4) & 0x0F);
                    shramSizeKb = (int)((configWord >> 8) & 0xFF);
                    product = (int)((configWord >> 28) & 0x0F);
                    macsPerCycle = 1 << log2Macs;

                    arch = product switch
                    {
                        0 => $"Ethos-U55-{macsPerCycle}",
                        1 => $"Ethos-U65-{macsPerCycle}",
                        2 => $"Ethos-U85-{macsPerCycle}",
                        _ => $"Ethos-U-Product{product}-{macsPerCycle}",
                    };
                    break;

                case DaCommand.CmdStream:
                    int lengthWords = (reserved << 16) | param;
                    if (lengthWords < 0 || lengthWords > MaxCommandWords)
                    {
                        throw new ModelParseException(
                            $"Ethos-U command stream word count ({lengthWords}) exceeds safety ceiling of {MaxCommandWords} words.",
                            baseFileOffset + cursor - 4,
                            "EthosUDriverPayload");
                    }

                    int streamByteLength = lengthWords * 4;
                    int streamByteOffset = cursor;

                    if (cursor + streamByteLength > span.Length)
                    {
                        throw new ModelParseException(
                            $"Truncated Ethos-U command stream: expected {streamByteLength} bytes ({lengthWords} words), but only {span.Length - cursor} bytes remain.",
                            baseFileOffset + cursor,
                            "EthosUDriverPayload");
                    }

                    return new EthosUDriverPayload(
                        arch,
                        product,
                        macsPerCycle,
                        shramSizeKb,
                        cmdStreamVersion,
                        streamByteOffset,
                        streamByteLength,
                        lengthWords);

                case DaCommand.Nop:
                case DaCommand.DumpShram:
                case DaCommand.ReadApb:
                case DaCommand.Reserved:
                default:
                    // Advance to next tag word
                    break;
            }
        }

        throw new ModelParseException(
            "Ethos-U driver payload did not contain a DACommands.CmdStream entry.",
            baseFileOffset + cursor,
            "EthosUDriverPayload");
    }
}
