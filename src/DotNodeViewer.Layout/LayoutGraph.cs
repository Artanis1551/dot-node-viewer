namespace DotNodeViewer.Layout;

/// <summary>
/// Represents an input or output connection port on a <see cref="LayoutNode"/>.
/// </summary>
/// <param name="Id">The unique identifier of the port within the node.</param>
/// <param name="RelativeOffset">
/// An optional normalized offset in <c>[0.0, 1.0]</c> along the node's port border,
/// or <see cref="double.NaN"/> to distribute ports evenly in declaration order.
/// </param>
public sealed record LayoutPort(
    string Id,
    double RelativeOffset = double.NaN);

/// <summary>
/// Represents an unpositioned node with measured dimensions and connection ports in a <see cref="LayoutGraph"/>.
/// </summary>
/// <param name="Id">The unique identifier of the node within the graph.</param>
/// <param name="Width">The measured width of the node box.</param>
/// <param name="Height">The measured height of the node box.</param>
/// <param name="InputPorts">The ordered list of input ports on the node.</param>
/// <param name="OutputPorts">The ordered list of output ports on the node.</param>
public sealed record LayoutNode(
    string Id,
    double Width,
    double Height,
    IReadOnlyList<LayoutPort>? InputPorts = null,
    IReadOnlyList<LayoutPort>? OutputPorts = null);

/// <summary>
/// Represents a directed edge connecting a source node/port to a target node/port in a <see cref="LayoutGraph"/>.
/// </summary>
/// <param name="Id">The unique identifier of the edge.</param>
/// <param name="SourceNodeId">The <see cref="LayoutNode.Id"/> of the producer node.</param>
/// <param name="TargetNodeId">The <see cref="LayoutNode.Id"/> of the consumer node.</param>
/// <param name="SourcePortId">The optional <see cref="LayoutPort.Id"/> on the source node.</param>
/// <param name="TargetPortId">The optional <see cref="LayoutPort.Id"/> on the target node.</param>
/// <param name="Label">An optional label (such as a tensor shape string) displayed along the edge.</param>
/// <param name="LabelWidth">The optional measured width of <paramref name="Label"/>.</param>
/// <param name="LabelHeight">The optional measured height of <paramref name="Label"/>.</param>
public sealed record LayoutEdge(
    string Id,
    string SourceNodeId,
    string TargetNodeId,
    string? SourcePortId = null,
    string? TargetPortId = null,
    string? Label = null,
    double LabelWidth = 0,
    double LabelHeight = 0);

/// <summary>
/// Represents an unpositioned directed graph input to <see cref="SugiyamaLayoutEngine"/>.
/// </summary>
/// <param name="Nodes">The ordered list of nodes to position.</param>
/// <param name="Edges">The ordered list of directed edges connecting <paramref name="Nodes"/>.</param>
public sealed record LayoutGraph(
    IReadOnlyList<LayoutNode> Nodes,
    IReadOnlyList<LayoutEdge> Edges);

