namespace DotNodeViewer.Layout;

/// <summary>
/// Provides a 2D spatial hash grid over <see cref="VisualNode"/> and <see cref="VisualEdge"/> bounding boxes for
/// <c>O(1)</c> pointer hit-testing and viewport culling on computational graphs.
/// </summary>
public sealed class SpatialGridIndex
{
    private const double DefaultCellSize = 160.0;
    private readonly double _cellSize;
    private readonly Dictionary<(int X, int Y), List<VisualNode>> _nodeGrid = new();
    private readonly Dictionary<(int X, int Y), List<VisualEdge>> _edgeGrid = new();
    private readonly IReadOnlyList<VisualNode> _nodes;
    private readonly IReadOnlyList<VisualEdge> _edges;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpatialGridIndex"/> class for the given collections of visual nodes and edges.
    /// </summary>
    /// <param name="nodes">The positioned visual nodes to index.</param>
    /// <param name="edges">Optional routed visual edges to index.</param>
    /// <param name="cellSize">The spatial grid cell dimension in layout units; defaults to 160.0.</param>
    /// <exception cref="ArgumentNullException"><paramref name="nodes"/> is <see langword="null"/>.</exception>
    public SpatialGridIndex(
        IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<VisualEdge>? edges = null,
        double cellSize = DefaultCellSize)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        _nodes = nodes;
        _edges = edges ?? [];
        _cellSize = cellSize > 10.0 ? cellSize : DefaultCellSize;

        IndexNodes(_nodes);
        if (_edges.Count > 0)
        {
            IndexEdges(_edges);
        }
    }

    /// <summary>
    /// Gets an empty <see cref="SpatialGridIndex"/> instance containing no nodes or edges.
    /// </summary>
    public static SpatialGridIndex Empty { get; } = new(Array.Empty<VisualNode>(), Array.Empty<VisualEdge>());

    /// <summary>
    /// Gets the total number of nodes indexed.
    /// </summary>
    public int Count => _nodes.Count;

    /// <summary>
    /// Gets the total number of edges indexed.
    /// </summary>
    public int EdgeCount => _edges.Count;

    /// <summary>
    /// Queries the spatial grid for a <see cref="VisualNode"/> whose bounding box contains the given world coordinates.
    /// </summary>
    /// <param name="worldX">The horizontal coordinate in layout space.</param>
    /// <param name="worldY">The vertical coordinate in layout space.</param>
    /// <returns>The matching <see cref="VisualNode"/> if found; otherwise, <see langword="null"/>.</returns>
    public VisualNode? FindNodeAt(double worldX, double worldY)
    {
        int cx = (int)Math.Floor(worldX / _cellSize);
        int cy = (int)Math.Floor(worldY / _cellSize);

        if (!_nodeGrid.TryGetValue((cx, cy), out List<VisualNode>? bucket))
        {
            return null;
        }

        LayoutPoint point = new(worldX, worldY);
        // Traverse backwards so later nodes take precedence in hit-testing
        for (int i = bucket.Count - 1; i >= 0; i--)
        {
            VisualNode node = bucket[i];
            if (node.Bounds.Contains(point))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// Queries the spatial grid for all nodes whose bounding boxes intersect the specified rectangular region.
    /// </summary>
    /// <param name="worldX">The left coordinate of the region.</param>
    /// <param name="worldY">The top coordinate of the region.</param>
    /// <param name="worldWidth">The width of the region.</param>
    /// <param name="worldHeight">The height of the region.</param>
    /// <returns>A list of unique intersecting <see cref="VisualNode"/> instances.</returns>
    public List<VisualNode> FindNodesInRegion(double worldX, double worldY, double worldWidth, double worldHeight) =>
        FindNodesInRegion(new BoundingBox(worldX, worldY, worldWidth, worldHeight));

    /// <summary>
    /// Queries the spatial grid for all nodes whose bounding boxes intersect the specified bounding box.
    /// </summary>
    /// <param name="region">The query bounding box in layout space.</param>
    /// <returns>A list of unique intersecting <see cref="VisualNode"/> instances.</returns>
    public List<VisualNode> FindNodesInRegion(BoundingBox region)
    {
        List<VisualNode> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        int minX = (int)Math.Floor(region.X / _cellSize);
        int maxX = (int)Math.Floor(region.Right / _cellSize);
        int minY = (int)Math.Floor(region.Y / _cellSize);
        int maxY = (int)Math.Floor(region.Bottom / _cellSize);

        for (int cy = minY; cy <= maxY; cy++)
        {
            for (int cx = minX; cx <= maxX; cx++)
            {
                if (_nodeGrid.TryGetValue((cx, cy), out List<VisualNode>? bucket))
                {
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        VisualNode node = bucket[i];
                        if (seen.Add(node.Id) && node.Bounds.Intersects(region))
                        {
                            result.Add(node);
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Queries the spatial grid for all routed edges whose bounding boxes intersect the specified bounding box.
    /// </summary>
    /// <param name="region">The query bounding box in layout space.</param>
    /// <returns>A list of unique intersecting <see cref="VisualEdge"/> instances.</returns>
    public List<VisualEdge> FindEdgesInRegion(BoundingBox region)
    {
        List<VisualEdge> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        int minX = (int)Math.Floor(region.X / _cellSize);
        int maxX = (int)Math.Floor(region.Right / _cellSize);
        int minY = (int)Math.Floor(region.Y / _cellSize);
        int maxY = (int)Math.Floor(region.Bottom / _cellSize);

        for (int cy = minY; cy <= maxY; cy++)
        {
            for (int cx = minX; cx <= maxX; cx++)
            {
                if (_edgeGrid.TryGetValue((cx, cy), out List<VisualEdge>? bucket))
                {
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        VisualEdge edge = bucket[i];
                        if (seen.Add(edge.Id) && edge.Bounds.Intersects(region))
                        {
                            result.Add(edge);
                        }
                    }
                }
            }
        }

        return result;
    }

    private void IndexNodes(IReadOnlyList<VisualNode> nodes)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            VisualNode node = nodes[i];
            BoundingBox b = node.Bounds;

            int minX = (int)Math.Floor(b.X / _cellSize);
            int maxX = (int)Math.Floor(b.Right / _cellSize);
            int minY = (int)Math.Floor(b.Y / _cellSize);
            int maxY = (int)Math.Floor(b.Bottom / _cellSize);

            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    (int X, int Y) key = (cx, cy);
                    if (!_nodeGrid.TryGetValue(key, out List<VisualNode>? bucket))
                    {
                        bucket = [];
                        _nodeGrid[key] = bucket;
                    }

                    bucket.Add(node);
                }
            }
        }
    }

    private void IndexEdges(IReadOnlyList<VisualEdge> edges)
    {
        for (int i = 0; i < edges.Count; i++)
        {
            VisualEdge edge = edges[i];
            BoundingBox b = edge.Bounds;

            int minX = (int)Math.Floor(b.X / _cellSize);
            int maxX = (int)Math.Floor(b.Right / _cellSize);
            int minY = (int)Math.Floor(b.Y / _cellSize);
            int maxY = (int)Math.Floor(b.Bottom / _cellSize);

            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    (int X, int Y) key = (cx, cy);
                    if (!_edgeGrid.TryGetValue(key, out List<VisualEdge>? bucket))
                    {
                        bucket = [];
                        _edgeGrid[key] = bucket;
                    }

                    bucket.Add(edge);
                }
            }
        }
    }
}
