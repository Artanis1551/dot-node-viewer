using System.Text.Json;
using System.Text.Json.Serialization;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Cli;

internal sealed record CliTopologySnapshot(
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("subgraphCount")] int SubgraphCount,
    [property: JsonPropertyName("primarySubgraph")] CliSubgraphSnapshot PrimarySubgraph);

internal sealed record CliSubgraphSnapshot(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("nodeCount")] int NodeCount,
    [property: JsonPropertyName("operatorHistogram")] IReadOnlyDictionary<string, int> OperatorHistogram,
    [property: JsonPropertyName("inputs")] IReadOnlyList<CliPortTensorSnapshot> Inputs,
    [property: JsonPropertyName("outputs")] IReadOnlyList<CliPortTensorSnapshot> Outputs,
    [property: JsonPropertyName("sampleWeightSlice"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CliWeightSliceSnapshot? SampleWeightSlice = null);

internal sealed record CliPortTensorSnapshot(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dataType")] string DataType,
    [property: JsonPropertyName("shape")] IReadOnlyList<string>? Shape,
    [property: JsonPropertyName("quantization"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CliQuantizationSnapshot? Quantization = null);

internal sealed record CliQuantizationSnapshot(
    [property: JsonPropertyName("scale")] IReadOnlyList<float> Scale,
    [property: JsonPropertyName("zeroPoint")] IReadOnlyList<long> ZeroPoint,
    [property: JsonPropertyName("quantizedDimension"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? QuantizedDimension = null);

internal sealed record CliWeightSliceSnapshot(
    [property: JsonPropertyName("tensorName")] string TensorName,
    [property: JsonPropertyName("dataType")] string DataType,
    [property: JsonPropertyName("streamOffset")] long StreamOffset,
    [property: JsonPropertyName("byteLength")] long ByteLength);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliTopologySnapshot))]
[JsonSerializable(typeof(CliSubgraphSnapshot))]
[JsonSerializable(typeof(CliPortTensorSnapshot))]
[JsonSerializable(typeof(CliQuantizationSnapshot))]
[JsonSerializable(typeof(CliWeightSliceSnapshot))]
internal sealed partial class CliJsonContext : JsonSerializerContext;

internal static class CliSnapshotBuilder
{
    public static string BuildJson(ModelGraph graph, string modelId)
    {
        Subgraph? primary = graph.PrimarySubgraph;
        CliTopologySnapshot snapshot;

        if (primary is null)
        {
            snapshot = new CliTopologySnapshot(
                ModelId: modelId,
                Format: graph.Format.ToDisplayString(),
                SubgraphCount: 0,
                PrimarySubgraph: new CliSubgraphSnapshot(
                    Name: string.Empty,
                    NodeCount: 0,
                    OperatorHistogram: new Dictionary<string, int>(StringComparer.Ordinal),
                    Inputs: [],
                    Outputs: [],
                    SampleWeightSlice: null));
        }
        else
        {
            Dictionary<string, int> histogram = new(StringComparer.Ordinal);
            for (int i = 0; i < primary.Nodes.Count; i++)
            {
                string opName = primary.Nodes[i].Operator.Name;
                histogram[opName] = histogram.GetValueOrDefault(opName) + 1;
            }

            snapshot = new CliTopologySnapshot(
                ModelId: modelId,
                Format: graph.Format.ToDisplayString(),
                SubgraphCount: graph.Subgraphs.Count,
                PrimarySubgraph: new CliSubgraphSnapshot(
                    Name: primary.Name,
                    NodeCount: primary.Nodes.Count,
                    OperatorHistogram: histogram,
                    Inputs: BuildPortSnapshots(primary.Inputs),
                    Outputs: BuildPortSnapshots(primary.Outputs),
                    SampleWeightSlice: FindRepresentativeWeightSlice(primary)));
        }

        return JsonSerializer.Serialize(snapshot, CliJsonContext.Default.CliTopologySnapshot);
    }

    private static List<CliPortTensorSnapshot> BuildPortSnapshots(IReadOnlyList<GraphPort> ports)
    {
        List<CliPortTensorSnapshot> result = new(ports.Count);
        for (int i = 0; i < ports.Count; i++)
        {
            GraphPort port = ports[i];
            if (port.Tensors.Count == 0)
            {
                result.Add(new CliPortTensorSnapshot(port.Name, DataType.Unknown.ToString(), null, null));
                continue;
            }

            for (int t = 0; t < port.Tensors.Count; t++)
            {
                TensorEdge tensor = port.Tensors[t];
                string name = string.IsNullOrEmpty(tensor.Name) ? port.Name : tensor.Name;
                string[]? shape = null;
                if (tensor.Descriptor.Shape is { } s)
                {
                    shape = new string[s.Dimensions.Count];
                    for (int d = 0; d < s.Dimensions.Count; d++)
                    {
                        shape[d] = s.Dimensions[d].ToDisplayString();
                    }
                }

                CliQuantizationSnapshot? quant = null;
                if (tensor.Descriptor.Quantization is AffineQuantization aq && (aq.Scale.Count > 0 || aq.ZeroPoint.Count > 0))
                {
                    quant = new CliQuantizationSnapshot(aq.Scale, aq.ZeroPoint, aq.QuantizedDimension);
                }

                result.Add(new CliPortTensorSnapshot(name, tensor.Descriptor.DataType.ToString(), shape, quant));
            }
        }

        return result;
    }

    private static CliWeightSliceSnapshot? FindRepresentativeWeightSlice(Subgraph subgraph)
    {
        for (int i = 0; i < subgraph.Tensors.Count; i++)
        {
            TensorEdge tensor = subgraph.Tensors[i];
            if ((tensor.Kind == TensorKind.Initializer || tensor.Kind == TensorKind.Constant)
                && tensor.Data is TensorDataSlice { ByteLength: > 0 } slice)
            {
                return new CliWeightSliceSnapshot(tensor.Name, slice.DataType.ToString(), slice.StreamOffset, slice.ByteLength);
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
                        return new CliWeightSliceSnapshot(tensor.Name, slice.DataType.ToString(), slice.StreamOffset, slice.ByteLength);
                    }
                }
            }
        }

        return null;
    }
}

