using System.IO.Compression;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Formats.PyTorchPt2;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Tests.Synthetic;

/// <summary>
/// Synthetic in-memory binary tests for <see cref="PyTorchPt2FormatDetector"/> and <see cref="PyTorchPt2FormatReader"/>.
/// </summary>
public sealed class PyTorchPt2SyntheticTests
{
    [Test]
    public async Task CanRead_ValidHeaderTruncatedAndRandomBytes_ReturnsExpectedResultAndRestoresPosition()
    {
        byte[] validPt2 = BuildSyntheticPt2Zip();
        using MemoryStream validStream = new(validPt2);
        validStream.Position = 8;

        ModelProbe validProbe = ModelProbe.Create(validStream, "model.pt2");
        bool canRead = PyTorchPt2FormatDetector.Instance.CanRead(validProbe);
        validProbe.RestorePosition();

        await Assert.That(canRead).IsTrue();
        await Assert.That(validStream.Position).IsEqualTo(8);

        ModelFormatInfo? detected = ModelFormatRegistry.Default.TryDetect(validStream);
        await Assert.That(detected).IsNotNull();
        await Assert.That(detected!.Id).IsEqualTo(ModelFormatId.PyTorchPt2);
        await Assert.That(validStream.Position).IsEqualTo(8);

        // Truncated stream (< 4 bytes)
        using MemoryStream truncatedStream = new(validPt2[..3]);
        ModelProbe truncatedProbe = ModelProbe.Create(truncatedStream, "truncated.pt2");
        await Assert.That(PyTorchPt2FormatDetector.Instance.CanRead(truncatedProbe)).IsFalse();

        // Random garbage bytes
        byte[] randomBytes = new byte[64];
        Random.Shared.NextBytes(randomBytes);
        using MemoryStream randomStream = new(randomBytes);
        ModelProbe randomProbe = ModelProbe.Create(randomStream, "random.pt2");
        await Assert.That(PyTorchPt2FormatDetector.Instance.CanRead(randomProbe)).IsFalse();
    }

    [Test]
    public async Task ReadAsync_TopologyShapesAndSymbolicConstraints_MapsExpectedGraph()
    {
        byte[] pt2Bytes = BuildSyntheticPt2Zip();
        using MemoryStream ms = new(pt2Bytes);

        ModelGraph graph = await PyTorchPt2FormatReader.Instance.ReadAsync(ms);

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.PyTorchPt2);
        await Assert.That(graph.Producer).IsNotNull();
        await Assert.That(graph.Producer!).Contains("PyTorch");
        await Assert.That(graph.Subgraphs.Count).IsEqualTo(1);

        Subgraph subgraph = graph.PrimarySubgraph!;
        await Assert.That(subgraph.Name).IsEqualTo("main");
        await Assert.That(subgraph.Nodes.Count).IsEqualTo(1);

        GraphNode node = subgraph.Nodes[0];
        await Assert.That(node.Operator.Category).IsEqualTo(OperatorCategory.Layer);
        await Assert.That(node.Operator.Name).IsEqualTo("torch.ops.aten.linear.default");

        // Inputs & outputs
        await Assert.That(subgraph.Inputs.Count).IsEqualTo(1);
        await Assert.That(subgraph.Inputs[0].Name).IsEqualTo("x");
        await Assert.That(subgraph.Outputs.Count).IsEqualTo(1);
        await Assert.That(subgraph.Outputs[0].Name).IsEqualTo("y");

        // Symbolic dimension & range constraint
        TensorEdge inputEdge = subgraph.Inputs[0].Tensors[0];
        await Assert.That(inputEdge.Descriptor.Shape).IsNotNull();
        await Assert.That(inputEdge.Descriptor.Shape!.Dimensions[0]).IsTypeOf<TensorDimension.Symbolic>();
        var symDim = (TensorDimension.Symbolic)inputEdge.Descriptor.Shape!.Dimensions[0];
        await Assert.That(symDim.Name).IsEqualTo("s0");

        await Assert.That(graph.Metadata.ContainsKey("range_constraints.s0")).IsTrue();
        await Assert.That(graph.Metadata["range_constraints.s0"]).IsEqualTo("[1, 32]");
    }

    [Test]
    public async Task ReadAsync_LazyWeightSlices_RecordedWithoutLoadingBytes()
    {
        byte[] pt2Bytes = BuildSyntheticPt2Zip();
        using MemoryStream ms = new(pt2Bytes);

        ModelGraph graph = await PyTorchPt2FormatReader.Instance.ReadAsync(ms);
        Subgraph subgraph = graph.PrimarySubgraph!;

        TensorEdge? weightEdge = subgraph.Tensors.FirstOrDefault(t => t.Name == "p_w");
        await Assert.That(weightEdge).IsNotNull();
        await Assert.That(weightEdge!.Kind).IsEqualTo(TensorKind.Initializer);
        await Assert.That(weightEdge.Data).IsNotNull();
        await Assert.That(weightEdge.Data).IsTypeOf<TensorDataSlice>();

        var slice = (TensorDataSlice)weightEdge.Data!;
        await Assert.That(slice.ByteLength).IsEqualTo(32);
        await Assert.That(slice.DataType).IsEqualTo(DataType.Float32);
        await Assert.That(slice.StreamOffset).IsGreaterThan(0);
        await Assert.That(slice.StreamOffset + slice.ByteLength).IsLessThanOrEqualTo(ms.Length);

        // Verify that the slice points to valid uncompressed bytes in the stream
        ms.Position = slice.StreamOffset;
        byte[] readBytes = new byte[slice.ByteLength];
        int read = await ms.ReadAsync(readBytes);
        await Assert.That(read).IsEqualTo(32);
        for (int i = 0; i < 32; i++)
        {
            await Assert.That(readBytes[i]).IsEqualTo((byte)0xAA);
        }
    }

    [Test]
    public async Task ReadAsync_CorruptedAndMalformedArchives_ThrowsModelParseException()
    {
        // 1. Missing model.json
        byte[] missingModelZip = BuildZipArchive(("archive_format", "pt2"u8.ToArray()));
        using (MemoryStream ms = new(missingModelZip))
        {
            await Assert.That(async () => await PyTorchPt2FormatReader.Instance.ReadAsync(ms))
                .Throws<ModelParseException>();
        }

        // 2. Malformed JSON in model.json
        byte[] corruptJsonZip = BuildZipArchive(
            ("archive_format", "pt2"u8.ToArray()),
            ("models/model.json", "{ malformed json"u8.ToArray()));
        using (MemoryStream ms = new(corruptJsonZip))
        {
            await Assert.That(async () => await PyTorchPt2FormatReader.Instance.ReadAsync(ms))
                .Throws<ModelParseException>();
        }

        // 3. Corrupt EOCD / truncated archive
        byte[] corruptZip = [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00];
        using (MemoryStream ms = new(corruptZip))
        {
            await Assert.That(async () => await PyTorchPt2FormatReader.Instance.ReadAsync(ms))
                .Throws<ModelParseException>();
        }
    }

    private static byte[] BuildSyntheticPt2Zip()
    {
        using MemoryStream ms = new();
        using (ZipArchive archive = new(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // archive_format
            var afEntry = archive.CreateEntry("archive_format", CompressionLevel.NoCompression);
            using (var s = afEntry.Open())
            {
                s.Write("pt2"u8);
            }

            // data/weights/weight_0 (32 bytes = 8 floats)
            var wEntry = archive.CreateEntry("data/weights/weight_0", CompressionLevel.NoCompression);
            using (var s = wEntry.Open())
            {
                byte[] weightBytes = new byte[32];
                Array.Fill(weightBytes, (byte)0xAA);
                s.Write(weightBytes);
            }

            // data/weights/model_weights_config.json
            var wcEntry = archive.CreateEntry("data/weights/model_weights_config.json", CompressionLevel.NoCompression);
            using (var s = wcEntry.Open())
            {
                string wcJson = """
                {
                  "config": {
                    "p_w": {
                      "path_name": "weight_0"
                    }
                  }
                }
                """;
                s.Write(Encoding.UTF8.GetBytes(wcJson));
            }

            // models/model.json
            var mEntry = archive.CreateEntry("models/model.json", CompressionLevel.NoCompression);
            using (var s = mEntry.Open())
            {
                string modelJson = """
                {
                  "torch_version": "2.14.1",
                  "schema_version": 2,
                  "range_constraints": {
                    "s0": {
                      "min_val": 1,
                      "max_val": 32
                    }
                  },
                  "graph_module": {
                    "signature": {
                      "input_specs": [
                        {
                          "parameter": {
                            "arg": { "name": "p_w" },
                            "parameter_name": "p_w"
                          }
                        },
                        {
                          "user_input": {
                            "arg": { "as_tensor": { "name": "x" } }
                          }
                        }
                      ],
                      "output_specs": [
                        {
                          "user_output": {
                            "arg": { "as_tensor": { "name": "y" } }
                          }
                        }
                      ]
                    },
                    "graph": {
                      "tensor_values": {
                        "p_w": {
                          "dtype": 7,
                          "sizes": [{ "as_int": 2 }, { "as_int": 4 }]
                        },
                        "x": {
                          "dtype": 7,
                          "sizes": [{ "as_sym_int": "s0" }, { "as_int": 4 }]
                        },
                        "y": {
                          "dtype": 7,
                          "sizes": [{ "as_sym_int": "s0" }, { "as_int": 2 }]
                        }
                      },
                      "nodes": [
                        {
                          "name": "linear",
                          "target": "torch.ops.aten.linear.default",
                          "inputs": [
                            { "name": "input", "arg": { "as_tensor": { "name": "x" } } },
                            { "name": "weight", "arg": { "as_tensor": { "name": "p_w" } } }
                          ],
                          "outputs": [
                            { "as_tensor": { "name": "y" } }
                          ]
                        }
                      ]
                    }
                  }
                }
                """;
                s.Write(Encoding.UTF8.GetBytes(modelJson));
            }
        }

        return ms.ToArray();
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

