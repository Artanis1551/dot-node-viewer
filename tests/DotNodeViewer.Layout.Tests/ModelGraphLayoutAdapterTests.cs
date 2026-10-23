using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Layout.Tests;

/// <summary>
/// Integration tests for <see cref="ModelGraphLayoutAdapter"/> across real ONNX and TensorFlow Lite model fixtures.
/// </summary>
public sealed class ModelGraphLayoutAdapterTests
{
    [Test]
    [Arguments("onnx", "tiny_mlp.onnx")]
    [Arguments("onnx", "tiny_cnn_dynamic_batch.onnx")]
    [Arguments("onnx", "if_subgraph.onnx")]
    [Arguments("tflite", "tiny_cnn_float.tflite")]
    [Arguments("tflite", "tiny_cnn_int8.tflite")]
    [Arguments("tflite", "tiny_while.tflite")]
    public async Task CreateLayout_CommittedOnnxAndTfliteFixtures_ProducesNonOverlappingVisualSubgraphLayout(
        string formatFolder,
        string fileName)
    {
        string fixturePath = ResolveFixturePath(formatFolder, fileName);
        await using FileStream stream = new(fixturePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph modelGraph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = fileName });

        VisualSubgraphLayout layout = ModelGraphLayoutAdapter.Default.CreateLayout(modelGraph);
        Subgraph primary = modelGraph.PrimarySubgraph!;

        // Expected visual node count = GraphInputs + OperatorNodes + GraphOutputs (Initializers are collapsed into operator nodes!)
        int expectedVisualNodes = primary.Inputs.Count + primary.Nodes.Count + primary.Outputs.Count;
        await Assert.That(layout.Nodes.Count).IsEqualTo(expectedVisualNodes);
        await Assert.That(layout.NodesById.Count).IsEqualTo(expectedVisualNodes);
        await Assert.That(layout.Edges.Count > 0).IsTrue();

        // Verify no two VisualNode bounding boxes intersect
        for (int i = 0; i < layout.Nodes.Count; i++)
        {
            for (int j = i + 1; j < layout.Nodes.Count; j++)
            {
                bool intersects = layout.Nodes[i].Bounds.Intersects(layout.Nodes[j].Bounds);
                await Assert.That(intersects).IsFalse();
            }
        }

        // Verify GraphInput pills sit at the top and GraphOutput pills sit below operator nodes
        int inputCount = layout.Nodes.Count(n => n.Kind == VisualNodeKind.GraphInput);
        int outputCount = layout.Nodes.Count(n => n.Kind == VisualNodeKind.GraphOutput);
        await Assert.That(inputCount).IsEqualTo(primary.Inputs.Count);
        await Assert.That(outputCount).IsEqualTo(primary.Outputs.Count);
    }

    [Test]
    public async Task CreateLayout_TinyCnnInt8Tflite_CollapsesQuantizedInitializersAndSurfacesFusedActivationBadges()
    {
        string fixturePath = ResolveFixturePath("tflite", "tiny_cnn_int8.tflite");
        await using FileStream stream = new(fixturePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph modelGraph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "tiny_cnn_int8.tflite" });

        VisualSubgraphLayout layout = ModelGraphLayoutAdapter.Default.CreateLayout(modelGraph);

        VisualNode? convVisualNode = layout.Nodes.FirstOrDefault(
            n => n.Kind == VisualNodeKind.Operator && n.Title == "CONV_2D");
        await Assert.That(convVisualNode).IsNotNull();
        await Assert.That(convVisualNode!.Category).IsEqualTo(OperatorCategory.Layer);
        await Assert.That(convVisualNode.FusedActivationBadge).IsEqualTo("RELU");

        // CONV_2D has filter + bias initializers collapsed into parameter rows with IsQuantized == true
        await Assert.That(convVisualNode.CollapsedParameters.Count).IsEqualTo(2);
        await Assert.That(convVisualNode.CollapsedParameters[0].PortName).IsEqualTo("filter");
        await Assert.That(convVisualNode.CollapsedParameters[0].IsQuantized).IsTrue();
        await Assert.That(convVisualNode.CollapsedParameters[0].DisplaySummary.Contains('Q', StringComparison.Ordinal)).IsTrue();

        // Activation edges in INT8 model are marked IsQuantized == true
        await Assert.That(layout.Edges.All(e => e.IsQuantized)).IsTrue();
    }

    private static string ResolveFixturePath(string formatFolder, string fileName)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "dot-node-viewer.slnx")))
        {
            dir = dir.Parent;
        }

        string repoRoot = dir?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root containing 'dot-node-viewer.slnx'.");

        return Path.Combine(
            repoRoot,
            "tests",
            "DotNodeViewer.Core.Tests",
            "Fixtures",
            formatFolder,
            fileName);
    }
}
