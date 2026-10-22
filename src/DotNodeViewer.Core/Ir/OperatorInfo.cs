namespace DotNodeViewer.Core.Ir;

/// <summary>
/// Classifies a graph operator into a format-agnostic semantic category for visual styling and filtering.
/// </summary>
public enum OperatorCategory
{
    /// <summary>
    /// Represents a core trainable or compute layer such as convolution, matrix multiplication, linear, or recurrent cells.
    /// </summary>
    Layer,

    /// <summary>
    /// Represents a non-linear activation function such as ReLU, Sigmoid, Tanh, GELU, or Softmax.
    /// </summary>
    Activation,

    /// <summary>
    /// Represents a normalization operation such as BatchNormalization, LayerNormalization, or InstanceNormalization.
    /// </summary>
    Normalization,

    /// <summary>
    /// Represents a spatial or temporal pooling operation such as MaxPool, AveragePool, or GlobalAveragePool.
    /// </summary>
    Pooling,

    /// <summary>
    /// Represents a quantization, dequantization, or fake-quantization operation.
    /// </summary>
    Quantization,

    /// <summary>
    /// Represents a shape or view manipulation operation such as Reshape, Transpose, Squeeze, Unsqueeze, Slice, or Concat.
    /// </summary>
    Shape,

    /// <summary>
    /// Represents an element-wise tensor arithmetic, reduction, or casting operation such as Add, Mul, Sub, Div, Cast, or ReduceMean.
    /// </summary>
    Tensor,

    /// <summary>
    /// Represents a constant value or constant fill operator.
    /// </summary>
    Constant,

    /// <summary>
    /// Represents a control-flow operator such as If, Loop, While, Scan, or Call.
    /// </summary>
    ControlFlow,

    /// <summary>
    /// Represents a data movement, queue, or I/O pipeline operator.
    /// </summary>
    Data,

    /// <summary>
    /// Represents a custom operator or lowered hardware-accelerator delegate block (such as XNNPACK, CoreML, or Ethos-U).
    /// </summary>
    Custom,

    /// <summary>
    /// Represents an operator whose semantic category is unknown or uncategorized.
    /// </summary>
    Unknown,
}

/// <summary>
/// Describes the operator type, semantic category, domain, and version of a <see cref="GraphNode"/>.
/// </summary>
/// <param name="Name">The format-specific operator name (for example, <c>"Conv"</c>, <c>"CONV_2D"</c>, or <c>"aten::conv2d.out"</c>).</param>
/// <param name="Category">One of the enumeration values that specifies the semantic operator category.</param>
/// <param name="Domain">An optional operator domain or namespace (for example, <c>"ai.onnx"</c> or <c>"aten"</c>).</param>
/// <param name="Version">An optional opset or operator schema version number.</param>
public sealed record OperatorInfo(
    string Name,
    OperatorCategory Category,
    string? Domain = null,
    int? Version = null);

