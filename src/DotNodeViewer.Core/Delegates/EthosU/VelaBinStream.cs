namespace DotNodeViewer.Core.Delegates.EthosU;

/// <summary>
/// Represents a named block within an ExecuTorch <c>vela_bin_stream</c> payload.
/// </summary>
/// <param name="Name">The 16-byte null-padded ASCII block identifier.</param>
/// <param name="Offset">The zero-based byte offset within the stream where the block payload begins.</param>
/// <param name="Length">The length of the unpadded payload in bytes.</param>
/// <param name="IsExternal">A value indicating whether the payload is stored externally.</param>
internal sealed record VelaBlock(string Name, long Offset, int Length, bool IsExternal);

/// <summary>
/// Represents input or output tensor metadata packed inside a Vela binary stream.
/// </summary>
/// <param name="Shape">The 6D tensor shape.</param>
/// <param name="ElementSize">The element size in bytes.</param>
/// <param name="Offset">The tensor offset in the scratch/arena memory region.</param>
/// <param name="Region">The memory region index (0 for flash, 1 for arena, etc.).</param>
internal sealed record VelaIoTensor(IReadOnlyList<int> Shape, int ElementSize, int Offset, int Region);

/// <summary>
/// Represents a parsed ExecuTorch <c>vela_bin_stream</c> container.
/// </summary>
/// <param name="Blocks">All named blocks discovered in the stream in sequence.</param>
/// <param name="CmdData">The <c>cmd_data</c> block containing driver payload and command stream, if present.</param>
/// <param name="WeightData">The <c>weight_data</c> block containing encoded weights and scales, if present.</param>
/// <param name="Inputs">The parsed input tensor descriptors from the <c>inputs</c> block.</param>
/// <param name="Outputs">The parsed output tensor descriptors from the <c>outputs</c> block.</param>
/// <param name="ScratchSize">The allocated scratch SRAM size in bytes.</param>
internal sealed record VelaBinStream(
    IReadOnlyList<VelaBlock> Blocks,
    VelaBlock? CmdData,
    VelaBlock? WeightData,
    IReadOnlyList<VelaIoTensor> Inputs,
    IReadOnlyList<VelaIoTensor> Outputs,
    uint ScratchSize);

