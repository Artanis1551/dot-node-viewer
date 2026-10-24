using System.Text.Json;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Formats.Keras;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Formats;

/// <summary>
/// Model integration tests verifying Keras models (.keras and .h5) against independent Python ground truth.
/// </summary>
public sealed class KerasModelTests
{
    [Test]
    public async Task TinySequential_MatchesTruth_WiresLayersAndLazyWeightSlices()
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "keras", "tiny_sequential.keras");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "keras", "tiny_sequential.truth.json");

        await Assert.That(File.Exists(modelPath)).IsTrue();
        await Assert.That(File.Exists(truthPath)).IsTrue();

        await using FileStream fs = File.OpenRead(modelPath);
        ModelGraph graph = await KerasFormatReader.Instance.ReadAsync(fs);

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.Keras);
        await Assert.That(graph.Subgraphs.Count).IsEqualTo(1);

        Subgraph subgraph = graph.Subgraphs[0];
        await Assert.That(subgraph.Nodes.Count).IsEqualTo(2);

        // Ground truth verification
        string truthJson = await File.ReadAllTextAsync(truthPath);
        using JsonDocument doc = JsonDocument.Parse(truthJson);
        var primary = doc.RootElement.GetProperty("primarySubgraph");

        int expectedNodeCount = primary.GetProperty("nodeCount").GetInt32();
        await Assert.That(subgraph.Nodes.Count).IsEqualTo(expectedNodeCount);

        // Operator histogram
        var opHist = primary.GetProperty("operatorHistogram");
        var actualHist = subgraph.Nodes
            .GroupBy(n => n.Operator.Name)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var prop in opHist.EnumerateObject())
        {
            await Assert.That(actualHist.ContainsKey(prop.Name)).IsTrue();
            await Assert.That(actualHist[prop.Name]).IsEqualTo(prop.Value.GetInt32());
        }

        // Inputs & outputs
        await Assert.That(subgraph.Inputs.Count).IsEqualTo(1);
        await Assert.That(subgraph.Inputs[0].Name).IsEqualTo("input_1");
        await Assert.That(subgraph.Outputs.Count).IsEqualTo(1);
        await Assert.That(subgraph.Outputs[0].Name).IsEqualTo("dense_2");

        // Verify dense_1 weights
        var dense1 = subgraph.Nodes.First(n => n.Name == "dense_1");
        var kernelPort = dense1.Inputs.FirstOrDefault(p => p.Name == "kernel");
        await Assert.That(kernelPort).IsNotNull();
        var kernelEdge = kernelPort!.Tensors[0];
        await Assert.That(kernelEdge.Kind).IsEqualTo(TensorKind.Initializer);
        await Assert.That(kernelEdge.Data).IsNotNull();
        await Assert.That(kernelEdge.Data).IsTypeOf<TensorDataSlice>();

        var slice = (TensorDataSlice)kernelEdge.Data!;
        await Assert.That(slice.ByteLength).IsEqualTo(128); // 4*8 * 4
        await Assert.That(slice.DataType).IsEqualTo(DataType.Float32);
        await Assert.That(slice.StreamOffset).IsGreaterThan(0);
    }

    [Test]
    public async Task TinyFunctionalResidual_MatchesTruth_WiresResidualSkipConnection()
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "keras", "tiny_functional_residual.keras");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "keras", "tiny_functional_residual.truth.json");

        await Assert.That(File.Exists(modelPath)).IsTrue();
        await Assert.That(File.Exists(truthPath)).IsTrue();

        await using FileStream fs = File.OpenRead(modelPath);
        ModelGraph graph = await KerasFormatReader.Instance.ReadAsync(fs);

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.Keras);
        Subgraph subgraph = graph.Subgraphs[0];
        await Assert.That(subgraph.Nodes.Count).IsEqualTo(3);

        // Verify Add node has 2 inputs (input_1 and dense_res2)
        var addNode = subgraph.Nodes.First(n => n.Name == "add_skip");
        await Assert.That(addNode.Operator.Name).IsEqualTo("Add");
        await Assert.That(addNode.Inputs.Count).IsEqualTo(2);

        var inputNames = addNode.Inputs.SelectMany(p => p.Tensors).Select(t => t.Name).ToList();
        await Assert.That(inputNames.Contains("input_1:0")).IsTrue();
        await Assert.That(inputNames.Contains("dense_res2:0")).IsTrue();

        // Output must be add_skip
        await Assert.That(subgraph.Outputs.Count).IsEqualTo(1);
        await Assert.That(subgraph.Outputs[0].Name).IsEqualTo("add_skip");
    }

    [Test]
    public async Task TinyCnnLegacyH5_MatchesTruth_ExtractsModelConfigAttributeAndContiguousSlices()
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "keras", "tiny_cnn_legacy.h5");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "keras", "tiny_cnn_legacy.truth.json");

        await Assert.That(File.Exists(modelPath)).IsTrue();
        await Assert.That(File.Exists(truthPath)).IsTrue();

        await using FileStream fs = File.OpenRead(modelPath);
        ModelGraph graph = await KerasFormatReader.Instance.ReadAsync(fs);

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.Keras);
        Subgraph subgraph = graph.Subgraphs[0];
        await Assert.That(subgraph.Nodes.Count).IsEqualTo(3);

        // Verify Conv2D node and initializers
        var convNode = subgraph.Nodes.First(n => n.Name == "conv_1");
        await Assert.That(convNode.Operator.Name).IsEqualTo("Conv2D");

        var kernelPort = convNode.Inputs.FirstOrDefault(p => p.Name == "kernel");
        await Assert.That(kernelPort).IsNotNull();
        var kernelEdge = kernelPort!.Tensors[0];
        await Assert.That(kernelEdge.Data).IsNotNull();
        var slice = (TensorDataSlice)kernelEdge.Data!;
        await Assert.That(slice.ByteLength).IsEqualTo(72); // 3*3*1*2 * 4
        await Assert.That(slice.StreamOffset).IsGreaterThan(0);
    }

    [Test]
    public async Task TinyWeightsOnly_ExtractsSummaryGraphAndDiagnostic()
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "keras", "tiny_weights_only.weights.h5");

        await Assert.That(File.Exists(modelPath)).IsTrue();

        await using FileStream fs = File.OpenRead(modelPath);
        ModelGraph graph = await KerasFormatReader.Instance.ReadAsync(fs);

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.Keras);
        await Assert.That(graph.Diagnostics.Any(d => d.Code == "DNV_KERAS_WEIGHTS_ONLY")).IsTrue();

        Subgraph subgraph = graph.PrimarySubgraph!;
        await Assert.That(subgraph.Nodes.Count).IsGreaterThan(0);
        await Assert.That(subgraph.Tensors.Count).IsGreaterThan(0);
    }
}

