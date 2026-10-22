using System.Diagnostics.CodeAnalysis;

namespace DotNodeViewer.Core.Ir;

/// <summary>
/// Represents a named attribute key-value pair attached to a <see cref="GraphNode"/>.
/// </summary>
/// <param name="Name">The attribute name (for example, <c>"strides"</c>, <c>"pads"</c>, or <c>"dilations"</c>).</param>
/// <param name="Value">The strongly typed attribute value.</param>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "NodeAttribute represents a graph node attribute in the frozen IR contract, not a .NET custom attribute.")]
public sealed record NodeAttribute(
    string Name,
    AttributeValue Value);

/// <summary>
/// Represents a strongly typed value of a <see cref="NodeAttribute"/> without boxing primitives into <see cref="object"/>.
/// </summary>
/// <remarks>
/// <see cref="AttributeValue"/> is a closed discriminated-union hierarchy supporting scalar primitives, homogeneous lists,
/// tensor descriptors, nested control-flow subgraphs, and lazy binary payload slices.
/// </remarks>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Discriminated union case names in the frozen IR contract intentionally name their underlying value type.")]
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "String is a sealed nested record in the frozen IR contract.")]
public abstract record AttributeValue
{
    private AttributeValue()
    {
    }

    /// <summary>
    /// Represents a 64-bit signed integer attribute value.
    /// </summary>
    /// <param name="Value">The integer value.</param>
    public sealed record Int64(long Value) : AttributeValue;

    /// <summary>
    /// Represents a 64-bit floating-point attribute value.
    /// </summary>
    /// <param name="Value">The floating-point value.</param>
    public sealed record Float64(double Value) : AttributeValue;

    /// <summary>
    /// Represents a boolean attribute value.
    /// </summary>
    /// <param name="Value"><see langword="true"/> or <see langword="false"/>.</param>
    public sealed record Bool(bool Value) : AttributeValue;

    /// <summary>
    /// Represents a UTF-8 string attribute value.
    /// </summary>
    /// <param name="Value">The string value.</param>
    public sealed record String(string Value) : AttributeValue;

    /// <summary>
    /// Represents a list of 64-bit signed integer attribute values (for example, kernel sizes, strides, or padding).
    /// </summary>
    /// <param name="Values">The ordered list of integer values.</param>
    public sealed record Int64List(IReadOnlyList<long> Values) : AttributeValue;

    /// <summary>
    /// Represents a list of 64-bit floating-point attribute values.
    /// </summary>
    /// <param name="Values">The ordered list of floating-point values.</param>
    public sealed record Float64List(IReadOnlyList<double> Values) : AttributeValue;

    /// <summary>
    /// Represents a list of string attribute values.
    /// </summary>
    /// <param name="Values">The ordered list of string values.</param>
    public sealed record StringList(IReadOnlyList<string> Values) : AttributeValue;

    /// <summary>
    /// Represents a <see cref="DataType"/> attribute value (for example, the target type of a <c>Cast</c> operator).
    /// </summary>
    /// <param name="Value">One of the enumeration values that specifies the target data type.</param>
    public sealed record DataTypeValue(DataType Value) : AttributeValue;

    /// <summary>
    /// Represents a <see cref="TensorShape"/> attribute value.
    /// </summary>
    /// <param name="Value">The tensor shape value.</param>
    public sealed record ShapeValue(TensorShape Value) : AttributeValue;

    /// <summary>
    /// Represents a <see cref="TensorEdge"/> attribute value (for example, the value tensor of a <c>Constant</c> operator).
    /// </summary>
    /// <param name="Value">The embedded tensor edge.</param>
    public sealed record Tensor(TensorEdge Value) : AttributeValue;

    /// <summary>
    /// Represents a nested <see cref="Subgraph"/> attribute value (for example, an <c>If</c> branch or <c>Loop</c> body).
    /// </summary>
    /// <param name="Value">The nested subgraph.</param>
    public sealed record Graph(Subgraph Value) : AttributeValue;

    /// <summary>
    /// Represents a list of nested <see cref="Subgraph"/> attribute values.
    /// </summary>
    /// <param name="Values">The ordered list of nested subgraphs.</param>
    public sealed record GraphList(IReadOnlyList<Subgraph> Values) : AttributeValue;

    /// <summary>
    /// Represents a lazy byte slice for an opaque binary attribute payload (such as a custom NPU delegate command stream).
    /// </summary>
    /// <param name="Slice">The lazy byte slice within the model stream.</param>
    public sealed record Bytes(TensorDataSlice Slice) : AttributeValue;
}
