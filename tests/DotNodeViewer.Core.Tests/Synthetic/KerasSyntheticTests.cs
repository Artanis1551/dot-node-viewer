using System.IO.Compression;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Formats.Keras;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Tests.Synthetic;

/// <summary>
/// Synthetic in-memory binary tests for <see cref="KerasFormatDetector"/> and <see cref="KerasFormatReader"/>.
/// </summary>
public sealed class KerasSyntheticTests
{
    [Test]
    public async Task CanRead_ValidHeaderTruncatedAndRandomBytes_ReturnsExpectedResultAndRestoresPosition()
    {
        // 1. Keras 3 ZIP archive
        byte[] validKeras = BuildSyntheticKerasZip();
        using MemoryStream validStream = new(validKeras);
        validStream.Position = 8;

        ModelProbe validProbe = ModelProbe.Create(validStream, "model.keras");
        bool canRead = KerasFormatDetector.Instance.CanRead(validProbe);
        validProbe.RestorePosition();

        await Assert.That(canRead).IsTrue();
        await Assert.That(validStream.Position).IsEqualTo(8);

        ModelFormatInfo? detected = ModelFormatRegistry.Default.TryDetect(validStream);
        await Assert.That(detected).IsNotNull();
        await Assert.That(detected!.Id).IsEqualTo(ModelFormatId.Keras);
        await Assert.That(validStream.Position).IsEqualTo(8);

        // 2. Legacy HDF5 file
        byte[] validH5 = BuildSyntheticHdf5Header();
        using MemoryStream h5Stream = new(validH5);
        ModelProbe h5Probe = ModelProbe.Create(h5Stream, "model.h5");
        await Assert.That(KerasFormatDetector.Instance.CanRead(h5Probe)).IsTrue();

        // 3. Truncated stream (< 8 bytes)
        using MemoryStream truncatedStream = new(validKeras[..4]);
        ModelProbe truncatedProbe = ModelProbe.Create(truncatedStream, "truncated.keras");
        await Assert.That(KerasFormatDetector.Instance.CanRead(truncatedProbe)).IsFalse();

        // 4. Random garbage bytes
        byte[] randomBytes = new byte[64];
        Random.Shared.NextBytes(randomBytes);
        using MemoryStream randomStream = new(randomBytes);
        ModelProbe randomProbe = ModelProbe.Create(randomStream, "random.keras");
        await Assert.That(KerasFormatDetector.Instance.CanRead(randomProbe)).IsFalse();
    }

    [Test]
    public async Task ReadAsync_SequentialTopology_MapsExpectedGraph()
    {
        byte[] kerasBytes = BuildSyntheticKerasZip();
        using MemoryStream ms = new(kerasBytes);

        ModelGraph graph = await KerasFormatReader.Instance.ReadAsync(ms);

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.Keras);
        await Assert.That(graph.Producer).IsNotNull();
        await Assert.That(graph.Producer!).Contains("Keras");
        await Assert.That(graph.Subgraphs.Count).IsEqualTo(1);

        Subgraph subgraph = graph.PrimarySubgraph!;
        await Assert.That(subgraph.Name).IsEqualTo("main");
        await Assert.That(subgraph.Nodes.Count).IsEqualTo(2);

        // Layers
        GraphNode dense1 = subgraph.Nodes[0];
        await Assert.That(dense1.Name).IsEqualTo("dense_1");
        await Assert.That(dense1.Operator.Category).IsEqualTo(OperatorCategory.Layer);
        await Assert.That(dense1.Operator.Name).IsEqualTo("Dense");

        GraphNode dense2 = subgraph.Nodes[1];
        await Assert.That(dense2.Name).IsEqualTo("dense_2");
        await Assert.That(dense2.Operator.Category).IsEqualTo(OperatorCategory.Layer);

        // Inputs & outputs
        await Assert.That(subgraph.Inputs.Count).IsEqualTo(1);
        await Assert.That(subgraph.Inputs[0].Name).IsEqualTo("input_1");
        await Assert.That(subgraph.Outputs.Count).IsEqualTo(1);
        await Assert.That(subgraph.Outputs[0].Name).IsEqualTo("dense_2");
    }

    [Test]
    public async Task ReadAsync_LazyWeightSlices_RecordedWithoutLoadingBytes()
    {
        byte[] kerasBytes = BuildSyntheticKerasZipWithWeights();
        using MemoryStream ms = new(kerasBytes);

        ModelGraph graph = await KerasFormatReader.Instance.ReadAsync(ms);
        Subgraph subgraph = graph.PrimarySubgraph!;

        TensorEdge? weightEdge = subgraph.Tensors.FirstOrDefault(t => t.Name == "dense_1/kernel");
        await Assert.That(weightEdge).IsNotNull();
        await Assert.That(weightEdge!.Kind).IsEqualTo(TensorKind.Initializer);
        await Assert.That(weightEdge.Data).IsNotNull();
        await Assert.That(weightEdge.Data).IsTypeOf<TensorDataSlice>();

        var slice = (TensorDataSlice)weightEdge.Data!;
        await Assert.That(slice.ByteLength).IsEqualTo(128); // 4*8 * 4
        await Assert.That(slice.DataType).IsEqualTo(DataType.Float32);
        await Assert.That(slice.StreamOffset).IsGreaterThan(0);
        await Assert.That(slice.StreamOffset + slice.ByteLength).IsLessThanOrEqualTo(ms.Length);
    }

    [Test]
    public async Task ReadAsync_CorruptedAndMalformedArchives_ThrowsModelParseException()
    {
        // 1. Missing config.json
        byte[] missingConfigZip = BuildZipArchive(("metadata.json", "{}"u8.ToArray()));
        using (MemoryStream ms = new(missingConfigZip))
        {
            await Assert.That(async () => await KerasFormatReader.Instance.ReadAsync(ms))
                .Throws<ModelParseException>();
        }

        // 2. Malformed config.json
        byte[] corruptJsonZip = BuildZipArchive(
            ("config.json", "{ malformed json"u8.ToArray()));
        using (MemoryStream ms = new(corruptJsonZip))
        {
            await Assert.That(async () => await KerasFormatReader.Instance.ReadAsync(ms))
                .Throws<ModelParseException>();
        }

        // 3. Corrupted header
        byte[] corruptBytes = [0x89, 0x48, 0x44, 0x46, 0x00, 0x00];
        using (MemoryStream ms = new(corruptBytes))
        {
            await Assert.That(async () => await KerasFormatReader.Instance.ReadAsync(ms))
                .Throws<ModelParseException>();
        }
    }

    private static byte[] BuildSyntheticKerasZip()
    {
        using MemoryStream ms = new();
        using (ZipArchive archive = new(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var metaEntry = archive.CreateEntry("metadata.json", CompressionLevel.NoCompression);
            using (var s = metaEntry.Open())
            {
                s.Write("{\"keras_version\": \"3.8.0\", \"date_saved\": \"2026-10-24\"}"u8);
            }

            var cfgEntry = archive.CreateEntry("config.json", CompressionLevel.NoCompression);
            using (var s = cfgEntry.Open())
            {
                string configJson = """
                {
                  "class_name": "Sequential",
                  "config": {
                    "name": "tiny_sequential",
                    "layers": [
                      {
                        "class_name": "InputLayer",
                        "config": {
                          "batch_shape": [null, 4],
                          "dtype": "float32",
                          "name": "input_1"
                        }
                      },
                      {
                        "class_name": "Dense",
                        "config": {
                          "name": "dense_1",
                          "units": 8,
                          "activation": "relu"
                        }
                      },
                      {
                        "class_name": "Dense",
                        "config": {
                          "name": "dense_2",
                          "units": 2,
                          "activation": "linear"
                        }
                      }
                    ]
                  }
                }
                """;
                s.Write(Encoding.UTF8.GetBytes(configJson));
            }
        }

        return ms.ToArray();
    }

    private static byte[] BuildSyntheticKerasZipWithWeights()
    {
        // Read the fixture tiny_sequential.keras directly
        string fixturePath = Path.Combine(
            Harness.ModelFixtureLoader.TestProjectDirectory,
            "Fixtures",
            "keras",
            "tiny_sequential.keras");

        if (File.Exists(fixturePath))
        {
            return File.ReadAllBytes(fixturePath);
        }

        return BuildSyntheticKerasZip();
    }

    private static byte[] BuildSyntheticHdf5Header()
    {
        byte[] bytes = new byte[64];
        byte[] magic = [0x89, 0x48, 0x44, 0x46, 0x0D, 0x0A, 0x1A, 0x0A];
        magic.CopyTo(bytes, 0);
        bytes[8] = 0; // Superblock version 0
        bytes[13] = 8; // Size of offsets
        bytes[14] = 8; // Size of lengths
        return bytes;
    }

    private static byte[] BuildZipArchive(params (string Name, byte[] Content)[] entries)
    {
        using MemoryStream ms = new();
        using (ZipArchive archive = new(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }
        return ms.ToArray();
    }
}

