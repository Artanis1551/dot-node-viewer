using System.Text.Json;
using System.Text.Json.Serialization;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Formats;

/// <summary>
/// Format-specific integration and ground-truth verification tests for TensorFlow Lite (<c>TFL3</c>) fixtures.
/// </summary>
public sealed partial class TfliteModelTests
{
    [Test]
    public async Task TinyCnnFloatFixture_ExtractsFusedActivationsAndLazyWeightSlices()
    {
        string path = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "tflite", "tiny_cnn_float.tflite");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "tiny_cnn_float.tflite" });

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.Tflite);
        await Assert.That(graph.Format.ToDisplayString()).IsEqualTo("TensorFlow Lite v3");

        Subgraph main = graph.PrimarySubgraph!;
        await Assert.That(main.Nodes.Count).IsEqualTo(5);

        GraphNode convNode = main.Nodes[0];
        await Assert.That(convNode.Operator.Name).IsEqualTo("CONV_2D");
        NodeAttribute? convAct = convNode.Attributes.FirstOrDefault(a => a.Name == "fused_activation_function");
        await Assert.That(convAct).IsNotNull();
        await Assert.That(((AttributeValue.String)convAct!.Value).Value).IsEqualTo("RELU");

        GraphNode dwNode = main.Nodes[1];
        await Assert.That(dwNode.Operator.Name).IsEqualTo("DEPTHWISE_CONV_2D");
        NodeAttribute? dwAct = dwNode.Attributes.FirstOrDefault(a => a.Name == "fused_activation_function");
        await Assert.That(dwAct).IsNotNull();
        await Assert.That(((AttributeValue.String)dwAct!.Value).Value).IsEqualTo("RELU");

        // Verify all initializer weight tensors have non-empty lazy TensorDataSlices that can be read via TensorDataLoader
        List<TensorEdge> initializers = main.Tensors.Where(t => t.Kind == TensorKind.Initializer).ToList();
        await Assert.That(initializers.Count).IsEqualTo(4);
        for (int i = 0; i < initializers.Count; i++)
        {
            await Assert.That(initializers[i].Data).IsTypeOf<TensorDataSlice>();
            TensorDataSlice slice = (TensorDataSlice)initializers[i].Data!;
            await Assert.That(slice.StreamOffset > 0).IsTrue();
            await Assert.That(slice.ByteLength > 0).IsTrue();
            byte[] buf = new byte[slice.ByteLength];
            int read = TensorDataLoader.ReadSlice(stream, slice, buf);
            await Assert.That(read).IsEqualTo((int)slice.ByteLength);
        }
    }

    [Test]
    public async Task TinyCnnInt8Fixture_ExtractsPerChannelAffineQuantizationParameters()
    {
        string path = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "tflite", "tiny_cnn_int8.tflite");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "tiny_cnn_int8.tflite" });

        Subgraph main = graph.PrimarySubgraph!;

        // Input tensor has per-tensor INT8 AffineQuantization
        TensorEdge inputTensor = main.Inputs[0].Tensors[0];
        await Assert.That(inputTensor.Descriptor.DataType).IsEqualTo(DataType.Int8);
        await Assert.That(inputTensor.Descriptor.Quantization).IsTypeOf<AffineQuantization>();
        AffineQuantization inputQuant = (AffineQuantization)inputTensor.Descriptor.Quantization!;
        await Assert.That(inputQuant.Scale.Count).IsEqualTo(1);
        await Assert.That(inputQuant.ZeroPoint.Count).IsEqualTo(1);

        // DepthwiseConv2D filter (tfl.pseudo_qconst1) has per-channel INT8 quantization along axis 3 (4 channels)
        TensorEdge? dwFilter = main.Tensors.FirstOrDefault(t => t.Name == "tfl.pseudo_qconst1");
        await Assert.That(dwFilter).IsNotNull();
        await Assert.That(dwFilter!.Kind).IsEqualTo(TensorKind.Initializer);
        await Assert.That(dwFilter.Descriptor.DataType).IsEqualTo(DataType.Int8);
        await Assert.That(dwFilter.Descriptor.Quantization).IsTypeOf<AffineQuantization>();

        AffineQuantization dwQuant = (AffineQuantization)dwFilter.Descriptor.Quantization!;
        await Assert.That(dwQuant.Scale.Count).IsEqualTo(4);
        await Assert.That(dwQuant.ZeroPoint.Count).IsEqualTo(4);
        await Assert.That(dwQuant.QuantizedDimension).IsEqualTo(3);

        // Conv2D filter (tfl.pseudo_qconst4) has per-channel INT8 quantization along axis 0 (4 channels)
        TensorEdge? convFilter = main.Tensors.FirstOrDefault(t => t.Name == "tfl.pseudo_qconst4");
        await Assert.That(convFilter).IsNotNull();
        AffineQuantization convQuant = (AffineQuantization)convFilter!.Descriptor.Quantization!;
        await Assert.That(convQuant.Scale.Count).IsEqualTo(4);
        await Assert.That(convQuant.ZeroPoint.Count).IsEqualTo(4);
        await Assert.That(convQuant.QuantizedDimension).IsEqualTo(0);
    }

    [Test]
    public async Task TinyWhileFixture_ExtractsCondAndBodyNestedSubgraphAttributes()
    {
        string path = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "tflite", "tiny_while.tflite");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "tiny_while.tflite" });

        await Assert.That(graph.Subgraphs.Count).IsEqualTo(3);

        Subgraph main = graph.PrimarySubgraph!;
        await Assert.That(main.Nodes.Count).IsEqualTo(1);

        GraphNode whileNode = main.Nodes[0];
        await Assert.That(whileNode.Operator.Name).IsEqualTo("WHILE");
        await Assert.That(whileNode.Operator.Category).IsEqualTo(OperatorCategory.ControlFlow);

        NodeAttribute? condAttr = whileNode.Attributes.FirstOrDefault(a => a.Name == "cond_subgraph");
        NodeAttribute? bodyAttr = whileNode.Attributes.FirstOrDefault(a => a.Name == "body_subgraph");

        await Assert.That(condAttr).IsNotNull();
        await Assert.That(condAttr!.Value).IsTypeOf<AttributeValue.Graph>();
        Subgraph condGraph = ((AttributeValue.Graph)condAttr.Value).Value;
        await Assert.That(condGraph.Nodes.Count > 0).IsTrue();
        await Assert.That(condGraph.Nodes.Any(n => n.Operator.Name == "LESS")).IsTrue();

        await Assert.That(bodyAttr).IsNotNull();
        await Assert.That(bodyAttr!.Value).IsTypeOf<AttributeValue.Graph>();
        Subgraph bodyGraph = ((AttributeValue.Graph)bodyAttr.Value).Value;
        await Assert.That(bodyGraph.Nodes.Count > 0).IsTrue();
        await Assert.That(bodyGraph.Nodes.Any(n => n.Operator.Name == "ADD")).IsTrue();
    }

    [Test]
    [Arguments("tiny_cnn_float")]
    [Arguments("tiny_cnn_int8")]
    [Arguments("tiny_while")]
    public async Task CommittedTfliteFixtures_CrossCheckAgainstPythonGroundTruth(string stem)
    {
        string tfliteDir = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "tflite");
        string modelPath = Path.Combine(tfliteDir, $"{stem}.tflite");
        string expectedPath = Path.Combine(tfliteDir, $"{stem}.expected.json");
        string truthPath = Path.Combine(tfliteDir, $"{stem}.truth.json");

        await using FileStream modelStream = new(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            modelStream,
            new ModelReadOptions { FileName = $"{stem}.tflite" });

        await using FileStream expectedStream = new(expectedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelTopologySnapshot expected = JsonSerializer.Deserialize(
            expectedStream,
            HarnessJsonContext.Default.ModelTopologySnapshot)!;

        await using FileStream truthStream = new(truthPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        TfliteTruthDocument truth = JsonSerializer.Deserialize(
            truthStream,
            TfliteTruthJsonContext.Default.TfliteTruthDocument)!;

        await Assert.That(expected.ModelId).IsEqualTo(truth.ModelId);
        await Assert.That(expected.Format).IsEqualTo(truth.Format);
        await Assert.That(expected.SubgraphCount).IsEqualTo(truth.SubgraphCount);
        await Assert.That(expected.PrimarySubgraph.NodeCount).IsEqualTo(truth.PrimarySubgraph.NodeCount);
        await Assert.That(expected.PrimarySubgraph.OperatorHistogram.Count).IsEqualTo(truth.PrimarySubgraph.OperatorHistogram.Count);

        foreach (KeyValuePair<string, int> kvp in truth.PrimarySubgraph.OperatorHistogram)
        {
            await Assert.That(expected.PrimarySubgraph.OperatorHistogram[kvp.Key]).IsEqualTo(kvp.Value);
        }

        // Cross-check initializers in parsed ModelGraph against Python ground truth
        Subgraph primary = graph.PrimarySubgraph!;
        List<TensorEdge> parsedInits = primary.Tensors.Where(t => t.Kind == TensorKind.Initializer).ToList();
        await Assert.That(parsedInits.Count).IsEqualTo(truth.PrimarySubgraph.Initializers.Count);

        for (int i = 0; i < truth.PrimarySubgraph.Initializers.Count; i++)
        {
            TfliteTruthInitializer truthInit = truth.PrimarySubgraph.Initializers[i];
            TensorEdge? actualInit = parsedInits.FirstOrDefault(t => t.Name == truthInit.Name);
            await Assert.That(actualInit).IsNotNull();
            await Assert.That(actualInit!.Descriptor.DataType.ToString()).IsEqualTo(truthInit.DataType);
            await Assert.That(actualInit.Data).IsTypeOf<TensorDataSlice>();

            TensorDataSlice slice = (TensorDataSlice)actualInit.Data!;
            await Assert.That(slice.ByteLength).IsEqualTo(truthInit.ByteLength);

            if (truthInit.Quantization is not null)
            {
                await Assert.That(actualInit.Descriptor.Quantization).IsTypeOf<AffineQuantization>();
                AffineQuantization actualQuant = (AffineQuantization)actualInit.Descriptor.Quantization!;
                await Assert.That(actualQuant.Scale.Count).IsEqualTo(truthInit.Quantization.Scale.Count);
                await Assert.That(actualQuant.ZeroPoint.Count).IsEqualTo(truthInit.Quantization.ZeroPoint.Count);
                if (truthInit.Quantization.Scale.Count > 1)
                {
                    await Assert.That(actualQuant.QuantizedDimension).IsEqualTo(truthInit.Quantization.QuantizedDimension);
                }
            }
        }
    }

    internal sealed record TfliteTruthDocument(
        [property: JsonPropertyName("modelId")] string ModelId,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("subgraphCount")] int SubgraphCount,
        [property: JsonPropertyName("primarySubgraph")] TfliteTruthSubgraph PrimarySubgraph);

    internal sealed record TfliteTruthSubgraph(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("nodeCount")] int NodeCount,
        [property: JsonPropertyName("operatorHistogram")] Dictionary<string, int> OperatorHistogram,
        [property: JsonPropertyName("initializers")] List<TfliteTruthInitializer> Initializers);

    internal sealed record TfliteTruthInitializer(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("dataType")] string DataType,
        [property: JsonPropertyName("shape")] List<string>? Shape,
        [property: JsonPropertyName("byteLength")] long ByteLength,
        [property: JsonPropertyName("quantization")] TfliteTruthQuantization? Quantization);

    internal sealed record TfliteTruthQuantization(
        [property: JsonPropertyName("scale")] List<float> Scale,
        [property: JsonPropertyName("zeroPoint")] List<long> ZeroPoint,
        [property: JsonPropertyName("quantizedDimension")] int? QuantizedDimension);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(TfliteTruthDocument))]
    internal sealed partial class TfliteTruthJsonContext : JsonSerializerContext;
}

