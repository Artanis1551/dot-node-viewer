using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Tests;

/// <summary>
/// Verifies the behavior, discriminated-union pattern matching, and invariants of the Unified Graph IR records in <see cref="DotNodeViewer.Core.Ir"/>.
/// </summary>
public sealed class UnifiedGraphIrTests
{
    [Test]
    public async Task TensorShape_ScalarUnrankedAndSymbolicDimensions_DistinguishesRankAndFormatsDisplayString()
    {
        TensorDescriptor unranked = new(DataType.Float32, Shape: null);
        TensorDescriptor scalar = new(DataType.Float32, TensorShape.Scalar);
        TensorShape rankedShape = new(
        [
            new TensorDimension.Symbolic("batch"),
            new TensorDimension.Fixed(3),
            new TensorDimension.Fixed(224),
            TensorDimension.Dynamic.Instance,
        ]);

        await Assert.That(unranked.Shape).IsNull();
        await Assert.That(scalar.Shape).IsNotNull();
        await Assert.That(scalar.Shape!.Rank).IsEqualTo(0);
        await Assert.That(scalar.Shape.IsScalar).IsTrue();
        await Assert.That(scalar.Shape.ToDisplayString()).IsEqualTo("[]");

        await Assert.That(rankedShape.Rank).IsEqualTo(4);
        await Assert.That(rankedShape.IsScalar).IsFalse();
        await Assert.That(rankedShape.ToDisplayString()).IsEqualTo("[batch, 3, 224, ?]");
    }

    [Test]
    public async Task AttributeValue_AllThirteenDiscriminatedUnionCases_SupportExhaustivePatternMatching()
    {
        Subgraph childSubgraph = new("branch", [], [], [], []);
        TensorEdge constTensor = new("c", new TensorDescriptor(DataType.Int32, TensorShape.Scalar), TensorKind.Constant);
        TensorDataSlice payloadSlice = new(StreamOffset: 128, ByteLength: 64, DataType.UInt8);

        AttributeValue[] values =
        [
            new AttributeValue.Int64(42),
            new AttributeValue.Float64(3.14),
            new AttributeValue.Bool(true),
            new AttributeValue.String("SAME_UPPER"),
            new AttributeValue.Int64List([1, 1]),
            new AttributeValue.Float64List([0.5, 0.5]),
            new AttributeValue.StringList(["a", "b"]),
            new AttributeValue.DataTypeValue(DataType.BFloat16),
            new AttributeValue.ShapeValue(new TensorShape([new TensorDimension.Fixed(16)])),
            new AttributeValue.Tensor(constTensor),
            new AttributeValue.Graph(childSubgraph),
            new AttributeValue.GraphList([childSubgraph]),
            new AttributeValue.Bytes(payloadSlice),
        ];

        await Assert.That(values.Length).IsEqualTo(13);

        foreach (AttributeValue val in values)
        {
            string tag = val switch
            {
                AttributeValue.Int64 v => $"i64:{v.Value}",
                AttributeValue.Float64 v => $"f64:{v.Value}",
                AttributeValue.Bool v => $"bool:{v.Value}",
                AttributeValue.String v => $"str:{v.Value}",
                AttributeValue.Int64List v => $"i64[]:{v.Values.Count}",
                AttributeValue.Float64List v => $"f64[]:{v.Values.Count}",
                AttributeValue.StringList v => $"str[]:{v.Values.Count}",
                AttributeValue.DataTypeValue v => $"dtype:{v.Value}",
                AttributeValue.ShapeValue v => $"shape:{v.Value.Rank}",
                AttributeValue.Tensor v => $"tensor:{v.Value.Name}",
                AttributeValue.Graph v => $"graph:{v.Value.Name}",
                AttributeValue.GraphList v => $"graphs:{v.Values.Count}",
                AttributeValue.Bytes v => $"bytes:{v.Slice.ByteLength}",
                _ => "unknown",
            };

            await Assert.That(tag).IsNotEqualTo("unknown");
        }
    }

    [Test]
    public async Task QuantizationAndTensorDataSource_ClosedHierarchies_PreserveMetadata()
    {
        QuantizationInfo affine = new AffineQuantization([0.125f, 0.25f], [0, 128], QuantizedDimension: 0);
        QuantizationInfo block = new BlockQuantization("Q4_0", BlockSize: 32, Bits: 4);

        TensorDataSource slice = new TensorDataSlice(1024, 4096, DataType.Int8, TensorDataEncoding.Raw);
        TensorDataSource external = new ExternalTensorDataReference("weights.bin", Offset: 0, Length: 8192, DataType.Float16);

        await Assert.That(affine is AffineQuantization { QuantizedDimension: 0 }).IsTrue();
        await Assert.That(block is BlockQuantization { Scheme: "Q4_0", BlockSize: 32, Bits: 4 }).IsTrue();
        await Assert.That(slice is TensorDataSlice { StreamOffset: 1024, ByteLength: 4096 }).IsTrue();
        await Assert.That(external is ExternalTensorDataReference { RelativePath: "weights.bin", Length: 8192 }).IsTrue();
    }

    [Test]
    public async Task ModelFormatInfo_WellKnownFormatIdsAndDisplayStrings_FormatCleanly()
    {
        ModelFormatInfo onnx = new(ModelFormatId.Onnx, "ONNX", "IR v9");
        ModelFormatInfo tflite = new(ModelFormatId.Tflite, "TensorFlow Lite");

        await Assert.That(onnx.Id.ToString()).IsEqualTo("onnx");
        await Assert.That(onnx.ToDisplayString()).IsEqualTo("ONNX IR v9");
        await Assert.That(tflite.ToDisplayString()).IsEqualTo("TensorFlow Lite");
        await Assert.That(ModelFormatId.ExecuTorch.Value).IsEqualTo("executorch");
        await Assert.That(ModelFormatId.PyTorch.Value).IsEqualTo("pytorch");
        await Assert.That(ModelFormatId.PyTorchPt2.Value).IsEqualTo("pytorch-pt2");
        await Assert.That(ModelFormatId.Keras.Value).IsEqualTo("keras");
    }
}

