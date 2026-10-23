namespace DotNodeViewer.Layout;

/// <summary>
/// Specifies the primary flow direction of a layered graph layout.
/// </summary>
public enum LayoutDirection
{
    /// <summary>
    /// Layers flow vertically from top (inputs) to bottom (outputs).
    /// </summary>
    TopToBottom,

    /// <summary>
    /// Layers flow horizontally from left (inputs) to right (outputs).
    /// </summary>
    LeftToRight,
}

/// <summary>
/// Represents a 2D point in layout space.
/// </summary>
/// <param name="X">The horizontal coordinate.</param>
/// <param name="Y">The vertical coordinate.</param>
public readonly record struct LayoutPoint(double X, double Y);

/// <summary>
/// Represents an axis-aligned 2D bounding box in layout space.
/// </summary>
/// <param name="X">The left coordinate of the box.</param>
/// <param name="Y">The top coordinate of the box.</param>
/// <param name="Width">The non-negative width of the box.</param>
/// <param name="Height">The non-negative height of the box.</param>
public readonly record struct BoundingBox(double X, double Y, double Width, double Height)
{
    /// <summary>
    /// Gets an empty <see cref="BoundingBox"/> at the origin.
    /// </summary>
    public static BoundingBox Empty => new(0, 0, 0, 0);

    /// <summary>
    /// Gets the right coordinate (<c>X + Width</c>) of the bounding box.
    /// </summary>
    public double Right => X + Width;

    /// <summary>
    /// Gets the bottom coordinate (<c>Y + Height</c>) of the bounding box.
    /// </summary>
    public double Bottom => Y + Height;

    /// <summary>
    /// Gets the horizontal center coordinate of the bounding box.
    /// </summary>
    public double CenterX => X + (Width * 0.5);

    /// <summary>
    /// Gets the vertical center coordinate of the bounding box.
    /// </summary>
    public double CenterY => Y + (Height * 0.5);

    /// <summary>
    /// Determines whether this bounding box strictly intersects <paramref name="other"/> (excluding touching borders within <paramref name="epsilon"/>).
    /// </summary>
    /// <param name="other">The bounding box to test against.</param>
    /// <param name="epsilon">The tolerance for touching edges.</param>
    /// <returns><see langword="true"/> if the boxes overlap in both axes; otherwise, <see langword="false"/>.</returns>
    public bool Intersects(BoundingBox other, double epsilon = 1e-6) =>
        X + epsilon < other.Right
        && Right - epsilon > other.X
        && Y + epsilon < other.Bottom
        && Bottom - epsilon > other.Y;

    /// <summary>
    /// Determines whether the specified <paramref name="point"/> lies within or on the boundary of this box.
    /// </summary>
    /// <param name="point">The point to test.</param>
    /// <returns><see langword="true"/> if <paramref name="point"/> is inside the box; otherwise, <see langword="false"/>.</returns>
    public bool Contains(LayoutPoint point) =>
        point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;
}

/// <summary>
/// Represents a single cubic Bézier curve segment along a routed edge.
/// </summary>
/// <param name="Start">The start point of the segment.</param>
/// <param name="Control1">The first control point.</param>
/// <param name="Control2">The second control point.</param>
/// <param name="End">The end point of the segment.</param>
public readonly record struct CubicBezierSegment(
    LayoutPoint Start,
    LayoutPoint Control1,
    LayoutPoint Control2,
    LayoutPoint End);

/// <summary>
/// Configures spacing, orientation, and iteration budgets for <see cref="SugiyamaLayoutEngine"/>.
/// </summary>
public sealed record LayoutOptions
{
    /// <summary>
    /// Gets a shared default <see cref="LayoutOptions"/> instance.
    /// </summary>
    public static LayoutOptions Default { get; } = new();

    /// <summary>
    /// Gets the primary flow direction of the layered graph.
    /// </summary>
    public LayoutDirection Direction { get; init; } = LayoutDirection.TopToBottom;

    /// <summary>
    /// Gets the minimum distance between adjacent layers (ranks) along the primary flow axis.
    /// </summary>
    public double LayerSeparation { get; init; } = 48.0;

    /// <summary>
    /// Gets the minimum distance between adjacent nodes within the same layer along the secondary axis.
    /// </summary>
    public double NodeSeparation { get; init; } = 28.0;

    /// <summary>
    /// Gets the outer margin applied around the positioned graph bounding box.
    /// </summary>
    public double Margin { get; init; } = 24.0;

    /// <summary>
    /// Gets the maximum number of alternating top-down / bottom-up crossing minimization sweeps.
    /// </summary>
    public int CrossingSweepIterations { get; init; } = 8;

    /// <summary>
    /// Gets the maximum number of tight-tree / slack-minimization passes during layer ranking.
    /// </summary>
    public int RankingRefinementIterations { get; init; } = 16;

    /// <summary>
    /// Gets the horizontal spacing reserved for each dummy routing vertex inserted along long multi-layer edges.
    /// </summary>
    public double DummyNodeWidth { get; init; } = 12.0;
}

