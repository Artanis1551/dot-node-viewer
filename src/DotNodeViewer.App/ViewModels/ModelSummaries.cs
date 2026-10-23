using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.App.ViewModels;

/// <summary>
/// Represents a displayable summary of a model input or output tensor.
/// </summary>
public sealed class TensorSummaryItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TensorSummaryItem"/> class.
    /// </summary>
    /// <param name="name">The tensor or port identifier name.</param>
    /// <param name="shapeText">The formatted shape string (e.g., <c>[1, 3, 224, 224]</c>).</param>
    /// <param name="dataTypeText">The formatted element data type.</param>
    /// <param name="quantizationBadge">The short quantization badge label (e.g., <c>INT8</c>, <c>FP32</c>).</param>
    /// <param name="quantizationBrushKey">The resource brush key for the badge background.</param>
    /// <param name="quantizationTextBrushKey">The resource brush key for the badge text.</param>
    /// <param name="isQuantized">Indicates whether the tensor is quantized.</param>
    /// <param name="details">Optional quantization details, such as scale and zero-point parameters.</param>
    public TensorSummaryItem(
        string name,
        string shapeText,
        string dataTypeText,
        string quantizationBadge,
        string quantizationBrushKey,
        string quantizationTextBrushKey,
        bool isQuantized,
        string? details = null)
    {
        Name = name;
        ShapeText = shapeText;
        DataTypeText = dataTypeText;
        QuantizationBadge = quantizationBadge;
        QuantizationBrushKey = quantizationBrushKey;
        QuantizationTextBrushKey = quantizationTextBrushKey;
        IsQuantized = isQuantized;
        Details = details;
    }

    /// <summary>
    /// Gets the tensor or port identifier name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the formatted shape string (e.g., <c>[1, 3, 224, 224]</c>).
    /// </summary>
    public string ShapeText { get; }

    /// <summary>
    /// Gets the formatted element data type.
    /// </summary>
    public string DataTypeText { get; }

    /// <summary>
    /// Gets the short quantization badge label.
    /// </summary>
    public string QuantizationBadge { get; }

    /// <summary>
    /// Gets the resource brush key for the badge background.
    /// </summary>
    public string QuantizationBrushKey { get; }

    /// <summary>
    /// Gets the resource brush key for the badge text.
    /// </summary>
    public string QuantizationTextBrushKey { get; }

    /// <summary>
    /// Gets a value indicating whether the tensor is quantized.
    /// </summary>
    public bool IsQuantized { get; }

    /// <summary>
    /// Gets optional quantization details, such as scale and zero-point parameters.
    /// </summary>
    public string? Details { get; }
}

/// <summary>
/// Represents an aggregated summary of operator categories within a model or subgraph.
/// </summary>
public sealed class OperatorCategorySummaryItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OperatorCategorySummaryItem"/> class.
    /// </summary>
    /// <param name="category">The operator category enum value.</param>
    /// <param name="name">The friendly display name of the category.</param>
    /// <param name="count">The number of operators belonging to this category.</param>
    /// <param name="categoryBrushKey">The resource brush key for the category accent.</param>
    /// <param name="categorySubtleBrushKey">The resource brush key for the category background.</param>
    public OperatorCategorySummaryItem(
        OperatorCategory category,
        string name,
        int count,
        string categoryBrushKey,
        string categorySubtleBrushKey)
    {
        Category = category;
        Name = name;
        Count = count;
        CategoryBrushKey = categoryBrushKey;
        CategorySubtleBrushKey = categorySubtleBrushKey;
    }

    /// <summary>
    /// Gets the operator category enum value.
    /// </summary>
    public OperatorCategory Category { get; }

    /// <summary>
    /// Gets the friendly display name of the category.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the number of operators belonging to this category.
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Gets the resource brush key for the category accent.
    /// </summary>
    public string CategoryBrushKey { get; }

    /// <summary>
    /// Gets the resource brush key for the category background.
    /// </summary>
    public string CategorySubtleBrushKey { get; }
}

/// <summary>
/// Represents a typed operator attribute item displayed in the Node Inspector properties sidebar.
/// </summary>
public sealed class NodeAttributeItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NodeAttributeItem"/> class.
    /// </summary>
    /// <param name="name">The attribute name.</param>
    /// <param name="typeName">The type name of the attribute value.</param>
    /// <param name="valueText">The formatted string representation of the attribute value.</param>
    public NodeAttributeItem(string name, string typeName, string valueText)
    {
        Name = name;
        TypeName = typeName;
        ValueText = valueText;
    }

    /// <summary>
    /// Gets the attribute name (e.g. <c>kernel_shape</c>, <c>strides</c>, <c>pads</c>).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the type name (e.g. <c>Int64List</c>, <c>Float64</c>, <c>String</c>).
    /// </summary>
    public string TypeName { get; }

    /// <summary>
    /// Gets the formatted string representation of the attribute value.
    /// </summary>
    public string ValueText { get; }
}

/// <summary>
/// Represents an input or output port connection item displayed in the Node Inspector properties sidebar.
/// </summary>
public sealed class NodePortItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NodePortItem"/> class.
    /// </summary>
    /// <param name="portName">The formal port name (e.g., <c>"X"</c>, <c>"W"</c>, or <c>"Y"</c>).</param>
    /// <param name="tensorName">The connected tensor identifier name.</param>
    /// <param name="kindBadge">A badge indicating whether this port connects to an activation or static initializer.</param>
    /// <param name="shapeText">The formatted shape string (e.g., <c>[1, 3, 224, 224]</c>).</param>
    /// <param name="dataTypeText">The element data type name.</param>
    /// <param name="quantizationBadge">The quantization badge label (e.g. <c>INT8</c>, <c>FP32</c>).</param>
    /// <param name="quantizationBrushKey">The resource brush key for the badge background.</param>
    /// <param name="quantizationTextBrushKey">The resource brush key for the badge text.</param>
    /// <param name="isQuantized">Indicates whether this port's tensor carries quantization metadata.</param>
    /// <param name="details">Optional quantization details such as scale and zero-point parameters.</param>
    public NodePortItem(
        string portName,
        string tensorName,
        string kindBadge,
        string shapeText,
        string dataTypeText,
        string quantizationBadge,
        string quantizationBrushKey,
        string quantizationTextBrushKey,
        bool isQuantized,
        string? details = null)
    {
        PortName = portName;
        TensorName = tensorName;
        KindBadge = kindBadge;
        ShapeText = shapeText;
        DataTypeText = dataTypeText;
        QuantizationBadge = quantizationBadge;
        QuantizationBrushKey = quantizationBrushKey;
        QuantizationTextBrushKey = quantizationTextBrushKey;
        IsQuantized = isQuantized;
        Details = details;
    }

    /// <summary>
    /// Gets the formal port name (e.g., <c>"X"</c>, <c>"W"</c>, <c>"B"</c>, or <c>"0"</c>).
    /// </summary>
    public string PortName { get; }

    /// <summary>
    /// Gets the connected tensor identifier name.
    /// </summary>
    public string TensorName { get; }

    /// <summary>
    /// Gets a badge string classifying the port role (for example, <c>"Activation"</c> or <c>"Weight / Parameter"</c>).
    /// </summary>
    public string KindBadge { get; }

    /// <summary>
    /// Gets the formatted shape string (e.g. <c>[64, 3, 3, 3]</c>).
    /// </summary>
    public string ShapeText { get; }

    /// <summary>
    /// Gets the element data type name.
    /// </summary>
    public string DataTypeText { get; }

    /// <summary>
    /// Gets the quantization badge label.
    /// </summary>
    public string QuantizationBadge { get; }

    /// <summary>
    /// Gets the resource brush key for the badge background.
    /// </summary>
    public string QuantizationBrushKey { get; }

    /// <summary>
    /// Gets the resource brush key for the badge text.
    /// </summary>
    public string QuantizationTextBrushKey { get; }

    /// <summary>
    /// Gets a value indicating whether the tensor is quantized.
    /// </summary>
    public bool IsQuantized { get; }

    /// <summary>
    /// Gets optional quantization details such as scale and zero-point parameters.
    /// </summary>
    public string? Details { get; }
}

