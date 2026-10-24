namespace DotNodeViewer.Core.Delegates.EthosU;

/// <summary>
/// Represents the parsed metadata from an Arm Ethos-U <c>COP1</c> driver action payload.
/// </summary>
/// <param name="Architecture">The hardware architecture identifier (for example, <c>Ethos-U55-128</c> or <c>Ethos-U65-256</c>).</param>
/// <param name="Product">The accelerator product generation code (0: U55, 1: U65, 2: U85).</param>
/// <param name="MacsPerCycle">The compute performance capacity in MAC operations per clock cycle.</param>
/// <param name="ShramSizeKb">The shared scratchpad/SHRAM memory size in kilobytes.</param>
/// <param name="CmdStreamVersion">The command stream format version.</param>
/// <param name="CommandStreamByteOffset">The zero-based byte offset within the driver payload where the command stream begins.</param>
/// <param name="CommandStreamByteLength">The total byte length of the command stream words.</param>
/// <param name="CommandWordCount">The total number of 32-bit command words.</param>
internal sealed record EthosUDriverPayload(
    string Architecture,
    int Product,
    int MacsPerCycle,
    int ShramSizeKb,
    int CmdStreamVersion,
    int CommandStreamByteOffset,
    int CommandStreamByteLength,
    int CommandWordCount);
