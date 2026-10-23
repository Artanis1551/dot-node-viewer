using DotNodeViewer.Layout;

namespace DotNodeViewer.App.Rendering;

/// <summary>
/// Provides a 2D spatial hash grid over <see cref="VisualNode"/> bounding boxes for <c>O(1)</c> pointer hit-testing
/// and viewport culling on large computational graphs.
/// </summary>
public sealed class SpatialGridIndex
{
    private const double DefaultCellSize = 160.0;
    private readonly double _cellSize;
    private readonly Dictionary<(int X, int Y), List<VisualNode>> _grid = new();
    private readonly IReadOnlyList<VisualNode> _nodes;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpatialGridIndex"/> class for the given collection of visual nodes.
    /// </summary>
    /// <param name="nodes">The positioned visual nodes to index.</param>
    /// <param name="cellSize">The spatial grid cell dimension in layout units.</param>
    public SpatialGridIndex(IReadOnlyList<VisualNode> nodes, double cellSize = DefaultCellSize)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        _nodes = nodes;
        _cellSize = cellSize > 10.0 ? cellSize : DefaultCellSize;

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
                    if (!_grid.TryGetValue(key, out List<VisualNode>? bucket))
                    {
                        bucket = [];
                        _grid[key] = bucket;
                    }
                    bucket.Add(node);
                }
            }
        }
    }

    /// <summary>
    /// Gets an empty <see cref="SpatialGridIndex"/> instance containing no nodes.
    /// </summary>
    public static SpatialGridIndex Empty { get; } = new(Array.Empty<VisualNode>());

    /// <summary>
    /// Gets the total number of nodes indexed.
    /// </summary>
    public int Count => _nodes.Count;

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

        if (!_grid.TryGetValue((cx, cy), out List<VisualNode>? bucket))
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
    /// Queries the spatial grid for all nodes whose bounding boxes intersect the specified world rectangular region.
    /// </summary>
    /// <param name="worldX">The left coordinate of the region.</param>
    /// <param name="worldY">The top coordinate of the region.</param>
    /// <param name="worldWidth">The width of the region.</param>
    /// <param name="worldHeight">The height of the region.</param>
    /// <returns>A list of unique intersecting <see cref="VisualNode"/> instances.</returns>
    public List<VisualNode> FindNodesInRegion(double worldX, double worldY, double worldWidth, double worldHeight)
    {
        List<VisualNode> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        int minX = (int)Math.Floor(worldX / _cellSize);
        int maxX = (int)Math.Floor((worldX + worldWidth) / _cellSize);
        int minY = (int)Math.Floor(worldY / _cellSize);
        int maxY = (int)Math.Floor((worldY + worldHeight) / _cellSize);

        BoundingBox queryBox = new(worldX, worldY, worldWidth, worldHeight);

        for (int cy = minY; cy <= maxY; cy++)
        {
            for (int cx = minX; cx <= maxX; cx++)
            {
                if (_grid.TryGetValue((cx, cy), out List<VisualNode>? bucket))
                {
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        VisualNode node = bucket[i];
                        if (seen.Add(node.Id) && node.Bounds.Intersects(queryBox))
                        {
                            result.Add(node);
                        }
                    }
                }
            }
        }

        return result;
    }
}

