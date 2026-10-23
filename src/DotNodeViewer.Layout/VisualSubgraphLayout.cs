using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Layout;

/// <summary>
/// Classifies the visual role of a <see cref="VisualNode"/> within a <see cref="VisualSubgraphLayout"/>.
/// </summary>
public enum VisualNodeKind
{
    /// <summary>
    /// An operator or custom delegate card corresponding to a <see cref="GraphNode"/>.
    /// </summary>
    Operator,

    /// <summary>
    /// A compact terminal pill representing a <see cref="Subgraph.Inputs"/> boundary port.
    /// </summary>
    GraphInput,

    /// <summary>
    /// A compact terminal pill representing a <see cref="Subgraph.Outputs"/> boundary port.
    /// </summary>
    GraphOutput,
}

/// <summary>
/// Represents a single-consumer <see cref="TensorKind.Initializer"/> or <see cref="TensorKind.Constant"/> tensor
/// collapsed into a compact parameter row inside an operator <see cref="VisualNode"/>.
/// </summary>
/// <param name="PortName">The formal input port name on the operator node (for example, <c>"W"</c>, <c>"B"</c>, or <c>"filter"</c>).</param>
/// <param name="Tensor">The underlying <see cref="TensorEdge"/> holding the parameter's shape, data type, quantization, and lazy data slice.</param>
/// <param name="DisplaySummary">A formatted single-line summary string (for example, <c>"float32 [64, 3, 3, 3]"</c>).</param>
/// <param name="IsQuantized"><see langword="true"/> if <paramref name="Tensor"/> carries quantization metadata or a sub-byte/integer quantized type.</param>
public sealed record CollapsedParameterSlot(
    string PortName,
    TensorEdge Tensor,
    string DisplaySummary,
    bool IsQuantized);

/// <summary>
/// Represents a positioned visual node (operator card or input/output terminal pill) in a <see cref="VisualSubgraphLayout"/>.
/// </summary>
/// <param name="Id">The unique visual node identifier in the layout graph.</param>
/// <param name="Kind">One of the enumeration values that specifies whether this visual node is an operator card or a boundary terminal pill.</param>
/// <param name="GraphNode">The underlying <see cref="DotNodeViewer.Core.Ir.GraphNode"/> when <paramref name="Kind"/> is <see cref="VisualNodeKind.Operator"/>; otherwise, <see langword="null"/>.</param>
/// <param name="BoundaryPort">The underlying <see cref="GraphPort"/> when <paramref name="Kind"/> is <see cref="VisualNodeKind.GraphInput"/> or <see cref="VisualNodeKind.GraphOutput"/>; otherwise, <see langword="null"/>.</param>
/// <param name="Title">The primary header title (for example, <c>"Conv"</c>, <c>"CONV_2D"</c>, or the input/output tensor name).</param>
/// <param name="Subtitle">An optional secondary subtitle (such as the node name or terminal tensor shape summary).</param>
/// <param name="Category">The semantic <see cref="OperatorCategory"/> controlling the node header accent color.</param>
/// <param name="CollapsedParameters">The list of static weight/constant tensors collapsed into compact parameter rows inside this node box.</param>
/// <param name="FusedActivationBadge">An optional fused activation badge label (for example, <c>"RELU"</c> or <c>"RELU6"</c>).</param>
/// <param name="Layout">The computed position, dimensions, layer assignment, and port anchors for this visual node.</param>
public sealed record VisualNode(
    string Id,
    VisualNodeKind Kind,
    GraphNode? GraphNode,
    GraphPort? BoundaryPort,
    string Title,
    string? Subtitle,
    OperatorCategory Category,
    IReadOnlyList<CollapsedParameterSlot> CollapsedParameters,
    string? FusedActivationBadge,
    PositionedNode Layout)
{
    /// <summary>
    /// Gets the axis-aligned <see cref="BoundingBox"/> of this visual node.
    /// </summary>
    public BoundingBox Bounds => Layout.Bounds;
}

/// <summary>
/// Represents a routed tensor activation edge connecting two <see cref="VisualNode"/> instances in a <see cref="VisualSubgraphLayout"/>.
/// </summary>
/// <param name="Id">The unique visual edge identifier.</param>
/// <param name="Tensor">The underlying <see cref="TensorEdge"/> flowing along this connection.</param>
/// <param name="ShapeLabel">The formatted tensor shape/type callout label (for example, <c>"[batch, 64, 112, 112]"</c>).</param>
/// <param name="IsQuantized"><see langword="true"/> if <paramref name="Tensor"/> carries quantization parameters.</param>
/// <param name="Layout">The routed polyline points, cubic Bézier segments, and midpoint label anchor.</param>
public sealed record VisualEdge(
    string Id,
    TensorEdge Tensor,
    string ShapeLabel,
    bool IsQuantized,
    PositionedEdge Layout);

/// <summary>
/// Represents the complete positioned visual layout of a <see cref="DotNodeViewer.Core.Ir.Subgraph"/> produced by <see cref="ModelGraphLayoutAdapter"/>.
/// </summary>
/// <param name="Subgraph">The source <see cref="DotNodeViewer.Core.Ir.Subgraph"/> from <c>DotNodeViewer.Core</c>.</param>
/// <param name="PositionedGraph">The underlying <see cref="DotNodeViewer.Layout.PositionedGraph"/> computed by <see cref="SugiyamaLayoutEngine"/>.</param>
/// <param name="Nodes">The ordered list of positioned visual nodes (input pills, operator cards, and output pills).</param>
/// <param name="Edges">The ordered list of routed visual tensor edges.</param>
/// <param name="NodesById">A fast lookup dictionary mapping <see cref="VisualNode.Id"/> to its <see cref="VisualNode"/>.</param>
public sealed record VisualSubgraphLayout(
    Subgraph Subgraph,
    PositionedGraph PositionedGraph,
    IReadOnlyList<VisualNode> Nodes,
    IReadOnlyList<VisualEdge> Edges,
    IReadOnlyDictionary<string, VisualNode> NodesById)
{
    /// <summary>
    /// Gets the overall bounding box of the positioned subgraph layout.
    /// </summary>
    public BoundingBox Bounds => PositionedGraph.Bounds;
}
