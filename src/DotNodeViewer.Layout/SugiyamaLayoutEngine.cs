using DotNodeViewer.Layout.Sugiyama;

namespace DotNodeViewer.Layout;

/// <summary>
/// Defines a deterministic layered graph layout engine that computes node coordinates and edge routes for a <see cref="LayoutGraph"/>.
/// </summary>
public interface IGraphLayoutEngine
{
    /// <summary>
    /// Computes a positioned layered graph layout for the specified <paramref name="graph"/>.
    /// </summary>
    /// <param name="graph">The unpositioned <see cref="LayoutGraph"/> to lay out.</param>
    /// <param name="options">Optional layout spacing, direction, and iteration settings; defaults to <see cref="LayoutOptions.Default"/>.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The computed <see cref="PositionedGraph"/> containing node coordinates, port anchors, and routed edges.</returns>
    PositionedGraph ComputeLayout(
        LayoutGraph graph,
        LayoutOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Computes deterministic Sugiyama/dagre-style layered directed graph layouts from <see cref="LayoutGraph"/> instances.
/// </summary>
public sealed class SugiyamaLayoutEngine : IGraphLayoutEngine
{
    /// <summary>
    /// Gets a shared singleton instance of <see cref="SugiyamaLayoutEngine"/>.
    /// </summary>
    public static SugiyamaLayoutEngine Default { get; } = new();

    /// <summary>
    /// Computes a positioned layered graph layout for the specified <paramref name="graph"/> using the default engine.
    /// </summary>
    /// <param name="graph">The unpositioned <see cref="LayoutGraph"/> to lay out.</param>
    /// <param name="options">Optional layout spacing, direction, and iteration settings; defaults to <see cref="LayoutOptions.Default"/>.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The computed <see cref="PositionedGraph"/> containing node coordinates, port anchors, and routed edges.</returns>
    public static PositionedGraph Layout(
        LayoutGraph graph,
        LayoutOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Default.ComputeLayout(graph, options, cancellationToken);

    /// <inheritdoc/>
    public PositionedGraph ComputeLayout(
        LayoutGraph graph,
        LayoutOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        cancellationToken.ThrowIfCancellationRequested();

        LayoutOptions effectiveOptions = options ?? LayoutOptions.Default;
        SugiyamaPipeline pipeline = new(graph, effectiveOptions);

        // Step 1: Cycle removal (DFS feedback arc set reversal)
        pipeline.RemoveCycles();
        cancellationToken.ThrowIfCancellationRequested();

        // Step 2: Layer assignment (longest-path + tight-tree slack refinement)
        pipeline.AssignLayers();
        cancellationToken.ThrowIfCancellationRequested();

        // Step 3: Normalization (dummy vertex insertion for rankDelta > 1)
        pipeline.NormalizeAndBuildLayers();
        cancellationToken.ThrowIfCancellationRequested();

        // Step 4: Crossing minimization (port-aware barycenter sweeps + adjacent-exchange transpose)
        pipeline.MinimizeCrossings();
        cancellationToken.ThrowIfCancellationRequested();

        // Step 5: Coordinate assignment (Brandes–Köpf directional median alignment + block compaction)
        pipeline.AssignCoordinates();
        cancellationToken.ThrowIfCancellationRequested();

        // Step 6: Edge routing & PositionedGraph construction
        return pipeline.BuildPositionedGraph();
    }
}
