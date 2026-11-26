using System.Text.Json;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Formats.Gguf;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Formats;

/// <summary>
/// Model integration tests verifying GGUF fixtures against independent Python ground truth.
/// </summary>
public sealed class GgufModelTests
{
    public static IEnumerable<string> GetCommittedGgufStems() =>
    [
        "tiny_llm",
        "tiny_moe_engram",
        "tiny_audio_kokoro",
        "tiny_hybrid_ssm",
    ];

    [Test]
    [MethodDataSource(nameof(GetCommittedGgufStems))]
    public async Task CommittedGgufFixtures_CrossCheckAgainstPythonGroundTruth(string stem)
    {
        string modelPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "gguf", $"{stem}.gguf");
        string truthPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "gguf", $"{stem}.truth.json");

        await Assert.That(File.Exists(modelPath)).IsTrue();
        await Assert.That(File.Exists(truthPath)).IsTrue();

        await using FileStream fs = File.OpenRead(modelPath);
        ModelProbe probe = ModelProbe.Create(fs, Path.GetFileName(modelPath));
        await Assert.That(GgufFormatDetector.Instance.CanRead(probe)).IsTrue();
        probe.RestorePosition();
        await Assert.That(fs.Position).IsEqualTo(0);

        ModelGraph graph = await GgufFormatReader.Instance.ReadAsync(fs);
        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.Gguf);

        using JsonDocument truthDoc = JsonDocument.Parse(await File.ReadAllTextAsync(truthPath));
        JsonElement root = truthDoc.RootElement;
        JsonElement truthPrimary = root.GetProperty("primarySubgraph");

        await Assert.That(graph.Format.ToDisplayString()).IsEqualTo(root.GetProperty("format").GetString());
        await Assert.That(graph.Subgraphs.Count).IsEqualTo(root.GetProperty("subgraphCount").GetInt32());

        Subgraph primary = graph.PrimarySubgraph!;
        await Assert.That(primary.Name).IsEqualTo(truthPrimary.GetProperty("name").GetString());
        await Assert.That(primary.Nodes.Count).IsEqualTo(truthPrimary.GetProperty("nodeCount").GetInt32());

        Dictionary<string, int> actualHist = primary.Nodes
            .GroupBy(n => n.Operator.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        JsonElement truthHist = truthPrimary.GetProperty("operatorHistogram");
        await Assert.That(actualHist.Count).IsEqualTo(truthHist.EnumerateObject().Count());
        foreach (JsonProperty prop in truthHist.EnumerateObject())
        {
            await Assert.That(actualHist.ContainsKey(prop.Name)).IsTrue();
            await Assert.That(actualHist[prop.Name]).IsEqualTo(prop.Value.GetInt32());
        }

        // Verify inputs and outputs
        JsonElement truthInputs = truthPrimary.GetProperty("inputs");
        await Assert.That(primary.Inputs.Count).IsEqualTo(truthInputs.GetArrayLength());
        for (int i = 0; i < primary.Inputs.Count; i++)
        {
            TensorEdge edge = primary.Inputs[i].Tensors[0];
            JsonElement ti = truthInputs[i];
            await Assert.That(edge.Name).IsEqualTo(ti.GetProperty("name").GetString());
            await Assert.That(edge.Descriptor.DataType.ToString()).IsEqualTo(ti.GetProperty("dataType").GetString());
        }

        JsonElement truthOutputs = truthPrimary.GetProperty("outputs");
        await Assert.That(primary.Outputs.Count).IsEqualTo(truthOutputs.GetArrayLength());
        for (int i = 0; i < primary.Outputs.Count; i++)
        {
            TensorEdge edge = primary.Outputs[i].Tensors[0];
            JsonElement to = truthOutputs[i];
            await Assert.That(edge.Name).IsEqualTo(to.GetProperty("name").GetString());
            await Assert.That(edge.Descriptor.DataType.ToString()).IsEqualTo(to.GetProperty("dataType").GetString());
        }

        // Verify every initializer's shape, dataType, BlockQuantization, and exact lazy TensorDataSlice
        foreach (JsonElement init in truthPrimary.GetProperty("initializers").EnumerateArray())
        {
            string name = init.GetProperty("name").GetString()!;
            TensorEdge? tensor = primary.Tensors.FirstOrDefault(t => t.Name == name);
            await Assert.That(tensor).IsNotNull();
            await Assert.That(tensor!.Kind).IsEqualTo(TensorKind.Initializer);
            await Assert.That(tensor.Descriptor.DataType.ToString()).IsEqualTo(init.GetProperty("dataType").GetString());

            string[] expectedShape = init.GetProperty("shape").EnumerateArray().Select(e => e.GetString()!).ToArray();
            string[] actualShape = tensor.Descriptor.Shape!.Dimensions.Select(d => d.ToDisplayString()).ToArray();
            await Assert.That(actualShape).IsEquivalentTo(expectedShape);

            TensorDataSlice slice = (TensorDataSlice)tensor.Data!;
            await Assert.That(slice.StreamOffset).IsEqualTo(init.GetProperty("streamOffset").GetInt64());
            await Assert.That(slice.ByteLength).IsEqualTo(init.GetProperty("byteLength").GetInt64());
            await Assert.That(slice.StreamOffset + slice.ByteLength).IsLessThanOrEqualTo(fs.Length);

            JsonElement schemeProp = init.GetProperty("quantizationScheme");
            if (schemeProp.ValueKind == JsonValueKind.String)
            {
                await Assert.That(slice.Encoding).IsEqualTo(TensorDataEncoding.GgmlQuantizedBlock);
                await Assert.That(tensor.Descriptor.Quantization).IsTypeOf<BlockQuantization>();
                BlockQuantization bq = (BlockQuantization)tensor.Descriptor.Quantization!;
                await Assert.That(bq.Scheme).IsEqualTo(schemeProp.GetString());
                await Assert.That(bq.BlockSize).IsEqualTo(init.GetProperty("blockSize").GetInt32());
            }
            else
            {
                await Assert.That(slice.Encoding).IsEqualTo(TensorDataEncoding.Raw);
                await Assert.That(tensor.Descriptor.Quantization).IsNull();
            }
        }

        // Verify on-demand slice reading succeeds on the first initializer
        TensorEdge firstInit = primary.Tensors.First(t => t.Kind == TensorKind.Initializer && t.Data is TensorDataSlice { ByteLength: > 0 });
        TensorDataSlice firstSlice = (TensorDataSlice)firstInit.Data!;
        byte[] sampleBuf = new byte[(int)Math.Min(32, firstSlice.ByteLength)];
        int bytesRead = TensorDataLoader.ReadSlice(fs, firstSlice, sampleBuf);
        await Assert.That(bytesRead).IsEqualTo(sampleBuf.Length);
    }

    [Test]
    public async Task BlockSubgraphs_DeepSeekMoEAndHybridSsm_ExpandInternalModules()
    {
        string moePath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "gguf", "tiny_moe_engram.gguf");
        await using (FileStream fs = File.OpenRead(moePath))
        {
            ModelGraph graph = await GgufFormatReader.Instance.ReadAsync(fs);
            GraphNode blk0 = graph.PrimarySubgraph!.Nodes.First(n => n.Name == "blk.0");
            GraphNode blk1 = graph.PrimarySubgraph!.Nodes.First(n => n.Name == "blk.1");

            Subgraph sub0 = ((AttributeValue.Graph)blk0.Attributes.First(a => a.Name == "subgraph").Value).Value;
            Subgraph sub1 = ((AttributeValue.Graph)blk1.Attributes.First(a => a.Name == "subgraph").Value).Value;

            await Assert.That(sub0.Nodes.Select(n => n.Operator.Name).ToArray())
                .IsEquivalentTo(["RMSNorm", "MultiHeadLatentAttention", "RMSNorm", "MoEFeedForward"]);
            await Assert.That(sub1.Nodes.Select(n => n.Operator.Name).ToArray())
                .IsEquivalentTo(["RMSNorm", "MultiHeadLatentAttention", "EngramMemory", "RMSNorm", "FeedForward"]);
        }

        string ssmPath = Path.Combine(ModelFixtureLoader.TestProjectDirectory, "Fixtures", "gguf", "tiny_hybrid_ssm.gguf");
        await using (FileStream fs = File.OpenRead(ssmPath))
        {
            ModelGraph graph = await GgufFormatReader.Instance.ReadAsync(fs);
            GraphNode blk0 = graph.PrimarySubgraph!.Nodes.First(n => n.Name == "blk.0");
            GraphNode blk1 = graph.PrimarySubgraph!.Nodes.First(n => n.Name == "blk.1");

            Subgraph sub0 = ((AttributeValue.Graph)blk0.Attributes.First(a => a.Name == "subgraph").Value).Value;
            Subgraph sub1 = ((AttributeValue.Graph)blk1.Attributes.First(a => a.Name == "subgraph").Value).Value;

            await Assert.That(sub0.Nodes.Select(n => n.Operator.Name).ToArray())
                .IsEquivalentTo(["RMSNorm", "StateSpace"]);
            await Assert.That(sub1.Nodes.Select(n => n.Operator.Name).ToArray())
                .IsEquivalentTo(["RMSNorm", "Attention", "RMSNorm", "FeedForward"]);
        }
    }
}
