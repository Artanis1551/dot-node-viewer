using System.Text.Json;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Formats.PyTorch;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Formats;

/// <summary>
/// Model integration tests verifying PyTorch TorchScript archives and state_dict checkpoints against independent Python ground truth.
/// </summary>
public sealed class PyTorchModelTests
{
    [Test]
    public async Task TracedModel_MatchesTruth_InlinesLayersAndExtractsLazyWeights()
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "pytorch", "tiny_cnn_traced.pt");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "pytorch", "tiny_cnn_traced.truth.json");

        await Assert.That(File.Exists(modelPath)).IsTrue();
        await Assert.That(File.Exists(truthPath)).IsTrue();

        await using FileStream fs = File.OpenRead(modelPath);
        ModelGraph graph = await PyTorchFormatReader.Instance.ReadAsync(fs);

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.PyTorch);
        await Assert.That(graph.Subgraphs.Count).IsEqualTo(1);

        Subgraph subgraph = graph.Subgraphs[0];
        await Assert.That(subgraph.Nodes.Count).IsEqualTo(5);

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

        // Verify conv1 weights and lazy slices
        var conv1 = subgraph.Nodes.First(n => n.Name == "conv1");
        var weightEdge = conv1.Inputs.First(p => p.Name == "weight").Tensors[0];
        await Assert.That(weightEdge.Descriptor.DataType).IsEqualTo(DataType.Float32);
        await Assert.That(weightEdge.Descriptor.Shape).IsNotNull();
        await Assert.That(weightEdge.Descriptor.Shape!.Dimensions.Count).IsEqualTo(4);
        await Assert.That(((TensorDimension.Fixed)weightEdge.Descriptor.Shape.Dimensions[0]).Value).IsEqualTo(2);
        await Assert.That(((TensorDimension.Fixed)weightEdge.Descriptor.Shape.Dimensions[1]).Value).IsEqualTo(1);
        await Assert.That(((TensorDimension.Fixed)weightEdge.Descriptor.Shape.Dimensions[2]).Value).IsEqualTo(3);
        await Assert.That(((TensorDimension.Fixed)weightEdge.Descriptor.Shape.Dimensions[3]).Value).IsEqualTo(3);

        await Assert.That(weightEdge.Data).IsNotNull();
        var slice = (TensorDataSlice)weightEdge.Data!;
        await Assert.That(slice.StreamOffset > 0).IsTrue();
        await Assert.That(slice.ByteLength).IsEqualTo(72);
        await Assert.That(slice.StreamOffset + slice.ByteLength <= fs.Length).IsTrue();
    }

    [Test]
    public async Task ScriptedIfModel_EmitsControlFlowDiagnosticAndModuleHierarchy()
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "pytorch", "tiny_cnn_scripted_if.pt");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "pytorch", "tiny_cnn_scripted_if.truth.json");

        await Assert.That(File.Exists(modelPath)).IsTrue();
        await Assert.That(File.Exists(truthPath)).IsTrue();

        await using FileStream fs = File.OpenRead(modelPath);
        ModelGraph graph = await PyTorchFormatReader.Instance.ReadAsync(fs);

        // Must emit control-flow diagnostic
        await Assert.That(graph.Diagnostics.Any(d => d.Code == "DNV_TORCH_CONTROL_FLOW")).IsTrue();
        var diag = graph.Diagnostics.First(d => d.Code == "DNV_TORCH_CONTROL_FLOW");
        await Assert.That(diag.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diag.Message.Contains("control flow")).IsTrue();

        // Must gracefully fall back to module hierarchy with 3 nodes
        Subgraph subgraph = graph.Subgraphs[0];
        await Assert.That(subgraph.Nodes.Count).IsEqualTo(3);
        var opNames = subgraph.Nodes.Select(n => n.Operator.Name).ToHashSet();
        await Assert.That(opNames.Contains("Conv2d")).IsTrue();
        await Assert.That(opNames.Contains("ReLU")).IsTrue();
        await Assert.That(opNames.Contains("Linear")).IsTrue();
    }

    [Test]
    public async Task StateDictModel_GroupsParametersIntoModuleNodesWithoutFakeActivations()
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "pytorch", "tiny_cnn_state_dict.pt");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "pytorch", "tiny_cnn_state_dict.truth.json");

        await Assert.That(File.Exists(modelPath)).IsTrue();
        await Assert.That(File.Exists(truthPath)).IsTrue();

        await using FileStream fs = File.OpenRead(modelPath);
        ModelGraph graph = await PyTorchFormatReader.Instance.ReadAsync(fs);

        // Must attach informational diagnostic
        await Assert.That(graph.Diagnostics.Any(d => d.Code == "DNV_TORCH_STATE_DICT")).IsTrue();
        var diag = graph.Diagnostics.First(d => d.Code == "DNV_TORCH_STATE_DICT");
        await Assert.That(diag.Severity).IsEqualTo(DiagnosticSeverity.Info);

        // No fake activation edges produced between nodes
        Subgraph subgraph = graph.Subgraphs[0];
        await Assert.That(subgraph.Inputs.Count).IsEqualTo(0);
        await Assert.That(subgraph.Outputs.Count).IsEqualTo(0);

        // Grouped module nodes
        var nodeNames = subgraph.Nodes.Select(n => n.Name).ToHashSet();
        await Assert.That(nodeNames.Contains("conv1")).IsTrue();
        await Assert.That(nodeNames.Contains("bn1")).IsTrue();
        await Assert.That(nodeNames.Contains("fc")).IsTrue();

        var conv1 = subgraph.Nodes.First(n => n.Name == "conv1");
        await Assert.That(conv1.Inputs.Any(p => p.Name == "weight")).IsTrue();
        await Assert.That(conv1.Inputs.Any(p => p.Name == "bias")).IsTrue();

        var weight = conv1.Inputs.First(p => p.Name == "weight").Tensors[0];
        await Assert.That(weight.Data).IsNotNull();
        var slice = (TensorDataSlice)weight.Data!;
        await Assert.That(slice.ByteLength).IsEqualTo(72);
    }
}
