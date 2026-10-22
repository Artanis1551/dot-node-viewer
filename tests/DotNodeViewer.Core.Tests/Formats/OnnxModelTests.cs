using System.Text.Json;
using System.Text.Json.Serialization;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Formats;

/// <summary>
/// Format-specific integration and ground-truth verification tests for ONNX fixtures.
/// </summary>
public sealed class OnnxModelTests
{
    [Test]
    public async Task IfSubgraphFixture_ExtractsThenAndElseBranchSubgraphAttributes()
    {
        string path = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "onnx", "if_subgraph.onnx");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "if_subgraph.onnx" });

        Subgraph main = graph.PrimarySubgraph!;
        GraphIndex index = new(main);
        GraphNode? ifNode = index.GetNodeByName("if_0");

        await Assert.That(ifNode).IsNotNull();
        await Assert.That(ifNode!.Operator.Name).IsEqualTo("If");
        await Assert.That(ifNode.Operator.Category).IsEqualTo(OperatorCategory.ControlFlow);

        NodeAttribute? thenAttr = ifNode.Attributes.FirstOrDefault(a => a.Name == "then_branch");
        NodeAttribute? elseAttr = ifNode.Attributes.FirstOrDefault(a => a.Name == "else_branch");

        await Assert.That(thenAttr).IsNotNull();
        await Assert.That(thenAttr!.Value).IsTypeOf<AttributeValue.Graph>();
        Subgraph thenGraph = ((AttributeValue.Graph)thenAttr.Value).Value;
        await Assert.That(thenGraph.Name).IsEqualTo("then_graph");
        await Assert.That(thenGraph.Nodes.Count).IsEqualTo(1);
        await Assert.That(thenGraph.Nodes[0].Operator.Name).IsEqualTo("Relu");
        await Assert.That(thenGraph.Outputs.Count).IsEqualTo(1);
        await Assert.That(thenGraph.Outputs[0].Name).IsEqualTo("then_out");

        await Assert.That(elseAttr).IsNotNull();
        await Assert.That(elseAttr!.Value).IsTypeOf<AttributeValue.Graph>();
        Subgraph elseGraph = ((AttributeValue.Graph)elseAttr.Value).Value;
        await Assert.That(elseGraph.Name).IsEqualTo("else_graph");
        await Assert.That(elseGraph.Nodes.Count).IsEqualTo(1);
        await Assert.That(elseGraph.Nodes[0].Operator.Name).IsEqualTo("Neg");
        await Assert.That(elseGraph.Outputs.Count).IsEqualTo(1);
        await Assert.That(elseGraph.Outputs[0].Name).IsEqualTo("else_out");
    }

    [Test]
    public async Task ExternalDataFixture_RecordsExternalReferenceAndInlineSlice()
    {
        string path = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "onnx", "external_data.onnx");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "external_data.onnx" });

        Subgraph main = graph.PrimarySubgraph!;
        GraphIndex index = new(main);

        TensorEdge? extWeight = index.GetTensorByName("ext_w");
        await Assert.That(extWeight).IsNotNull();
        await Assert.That(extWeight!.Kind).IsEqualTo(TensorKind.Initializer);
        await Assert.That(extWeight.Descriptor.DataType).IsEqualTo(DataType.Float32);
        await Assert.That(extWeight.Descriptor.Shape!.ToDisplayString()).IsEqualTo("[4, 2]");
        await Assert.That(extWeight.Data).IsTypeOf<ExternalTensorDataReference>();

        ExternalTensorDataReference extRef = (ExternalTensorDataReference)extWeight.Data!;
        await Assert.That(extRef.RelativePath).IsEqualTo("external_data.bin");
        await Assert.That(extRef.Offset).IsEqualTo(0L);
        await Assert.That(extRef.Length).IsEqualTo(32L);
        await Assert.That(extRef.DataType).IsEqualTo(DataType.Float32);

        TensorEdge? inlineBias = index.GetTensorByName("inline_b");
        await Assert.That(inlineBias).IsNotNull();
        await Assert.That(inlineBias!.Data).IsTypeOf<TensorDataSlice>();

        TensorDataSlice inlineSlice = (TensorDataSlice)inlineBias.Data!;
        await Assert.That(inlineSlice.ByteLength).IsEqualTo(8L);
        byte[] buffer = new byte[8];
        int bytesRead = TensorDataLoader.ReadSlice(stream, inlineSlice, buffer);
        await Assert.That(bytesRead).IsEqualTo(8);
    }

    [Test]
    [Arguments("tiny_mlp")]
    [Arguments("tiny_cnn_dynamic_batch")]
    [Arguments("if_subgraph")]
    [Arguments("external_data")]
    public async Task CommittedOnnxFixtures_CrossCheckAgainstPythonGroundTruth(string stem)
    {
        string onnxDir = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "onnx");
        string modelPath = Path.Combine(onnxDir, $"{stem}.onnx");
        string expectedPath = Path.Combine(onnxDir, $"{stem}.expected.json");
        string truthPath = Path.Combine(onnxDir, $"{stem}.truth.json");

        await using FileStream modelStream = new(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            modelStream,
            new ModelReadOptions { FileName = $"{stem}.onnx" });

        await using FileStream expectedStream = new(expectedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelTopologySnapshot expected = JsonSerializer.Deserialize(
            expectedStream,
            HarnessJsonContext.Default.ModelTopologySnapshot)!;

        await using FileStream truthStream = new(truthPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        OnnxTruthDocument truth = JsonSerializer.Deserialize(
            truthStream,
            OnnxTruthJsonContext.Default.OnnxTruthDocument)!;

        // Verify expected snapshot matches independent Python ground truth
        await Assert.That(expected.ModelId).IsEqualTo(truth.ModelId);
        await Assert.That(expected.Format).IsEqualTo(truth.Format);
        await Assert.That(expected.SubgraphCount).IsEqualTo(truth.SubgraphCount);
        await Assert.That(expected.PrimarySubgraph.Name).IsEqualTo(truth.PrimarySubgraph.Name);
        await Assert.That(expected.PrimarySubgraph.NodeCount).IsEqualTo(truth.PrimarySubgraph.NodeCount);

        await Assert.That(expected.PrimarySubgraph.OperatorHistogram.Count)
            .IsEqualTo(truth.PrimarySubgraph.OperatorHistogram.Count);
        foreach (KeyValuePair<string, int> kvp in truth.PrimarySubgraph.OperatorHistogram)
        {
            await Assert.That(expected.PrimarySubgraph.OperatorHistogram[kvp.Key]).IsEqualTo(kvp.Value);
        }

        await Assert.That(expected.PrimarySubgraph.Inputs.Count).IsEqualTo(truth.PrimarySubgraph.Inputs.Count);
        for (int i = 0; i < truth.PrimarySubgraph.Inputs.Count; i++)
        {
            await Assert.That(expected.PrimarySubgraph.Inputs[i].Name).IsEqualTo(truth.PrimarySubgraph.Inputs[i].Name);
            await Assert.That(expected.PrimarySubgraph.Inputs[i].DataType).IsEqualTo(truth.PrimarySubgraph.Inputs[i].DataType);
        }

        await Assert.That(expected.PrimarySubgraph.Outputs.Count).IsEqualTo(truth.PrimarySubgraph.Outputs.Count);
        for (int i = 0; i < truth.PrimarySubgraph.Outputs.Count; i++)
        {
            await Assert.That(expected.PrimarySubgraph.Outputs[i].Name).IsEqualTo(truth.PrimarySubgraph.Outputs[i].Name);
            await Assert.That(expected.PrimarySubgraph.Outputs[i].DataType).IsEqualTo(truth.PrimarySubgraph.Outputs[i].DataType);
        }

        // Verify parsed initializers match Python ground-truth initializers
        GraphIndex index = new(graph.PrimarySubgraph!);
        foreach (OnnxTruthInitializer truthInit in truth.PrimarySubgraph.Initializers)
        {
            TensorEdge? edge = index.GetTensorByName(truthInit.Name);
            await Assert.That(edge).IsNotNull();
            await Assert.That(edge!.Kind).IsEqualTo(TensorKind.Initializer);
            await Assert.That(edge.Descriptor.DataType.ToString()).IsEqualTo(truthInit.DataType);

            if (truthInit.IsExternal)
            {
                await Assert.That(edge.Data).IsTypeOf<ExternalTensorDataReference>();
                ExternalTensorDataReference ext = (ExternalTensorDataReference)edge.Data!;
                await Assert.That(ext.RelativePath).IsEqualTo(truthInit.ExternalLocation);
                await Assert.That(ext.Length).IsEqualTo(truthInit.ByteLength);
            }
            else
            {
                await Assert.That(edge.Data).IsTypeOf<TensorDataSlice>();
                TensorDataSlice slice = (TensorDataSlice)edge.Data!;
                await Assert.That(slice.ByteLength).IsEqualTo(truthInit.ByteLength);
            }
        }
    }

    [Test]
    public async Task MobileNetV2_ParseAllocationBudget_AllocatesLessThanTenPercentOfWeightByteLength()
    {
        ModelManifestDocument manifest = ModelFixtureLoader.LoadManifest();
        ModelManifestEntry entry = manifest.Models.First(m => m.Id == "onnx-mobilenetv2-7");

        await using FileStream? stream = await ModelFixtureLoader.OpenVerifiedModelStreamAsync(entry);
        await Assert.That(stream).IsNotNull();

        ModelReadOptions options = new() { FileName = entry.FileName };
        // Warm up JIT once before measuring thread allocations.
        _ = await ModelFormatRegistry.Default.ReadAsync(stream!, options);
        stream!.Position = 0;

        (ModelGraph graph, long allocatedBytes) = await AllocationProbe.MeasureAsync(
            () => ModelFormatRegistry.Default.ReadAsync(stream, options));

        Subgraph main = graph.PrimarySubgraph!;
        long totalWeightBytes = 0;
        for (int i = 0; i < main.Tensors.Count; i++)
        {
            if (main.Tensors[i].Data is TensorDataSlice slice)
            {
                totalWeightBytes += slice.ByteLength;
            }
        }

        await Assert.That(totalWeightBytes > 10_000_000L).IsTrue();
        await Assert.That(allocatedBytes < totalWeightBytes / 10).IsTrue();
    }
}

internal sealed record OnnxTruthDocument(
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("subgraphCount")] int SubgraphCount,
    [property: JsonPropertyName("primarySubgraph")] OnnxTruthSubgraph PrimarySubgraph);

internal sealed record OnnxTruthSubgraph(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("nodeCount")] int NodeCount,
    [property: JsonPropertyName("operatorHistogram")] IReadOnlyDictionary<string, int> OperatorHistogram,
    [property: JsonPropertyName("inputs")] IReadOnlyList<TensorPortSnapshot> Inputs,
    [property: JsonPropertyName("outputs")] IReadOnlyList<TensorPortSnapshot> Outputs,
    [property: JsonPropertyName("initializers")] IReadOnlyList<OnnxTruthInitializer> Initializers);

internal sealed record OnnxTruthInitializer(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dataType")] string DataType,
    [property: JsonPropertyName("shape")] IReadOnlyList<string> Shape,
    [property: JsonPropertyName("byteLength")] long ByteLength,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("externalLocation")] string? ExternalLocation);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OnnxTruthDocument))]
internal sealed partial class OnnxTruthJsonContext : JsonSerializerContext;
