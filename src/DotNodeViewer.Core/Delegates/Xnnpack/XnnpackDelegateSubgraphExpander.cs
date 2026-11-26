using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Delegates.Xnnpack;

/// <summary>
/// On-demand delegate expander that decodes ExecuTorch <c>XnnpackBackend</c> FlatBuffer payloads
/// (<c>XH00</c> / <c>XN01</c>) into synthesized <see cref="Subgraph"/> instances with lazy weight slicing.
/// </summary>
internal sealed class XnnpackDelegateSubgraphExpander : IDelegateSubgraphExpander
{
    /// <summary>
    /// Gets the shared singleton instance of <see cref="XnnpackDelegateSubgraphExpander"/>.
    /// </summary>
    public static XnnpackDelegateSubgraphExpander Instance { get; } = new();

    /// <inheritdoc/>
    public string BackendId => "xnnpack";

    /// <inheritdoc/>
    public bool CanExpand(ModelGraph graph, GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);

        bool isXnnpackBackend = false;
        TensorDataSlice? processedSlice = null;

        for (int i = 0; i < node.Attributes.Count; i++)
        {
            NodeAttribute attr = node.Attributes[i];
            if (string.Equals(attr.Name, "delegate.backend", StringComparison.OrdinalIgnoreCase) &&
                attr.Value is AttributeValue.String backendStr &&
                string.Equals(backendStr.Value, "xnnpack", StringComparison.OrdinalIgnoreCase))
            {
                isXnnpackBackend = true;
            }
            else if (string.Equals(attr.Name, "processed", StringComparison.OrdinalIgnoreCase) &&
                     attr.Value is AttributeValue.Bytes bytesAttr)
            {
                processedSlice = bytesAttr.Slice;
            }
        }

        if (!isXnnpackBackend && node.Operator.Name.Contains("Xnnpack", StringComparison.OrdinalIgnoreCase))
        {
            isXnnpackBackend = true;
        }

        return isXnnpackBackend && processedSlice is not null && processedSlice.ByteLength >= 8;
    }

    /// <inheritdoc/>
    public ValueTask<Subgraph?> ExpandAsync(
        Stream modelStream,
        ModelGraph graph,
        GraphNode node,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelStream);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);
        cancellationToken.ThrowIfCancellationRequested();

        if (!modelStream.CanSeek)
        {
            throw new ModelParseException("Model stream must be seekable to expand XNNPACK delegate payloads.", 0, node.Name);
        }

        TensorDataSlice? processedSlice = null;
        for (int i = 0; i < node.Attributes.Count; i++)
        {
            if (string.Equals(node.Attributes[i].Name, "processed", StringComparison.OrdinalIgnoreCase) &&
                node.Attributes[i].Value is AttributeValue.Bytes bytesAttr)
            {
                processedSlice = bytesAttr.Slice;
                break;
            }
        }

        if (processedSlice is null)
        {
            return ValueTask.FromResult<Subgraph?>(null);
        }

        XnnpackDecodedGraph decoded = XnnpackGraphDecoder.Decode(modelStream, node, processedSlice, cancellationToken);
        return ValueTask.FromResult<Subgraph?>(decoded.Subgraph);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>?> DisassembleAsync(
        Stream modelStream,
        ModelGraph graph,
        GraphNode node,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelStream);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);

        // XNNPACK payloads encode high-level operator FlatBuffers rather than hardware register command streams.
        return ValueTask.FromResult<IReadOnlyList<string>?>(null);
    }
}
