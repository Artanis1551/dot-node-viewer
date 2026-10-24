namespace DotNodeViewer.Layout;

/// <summary>
/// Represents a positioned input or output connection port on a <see cref="PositionedNode"/>.
/// </summary>
/// <param name="Id">The unique identifier of the port within the node.</param>
/// <param name="X">The absolute horizontal coordinate of the port anchor.</param>
/// <param name="Y">The absolute vertical coordinate of the port anchor.</param>
public sealed record PositionedPort(
    string Id,
    double X,
    double Y)
{
    /// <summary>
    /// Gets the port anchor coordinate as a <see cref="LayoutPoint"/>.
    /// </summary>
    public LayoutPoint Location => new(X, Y);
}

/// <summary>
/// Represents a positioned node produced by <see cref="SugiyamaLayoutEngine"/>.
/// </summary>
/// <param name="Id">The unique identifier of the node.</param>
/// <param name="X">The top-left horizontal coordinate of the node box.</param>
/// <param name="Y">The top-left vertical coordinate of the node box.</param>
/// <param name="Width">The width of the node box.</param>
/// <param name="Height">The height of the node box.</param>
/// <param name="Layer">The zero-based layer (rank) index assigned to the node.</param>
/// <param name="OrderInLayer">The zero-based horizontal/secondary ordering index within <paramref name="Layer"/>.</param>
/// <param name="InputPorts">The positioned input ports along the node's entry border.</param>
/// <param name="OutputPorts">The positioned output ports along the node's exit border.</param>
public sealed record PositionedNode(
    string Id,
    double X,
    double Y,
    double Width,
    double Height,
    int Layer,
    int OrderInLayer,
    IReadOnlyList<PositionedPort> InputPorts,
    IReadOnlyList<PositionedPort> OutputPorts)
{
    /// <summary>
    /// Gets the axis-aligned <see cref="BoundingBox"/> of this positioned node.
    /// </summary>
    public BoundingBox Bounds => new(X, Y, Width, Height);
}

/// <summary>
/// Represents a routed directed edge produced by <see cref="SugiyamaLayoutEngine"/>.
/// </summary>
/// <param name="Id">The unique identifier of the edge.</param>
/// <param name="SourceNodeId">The <see cref="PositionedNode.Id"/> of the source node.</param>
/// <param name="TargetNodeId">The <see cref="PositionedNode.Id"/> of the target node.</param>
/// <param name="SourcePortId">The optional port identifier on the source node.</param>
/// <param name="TargetPortId">The optional port identifier on the target node.</param>
/// <param name="IsReversed">
/// <see langword="true"/> if this edge was temporarily reversed during cycle removal to break a feedback cycle;
/// its <paramref name="Points"/> and <paramref name="BezierSegments"/> are always oriented from <paramref name="SourceNodeId"/> to <paramref name="TargetNodeId"/>.
/// </param>
/// <param name="Points">The polyline control points from the source port anchor to the target port anchor.</param>
/// <param name="BezierSegments">The smooth cubic Bézier segments connecting the source port anchor to the target port anchor.</param>
/// <param name="LabelAnchor">The midpoint anchor for rendering <paramref name="Label"/>, or <see langword="null"/> if unlabeled.</param>
/// <param name="Label">The optional label string associated with the edge.</param>
public sealed record PositionedEdge(
    string Id,
    string SourceNodeId,
    string TargetNodeId,
    string? SourcePortId,
    string? TargetPortId,
    bool IsReversed,
    IReadOnlyList<LayoutPoint> Points,
    IReadOnlyList<CubicBezierSegment> BezierSegments,
    LayoutPoint? LabelAnchor,
    string? Label)
{
    private BoundingBox? _bounds;

    /// <summary>
    /// Gets the axis-aligned <see cref="BoundingBox"/> enclosing all routed points of this edge.
    /// </summary>
    public BoundingBox Bounds => _bounds ??= ComputeBounds(Points);

    private static BoundingBox ComputeBounds(IReadOnlyList<LayoutPoint> points)
    {
        if (points.Count == 0)
        {
            return new BoundingBox(0, 0, 0, 0);
        }

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;

        for (int i = 0; i < points.Count; i++)
        {
            LayoutPoint p = points[i];
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        return new BoundingBox(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }
}

/// <summary>
/// Represents the complete positioned layered graph output produced by <see cref="SugiyamaLayoutEngine"/>.
/// </summary>
/// <param name="Nodes">The positioned nodes in declaration order.</param>
/// <param name="Edges">The routed edges in declaration order.</param>
/// <param name="Bounds">The overall bounding box enclosing all positioned nodes, routed edges, and outer margins.</param>
/// <param name="Direction">The primary flow direction used to compute the layout.</param>
public sealed record PositionedGraph(
    IReadOnlyList<PositionedNode> Nodes,
    IReadOnlyList<PositionedEdge> Edges,
    BoundingBox Bounds,
    LayoutDirection Direction);

