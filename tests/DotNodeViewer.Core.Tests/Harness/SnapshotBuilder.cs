using System.Text.Json;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Tests.Harness;

/// <summary>
/// Constructs deterministic <see cref="ModelTopologySnapshot"/> objects and JSON strings from a parsed <see cref="ModelGraph"/>.
/// </summary>
public static class SnapshotBuilder
{
    /// <summary>
    /// Builds a <see cref="ModelTopologySnapshot"/> for the specified <paramref name="graph"/> and <paramref name="modelId"/>.
    /// </summary>
    /// <param name="graph">The parsed <see cref="ModelGraph"/> to summarize.</param>
    /// <param name="modelId">The unique model identifier recorded in the snapshot.</param>
    /// <returns>The constructed <see cref="ModelTopologySnapshot"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="modelId"/> is <see langword="null"/>.</exception>
    public static ModelTopologySnapshot Build(ModelGraph graph, string modelId)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(modelId);

        Subgraph? primary = graph.PrimarySubgraph;
        if (primary is null)
        {
            return new ModelTopologySnapshot(
                ModelId: modelId,
                Format: graph.Format.ToDisplayString(),
                SubgraphCount: 0,
                PrimarySubgraph: new SubgraphTopologySnapshot(
                    Name: string.Empty,
                    NodeCount: 0,
                    OperatorHistogram: new Dictionary<string, int>(StringComparer.Ordinal),
                    Inputs: [],
                    Outputs: [],
                    SampleWeightSlice: null));
        }

        Dictionary<string, int> histogram = new(StringComparer.Ordinal);
        for (int i = 0; i < primary.Nodes.Count; i++)
        {
            string opName = primary.Nodes[i].Operator.Name;
            histogram[opName] = histogram.GetValueOrDefault(opName) + 1;
        }

        IReadOnlyList<TensorPortSnapshot> inputs = BuildPortSnapshots(primary.Inputs);
        IReadOnlyList<TensorPortSnapshot> outputs = BuildPortSnapshots(primary.Outputs);
        WeightSliceSnapshot? sampleWeightSlice = FindRepresentativeWeightSlice(primary);

        return new ModelTopologySnapshot(
            ModelId: modelId,
            Format: graph.Format.ToDisplayString(),
            SubgraphCount: graph.Subgraphs.Count,
            PrimarySubgraph: new SubgraphTopologySnapshot(
                Name: primary.Name,
                NodeCount: primary.Nodes.Count,
                OperatorHistogram: histogram,
                Inputs: inputs,
                Outputs: outputs,
                SampleWeightSlice: sampleWeightSlice));
    }

    /// <summary>
    /// Serializes a <see cref="ModelTopologySnapshot"/> to a formatted JSON string using source-generated metadata.
    /// </summary>
    /// <param name="snapshot">The snapshot to serialize.</param>
    /// <returns>The indented JSON representation of <paramref name="snapshot"/>.</returns>
    public static string ToJson(ModelTopologySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, HarnessJsonContext.Default.ModelTopologySnapshot);
    }

    /// <summary>
    /// Deserializes a <see cref="ModelTopologySnapshot"/> from a JSON string using source-generated metadata.
    /// </summary>
    /// <param name="json">The JSON string to parse.</param>
    /// <returns>The deserialized <see cref="ModelTopologySnapshot"/>.</returns>
    public static ModelTopologySnapshot FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize(json, HarnessJsonContext.Default.ModelTopologySnapshot)
            ?? throw new InvalidOperationException("Failed to deserialize ModelTopologySnapshot from JSON.");
    }

    private static List<TensorPortSnapshot> BuildPortSnapshots(IReadOnlyList<GraphPort> ports)
    {
        List<TensorPortSnapshot> result = new(ports.Count);
        for (int i = 0; i < ports.Count; i++)
        {
            GraphPort port = ports[i];
            if (port.Tensors.Count == 0)
            {
                result.Add(new TensorPortSnapshot(
                    Name: port.Name,
                    DataType: DataType.Unknown.ToString(),
                    Shape: null,
                    Quantization: null));
                continue;
            }

            for (int t = 0; t < port.Tensors.Count; t++)
            {
                TensorEdge tensor = port.Tensors[t];
                string name = string.IsNullOrEmpty(tensor.Name) ? port.Name : tensor.Name;
                IReadOnlyList<string>? shape = FormatShape(tensor.Descriptor.Shape);
                QuantizationSnapshot? quantization = FormatQuantization(tensor.Descriptor.Quantization);

                result.Add(new TensorPortSnapshot(
                    Name: name,
                    DataType: tensor.Descriptor.DataType.ToString(),
                    Shape: shape,
                    Quantization: quantization));
            }
        }

        return result;
    }

    private static string[]? FormatShape(TensorShape? shape)
    {
        if (shape is null)
        {
            return null;
        }

        string[] dims = new string[shape.Dimensions.Count];
        for (int i = 0; i < shape.Dimensions.Count; i++)
        {
            dims[i] = shape.Dimensions[i].ToDisplayString();
        }

        return dims;
    }

    private static QuantizationSnapshot? FormatQuantization(QuantizationInfo? quantization)
    {
        if (quantization is AffineQuantization affine && (affine.Scale.Count > 0 || affine.ZeroPoint.Count > 0))
        {
            return new QuantizationSnapshot(affine.Scale, affine.ZeroPoint, affine.QuantizedDimension);
        }

        return null;
    }

    private static WeightSliceSnapshot? FindRepresentativeWeightSlice(Subgraph subgraph)
    {
        for (int i = 0; i < subgraph.Tensors.Count; i++)
        {
            TensorEdge tensor = subgraph.Tensors[i];
            if ((tensor.Kind == TensorKind.Initializer || tensor.Kind == TensorKind.Constant)
                && tensor.Data is TensorDataSlice { ByteLength: > 0 } slice)
            {
                return new WeightSliceSnapshot(
                    TensorName: tensor.Name,
                    DataType: slice.DataType.ToString(),
                    StreamOffset: slice.StreamOffset,
                    ByteLength: slice.ByteLength);
            }
        }

        for (int i = 0; i < subgraph.Nodes.Count; i++)
        {
            GraphNode node = subgraph.Nodes[i];
            for (int p = 0; p < node.Inputs.Count; p++)
            {
                IReadOnlyList<TensorEdge> tensors = node.Inputs[p].Tensors;
                for (int t = 0; t < tensors.Count; t++)
                {
                    TensorEdge tensor = tensors[t];
                    if (tensor.Data is TensorDataSlice { ByteLength: > 0 } slice)
                    {
                        return new WeightSliceSnapshot(
                            TensorName: tensor.Name,
                            DataType: slice.DataType.ToString(),
                            StreamOffset: slice.StreamOffset,
                            ByteLength: slice.ByteLength);
                    }
                }
            }
        }

        return null;
    }
}

