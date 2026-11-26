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

        await AssertMatchesTruthAsync(expanded!, truthPath);
        await AssertCleanConnectedTopologyAsync(expanded!, "serving_default_input:0", "StatefulPartitionedCall_1:0");
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

        await AssertMatchesTruthAsync(expanded!, truthPath);
        await AssertCleanConnectedTopologyAsync(expanded!, "serving_default_input:0", "StatefulPartitionedCall_1:0");
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

        await AssertMatchesTruthAsync(expanded!, truthPath);
        await AssertCleanConnectedTopologyAsync(expanded!, "v1", "v7");
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

    private static async Task AssertMatchesTruthAsync(Subgraph expanded, string truthPath)
    {
        using JsonDocument truthDoc = JsonDocument.Parse(await File.ReadAllTextAsync(truthPath));
        JsonElement delegateElem = truthDoc.RootElement.GetProperty("delegate");

        string expectedArch = delegateElem.GetProperty("architecture").GetString()!;
        int expectedOpCount = delegateElem.GetProperty("totalNpuOperations").GetInt32();

        await Assert.That(expanded.Name).Contains(expectedArch);
        await Assert.That(expanded.Nodes.Count).IsEqualTo(expectedOpCount);

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

    private static async Task AssertCleanConnectedTopologyAsync(
        Subgraph expanded,
        string expectedInputName,
        string expectedOutputName)
    {
        await Assert.That(expanded.Inputs.Count).IsEqualTo(1);
        await Assert.That(expanded.Inputs[0].Name).IsEqualTo(expectedInputName);
        await Assert.That(expanded.Outputs.Count).IsEqualTo(1);
        await Assert.That(expanded.Outputs[0].Name).IsEqualTo(expectedOutputName);

        var producerByTensorName = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodeIndexByVertexId = new Dictionary<string, int>(StringComparer.Ordinal);
        var adj = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var outDegree = new Dictionary<string, int>(StringComparer.Ordinal);

        void EnsureVertex(string id)
        {
            if (!adj.ContainsKey(id))
            {
                adj[id] = new HashSet<string>(StringComparer.Ordinal);
                inDegree[id] = 0;
                outDegree[id] = 0;
            }
        }

        foreach (GraphPort inPort in expanded.Inputs)
        {
            string vId = $"in:{inPort.Name}";
            EnsureVertex(vId);
            foreach (TensorEdge edge in inPort.Tensors)
            {
                producerByTensorName[edge.Name] = vId;
            }
        }

        var uniqueOutputTensorNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < expanded.Nodes.Count; i++)
        {
            GraphNode node = expanded.Nodes[i];
            string vId = $"node:{i}:{node.Name}";
            EnsureVertex(vId);
            nodeIndexByVertexId[vId] = i;

            foreach (NodePort outPort in node.Outputs)
            {
                foreach (TensorEdge edge in outPort.Tensors)
                {
                    bool added = uniqueOutputTensorNames.Add(edge.Name);
                    await Assert.That(added).IsTrue();
                    producerByTensorName[edge.Name] = vId;
                }
            }
        }

        for (int i = 0; i < expanded.Nodes.Count; i++)
        {
            GraphNode node = expanded.Nodes[i];
            string targetId = $"node:{i}:{node.Name}";

            foreach (NodePort inPort in node.Inputs)
            {
                foreach (TensorEdge edge in inPort.Tensors)
                {
                    bool hasProducer = producerByTensorName.TryGetValue(edge.Name, out string? srcId);
                    if ((edge.Kind == TensorKind.Initializer || edge.Kind == TensorKind.Constant) && !hasProducer)
                    {
                        continue;
                    }

                    await Assert.That(hasProducer).IsTrue();
                    if (nodeIndexByVertexId.TryGetValue(srcId!, out int srcIdx))
                    {
                        await Assert.That(srcIdx < i).IsTrue();
                    }

                    adj[srcId!].Add(targetId);
                    adj[targetId].Add(srcId!);
                    outDegree[srcId!]++;
                    inDegree[targetId]++;
                }
            }
        }

        foreach (GraphPort outPort in expanded.Outputs)
        {
            string outId = $"out:{outPort.Name}";
            EnsureVertex(outId);
            await Assert.That(outPort.Tensors.Count > 0).IsTrue();

            foreach (TensorEdge edge in outPort.Tensors)
            {
                bool hasProducer = producerByTensorName.TryGetValue(edge.Name, out string? srcId);
                await Assert.That(hasProducer).IsTrue();

                adj[srcId!].Add(outId);
                adj[outId].Add(srcId!);
                outDegree[srcId!]++;
                inDegree[outId]++;
            }
        }

        // Verify 0 dead-ends and 0 orphan non-DMA operators
        for (int i = 0; i < expanded.Nodes.Count; i++)
        {
            GraphNode node = expanded.Nodes[i];
            string vId = $"node:{i}:{node.Name}";
            await Assert.That(outDegree[vId] > 0).IsTrue();
            if (!string.Equals(node.Operator.Name, "NPU_DMA", StringComparison.Ordinal))
            {
                await Assert.That(inDegree[vId] > 0).IsTrue();
            }
        }

        // Verify single connected component across all inputs, nodes, and outputs
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        string start = adj.Keys.First();
        visited.Add(start);
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            string cur = queue.Dequeue();
            foreach (string nb in adj[cur])
            {
                if (visited.Add(nb))
                {
                    queue.Enqueue(nb);
                }
            }
        }

        await Assert.That(visited.Count).IsEqualTo(adj.Count);
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
