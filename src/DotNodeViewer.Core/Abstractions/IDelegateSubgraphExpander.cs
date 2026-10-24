using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Abstractions;

/// <summary>
/// Defines an on-demand expander that decodes compiled hardware accelerator delegate payloads into synthesized subgraphs.
/// </summary>
public interface IDelegateSubgraphExpander
{
    /// <summary>
    /// Gets the hardware accelerator backend identifier supported by this expander (for example, <c>"ethos-u"</c>).
    /// </summary>
    string BackendId { get; }

    /// <summary>
    /// Determines whether the specified graph node contains an expandable delegate payload supported by this expander.
    /// </summary>
    /// <param name="graph">The container model graph.</param>
    /// <param name="node">The candidate delegate node.</param>
    /// <returns><see langword="true"/> if this expander can decode the delegate payload; otherwise, <see langword="false"/>.</returns>
    bool CanExpand(ModelGraph graph, GraphNode node);

    /// <summary>
    /// Asynchronously reads the delegate payload from the source stream and expands it into a synthesized <see cref="Subgraph"/>.
    /// </summary>
    /// <param name="modelStream">The seekable source model stream.</param>
    /// <param name="graph">The container model graph.</param>
    /// <param name="node">The delegate node to expand.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A value task producing the synthesized <see cref="Subgraph"/>, or <see langword="null"/> if decoding failed or is unsupported.</returns>
    ValueTask<Subgraph?> ExpandAsync(
        Stream modelStream,
        ModelGraph graph,
        GraphNode node,
        CancellationToken cancellationToken = default);
}

