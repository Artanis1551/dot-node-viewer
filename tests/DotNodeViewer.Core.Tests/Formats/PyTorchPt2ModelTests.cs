using System.Text.Json;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Formats.PyTorchPt2;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Formats;

/// <summary>
/// Model integration tests verifying PyTorch 2 <c>torch.export</c> (<c>.pt2</c>) models against independent Python ground truth.
/// </summary>
public sealed class PyTorchPt2ModelTests
{
    [Test]
    public async Task TinyCnnExport_MatchesTruth_SeparatesInitializersAndLazyWeightSlices()
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "pytorch_pt2", "tiny_cnn_export.pt2");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "pytorch_pt2", "tiny_cnn_export.truth.json");

        await Assert.That(File.Exists(modelPath)).IsTrue();
        await Assert.That(File.Exists(truthPath)).IsTrue();

        await using FileStream fs = File.OpenRead(modelPath);
        ModelGraph graph = await PyTorchPt2FormatReader.Instance.ReadAsync(fs);

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.PyTorchPt2);
        await Assert.That(graph.Subgraphs.Count).IsEqualTo(1);

        Subgraph subgraph = graph.Subgraphs[0];
        await Assert.That(subgraph.Nodes.Count).IsEqualTo(4);

        // Ground truth verification
        string truthJson = await File.ReadAllTextAsync(truthPath);
        using JsonDocument doc = JsonDocument.Parse(truthJson);
        var primary = doc.RootElement.GetProperty("primarySubgraph");

        int expectedNodeCount = primary.GetProperty("nodeCount").GetInt32();
        await Assert.That(subgraph.Nodes.Count).IsEqualTo(expectedNodeCount);

        // Verify operator histogram
        var opHist = primary.GetProperty("operatorHistogram");
        var actualHist = subgraph.Nodes
            .GroupBy(n => n.Operator.Name)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var prop in opHist.EnumerateObject())
        {
            await Assert.That(actualHist.ContainsKey(prop.Name)).IsTrue();
            await Assert.That(actualHist[prop.Name]).IsEqualTo(prop.Value.GetInt32());
        }

        // Verify user inputs vs initializers
        await Assert.That(subgraph.Inputs.Count).IsEqualTo(1);
        await Assert.That(subgraph.Inputs[0].Name).IsEqualTo("x");
        await Assert.That(subgraph.Inputs[0].Tensors[0].Kind).IsEqualTo(TensorKind.GraphInput);

        // Outputs
        await Assert.That(subgraph.Outputs.Count).IsEqualTo(1);
        await Assert.That(subgraph.Outputs[0].Name).IsEqualTo("linear");
        await Assert.That(subgraph.Outputs[0].Tensors[0].Kind).IsEqualTo(TensorKind.GraphOutput);

        // Parameters as Initializers with lazy weight slices
        var pConvWeight = subgraph.Tensors.FirstOrDefault(t => t.Name == "p_conv_weight");
        await Assert.That(pConvWeight).IsNotNull();
        await Assert.That(pConvWeight!.Kind).IsEqualTo(TensorKind.Initializer);
        await Assert.That(pConvWeight.Data).IsNotNull();
        await Assert.That(pConvWeight.Data).IsTypeOf<TensorDataSlice>();
        var slice = (TensorDataSlice)pConvWeight.Data!;
        await Assert.That(slice.ByteLength).IsEqualTo(72); // 2*1*3*3 * 4
        await Assert.That(slice.DataType).IsEqualTo(DataType.Float32);

        var pFcWeight = subgraph.Tensors.FirstOrDefault(t => t.Name == "p_fc_weight");
        await Assert.That(pFcWeight).IsNotNull();
        await Assert.That(pFcWeight!.Kind).IsEqualTo(TensorKind.Initializer);
        await Assert.That(pFcWeight.Data).IsNotNull();
        var fcSlice = (TensorDataSlice)pFcWeight.Data!;
        await Assert.That(fcSlice.ByteLength).IsEqualTo(384); // 3*32 * 4
    }

    [Test]
    public async Task DynamicBatchExport_ExtractsSymbolicDimensionsAndRangeConstraints()
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "pytorch_pt2", "tiny_dynamic_batch_export.pt2");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "pytorch_pt2", "tiny_dynamic_batch_export.truth.json");

        await Assert.That(File.Exists(modelPath)).IsTrue();
        await Assert.That(File.Exists(truthPath)).IsTrue();

        await using FileStream fs = File.OpenRead(modelPath);
        ModelGraph graph = await PyTorchPt2FormatReader.Instance.ReadAsync(fs);

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.PyTorchPt2);
        Subgraph subgraph = graph.Subgraphs[0];

        // Verify inputs with symbolic dimension
        await Assert.That(subgraph.Inputs.Count).IsEqualTo(1);
        var inputTensor = subgraph.Inputs[0].Tensors[0];
        await Assert.That(inputTensor.Descriptor.Shape).IsNotNull();
        await Assert.That(inputTensor.Descriptor.Shape!.Rank).IsEqualTo(2);

        // First dimension must be symbolic
        TensorDimension dim0 = inputTensor.Descriptor.Shape.Dimensions[0];
        await Assert.That(dim0).IsTypeOf<TensorDimension.Symbolic>();
        string symName = ((TensorDimension.Symbolic)dim0).Name;

        // Verify range constraints in metadata
        string rcKey = $"range_constraints.{symName}";
        await Assert.That(graph.Metadata.ContainsKey(rcKey)).IsTrue();
        await Assert.That(graph.Metadata[rcKey]).IsEqualTo("[1, 32]");

        // Initializer p_fc_weight
        var pWeight = subgraph.Tensors.First(t => t.Name == "p_fc_weight");
        await Assert.That(pWeight.Kind).IsEqualTo(TensorKind.Initializer);
        await Assert.That(pWeight.Data).IsNotNull();
        var slice = (TensorDataSlice)pWeight.Data!;
        await Assert.That(slice.ByteLength).IsEqualTo(32); // 2*4 * 4
    }
}

