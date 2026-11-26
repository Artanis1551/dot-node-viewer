using System.Buffers.Binary;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Delegates.Xnnpack;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Synthetic;

/// <summary>
/// Synthetic in-memory binary tests for <see cref="XnnpackHeaderReader"/>, <see cref="XnnpackGraphDecoder"/>,
/// <see cref="XnnpackOperatorCatalog"/>, and <see cref="XnnpackDelegateSubgraphExpander"/>.
/// </summary>
public sealed class XnnpackDelegateSyntheticTests
{
    [Test]
    public async Task ExpandAsync_StandardOpsAndFusedRelu_DecodesExplicitTopologyAndAttributes()
    {
        byte[] constWeights = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] constBias = [9, 10, 11, 12];
        byte[] payload = BuildSyntheticXnnpackPayload(b =>
        {
            // xvalues: 0..23
            int vIn = b.AddTensor(1, [1, 8, 8, 3]);
            int vConvW = b.AddTensor(1, [8, 3, 3, 3], constIdx: 1);
            int vConvB = b.AddTensor(1, [8], constIdx: 2);
            int vConvOut = b.AddTensor(1, [1, 8, 8, 8]);
            int vDwW = b.AddTensor(1, [1, 3, 3, 8], constIdx: 1);
            int vDwOut = b.AddTensor(1, [1, 8, 8, 8]);
            int vHardswishOut = b.AddTensor(1, [1, 8, 8, 8]);
            int vAddOut = b.AddTensor(1, [1, 8, 8, 8]);
            int vResizeOut = b.AddTensor(1, [1, 16, 16, 8]);
            int vPadOut = b.AddTensor(1, [1, 18, 18, 8]);
            int vSliceOut = b.AddTensor(1, [1, 16, 16, 8]);
            int vTransposeOut = b.AddTensor(1, [1, 8, 16, 16]);
            int vReshapeOut = b.AddTensor(1, [1, 2048]);
            int vFcW = b.AddTensor(1, [4, 2048], constIdx: 1);
            int vFcOut = b.AddTensor(1, [1, 4]);
            int vSoftmaxOut = b.AddTensor(1, [1, 4]);
            int vCatOut = b.AddTensor(1, [1, 20]);
            int vSdpaQ = b.AddTensor(1, [1, 4, 8]);
            int vSdpaK = b.AddTensor(1, [1, 4, 8]);
            int vSdpaV = b.AddTensor(1, [1, 4, 8]);
            int vSdpaOut = b.AddTensor(1, [1, 4, 8]);
            int vBmmOut = b.AddTensor(1, [1, 4, 4]);
            int vArgMaxVal = b.AddTensor(1, [1, 4, 4, 8]);
            int vArgMaxIdx = b.AddTensor(12, [1, 4, 4, 8]);
            int vEluOut = b.AddTensor(1, [1, 4, 4, 8]);
            int vLeakyOut = b.AddTensor(1, [1, 4, 4, 8]);

            b.SetInputs([vIn, vSdpaQ, vSdpaK, vSdpaV]);
            b.SetOutputs([vSoftmaxOut, vCatOut, vBmmOut, vLeakyOut]);

            b.AddConvNode(7, vIn, vConvW, (uint)vConvB, vConvOut, pad: 1, k: 3, stride: 1, dilation: 1, groups: 1, gIn: 3, gOut: 8, debugHandle: 12, outMin: 0f, outMax: float.PositiveInfinity);
            b.AddConvNode(13, vConvOut, vDwW, uint.MaxValue, vDwOut, pad: 1, k: 3, stride: 1, dilation: 1, groups: 8, gIn: 1, gOut: 1);
            b.AddUnary1x1(24, vDwOut, vHardswishOut); // XNNHardswish
            b.AddBinary2x1(1, vConvOut, vHardswishOut, vAddOut); // XNNAdd
            b.AddResizeBilinear2d(vAddOut, vResizeOut, 16, 16);
            b.AddConstantPad(vResizeOut, vPadOut, [0, 1, 1, 0], [0, 1, 1, 0], 0.5f);
            b.AddStaticSlice(vPadOut, vSliceOut, [0, 1, 1, 0], [1, 16, 16, 8]);
            b.AddStaticTranspose(vSliceOut, vTransposeOut, [0, 3, 1, 2]);
            b.AddStaticReshape(vTransposeOut, vReshapeOut, [1, 2048]);
            b.AddFullyConnected(vReshapeOut, vFcW, uint.MaxValue, vFcOut); // omitted optional bias_id
            b.AddUnary1x1(3, vFcOut, vSoftmaxOut); // XNNSoftmax
            b.AddConcat5(1, vFcOut, vFcOut, vFcOut, vFcOut, vFcOut, vCatOut);
            b.AddSdpa(vSdpaQ, vSdpaK, vSdpaV, uint.MaxValue, uint.MaxValue, vSdpaOut);
            b.AddBinary2x1(37, vSdpaOut, vSdpaK, vBmmOut); // XNNBatchMatrixMultiply
            b.AddArgMaxPooling2d(vAddOut, vArgMaxVal, vArgMaxIdx, 2, 2);
            b.AddElu(vArgMaxVal, vEluOut, 1.0f);
            b.AddLeakyRelu(vEluOut, vLeakyOut, 0.2f);

            b.AddConstDataOffset(0, 0); // index 0 reserved
            b.AddConstDataOffset(0, (ulong)constWeights.Length);
            b.AddConstDataOffset((ulong)constWeights.Length, (ulong)constBias.Length);
        }, trailingConstData: [.. constWeights, .. constBias]);

        (ModelGraph graph, GraphNode delegateNode, MemoryStream stream) = CreateDelegateFixture(payload);
        await using (stream)
        {
            IDelegateSubgraphExpander? expander = DelegateSubgraphExpanderRegistry.Default.FindExpander(graph, delegateNode);
            await Assert.That(expander).IsNotNull();
            await Assert.That(expander!.BackendId).IsEqualTo("xnnpack");

            Subgraph? expanded = await expander.ExpandAsync(stream, graph, delegateNode);
            await Assert.That(expanded).IsNotNull();
            await Assert.That(expanded!.Name).IsEqualTo("XnnpackBackend_0 (XNNPACK)");
            await Assert.That(expanded.Nodes.Count).IsEqualTo(17);
            await Assert.That(expanded.Inputs.Count).IsEqualTo(4);
            await Assert.That(expanded.Outputs.Count).IsEqualTo(4);

            // Conv2d fused RELU & optional bias absent on FullyConnected
            GraphNode convNode = expanded.Nodes[0];
            await Assert.That(convNode.Operator.Name).IsEqualTo("XNNConv2d");
            await Assert.That(convNode.Operator.Category).IsEqualTo(OperatorCategory.Layer);
            await Assert.That(convNode.Inputs.Count).IsEqualTo(3);
            await Assert.That(convNode.Attributes.Any(a => a.Name == "fused_activation_function" && a.Value is AttributeValue.String s && s.Value == "RELU")).IsTrue();
            await Assert.That(convNode.Attributes.Any(a => a.Name == "debug_handle" && a.Value is AttributeValue.Int64 i && i.Value == 12)).IsTrue();

            GraphNode fcNode = expanded.Nodes[9];
            await Assert.That(fcNode.Operator.Name).IsEqualTo("XNNFullyConnected");
            await Assert.That(fcNode.Inputs.Count).IsEqualTo(2); // bias omitted

            // Verify Concat5 includes input5 at field 7
            GraphNode concat5 = expanded.Nodes[11];
            await Assert.That(concat5.Operator.Name).IsEqualTo("XNNConcatenate5");
            await Assert.That(concat5.Inputs.Count).IsEqualTo(5);
            await Assert.That(concat5.Inputs[4].Name).IsEqualTo("input5");

            // Verify SDPA optional scale/mask omitted cleanly
            GraphNode sdpa = expanded.Nodes[12];
            await Assert.That(sdpa.Operator.Name).IsEqualTo("XNNScaledDotProductAttention");
            await Assert.That(sdpa.Inputs.Count).IsEqualTo(3);

            // Verify ArgMaxPooling2d dual outputs
            GraphNode argmax = expanded.Nodes[14];
            await Assert.That(argmax.Outputs.Count).IsEqualTo(2);
            await Assert.That(argmax.Outputs[1].Tensors[0].Descriptor.DataType).IsEqualTo(DataType.Int32);
        }
    }

    [Test]
    public async Task ExpandAsync_DepthToSpace2dAndStaticReduce_MapsForwardCompatibleUnions()
    {
        byte[] payload = BuildSyntheticXnnpackPayload(b =>
        {
            int vIn = b.AddTensor(1, [1, 4, 4, 8]);
            int vD2s = b.AddTensor(1, [1, 8, 8, 2]);
            int vOut = b.AddTensor(1, [1, 1, 1, 2]);
            b.SetInputs([vIn]);
            b.SetOutputs([vOut]);

            b.AddDepthToSpace2d(vIn, vD2s, blockSize: 2);
            b.AddStaticReduce(vD2s, vOut, [1, 2]);
        });

        (ModelGraph graph, GraphNode delegateNode, MemoryStream stream) = CreateDelegateFixture(payload);
        await using (stream)
        {
            Subgraph? expanded = await DelegateSubgraphExpanderRegistry.Default.ExpandAsync(stream, graph, delegateNode);
            await Assert.That(expanded).IsNotNull();
            await Assert.That(expanded!.Nodes.Count).IsEqualTo(2);

            GraphNode d2s = expanded.Nodes[0];
            await Assert.That(d2s.Operator.Name).IsEqualTo("XNNDepthToSpace2d");
            await Assert.That(d2s.Operator.Category).IsEqualTo(OperatorCategory.Shape);
            await Assert.That(d2s.Attributes.Any(a => a.Name == "block_size" && a.Value is AttributeValue.Int64 v && v.Value == 2)).IsTrue();

            GraphNode reduce = expanded.Nodes[1];
            await Assert.That(reduce.Operator.Name).IsEqualTo("_XNNStaticReduce");
            await Assert.That(reduce.Operator.Category).IsEqualTo(OperatorCategory.Tensor);
            NodeAttribute? axesAttr = reduce.Attributes.FirstOrDefault(a => a.Name == "reduction_axes");
            await Assert.That(axesAttr).IsNotNull();
            await Assert.That(((AttributeValue.Int64List)axesAttr!.Value).Values.SequenceEqual([1L, 2L])).IsTrue();
        }
    }

    [Test]
    public async Task ExpandAsync_QuantizedTensors_MapsPerTensorPerChannelDynamicAndGroupQuant()
    {
        byte[] scaleBytes = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(scaleBytes.AsSpan(0, 4), 0.125f);
        BinaryPrimitives.WriteSingleLittleEndian(scaleBytes.AsSpan(4, 4), 0.25f);

        byte[] payload = BuildSyntheticXnnpackPayload(b =>
        {
            int vPerTensor = b.AddQuantizedPerTensor(3, [1, 4], scale: 0.05f, zeroPoint: -12);
            int vPerChannelInline = b.AddQuantizedPerChannel(6, [2, 4], scales: [0.1f, 0.2f], channelDim: 0);
            int vPerChannelBuffered = b.AddQuantizedPerChannelBuffered(6, [2, 4], channelDim: 0, scaleBufferIdx: 1, numScales: 2);
            int vPerTokenDynamic = b.AddQuantizedPerTokenDynamic(9, [1, 4], numNonbatchDims: 1);
            int vPerChannelGroup = b.AddQuantizedPerChannelGroup(10, [2, 32], channelDim: 0, groupSize: 32);
            int vOut = b.AddTensor(1, [1, 4]);

            b.SetInputs([vPerTensor]);
            b.SetOutputs([vOut]);
            b.AddUnary1x1(18, vPerTensor, vOut);

            b.AddConstDataOffset(0, 0);
            b.AddConstDataOffset(0, (ulong)scaleBytes.Length);
        }, trailingConstData: scaleBytes);

        (ModelGraph graph, GraphNode delegateNode, MemoryStream stream) = CreateDelegateFixture(payload);
        await using (stream)
        {
            Subgraph? expanded = await DelegateSubgraphExpanderRegistry.Default.ExpandAsync(stream, graph, delegateNode);
            await Assert.That(expanded).IsNotNull();

            // PerTensorQuant
            QuantizationInfo? q0 = expanded!.Tensors[0].Descriptor.Quantization;
            await Assert.That(q0).IsTypeOf<AffineQuantization>();
            AffineQuantization a0 = (AffineQuantization)q0!;
            await Assert.That(a0.QuantizedDimension).IsNull();
            await Assert.That(a0.Scale[0]).IsEqualTo(0.05f);
            await Assert.That(a0.ZeroPoint[0]).IsEqualTo(-12L);

            // PerChannelQuant (inline)
            QuantizationInfo? q1 = expanded.Tensors[1].Descriptor.Quantization;
            await Assert.That(q1).IsTypeOf<AffineQuantization>();
            AffineQuantization a1 = (AffineQuantization)q1!;
            await Assert.That(a1.QuantizedDimension).IsEqualTo(0);
            await Assert.That(a1.Scale.SequenceEqual([0.1f, 0.2f])).IsTrue();

            // PerChannelQuant (buffered via scale_buffer_idx)
            QuantizationInfo? q2 = expanded.Tensors[2].Descriptor.Quantization;
            await Assert.That(q2).IsTypeOf<AffineQuantization>();
            AffineQuantization a2 = (AffineQuantization)q2!;
            await Assert.That(a2.Scale.SequenceEqual([0.125f, 0.25f])).IsTrue();

            // PerTokenDynamicQuant
            QuantizationInfo? q3 = expanded.Tensors[3].Descriptor.Quantization;
            await Assert.That(q3).IsTypeOf<BlockQuantization>();
            await Assert.That(((BlockQuantization)q3!).Scheme).IsEqualTo("PerTokenDynamic");

            // PerChannelGroupQuant (4-bit qbint4)
            QuantizationInfo? q4 = expanded.Tensors[4].Descriptor.Quantization;
            await Assert.That(q4).IsTypeOf<BlockQuantization>();
            BlockQuantization b4 = (BlockQuantization)q4!;
            await Assert.That(b4.BlockSize).IsEqualTo(32);
            await Assert.That(b4.Bits).IsEqualTo(4);
            await Assert.That(expanded.Tensors[4].Descriptor.DataType).IsEqualTo(DataType.Int4);
        }
    }

    [Test]
    public async Task ExpandAsync_UnknownUnionDiscriminant_MapsToUnknownWithDiagnosticWarning()
    {
        byte[] payload = BuildSyntheticXnnpackPayload(b =>
        {
            int vIn = b.AddTensor(1, [1, 4]);
            int vOut = b.AddTensor(1, [1, 4]);
            b.SetInputs([vIn]);
            b.SetOutputs([vOut]);
            b.AddUnary1x1(250, vIn, vOut);
        });

        (ModelGraph _, GraphNode delegateNode, MemoryStream stream) = CreateDelegateFixture(payload);
        await using (stream)
        {
            TensorDataSlice processed = ((AttributeValue.Bytes)delegateNode.Attributes.First(a => a.Name == "processed").Value).Slice;
            XnnpackDecodedGraph decoded = XnnpackGraphDecoder.Decode(stream, delegateNode, processed);

            await Assert.That(decoded.Subgraph.Nodes.Count).IsEqualTo(1);
            GraphNode unknownNode = decoded.Subgraph.Nodes[0];
            await Assert.That(unknownNode.Operator.Category).IsEqualTo(OperatorCategory.Unknown);
            await Assert.That(unknownNode.Operator.Name).IsEqualTo("XNNUnknown_250");
            await Assert.That(decoded.Diagnostics.Count).IsEqualTo(1);
            await Assert.That(decoded.Diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
            await Assert.That(decoded.Diagnostics[0].Code).IsEqualTo("DNV_XNNPACK_UNKNOWN_OP");
        }
    }

    [Test]
    public async Task ExpandAsync_ExternalNamedKeyMissingFromPte_EmitsExternalReferenceAndDiagnostic()
    {
        byte[] payload = BuildSyntheticXnnpackPayload(b =>
        {
            int vIn = b.AddTensor(1, [1, 4]);
            int vWeight = b.AddTensor(1, [4, 4], constIdx: 1);
            int vOut = b.AddTensor(1, [1, 4]);
            b.SetInputs([vIn]);
            b.SetOutputs([vOut]);
            b.AddFullyConnected(vIn, vWeight, uint.MaxValue, vOut);
            b.AddConstDataOffset(0, 0);
            b.AddConstDataOffset(ulong.MaxValue, 64, namedKey: "external_fc_weight_key");
        });

        (ModelGraph _, GraphNode delegateNode, MemoryStream stream) = CreateDelegateFixture(payload);
        await using (stream)
        {
            TensorDataSlice processed = ((AttributeValue.Bytes)delegateNode.Attributes.First(a => a.Name == "processed").Value).Slice;
            XnnpackDecodedGraph decoded = XnnpackGraphDecoder.Decode(stream, delegateNode, processed);

            TensorEdge weightEdge = decoded.Subgraph.Tensors[1];
            await Assert.That(weightEdge.Data).IsTypeOf<ExternalTensorDataReference>();
            ExternalTensorDataReference ext = (ExternalTensorDataReference)weightEdge.Data!;
            await Assert.That(ext.RelativePath).IsEqualTo("external_fc_weight_key.ptd");
            await Assert.That(ext.Length).IsEqualTo(64L);
            await Assert.That(decoded.Diagnostics.Any(d => d.Code == "DNV_XNNPACK_EXTERNAL_NAMED_DATA")).IsTrue();
        }
    }

    [Test]
    public async Task ExpandAsync_DirectFlatBufferWithoutXnnHeader_AndLegacyConstantBuffer_DecodesCleanly()
    {
        byte[] legacyWeights = [42, 43, 44, 45, 46, 47, 48, 49];
        byte[] directFb = BuildSyntheticXnnpackPayload(b =>
        {
            int vIn = b.AddTensor(1, [1, 2]);
            int vW = b.AddTensor(1, [1, 2], constIdx: 1);
            int vOut = b.AddTensor(1, [1, 1]);
            b.SetInputs([vIn]);
            b.SetOutputs([vOut]);
            b.AddFullyConnected(vIn, vW, uint.MaxValue, vOut);
            b.AddLegacyBuffer([]); // index 0 reserved
            b.AddLegacyBuffer(legacyWeights);
        }, includeXnnHeader: false, identifier: "XN00");

        (ModelGraph graph, GraphNode delegateNode, MemoryStream stream) = CreateDelegateFixture(directFb);
        await using (stream)
        {
            Subgraph? expanded = await DelegateSubgraphExpanderRegistry.Default.ExpandAsync(stream, graph, delegateNode);
            await Assert.That(expanded).IsNotNull();

            TensorEdge wEdge = expanded!.Tensors[1];
            await Assert.That(wEdge.Data).IsTypeOf<TensorDataSlice>();
            TensorDataSlice slice = (TensorDataSlice)wEdge.Data!;
            await Assert.That(slice.ByteLength).IsEqualTo(legacyWeights.Length);

            byte[] readBuf = new byte[legacyWeights.Length];
            int read = TensorDataLoader.ReadSlice(stream, slice, readBuf);
            await Assert.That(read).IsEqualTo(legacyWeights.Length);
            await Assert.That(readBuf.SequenceEqual(legacyWeights)).IsTrue();
        }
    }

    [Test]
    public async Task ExpandAsync_LargeTrailingConstantDataSegment_SlicesWeightsLazilyWithAllocationProbe()
    {
        const int FourMegabytes = 4 * 1024 * 1024;
        byte[] largeWeights = new byte[FourMegabytes];
        largeWeights[0] = 0xDE;
        largeWeights[1] = 0xAD;
        largeWeights[^2] = 0xBE;
        largeWeights[^1] = 0xEF;

        byte[] payload = BuildSyntheticXnnpackPayload(b =>
        {
            int vIn = b.AddTensor(1, [1, 1024]);
            int vW = b.AddTensor(1, [1024, 1024], constIdx: 1);
            int vOut = b.AddTensor(1, [1, 1024]);
            b.SetInputs([vIn]);
            b.SetOutputs([vOut]);
            b.AddFullyConnected(vIn, vW, uint.MaxValue, vOut);
            b.AddConstDataOffset(0, 0);
            b.AddConstDataOffset(0, (ulong)FourMegabytes);
        }, trailingConstData: largeWeights);

        (ModelGraph graph, GraphNode delegateNode, MemoryStream stream) = CreateDelegateFixture(payload);
        await using (stream)
        {
            IDelegateSubgraphExpander expander = DelegateSubgraphExpanderRegistry.Default.FindExpander(graph, delegateNode)!;

            // Warm up JIT
            Subgraph? warm = await expander.ExpandAsync(stream, graph, delegateNode);
            await Assert.That(warm).IsNotNull();

            (Subgraph? expanded, long allocatedBytes) = await AllocationProbe.MeasureAsync(
                () => expander.ExpandAsync(stream, graph, delegateNode));

            await Assert.That(expanded).IsNotNull();
            // Must never allocate the 4 MB trailing weight buffer
            await Assert.That(allocatedBytes).IsLessThan(256L * 1024);

            TensorEdge wEdge = expanded!.Tensors[1];
            await Assert.That(wEdge.Data).IsTypeOf<TensorDataSlice>();
            TensorDataSlice slice = (TensorDataSlice)wEdge.Data!;
            await Assert.That(slice.ByteLength).IsEqualTo(FourMegabytes);

            byte[] headTail = new byte[2];
            int r1 = TensorDataLoader.ReadSlice(stream, new TensorDataSlice(slice.StreamOffset, 2, DataType.UInt8, TensorDataEncoding.Raw), headTail);
            await Assert.That(r1).IsEqualTo(2);
            await Assert.That(headTail[0]).IsEqualTo((byte)0xDE);
            await Assert.That(headTail[1]).IsEqualTo((byte)0xAD);

            int r2 = TensorDataLoader.ReadSlice(stream, new TensorDataSlice(slice.StreamOffset + FourMegabytes - 2, 2, DataType.UInt8, TensorDataEncoding.Raw), headTail);
            await Assert.That(r2).IsEqualTo(2);
            await Assert.That(headTail[0]).IsEqualTo((byte)0xBE);
            await Assert.That(headTail[1]).IsEqualTo((byte)0xEF);
        }
    }

    [Test]
    public async Task ExpandAsync_MalformedAndOutOfBoundsPayloads_ThrowsModelParseException()
    {
        // 1. Truncated payload (< 8 bytes)
        await AssertThrowsModelParseExceptionAsync([0x00, 0x00, 0x00, 0x00, (byte)'X', (byte)'H']);

        // 2. Truncated XH00 header (< 30 bytes)
        byte[] shortXh = new byte[16];
        "XH00"u8.CopyTo(shortXh.AsSpan(4, 4));
        await AssertThrowsModelParseExceptionAsync(shortXh);

        // 3. Invalid magic
        byte[] badMagic = new byte[32];
        "BAD!"u8.CopyTo(badMagic.AsSpan(4, 4));
        await AssertThrowsModelParseExceptionAsync(badMagic);

        // 4. Out-of-bounds flatbuffer_size in XH00
        byte[] oobFb = new byte[48];
        "XH00"u8.CopyTo(oobFb.AsSpan(4, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(oobFb.AsSpan(8, 2), 30);
        BinaryPrimitives.WriteUInt32LittleEndian(oobFb.AsSpan(10, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(oobFb.AsSpan(14, 4), 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(oobFb.AsSpan(18, 4), 1056);
        BinaryPrimitives.WriteUInt64LittleEndian(oobFb.AsSpan(22, 8), 0);
        await AssertThrowsModelParseExceptionAsync(oobFb);

        // 5. Out-of-bounds constant_buffer_idx
        byte[] oobConstIdx = BuildSyntheticXnnpackPayload(b =>
        {
            int vIn = b.AddTensor(1, [1, 2]);
            int vW = b.AddTensor(1, [1, 2], constIdx: 99);
            b.SetInputs([vIn]);
            b.SetOutputs([vW]);
            b.AddConstDataOffset(0, 0);
        });
        await AssertThrowsModelParseExceptionAsync(oobConstIdx);

        // 6. Out-of-bounds value ID in node port
        byte[] oobValId = BuildSyntheticXnnpackPayload(b =>
        {
            int vIn = b.AddTensor(1, [1, 2]);
            b.SetInputs([vIn]);
            b.SetOutputs([vIn]);
            b.AddUnary1x1(3, vIn, 999);
        });
        await AssertThrowsModelParseExceptionAsync(oobValId);
    }

    [Test]
    public async Task DisassembleAsync_ReturnsNullForXnnpack()
    {
        byte[] payload = BuildSyntheticXnnpackPayload(b =>
        {
            int vIn = b.AddTensor(1, [1, 2]);
            int vOut = b.AddTensor(1, [1, 2]);
            b.SetInputs([vIn]);
            b.SetOutputs([vOut]);
            b.AddUnary1x1(3, vIn, vOut);
        });

        (ModelGraph graph, GraphNode delegateNode, MemoryStream stream) = CreateDelegateFixture(payload);
        await using (stream)
        {
            IReadOnlyList<string>? disassembly = await DelegateSubgraphExpanderRegistry.Default.DisassembleAsync(stream, graph, delegateNode);
            await Assert.That(disassembly).IsNull();
        }
    }

    private static async Task AssertThrowsModelParseExceptionAsync(byte[] payload)
    {
        (ModelGraph graph, GraphNode delegateNode, MemoryStream stream) = CreateDelegateFixture(payload);
        await using (stream)
        {
            TensorDataSlice processed = new(0, payload.Length, DataType.UInt8, TensorDataEncoding.Raw);
            await Assert.That(() => XnnpackGraphDecoder.Decode(stream, delegateNode, processed)).Throws<ModelParseException>();
            _ = graph;
        }
    }

    private static (ModelGraph Graph, GraphNode DelegateNode, MemoryStream Stream) CreateDelegateFixture(byte[] payload)
    {
        TensorEdge inEdge = new("x", new TensorDescriptor(DataType.Float32, new TensorShape([new TensorDimension.Fixed(1)])), TensorKind.GraphInput);
        TensorEdge outEdge = new("y", new TensorDescriptor(DataType.Float32, new TensorShape([new TensorDimension.Fixed(1)])), TensorKind.GraphOutput);
        TensorDataSlice processedSlice = new(0, payload.Length, DataType.UInt8, TensorDataEncoding.Raw);

        GraphNode delegateNode = new(
            "XnnpackBackend_0",
            new OperatorInfo("Delegate (XnnpackBackend)", OperatorCategory.Custom, Domain: "XnnpackBackend"),
            [new NodePort("in_0", [inEdge])],
            [new NodePort("out_0", [outEdge])],
            [
                new NodeAttribute("delegate.backend", new AttributeValue.String("xnnpack")),
                new NodeAttribute("delegate.expandable", new AttributeValue.Bool(true)),
                new NodeAttribute("processed", new AttributeValue.Bytes(processedSlice)),
            ]);

        Subgraph main = new("forward", [delegateNode], [inEdge, outEdge], [new GraphPort("x", [inEdge])], [new GraphPort("y", [outEdge])]);
        ModelGraph graph = new(
            new ModelFormatInfo(ModelFormatId.ExecuTorch, "ExecuTorch"),
            "executorch",
            [main],
            new Dictionary<string, string>(),
            []);

        return (graph, delegateNode, new MemoryStream(payload));
    }

    private static byte[] BuildSyntheticXnnpackPayload(
        Action<SyntheticXnnGraphBuilder> configure,
        byte[]? trailingConstData = null,
        bool includeXnnHeader = true,
        string identifier = "XN01")
    {
        SyntheticXnnGraphBuilder builder = new();
        configure(builder);
        byte[] fbBytes = builder.Build(identifier);

        if (!includeXnnHeader)
        {
            return fbBytes;
        }

        int paddedHeaderLen = 32;
        int paddedFbLen = (fbBytes.Length + 15) & ~15;
        byte[] constBytes = trailingConstData ?? [];
        byte[] output = new byte[paddedHeaderLen + paddedFbLen + constBytes.Length];

        "XH00"u8.CopyTo(output.AsSpan(4, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(8, 2), 30);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(10, 4), (uint)paddedHeaderLen);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(14, 4), (uint)fbBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(18, 4), (uint)(paddedHeaderLen + paddedFbLen));
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(22, 8), (ulong)constBytes.Length);

        fbBytes.CopyTo(output.AsSpan(paddedHeaderLen));
        if (constBytes.Length > 0)
        {
            constBytes.CopyTo(output.AsSpan(paddedHeaderLen + paddedFbLen));
        }

        return output;
    }

    private sealed class SyntheticXnnGraphBuilder
    {
        private readonly List<Func<FlatBufferWriter, int>> _xvalues = [];
        private readonly List<Func<FlatBufferWriter, int>> _xnodes = [];
        private readonly List<(ulong Offset, ulong Size, string? NamedKey)> _constData = [];
        private readonly List<byte[]> _legacyBuffers = [];
        private int[] _inputs = [];
        private int[] _outputs = [];

        public void SetInputs(int[] ids) => _inputs = ids;

        public void SetOutputs(int[] ids) => _outputs = ids;

        public void AddConstDataOffset(ulong offset, ulong size, string? namedKey = null) =>
            _constData.Add((offset, size, namedKey));

        public void AddLegacyBuffer(byte[] bytes) => _legacyBuffers.Add(bytes);

        public int AddTensor(short dtype, uint[] dims, uint constIdx = 0)
        {
            int idx = _xvalues.Count;
            _xvalues.Add(w => w.WriteTable(2, tw =>
            {
                tw.WriteByte(0, 1); // XNNTensorValue
                tw.WriteChild(1, cw => WriteTensorValue(cw, dtype, dims, constIdx, (uint)idx));
            }));
            return idx;
        }

        public int AddQuantizedPerTensor(short dtype, uint[] dims, float scale, int zeroPoint)
        {
            int idx = _xvalues.Count;
            _xvalues.Add(w => w.WriteTable(2, tw =>
            {
                tw.WriteByte(0, 2); // XNNQuantizedTensorValue
                tw.WriteChild(1, cw => cw.WriteTable(3, qtv =>
                {
                    qtv.WriteChild(0, vw => WriteTensorValue(vw, dtype, dims, 0, (uint)idx));
                    qtv.WriteByte(1, 2); // PerTensorQuant
                    qtv.WriteChild(2, qw => qw.WriteTable(2, qp =>
                    {
                        qp.WriteSingle(0, scale);
                        qp.WriteInt32(1, zeroPoint);
                    }));
                }));
            }));
            return idx;
        }

        public int AddQuantizedPerChannel(short dtype, uint[] dims, float[] scales, int channelDim)
        {
            int idx = _xvalues.Count;
            _xvalues.Add(w => w.WriteTable(2, tw =>
            {
                tw.WriteByte(0, 2);
                tw.WriteChild(1, cw => cw.WriteTable(3, qtv =>
                {
                    qtv.WriteChild(0, vw => WriteTensorValue(vw, dtype, dims, 0, (uint)idx));
                    qtv.WriteByte(1, 1); // PerChannelQuant
                    qtv.WriteChild(2, qw => qw.WriteTable(4, qp =>
                    {
                        qp.WriteChild(0, sw => sw.WriteSingleVector(scales));
                        qp.WriteInt32(1, channelDim);
                    }));
                }));
            }));
            return idx;
        }

        public int AddQuantizedPerChannelBuffered(short dtype, uint[] dims, int channelDim, uint scaleBufferIdx, uint numScales)
        {
            int idx = _xvalues.Count;
            _xvalues.Add(w => w.WriteTable(2, tw =>
            {
                tw.WriteByte(0, 2);
                tw.WriteChild(1, cw => cw.WriteTable(3, qtv =>
                {
                    qtv.WriteChild(0, vw => WriteTensorValue(vw, dtype, dims, 0, (uint)idx));
                    qtv.WriteByte(1, 1); // PerChannelQuant
                    qtv.WriteChild(2, qw => qw.WriteTable(4, qp =>
                    {
                        qp.WriteInt32(1, channelDim);
                        qp.WriteUInt32(2, scaleBufferIdx);
                        qp.WriteUInt32(3, numScales);
                    }));
                }));
            }));
            return idx;
        }

        public int AddQuantizedPerTokenDynamic(short dtype, uint[] dims, int numNonbatchDims)
        {
            int idx = _xvalues.Count;
            _xvalues.Add(w => w.WriteTable(2, tw =>
            {
                tw.WriteByte(0, 2);
                tw.WriteChild(1, cw => cw.WriteTable(3, qtv =>
                {
                    qtv.WriteChild(0, vw => WriteTensorValue(vw, dtype, dims, 0, (uint)idx));
                    qtv.WriteByte(1, 3); // PerTokenDynamicQuant
                    qtv.WriteChild(2, qw => qw.WriteTable(1, qp => qp.WriteInt32(0, numNonbatchDims)));
                }));
            }));
            return idx;
        }

        public int AddQuantizedPerChannelGroup(short dtype, uint[] dims, int channelDim, int groupSize)
        {
            int idx = _xvalues.Count;
            _xvalues.Add(w => w.WriteTable(2, tw =>
            {
                tw.WriteByte(0, 2);
                tw.WriteChild(1, cw => cw.WriteTable(3, qtv =>
                {
                    qtv.WriteChild(0, vw => WriteTensorValue(vw, dtype, dims, 0, (uint)idx));
                    qtv.WriteByte(1, 4); // PerChannelGroupQuant
                    qtv.WriteChild(2, qw => qw.WriteTable(6, qp =>
                    {
                        qp.WriteInt32(1, channelDim);
                        qp.WriteInt32(2, groupSize);
                    }));
                }));
            }));
            return idx;
        }

        public void AddUnary1x1(byte unionType, int inputId, int outputId)
        {
            _xnodes.Add(w => WrapXNode(w, unionType, cw => cw.WriteTable(3, tw =>
            {
                tw.WriteUInt32(0, (uint)inputId);
                tw.WriteUInt32(1, (uint)outputId);
            })));
        }

        public void AddBinary2x1(byte unionType, int in1, int in2, int outId)
        {
            _xnodes.Add(w => WrapXNode(w, unionType, cw => cw.WriteTable(4, tw =>
            {
                tw.WriteUInt32(0, (uint)in1);
                tw.WriteUInt32(1, (uint)in2);
                tw.WriteUInt32(2, (uint)outId);
            })));
        }

        public void AddFullyConnected(int inId, int filterId, uint biasId, int outId)
        {
            _xnodes.Add(w => WrapXNode(w, 2, cw => cw.WriteTable(5, tw =>
            {
                tw.WriteUInt32(0, (uint)inId);
                tw.WriteUInt32(1, (uint)filterId);
                tw.WriteUInt32(2, biasId);
                tw.WriteUInt32(3, (uint)outId);
            })));
        }

        public void AddConvNode(
            byte unionType,
            int inId,
            int filterId,
            uint biasId,
            int outId,
            uint pad,
            uint k,
            uint stride,
            uint dilation,
            uint groups,
            uint gIn,
            uint gOut,
            uint debugHandle = 0,
            float? outMin = null,
            float? outMax = null)
        {
            _xnodes.Add(w => WrapXNode(w, unionType, cw => cw.WriteTable(20, tw =>
            {
                tw.WriteUInt32(0, pad);
                tw.WriteUInt32(1, pad);
                tw.WriteUInt32(2, pad);
                tw.WriteUInt32(3, pad);
                tw.WriteUInt32(4, k);
                tw.WriteUInt32(5, k);
                tw.WriteUInt32(6, stride);
                tw.WriteUInt32(7, stride);
                tw.WriteUInt32(8, dilation);
                tw.WriteUInt32(9, dilation);
                tw.WriteUInt32(10, gIn);
                tw.WriteUInt32(11, gOut);
                tw.WriteUInt32(12, groups);
                tw.WriteUInt32(15, (uint)inId);
                tw.WriteUInt32(16, (uint)filterId);
                tw.WriteUInt32(17, biasId);
                tw.WriteUInt32(18, (uint)outId);
            }), debugHandle, outMin, outMax));
        }

        public void AddResizeBilinear2d(int inId, int outId, uint newH, uint newW)
        {
            _xnodes.Add(w => WrapXNode(w, 9, cw => cw.WriteTable(5, tw =>
            {
                tw.WriteUInt32(0, newH);
                tw.WriteUInt32(1, newW);
                tw.WriteUInt32(2, (uint)inId);
                tw.WriteUInt32(3, (uint)outId);
            })));
        }

        public void AddConstantPad(int inId, int outId, uint[] pre, uint[] post, float padValue)
        {
            _xnodes.Add(w => WrapXNode(w, 10, cw => cw.WriteTable(6, tw =>
            {
                tw.WriteChild(0, vw => vw.WriteUInt32Vector(pre));
                tw.WriteChild(1, vw => vw.WriteUInt32Vector(post));
                tw.WriteSingle(2, padValue);
                tw.WriteUInt32(3, (uint)inId);
                tw.WriteUInt32(4, (uint)outId);
            })));
        }

        public void AddStaticSlice(int inId, int outId, uint[] offsets, uint[] sizes)
        {
            _xnodes.Add(w => WrapXNode(w, 35, cw => cw.WriteTable(6, tw =>
            {
                tw.WriteUInt32(0, (uint)offsets.Length);
                tw.WriteChild(1, vw => vw.WriteUInt32Vector(offsets));
                tw.WriteChild(2, vw => vw.WriteUInt32Vector(sizes));
                tw.WriteUInt32(3, (uint)inId);
                tw.WriteUInt32(4, (uint)outId);
            })));
        }

        public void AddStaticTranspose(int inId, int outId, uint[] perm)
        {
            _xnodes.Add(w => WrapXNode(w, 5, cw => cw.WriteTable(5, tw =>
            {
                tw.WriteUInt32(0, (uint)perm.Length);
                tw.WriteChild(1, vw => vw.WriteUInt32Vector(perm));
                tw.WriteUInt32(2, (uint)inId);
                tw.WriteUInt32(3, (uint)outId);
            })));
        }

        public void AddStaticReshape(int inId, int outId, uint[] newShape)
        {
            _xnodes.Add(w => WrapXNode(w, 20, cw => cw.WriteTable(5, tw =>
            {
                tw.WriteUInt32(0, (uint)newShape.Length);
                tw.WriteChild(1, vw => vw.WriteUInt32Vector(newShape));
                tw.WriteUInt32(2, (uint)inId);
                tw.WriteUInt32(3, (uint)outId);
            })));
        }

        public void AddConcat5(uint axis, int i1, int i2, int i3, int i4, int i5, int outId)
        {
            _xnodes.Add(w => WrapXNode(w, 38, cw => cw.WriteTable(8, tw =>
            {
                tw.WriteUInt32(0, axis);
                tw.WriteUInt32(1, (uint)i1);
                tw.WriteUInt32(2, (uint)i2);
                tw.WriteUInt32(3, (uint)i3);
                tw.WriteUInt32(4, (uint)i4);
                tw.WriteUInt32(5, (uint)outId);
                tw.WriteUInt32(7, (uint)i5);
            })));
        }

        public void AddSdpa(int q, int k, int v, uint scaleId, uint maskId, int outId)
        {
            _xnodes.Add(w => WrapXNode(w, 36, cw => cw.WriteTable(7, tw =>
            {
                tw.WriteUInt32(0, (uint)q);
                tw.WriteUInt32(1, (uint)k);
                tw.WriteUInt32(2, (uint)v);
                tw.WriteUInt32(3, scaleId);
                tw.WriteUInt32(4, maskId);
                tw.WriteUInt32(5, (uint)outId);
            })));
        }

        public void AddArgMaxPooling2d(int inId, int outVal, int outIdx, uint poolH, uint poolW)
        {
            _xnodes.Add(w => WrapXNode(w, 21, cw => cw.WriteTable(10, tw =>
            {
                tw.WriteUInt32(4, poolH);
                tw.WriteUInt32(5, poolW);
                tw.WriteUInt32(6, (uint)inId);
                tw.WriteUInt32(7, (uint)outVal);
                tw.WriteUInt32(8, (uint)outIdx);
            })));
        }

        public void AddElu(int inId, int outId, float alpha)
        {
            _xnodes.Add(w => WrapXNode(w, 29, cw => cw.WriteTable(4, tw =>
            {
                tw.WriteSingle(0, alpha);
                tw.WriteUInt32(1, (uint)inId);
                tw.WriteUInt32(2, (uint)outId);
            })));
        }

        public void AddLeakyRelu(int inId, int outId, float slope)
        {
            _xnodes.Add(w => WrapXNode(w, 25, cw => cw.WriteTable(4, tw =>
            {
                tw.WriteSingle(0, slope);
                tw.WriteUInt32(1, (uint)inId);
                tw.WriteUInt32(2, (uint)outId);
            })));
        }

        public void AddDepthToSpace2d(int inId, int outId, uint blockSize)
        {
            _xnodes.Add(w => WrapXNode(w, 49, cw => cw.WriteTable(4, tw =>
            {
                tw.WriteUInt32(0, blockSize);
                tw.WriteUInt32(1, (uint)inId);
                tw.WriteUInt32(2, (uint)outId);
            })));
        }

        public void AddStaticReduce(int inId, int outId, int[] axes)
        {
            _xnodes.Add(w => WrapXNode(w, 50, cw => cw.WriteTable(5, tw =>
            {
                tw.WriteUInt32(0, (uint)axes.Length);
                tw.WriteChild(1, vw => vw.WriteInt32Vector(axes));
                tw.WriteUInt32(2, (uint)inId);
                tw.WriteUInt32(3, (uint)outId);
            })));
        }

        public byte[] Build(string identifier)
        {
            FlatBufferWriter w = new(identifier);
            int rootTable = w.WriteTable(9, tw =>
            {
                tw.WriteChild(0, cw => cw.WriteString("0"));
                tw.WriteChild(1, cw => cw.WriteTableVector(_xnodes));
                tw.WriteChild(2, cw => cw.WriteTableVector(_xvalues));
                tw.WriteUInt32(3, (uint)(_inputs.Length + _outputs.Length));
                tw.WriteChild(4, cw => cw.WriteUInt32Vector(Array.ConvertAll(_inputs, i => (uint)i)));
                tw.WriteChild(5, cw => cw.WriteUInt32Vector(Array.ConvertAll(_outputs, i => (uint)i)));

                if (_legacyBuffers.Count > 0)
                {
                    List<Func<FlatBufferWriter, int>> bufEmitters = new(_legacyBuffers.Count);
                    foreach (byte[] buf in _legacyBuffers)
                    {
                        byte[] captured = buf;
                        bufEmitters.Add(cw => cw.WriteTable(1, bw =>
                        {
                            if (captured.Length > 0)
                            {
                                bw.WriteChild(0, sw => sw.WriteByteVector(captured));
                            }
                        }));
                    }

                    tw.WriteChild(6, cw => cw.WriteTableVector(bufEmitters));
                }

                if (_constData.Count > 0)
                {
                    List<Func<FlatBufferWriter, int>> cdEmitters = new(_constData.Count);
                    foreach (var (offset, size, key) in _constData)
                    {
                        ulong cOffset = offset;
                        ulong cSize = size;
                        string? cKey = key;
                        cdEmitters.Add(cw => cw.WriteTable(3, cdw =>
                        {
                            cdw.WriteUInt64(0, cOffset);
                            cdw.WriteUInt64(1, cSize);
                            if (cKey is not null)
                            {
                                cdw.WriteChild(2, kw => kw.WriteString(cKey));
                            }
                        }));
                    }

                    tw.WriteChild(8, cw => cw.WriteTableVector(cdEmitters));
                }
            });

            return w.Finish(rootTable);
        }

        private static int WriteTensorValue(FlatBufferWriter w, short dtype, uint[] dims, uint constIdx, uint idOut)
        {
            return w.WriteTable(7, tw =>
            {
                tw.WriteInt16(0, dtype);
                tw.WriteUInt32(1, (uint)dims.Length);
                tw.WriteChild(2, cw => cw.WriteUInt32Vector(dims));
                tw.WriteUInt32(3, constIdx);
                tw.WriteUInt32(4, uint.MaxValue);
                tw.WriteUInt32(6, idOut);
            });
        }

        private static int WrapXNode(
            FlatBufferWriter w,
            byte unionType,
            Func<FlatBufferWriter, int> emitInnerTable,
            uint debugHandle = 0,
            float? outMin = null,
            float? outMax = null)
        {
            return w.WriteTable(4, tw =>
            {
                tw.WriteByte(0, unionType);
                tw.WriteChild(1, emitInnerTable);
                if (debugHandle != 0)
                {
                    tw.WriteUInt32(2, debugHandle);
                }

                if (outMin.HasValue || outMax.HasValue)
                {
                    tw.WriteChild(3, mw => mw.WriteTable(2, mm =>
                    {
                        mm.WriteSingle(0, outMin ?? float.NegativeInfinity);
                        mm.WriteSingle(1, outMax ?? float.PositiveInfinity);
                    }));
                }
            });
        }
    }

    private sealed class FlatBufferWriter
    {
        private readonly List<byte> _buf = [];

        public FlatBufferWriter(string identifier)
        {
            // Reserve 8-byte FlatBuffer header [root_uoffset (4), identifier (4)]
            _buf.AddRange(new byte[8]);
            byte[] idBytes = Encoding.ASCII.GetBytes(identifier);
            for (int i = 0; i < 4; i++)
            {
                _buf[4 + i] = idBytes[i];
            }
        }

        public int WriteString(string value)
        {
            Align(4);
            int pos = _buf.Count;
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            WriteUInt32Raw((uint)utf8.Length);
            _buf.AddRange(utf8);
            _buf.Add(0);
            Align(4);
            return pos;
        }

        public int WriteByteVector(byte[] data)
        {
            Align(4);
            int pos = _buf.Count;
            WriteUInt32Raw((uint)data.Length);
            _buf.AddRange(data);
            Align(4);
            return pos;
        }

        public int WriteUInt32Vector(uint[] items)
        {
            Align(4);
            int pos = _buf.Count;
            WriteUInt32Raw((uint)items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                WriteUInt32Raw(items[i]);
            }
            return pos;
        }

        public int WriteInt32Vector(int[] items)
        {
            Align(4);
            int pos = _buf.Count;
            WriteUInt32Raw((uint)items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                WriteInt32Raw(items[i]);
            }
            return pos;
        }

        public int WriteSingleVector(float[] items)
        {
            Align(4);
            int pos = _buf.Count;
            WriteUInt32Raw((uint)items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                WriteUInt32Raw((uint)BitConverter.SingleToInt32Bits(items[i]));
            }
            return pos;
        }

        public int WriteTableVector(IReadOnlyList<Func<FlatBufferWriter, int>> emitters)
        {
            Align(4);
            int pos = _buf.Count;
            WriteUInt32Raw((uint)emitters.Count);
            int firstSlotPos = _buf.Count;
            for (int i = 0; i < emitters.Count; i++)
            {
                WriteUInt32Raw(0);
            }

            for (int i = 0; i < emitters.Count; i++)
            {
                int targetPos = emitters[i](this);
                int slotPos = firstSlotPos + (i * 4);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buf).Slice(slotPos, 4),
                    (uint)(targetPos - slotPos));
            }

            return pos;
        }

        public int WriteTable(int fieldCount, Action<TableBuilder> populate)
        {
            Align(4);
            TableBuilder tb = new(fieldCount);
            populate(tb);

            Align(2);
            int vtablePos = _buf.Count;
            ushort vtableLen = (ushort)(4 + (fieldCount * 2));
            WriteUInt16Raw(vtableLen);
            WriteUInt16Raw((ushort)tb.InlineSize);
            for (int i = 0; i < fieldCount; i++)
            {
                WriteUInt16Raw(tb.VTableOffsets[i]);
            }

            Align(4);
            int tablePos = _buf.Count;
            WriteInt32Raw(tablePos - vtablePos);
            _buf.AddRange(tb.Payload);

            for (int i = 0; i < tb.DeferredChildren.Count; i++)
            {
                var (inlineOffset, emitChild) = tb.DeferredChildren[i];
                int slotPos = tablePos + inlineOffset;
                int targetPos = emitChild(this);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buf).Slice(slotPos, 4),
                    (uint)(targetPos - slotPos));
            }

            return tablePos;
        }

        public byte[] Finish(int rootTablePos)
        {
            byte[] arr = [.. _buf];
            BinaryPrimitives.WriteUInt32LittleEndian(arr.AsSpan(0, 4), (uint)rootTablePos);
            return arr;
        }

        private void Align(int alignment)
        {
            while (_buf.Count % alignment != 0)
            {
                _buf.Add(0);
            }
        }

        private void WriteUInt16Raw(ushort val)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, val);
            _buf.AddRange(b);
        }

        private void WriteInt32Raw(int val)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, val);
            _buf.AddRange(b);
        }

        private void WriteUInt32Raw(uint val)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, val);
            _buf.AddRange(b);
        }

        public sealed class TableBuilder
        {
            private readonly List<byte> _data = [];

            public TableBuilder(int fieldCount)
            {
                VTableOffsets = new ushort[fieldCount];
            }

            public ushort[] VTableOffsets { get; }

            public List<(int InlineOffset, Func<FlatBufferWriter, int> EmitChild)> DeferredChildren { get; } = [];

            public int InlineSize => 4 + _data.Count;

            public IReadOnlyList<byte> Payload => _data;

            public void WriteByte(int fieldIndex, byte value)
            {
                VTableOffsets[fieldIndex] = (ushort)InlineSize;
                _data.Add(value);
            }

            public void WriteInt16(int fieldIndex, short value)
            {
                PadTo(2);
                VTableOffsets[fieldIndex] = (ushort)InlineSize;
                Span<byte> b = stackalloc byte[2];
                BinaryPrimitives.WriteInt16LittleEndian(b, value);
                _data.AddRange(b);
            }

            public void WriteInt32(int fieldIndex, int value)
            {
                PadTo(4);
                VTableOffsets[fieldIndex] = (ushort)InlineSize;
                Span<byte> b = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(b, value);
                _data.AddRange(b);
            }

            public void WriteUInt32(int fieldIndex, uint value)
            {
                PadTo(4);
                VTableOffsets[fieldIndex] = (ushort)InlineSize;
                Span<byte> b = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(b, value);
                _data.AddRange(b);
            }

            public void WriteUInt64(int fieldIndex, ulong value)
            {
                PadTo(8);
                VTableOffsets[fieldIndex] = (ushort)InlineSize;
                Span<byte> b = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(b, value);
                _data.AddRange(b);
            }

            public void WriteSingle(int fieldIndex, float value)
            {
                PadTo(4);
                VTableOffsets[fieldIndex] = (ushort)InlineSize;
                Span<byte> b = stackalloc byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(b, value);
                _data.AddRange(b);
            }

            public void WriteChild(int fieldIndex, Func<FlatBufferWriter, int> emitChild)
            {
                PadTo(4);
                int inlineOffset = InlineSize;
                VTableOffsets[fieldIndex] = (ushort)inlineOffset;
                DeferredChildren.Add((inlineOffset, emitChild));
                _data.AddRange(new byte[4]);
            }

            private void PadTo(int alignment)
            {
                while (InlineSize % alignment != 0)
                {
                    _data.Add(0);
                }
            }
        }
    }
}
