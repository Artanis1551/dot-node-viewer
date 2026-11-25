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
    /// Gets the axis-aligned <see cref="BoundingBox"/> enclosing all routed points, Bézier control points, and label anchor of this visual edge.
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
/// equipped with an embedded 2D spatial hash grid, precomputed edge bounds, viewport culling, and thread-safe 1-hop adjacency queries.
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
    private readonly AdjacencyIndex _adjacency = new(Nodes, Edges, NodesById);

    /// <summary>
    /// Gets the overall bounding box of the positioned subgraph layout.
    /// </summary>
    public BoundingBox Bounds => PositionedGraph.Bounds;

    /// <summary>
    /// Gets a fast lookup dictionary mapping <see cref="VisualEdge.Id"/> to its <see cref="VisualEdge"/>.
    /// </summary>
    public IReadOnlyDictionary<string, VisualEdge> EdgesById => _adjacency.EdgesById;

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
    /// Populates <paramref name="destination"/> with all unique nodes whose bounding boxes intersect <paramref name="region"/>
    /// without allocating intermediate collections.
    /// </summary>
    /// <param name="region">The query bounding box in layout space.</param>
    /// <param name="destination">The caller-owned list cleared and populated with matching <see cref="VisualNode"/> instances.</param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    public void FindNodesInRegion(BoundingBox region, List<VisualNode> destination) =>
        _spatialIndex.FindNodesInRegion(region, destination);

    /// <summary>
    /// Queries the spatial grid for all routed edges whose bounding boxes intersect the specified bounding box.
    /// </summary>
    /// <param name="region">The query bounding box in layout space.</param>
    /// <returns>A list of unique intersecting <see cref="VisualEdge"/> instances.</returns>
    public IReadOnlyList<VisualEdge> FindEdgesInRegion(BoundingBox region) =>
        _spatialIndex.FindEdgesInRegion(region);

    /// <summary>
    /// Populates <paramref name="destination"/> with all unique routed edges whose bounding boxes intersect or touch <paramref name="region"/>
    /// without allocating intermediate collections.
    /// </summary>
    /// <param name="region">The query bounding box in layout space.</param>
    /// <param name="destination">The caller-owned list cleared and populated with matching <see cref="VisualEdge"/> instances.</param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    public void FindEdgesInRegion(BoundingBox region, List<VisualEdge> destination) =>
        _spatialIndex.FindEdgesInRegion(region, destination);

    /// <summary>
    /// Queries the spatial grid for all visual nodes and routed edges intersecting the specified viewport region.
    /// </summary>
    /// <param name="viewport">The visible viewport bounding box in layout space.</param>
    /// <returns>A <see cref="VisibleLayoutElements"/> containing both visible nodes and edges.</returns>
    public VisibleLayoutElements GetVisibleElements(BoundingBox viewport) =>
        new(_spatialIndex.FindNodesInRegion(viewport), _spatialIndex.FindEdgesInRegion(viewport));

    /// <summary>
    /// Populates <paramref name="visibleNodes"/> and <paramref name="visibleEdges"/> with all visual nodes and routed edges
    /// intersecting <paramref name="viewport"/> without allocating new result collections.
    /// </summary>
    /// <param name="viewport">The visible viewport bounding box in layout space.</param>
    /// <param name="visibleNodes">The caller-owned list cleared and populated with visible <see cref="VisualNode"/> instances.</param>
    /// <param name="visibleEdges">The caller-owned list cleared and populated with visible <see cref="VisualEdge"/> instances.</param>
    /// <exception cref="ArgumentNullException"><paramref name="visibleNodes"/> or <paramref name="visibleEdges"/> is <see langword="null"/>.</exception>
    public void GetVisibleElements(BoundingBox viewport, List<VisualNode> visibleNodes, List<VisualEdge> visibleEdges) =>
        GetVisibleElements(viewport, viewport, visibleNodes, visibleEdges);

    /// <summary>
    /// Populates <paramref name="visibleNodes"/> and <paramref name="visibleEdges"/> using separate node and edge cull regions
    /// (for example, an expanded edge region for arrowheads and shape labels) without allocating new result collections.
    /// </summary>
    /// <param name="nodeViewport">The visible bounding box for node culling in layout space.</param>
    /// <param name="edgeViewport">The visible bounding box for edge culling in layout space.</param>
    /// <param name="visibleNodes">The caller-owned list cleared and populated with visible <see cref="VisualNode"/> instances.</param>
    /// <param name="visibleEdges">The caller-owned list cleared and populated with visible <see cref="VisualEdge"/> instances.</param>
    /// <exception cref="ArgumentNullException"><paramref name="visibleNodes"/> or <paramref name="visibleEdges"/> is <see langword="null"/>.</exception>
    public void GetVisibleElements(
        BoundingBox nodeViewport,
        BoundingBox edgeViewport,
        List<VisualNode> visibleNodes,
        List<VisualEdge> visibleEdges)
    {
        ArgumentNullException.ThrowIfNull(visibleNodes);
        ArgumentNullException.ThrowIfNull(visibleEdges);

        _spatialIndex.FindNodesInRegion(nodeViewport, visibleNodes);
        _spatialIndex.FindEdgesInRegion(edgeViewport, visibleEdges);
    }

    /// <summary>
    /// Gets all routed visual edges directly connected to the specified node (either as source or target).
    /// </summary>
    /// <param name="nodeId">The unique visual node identifier.</param>
    /// <returns>A read-only list of connected visual edges, or an empty list if no edges connect to the node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nodeId"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<VisualEdge> GetConnectedEdges(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        return _adjacency.ConnectedEdgesByNodeId.TryGetValue(nodeId, out VisualEdge[]? edges) ? edges : [];
    }

    /// <summary>
    /// Gets all routed visual edges directed into the specified node (incoming edges where <see cref="PositionedEdge.TargetNodeId"/> matches <paramref name="nodeId"/>).
    /// </summary>
    /// <param name="nodeId">The unique visual node identifier.</param>
    /// <returns>A read-only list of incoming visual edges, or an empty list if none exist.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nodeId"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<VisualEdge> GetIncomingEdges(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        return _adjacency.IncomingEdgesByNodeId.TryGetValue(nodeId, out VisualEdge[]? edges) ? edges : [];
    }

    /// <summary>
    /// Gets all routed visual edges exiting the specified node (outgoing edges where <see cref="PositionedEdge.SourceNodeId"/> matches <paramref name="nodeId"/>).
    /// </summary>
    /// <param name="nodeId">The unique visual node identifier.</param>
    /// <returns>A read-only list of outgoing visual edges, or an empty list if none exist.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nodeId"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<VisualEdge> GetOutgoingEdges(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        return _adjacency.OutgoingEdgesByNodeId.TryGetValue(nodeId, out VisualEdge[]? edges) ? edges : [];
    }

    /// <summary>
    /// Gets all immediate neighbor nodes (both 1-hop upstream predecessors and 1-hop downstream successors) connected to <paramref name="nodeId"/>.
    /// </summary>
    /// <param name="nodeId">The unique visual node identifier.</param>
    /// <returns>A read-only list of unique adjacent visual nodes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nodeId"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<VisualNode> GetAdjacentNodes(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        return _adjacency.AdjacentNodesByNodeId.TryGetValue(nodeId, out VisualNode[]? nodes) ? nodes : [];
    }

    /// <summary>
    /// Gets all immediate 1-hop upstream predecessor nodes that produce input activations for <paramref name="nodeId"/>.
    /// </summary>
    /// <param name="nodeId">The unique visual node identifier.</param>
    /// <returns>A read-only list of unique upstream predecessor visual nodes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nodeId"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<VisualNode> GetPredecessors(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        return _adjacency.PredecessorsByNodeId.TryGetValue(nodeId, out VisualNode[]? nodes) ? nodes : [];
    }

    /// <summary>
    /// Gets all immediate 1-hop downstream successor nodes that consume output activations from <paramref name="nodeId"/>.
    /// </summary>
    /// <param name="nodeId">The unique visual node identifier.</param>
    /// <returns>A read-only list of unique downstream successor visual nodes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nodeId"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<VisualNode> GetSuccessors(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        return _adjacency.SuccessorsByNodeId.TryGetValue(nodeId, out VisualNode[]? nodes) ? nodes : [];
    }

    private sealed class AdjacencyIndex
    {
        public Dictionary<string, VisualEdge> EdgesById { get; }
        public Dictionary<string, VisualEdge[]> ConnectedEdgesByNodeId { get; }
        public Dictionary<string, VisualEdge[]> IncomingEdgesByNodeId { get; }
        public Dictionary<string, VisualEdge[]> OutgoingEdgesByNodeId { get; }
        public Dictionary<string, VisualNode[]> PredecessorsByNodeId { get; }
        public Dictionary<string, VisualNode[]> SuccessorsByNodeId { get; }
        public Dictionary<string, VisualNode[]> AdjacentNodesByNodeId { get; }

        public AdjacencyIndex(
            IReadOnlyList<VisualNode> nodes,
            IReadOnlyList<VisualEdge> edges,
            IReadOnlyDictionary<string, VisualNode> nodesById)
        {
            EdgesById = new Dictionary<string, VisualEdge>(edges.Count, StringComparer.Ordinal);

            Dictionary<string, List<VisualEdge>> connected = new(nodes.Count, StringComparer.Ordinal);
            Dictionary<string, List<VisualEdge>> incoming = new(nodes.Count, StringComparer.Ordinal);
            Dictionary<string, List<VisualEdge>> outgoing = new(nodes.Count, StringComparer.Ordinal);

            for (int i = 0; i < edges.Count; i++)
            {
                VisualEdge edge = edges[i];
                EdgesById.TryAdd(edge.Id, edge);

                string src = edge.Layout.SourceNodeId;
                string tgt = edge.Layout.TargetNodeId;

                AddEdge(connected, src, edge);
                if (!string.Equals(src, tgt, StringComparison.Ordinal))
                {
                    AddEdge(connected, tgt, edge);
                }

                AddEdge(incoming, tgt, edge);
                AddEdge(outgoing, src, edge);
            }

            ConnectedEdgesByNodeId = FreezeEdges(connected);
            IncomingEdgesByNodeId = FreezeEdges(incoming);
            OutgoingEdgesByNodeId = FreezeEdges(outgoing);

            PredecessorsByNodeId = new Dictionary<string, VisualNode[]>(incoming.Count, StringComparer.Ordinal);
            foreach ((string tgtId, List<VisualEdge> inEdges) in incoming)
            {
                PredecessorsByNodeId[tgtId] = CollectUniqueNodes(inEdges, isSource: true, nodesById);
            }

            SuccessorsByNodeId = new Dictionary<string, VisualNode[]>(outgoing.Count, StringComparer.Ordinal);
            foreach ((string srcId, List<VisualEdge> outEdges) in outgoing)
            {
                SuccessorsByNodeId[srcId] = CollectUniqueNodes(outEdges, isSource: false, nodesById);
            }

            AdjacentNodesByNodeId = new Dictionary<string, VisualNode[]>(nodes.Count, StringComparer.Ordinal);
            for (int i = 0; i < nodes.Count; i++)
            {
                string id = nodes[i].Id;
                PredecessorsByNodeId.TryGetValue(id, out VisualNode[]? preds);
                SuccessorsByNodeId.TryGetValue(id, out VisualNode[]? succs);

                if ((preds is null || preds.Length == 0) && (succs is null || succs.Length == 0))
                {
                    continue;
                }

                List<VisualNode> adj = new((preds?.Length ?? 0) + (succs?.Length ?? 0));
                HashSet<string> seen = new(StringComparer.Ordinal);

                AddUniqueNodes(preds, seen, adj);
                AddUniqueNodes(succs, seen, adj);

                AdjacentNodesByNodeId[id] = adj.ToArray();
            }
        }

        private static void AddEdge(Dictionary<string, List<VisualEdge>> map, string nodeId, VisualEdge edge)
        {
            if (!map.TryGetValue(nodeId, out List<VisualEdge>? list))
            {
                list = [];
                map[nodeId] = list;
            }

            list.Add(edge);
        }

        private static Dictionary<string, VisualEdge[]> FreezeEdges(Dictionary<string, List<VisualEdge>> source)
        {
            Dictionary<string, VisualEdge[]> result = new(source.Count, StringComparer.Ordinal);
            foreach ((string key, List<VisualEdge> list) in source)
            {
                result[key] = list.ToArray();
            }

            return result;
        }

        private static VisualNode[] CollectUniqueNodes(
            List<VisualEdge> edges,
            bool isSource,
            IReadOnlyDictionary<string, VisualNode> nodesById)
        {
            List<VisualNode> result = new(edges.Count);
            HashSet<string> seen = new(StringComparer.Ordinal);
            for (int i = 0; i < edges.Count; i++)
            {
                string neighborId = isSource ? edges[i].Layout.SourceNodeId : edges[i].Layout.TargetNodeId;
                if (seen.Add(neighborId) && nodesById.TryGetValue(neighborId, out VisualNode? neighborNode))
                {
                    result.Add(neighborNode);
                }
            }

            return result.ToArray();
        }

        private static void AddUniqueNodes(VisualNode[]? source, HashSet<string> seen, List<VisualNode> destination)
        {
            if (source is null)
            {
                return;
            }

            for (int i = 0; i < source.Length; i++)
            {
                VisualNode node = source[i];
                if (seen.Add(node.Id))
                {
                    destination.Add(node);
                }
            }
        }
    }
}
