using System.Text.Json;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Formats;

/// <summary>
/// End-to-end integration and ground-truth verification tests for ExecuTorch <c>XnnpackBackend</c> delegate unwrapping.
/// </summary>
public sealed class XnnpackDelegateModelTests
{
    [Test]
    [Arguments("tiny_cnn_xnnpack")]
    [Arguments("tiny_xnnpack_ops")]
    [Arguments("tiny_cnn_xnnpack_int8")]
    public async Task XnnpackFixtures_ExpandDelegateSubgraph_MatchingGroundTruthAndLazyWeights(string fixtureStem)
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "executorch", $"{fixtureStem}.pte");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "executorch", $"{fixtureStem}.truth.json");

        await using FileStream stream = new(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(stream, new ModelReadOptions { FileName = $"{fixtureStem}.pte" });

        Subgraph main = graph.PrimarySubgraph!;
        GraphNode? delegateNode = main.Nodes.FirstOrDefault(n => n.Operator.Name.Contains("Xnnpack", StringComparison.OrdinalIgnoreCase));
        await Assert.That(delegateNode).IsNotNull();

        await Assert.That(delegateNode!.Attributes.Any(a => a.Name == "delegate.backend" && a.Value is AttributeValue.String s && s.Value == "xnnpack")).IsTrue();
        await Assert.That(delegateNode.Attributes.Any(a => a.Name == "delegate.expandable" && a.Value is AttributeValue.Bool b && b.Value)).IsTrue();

        IDelegateSubgraphExpander? expander = DelegateSubgraphExpanderRegistry.Default.FindExpander(graph, delegateNode);
        await Assert.That(expander).IsNotNull();
        await Assert.That(expander!.BackendId).IsEqualTo("xnnpack");

        Subgraph? expanded = await expander.ExpandAsync(stream, graph, delegateNode);
        await Assert.That(expanded).IsNotNull();
        await Assert.That(expanded!.Name).Contains("(XNNPACK)");

        using JsonDocument truthDoc = JsonDocument.Parse(await File.ReadAllTextAsync(truthPath));
        JsonElement delegateElem = truthDoc.RootElement.GetProperty("delegate");

        int expectedNodes = delegateElem.GetProperty("nodeCount").GetInt32();
        int expectedValues = delegateElem.GetProperty("valueCount").GetInt32();
        int expectedConsts = delegateElem.GetProperty("constantCount").GetInt32();
        int expectedQuantized = delegateElem.GetProperty("quantizedValueCount").GetInt32();

        await Assert.That(expanded.Nodes.Count).IsEqualTo(expectedNodes);
        await Assert.That(expanded.Tensors.Count).IsEqualTo(expectedValues);

        int actualConsts = expanded.Tensors.Count(t => t.Kind == TensorKind.Initializer);
        int actualQuantized = expanded.Tensors.Count(t => t.Descriptor.Quantization is not null);
        await Assert.That(actualConsts).IsEqualTo(expectedConsts);
        await Assert.That(actualQuantized).IsEqualTo(expectedQuantized);

        // Verify operator histogram matches ground truth
        Dictionary<string, int> actualHistogram = new(StringComparer.Ordinal);
        foreach (GraphNode node in expanded.Nodes)
        {
            actualHistogram[node.Operator.Name] = actualHistogram.GetValueOrDefault(node.Operator.Name) + 1;
        }

        foreach (JsonProperty prop in delegateElem.GetProperty("operatorHistogram").EnumerateObject())
        {
            await Assert.That(actualHistogram.GetValueOrDefault(prop.Name)).IsEqualTo(prop.Value.GetInt32());
        }

        // Verify input/output shapes & types match ground truth
        JsonElement truthInputs = delegateElem.GetProperty("inputs");
        await Assert.That(expanded.Inputs.Count).IsEqualTo(truthInputs.GetArrayLength());
        for (int i = 0; i < expanded.Inputs.Count; i++)
        {
            TensorEdge edge = expanded.Inputs[i].Tensors[0];
            JsonElement expectedIn = truthInputs[i];
            await Assert.That(edge.Descriptor.DataType.ToString()).IsEqualTo(expectedIn.GetProperty("dataType").GetString());
            await AssertShapeMatchesAsync(edge.Descriptor.Shape, expectedIn.GetProperty("shape"));
        }

        JsonElement truthOutputs = delegateElem.GetProperty("outputs");
        await Assert.That(expanded.Outputs.Count).IsEqualTo(truthOutputs.GetArrayLength());
        for (int i = 0; i < expanded.Outputs.Count; i++)
        {
            TensorEdge edge = expanded.Outputs[i].Tensors[0];
            JsonElement expectedOut = truthOutputs[i];
            await Assert.That(edge.Descriptor.DataType.ToString()).IsEqualTo(expectedOut.GetProperty("dataType").GetString());
            await AssertShapeMatchesAsync(edge.Descriptor.Shape, expectedOut.GetProperty("shape"));
        }

        // Verify every constant initializer has a readable lazy TensorDataSlice inside the .pte stream
        List<TensorEdge> initializers = expanded.Tensors.Where(t => t.Kind == TensorKind.Initializer).ToList();
        await Assert.That(initializers.Count).IsGreaterThan(0);

        foreach (TensorEdge init in initializers)
        {
            await Assert.That(init.Data).IsTypeOf<TensorDataSlice>();
            TensorDataSlice slice = (TensorDataSlice)init.Data!;
            await Assert.That(slice.StreamOffset).IsGreaterThan(0L);
            await Assert.That(slice.ByteLength).IsGreaterThan(0L);

            byte[] buf = new byte[slice.ByteLength];
            int read = TensorDataLoader.ReadSlice(stream, slice, buf);
            await Assert.That(read).IsEqualTo((int)slice.ByteLength);
        }
    }

    [Test]
    public async Task TinyCnnXnnpackInt8_DecodesPerTensorAndPerChannelAffineQuantization()
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "executorch", "tiny_cnn_xnnpack_int8.pte");
        await using FileStream stream = new(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(stream, new ModelReadOptions { FileName = "tiny_cnn_xnnpack_int8.pte" });

        GraphNode delegateNode = graph.PrimarySubgraph!.Nodes[0];
        Subgraph? expanded = await DelegateSubgraphExpanderRegistry.Default.ExpandAsync(stream, graph, delegateNode);
        await Assert.That(expanded).IsNotNull();

        List<AffineQuantization> affineInfos = expanded!.Tensors
            .Select(t => t.Descriptor.Quantization)
            .OfType<AffineQuantization>()
            .ToList();

        await Assert.That(affineInfos.Count).IsEqualTo(11);
        await Assert.That(affineInfos.Any(a => a.QuantizedDimension is null && a.Scale.Count == 1 && a.Scale[0] > 0f)).IsTrue();
        await Assert.That(affineInfos.Any(a => a.QuantizedDimension is not null && a.Scale.Count > 1 && a.Scale.All(s => s > 0f))).IsTrue();
    }

    private static async Task AssertShapeMatchesAsync(TensorShape? actualShape, JsonElement expectedArray)
    {
        await Assert.That(actualShape).IsNotNull();
        await Assert.That(actualShape!.Dimensions.Count).IsEqualTo(expectedArray.GetArrayLength());

        for (int d = 0; d < actualShape.Dimensions.Count; d++)
        {
            TensorDimension dim = actualShape.Dimensions[d];
            await Assert.That(dim).IsTypeOf<TensorDimension.Fixed>();
            await Assert.That(((TensorDimension.Fixed)dim).Value).IsEqualTo(expectedArray[d].GetInt64());
        }
    }
}
