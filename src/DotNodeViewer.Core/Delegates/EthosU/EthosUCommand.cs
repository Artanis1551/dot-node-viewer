namespace DotNodeViewer.Core.Delegates.EthosU;

/// <summary>
/// Represents a single decoded 32-bit (cmd0) or 64-bit (cmd1) register command from an Ethos-U NPU stream.
/// </summary>
/// <param name="WordOffset">The zero-based word index of this command in the command stream.</param>
/// <param name="ByteOffset">The zero-based byte offset of this command in the command stream.</param>
/// <param name="Opcode">The 10-bit hardware opcode.</param>
/// <param name="Param">The 16-bit command parameter field.</param>
/// <param name="Payload">The 32-bit payload word if <paramref name="IsCmd1"/> is <c>true</c>; otherwise <c>null</c>.</param>
/// <param name="IsCmd1">A value indicating whether this command carries a 32-bit payload word.</param>
/// <param name="Name">The symbolic disassembly name of the command.</param>
internal readonly record struct EthosUCommand(
    int WordOffset,
    int ByteOffset,
    int Opcode,
    int Param,
    uint? Payload,
    bool IsCmd1,
    string Name);

