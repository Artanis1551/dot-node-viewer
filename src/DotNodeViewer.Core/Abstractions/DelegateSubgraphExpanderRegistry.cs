using DotNodeViewer.Core.Delegates.EthosU;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Abstractions;

/// <summary>
/// Registry that maintains and discovers <see cref="IDelegateSubgraphExpander"/> implementations.
/// </summary>
public sealed class DelegateSubgraphExpanderRegistry
{
    private readonly List<IDelegateSubgraphExpander> _expanders = [];
    private readonly object _gate = new();

    /// <summary>
    /// Gets the shared default expander registry pre-populated with built-in delegate expanders.
    /// </summary>
    public static DelegateSubgraphExpanderRegistry Default { get; } = CreateDefault();

    /// <summary>
    /// Creates a new expander registry instance populated with all built-in delegate decoders.
    /// </summary>
    /// <returns>A new <see cref="DelegateSubgraphExpanderRegistry"/> instance.</returns>
    public static DelegateSubgraphExpanderRegistry CreateDefault()
    {
        var registry = new DelegateSubgraphExpanderRegistry();
        registry.Register(new EthosUDelegateExpander());
        return registry;
    }

    /// <summary>
    /// Registers an expander in the registry.
    /// </summary>
    /// <param name="expander">The expander instance to register.</param>
    public void Register(IDelegateSubgraphExpander expander)
    {
        ArgumentNullException.ThrowIfNull(expander);
        lock (_gate)
        {
            _expanders.Add(expander);
        }
    }

    /// <summary>
    /// Finds the first registered expander capable of expanding the given delegate node.
    /// </summary>
    /// <param name="graph">The container model graph.</param>
    /// <param name="node">The candidate delegate node.</param>
    /// <returns>The matching <see cref="IDelegateSubgraphExpander"/>, or <see langword="null"/> if none found.</returns>
    public IDelegateSubgraphExpander? FindExpander(ModelGraph graph, GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);

        lock (_gate)
        {
            for (int i = 0; i < _expanders.Count; i++)
            {
                if (_expanders[i].CanExpand(graph, node))
                {
                    return _expanders[i];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Asynchronously expands the specified delegate node using the first matching registered expander.
    /// </summary>
    /// <param name="modelStream">The seekable source model stream.</param>
    /// <param name="graph">The container model graph.</param>
    /// <param name="node">The delegate node to expand.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A value task producing the synthesized <see cref="Subgraph"/>, or <see langword="null"/>.</returns>
    public async ValueTask<Subgraph?> ExpandAsync(
        Stream modelStream,
        ModelGraph graph,
        GraphNode node,
        CancellationToken cancellationToken = default)
    {
        IDelegateSubgraphExpander? expander = FindExpander(graph, node);
        if (expander is null)
        {
            return null;
        }

        return await expander.ExpandAsync(modelStream, graph, node, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously extracts and formats the disassembled command lines for the specified delegate node using the first matching registered expander.
    /// </summary>
    /// <param name="modelStream">The seekable source model stream.</param>
    /// <param name="graph">The container model graph.</param>
    /// <param name="node">The delegate node to disassemble.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A value task producing the list of disassembled command strings, or <see langword="null"/>.</returns>
    public async ValueTask<IReadOnlyList<string>?> DisassembleAsync(
        Stream modelStream,
        ModelGraph graph,
        GraphNode node,
        CancellationToken cancellationToken = default)
    {
        IDelegateSubgraphExpander? expander = FindExpander(graph, node);
        if (expander is null)
        {
            return null;
        }

        return await expander.DisassembleAsync(modelStream, graph, node, cancellationToken).ConfigureAwait(false);
    }
}

