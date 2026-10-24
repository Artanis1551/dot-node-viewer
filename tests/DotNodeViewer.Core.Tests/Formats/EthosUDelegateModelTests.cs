using System.Buffers;
using System.Text.Json;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Delegates.EthosU;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Formats;

/// <summary>
/// End-to-end integration and ground-truth verification tests for Arm Ethos-U delegate expansion in TFLite and ExecuTorch models.
/// </summary>
public sealed class EthosUDelegateModelTests
{
    [Test]
    public async Task TfliteVelaU55_ExpandsDelegateSubgraph_MatchingGroundTruth()
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "tflite", "tiny_cnn_int8_vela_u55.tflite");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "tflite", "tiny_cnn_int8_vela_u55.truth.json");

        await using FileStream stream = new(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(stream, new ModelReadOptions { FileName = "tiny_cnn_int8_vela_u55.tflite" });

        Subgraph main = graph.PrimarySubgraph!;
        await Assert.That(main.Nodes.Count).IsEqualTo(1);

        GraphNode delegateNode = main.Nodes[0];
        IDelegateSubgraphExpander? expander = DelegateSubgraphExpanderRegistry.Default.FindExpander(graph, delegateNode);
        await Assert.That(expander).IsNotNull();
        await Assert.That(expander!.BackendId).IsEqualTo("ethos-u");

        Subgraph? expanded = await DelegateSubgraphExpanderRegistry.Default.ExpandAsync(stream, graph, delegateNode);
        await Assert.That(expanded).IsNotNull();

        using JsonDocument truthDoc = JsonDocument.Parse(await File.ReadAllTextAsync(truthPath));
        JsonElement delegateElem = truthDoc.RootElement.GetProperty("delegate");

        string expectedArch = delegateElem.GetProperty("architecture").GetString()!;
        int expectedOpCount = delegateElem.GetProperty("totalNpuOperations").GetInt32();

        await Assert.That(expanded!.Name).Contains(expectedArch);
        await Assert.That(expanded.Nodes.Count).IsEqualTo(expectedOpCount);

        // Check op histogram
        Dictionary<string, int> actualHistogram = [];
        foreach (GraphNode node in expanded.Nodes)
        {
            actualHistogram[node.Operator.Name] = actualHistogram.GetValueOrDefault(node.Operator.Name) + 1;
        }

        foreach (JsonProperty prop in delegateElem.GetProperty("operatorHistogram").EnumerateObject())
        {
            await Assert.That(actualHistogram.GetValueOrDefault(prop.Name)).IsEqualTo(prop.Value.GetInt32());
        }
    }

    [Test]
    public async Task TfliteVelaU65_ExpandsDelegateSubgraph_MatchingGroundTruth()
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "tflite", "tiny_cnn_int8_vela_u65.tflite");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "tflite", "tiny_cnn_int8_vela_u65.truth.json");

        await using FileStream stream = new(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(stream, new ModelReadOptions { FileName = "tiny_cnn_int8_vela_u65.tflite" });

        Subgraph main = graph.PrimarySubgraph!;
        GraphNode delegateNode = main.Nodes[0];

        Subgraph? expanded = await DelegateSubgraphExpanderRegistry.Default.ExpandAsync(stream, graph, delegateNode);
        await Assert.That(expanded).IsNotNull();

        using JsonDocument truthDoc = JsonDocument.Parse(await File.ReadAllTextAsync(truthPath));
        JsonElement delegateElem = truthDoc.RootElement.GetProperty("delegate");

        string expectedArch = delegateElem.GetProperty("architecture").GetString()!;
        int expectedOpCount = delegateElem.GetProperty("totalNpuOperations").GetInt32();

        await Assert.That(expanded!.Name).Contains(expectedArch);
        await Assert.That(expanded.Nodes.Count).IsEqualTo(expectedOpCount);
    }

    [Test]
    public async Task ExecuTorchEthosU55_ExpandsDelegateSubgraph_MatchingGroundTruth()
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "executorch", "tiny_cnn_ethosu55.pte");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "executorch", "tiny_cnn_ethosu55.truth.json");

        await using FileStream stream = new(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(stream, new ModelReadOptions { FileName = "tiny_cnn_ethosu55.pte" });

        Subgraph main = graph.PrimarySubgraph!;
        GraphNode? delegateNode = main.Nodes.FirstOrDefault(n => n.Operator.Name.Contains("EthosU", StringComparison.OrdinalIgnoreCase));
        await Assert.That(delegateNode).IsNotNull();

        IDelegateSubgraphExpander? expander = DelegateSubgraphExpanderRegistry.Default.FindExpander(graph, delegateNode!);
        await Assert.That(expander).IsNotNull();

        Subgraph? expanded = await DelegateSubgraphExpanderRegistry.Default.ExpandAsync(stream, graph, delegateNode!);
        await Assert.That(expanded).IsNotNull();

        using JsonDocument truthDoc = JsonDocument.Parse(await File.ReadAllTextAsync(truthPath));
        JsonElement delegateElem = truthDoc.RootElement.GetProperty("delegate");

        string expectedArch = delegateElem.GetProperty("architecture").GetString()!;
        int expectedOpCount = delegateElem.GetProperty("totalNpuOperations").GetInt32();

        await Assert.That(expanded!.Name).Contains(expectedArch);
        await Assert.That(expanded.Nodes.Count).IsEqualTo(expectedOpCount);

        // Check op histogram
        Dictionary<string, int> actualHistogram = [];
        foreach (GraphNode node in expanded.Nodes)
        {
            actualHistogram[node.Operator.Name] = actualHistogram.GetValueOrDefault(node.Operator.Name) + 1;
        }

        foreach (JsonProperty prop in delegateElem.GetProperty("operatorHistogram").EnumerateObject())
        {
            await Assert.That(actualHistogram.GetValueOrDefault(prop.Name)).IsEqualTo(prop.Value.GetInt32());
        }
    }

    [Test]
    public async Task Decode_CommandStream_ZeroAllocationsPerCommand()
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "tflite", "tiny_cnn_int8_vela_u55.tflite");
        byte[] fileBytes = await File.ReadAllBytesAsync(modelPath);

        await using MemoryStream stream = new(fileBytes);
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(stream);
        GraphNode delegateNode = graph.PrimarySubgraph!.Nodes[0];

        TensorDataSlice cmdSlice = (TensorDataSlice)delegateNode.Inputs[0].Tensors[0].Data!;

        (int warmCount, long allocatedDelta) = MeasureDecodeAllocations(fileBytes, cmdSlice);

        await Assert.That(warmCount > 0).IsTrue();
        await Assert.That(allocatedDelta).IsEqualTo(0L);
    }

    private sealed class CommandCounter
    {
        public int Count { get; set; }

        public void OnCommand(in EthosUCommand cmd)
        {
            Count++;
        }
    }

    private static (int warmCount, long allocatedDelta) MeasureDecodeAllocations(byte[] fileBytes, TensorDataSlice cmdSlice)
    {
        ReadOnlySpan<byte> sliceSpan = fileBytes.AsSpan((int)cmdSlice.StreamOffset, (int)cmdSlice.ByteLength);
        EthosUDriverPayload driverPayload = EthosUDriverPayloadReader.Read(sliceSpan, cmdSlice.StreamOffset);
        ReadOnlySpan<byte> wordsSpan = sliceSpan.Slice(driverPayload.CommandStreamByteOffset, driverPayload.CommandStreamByteLength);

        var counter = new CommandCounter();
        EthosUCommandAction callback = counter.OnCommand;

        // Warm up JIT and delegate
        EthosUCommandStreamDecoder.Decode(wordsSpan, callback);

        // Measure GC allocation during decode
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();

        counter.Count = 0;
        EthosUCommandStreamDecoder.Decode(wordsSpan, callback);

        long afterAllocated = GC.GetAllocatedBytesForCurrentThread();
        return (counter.Count, afterAllocated - beforeAllocated);
    }
}
