using System.Security.Cryptography;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests;

/// <summary>
/// Verifies the behavior of <see cref="ModelFixtureLoader"/> including temporary synthetic fixture generation and verification.
/// </summary>
public sealed class ModelFixtureLoaderTests
{
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
            await ModelManifestTests.AssertSnapshotsMatchAsync(snapshot, loadedExpected, entry.ByteLength);

            ModelManifestEntry badHashEntry = entry with { Sha256 = new string('0', 64) };
            await Assert.That(async () => await ModelFixtureLoader.OpenVerifiedModelStreamAsync(badHashEntry, tempDir))
                .Throws<InvalidDataException>();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
