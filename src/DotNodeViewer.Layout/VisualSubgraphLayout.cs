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
    PositionedEdge Layout)
{
    /// <summary>
    /// Gets the axis-aligned <see cref="BoundingBox"/> enclosing all routed points of this visual edge.
    /// </summary>
    public BoundingBox Bounds => Layout.Bounds;
}

/// <summary>
/// Represents the culled visual nodes and routed edges intersecting a visible layout region.
/// </summary>
/// <param name="Nodes">The visual nodes visible within the query viewport.</param>
/// <param name="Edges">The routed visual edges visible within the query viewport.</param>
public readonly record struct VisibleLayoutElements(
    IReadOnlyList<VisualNode> Nodes,
    IReadOnlyList<VisualEdge> Edges);

/// <summary>
/// Represents the complete positioned visual layout of a <see cref="DotNodeViewer.Core.Ir.Subgraph"/> produced by <see cref="ModelGraphLayoutAdapter"/>,
/// equipped with an embedded 2D spatial hash grid, precomputed edge bounds, viewport culling, and 1-hop adjacency queries.
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
    private readonly SpatialGridIndex _spatialIndex = new(Nodes, Edges);
    private readonly Dictionary<string, VisualEdge> _edgesById = new(Edges.Count, StringComparer.Ordinal);
    private readonly Dictionary<string, List<VisualEdge>> _connectedEdgesByNodeId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<VisualEdge>> _incomingEdgesByNodeId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<VisualEdge>> _outgoingEdgesByNodeId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<VisualNode>> _adjacentNodesByNodeId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<VisualNode>> _predecessorsByNodeId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<VisualNode>> _successorsByNodeId = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the overall bounding box of the positioned subgraph layout.
    /// </summary>
    public BoundingBox Bounds => PositionedGraph.Bounds;

    /// <summary>
    /// Gets a fast lookup dictionary mapping <see cref="VisualEdge.Id"/> to its <see cref="VisualEdge"/>.
    /// </summary>
    public IReadOnlyDictionary<string, VisualEdge> EdgesById => _edgesById;

    // Initialization logic for indices
    private void InitializeIndices()
    {
        for (int i = 0; i < Edges.Count; i++)
        {
            VisualEdge edge = Edges[i];
            _edgesById.TryAdd(edge.Id, edge);

            string src = edge.Layout.SourceNodeId;
            string tgt = edge.Layout.TargetNodeId;

            AddConnectedEdge(src, edge);
            AddConnectedEdge(tgt, edge);

            AddIncomingEdge(tgt, edge);
            AddOutgoingEdge(src, edge);
        }

        foreach (var (tgtId, inEdges) in _incomingEdgesByNodeId)
        {
            List<VisualNode> preds = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            for (int i = 0; i < inEdges.Count; i++)
            {
                string srcId = inEdges[i].Layout.SourceNodeId;
                if (seen.Add(srcId) && NodesById.TryGetValue(srcId, out VisualNode? srcNode))
                {
                    preds.Add(srcNode);
                }
            }

            _predecessorsByNodeId[tgtId] = preds;
        }

        foreach (var (srcId, outEdges) in _outgoingEdgesByNodeId)
        {
            List<VisualNode> succs = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            for (int i = 0; i < outEdges.Count; i++)
            {
                string tgtId = outEdges[i].Layout.TargetNodeId;
                if (seen.Add(tgtId) && NodesById.TryGetValue(tgtId, out VisualNode? tgtNode))
                {
                    succs.Add(tgtNode);
                }
            }

            _successorsByNodeId[srcId] = succs;
        }

        for (int i = 0; i < Nodes.Count; i++)
        {
            string id = Nodes[i].Id;
            List<VisualNode> adj = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            if (_predecessorsByNodeId.TryGetValue(id, out var preds))
            {
                for (int p = 0; p < preds.Count; p++)
                {
                    if (seen.Add(preds[p].Id))
                    {
                        adj.Add(preds[p]);
                    }
                }
            }

            if (_successorsByNodeId.TryGetValue(id, out var succs))
            {
                for (int s = 0; s < succs.Count; s++)
                {
                    if (seen.Add(succs[s].Id))
                    {
                        adj.Add(succs[s]);
                    }
                }
            }

            _adjacentNodesByNodeId[id] = adj;
        }
    }

    private void AddConnectedEdge(string nodeId, VisualEdge edge)
    {
        if (!_connectedEdgesByNodeId.TryGetValue(nodeId, out List<VisualEdge>? list))
        {
            list = [];
            _connectedEdgesByNodeId[nodeId] = list;
        }

        list.Add(edge);
    }

    private void AddIncomingEdge(string targetNodeId, VisualEdge edge)
    {
        if (!_incomingEdgesByNodeId.TryGetValue(targetNodeId, out List<VisualEdge>? list))
        {
            list = [];
            _incomingEdgesByNodeId[targetNodeId] = list;
        }

        list.Add(edge);
    }

    private void AddOutgoingEdge(string sourceNodeId, VisualEdge edge)
    {
        if (!_outgoingEdgesByNodeId.TryGetValue(sourceNodeId, out List<VisualEdge>? list))
        {
            list = [];
            _outgoingEdgesByNodeId[sourceNodeId] = list;
        }

        list.Add(edge);
    }

    /// <summary>
    /// Queries the spatial grid for a <see cref="VisualNode"/> whose bounding box contains the given world coordinates.
    /// </summary>
    /// <param name="worldX">The horizontal coordinate in layout space.</param>
    /// <param name="worldY">The vertical coordinate in layout space.</param>
    /// <returns>The matching <see cref="VisualNode"/> if found; otherwise, <see langword="null"/>.</returns>
    public VisualNode? FindNodeAt(double worldX, double worldY) =>
        _spatialIndex.FindNodeAt(worldX, worldY);

    /// <summary>
    /// Queries the spatial grid for a <see cref="VisualNode"/> whose bounding box contains the given layout point.
    /// </summary>
    /// <param name="point">The layout coordinate point.</param>
    /// <returns>The matching <see cref="VisualNode"/> if found; otherwise, <see langword="null"/>.</returns>
    public VisualNode? FindNodeAt(LayoutPoint point) =>
        _spatialIndex.FindNodeAt(point.X, point.Y);

    /// <summary>
    /// Queries the spatial grid for all nodes whose bounding boxes intersect the specified rectangular region.
    /// </summary>
    /// <param name="worldX">The left coordinate of the region.</param>
    /// <param name="worldY">The top coordinate of the region.</param>
    /// <param name="worldWidth">The width of the region.</param>
    /// <param name="worldHeight">The height of the region.</param>
    /// <returns>A list of unique intersecting <see cref="VisualNode"/> instances.</returns>
    public IReadOnlyList<VisualNode> FindNodesInRegion(double worldX, double worldY, double worldWidth, double worldHeight) =>
        _spatialIndex.FindNodesInRegion(worldX, worldY, worldWidth, worldHeight);

    /// <summary>
    /// Queries the spatial grid for all nodes whose bounding boxes intersect the specified bounding box.
    /// </summary>
    /// <param name="region">The query bounding box in layout space.</param>
    /// <returns>A list of unique intersecting <see cref="VisualNode"/> instances.</returns>
    public IReadOnlyList<VisualNode> FindNodesInRegion(BoundingBox region) =>
        _spatialIndex.FindNodesInRegion(region);

    /// <summary>
    /// Queries the spatial grid for all routed edges whose bounding boxes intersect the specified bounding box.
    /// </summary>
    /// <param name="region">The query bounding box in layout space.</param>
    /// <returns>A list of unique intersecting <see cref="VisualEdge"/> instances.</returns>
    public IReadOnlyList<VisualEdge> FindEdgesInRegion(BoundingBox region) =>
        _spatialIndex.FindEdgesInRegion(region);

    /// <summary>
    /// Queries the spatial grid for all visual nodes and routed edges intersecting the specified viewport region.
    /// </summary>
    /// <param name="viewport">The visible viewport bounding box in layout space.</param>
    /// <returns>A <see cref="VisibleLayoutElements"/> containing both visible nodes and edges.</returns>
    public VisibleLayoutElements GetVisibleElements(BoundingBox viewport) =>
        new(_spatialIndex.FindNodesInRegion(viewport), _spatialIndex.FindEdgesInRegion(viewport));

    /// <summary>
    /// Gets all routed visual edges directly connected to the specified node (either as source or target).
    /// </summary>
    /// <param name="nodeId">The unique visual node identifier.</param>
    /// <returns>A read-only list of connected visual edges, or an empty list if no edges connect to the node.</returns>
    public IReadOnlyList<VisualEdge> GetConnectedEdges(string nodeId)
    {
        EnsureIndicesInitialized();
        return _connectedEdgesByNodeId.TryGetValue(nodeId, out var list) ? list : [];
    }

    /// <summary>
    /// Gets all routed visual edges directed into the specified node (incoming edges where <see cref="PositionedEdge.TargetNodeId"/> matches <paramref name="nodeId"/>).
    /// </summary>
    /// <param name="nodeId">The unique visual node identifier.</param>
    /// <returns>A read-only list of incoming visual edges, or an empty list if none exist.</returns>
    public IReadOnlyList<VisualEdge> GetIncomingEdges(string nodeId)
    {
        EnsureIndicesInitialized();
        return _incomingEdgesByNodeId.TryGetValue(nodeId, out var list) ? list : [];
    }

    /// <summary>
    /// Gets all routed visual edges exiting the specified node (outgoing edges where <see cref="PositionedEdge.SourceNodeId"/> matches <paramref name="nodeId"/>).
    /// </summary>
    /// <param name="nodeId">The unique visual node identifier.</param>
    /// <returns>A read-only list of outgoing visual edges, or an empty list if none exist.</returns>
    public IReadOnlyList<VisualEdge> GetOutgoingEdges(string nodeId)
    {
        EnsureIndicesInitialized();
        return _outgoingEdgesByNodeId.TryGetValue(nodeId, out var list) ? list : [];
    }

    /// <summary>
    /// Gets all immediate neighbor nodes (both 1-hop upstream predecessors and 1-hop downstream successors) connected to <paramref name="nodeId"/>.
    /// </summary>
    /// <param name="nodeId">The unique visual node identifier.</param>
    /// <returns>A read-only list of unique adjacent visual nodes.</returns>
    public IReadOnlyList<VisualNode> GetAdjacentNodes(string nodeId)
    {
        EnsureIndicesInitialized();
        return _adjacentNodesByNodeId.TryGetValue(nodeId, out var list) ? list : [];
    }

    /// <summary>
    /// Gets all immediate 1-hop upstream predecessor nodes that produce input activations for <paramref name="nodeId"/>.
    /// </summary>
    /// <param name="nodeId">The unique visual node identifier.</param>
    /// <returns>A read-only list of unique upstream predecessor visual nodes.</returns>
    public IReadOnlyList<VisualNode> GetPredecessors(string nodeId)
    {
        EnsureIndicesInitialized();
        return _predecessorsByNodeId.TryGetValue(nodeId, out var list) ? list : [];
    }

    /// <summary>
    /// Gets all immediate 1-hop downstream successor nodes that consume output activations from <paramref name="nodeId"/>.
    /// </summary>
    /// <param name="nodeId">The unique visual node identifier.</param>
    /// <returns>A read-only list of unique downstream successor visual nodes.</returns>
    public IReadOnlyList<VisualNode> GetSuccessors(string nodeId)
    {
        EnsureIndicesInitialized();
        return _successorsByNodeId.TryGetValue(nodeId, out var list) ? list : [];
    }

    private bool _indicesInitialized;
    private void EnsureIndicesInitialized()
    {
        if (!_indicesInitialized)
        {
            InitializeIndices();
            _indicesInitialized = true;
        }
    }
}
