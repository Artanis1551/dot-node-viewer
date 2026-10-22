using System.Diagnostics.CodeAnalysis;

namespace DotNodeViewer.Core.Ir;

/// <summary>
/// Represents a named input or output port on a <see cref="GraphNode"/>.
/// </summary>
/// <param name="Name">The formal parameter or port name (for example, <c>"X"</c>, <c>"W"</c>, <c>"B"</c>, or <c>"Y"</c>).</param>
/// <param name="Tensors">The ordered list of <see cref="TensorEdge"/> connections attached to this port (supporting single and variadic ports).</param>
public sealed record NodePort(
    string Name,
    IReadOnlyList<TensorEdge> Tensors);

/// <summary>
/// Represents a named external input or output boundary port of a <see cref="Subgraph"/>.
/// </summary>
/// <param name="Name">The name of the subgraph boundary port.</param>
/// <param name="Tensors">The <see cref="TensorEdge"/> connections exposed through this boundary port.</param>
public sealed record GraphPort(
    string Name,
    IReadOnlyList<TensorEdge> Tensors);

/// <summary>
/// Represents a single operator, layer, or custom delegate invocation node within a <see cref="Subgraph"/>.
/// </summary>
/// <param name="Name">The node identifier or display name within the subgraph.</param>
/// <param name="Operator">Metadata describing the operator type, semantic category, domain, and version.</param>
/// <param name="Inputs">The ordered input ports of the node.</param>
/// <param name="Outputs">The ordered output ports of the node.</param>
/// <param name="Attributes">The typed hyperparameter and configuration attributes attached to the node.</param>
/// <param name="Documentation">An optional documentation string or source stack trace annotation associated with the node.</param>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Operator is the property name specified in the frozen IR contract on a sealed record.")]
public sealed record GraphNode(
    string Name,
    OperatorInfo Operator,
    IReadOnlyList<NodePort> Inputs,
    IReadOnlyList<NodePort> Outputs,
    IReadOnlyList<NodeAttribute> Attributes,
    string? Documentation = null);

/// <summary>
/// Represents a directed computational graph (or module hierarchy fallback) consisting of operator nodes, tensor edges, and boundary ports.
/// </summary>
/// <param name="Name">The name of the subgraph (for example, <c>"main"</c> or <c>"forward"</c>).</param>
/// <param name="Nodes">The ordered list of operator nodes in the subgraph.</param>
/// <param name="Tensors">The catalog of all tensors referenced within the subgraph.</param>
/// <param name="Inputs">The external input ports of the subgraph.</param>
/// <param name="Outputs">The external output ports of the subgraph.</param>
public sealed record Subgraph(
    string Name,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<TensorEdge> Tensors,
    IReadOnlyList<GraphPort> Inputs,
    IReadOnlyList<GraphPort> Outputs);

/// <summary>
/// Represents the top-level immutable Unified Graph IR extracted from a neural network model file.
/// </summary>
/// <param name="Format">Information about the detected model format and version.</param>
/// <param name="Producer">An optional producer tool name and version string (for example, <c>"pytorch 2.6.0"</c>).</param>
/// <param name="Subgraphs">The list of subgraphs in the model. When non-empty, <c>Subgraphs[0]</c> is always the primary entry subgraph.</param>
/// <param name="Metadata">Arbitrary key-value model metadata properties extracted from the container header.</param>
/// <param name="Diagnostics">Non-fatal warnings or informational notes recorded during parsing.</param>
public sealed record ModelGraph(
    ModelFormatInfo Format,
    string? Producer,
    IReadOnlyList<Subgraph> Subgraphs,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<ParseDiagnostic> Diagnostics)
{
    /// <summary>
    /// Gets the primary entry subgraph (<c>Subgraphs[0]</c>) of the model, or <see langword="null"/> if <see cref="Subgraphs"/> is empty.
    /// </summary>
    /// <value>The primary <see cref="Subgraph"/> or <see langword="null"/>.</value>
    public Subgraph? PrimarySubgraph => Subgraphs.Count > 0 ? Subgraphs[0] : null;
}

