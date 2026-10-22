using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Abstractions;

/// <summary>
/// Provides fast, precomputed lookup tables for navigating tensor producers, consumers, and 1-hop node neighborhoods within a <see cref="Subgraph"/>.
/// </summary>
public sealed class GraphIndex
{
    private readonly Dictionary<string, GraphNode> _nodesByName;
    private readonly Dictionary<string, TensorEdge> _tensorsByName;
    private readonly Dictionary<string, GraphNode> _producerByTensorName;
    private readonly Dictionary<string, List<GraphNode>> _consumersByTensorName;

    /// <summary>
    /// Initializes a new instance of the <see cref="GraphIndex"/> class for the specified <paramref name="subgraph"/>.
    /// </summary>
    /// <param name="subgraph">The subgraph whose nodes, ports, and tensor edges will be indexed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="subgraph"/> is <see langword="null"/>.</exception>
    public GraphIndex(Subgraph subgraph)
    {
        ArgumentNullException.ThrowIfNull(subgraph);
        Subgraph = subgraph;

        _nodesByName = new Dictionary<string, GraphNode>(subgraph.Nodes.Count, StringComparer.Ordinal);
        _tensorsByName = new Dictionary<string, TensorEdge>(subgraph.Tensors.Count, StringComparer.Ordinal);
        _producerByTensorName = new Dictionary<string, GraphNode>(subgraph.Tensors.Count, StringComparer.Ordinal);
        _consumersByTensorName = new Dictionary<string, List<GraphNode>>(subgraph.Tensors.Count, StringComparer.Ordinal);

        for (int i = 0; i < subgraph.Tensors.Count; i++)
        {
            TensorEdge tensor = subgraph.Tensors[i];
            if (!string.IsNullOrEmpty(tensor.Name))
            {
                _tensorsByName.TryAdd(tensor.Name, tensor);
            }
        }

        for (int i = 0; i < subgraph.Nodes.Count; i++)
        {
            GraphNode node = subgraph.Nodes[i];
            if (!string.IsNullOrEmpty(node.Name))
            {
                _nodesByName.TryAdd(node.Name, node);
            }

            for (int p = 0; p < node.Outputs.Count; p++)
            {
                IReadOnlyList<TensorEdge> portTensors = node.Outputs[p].Tensors;
                for (int t = 0; t < portTensors.Count; t++)
                {
                    TensorEdge tensor = portTensors[t];
                    if (!string.IsNullOrEmpty(tensor.Name))
                    {
                        _tensorsByName.TryAdd(tensor.Name, tensor);
                        _producerByTensorName.TryAdd(tensor.Name, node);
                    }
                }
            }

            for (int p = 0; p < node.Inputs.Count; p++)
            {
                IReadOnlyList<TensorEdge> portTensors = node.Inputs[p].Tensors;
                for (int t = 0; t < portTensors.Count; t++)
                {
                    TensorEdge tensor = portTensors[t];
                    if (string.IsNullOrEmpty(tensor.Name))
                    {
                        continue;
                    }

                    _tensorsByName.TryAdd(tensor.Name, tensor);
                    if (!_consumersByTensorName.TryGetValue(tensor.Name, out List<GraphNode>? consumers))
                    {
                        consumers = [];
                        _consumersByTensorName[tensor.Name] = consumers;
                    }

                    if (!consumers.Contains(node))
                    {
                        consumers.Add(node);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets the <see cref="Subgraph"/> indexed by this instance.
    /// </summary>
    /// <value>The indexed <see cref="Subgraph"/>.</value>
    public Subgraph Subgraph { get; }

    /// <summary>
    /// Looks up a <see cref="GraphNode"/> by its exact name.
    /// </summary>
    /// <param name="name">The node name to search for.</param>
    /// <returns>The matching <see cref="GraphNode"/>, or <see langword="null"/> if not found.</returns>
    public GraphNode? GetNodeByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _nodesByName.GetValueOrDefault(name);
    }

    /// <summary>
    /// Looks up a <see cref="TensorEdge"/> by its exact name.
    /// </summary>
    /// <param name="name">The tensor name to search for.</param>
    /// <returns>The matching <see cref="TensorEdge"/>, or <see langword="null"/> if not found.</returns>
    public TensorEdge? GetTensorByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _tensorsByName.GetValueOrDefault(name);
    }

    /// <summary>
    /// Gets the operator node that produces the specified <paramref name="tensor"/>.
    /// </summary>
    /// <param name="tensor">The tensor edge whose producer node is requested.</param>
    /// <returns>The producing <see cref="GraphNode"/>, or <see langword="null"/> if the tensor is a graph input, initializer, or unproduced.</returns>
    public GraphNode? GetProducer(TensorEdge tensor)
    {
        ArgumentNullException.ThrowIfNull(tensor);
        return GetProducer(tensor.Name);
    }

    /// <summary>
    /// Gets the operator node that produces the tensor with the specified <paramref name="tensorName"/>.
    /// </summary>
    /// <param name="tensorName">The name of the tensor whose producer node is requested.</param>
    /// <returns>The producing <see cref="GraphNode"/>, or <see langword="null"/> if the tensor is a graph input, initializer, or unproduced.</returns>
    public GraphNode? GetProducer(string tensorName)
    {
        ArgumentNullException.ThrowIfNull(tensorName);
        return _producerByTensorName.GetValueOrDefault(tensorName);
    }

    /// <summary>
    /// Gets the ordered list of operator nodes that consume the specified <paramref name="tensor"/> as an input.
    /// </summary>
    /// <param name="tensor">The tensor edge whose consumer nodes are requested.</param>
    /// <returns>A read-only list of consuming <see cref="GraphNode"/> instances.</returns>
    public IReadOnlyList<GraphNode> GetConsumers(TensorEdge tensor)
    {
        ArgumentNullException.ThrowIfNull(tensor);
        return GetConsumers(tensor.Name);
    }

    /// <summary>
    /// Gets the ordered list of operator nodes that consume the tensor with the specified <paramref name="tensorName"/> as an input.
    /// </summary>
    /// <param name="tensorName">The name of the tensor whose consumer nodes are requested.</param>
    /// <returns>A read-only list of consuming <see cref="GraphNode"/> instances.</returns>
    public IReadOnlyList<GraphNode> GetConsumers(string tensorName)
    {
        ArgumentNullException.ThrowIfNull(tensorName);
        return _consumersByTensorName.TryGetValue(tensorName, out List<GraphNode>? consumers)
            ? consumers
            : [];
    }

    /// <summary>
    /// Gets the distinct immediate upstream producer nodes that feed any input port of <paramref name="node"/>.
    /// </summary>
    /// <param name="node">The node whose 1-hop upstream producers are requested.</param>
    /// <returns>A read-only list of upstream <see cref="GraphNode"/> instances.</returns>
    public IReadOnlyList<GraphNode> GetUpstreamNodes(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        List<GraphNode> upstream = [];
        for (int p = 0; p < node.Inputs.Count; p++)
        {
            IReadOnlyList<TensorEdge> tensors = node.Inputs[p].Tensors;
            for (int t = 0; t < tensors.Count; t++)
            {
                GraphNode? producer = GetProducer(tensors[t].Name);
                if (producer is not null && !upstream.Contains(producer))
                {
                    upstream.Add(producer);
                }
            }
        }

        return upstream;
    }

    /// <summary>
    /// Gets the distinct immediate downstream consumer nodes fed by any output port of <paramref name="node"/>.
    /// </summary>
    /// <param name="node">The node whose 1-hop downstream consumers are requested.</param>
    /// <returns>A read-only list of downstream <see cref="GraphNode"/> instances.</returns>
    public IReadOnlyList<GraphNode> GetDownstreamNodes(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        List<GraphNode> downstream = [];
        for (int p = 0; p < node.Outputs.Count; p++)
        {
            IReadOnlyList<TensorEdge> tensors = node.Outputs[p].Tensors;
            for (int t = 0; t < tensors.Count; t++)
            {
                IReadOnlyList<GraphNode> consumers = GetConsumers(tensors[t].Name);
                for (int c = 0; c < consumers.Count; c++)
                {
                    if (!downstream.Contains(consumers[c]))
                    {
                        downstream.Add(consumers[c]);
                    }
                }
            }
        }

        return downstream;
    }
}

