using System.Buffers.Binary;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Formats.ExecuTorch;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Synthetic;

/// <summary>
/// Synthetic in-memory binary tests for <see cref="ExecuTorchFormatDetector"/>, <see cref="ExecuTorchFormatReader"/>, and <see cref="ExecuTorchOperatorCatalog"/>.
/// </summary>
public sealed class ExecuTorchSyntheticTests
{
    [Test]
    public async Task CanRead_ValidET12HeaderTruncatedAndRandomBytes_ReturnsExpectedResultAndRestoresPosition()
    {
        byte[] validModel = BuildSyntheticExecuTorchModel(weightBytes: [1, 2, 3, 4, 5, 6, 7, 8]);
        using MemoryStream validStream = new(validModel);
        validStream.Position = 6;

        ModelProbe validProbe = ModelProbe.Create(validStream, "synthetic.pte");
        bool canRead = ExecuTorchFormatDetector.Instance.CanRead(validProbe);
        validProbe.RestorePosition();

        await Assert.That(canRead).IsTrue();
        await Assert.That(validStream.Position).IsEqualTo(6);

        ModelFormatInfo? detectedViaRegistry = ModelFormatRegistry.Default.TryDetect(validStream);
        await Assert.That(detectedViaRegistry).IsNotNull();
        await Assert.That(detectedViaRegistry!.Id).IsEqualTo(ModelFormatId.ExecuTorch);
        await Assert.That(validStream.Position).IsEqualTo(6);

        // Truncated header (< 8 bytes)
        using MemoryStream truncatedStream = new(validModel[..6]);
        ModelProbe truncatedProbe = ModelProbe.Create(truncatedStream, "truncated.pte");
        await Assert.That(ExecuTorchFormatDetector.Instance.CanRead(truncatedProbe)).IsFalse();

        // Wrong magic identifier
        byte[] wrongMagic = (byte[])validModel.Clone();
        wrongMagic[4] = (byte)'T';
        wrongMagic[5] = (byte)'F';
        wrongMagic[6] = (byte)'L';
        wrongMagic[7] = (byte)'3';
        using MemoryStream wrongMagicStream = new(wrongMagic);
        ModelProbe wrongMagicProbe = ModelProbe.Create(wrongMagicStream, "wrong.pte");
        await Assert.That(ExecuTorchFormatDetector.Instance.CanRead(wrongMagicProbe)).IsFalse();

        // Out-of-bounds root table offset
        byte[] badRoot = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(badRoot.AsSpan(0, 4), 2048);
        "ET12"u8.CopyTo(badRoot.AsSpan(4, 4));
        using MemoryStream badRootStream = new(badRoot);
        ModelProbe badRootProbe = ModelProbe.Create(badRootStream, "bad_root.pte");
        await Assert.That(ExecuTorchFormatDetector.Instance.CanRead(badRootProbe)).IsFalse();
    }

    [Test]
    public async Task ReadAsync_TopologyDynamicShapesAndXnnpackDelegate_MapsExpectedGraph()
    {
        byte[] rawWeights = [10, 20, 30, 40, 50, 60, 70, 80];
        byte[] delegatePayload = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        byte[] modelBytes = BuildSyntheticExecuTorchModel(rawWeights, delegatePayload, includeExtendedHeader: true);

        using MemoryStream stream = new(modelBytes);
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "synth.pte" });

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.ExecuTorch);
        await Assert.That(graph.Format.DisplayName).IsEqualTo("ExecuTorch");
        await Assert.That(graph.Producer).IsEqualTo("executorch");
        await Assert.That(graph.Subgraphs.Count).IsEqualTo(1);
        await Assert.That(graph.Metadata.ContainsKey("executorch.extended_header")).IsTrue();

        Subgraph main = graph.PrimarySubgraph!;
        await Assert.That(main.Name).IsEqualTo("forward");
        await Assert.That(main.Nodes.Count).IsEqualTo(2);
        await Assert.That(main.Inputs.Count).IsEqualTo(1);
        await Assert.That(main.Outputs.Count).IsEqualTo(1);

        // Input tensor has dynamic bound dimension [?, 4]
        TensorEdge inputEdge = main.Inputs[0].Tensors[0];
        await Assert.That(inputEdge.Descriptor.DataType).IsEqualTo(DataType.Float32);
        await Assert.That(inputEdge.Descriptor.Shape).IsNotNull();
        await Assert.That(inputEdge.Descriptor.Shape!.ToDisplayString()).IsEqualTo("[?, 4]");

        // Node 0: KernelCall aten::linear.out
        GraphNode kernelNode = main.Nodes[0];
        await Assert.That(kernelNode.Operator.Name).IsEqualTo("aten::linear.out");
        await Assert.That(kernelNode.Operator.Category).IsEqualTo(OperatorCategory.Layer);

        // Node 1: DelegateCall Delegate (XnnpackBackend)
        GraphNode delegateNode = main.Nodes[1];
        await Assert.That(delegateNode.Operator.Name).IsEqualTo("Delegate (XnnpackBackend)");
        await Assert.That(delegateNode.Operator.Category).IsEqualTo(OperatorCategory.Custom);
        NodeAttribute? payloadAttr = delegateNode.Attributes.FirstOrDefault(a => a.Name == "processed");
        await Assert.That(payloadAttr).IsNotNull();
        await Assert.That(payloadAttr!.Value).IsTypeOf<AttributeValue.Bytes>();

        AttributeValue.Bytes bytesVal = (AttributeValue.Bytes)payloadAttr.Value;
        await Assert.That(bytesVal.Slice.ByteLength).IsEqualTo(delegatePayload.Length);
        byte[] readBuf = new byte[bytesVal.Slice.ByteLength];
        int readBytes = TensorDataLoader.ReadSlice(stream, bytesVal.Slice, readBuf);
        await Assert.That(readBytes).IsEqualTo(delegatePayload.Length);
        await Assert.That(readBuf.SequenceEqual(delegatePayload)).IsTrue();
    }

    [Test]
    public async Task ReadAsync_LazyWeightBuffers_ExtractsValidOffsetsWithoutHeapArrayAllocations()
    {
        byte[] rawWeights = new byte[1024];
        for (int i = 0; i < rawWeights.Length; i++)
        {
            rawWeights[i] = (byte)(i % 256);
        }

        byte[] modelBytes = BuildSyntheticExecuTorchModel(rawWeights);
        using MemoryStream stream = new(modelBytes);

        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "alloc.pte" });
        long deltaAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;

        // Weight payload of 1024 bytes should not be eagerly allocated on the managed heap
        await Assert.That(deltaAlloc < 200_000).IsTrue();

        TensorEdge? weightEdge = graph.PrimarySubgraph!.Tensors.FirstOrDefault(t => t.Kind == TensorKind.Initializer);
        await Assert.That(weightEdge).IsNotNull();
        await Assert.That(weightEdge!.Data).IsTypeOf<TensorDataSlice>();

        TensorDataSlice slice = (TensorDataSlice)weightEdge.Data!;
        await Assert.That(slice.StreamOffset > 0).IsTrue();
        await Assert.That(slice.ByteLength).IsEqualTo(rawWeights.Length);
        await Assert.That(slice.StreamOffset + slice.ByteLength <= modelBytes.Length).IsTrue();
    }

    [Test]
    public async Task ReadAsync_TruncatedAndCorruptedStreams_ThrowsModelParseExceptionCleanly()
    {
        byte[] validModel = BuildSyntheticExecuTorchModel(weightBytes: [1, 2, 3, 4]);

        // Truncated body (< model length)
        byte[] truncated = validModel[..(validModel.Length / 2)];
        using MemoryStream truncStream = new(truncated);
        await Assert.That(async () =>
            await ModelFormatRegistry.Default.ReadAsync(truncStream, new ModelReadOptions { FileName = "corrupt.pte" }))
            .Throws<ModelParseException>();

        // Corrupted vtable soffset
        byte[] badVtable = (byte[])validModel.Clone();
        uint rootUOffset = BinaryPrimitives.ReadUInt32LittleEndian(badVtable.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(badVtable.AsSpan((int)rootUOffset, 4), 99999);
        using MemoryStream badVtableStream = new(badVtable);
        await Assert.That(async () =>
            await ModelFormatRegistry.Default.ReadAsync(badVtableStream, new ModelReadOptions { FileName = "bad_vtable.pte" }))
            .Throws<ModelParseException>();
    }

    private static byte[] BuildSyntheticExecuTorchModel(
        byte[] weightBytes,
        byte[]? delegatePayload = null,
        bool includeExtendedHeader = false)
    {
        byte[] delPayload = delegatePayload ?? [0x11, 0x22, 0x33];
        MiniFlatBufferWriter writer = new();

        // 1. Strings
        int planNameStr = writer.WriteString("forward");
        int opNameStr = writer.WriteString("aten::linear");
        int overloadStr = writer.WriteString("out");
        int backendIdStr = writer.WriteString("XnnpackBackend");
        int t0NameStr = writer.WriteString("x");
        int t1NameStr = writer.WriteString("weight");
        int t2NameStr = writer.WriteString("y");
        int t3NameStr = writer.WriteString("out");

        // 2. Weight Buffer: Buffer table (storage = byte vector)
        int weightVec = writer.WriteByteVector(weightBytes);
        int buf0 = writer.WriteTable([]); // index 0 sentinel
        int buf1 = writer.WriteTable([(0, FieldType.UOffset, weightVec)]);
        int constantBuffersVec = writer.WriteOffsetVector([buf0, buf1]);

        // 3. Delegate Inline Data: BackendDelegateInlineData table (data = byte vector)
        int delDataVec = writer.WriteByteVector(delPayload);
        int delInline0 = writer.WriteTable([(0, FieldType.UOffset, delDataVec)]);
        int backendDelegateDataVec = writer.WriteOffsetVector([delInline0]);

        // 4. Operator table: Operator (name = "aten::linear", overload = "out")
        int op0 = writer.WriteTable([
            (0, FieldType.UOffset, opNameStr),
            (1, FieldType.UOffset, overloadStr),
        ]);
        int operatorsVec = writer.WriteOffsetVector([op0]);

        // 5. BackendDelegate: BackendDelegateDataReference (location=0 INLINE, index=0)
        int delDataRef = writer.WriteTable([
            (0, FieldType.Byte, 0), // location = INLINE
            (1, FieldType.Int32, 0), // index = 0
        ]);
        int delegate0 = writer.WriteTable([
            (0, FieldType.UOffset, backendIdStr),
            (1, FieldType.UOffset, delDataRef),
        ]);
        int delegatesVec = writer.WriteOffsetVector([delegate0]);

        // 6. Tensors
        // Tensor[0]: input x, sizes=[-1, 4], scalar_type=FLOAT(6), dynamism=DYNAMIC_BOUND(1), data_buffer_idx=0
        int shape0 = writer.WriteInt32Vector([-1, 4]);
        int tensor0 = writer.WriteTable([
            (0, FieldType.Byte, 6), // FLOAT
            (2, FieldType.UOffset, shape0),
            (5, FieldType.Int32, 0), // data_buffer_idx = 0
            (8, FieldType.Byte, 1),  // DYNAMIC_BOUND
        ]);

        // Tensor[1]: weight, sizes=[2, 4], scalar_type=FLOAT(6), data_buffer_idx=1
        int shape1 = writer.WriteInt32Vector([2, 4]);
        int tensor1 = writer.WriteTable([
            (0, FieldType.Byte, 6),
            (2, FieldType.UOffset, shape1),
            (5, FieldType.Int32, 1), // data_buffer_idx = 1
        ]);

        // Tensor[2]: intermediate y, sizes=[1, 2], scalar_type=FLOAT(6)
        int shape2 = writer.WriteInt32Vector([1, 2]);
        int tensor2 = writer.WriteTable([
            (0, FieldType.Byte, 6),
            (2, FieldType.UOffset, shape2),
            (5, FieldType.Int32, 0),
        ]);

        // Tensor[3]: output out, sizes=[1, 2], scalar_type=FLOAT(6)
        int shape3 = writer.WriteInt32Vector([1, 2]);
        int tensor3 = writer.WriteTable([
            (0, FieldType.Byte, 6),
            (2, FieldType.UOffset, shape3),
            (5, FieldType.Int32, 0),
        ]);

        // Values: EValue table (val = Tensor)
        int evalue0 = writer.WriteTable([(0, FieldType.Byte, 5), (1, FieldType.UOffset, tensor0)]);
        int evalue1 = writer.WriteTable([(0, FieldType.Byte, 5), (1, FieldType.UOffset, tensor1)]);
        int evalue2 = writer.WriteTable([(0, FieldType.Byte, 5), (1, FieldType.UOffset, tensor2)]);
        int evalue3 = writer.WriteTable([(0, FieldType.Byte, 5), (1, FieldType.UOffset, tensor3)]);
        int valuesVec = writer.WriteOffsetVector([evalue0, evalue1, evalue2, evalue3]);

        // 7. Instructions:
        //    Inst[0]: KernelCall op_index=0, args=[0, 1, 2, 2] (inputs: 0, 1; output: 2)
        int inst0Args = writer.WriteInt32Vector([0, 1, 2, 2]);
        int kernelCall0 = writer.WriteTable([
            (0, FieldType.Int32, 0),
            (1, FieldType.UOffset, inst0Args),
        ]);
        int inst0 = writer.WriteTable([
            (0, FieldType.Byte, 1), // KernelCall
            (1, FieldType.UOffset, kernelCall0),
        ]);

        //    Inst[1]: DelegateCall delegate_index=0, args=[2, 3] (input: 2, output: 3)
        int inst1Args = writer.WriteInt32Vector([2, 3]);
        int delegateCall0 = writer.WriteTable([
            (0, FieldType.Int32, 0),
            (1, FieldType.UOffset, inst1Args),
        ]);
        int inst1 = writer.WriteTable([
            (0, FieldType.Byte, 2), // DelegateCall
            (1, FieldType.UOffset, delegateCall0),
        ]);

        int instructionsVec = writer.WriteOffsetVector([inst0, inst1]);

        // Chain table
        int chain0 = writer.WriteTable([(2, FieldType.UOffset, instructionsVec)]);
        int chainsVec = writer.WriteOffsetVector([chain0]);

        // ExecutionPlan table
        int planInputs = writer.WriteInt32Vector([0]);
        int planOutputs = writer.WriteInt32Vector([3]);
        int plan0 = writer.WriteTable([
            (0, FieldType.UOffset, planNameStr),
            (2, FieldType.UOffset, valuesVec),
            (3, FieldType.UOffset, planInputs),
            (4, FieldType.UOffset, planOutputs),
            (5, FieldType.UOffset, chainsVec),
            (6, FieldType.UOffset, operatorsVec),
            (7, FieldType.UOffset, delegatesVec),
        ]);
        int executionPlansVec = writer.WriteOffsetVector([plan0]);

        // Root Program table
        int programTable = writer.WriteTable([
            (0, FieldType.Int32, 1), // version = 1
            (1, FieldType.UOffset, executionPlansVec),
            (2, FieldType.UOffset, constantBuffersVec),
            (3, FieldType.UOffset, backendDelegateDataVec),
        ]);

        byte[] fbBytes = writer.Finish(programTable, "ET12"u8);

        if (!includeExtendedHeader)
        {
            return fbBytes;
        }

        // Insert extended header "eh00" starting at offset 8
        // Layout:
        // [0..4]: rootUOffset
        // [4..8]: "ET12"
        // [8..40]: eh00 (32 bytes)
        // [40..]: FlatBuffers payload shifted by 32 bytes
        byte[] withEh = new byte[fbBytes.Length + 32];
        // Original rootUOffset points to programTable. Since everything from offset 8 onwards shifts by 32 bytes,
        // rootUOffset increases by 32!
        uint origRoot = BinaryPrimitives.ReadUInt32LittleEndian(fbBytes.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(withEh.AsSpan(0, 4), origRoot + 32);
        fbBytes.AsSpan(4, 4).CopyTo(withEh.AsSpan(4, 4)); // "ET12"

        // Extended header "eh00"
        "eh00"u8.CopyTo(withEh.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(withEh.AsSpan(12, 4), 32); // length = 32
        BinaryPrimitives.WriteInt64LittleEndian(withEh.AsSpan(16, 8), withEh.Length); // program_size
        BinaryPrimitives.WriteInt64LittleEndian(withEh.AsSpan(24, 8), withEh.Length); // segment_base_offset
        BinaryPrimitives.WriteInt64LittleEndian(withEh.AsSpan(32, 8), 0); // segment_data_size

        // Copy remainder of flatbuffer bytes
        fbBytes.AsSpan(8).CopyTo(withEh.AsSpan(40));

        return withEh;
    }

    private enum FieldType
    {
        Byte,
        Int32,
        UOffset,
    }

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

            // 4-byte fields first, then 1-byte fields
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    (int fieldId, FieldType type, int val) = fields[i];
                    if (pass == 0 && (type == FieldType.Int32 || type == FieldType.UOffset))
                    {
                        while ((body.Count % 4) != 0)
                        {
                            body.Add(0);
                        }

                        fieldOffsets[fieldId] = (ushort)body.Count;
                        if (type == FieldType.Int32)
                        {
                            BinaryPrimitives.WriteInt32LittleEndian(tmp, val);
                            for (int b = 0; b < 4; b++)
                            {
                                body.Add(tmp[b]);
                            }
                        }
                        else
                        {
                            fixups.Add((body.Count, val));
                            for (int b = 0; b < 4; b++)
                            {
                                body.Add(0);
                            }
                        }
                    }
                    else if (pass == 1 && type == FieldType.Byte)
                    {
                        fieldOffsets[fieldId] = (ushort)body.Count;
                        body.Add((byte)val);
                    }
                }
            }

            while ((body.Count % 4) != 0)
            {
                body.Add(0);
            }

            int vtableSize = 4 + (numFields * 2);
            byte[] vtableBytes = new byte[vtableSize];
            BinaryPrimitives.WriteUInt16LittleEndian(vtableBytes.AsSpan(0, 2), (ushort)vtableSize);
            BinaryPrimitives.WriteUInt16LittleEndian(vtableBytes.AsSpan(2, 2), (ushort)body.Count);
            for (int i = 0; i < numFields; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(vtableBytes.AsSpan(4 + (i * 2), 2), fieldOffsets[i]);
            }

            int vtableChunkId = AddChunk(alignment: 2, vtableBytes, []);
            int tableChunkId = AddChunk(alignment: 4, body.ToArray(), fixups);
            _chunks[tableChunkId].VTableTargetChunkId = vtableChunkId;
            return tableChunkId;
        }

        public byte[] Finish(int rootTableChunkId, ReadOnlySpan<byte> fileIdentifier)
        {
            List<Chunk> ordered = [];
            for (int i = _chunks.Count - 1; i >= 0; i--)
            {
                ordered.Add(_chunks[i]);
            }

            int headerSize = 8;
            int currentOffset = headerSize;
            Dictionary<int, int> chunkOffsets = [];

            for (int i = 0; i < ordered.Count; i++)
            {
                Chunk c = ordered[i];
                int align = c.Alignment;
                int rem = currentOffset % align;
                if (rem != 0)
                {
                    currentOffset += align - rem;
                }

                chunkOffsets[c.Id] = currentOffset;
                currentOffset += c.Bytes.Length;
            }

            byte[] buffer = new byte[currentOffset];
            int rootOffset = chunkOffsets[rootTableChunkId];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)rootOffset);
            fileIdentifier.CopyTo(buffer.AsSpan(4, 4));

            for (int i = 0; i < ordered.Count; i++)
            {
                Chunk c = ordered[i];
                int start = chunkOffsets[c.Id];
                c.Bytes.CopyTo(buffer.AsSpan(start));

                if (c.VTableTargetChunkId >= 0)
                {
                    int vtableStart = chunkOffsets[c.VTableTargetChunkId];
                    int soffset = start - vtableStart;
                    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(start, 4), soffset);
                }

                for (int f = 0; f < c.Fixups.Count; f++)
                {
                    (int slotOffset, int targetId) = c.Fixups[f];
                    int targetStart = chunkOffsets[targetId];
                    int slotGlobal = start + slotOffset;
                    int uoffset = targetStart - slotGlobal;
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(slotGlobal, 4), unchecked((uint)uoffset));
                }
            }

            return buffer;
        }

        private int AddChunk(int alignment, byte[] data, List<(int RelativeSlotOffset, int TargetChunkId)> fixups)
        {
            int id = _chunks.Count;
            _chunks.Add(new Chunk(id, alignment, data, fixups));
            return id;
        }

        private sealed class Chunk(
            int id,
            int alignment,
            byte[] bytes,
            List<(int RelativeSlotOffset, int TargetChunkId)> fixups)
        {
            public int Id { get; } = id;
            public int Alignment { get; } = alignment;
            public byte[] Bytes { get; } = bytes;
            public List<(int RelativeSlotOffset, int TargetChunkId)> Fixups { get; } = fixups;
            public int VTableTargetChunkId { get; set; } = -1;
        }
    }
}

