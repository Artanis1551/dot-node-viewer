using System.Buffers.Binary;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Formats.Tflite;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Synthetic;

/// <summary>
/// Synthetic in-memory binary tests for <see cref="TfliteFormatDetector"/>, <see cref="TfliteFormatReader"/>, and <see cref="TfliteOperatorCatalog"/>.
/// </summary>
public sealed class TfliteSyntheticTests
{
    [Test]
    public async Task CanRead_ValidTfl3HeaderTruncatedAndRandomBytes_ReturnsExpectedResultAndRestoresPosition()
    {
        byte[] validModel = BuildSyntheticTfliteModel(weightBytes: [1, 2, 3, 4, 5, 6, 7, 8]);
        using MemoryStream validStream = new(validModel);
        validStream.Position = 6;

        ModelProbe validProbe = ModelProbe.Create(validStream, "synthetic.tflite");
        bool canRead = TfliteFormatDetector.Instance.CanRead(validProbe);
        validProbe.RestorePosition();

        await Assert.That(canRead).IsTrue();
        await Assert.That(validStream.Position).IsEqualTo(6);

        ModelFormatInfo? detectedViaRegistry = ModelFormatRegistry.Default.TryDetect(validStream);
        await Assert.That(detectedViaRegistry).IsNotNull();
        await Assert.That(detectedViaRegistry!.Id).IsEqualTo(ModelFormatId.Tflite);
        await Assert.That(validStream.Position).IsEqualTo(6);

        // Truncated header (< 12 bytes)
        using MemoryStream truncatedStream = new(validModel[..8]);
        ModelProbe truncatedProbe = ModelProbe.Create(truncatedStream, "truncated.tflite");
        await Assert.That(TfliteFormatDetector.Instance.CanRead(truncatedProbe)).IsFalse();

        // Wrong magic identifier
        byte[] wrongMagic = (byte[])validModel.Clone();
        wrongMagic[4] = (byte)'O';
        wrongMagic[5] = (byte)'N';
        wrongMagic[6] = (byte)'N';
        wrongMagic[7] = (byte)'X';
        using MemoryStream wrongMagicStream = new(wrongMagic);
        ModelProbe wrongMagicProbe = ModelProbe.Create(wrongMagicStream, "wrong.tflite");
        await Assert.That(TfliteFormatDetector.Instance.CanRead(wrongMagicProbe)).IsFalse();

        // Valid TFL3 magic but out-of-bounds root table offset
        byte[] badRoot = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(badRoot.AsSpan(0, 4), 1024);
        "TFL3"u8.CopyTo(badRoot.AsSpan(4, 4));
        using MemoryStream badRootStream = new(badRoot);
        ModelProbe badRootProbe = ModelProbe.Create(badRootStream, "bad_root.tflite");
        await Assert.That(TfliteFormatDetector.Instance.CanRead(badRootProbe)).IsFalse();
    }

    [Test]
    public async Task ReadAsync_TopologyQuantizationFusedActivationsAndCustomDelegate_MapsExpectedGraph()
    {
        byte[] rawWeights = [10, 20, 30, 40, 50, 60, 70, 80];
        byte[] customDelegatePayload = [0xAA, 0xBB, 0xCC, 0xDD];
        byte[] modelBytes = BuildSyntheticTfliteModel(rawWeights, customDelegatePayload);

        using MemoryStream stream = new(modelBytes);
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "synth.tflite" });

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.Tflite);
        await Assert.That(graph.Format.ToDisplayString()).IsEqualTo("TensorFlow Lite v3");
        await Assert.That(graph.Producer).IsEqualTo("dnv-tflite-synth");
        await Assert.That(graph.Subgraphs.Count).IsEqualTo(1);

        Subgraph main = graph.PrimarySubgraph!;
        await Assert.That(main.Name).IsEqualTo("main");
        await Assert.That(main.Nodes.Count).IsEqualTo(2);
        await Assert.That(main.Inputs.Count).IsEqualTo(1);
        await Assert.That(main.Outputs.Count).IsEqualTo(1);

        // Input tensor has shape_signature [-1, 28, 28, 1] -> [?, 28, 28, 1]
        TensorEdge inputEdge = main.Inputs[0].Tensors[0];
        await Assert.That(inputEdge.Name).IsEqualTo("input_0");
        await Assert.That(inputEdge.Kind).IsEqualTo(TensorKind.GraphInput);
        await Assert.That(inputEdge.Descriptor.DataType).IsEqualTo(DataType.Int8);
        await Assert.That(inputEdge.Descriptor.Shape).IsNotNull();
        await Assert.That(inputEdge.Descriptor.Shape!.ToDisplayString()).IsEqualTo("[?, 28, 28, 1]");

        // Weight tensor has per-channel AffineQuantization (2 channels along axis 0) and lazy TensorDataSlice
        TensorEdge weightEdge = main.Tensors[1];
        await Assert.That(weightEdge.Name).IsEqualTo("conv_weights");
        await Assert.That(weightEdge.Kind).IsEqualTo(TensorKind.Initializer);
        await Assert.That(weightEdge.Descriptor.DataType).IsEqualTo(DataType.Int8);
        await Assert.That(weightEdge.Descriptor.Quantization).IsTypeOf<AffineQuantization>();

        AffineQuantization quant = (AffineQuantization)weightEdge.Descriptor.Quantization!;
        await Assert.That(quant.Scale.Count).IsEqualTo(2);
        await Assert.That(quant.Scale[0]).IsEqualTo(0.125f);
        await Assert.That(quant.Scale[1]).IsEqualTo(0.25f);
        await Assert.That(quant.ZeroPoint.Count).IsEqualTo(2);
        await Assert.That(quant.ZeroPoint[0]).IsEqualTo(0L);
        await Assert.That(quant.ZeroPoint[1]).IsEqualTo(-1L);
        await Assert.That(quant.QuantizedDimension).IsEqualTo(0);

        // Node 0: CONV_2D with padding=SAME, strides=[2, 2], fused_activation_function=RELU6, and omitted bias (-1)
        GraphNode convNode = main.Nodes[0];
        await Assert.That(convNode.Operator.Name).IsEqualTo("CONV_2D");
        await Assert.That(convNode.Operator.Category).IsEqualTo(OperatorCategory.Layer);
        await Assert.That(convNode.Inputs.Count).IsEqualTo(3);
        await Assert.That(convNode.Inputs[0].Name).IsEqualTo("input");
        await Assert.That(convNode.Inputs[1].Name).IsEqualTo("filter");
        await Assert.That(convNode.Inputs[2].Name).IsEqualTo("bias");
        await Assert.That(convNode.Inputs[2].Tensors.Count).IsEqualTo(0); // -1 optional bias omitted

        NodeAttribute? paddingAttr = convNode.Attributes.FirstOrDefault(a => a.Name == "padding");
        await Assert.That(paddingAttr).IsNotNull();
        await Assert.That(((AttributeValue.String)paddingAttr!.Value).Value).IsEqualTo("SAME");

        NodeAttribute? fusedActAttr = convNode.Attributes.FirstOrDefault(a => a.Name == "fused_activation_function");
        await Assert.That(fusedActAttr).IsNotNull();
        await Assert.That(((AttributeValue.String)fusedActAttr!.Value).Value).IsEqualTo("RELU6");

        // Node 1: CUSTOM operator ("ethos-u") with lazy custom_options slice
        GraphNode customNode = main.Nodes[1];
        await Assert.That(customNode.Operator.Name).IsEqualTo("CUSTOM(ethos-u)");
        await Assert.That(customNode.Operator.Category).IsEqualTo(OperatorCategory.Custom);
        await Assert.That(customNode.Operator.Domain).IsEqualTo("tflite.custom");

        NodeAttribute? customCodeAttr = customNode.Attributes.FirstOrDefault(a => a.Name == "custom_code");
        await Assert.That(customCodeAttr).IsNotNull();
        await Assert.That(((AttributeValue.String)customCodeAttr!.Value).Value).IsEqualTo("ethos-u");

        NodeAttribute? customOptsAttr = customNode.Attributes.FirstOrDefault(a => a.Name == "custom_options");
        await Assert.That(customOptsAttr).IsNotNull();
        TensorDataSlice customSlice = ((AttributeValue.Bytes)customOptsAttr!.Value).Slice;
        await Assert.That(customSlice.StreamOffset > 0).IsTrue();
        await Assert.That(customSlice.ByteLength).IsEqualTo(4);
    }

    [Test]
    public async Task ReadAsync_SimulatedLargeWeightBuffer_RecordsExactSliceWithoutAllocatingWeightPayload()
    {
        const int weightByteLength = 2 * 1024 * 1024; // 2 MB weight buffer
        byte[] expectedWeights = new byte[weightByteLength];
        expectedWeights[0] = 0x11;
        expectedWeights[weightByteLength - 1] = 0x7F;
        byte[] modelBytes = BuildSyntheticTfliteModel(expectedWeights);

        using MemoryStream stream = new(modelBytes);

        // Warm up JIT once before measuring thread allocations.
        _ = await TfliteFormatReader.Instance.ReadAsync(stream);
        stream.Position = 0;

        (ModelGraph graph, long allocatedBytes) = await AllocationProbe.MeasureAsync(
            () => TfliteFormatReader.Instance.ReadAsync(stream));

        TensorEdge weightEdge = graph.PrimarySubgraph!.Tensors[1];

        await Assert.That(weightEdge.Data).IsTypeOf<TensorDataSlice>();
        TensorDataSlice slice = (TensorDataSlice)weightEdge.Data!;
        await Assert.That(slice.StreamOffset > 0).IsTrue();
        await Assert.That(slice.ByteLength).IsEqualTo(weightByteLength);
        await Assert.That(slice.StreamOffset + slice.ByteLength <= modelBytes.Length).IsTrue();

        // Parsing a 2 MB weight model must allocate well under 10% (< 200 KB) on the managed heap.
        await Assert.That(allocatedBytes < weightByteLength / 10).IsTrue();

        byte firstByte = modelBytes[(int)slice.StreamOffset];
        byte lastByte = modelBytes[(int)(slice.StreamOffset + slice.ByteLength - 1)];
        await Assert.That(firstByte).IsEqualTo((byte)0x11);
        await Assert.That(lastByte).IsEqualTo((byte)0x7F);
    }

    [Test]
    public async Task ReadAsync_TruncatedOrMaliciousFlatBufferOffsets_ThrowsModelParseException()
    {
        byte[] validModel = BuildSyntheticTfliteModel([1, 2, 3, 4]);

        // 1. Mid-file truncation
        using MemoryStream truncatedStream = new(validModel[..(validModel.Length / 2)]);
        await Assert.That(async () => await TfliteFormatReader.Instance.ReadAsync(truncatedStream))
            .Throws<ModelParseException>();

        // 2. Corrupted root vtable offset pointing outside file
        byte[] corruptedRootVtable = (byte[])validModel.Clone();
        uint rootPos = BinaryPrimitives.ReadUInt32LittleEndian(corruptedRootVtable.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(corruptedRootVtable.AsSpan((int)rootPos, 4), -999999);
        using MemoryStream corruptedVtableStream = new(corruptedRootVtable);
        await Assert.That(async () => await TfliteFormatReader.Instance.ReadAsync(corruptedVtableStream))
            .Throws<ModelParseException>();

        // 3. Empty stream
        using MemoryStream emptyStream = new([]);
        await Assert.That(async () => await TfliteFormatReader.Instance.ReadAsync(emptyStream))
            .Throws<ModelParseException>();
    }

    /// <summary>
    /// Builds a valid, self-contained TFLite v3 FlatBuffer binary in memory for synthetic testing.
    /// </summary>
    private static byte[] BuildSyntheticTfliteModel(byte[] weightBytes, byte[]? customOptionsBytes = null)
    {
        byte[] customBytes = customOptionsBytes ?? [0x01, 0x02];
        MiniFlatBufferWriter writer = new();

        // 1. Strings & byte vectors
        int descStr = writer.WriteString("dnv-tflite-synth");
        int mainNameStr = writer.WriteString("main");
        int customCodeStr = writer.WriteString("ethos-u");
        int t0NameStr = writer.WriteString("input_0");
        int t1NameStr = writer.WriteString("conv_weights");
        int t2NameStr = writer.WriteString("conv_out");
        int t3NameStr = writer.WriteString("output_0");

        int weightVec = writer.WriteByteVector(weightBytes);
        int customOptsVec = writer.WriteByteVector(customBytes);

        // 2. Buffers: Buffer[0] (empty sentinel), Buffer[1] (weights), Buffer[2] (empty)
        int buf0 = writer.WriteTable([]);
        int buf1 = writer.WriteTable([(0, FieldType.UOffset, weightVec)]);
        int buf2 = writer.WriteTable([]);
        int buffersVec = writer.WriteOffsetVector([buf0, buf1, buf2]);

        // 3. OperatorCodes:
        //    OpCode[0] = CONV_2D (deprecated_builtin_code = 3, version = 1, builtin_code = 3)
        //    OpCode[1] = CUSTOM (deprecated_builtin_code = 32, custom_code = "ethos-u", version = 1, builtin_code = 32)
        int opCode0 = writer.WriteTable([
            (0, FieldType.Byte, 3),
            (2, FieldType.Int32, 1),
            (3, FieldType.Int32, 3),
        ]);
        int opCode1 = writer.WriteTable([
            (0, FieldType.Byte, 32),
            (1, FieldType.UOffset, customCodeStr),
            (2, FieldType.Int32, 1),
            (3, FieldType.Int32, 32),
        ]);
        int opCodesVec = writer.WriteOffsetVector([opCode0, opCode1]);

        // 4. QuantizationParameters for Tensor[1] (scales = [0.125f, 0.25f], zero_points = [0L, -1L], quantized_dimension = 0)
        int scalesVec = writer.WriteFloat32Vector([0.125f, 0.25f]);
        int zeroPointsVec = writer.WriteInt64Vector([0L, -1L]);
        int quant1 = writer.WriteTable([
            (2, FieldType.UOffset, scalesVec),
            (3, FieldType.UOffset, zeroPointsVec),
            (6, FieldType.Int32, 0),
        ]);

        // 5. Tensors:
        //    Tensor[0]: input_0, shape=[1, 28, 28, 1], shape_signature=[-1, 28, 28, 1], type=INT8 (9), buffer=0
        int shape0 = writer.WriteInt32Vector([1, 28, 28, 1]);
        int sig0 = writer.WriteInt32Vector([-1, 28, 28, 1]);
        int tensor0 = writer.WriteTable([
            (0, FieldType.UOffset, shape0),
            (1, FieldType.Byte, 9),
            (2, FieldType.Int32, 0),
            (3, FieldType.UOffset, t0NameStr),
            (7, FieldType.UOffset, sig0),
        ]);

        //    Tensor[1]: conv_weights, shape=[2, 2, 2, 1], type=INT8 (9), buffer=1, quantization=quant1
        int shape1 = writer.WriteInt32Vector([2, 2, 2, 1]);
        int tensor1 = writer.WriteTable([
            (0, FieldType.UOffset, shape1),
            (1, FieldType.Byte, 9),
            (2, FieldType.Int32, 1),
            (3, FieldType.UOffset, t1NameStr),
            (4, FieldType.UOffset, quant1),
        ]);

        //    Tensor[2]: conv_out, shape=[1, 14, 14, 2], type=INT8 (9), buffer=0
        int shape2 = writer.WriteInt32Vector([1, 14, 14, 2]);
        int tensor2 = writer.WriteTable([
            (0, FieldType.UOffset, shape2),
            (1, FieldType.Byte, 9),
            (2, FieldType.Int32, 0),
            (3, FieldType.UOffset, t2NameStr),
        ]);

        //    Tensor[3]: output_0, shape=[1, 14, 14, 2], type=INT8 (9), buffer=0
        int shape3 = writer.WriteInt32Vector([1, 14, 14, 2]);
        int tensor3 = writer.WriteTable([
            (0, FieldType.UOffset, shape3),
            (1, FieldType.Byte, 9),
            (2, FieldType.Int32, 0),
            (3, FieldType.UOffset, t3NameStr),
        ]);

        int tensorsVec = writer.WriteOffsetVector([tensor0, tensor1, tensor2, tensor3]);

        // 6. Operators:
        //    Op[0]: CONV_2D (opcode_index=0, inputs=[0, 1, -1], outputs=[2], Conv2DOptions: padding=SAME(0), stride_w=2, stride_h=2, fused_activation=RELU6(3))
        int conv2dOpts = writer.WriteTable([
            (0, FieldType.Byte, 0), // padding = SAME
            (1, FieldType.Int32, 2), // stride_w = 2
            (2, FieldType.Int32, 2), // stride_h = 2
            (3, FieldType.Byte, 3), // fused_activation_function = RELU6
            (4, FieldType.Int32, 1), // dilation_w = 1
            (5, FieldType.Int32, 1), // dilation_h = 1
        ]);
        int op0Inputs = writer.WriteInt32Vector([0, 1, -1]);
        int op0Outputs = writer.WriteInt32Vector([2]);
        int op0 = writer.WriteTable([
            (0, FieldType.Int32, 0),
            (1, FieldType.UOffset, op0Inputs),
            (2, FieldType.UOffset, op0Outputs),
            (3, FieldType.Byte, 1), // BuiltinOptions.Conv2DOptions
            (4, FieldType.UOffset, conv2dOpts),
        ]);

        //    Op[1]: CUSTOM (opcode_index=1, inputs=[2], outputs=[3], custom_options=customOptsVec)
        int op1Inputs = writer.WriteInt32Vector([2]);
        int op1Outputs = writer.WriteInt32Vector([3]);
        int op1 = writer.WriteTable([
            (0, FieldType.Int32, 1),
            (1, FieldType.UOffset, op1Inputs),
            (2, FieldType.UOffset, op1Outputs),
            (5, FieldType.UOffset, customOptsVec),
        ]);

        int operatorsVec = writer.WriteOffsetVector([op0, op1]);

        // 7. SubGraph[0]
        int sgInputs = writer.WriteInt32Vector([0]);
        int sgOutputs = writer.WriteInt32Vector([3]);
        int subgraph0 = writer.WriteTable([
            (0, FieldType.UOffset, tensorsVec),
            (1, FieldType.UOffset, sgInputs),
            (2, FieldType.UOffset, sgOutputs),
            (3, FieldType.UOffset, operatorsVec),
            (4, FieldType.UOffset, mainNameStr),
        ]);
        int subgraphsVec = writer.WriteOffsetVector([subgraph0]);

        // 8. Root Model table
        int modelTable = writer.WriteTable([
            (0, FieldType.Int32, 3), // version = 3
            (1, FieldType.UOffset, opCodesVec),
            (2, FieldType.UOffset, subgraphsVec),
            (3, FieldType.UOffset, descStr),
            (4, FieldType.UOffset, buffersVec),
        ]);

        return writer.Finish(modelTable, "TFL3"u8);
    }

    private enum FieldType
    {
        Byte,
        Int32,
        UOffset,
    }

    /// <summary>
    /// Minimal FlatBuffer builder that emits chunks in reverse creation order so all <c>uoffset_t</c> links are positive forward offsets.
    /// </summary>
    private sealed class MiniFlatBufferWriter
    {
        private readonly List<Chunk> _chunks = [];

        public int WriteString(string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            byte[] data = new byte[4 + utf8.Length + 1];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), (uint)utf8.Length);
            utf8.CopyTo(data.AsSpan(4));
            return AddChunk(alignment: 4, data, []);
        }

        public int WriteByteVector(ReadOnlySpan<byte> bytes)
        {
            byte[] data = new byte[4 + bytes.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), (uint)bytes.Length);
            bytes.CopyTo(data.AsSpan(4));
            return AddChunk(alignment: 4, data, []);
        }

        public int WriteInt32Vector(ReadOnlySpan<int> values)
        {
            byte[] data = new byte[4 + (values.Length * 4)];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), (uint)values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4 + (i * 4), 4), values[i]);
            }

            return AddChunk(alignment: 4, data, []);
        }

        public int WriteFloat32Vector(ReadOnlySpan<float> values)
        {
            byte[] data = new byte[4 + (values.Length * 4)];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), (uint)values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4 + (i * 4), 4), values[i]);
            }

            return AddChunk(alignment: 4, data, []);
        }

        public int WriteInt64Vector(ReadOnlySpan<long> values)
        {
            byte[] data = new byte[4 + (values.Length * 8)];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), (uint)values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4 + (i * 8), 8), values[i]);
            }

            return AddChunk(alignment: 8, data, []);
        }

        public int WriteOffsetVector(ReadOnlySpan<int> targetChunkIds)
        {
            byte[] data = new byte[4 + (targetChunkIds.Length * 4)];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), (uint)targetChunkIds.Length);
            List<(int RelativeSlotOffset, int TargetChunkId)> fixups = new(targetChunkIds.Length);
            for (int i = 0; i < targetChunkIds.Length; i++)
            {
                fixups.Add((4 + (i * 4), targetChunkIds[i]));
            }

            return AddChunk(alignment: 4, data, fixups);
        }

        public int WriteTable(ReadOnlySpan<(int FieldId, FieldType Type, int Value)> fields)
        {
            int maxFieldId = -1;
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldId > maxFieldId)
                {
                    maxFieldId = fields[i].FieldId;
                }
            }

            int numFields = maxFieldId + 1;
            ushort[] fieldOffsets = new ushort[numFields];

            List<byte> body = [0, 0, 0, 0]; // 4-byte soffset_t to vtable
            List<(int RelativeSlotOffset, int TargetChunkId)> fixups = [];
            Span<byte> tmp = stackalloc byte[4];

            // Place 4-byte fields first, then 1-byte fields
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    (int fieldId, FieldType type, int val) = fields[i];
                    bool isFourByte = type is FieldType.Int32 or FieldType.UOffset;
                    if ((pass == 0 && !isFourByte) || (pass == 1 && isFourByte))
                    {
                        continue;
                    }

                    if (isFourByte)
                    {
                        while ((body.Count % 4) != 0)
                        {
                            body.Add(0);
                        }
                    }

                    int relOffset = body.Count;
                    fieldOffsets[fieldId] = (ushort)relOffset;
                    if (type == FieldType.Byte)
                    {
                        body.Add((byte)val);
                    }
                    else if (type == FieldType.Int32)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(tmp, val);
                        for (int b = 0; b < 4; b++)
                        {
                            body.Add(tmp[b]);
                        }
                    }
                    else
                    {
                        fixups.Add((relOffset, val));
                        for (int b = 0; b < 4; b++)
                        {
                            body.Add(0);
                        }
                    }
                }
            }

            while ((body.Count % 4) != 0)
            {
                body.Add(0);
            }

            // Build vtable bytes and append right after table body inside the same chunk, or as its own prefix
            ushort vtableBytesLen = (ushort)(4 + (numFields * 2));
            byte[] vtable = new byte[vtableBytesLen];
            BinaryPrimitives.WriteUInt16LittleEndian(vtable.AsSpan(0, 2), vtableBytesLen);
            BinaryPrimitives.WriteUInt16LittleEndian(vtable.AsSpan(2, 2), (ushort)body.Count);
            for (int f = 0; f < numFields; f++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(vtable.AsSpan(4 + (f * 2), 2), fieldOffsets[f]);
            }

            // Table body starts at offset 0 of the chunk; vtable follows immediately at offset body.Count.
            // soffset_t = tableOffset - vtableOffset = -body.Count.
            byte[] combined = new byte[body.Count + vtable.Length];
            body.CopyTo(combined, 0);
            vtable.CopyTo(combined.AsSpan(body.Count));
            BinaryPrimitives.WriteInt32LittleEndian(combined.AsSpan(0, 4), -body.Count);

            return AddChunk(alignment: 4, combined, fixups);
        }

        public byte[] Finish(int rootChunkId, ReadOnlySpan<byte> identifier4)
        {
            List<byte> output = new(1024);
            for (int i = 0; i < 8; i++)
            {
                output.Add(0);
            }

            int[] chunkFileOffsets = new int[_chunks.Count];

            // Emit chunks in reverse creation order (rootChunkId first, leaves last) so all uoffset_t are positive.
            for (int id = _chunks.Count - 1; id >= 0; id--)
            {
                Chunk chunk = _chunks[id];
                while ((output.Count % chunk.Alignment) != 0)
                {
                    output.Add(0);
                }

                chunkFileOffsets[id] = output.Count;
                output.AddRange(chunk.Data);
            }

            byte[] result = [.. output];
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), (uint)chunkFileOffsets[rootChunkId]);
            identifier4[..4].CopyTo(result.AsSpan(4, 4));

            for (int id = 0; id < _chunks.Count; id++)
            {
                Chunk chunk = _chunks[id];
                int chunkStart = chunkFileOffsets[id];
                for (int f = 0; f < chunk.Fixups.Count; f++)
                {
                    (int relSlot, int targetId) = chunk.Fixups[f];
                    int slotAbs = chunkStart + relSlot;
                    int targetAbs = chunkFileOffsets[targetId];
                    uint uoffset = checked((uint)(targetAbs - slotAbs));
                    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(slotAbs, 4), uoffset);
                }
            }

            return result;
        }

        private int AddChunk(int alignment, byte[] data, List<(int RelativeSlotOffset, int TargetChunkId)> fixups)
        {
            int id = _chunks.Count;
            _chunks.Add(new Chunk(alignment, data, fixups));
            return id;
        }

        private sealed record Chunk(
            int Alignment,
            byte[] Data,
            List<(int RelativeSlotOffset, int TargetChunkId)> Fixups);
    }
}
