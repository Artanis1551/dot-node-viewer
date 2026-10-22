namespace DotNodeViewer.Core.Ir;

/// <summary>
/// Classifies the role of a <see cref="TensorEdge"/> within a <see cref="Subgraph"/>.
/// </summary>
public enum TensorKind
{
    /// <summary>
    /// An intermediate runtime activation tensor flowing between operator nodes.
    /// </summary>
    Activation,

    /// <summary>
    /// A static learned weight or parameter tensor (such as convolution kernels or linear weights).
    /// </summary>
    Initializer,

    /// <summary>
    /// A static constant tensor embedded in the graph (such as shape constants or scalar constants).
    /// </summary>
    Constant,

    /// <summary>
    /// An external runtime input tensor fed into the subgraph.
    /// </summary>
    GraphInput,

    /// <summary>
    /// An external runtime output tensor produced by the subgraph.
    /// </summary>
    GraphOutput,
}

/// <summary>
/// Describes the element data type, shape, quantization parameters, and memory layout of a tensor.
/// </summary>
/// <param name="DataType">One of the enumeration values that specifies the tensor's element data type.</param>
/// <param name="Shape">The tensor shape, or <see langword="null"/> if the tensor is unranked. A rank-0 scalar tensor has a non-null <see cref="TensorShape"/> with zero dimensions.</param>
/// <param name="Quantization">Optional quantization metadata attached to the tensor.</param>
/// <param name="Layout">An optional memory layout or dimension order annotation (for example, <c>"NCHW"</c>, <c>"NHWC"</c>, or <c>"0,1,2,3"</c>).</param>
public sealed record TensorDescriptor(
    DataType DataType,
    TensorShape? Shape,
    QuantizationInfo? Quantization = null,
    string? Layout = null);

/// <summary>
/// Represents a named tensor edge connecting operator ports or holding static weight metadata within a <see cref="Subgraph"/>.
/// </summary>
/// <param name="Name">The identifier or name of the tensor within the subgraph.</param>
/// <param name="Descriptor">The shape, data type, and quantization descriptor of the tensor.</param>
/// <param name="Kind">One of the enumeration values that specifies whether the tensor is an activation, initializer, constant, or graph boundary.</param>
/// <param name="Data">An optional lazy reference to the tensor's raw weight payload in the model stream or an external file.</param>
public sealed record TensorEdge(
    string Name,
    TensorDescriptor Descriptor,
    TensorKind Kind,
    TensorDataSource? Data = null);

