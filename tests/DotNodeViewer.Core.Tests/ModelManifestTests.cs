using System.Security.Cryptography;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests;

/// <summary>
/// Executes manifest-driven golden topology regression tests across all registered neural network fixtures.
/// </summary>
public sealed class ModelManifestTests
{
    /// <summary>
    /// Enumerates all model entries registered in <c>Fixtures/models.manifest.json</c>, or yields a single <see langword="null"/> sentinel when the manifest is empty.
    /// </summary>
    /// <returns>The sequence of registered <see cref="ModelManifestEntry"/> instances.</returns>
    public static IEnumerable<ModelManifestEntry?> GetRegisteredModels()
    {
        ModelManifestDocument manifest = ModelFixtureLoader.LoadManifest();
        if (manifest.Models.Count == 0)
        {
            yield return null;
            yield break;
        }

        for (int i = 0; i < manifest.Models.Count; i++)
        {
            yield return manifest.Models[i];
        }
    }

    [Test]
    [MethodDataSource(nameof(GetRegisteredModels))]
    public async Task ParseRegisteredModel_MatchesExpectedTopology(ModelManifestEntry? entry)
    {
        if (entry is null)
        {
            ModelManifestDocument manifest = ModelFixtureLoader.LoadManifest();
            await Assert.That(manifest.SchemaVersion).IsEqualTo(1);
            return;
        }

        await using FileStream? stream = await ModelFixtureLoader.OpenVerifiedModelStreamAsync(entry);
        if (stream is null)
        {
            return;
        }

        await Assert.That(stream.Position).IsEqualTo(0);
        await Assert.That(stream.Length).IsEqualTo(entry.ByteLength);

        ModelProbe probe = ModelProbe.Create(stream, entry.FileName);
        IModelFormatDetector? detector = ModelFormatRegistry.Default.TryDetect(probe);
        await Assert.That(detector).IsNotNull();
        await Assert.That(detector!.Format.Id.Value).IsEqualTo(entry.Format);
        await Assert.That(stream.Position).IsEqualTo(0);

        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = entry.FileName });

        ModelTopologySnapshot actual = SnapshotBuilder.Build(graph, entry.Id);
        ModelTopologySnapshot expected = ModelFixtureLoader.LoadExpectedSnapshot(entry);

        await AssertSnapshotsMatchAsync(actual, expected, entry.ByteLength);
    }

    [Test]
    public async Task ModelFixtureLoader_EndToEndWithTemporaryFixture_VerifiesIntegrityAndTopologySnapshot()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"dnv-manifest-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            byte[] modelBytes = Encoding.UTF8.GetBytes("SYNTH_HEADER____WEIGHT_PAYLOAD_12345678");
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(modelBytes));

            string fixturesDir = Path.Combine(tempDir, "Fixtures", "synth");
            Directory.CreateDirectory(fixturesDir);
            string modelPath = Path.Combine(fixturesDir, "tiny.synth");
            await File.WriteAllBytesAsync(modelPath, modelBytes);

            TensorEdge inputEdge = new(
                "x",
                new TensorDescriptor(
                    DataType.Float32,
                    new TensorShape([new TensorDimension.Symbolic("batch"), new TensorDimension.Fixed(4)])),
                TensorKind.GraphInput);

            TensorEdge weightEdge = new(
                "w",
                new TensorDescriptor(
                    DataType.Int8,
                    new TensorShape([new TensorDimension.Fixed(4), new TensorDimension.Fixed(2)]),
                    new AffineQuantization([0.25f], [0], QuantizedDimension: 0)),
                TensorKind.Initializer,
                new TensorDataSlice(StreamOffset: 16, ByteLength: 8, DataType.Int8));

            TensorEdge outputEdge = new(
                "y",
                new TensorDescriptor(
                    DataType.Float32,
                    new TensorShape([new TensorDimension.Symbolic("batch"), new TensorDimension.Fixed(2)])),
                TensorKind.GraphOutput);

            GraphNode node = new(
                "gemm_0",
                new OperatorInfo("Gemm", OperatorCategory.Layer),
                Inputs: [new NodePort("A", [inputEdge]), new NodePort("B", [weightEdge])],
                Outputs: [new NodePort("Y", [outputEdge])],
                Attributes: []);

            Subgraph subgraph = new(
                "main",
                Nodes: [node],
                Tensors: [inputEdge, weightEdge, outputEdge],
                Inputs: [new GraphPort("x", [inputEdge])],
                Outputs: [new GraphPort("y", [outputEdge])]);

            ModelGraph expectedGraph = new(
                new ModelFormatInfo(new ModelFormatId("synth"), "Synthetic Test Format", "v1"),
                Producer: "unit-test",
                Subgraphs: [subgraph],
                Metadata: new Dictionary<string, string>(),
                Diagnostics: []);

            ModelTopologySnapshot snapshot = SnapshotBuilder.Build(expectedGraph, "synth-tiny");
            string snapshotJson = SnapshotBuilder.ToJson(snapshot);
            string snapshotPath = Path.Combine(fixturesDir, "tiny.expected.json");
            await File.WriteAllTextAsync(snapshotPath, snapshotJson);

            ModelManifestEntry entry = new(
                Id: "synth-tiny",
                Format: "synth",
                FileName: "tiny.synth",
                Tier: "committed",
                RelativePath: "Fixtures/synth/tiny.synth",
                SourceUrl: null,
                Sha256: sha256,
                ByteLength: modelBytes.Length,
                ExpectedSnapshot: "Fixtures/synth/tiny.expected.json");

            await using FileStream? verifiedStream = await ModelFixtureLoader.OpenVerifiedModelStreamAsync(entry, tempDir);
            await Assert.That(verifiedStream).IsNotNull();
            await Assert.That(verifiedStream!.Position).IsEqualTo(0);

            ModelTopologySnapshot loadedExpected = ModelFixtureLoader.LoadExpectedSnapshot(entry, tempDir);
            await AssertSnapshotsMatchAsync(snapshot, loadedExpected, entry.ByteLength);

            ModelManifestEntry badHashEntry = entry with { Sha256 = new string('0', 64) };
            await Assert.That(async () => await ModelFixtureLoader.OpenVerifiedModelStreamAsync(badHashEntry, tempDir))
                .Throws<InvalidDataException>();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static async Task AssertSnapshotsMatchAsync(
        ModelTopologySnapshot actual,
        ModelTopologySnapshot expected,
        long fileByteLength)
    {
        await Assert.That(actual.ModelId).IsEqualTo(expected.ModelId);
        await Assert.That(actual.Format).IsEqualTo(expected.Format);
        await Assert.That(actual.SubgraphCount).IsEqualTo(expected.SubgraphCount);
        await Assert.That(actual.PrimarySubgraph.Name).IsEqualTo(expected.PrimarySubgraph.Name);
        await Assert.That(actual.PrimarySubgraph.NodeCount).IsEqualTo(expected.PrimarySubgraph.NodeCount);

        await Assert.That(actual.PrimarySubgraph.OperatorHistogram.Count)
            .IsEqualTo(expected.PrimarySubgraph.OperatorHistogram.Count);
        foreach (KeyValuePair<string, int> kvp in expected.PrimarySubgraph.OperatorHistogram)
        {
            await Assert.That(actual.PrimarySubgraph.OperatorHistogram.ContainsKey(kvp.Key)).IsTrue();
            await Assert.That(actual.PrimarySubgraph.OperatorHistogram[kvp.Key]).IsEqualTo(kvp.Value);
        }

        await AssertPortListMatchesAsync(actual.PrimarySubgraph.Inputs, expected.PrimarySubgraph.Inputs);
        await AssertPortListMatchesAsync(actual.PrimarySubgraph.Outputs, expected.PrimarySubgraph.Outputs);

        if (expected.PrimarySubgraph.SampleWeightSlice is not null)
        {
            WeightSliceSnapshot? actualSlice = actual.PrimarySubgraph.SampleWeightSlice;
            WeightSliceSnapshot expectedSlice = expected.PrimarySubgraph.SampleWeightSlice;
            await Assert.That(actualSlice).IsNotNull();
            await Assert.That(actualSlice!.TensorName).IsEqualTo(expectedSlice.TensorName);
            await Assert.That(actualSlice.DataType).IsEqualTo(expectedSlice.DataType);
            await Assert.That(actualSlice.StreamOffset).IsEqualTo(expectedSlice.StreamOffset);
            await Assert.That(actualSlice.ByteLength).IsEqualTo(expectedSlice.ByteLength);
            await Assert.That(actualSlice.StreamOffset > 0).IsTrue();
            await Assert.That(actualSlice.ByteLength > 0).IsTrue();
            await Assert.That(actualSlice.StreamOffset + actualSlice.ByteLength <= fileByteLength).IsTrue();
        }
    }

    private static async Task AssertPortListMatchesAsync(
        IReadOnlyList<TensorPortSnapshot> actualPorts,
        IReadOnlyList<TensorPortSnapshot> expectedPorts)
    {
        await Assert.That(actualPorts.Count).IsEqualTo(expectedPorts.Count);
        for (int i = 0; i < expectedPorts.Count; i++)
        {
            TensorPortSnapshot actual = actualPorts[i];
            TensorPortSnapshot expected = expectedPorts[i];
            await Assert.That(actual.Name).IsEqualTo(expected.Name);
            await Assert.That(actual.DataType).IsEqualTo(expected.DataType);

            if (expected.Shape is null)
            {
                await Assert.That(actual.Shape).IsNull();
            }
            else
            {
                await Assert.That(actual.Shape).IsNotNull();
                await Assert.That(actual.Shape!.Count).IsEqualTo(expected.Shape.Count);
                for (int d = 0; d < expected.Shape.Count; d++)
                {
                    await Assert.That(actual.Shape[d]).IsEqualTo(expected.Shape[d]);
                }
            }

            if (expected.Quantization is not null)
            {
                await Assert.That(actual.Quantization).IsNotNull();
                await Assert.That(actual.Quantization!.Scale.Count).IsEqualTo(expected.Quantization.Scale.Count);
                await Assert.That(actual.Quantization.ZeroPoint.Count).IsEqualTo(expected.Quantization.ZeroPoint.Count);
                await Assert.That(actual.Quantization.QuantizedDimension).IsEqualTo(expected.Quantization.QuantizedDimension);
            }
        }
    }
}

