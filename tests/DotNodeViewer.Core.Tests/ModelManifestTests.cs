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

    /// <summary>
    /// Enumerates all ONNX model entries registered in <c>Fixtures/models.manifest.json</c>.
    /// </summary>
    /// <returns>The sequence of registered ONNX <see cref="ModelManifestEntry"/> instances.</returns>
    public static IEnumerable<ModelManifestEntry> GetRegisteredOnnxModels() =>
        ModelFixtureLoader.LoadManifest().Models.Where(m => m.Format == "onnx");

    /// <summary>
    /// Enumerates all TensorFlow Lite model entries registered in <c>Fixtures/models.manifest.json</c>.
    /// </summary>
    /// <returns>The sequence of registered TensorFlow Lite <see cref="ModelManifestEntry"/> instances.</returns>
    public static IEnumerable<ModelManifestEntry> GetRegisteredTfliteModels() =>
        ModelFixtureLoader.LoadManifest().Models.Where(m => m.Format == "tflite");

    /// <summary>
    /// Enumerates all ExecuTorch model entries registered in <c>Fixtures/models.manifest.json</c>.
    /// </summary>
    /// <returns>The sequence of registered ExecuTorch <see cref="ModelManifestEntry"/> instances.</returns>
    public static IEnumerable<ModelManifestEntry> GetRegisteredExecuTorchModels() =>
        ModelFixtureLoader.LoadManifest().Models.Where(m => m.Format == "executorch");

    [Test]
    [MethodDataSource(nameof(GetRegisteredOnnxModels))]
    public async Task ParseRegisteredOnnxModel_MatchesExpectedTopology(ModelManifestEntry entry) =>
        await AssertModelMatchesExpectedTopologyAsync(entry);

    [Test]
    [MethodDataSource(nameof(GetRegisteredTfliteModels))]
    public async Task ParseRegisteredTfliteModel_MatchesExpectedTopology(ModelManifestEntry entry) =>
        await AssertModelMatchesExpectedTopologyAsync(entry);

    [Test]
    [MethodDataSource(nameof(GetRegisteredExecuTorchModels))]
    public async Task ParseRegisteredExecuTorchModel_MatchesExpectedTopology(ModelManifestEntry entry) =>
        await AssertModelMatchesExpectedTopologyAsync(entry);

    private static async Task AssertModelMatchesExpectedTopologyAsync(ModelManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

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

    internal static async Task AssertSnapshotsMatchAsync(
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

