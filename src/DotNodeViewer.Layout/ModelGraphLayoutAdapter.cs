using System.Globalization;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Layout;

/// <summary>
/// Converts a <see cref="Subgraph"/> from <c>DotNodeViewer.Core</c> into a <see cref="LayoutGraph"/> and produces a
/// positioned <see cref="VisualSubgraphLayout"/> with collapsed initializer slots, fused activation badges, and routed tensor edges.
/// </summary>
public sealed class ModelGraphLayoutAdapter
{
    private const double MinOperatorNodeWidth = 160.0;
    private const double MaxOperatorNodeWidth = 600.0;
    private const double MinTerminalPillWidth = 110.0;
    private const double MaxTerminalPillWidth = 460.0;

    private const double BaseOperatorHeaderHeight = 34.0;
    private const double ParameterRowHeight = 22.0;
    private const double ActivationBadgeRowHeight = 24.0;
    private const double OperatorVerticalPadding = 12.0;
    private const double TerminalPillHeightSingle = 32.0;
    private const double TerminalPillHeightWithSubtitle = 46.0;

    private const double TitleFontSize = 13.0;
    private const double SubtitleFontSize = 11.0;
    private const double ParameterFontSize = 11.0;
    private const double EdgeLabelFontSize = 10.5;

    private readonly IGraphLayoutEngine _layoutEngine;
    private readonly ITextMeasurer _textMeasurer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelGraphLayoutAdapter"/> class.
    /// </summary>
    /// <param name="layoutEngine">An optional layout engine; defaults to <see cref="SugiyamaLayoutEngine.Default"/>.</param>
    /// <param name="textMeasurer">An optional text measurer; defaults to <see cref="MonospaceTextMeasurer.Default"/>.</param>
    public ModelGraphLayoutAdapter(
        IGraphLayoutEngine? layoutEngine = null,
        ITextMeasurer? textMeasurer = null)
    {
        _layoutEngine = layoutEngine ?? SugiyamaLayoutEngine.Default;
        _textMeasurer = textMeasurer ?? MonospaceTextMeasurer.Default;
    }

    /// <summary>
    /// Gets a shared default <see cref="ModelGraphLayoutAdapter"/> instance using <see cref="SugiyamaLayoutEngine.Default"/> and <see cref="MonospaceTextMeasurer.Default"/>.
    /// </summary>
    public static ModelGraphLayoutAdapter Default { get; } = new();

    /// <summary>
    /// Builds and computes the positioned <see cref="VisualSubgraphLayout"/> for the primary subgraph of <paramref name="modelGraph"/>.
    /// </summary>
    /// <param name="modelGraph">The <see cref="ModelGraph"/> whose primary subgraph will be laid out.</param>
    /// <param name="options">Optional layout spacing and direction settings.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The positioned <see cref="VisualSubgraphLayout"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelGraph"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="modelGraph"/> contains no subgraphs.</exception>
    public VisualSubgraphLayout CreateLayout(
        ModelGraph modelGraph,
        LayoutOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelGraph);
        Subgraph primary = modelGraph.PrimarySubgraph
            ?? throw new InvalidOperationException("Cannot lay out a ModelGraph that contains no subgraphs.");
        return CreateLayout(primary, options, cancellationToken);
    }

    /// <summary>
    /// Builds and computes the positioned <see cref="VisualSubgraphLayout"/> for the specified <paramref name="subgraph"/>.
    /// </summary>
    /// <param name="subgraph">The <see cref="Subgraph"/> to lay out.</param>
    /// <param name="options">Optional layout spacing and direction settings.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The positioned <see cref="VisualSubgraphLayout"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="subgraph"/> is <see langword="null"/>.</exception>
    public VisualSubgraphLayout CreateLayout(
        Subgraph subgraph,
        LayoutOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subgraph);
        cancellationToken.ThrowIfCancellationRequested();

        List<PendingVisualNode> pendingNodes = new(subgraph.Inputs.Count + subgraph.Nodes.Count + subgraph.Outputs.Count);
        List<LayoutNode> layoutNodes = new(subgraph.Inputs.Count + subgraph.Nodes.Count + subgraph.Outputs.Count);

        // Map tensor Name -> (SourceNodeId, SourcePortId)
        Dictionary<string, (string NodeId, string PortId, TensorEdge Tensor)> producerByTensorName =
            new(subgraph.Tensors.Count, StringComparer.Ordinal);

        // 1. Create compact terminal pills for Subgraph.Inputs
        for (int i = 0; i < subgraph.Inputs.Count; i++)
        {
            GraphPort inputPort = subgraph.Inputs[i];
            TensorEdge? primaryTensor = inputPort.Tensors.Count > 0 ? inputPort.Tensors[0] : null;
            string nodeId = $"input:{i.ToString(CultureInfo.InvariantCulture)}:{inputPort.Name}";
            string title = inputPort.Name;
            string? subtitle = primaryTensor is not null ? FormatTensorSummary(primaryTensor) : null;

            double width = MeasureTerminalWidth(title, subtitle);
            double height = !string.IsNullOrEmpty(subtitle) ? TerminalPillHeightWithSubtitle : TerminalPillHeightSingle;
            LayoutPort outPort = new("out");

            LayoutNode layoutNode = new(
                Id: nodeId,
                Width: width,
                Height: height,
                InputPorts: [],
                OutputPorts: [outPort]);

            layoutNodes.Add(layoutNode);
            pendingNodes.Add(new PendingVisualNode(
                Id: nodeId,
                Kind: VisualNodeKind.GraphInput,
                GraphNode: null,
                BoundaryPort: inputPort,
                Title: title,
                Subtitle: subtitle,
                Category: OperatorCategory.Data,
                CollapsedParameters: [],
                FusedActivationBadge: null));

            for (int t = 0; t < inputPort.Tensors.Count; t++)
            {
                TensorEdge edge = inputPort.Tensors[t];
                if (!string.IsNullOrEmpty(edge.Name))
                {
                    producerByTensorName[edge.Name] = (nodeId, outPort.Id, edge);
                }
            }
        }

        // 2. First pass over operator nodes: register all operator output tensors in producerByTensorName
        //    so even non-topologically-ordered graphs resolve producers accurately.
        string[] operatorNodeIds = new string[subgraph.Nodes.Count];
        for (int i = 0; i < subgraph.Nodes.Count; i++)
        {
            GraphNode node = subgraph.Nodes[i];
            string nodeId = $"node:{i.ToString(CultureInfo.InvariantCulture)}:{node.Name}";
            operatorNodeIds[i] = nodeId;

            for (int p = 0; p < node.Outputs.Count; p++)
            {
                NodePort outPort = node.Outputs[p];
                string portId = $"out:{p.ToString(CultureInfo.InvariantCulture)}:{outPort.Name}";
                for (int t = 0; t < outPort.Tensors.Count; t++)
                {
                    TensorEdge tensor = outPort.Tensors[t];
                    if (!string.IsNullOrEmpty(tensor.Name))
                    {
                        producerByTensorName[tensor.Name] = (nodeId, portId, tensor);
                    }
                }
            }
        }

        // 3. Second pass over operator nodes: collapse Initializer/Constant inputs into parameter slots
        //    and emit runtime activation edges for non-initializer inputs.
        List<PendingVisualEdge> pendingEdges = [];
        List<LayoutEdge> layoutEdges = [];

        for (int i = 0; i < subgraph.Nodes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphNode node = subgraph.Nodes[i];
            string nodeId = operatorNodeIds[i];

            List<CollapsedParameterSlot> collapsedParams = [];
            List<LayoutPort> activeInputPorts = [];

            for (int p = 0; p < node.Inputs.Count; p++)
            {
                NodePort inPort = node.Inputs[p];
                if (inPort.Tensors.Count == 0)
                {
                    continue;
                }

                for (int t = 0; t < inPort.Tensors.Count; t++)
                {
                    TensorEdge tensor = inPort.Tensors[t];
                    bool hasNodeProducer = !string.IsNullOrEmpty(tensor.Name)
                        && producerByTensorName.ContainsKey(tensor.Name);

                    if ((tensor.Kind == TensorKind.Initializer || tensor.Kind == TensorKind.Constant) && !hasNodeProducer)
                    {
                        string summary = FormatTensorSummary(tensor);
                        bool isQuant = tensor.Descriptor.Quantization is not null;
                        collapsedParams.Add(new CollapsedParameterSlot(
                            PortName: inPort.Name,
                            Tensor: tensor,
                            DisplaySummary: summary,
                            IsQuantized: isQuant));
                        continue;
                    }

                    string inPortId = $"in:{p.ToString(CultureInfo.InvariantCulture)}:{t.ToString(CultureInfo.InvariantCulture)}:{inPort.Name}";
                    activeInputPorts.Add(new LayoutPort(inPortId));

                    if (!string.IsNullOrEmpty(tensor.Name)
                        && producerByTensorName.TryGetValue(tensor.Name, out (string NodeId, string PortId, TensorEdge Tensor) prod))
                    {
                        AddActivationEdge(
                            prod.NodeId,
                            prod.PortId,
                            nodeId,
                            inPortId,
                            tensor,
                            layoutEdges,
                            pendingEdges);
                    }
                }
            }

            List<LayoutPort> outputPorts = new(node.Outputs.Count);
            for (int p = 0; p < node.Outputs.Count; p++)
            {
                string outPortId = $"out:{p.ToString(CultureInfo.InvariantCulture)}:{node.Outputs[p].Name}";
                outputPorts.Add(new LayoutPort(outPortId));
            }

            string title = node.Operator.Name;
            string? subtitle = !string.IsNullOrWhiteSpace(node.Name)
                && !string.Equals(node.Name, title, StringComparison.OrdinalIgnoreCase)
                ? node.Name
                : null;

            string? fusedBadge = ExtractFusedActivationBadge(node);
            (double width, double height) = MeasureOperatorNodeBox(title, subtitle, collapsedParams, fusedBadge);

            layoutNodes.Add(new LayoutNode(
                Id: nodeId,
                Width: width,
                Height: height,
                InputPorts: activeInputPorts,
                OutputPorts: outputPorts));

            pendingNodes.Add(new PendingVisualNode(
                Id: nodeId,
                Kind: VisualNodeKind.Operator,
                GraphNode: node,
                BoundaryPort: null,
                Title: title,
                Subtitle: subtitle,
                Category: node.Operator.Category,
                CollapsedParameters: collapsedParams,
                FusedActivationBadge: fusedBadge));
        }

        // 4. Create compact terminal pills for Subgraph.Outputs and connect from producing nodes
        for (int i = 0; i < subgraph.Outputs.Count; i++)
        {
            GraphPort outputPort = subgraph.Outputs[i];
            TensorEdge? primaryTensor = outputPort.Tensors.Count > 0 ? outputPort.Tensors[0] : null;
            string nodeId = $"output:{i.ToString(CultureInfo.InvariantCulture)}:{outputPort.Name}";
            string title = outputPort.Name;
            string? subtitle = primaryTensor is not null ? FormatTensorSummary(primaryTensor) : null;

            double width = MeasureTerminalWidth(title, subtitle);
            double height = !string.IsNullOrEmpty(subtitle) ? TerminalPillHeightWithSubtitle : TerminalPillHeightSingle;
            LayoutPort inPort = new("in");

            layoutNodes.Add(new LayoutNode(
                Id: nodeId,
                Width: width,
                Height: height,
                InputPorts: [inPort],
                OutputPorts: []));

            pendingNodes.Add(new PendingVisualNode(
                Id: nodeId,
                Kind: VisualNodeKind.GraphOutput,
                GraphNode: null,
                BoundaryPort: outputPort,
                Title: title,
                Subtitle: subtitle,
                Category: OperatorCategory.Data,
                CollapsedParameters: [],
                FusedActivationBadge: null));

            for (int t = 0; t < outputPort.Tensors.Count; t++)
            {
                TensorEdge tensor = outputPort.Tensors[t];
                if (!string.IsNullOrEmpty(tensor.Name)
                    && producerByTensorName.TryGetValue(tensor.Name, out (string NodeId, string PortId, TensorEdge Tensor) prod))
                {
                    AddActivationEdge(
                        prod.NodeId,
                        prod.PortId,
                        nodeId,
                        inPort.Id,
                        tensor,
                        layoutEdges,
                        pendingEdges);
                }
            }
        }

        // 5. Run Sugiyama layout engine
        LayoutOptions effectiveOptions = options ?? new LayoutOptions
        {
            LayerSeparation = 68.0,
            NodeSeparation = 36.0,
            Margin = 32.0
        };

        LayoutGraph layoutGraph = new(layoutNodes, layoutEdges);
        PositionedGraph positioned = _layoutEngine.ComputeLayout(layoutGraph, effectiveOptions, cancellationToken);

        // 6. Zip PositionedNodes and PositionedEdges into VisualNodes and VisualEdges
        VisualNode[] visualNodes = new VisualNode[pendingNodes.Count];
        Dictionary<string, VisualNode> nodesById = new(pendingNodes.Count, StringComparer.Ordinal);

        for (int i = 0; i < pendingNodes.Count; i++)
        {
            PendingVisualNode p = pendingNodes[i];
            VisualNode vNode = new(
                Id: p.Id,
                Kind: p.Kind,
                GraphNode: p.GraphNode,
                BoundaryPort: p.BoundaryPort,
                Title: p.Title,
                Subtitle: p.Subtitle,
                Category: p.Category,
                CollapsedParameters: p.CollapsedParameters,
                FusedActivationBadge: p.FusedActivationBadge,
                Layout: positioned.Nodes[i]);

            visualNodes[i] = vNode;
            nodesById[vNode.Id] = vNode;
        }

        VisualEdge[] visualEdges = new VisualEdge[pendingEdges.Count];
        for (int e = 0; e < pendingEdges.Count; e++)
        {
            PendingVisualEdge pe = pendingEdges[e];
            visualEdges[e] = new VisualEdge(
                Id: pe.Id,
                Tensor: pe.Tensor,
                ShapeLabel: pe.ShapeLabel,
                IsQuantized: pe.IsQuantized,
                Layout: positioned.Edges[e]);
        }

        return new VisualSubgraphLayout(
            Subgraph: subgraph,
            PositionedGraph: positioned,
            Nodes: visualNodes,
            Edges: visualEdges,
            NodesById: nodesById);
    }

    private static string? ExtractFusedActivationBadge(GraphNode node)
    {
        for (int i = 0; i < node.Attributes.Count; i++)
        {
            NodeAttribute attr = node.Attributes[i];
            if (string.Equals(attr.Name, "delegate.backend", StringComparison.OrdinalIgnoreCase) &&
                attr.Value is AttributeValue.String deb)
            {
                return $"{deb.Value} · Expandable";
            }
        }

        for (int i = 0; i < node.Attributes.Count; i++)
        {
            NodeAttribute attr = node.Attributes[i];
            if (string.Equals(attr.Name, "fused_activation_function", StringComparison.OrdinalIgnoreCase)
                || string.Equals(attr.Name, "activation", StringComparison.OrdinalIgnoreCase))
            {
                if (attr.Value is AttributeValue.String strVal
                    && !string.IsNullOrWhiteSpace(strVal.Value)
                    && !string.Equals(strVal.Value, "NONE", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(strVal.Value, "linear", StringComparison.OrdinalIgnoreCase))
                {
                    return strVal.Value;
                }
            }
        }

        return null;
    }

    private static string FormatTensorSummary(TensorEdge tensor)
    {
        string typeStr = tensor.Descriptor.DataType.ToString().ToLowerInvariant();
        string? shapeStr = tensor.Descriptor.Shape?.ToDisplayString();

        return string.IsNullOrEmpty(shapeStr)
            ? typeStr
            : $"{typeStr} {shapeStr}";
    }

    private static string FormatEdgeShapeLabel(TensorEdge tensor)
    {
        string? shapeStr = tensor.Descriptor.Shape?.ToDisplayString();
        if (string.IsNullOrEmpty(shapeStr))
        {
            return string.Empty;
        }

        return tensor.Descriptor.Quantization is not null
            ? $"{shapeStr} ({tensor.Descriptor.DataType})"
            : shapeStr;
    }

    private void AddActivationEdge(
        string sourceNodeId,
        string sourcePortId,
        string targetNodeId,
        string targetPortId,
        TensorEdge tensor,
        List<LayoutEdge> layoutEdges,
        List<PendingVisualEdge> pendingEdges)
    {
        int idx = layoutEdges.Count;
        string edgeId = $"edge:{idx.ToString(CultureInfo.InvariantCulture)}:{tensor.Name}";
        string shapeLabel = FormatEdgeShapeLabel(tensor);
        bool isQuant = tensor.Descriptor.Quantization is not null;

        double labelWidth = !string.IsNullOrEmpty(shapeLabel)
            ? _textMeasurer.MeasureTextWidth(shapeLabel, EdgeLabelFontSize) + 12.0
            : 0.0;
        double labelHeight = !string.IsNullOrEmpty(shapeLabel) ? 18.0 : 0.0;

        layoutEdges.Add(new LayoutEdge(
            Id: edgeId,
            SourceNodeId: sourceNodeId,
            TargetNodeId: targetNodeId,
            SourcePortId: sourcePortId,
            TargetPortId: targetPortId,
            Label: string.IsNullOrEmpty(shapeLabel) ? null : shapeLabel,
            LabelWidth: labelWidth,
            LabelHeight: labelHeight));

        pendingEdges.Add(new PendingVisualEdge(
            Id: edgeId,
            Tensor: tensor,
            ShapeLabel: shapeLabel,
            IsQuantized: isQuant));
    }

    private double MeasureTerminalWidth(string title, string? subtitle)
    {
        double titleW = _textMeasurer.MeasureTextWidth(title, TitleFontSize, isBold: true);
        double subW = !string.IsNullOrEmpty(subtitle)
            ? _textMeasurer.MeasureTextWidth(subtitle, SubtitleFontSize)
            : 0.0;

        // Reserve 28px on left for circle indicator and spacing, plus 20px on right for border margin
        double rawWidth = Math.Max(titleW, subW) + 48.0;
        return Math.Clamp(rawWidth, MinTerminalPillWidth, MaxTerminalPillWidth);
    }

    private (double Width, double Height) MeasureOperatorNodeBox(
        string title,
        string? subtitle,
        List<CollapsedParameterSlot> collapsedParams,
        string? fusedBadge)
    {
        double titleW = _textMeasurer.MeasureTextWidth(title, TitleFontSize, isBold: true);
        double headerTotalW = titleW + 28.0;

        if (!string.IsNullOrEmpty(subtitle))
        {
            double subW = _textMeasurer.MeasureTextWidth(subtitle, SubtitleFontSize);
            // Title and subtitle sit side-by-side in header with 16px gap
            headerTotalW = titleW + 16.0 + subW + 28.0;
        }

        double maxContentW = headerTotalW;

        for (int i = 0; i < collapsedParams.Count; i++)
        {
            CollapsedParameterSlot slot = collapsedParams[i];
            double portW = _textMeasurer.MeasureTextWidth(slot.PortName, ParameterFontSize, isBold: true);
            double sumW = _textMeasurer.MeasureTextWidth(slot.DisplaySummary, ParameterFontSize);
            // port text (portW) + gap (18) + summary pill (sumW + 12 pill padding) + optional Q badge (26) + node margins (24)
            double rowW = portW + 18.0 + sumW + 12.0 + (slot.IsQuantized ? 26.0 : 0.0) + 24.0;
            if (rowW > maxContentW)
            {
                maxContentW = rowW;
            }
        }

        if (!string.IsNullOrEmpty(fusedBadge))
        {
            double badgeW = _textMeasurer.MeasureTextWidth($"+ {fusedBadge}", ParameterFontSize, isBold: true) + 32.0;
            if (badgeW > maxContentW)
            {
                maxContentW = badgeW;
            }
        }

        double width = Math.Clamp(maxContentW, MinOperatorNodeWidth, MaxOperatorNodeWidth);

        double height = BaseOperatorHeaderHeight;
        if (collapsedParams.Count > 0)
        {
            height += (collapsedParams.Count * ParameterRowHeight) + 8.0;
        }

        if (!string.IsNullOrEmpty(fusedBadge))
        {
            height += ActivationBadgeRowHeight;
        }

        height += OperatorVerticalPadding;
        return (width, height);
    }

    private sealed record PendingVisualNode(
        string Id,
        VisualNodeKind Kind,
        GraphNode? GraphNode,
        GraphPort? BoundaryPort,
        string Title,
        string? Subtitle,
        OperatorCategory Category,
        IReadOnlyList<CollapsedParameterSlot> CollapsedParameters,
        string? FusedActivationBadge);

    private sealed record PendingVisualEdge(
        string Id,
        TensorEdge Tensor,
        string ShapeLabel,
        bool IsQuantized);
}
