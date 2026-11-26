namespace DotNodeViewer.Core.Delegates.Xnnpack;

/// <summary>
/// Represents the parsed 30-byte <c>XH00</c> header framing an ExecuTorch XNNPACK delegate payload.
/// </summary>
/// <param name="HasHeader"><see langword="true"/> if an <c>XH00</c> header was present; <see langword="false"/> if the payload starts directly at the FlatBuffer.</param>
/// <param name="FlatBufferOffset">The byte offset of the <c>XNNGraph</c> FlatBuffer relative to the delegate payload start.</param>
/// <param name="FlatBufferSize">The byte length of the <c>XNNGraph</c> FlatBuffer.</param>
/// <param name="ConstantDataOffset">The byte offset of the trailing constant data segment relative to the delegate payload start.</param>
/// <param name="ConstantDataSize">The byte length of the trailing constant data segment.</param>
internal readonly record struct XnnpackHeader(
    bool HasHeader,
    long FlatBufferOffset,
    long FlatBufferSize,
    long ConstantDataOffset,
    long ConstantDataSize);
