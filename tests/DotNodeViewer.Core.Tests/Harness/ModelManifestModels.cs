using System.Text.Json.Serialization;

namespace DotNodeViewer.Core.Tests.Harness;

/// <summary>
/// Represents the root document of <c>Fixtures/models.manifest.json</c>.
/// </summary>
/// <param name="SchemaVersion">The manifest schema version number (currently <c>1</c>).</param>
/// <param name="Models">The registered test model entries.</param>
public sealed record ModelManifestDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("models")] IReadOnlyList<ModelManifestEntry> Models);

/// <summary>
/// Represents a single committed or cached neural network model fixture registered in <c>Fixtures/models.manifest.json</c>.
/// </summary>
/// <param name="Id">The unique model identifier (for example, <c>"onnx-tiny-mlp"</c>).</param>
/// <param name="Format">The lowercase format slug (for example, <c>"onnx"</c> or <c>"tflite"</c>).</param>
/// <param name="FileName">The file name of the model binary.</param>
/// <param name="Tier">The storage tier (<c>"committed"</c> or <c>"cached"</c>).</param>
/// <param name="RelativePath">The path to the model binary relative to the test project directory.</param>
/// <param name="SourceUrl">An optional remote URL used to populate <c>.model-cache/</c> when the cached binary is absent locally.</param>
/// <param name="Sha256">The expected lowercase hexadecimal SHA-256 digest of the model binary.</param>
/// <param name="ByteLength">The expected exact file size in bytes.</param>
/// <param name="ExpectedSnapshot">The relative path to the golden <c>.expected.json</c> topology snapshot file.</param>
public sealed record ModelManifestEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("sourceUrl")] string? SourceUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("byteLength")] long ByteLength,
    [property: JsonPropertyName("expectedSnapshot")] string ExpectedSnapshot)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Id} ({Format}, {Tier})";
}

/// <summary>
/// Represents the golden expected topology snapshot (<c>&lt;model-id&gt;.expected.json</c>) for a test model.
/// </summary>
/// <param name="ModelId">The unique identifier of the model.</param>
/// <param name="Format">The formatted display name and version of the detected model format.</param>
/// <param name="SubgraphCount">The total number of subgraphs in the parsed <c>ModelGraph</c>.</param>
/// <param name="PrimarySubgraph">The topology snapshot of the primary subgraph (<c>Subgraphs[0]</c>).</param>
public sealed record ModelTopologySnapshot(
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("subgraphCount")] int SubgraphCount,
    [property: JsonPropertyName("primarySubgraph")] SubgraphTopologySnapshot PrimarySubgraph);

/// <summary>
/// Represents the topology summary of a model's primary subgraph inside a <see cref="ModelTopologySnapshot"/>.
/// </summary>
/// <param name="Name">The name of the primary subgraph.</param>
/// <param name="NodeCount">The total number of operator nodes in the subgraph.</param>
/// <param name="OperatorHistogram">A map of operator names to their occurrence counts in the subgraph.</param>
/// <param name="Inputs">The ordered list of graph input tensor snapshots.</param>
/// <param name="Outputs">The ordered list of graph output tensor snapshots.</param>
/// <param name="SampleWeightSlice">An optional representative lazy weight slice verifying zero-copy stream offset extraction.</param>
public sealed record SubgraphTopologySnapshot(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("nodeCount")] int NodeCount,
    [property: JsonPropertyName("operatorHistogram")] IReadOnlyDictionary<string, int> OperatorHistogram,
    [property: JsonPropertyName("inputs")] IReadOnlyList<TensorPortSnapshot> Inputs,
    [property: JsonPropertyName("outputs")] IReadOnlyList<TensorPortSnapshot> Outputs,
    [property: JsonPropertyName("sampleWeightSlice"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WeightSliceSnapshot? SampleWeightSlice = null);

/// <summary>
/// Represents a single graph input or output tensor in a <see cref="SubgraphTopologySnapshot"/>.
/// </summary>
/// <param name="Name">The name of the input or output tensor.</param>
/// <param name="DataType">The string name of the tensor's <c>DataType</c>.</param>
/// <param name="Shape">The formatted dimension strings of the tensor shape, or <see langword="null"/> if unranked.</param>
/// <param name="Quantization">Optional affine quantization parameters attached to the tensor.</param>
public sealed record TensorPortSnapshot(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dataType")] string DataType,
    [property: JsonPropertyName("shape")] IReadOnlyList<string>? Shape,
    [property: JsonPropertyName("quantization"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QuantizationSnapshot? Quantization = null);

/// <summary>
/// Represents affine quantization scale and zero-point arrays inside a <see cref="TensorPortSnapshot"/>.
/// </summary>
/// <param name="Scale">The quantization scale factor(s).</param>
/// <param name="ZeroPoint">The quantization zero-point offset(s).</param>
/// <param name="QuantizedDimension">The optional quantized axis index.</param>
public sealed record QuantizationSnapshot(
    [property: JsonPropertyName("scale")] IReadOnlyList<float> Scale,
    [property: JsonPropertyName("zeroPoint")] IReadOnlyList<long> ZeroPoint,
    [property: JsonPropertyName("quantizedDimension"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? QuantizedDimension = null);

/// <summary>
/// Represents a sample lazy weight slice recorded inside a <see cref="SubgraphTopologySnapshot"/>.
/// </summary>
/// <param name="TensorName">The name of the initializer or constant tensor.</param>
/// <param name="DataType">The element data type name of the slice.</param>
/// <param name="StreamOffset">The zero-based byte offset of the slice in the model stream.</param>
/// <param name="ByteLength">The byte length of the slice in the model stream.</param>
public sealed record WeightSliceSnapshot(
    [property: JsonPropertyName("tensorName")] string TensorName,
    [property: JsonPropertyName("dataType")] string DataType,
    [property: JsonPropertyName("streamOffset")] long StreamOffset,
    [property: JsonPropertyName("byteLength")] long ByteLength);

/// <summary>
/// Provides source-generated JSON serialization metadata for manifest and topology snapshot types.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ModelManifestDocument))]
[JsonSerializable(typeof(ModelManifestEntry))]
[JsonSerializable(typeof(ModelTopologySnapshot))]
[JsonSerializable(typeof(SubgraphTopologySnapshot))]
[JsonSerializable(typeof(TensorPortSnapshot))]
[JsonSerializable(typeof(QuantizationSnapshot))]
[JsonSerializable(typeof(WeightSliceSnapshot))]
internal sealed partial class HarnessJsonContext : JsonSerializerContext;

