namespace DotNodeViewer.Core.Ir;

/// <summary>
/// Represents quantization parameters attached to a <see cref="TensorDescriptor"/>.
/// </summary>
/// <remarks>
/// <see cref="QuantizationInfo"/> is a closed hierarchy consisting of <see cref="AffineQuantization"/>
/// and <see cref="BlockQuantization"/>.
/// </remarks>
public abstract record QuantizationInfo
{
    private protected QuantizationInfo()
    {
    }
}

/// <summary>
/// Represents per-tensor or per-channel affine quantization parameters (<c>real_value = scale * (quantized_value - zero_point)</c>).
/// </summary>
/// <param name="Scale">The quantization scale factor(s). Contains a single element for per-tensor quantization or one element per channel for per-axis quantization.</param>
/// <param name="ZeroPoint">The quantization zero-point offset(s).</param>
/// <param name="QuantizedDimension">The optional axis index along which per-channel scales and zero-points are applied, or <see langword="null"/> for per-tensor quantization.</param>
public sealed record AffineQuantization(
    IReadOnlyList<float> Scale,
    IReadOnlyList<long> ZeroPoint,
    int? QuantizedDimension = null) : QuantizationInfo;

/// <summary>
/// Represents block-wise quantization metadata (such as GGUF or INT4 group-quantized weight tensors).
/// </summary>
/// <param name="Scheme">The quantization scheme identifier (for example, <c>"Q4_0"</c>, <c>"Q4_K_M"</c>, or <c>"GPTQ"</c>).</param>
/// <param name="BlockSize">The number of elements grouped into a single quantization block.</param>
/// <param name="Bits">The effective number of bits per quantized weight element.</param>
public sealed record BlockQuantization(
    string Scheme,
    int BlockSize,
    int Bits) : QuantizationInfo;

