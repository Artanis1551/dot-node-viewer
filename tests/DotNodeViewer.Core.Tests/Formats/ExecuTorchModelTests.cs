using System.Text.Json;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Formats;

/// <summary>
/// Format-specific integration and ground-truth verification tests for ExecuTorch (<c>.pte</c>) fixtures.
/// </summary>
public sealed class ExecuTorchModelTests
{
    [Test]
    public async Task TinyMlpPortableFixture_ExtractsPortableOperatorsAndLazyWeightSlices()
    {
        string ptePath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "executorch", "tiny_mlp_portable.pte");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "executorch", "tiny_mlp_portable.truth.json");

        await using FileStream stream = new(ptePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "tiny_mlp_portable.pte" });

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.ExecuTorch);
        await Assert.That(graph.Format.DisplayName).IsEqualTo("ExecuTorch");

        Subgraph main = graph.PrimarySubgraph!;
        await Assert.That(main.Name).IsEqualTo("forward");
        await Assert.That(main.Nodes.Count).IsEqualTo(6);
        await Assert.That(main.Inputs.Count).IsEqualTo(1);
        await Assert.That(main.Outputs.Count).IsEqualTo(1);

        // Verify all initializer weight tensors have valid lazy TensorDataSlices
        List<TensorEdge> initializers = main.Tensors.Where(t => t.Kind == TensorKind.Initializer).ToList();
        await Assert.That(initializers.Count).IsEqualTo(5);

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

        // Cross-check against truth JSON
        if (File.Exists(truthPath))
        {
            string truthJson = await File.ReadAllTextAsync(truthPath);
            using JsonDocument truthDoc = JsonDocument.Parse(truthJson);
            JsonElement primary = truthDoc.RootElement.GetProperty("primarySubgraph");

            int expectedNodes = primary.GetProperty("nodeCount").GetInt32();
            await Assert.That(main.Nodes.Count).IsEqualTo(expectedNodes);

            JsonElement truthInputs = primary.GetProperty("inputs");
            await Assert.That(main.Inputs.Count).IsEqualTo(truthInputs.GetArrayLength());

            JsonElement truthOutputs = primary.GetProperty("outputs");
            await Assert.That(main.Outputs.Count).IsEqualTo(truthOutputs.GetArrayLength());
        }
    }

    [Test]
    public async Task TinyCnnXnnpackFixture_ExtractsDelegateCallAndProcessedPayload()
    {
        string ptePath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "executorch", "tiny_cnn_xnnpack.pte");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "executorch", "tiny_cnn_xnnpack.truth.json");

        await using FileStream stream = new(ptePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "tiny_cnn_xnnpack.pte" });

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.ExecuTorch);

        Subgraph main = graph.PrimarySubgraph!;
        await Assert.That(main.Nodes.Count).IsEqualTo(1);

        GraphNode delegateNode = main.Nodes[0];
        await Assert.That(delegateNode.Operator.Name).IsEqualTo("Delegate (XnnpackBackend)");
        await Assert.That(delegateNode.Operator.Category).IsEqualTo(OperatorCategory.Custom);

        NodeAttribute? processedAttr = delegateNode.Attributes.FirstOrDefault(a => a.Name == "processed");
        await Assert.That(processedAttr).IsNotNull();
        await Assert.That(processedAttr!.Value).IsTypeOf<AttributeValue.Bytes>();

        AttributeValue.Bytes payloadBytes = (AttributeValue.Bytes)processedAttr.Value;
        await Assert.That(payloadBytes.Slice.ByteLength > 0).IsTrue();
        await Assert.That(payloadBytes.Slice.StreamOffset > 0).IsTrue();

        byte[] buf = new byte[payloadBytes.Slice.ByteLength];
        int read = TensorDataLoader.ReadSlice(stream, payloadBytes.Slice, buf);
        await Assert.That(read).IsEqualTo((int)payloadBytes.Slice.ByteLength);

        // Cross-check against truth JSON
        if (File.Exists(truthPath))
        {
            string truthJson = await File.ReadAllTextAsync(truthPath);
            using JsonDocument truthDoc = JsonDocument.Parse(truthJson);
            JsonElement primary = truthDoc.RootElement.GetProperty("primarySubgraph");

            int expectedNodes = primary.GetProperty("nodeCount").GetInt32();
            await Assert.That(main.Nodes.Count).IsEqualTo(expectedNodes);
        }
    }
}

