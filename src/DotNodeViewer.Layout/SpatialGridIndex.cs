using System.Buffers;

namespace DotNodeViewer.Layout;

/// <summary>
/// Provides a 2D spatial hash grid over <see cref="VisualNode"/> and <see cref="VisualEdge"/> bounding boxes for
/// <c>O(1)</c> pointer hit-testing and zero-allocation viewport culling on computational graphs.
/// </summary>
public sealed class SpatialGridIndex
{
    private const double DefaultCellSize = 160.0;
    private const int StackAllocWordLimit = 128;

    private readonly double _cellSize;
    private readonly Dictionary<(int X, int Y), List<int>> _nodeGrid = new();
    private readonly Dictionary<(int X, int Y), List<int>> _edgeGrid = new();
    private readonly IReadOnlyList<VisualNode> _nodes;
    private readonly IReadOnlyList<VisualEdge> _edges;
    private readonly CellBounds _nodeCellBounds;
    private readonly CellBounds _edgeCellBounds;

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
        _cellSize = double.IsFinite(cellSize) && cellSize > 10.0 ? cellSize : DefaultCellSize;

        _nodeCellBounds = IndexNodes(_nodes, _nodeGrid, _cellSize);
        if (_edges.Count > 0)
        {
            _edgeCellBounds = IndexEdges(_edges, _edgeGrid, _cellSize);
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
        if (!_nodeCellBounds.HasCells || !double.IsFinite(worldX) || !double.IsFinite(worldY))
        {
            return null;
        }

        double rawCx = Math.Floor(worldX / _cellSize);
        double rawCy = Math.Floor(worldY / _cellSize);
        if (rawCx < _nodeCellBounds.MinX
            || rawCx > _nodeCellBounds.MaxX
            || rawCy < _nodeCellBounds.MinY
            || rawCy > _nodeCellBounds.MaxY)
        {
            return null;
        }

        int cx = (int)rawCx;
        int cy = (int)rawCy;

        if (!_nodeGrid.TryGetValue((cx, cy), out List<int>? bucket))
        {
            return null;
        }

        LayoutPoint point = new(worldX, worldY);
        // Traverse backwards so later nodes take precedence in hit-testing
        for (int i = bucket.Count - 1; i >= 0; i--)
        {
            VisualNode node = _nodes[bucket[i]];
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
        FindNodesInRegion(region, result);
        return result;
    }

    /// <summary>
    /// Populates <paramref name="destination"/> with all unique nodes whose bounding boxes intersect <paramref name="region"/>
    /// without allocating intermediate hash sets.
    /// </summary>
    /// <param name="region">The query bounding box in layout space.</param>
    /// <param name="destination">The caller-owned list cleared and populated with matching <see cref="VisualNode"/> instances.</param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    public void FindNodesInRegion(BoundingBox region, List<VisualNode> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();

        if (!TryComputeClampedCellRange(region, in _nodeCellBounds, _cellSize, out int minX, out int maxX, out int minY, out int maxY))
        {
            return;
        }

        int wordCount = (_nodes.Count + 63) >> 6;
        ulong[]? rented = wordCount > StackAllocWordLimit ? ArrayPool<ulong>.Shared.Rent(wordCount) : null;
        Span<ulong> visited = rented is not null ? rented.AsSpan(0, wordCount) : stackalloc ulong[wordCount];
        visited.Clear();

        try
        {
            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    if (!_nodeGrid.TryGetValue((cx, cy), out List<int>? bucket))
                    {
                        continue;
                    }

                    for (int i = 0; i < bucket.Count; i++)
                    {
                        int nodeIdx = bucket[i];
                        if (TryMarkVisited(visited, nodeIdx))
                        {
                            VisualNode node = _nodes[nodeIdx];
                            if (node.Bounds.Intersects(region))
                            {
                                destination.Add(node);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<ulong>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Queries the spatial grid for all routed edges whose bounding boxes intersect the specified bounding box.
    /// </summary>
    /// <param name="region">The query bounding box in layout space.</param>
    /// <returns>A list of unique intersecting <see cref="VisualEdge"/> instances.</returns>
    public List<VisualEdge> FindEdgesInRegion(BoundingBox region)
    {
        List<VisualEdge> result = [];
        FindEdgesInRegion(region, result);
        return result;
    }

    /// <summary>
    /// Populates <paramref name="destination"/> with all unique routed edges whose bounding boxes intersect or touch <paramref name="region"/>
    /// without allocating intermediate hash sets.
    /// </summary>
    /// <param name="region">The query bounding box in layout space.</param>
    /// <param name="destination">The caller-owned list cleared and populated with matching <see cref="VisualEdge"/> instances.</param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    public void FindEdgesInRegion(BoundingBox region, List<VisualEdge> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();

        if (!TryComputeClampedCellRange(region, in _edgeCellBounds, _cellSize, out int minX, out int maxX, out int minY, out int maxY))
        {
            return;
        }

        int wordCount = (_edges.Count + 63) >> 6;
        ulong[]? rented = wordCount > StackAllocWordLimit ? ArrayPool<ulong>.Shared.Rent(wordCount) : null;
        Span<ulong> visited = rented is not null ? rented.AsSpan(0, wordCount) : stackalloc ulong[wordCount];
        visited.Clear();

        try
        {
            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    if (!_edgeGrid.TryGetValue((cx, cy), out List<int>? bucket))
                    {
                        continue;
                    }

                    for (int i = 0; i < bucket.Count; i++)
                    {
                        int edgeIdx = bucket[i];
                        if (TryMarkVisited(visited, edgeIdx))
                        {
                            VisualEdge edge = _edges[edgeIdx];
                            if (edge.Bounds.IntersectsInclusive(region))
                            {
                                destination.Add(edge);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<ulong>.Shared.Return(rented);
            }
        }
    }

    private static CellBounds IndexNodes(
        IReadOnlyList<VisualNode> nodes,
        Dictionary<(int X, int Y), List<int>> grid,
        double cellSize)
    {
        CellBounds bounds = default;
        for (int i = 0; i < nodes.Count; i++)
        {
            AddToGrid(grid, ref bounds, i, nodes[i].Bounds, cellSize);
        }

        return bounds;
    }

    private static CellBounds IndexEdges(
        IReadOnlyList<VisualEdge> edges,
        Dictionary<(int X, int Y), List<int>> grid,
        double cellSize)
    {
        CellBounds bounds = default;
        for (int i = 0; i < edges.Count; i++)
        {
            VisualEdge edge = edges[i];
            if (edge.Layout.Points.Count == 0 && edge.Layout.BezierSegments.Count == 0)
            {
                continue;
            }

            AddToGrid(grid, ref bounds, i, edge.Bounds, cellSize);
        }

        return bounds;
    }

    private static void AddToGrid(
        Dictionary<(int X, int Y), List<int>> grid,
        ref CellBounds bounds,
        int index,
        BoundingBox box,
        double cellSize)
    {
        if (!IsValidFiniteBox(box))
        {
            return;
        }

        double rawMinX = Math.Floor(box.X / cellSize);
        double rawMaxX = Math.Floor(box.Right / cellSize);
        double rawMinY = Math.Floor(box.Y / cellSize);
        double rawMaxY = Math.Floor(box.Bottom / cellSize);
        if (rawMinX < int.MinValue || rawMaxX > int.MaxValue || rawMinY < int.MinValue || rawMaxY > int.MaxValue)
        {
            return;
        }

        int minX = (int)rawMinX;
        int maxX = (int)rawMaxX;
        int minY = (int)rawMinY;
        int maxY = (int)rawMaxY;

        if (!bounds.HasCells)
        {
            bounds = new CellBounds(true, minX, maxX, minY, maxY);
        }
        else
        {
            bounds = new CellBounds(
                true,
                Math.Min(bounds.MinX, minX),
                Math.Max(bounds.MaxX, maxX),
                Math.Min(bounds.MinY, minY),
                Math.Max(bounds.MaxY, maxY));
        }

        for (int cy = minY; cy <= maxY; cy++)
        {
            for (int cx = minX; cx <= maxX; cx++)
            {
                (int X, int Y) key = (cx, cy);
                if (!grid.TryGetValue(key, out List<int>? bucket))
                {
                    bucket = [];
                    grid[key] = bucket;
                }

                bucket.Add(index);
            }
        }
    }

    private static bool TryComputeClampedCellRange(
        BoundingBox region,
        in CellBounds gridBounds,
        double cellSize,
        out int minX,
        out int maxX,
        out int minY,
        out int maxY)
    {
        if (!gridBounds.HasCells || !IsValidFiniteBox(region))
        {
            minX = maxX = minY = maxY = 0;
            return false;
        }

        double rawMinX = Math.Floor(region.X / cellSize);
        double rawMaxX = Math.Floor(region.Right / cellSize);
        double rawMinY = Math.Floor(region.Y / cellSize);
        double rawMaxY = Math.Floor(region.Bottom / cellSize);

        if (rawMaxX < gridBounds.MinX
            || rawMinX > gridBounds.MaxX
            || rawMaxY < gridBounds.MinY
            || rawMinY > gridBounds.MaxY)
        {
            minX = maxX = minY = maxY = 0;
            return false;
        }

        minX = (int)Math.Max(rawMinX, gridBounds.MinX);
        maxX = (int)Math.Min(rawMaxX, gridBounds.MaxX);
        minY = (int)Math.Max(rawMinY, gridBounds.MinY);
        maxY = (int)Math.Min(rawMaxY, gridBounds.MaxY);
        return true;
    }

    private static bool IsValidFiniteBox(BoundingBox box) =>
        double.IsFinite(box.X)
        && double.IsFinite(box.Y)
        && double.IsFinite(box.Width)
        && double.IsFinite(box.Height)
        && box.Width >= 0.0
        && box.Height >= 0.0
        && double.IsFinite(box.Right)
        && double.IsFinite(box.Bottom);

    private static bool TryMarkVisited(Span<ulong> visited, int index)
    {
        int wordIndex = index >> 6;
        ulong mask = 1UL << (index & 63);
        ref ulong word = ref visited[wordIndex];
        if ((word & mask) != 0)
        {
            return false;
        }

        word |= mask;
        return true;
    }

    private readonly record struct CellBounds(
        bool HasCells,
        int MinX,
        int MaxX,
        int MinY,
        int MaxY);
}
