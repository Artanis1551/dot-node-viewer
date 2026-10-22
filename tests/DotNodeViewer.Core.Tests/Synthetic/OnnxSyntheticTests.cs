using System.Buffers.Binary;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Formats.Onnx;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Synthetic;

/// <summary>
/// Synthetic in-memory binary tests for <see cref="OnnxFormatDetector"/>, <see cref="OnnxFormatReader"/>, and <see cref="OnnxOperatorCatalog"/>.
/// </summary>
public sealed class OnnxSyntheticTests
{
    [Test]
    public async Task CanRead_ValidOnnxHeaderTruncatedAndRandomBytes_ReturnsExpectedResultAndRestoresPosition()
    {
        byte[] validModel = BuildSyntheticOnnxModel(rawWeightByteLength: 32, includeInitializerInGraphInput: false);
        using MemoryStream validStream = new(validModel);
        validStream.Position = 5;

        ModelProbe validProbe = ModelProbe.Create(validStream, "synthetic.onnx");
        bool canRead = OnnxFormatDetector.Instance.CanRead(validProbe);
        validProbe.RestorePosition();

        await Assert.That(canRead).IsTrue();
        await Assert.That(validStream.Position).IsEqualTo(5);

        ModelFormatInfo? detectedViaRegistry = ModelFormatRegistry.Default.TryDetect(validStream);
        await Assert.That(detectedViaRegistry).IsNotNull();
        await Assert.That(detectedViaRegistry!.Id).IsEqualTo(ModelFormatId.Onnx);
        await Assert.That(validStream.Position).IsEqualTo(5);

        // Truncated header / mid-graph bytes
        using MemoryStream truncatedStream = new(validModel[..10]);
        ModelProbe truncatedProbe = ModelProbe.Create(truncatedStream, "truncated.onnx");
        await Assert.That(OnnxFormatDetector.Instance.CanRead(truncatedProbe)).IsFalse();

        // Random / non-ONNX magic bytes
        using MemoryStream randomStream = new([0xDE, 0xAD, 0xBE, 0xEF, 0xFF, 0xFF, 0xFF, 0x7F]);
        ModelProbe randomProbe = ModelProbe.Create(randomStream, "random.onnx");
        await Assert.That(OnnxFormatDetector.Instance.CanRead(randomProbe)).IsFalse();

        // TFLite / HDF5 signatures must be rejected even with .onnx extension
        using MemoryStream tfliteStream = new([0x18, 0x00, 0x00, 0x00, (byte)'T', (byte)'F', (byte)'L', (byte)'3', 0, 0, 0, 0]);
        ModelProbe tfliteProbe = ModelProbe.Create(tfliteStream, "fake.onnx");
        await Assert.That(OnnxFormatDetector.Instance.CanRead(tfliteProbe)).IsFalse();
    }

    [Test]
    public async Task ReadAsync_TopologySymbolicDynamicUnrankedShapesAndIrV3Initializers_MapsExpectedGraph()
    {
        byte[] modelBytes = BuildSyntheticOnnxModel(rawWeightByteLength: 64, includeInitializerInGraphInput: true);
        using MemoryStream stream = new(modelBytes);

        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "ir_v3_test.onnx" });

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.Onnx);
        await Assert.That(graph.Format.Version).IsEqualTo("IR v3");
        await Assert.That(graph.Producer).IsEqualTo("dnv-synth 1.0");
        await Assert.That(graph.Metadata["model_author"]).IsEqualTo("dot-node-viewer");
        await Assert.That(graph.Subgraphs.Count).IsEqualTo(2); // main graph + 1 FunctionProto

        Subgraph main = graph.PrimarySubgraph!;
        await Assert.That(main.Name).IsEqualTo("synth_main");
        await Assert.That(main.Nodes.Count).IsEqualTo(2);

        // Initializer "W" was also listed in GraphProto.input (ONNX IR v3), so only "X" and "aux_unranked" should remain in GraphInput ports.
        await Assert.That(main.Inputs.Count).IsEqualTo(2);
        TensorEdge xInput = main.Inputs[0].Tensors[0];
        await Assert.That(xInput.Name).IsEqualTo("X");
        await Assert.That(xInput.Kind).IsEqualTo(TensorKind.GraphInput);
        await Assert.That(xInput.Descriptor.DataType).IsEqualTo(DataType.Float32);
        await Assert.That(xInput.Descriptor.Shape).IsNotNull();
        await Assert.That(xInput.Descriptor.Shape!.ToDisplayString()).IsEqualTo("[batch, 3, ?, 224]");

        TensorEdge unrankedInput = main.Inputs[1].Tensors[0];
        await Assert.That(unrankedInput.Name).IsEqualTo("aux_unranked");
        await Assert.That(unrankedInput.Descriptor.Shape).IsNull();

        GraphNode convNode = main.Nodes[0];
        await Assert.That(convNode.Name).IsEqualTo("conv_0");
        await Assert.That(convNode.Operator.Name).IsEqualTo("Conv");
        await Assert.That(convNode.Operator.Category).IsEqualTo(OperatorCategory.Layer);
        await Assert.That(convNode.Operator.Domain).IsEqualTo("ai.onnx");
        await Assert.That(convNode.Operator.Version).IsEqualTo(18);
        await Assert.That(convNode.Inputs[0].Name).IsEqualTo("X");
        await Assert.That(convNode.Inputs[1].Name).IsEqualTo("W");
        await Assert.That(convNode.Inputs[1].Tensors[0].Kind).IsEqualTo(TensorKind.Initializer);

        GraphNode reluNode = main.Nodes[1];
        await Assert.That(reluNode.Operator.Name).IsEqualTo("Relu");
        await Assert.That(reluNode.Operator.Category).IsEqualTo(OperatorCategory.Activation);

        Subgraph funcSubgraph = graph.Subgraphs[1];
        await Assert.That(funcSubgraph.Name).IsEqualTo("custom.domain.MyGelu");
        await Assert.That(funcSubgraph.Nodes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ReadAsync_SimulatedLargeWeightBuffer_RecordsExactSliceWithoutAllocatingWeightPayload()
    {
        const int weightBytes = 2 * 1024 * 1024; // 2 MB weight buffer
        byte[] modelBytes = BuildSyntheticOnnxModel(
            rawWeightByteLength: weightBytes,
            includeInitializerInGraphInput: false,
            out long expectedWeightOffset);

        using MemoryStream stream = new(modelBytes);

        // Warm up JIT once before measuring thread allocations.
        _ = await OnnxFormatReader.Instance.ReadAsync(stream);
        stream.Position = 0;

        (ModelGraph graph, long allocatedBytes) = await AllocationProbe.MeasureAsync(
            () => OnnxFormatReader.Instance.ReadAsync(stream));

        Subgraph main = graph.PrimarySubgraph!;
        GraphIndex index = new(main);
        TensorEdge? weightEdge = index.GetTensorByName("W");

        await Assert.That(weightEdge).IsNotNull();
        await Assert.That(weightEdge!.Kind).IsEqualTo(TensorKind.Initializer);
        await Assert.That(weightEdge.Data).IsTypeOf<TensorDataSlice>();

        TensorDataSlice slice = (TensorDataSlice)weightEdge.Data!;
        await Assert.That(slice.StreamOffset).IsEqualTo(expectedWeightOffset);
        await Assert.That(slice.ByteLength).IsEqualTo(weightBytes);
        await Assert.That(slice.DataType).IsEqualTo(DataType.Float32);
        await Assert.That(slice.Encoding).IsEqualTo(TensorDataEncoding.Raw);

        // Parsing a 2 MB weight model must allocate well under 10% (< 200 KB) on the managed heap.
        await Assert.That(allocatedBytes < weightBytes / 10).IsTrue();
    }

    [Test]
    public async Task ReadAsync_TruncatedAndCorruptedStreams_ThrowsModelParseException()
    {
        byte[] validModel = BuildSyntheticOnnxModel(rawWeightByteLength: 128, includeInitializerInGraphInput: false);

        // 1. Empty stream
        using MemoryStream emptyStream = new([]);
        await Assert.That(async () => await OnnxFormatReader.Instance.ReadAsync(emptyStream))
            .Throws<ModelParseException>();

        // 2. Truncated mid-weight payload
        using MemoryStream truncatedPayload = new(validModel[..(validModel.Length - 20)]);
        await Assert.That(async () => await OnnxFormatReader.Instance.ReadAsync(truncatedPayload))
            .Throws<ModelParseException>();

        // 3. Corrupted length prefix claiming 1 GB inside GraphProto
        byte[] hugeLengthClaim = [0x08, 0x09, 0x42, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x01, 0x02];
        using MemoryStream corruptedLengthStream = new(hugeLengthClaim);
        await Assert.That(async () => await OnnxFormatReader.Instance.ReadAsync(corruptedLengthStream))
            .Throws<ModelParseException>();

        // 4. ModelProto missing GraphProto (only ir_version field)
        using MemoryStream missingGraphStream = new([0x08, 0x09]);
        await Assert.That(async () => await OnnxFormatReader.Instance.ReadAsync(missingGraphStream))
            .Throws<ModelParseException>();
    }

    private static byte[] BuildSyntheticOnnxModel(
        int rawWeightByteLength,
        bool includeInitializerInGraphInput) =>
        BuildSyntheticOnnxModel(rawWeightByteLength, includeInitializerInGraphInput, out _);

    private static byte[] BuildSyntheticOnnxModel(
        int rawWeightByteLength,
        bool includeInitializerInGraphInput,
        out long weightPayloadFileOffset)
    {
        // Build GraphProto payload:
        // Node 0: Conv(X, W) -> conv_out, with strides=[1, 1], kernel_shape=[3, 3], group=1, scale=0.5f, auto_pad="NOTSET"
        byte[] attrStrides = BuildAttributeInts("strides", [1, 1]);
        byte[] attrPads = BuildAttributeString("auto_pad", "NOTSET");
        byte[] attrGroup = BuildAttributeInt("group", 1);
        byte[] attrAlpha = BuildAttributeFloat("alpha", 0.5f);
        byte[] convNode = BuildNodeProto("conv_0", "Conv", "", ["X", "W", ""], ["conv_out"], [attrStrides, attrPads, attrGroup, attrAlpha]);
        byte[] reluNode = BuildNodeProto("relu_0", "Relu", "", ["conv_out"], ["Y"], []);

        // Input X: float32 [batch, 3, ?, 224]
        byte[] dimBatch = BuildDimensionSymbolic("batch");
        byte[] dimC = BuildDimensionFixed(3);
        byte[] dimDynamic = []; // empty Dimension -> Dynamic
        byte[] dimW = BuildDimensionFixed(224);
        byte[] inputX = BuildValueInfo("X", elemType: 1, [dimBatch, dimC, dimDynamic, dimW]);

        // Input aux_unranked: float32 with no shape message (unranked)
        byte[] inputUnranked = BuildValueInfoUnranked("aux_unranked", elemType: 1);

        // Output Y: float32 [batch, 16, ?, 224]
        byte[] outputY = BuildValueInfo("Y", elemType: 1, [dimBatch, BuildDimensionFixed(16), dimDynamic, dimW]);

        // Initializer W: float32 [16, 3, 3, 3] with raw_data of length rawWeightByteLength
        byte[] initW = BuildTensorProtoRaw("W", elemType: 1, [16, 3, 3, 3], rawWeightByteLength, out int rawOffsetInInitW);

        using MemoryStream graphBody = new();
        WriteLengthDelimited(graphBody, 1, convNode);
        WriteLengthDelimited(graphBody, 1, reluNode);
        WriteStringField(graphBody, 2, "synth_main");

        // Record where initW will sit inside graphBody
        WriteTag(graphBody, 5, 2);
        WriteVarint(graphBody, (ulong)initW.Length);
        int initWStartInGraph = (int)graphBody.Position;
        graphBody.Write(initW);

        WriteLengthDelimited(graphBody, 11, inputX);
        WriteLengthDelimited(graphBody, 11, inputUnranked);
        if (includeInitializerInGraphInput)
        {
            byte[] initWInput = BuildValueInfo("W", elemType: 1, [BuildDimensionFixed(16), BuildDimensionFixed(3), BuildDimensionFixed(3), BuildDimensionFixed(3)]);
            WriteLengthDelimited(graphBody, 11, initWInput);
        }

        WriteLengthDelimited(graphBody, 12, outputY);
        byte[] graphBytes = graphBody.ToArray();

        // Build FunctionProto (field 25)
        byte[] funcNode = BuildNodeProto("gelu_inner", "Gelu", "", ["fx"], ["fy"], []);
        using MemoryStream funcBody = new();
        WriteStringField(funcBody, 1, "MyGelu");
        WriteStringField(funcBody, 4, "fx");
        WriteStringField(funcBody, 5, "fy");
        WriteLengthDelimited(funcBody, 7, funcNode);
        WriteStringField(funcBody, 10, "custom.domain");
        byte[] funcBytes = funcBody.ToArray();

        // Build ModelProto
        using MemoryStream model = new();
        // Field 1: ir_version = 3
        WriteTag(model, 1, 0);
        WriteVarint(model, 3);

        // Field 2: opset_import ("", 18)
        using (MemoryStream opset = new())
        {
            WriteStringField(opset, 1, "");
            WriteTag(opset, 2, 0);
            WriteVarint(opset, 18);
            WriteLengthDelimited(model, 2, opset.ToArray());
        }

        // Field 3 & 4: producer_name & producer_version
        WriteStringField(model, 3, "dnv-synth");
        WriteStringField(model, 4, "1.0");

        // Field 14: metadata_props ("model_author" = "dot-node-viewer")
        using (MemoryStream meta = new())
        {
            WriteStringField(meta, 1, "model_author");
            WriteStringField(meta, 2, "dot-node-viewer");
            WriteLengthDelimited(model, 14, meta.ToArray());
        }

        // Field 8: graph
        WriteTag(model, 8, 2);
        WriteVarint(model, (ulong)graphBytes.Length);
        long graphStartInModel = model.Position;
        model.Write(graphBytes);

        // Field 25: functions
        WriteLengthDelimited(model, 25, funcBytes);

        weightPayloadFileOffset = graphStartInModel + initWStartInGraph + rawOffsetInInitW;
        return model.ToArray();
    }

    private static byte[] BuildTensorProtoRaw(
        string name,
        int elemType,
        long[] dims,
        int rawByteLength,
        out int rawPayloadOffsetInTensor)
    {
        using MemoryStream ms = new();
        foreach (long d in dims)
        {
            WriteTag(ms, 1, 0);
            WriteVarint(ms, (ulong)d);
        }

        WriteTag(ms, 2, 0);
        WriteVarint(ms, (ulong)elemType);
        WriteStringField(ms, 8, name);

        WriteTag(ms, 9, 2);
        WriteVarint(ms, (ulong)rawByteLength);
        rawPayloadOffsetInTensor = (int)ms.Position;
        byte[] payload = new byte[rawByteLength];
        if (rawByteLength >= 4)
        {
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(0, 4), 1.25f);
        }

        ms.Write(payload);
        return ms.ToArray();
    }

    private static byte[] BuildNodeProto(
        string name,
        string opType,
        string domain,
        string[] inputs,
        string[] outputs,
        byte[][] attributes)
    {
        using MemoryStream ms = new();
        foreach (string inp in inputs)
        {
            WriteStringField(ms, 1, inp);
        }

        foreach (string outp in outputs)
        {
            WriteStringField(ms, 2, outp);
        }

        WriteStringField(ms, 3, name);
        WriteStringField(ms, 4, opType);
        foreach (byte[] attr in attributes)
        {
            WriteLengthDelimited(ms, 5, attr);
        }

        if (!string.IsNullOrEmpty(domain))
        {
            WriteStringField(ms, 7, domain);
        }

        return ms.ToArray();
    }

    private static byte[] BuildAttributeInt(string name, long value)
    {
        using MemoryStream ms = new();
        WriteStringField(ms, 1, name);
        WriteTag(ms, 3, 0);
        WriteVarint(ms, unchecked((ulong)value));
        WriteTag(ms, 20, 0);
        WriteVarint(ms, 2); // INT
        return ms.ToArray();
    }

    private static byte[] BuildAttributeFloat(string name, float value)
    {
        using MemoryStream ms = new();
        WriteStringField(ms, 1, name);
        WriteTag(ms, 2, 5);
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buf, value);
        ms.Write(buf);
        WriteTag(ms, 20, 0);
        WriteVarint(ms, 1); // FLOAT
        return ms.ToArray();
    }

    private static byte[] BuildAttributeString(string name, string value)
    {
        using MemoryStream ms = new();
        WriteStringField(ms, 1, name);
        WriteStringField(ms, 4, value);
        WriteTag(ms, 20, 0);
        WriteVarint(ms, 3); // STRING
        return ms.ToArray();
    }

    private static byte[] BuildAttributeInts(string name, long[] values)
    {
        using MemoryStream ms = new();
        WriteStringField(ms, 1, name);
        foreach (long v in values)
        {
            WriteTag(ms, 8, 0);
            WriteVarint(ms, unchecked((ulong)v));
        }

        WriteTag(ms, 20, 0);
        WriteVarint(ms, 7); // INTS
        return ms.ToArray();
    }

    private static byte[] BuildValueInfo(string name, int elemType, byte[][] dimMessages)
    {
        using MemoryStream shapeMs = new();
        foreach (byte[] dim in dimMessages)
        {
            WriteLengthDelimited(shapeMs, 1, dim);
        }

        using MemoryStream tensorTypeMs = new();
        WriteTag(tensorTypeMs, 1, 0);
        WriteVarint(tensorTypeMs, (ulong)elemType);
        WriteLengthDelimited(tensorTypeMs, 2, shapeMs.ToArray());

        using MemoryStream typeMs = new();
        WriteLengthDelimited(typeMs, 1, tensorTypeMs.ToArray());

        using MemoryStream viMs = new();
        WriteStringField(viMs, 1, name);
        WriteLengthDelimited(viMs, 2, typeMs.ToArray());
        return viMs.ToArray();
    }

    private static byte[] BuildValueInfoUnranked(string name, int elemType)
    {
        using MemoryStream tensorTypeMs = new();
        WriteTag(tensorTypeMs, 1, 0);
        WriteVarint(tensorTypeMs, (ulong)elemType);

        using MemoryStream typeMs = new();
        WriteLengthDelimited(typeMs, 1, tensorTypeMs.ToArray());

        using MemoryStream viMs = new();
        WriteStringField(viMs, 1, name);
        WriteLengthDelimited(viMs, 2, typeMs.ToArray());
        return viMs.ToArray();
    }

    private static byte[] BuildDimensionFixed(long dimValue)
    {
        using MemoryStream ms = new();
        WriteTag(ms, 1, 0);
        WriteVarint(ms, (ulong)dimValue);
        return ms.ToArray();
    }

    private static byte[] BuildDimensionSymbolic(string dimParam)
    {
        using MemoryStream ms = new();
        WriteStringField(ms, 2, dimParam);
        return ms.ToArray();
    }

    private static void WriteStringField(Stream s, uint fieldNumber, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteLengthDelimited(s, fieldNumber, bytes);
    }

    private static void WriteLengthDelimited(Stream s, uint fieldNumber, ReadOnlySpan<byte> payload)
    {
        WriteTag(s, fieldNumber, 2);
        WriteVarint(s, (ulong)payload.Length);
        s.Write(payload);
    }

    private static void WriteTag(Stream s, uint fieldNumber, byte wireType) =>
        WriteVarint(s, ((ulong)fieldNumber << 3) | wireType);

    private static void WriteVarint(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        s.WriteByte((byte)value);
    }
}
