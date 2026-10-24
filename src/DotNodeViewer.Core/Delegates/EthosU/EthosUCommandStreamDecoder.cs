using System.Buffers.Binary;
using System.Globalization;

namespace DotNodeViewer.Core.Delegates.EthosU;

/// <summary>
/// Delegate invoked for each decoded command in an Ethos-U command stream.
/// </summary>
internal delegate void EthosUCommandAction(in EthosUCommand command);

/// <summary>
/// Provides high-performance zero-allocation decoding of 32-bit and 64-bit Ethos-U register commands.
/// </summary>
internal static class EthosUCommandStreamDecoder
{
    private const int CmdModeNoPayload = 0x0000;
    private const int CmdModePayload32 = 0x4000;
    private const int CmdModeMask = 0xC000;
    private const int CmdOpMask = 0x03FF;

    /// <summary>
    /// Decodes a span of command stream bytes and invokes the specified action for each decoded command.
    /// </summary>
    /// <param name="span">The command stream byte span.</param>
    /// <param name="action">The callback invoked for each decoded command.</param>
    /// <param name="diagnostics">Optional list to collect non-fatal decoding warnings.</param>
    /// <returns>The total number of commands successfully decoded.</returns>
    public static int Decode(
        ReadOnlySpan<byte> span,
        EthosUCommandAction action,
        List<string>? diagnostics = null)
    {
        int cursor = 0;
        int wordOffset = 0;
        int commandCount = 0;

        while (cursor + 4 <= span.Length)
        {
            uint word0 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cursor, 4));
            int code = (int)(word0 & 0xFFFF);
            int param = (int)((word0 >> 16) & 0xFFFF);
            int mode = code & CmdModeMask;
            int opcode = code & CmdOpMask;

            if (mode == CmdModeNoPayload)
            {
                string name = EthosUOpcodeCatalog.GetCmd0Name(opcode);
                var cmd = new EthosUCommand(wordOffset, cursor, opcode, param, null, false, name);
                action(in cmd);
                commandCount++;
                cursor += 4;
                wordOffset += 1;

                if (opcode == EthosUOpcodeCatalog.NpuOpStop)
                {
                    // STOP instruction completes the active command stream
                    break;
                }
            }
            else if (mode == CmdModePayload32)
            {
                if (cursor + 8 > span.Length)
                {
                    diagnostics?.Add(string.Create(CultureInfo.InvariantCulture,
                        $"Truncated cmd1 payload at word offset {wordOffset} (byte offset 0x{cursor:X8})."));
                    break;
                }

                uint payload = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cursor + 4, 4));
                string name = EthosUOpcodeCatalog.GetCmd1Name(opcode);
                var cmd = new EthosUCommand(wordOffset, cursor, opcode, param, payload, true, name);
                action(in cmd);
                commandCount++;
                cursor += 8;
                wordOffset += 2;
            }
            else
            {
                string name = string.Create(CultureInfo.InvariantCulture, $"UNKNOWN_MODE_0x{mode:X4}_OP_0x{opcode:X4}");
                diagnostics?.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Unsupported command mode 0x{mode:X4} for opcode 0x{opcode:X4} at word offset {wordOffset}."));
                var cmd = new EthosUCommand(wordOffset, cursor, opcode, param, null, false, name);
                action(in cmd);
                commandCount++;
                cursor += 4;
                wordOffset += 1;
            }
        }

        return commandCount;
    }

    /// <summary>
    /// Decodes a span of command stream bytes into a list of commands.
    /// </summary>
    /// <param name="span">The command stream byte span.</param>
    /// <param name="diagnostics">Optional list to collect non-fatal decoding warnings.</param>
    /// <returns>A list containing all decoded commands.</returns>
    public static List<EthosUCommand> DecodeToList(
        ReadOnlySpan<byte> span,
        List<string>? diagnostics = null)
    {
        var list = new List<EthosUCommand>(span.Length / 4);
        Decode(span, (in EthosUCommand cmd) => list.Add(cmd), diagnostics);
        return list;
    }
}
