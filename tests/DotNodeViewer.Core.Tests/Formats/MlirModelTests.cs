using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Formats;

/// <summary>
/// Format-specific integration and ground-truth verification tests for committed MLIR fixtures.
/// </summary>
public sealed class MlirModelTests
{
    [Test]
    [Arguments("tiny_stablehlo", ".mlir")]
    [Arguments("tiny_tosa", ".mlir")]
    [Arguments("tiny_torch_quant", ".mlir")]
    [Arguments("tiny_triton_kernel", ".mlir")]
    [Arguments("tiny_stablehlo_bytecode", ".mlirbc")]
    public async Task CommittedMlirFixtures_CrossCheckAgainstPythonGroundTruth(string stem, string extension)
    {
        string mlirDir = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "mlir");
        string modelPath = Path.Combine(mlirDir, $"{stem}{extension}");
        string expectedPath = Path.Combine(mlirDir, $"{stem}.expected.json");
        string truthPath = Path.Combine(mlirDir, $"{stem}.truth.json");

        await using FileStream modelStream = new(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            modelStream,
            new ModelReadOptions { FileName = $"{stem}{extension}" });

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.Mlir);

        await using FileStream expectedStream = new(expectedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelTopologySnapshot expected = JsonSerializer.Deserialize(
            expectedStream,
            HarnessJsonContext.Default.ModelTopologySnapshot)!;

        await using FileStream truthStream = new(truthPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        MlirTruthDocument truth = JsonSerializer.Deserialize(
            truthStream,
            MlirTruthJsonContext.Default.MlirTruthDocument)!;

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
    }

    [Test]
    public async Task TinyStableHloTextual_InfersSingleDynamicDimensionAndNestedScfIfBranches()
    {
        string path = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "mlir", "tiny_stablehlo.mlir");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(stream, new ModelReadOptions { FileName = "tiny_stablehlo.mlir" });
        Subgraph main = graph.PrimarySubgraph!;
        GraphIndex index = new(main);

        TensorEdge? weightEdge = index.GetTensorByName("%w");
        await Assert.That(weightEdge).IsNotNull();
        await Assert.That(weightEdge!.Kind).IsEqualTo(TensorKind.Constant);
        await Assert.That(weightEdge.Descriptor.Shape!.ToDisplayString()).IsEqualTo("[2, 4]");

        GraphNode? ifNode = main.Nodes.FirstOrDefault(n => n.Operator.Name == "scf.if");
        await Assert.That(ifNode).IsNotNull();
        NodeAttribute? thenAttr = ifNode!.Attributes.FirstOrDefault(a => a.Name == "then_branch");
        NodeAttribute? elseAttr = ifNode.Attributes.FirstOrDefault(a => a.Name == "else_branch");
        await Assert.That(thenAttr).IsNotNull();
        await Assert.That(elseAttr).IsNotNull();
        await Assert.That(((AttributeValue.Graph)thenAttr!.Value).Value.Nodes.Count).IsEqualTo(1);
        await Assert.That(((AttributeValue.Graph)thenAttr.Value).Value.Nodes[0].Operator.Name).IsEqualTo("stablehlo.negate");
    }

    [Test]
    public async Task TinyTosa_ExtractsUniformQuantizationParameters()
    {
        string path = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "mlir", "tiny_tosa.mlir");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(stream, new ModelReadOptions { FileName = "tiny_tosa.mlir" });
        Subgraph main = graph.PrimarySubgraph!;
        TensorDescriptor inputDesc = main.Inputs[0].Tensors[0].Descriptor;

        await Assert.That(inputDesc.DataType).IsEqualTo(DataType.Int8);
        await Assert.That(inputDesc.Quantization).IsTypeOf<AffineQuantization>();
        AffineQuantization quant = (AffineQuantization)inputDesc.Quantization!;
        await Assert.That(quant.Scale[0]).IsEqualTo(0.05f);
        await Assert.That(quant.ZeroPoint[0]).IsEqualTo(128L);
    }

    [Test]
    public async Task TinyTorchQuantAndTriton_ClassifyDialectCategoriesAccurately()
    {
        string torchPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "mlir", "tiny_torch_quant.mlir");
        await using (FileStream stream = new(torchPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ModelGraph torchGraph = await ModelFormatRegistry.Default.ReadAsync(stream, new ModelReadOptions { FileName = "tiny_torch_quant.mlir" });
            Subgraph forward = torchGraph.PrimarySubgraph!;
            await Assert.That(forward.Nodes.Any(n =>
                n.Operator.Name == "torch.quantized_decomposed.quantize_per_tensor"
                && n.Operator.Category == OperatorCategory.Quantization
                && n.Operator.Domain == "torch.quantized_decomposed")).IsTrue();
            await Assert.That(forward.Nodes.Any(n =>
                n.Operator.Name == "torch.torchao.dequantize_affine"
                && n.Operator.Category == OperatorCategory.Quantization
                && n.Operator.Domain == "torch.torchao")).IsTrue();
        }

        string tritonPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "mlir", "tiny_triton_kernel.mlir");
        await using (FileStream stream = new(tritonPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ModelGraph tritonGraph = await ModelFormatRegistry.Default.ReadAsync(stream, new ModelReadOptions { FileName = "tiny_triton_kernel.mlir" });
            Subgraph kernel = tritonGraph.PrimarySubgraph!;
            await Assert.That(kernel.Name).IsEqualTo("fused_gemm_kernel");
            await Assert.That(kernel.Nodes.Any(n => n.Operator.Name == "tt.dot" && n.Operator.Category == OperatorCategory.Layer)).IsTrue();
            await Assert.That(kernel.Nodes.Any(n => n.Operator.Name == "tt.load" && n.Operator.Category == OperatorCategory.Data)).IsTrue();
            await Assert.That(kernel.Nodes.Any(n => n.Operator.Name == "tt.store" && n.Operator.Category == OperatorCategory.Data)).IsTrue();
        }
    }

    [Test]
    public async Task TinyStableHloBytecode_ReadsLazyResourceAndDenseFloatSlices()
    {
        string path = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "mlir", "tiny_stablehlo_bytecode.mlirbc");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "tiny_stablehlo_bytecode.mlirbc" });

        await Assert.That(graph.Producer).IsEqualTo("dot-node-viewer-gen");
        await Assert.That(graph.Format.ToDisplayString()).IsEqualTo("MLIR Bytecode v6");

        Subgraph main = graph.PrimarySubgraph!;
        TensorEdge? weightEdge = main.Tensors.FirstOrDefault(t => t.Data is TensorDataSlice { ByteLength: 32 });
        await Assert.That(weightEdge).IsNotNull();
        await Assert.That(weightEdge!.Descriptor.Shape!.ToDisplayString()).IsEqualTo("[2, 4]");

        TensorDataSlice weightSlice = (TensorDataSlice)weightEdge.Data!;
        byte[] weightBuf = new byte[32];
        int read = TensorDataLoader.ReadSlice(stream, weightSlice, weightBuf);
        await Assert.That(read).IsEqualTo(32);
        await Assert.That(BinaryPrimitives.ReadSingleLittleEndian(weightBuf.AsSpan(0, 4))).IsEqualTo(1.0f);
        await Assert.That(BinaryPrimitives.ReadSingleLittleEndian(weightBuf.AsSpan(8, 4))).IsEqualTo(-3.5f);

        TensorEdge? biasEdge = main.Tensors.FirstOrDefault(t => t.Data is TensorDataSlice { ByteLength: 16 });
        await Assert.That(biasEdge).IsNotNull();
        TensorDataSlice biasSlice = (TensorDataSlice)biasEdge!.Data!;
        byte[] biasBuf = new byte[16];
        int biasRead = TensorDataLoader.ReadSlice(stream, biasSlice, biasBuf);
        await Assert.That(biasRead).IsEqualTo(16);
        await Assert.That(Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(biasBuf.AsSpan(0, 4)) - 0.1f)).IsLessThan(1e-6f);
    }
}

internal sealed record MlirTruthDocument(
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("subgraphCount")] int SubgraphCount,
    [property: JsonPropertyName("primarySubgraph")] MlirTruthSubgraph PrimarySubgraph);

internal sealed record MlirTruthSubgraph(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("nodeCount")] int NodeCount,
    [property: JsonPropertyName("operatorHistogram")] IReadOnlyDictionary<string, int> OperatorHistogram,
    [property: JsonPropertyName("inputs")] IReadOnlyList<TensorPortSnapshot> Inputs,
    [property: JsonPropertyName("outputs")] IReadOnlyList<TensorPortSnapshot> Outputs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MlirTruthDocument))]
internal sealed partial class MlirTruthJsonContext : JsonSerializerContext;
