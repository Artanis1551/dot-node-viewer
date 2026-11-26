using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Formats.Gguf;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Synthetic;

/// <summary>
/// Synthetic in-memory binary tests for <see cref="GgufFormatDetector"/> and <see cref="GgufFormatReader"/>.
/// </summary>
public sealed class GgufSyntheticTests
{
    [Test]
    public async Task CanRead_ValidV1V2V3AndBigEndianHeaders_TruncatedAndRandomBytes_RestoresPosition()
    {
        byte[] v1 = BuildHeaderOnlyBytes(version: 1, bigEndian: false, tensorCount: 2, kvCount: 1);
        byte[] v2 = BuildHeaderOnlyBytes(version: 2, bigEndian: false, tensorCount: 4, kvCount: 3);
        byte[] v3 = BuildHeaderOnlyBytes(version: 3, bigEndian: false, tensorCount: 8, kvCount: 5);
        byte[] v3Be = BuildHeaderOnlyBytes(version: 3, bigEndian: true, tensorCount: 8, kvCount: 5);

        foreach (byte[] valid in new[] { v1, v2, v3, v3Be })
        {
            using MemoryStream ms = new(valid);
            ms.Position = 5;
            ModelProbe probe = ModelProbe.Create(ms, "model.gguf");
            bool canRead = GgufFormatDetector.Instance.CanRead(probe);
            probe.RestorePosition();

            await Assert.That(canRead).IsTrue();
            await Assert.That(ms.Position).IsEqualTo(5);

            ModelFormatInfo? detected = ModelFormatRegistry.Default.TryDetect(ms);
            await Assert.That(detected).IsNotNull();
            await Assert.That(detected!.Id).IsEqualTo(ModelFormatId.Gguf);
            await Assert.That(ms.Position).IsEqualTo(5);
        }

        // Unsupported versions (0 and 4)
        foreach (uint badVer in new uint[] { 0, 4, 99 })
        {
            using MemoryStream ms = new(BuildHeaderOnlyBytes(badVer, bigEndian: false, 1, 1));
            ModelProbe probe = ModelProbe.Create(ms);
            await Assert.That(GgufFormatDetector.Instance.CanRead(probe)).IsFalse();
        }

        // Truncated (< 16 bytes for v1, < 24 bytes for v3)
        using (MemoryStream truncV1 = new(v1[..12]))
        {
            await Assert.That(GgufFormatDetector.Instance.CanRead(ModelProbe.Create(truncV1))).IsFalse();
        }

        using (MemoryStream truncV3 = new(v3[..20]))
        {
            await Assert.That(GgufFormatDetector.Instance.CanRead(ModelProbe.Create(truncV3))).IsFalse();
        }

        // Random bytes
        byte[] random = new byte[64];
        new Random(42).NextBytes(random);
        using (MemoryStream randMs = new(random))
        {
            await Assert.That(GgufFormatDetector.Instance.CanRead(ModelProbe.Create(randMs))).IsFalse();
        }
    }

    [Test]
    public async Task ReadAsync_GgufV2AndBigEndianV3_ParsesMetadataAndAlignedTensorSlices()
    {
        // 1. GGUF v2 with custom alignment = 64
        byte[] v2Bytes = BuildGguf(
            version: 2,
            bigEndian: false,
            kvs:
            [
                ("general.architecture", 8u, "llama"),
                ("general.name", 8u, "v2-test"),
                ("general.alignment", 4u, 64u),
                ("llama.embedding_length", 4u, 32u),
            ],
            tensors:
            [
                ("token_embd.weight", [32UL, 16UL], 1u, 1024, (byte)0xAB),
                ("output_norm.weight", [32UL], 0u, 128, (byte)0xCD),
            ],
            alignment: 64);

        using (MemoryStream ms = new(v2Bytes))
        {
            ModelGraph graph = await GgufFormatReader.Instance.ReadAsync(ms);
            await Assert.That(graph.Format.ToDisplayString()).IsEqualTo("GGUF v2");
            await Assert.That(graph.Metadata["general.alignment"]).IsEqualTo("64");

            TensorEdge tok = graph.PrimarySubgraph!.Tensors.First(t => t.Name == "token_embd.weight");
            TensorDataSlice slice = (TensorDataSlice)tok.Data!;
            await Assert.That(slice.StreamOffset % 64).IsEqualTo(0);
            await Assert.That(slice.ByteLength).IsEqualTo(1024);

            byte[] readBuf = new byte[4];
            TensorDataLoader.ReadSlice(ms, slice, readBuf);
            await Assert.That(readBuf[0]).IsEqualTo((byte)0xAB);
        }

        // 2. GGUF v3 Big-Endian
        byte[] v3BeBytes = BuildGguf(
            version: 3,
            bigEndian: true,
            kvs:
            [
                ("general.architecture", 8u, "qwen2"),
                ("general.name", 8u, "v3-be-test"),
                ("qwen2.embedding_length", 4u, 32u),
            ],
            tensors:
            [
                ("blk.0.attn_norm.weight", [32UL], 0u, 128, (byte)0x5A),
                ("blk.0.attn_q.weight", [32UL, 32UL], 2u, 576, (byte)0x7E),
            ],
            alignment: 32);

        using (MemoryStream ms = new(v3BeBytes))
        {
            ModelGraph graph = await GgufFormatReader.Instance.ReadAsync(ms);
            await Assert.That(graph.Format.ToDisplayString()).IsEqualTo("GGUF v3");
            await Assert.That(graph.Metadata["general.architecture"]).IsEqualTo("qwen2");

            TensorEdge attnQ = graph.PrimarySubgraph!.Tensors.First(t => t.Name == "blk.0.attn_q.weight");
            await Assert.That(attnQ.Descriptor.Quantization).IsTypeOf<BlockQuantization>();
            BlockQuantization bq = (BlockQuantization)attnQ.Descriptor.Quantization!;
            await Assert.That(bq.Scheme).IsEqualTo("Q4_0");
            await Assert.That(bq.BlockSize).IsEqualTo(32);
            await Assert.That(bq.Bits).IsEqualTo(4);

            TensorDataSlice slice = (TensorDataSlice)attnQ.Data!;
            await Assert.That(slice.Encoding).IsEqualTo(TensorDataEncoding.GgmlQuantizedBlock);
            await Assert.That(slice.ByteLength).IsEqualTo(576);

            byte[] readBuf = new byte[4];
            TensorDataLoader.ReadSlice(ms, slice, readBuf);
            await Assert.That(readBuf[0]).IsEqualTo((byte)0x7E);
        }
    }

    [Test]
    public async Task ReadAsync_LargeTokenizerArrays_SkipsWithoutMaterializingAllEntries()
    {
        string[] tokens = Enumerable.Range(0, 2000).Select(i => $"tok_{i}").ToArray();
        float[] scores = Enumerable.Range(0, 4000).Select(i => i * 0.1f).ToArray();

        byte[] gguf = BuildGguf(
            version: 3,
            bigEndian: false,
            kvs:
            [
                ("general.architecture", 8u, "llama"),
                ("tokenizer.ggml.tokens", 9u, (8u, (object)tokens)),
                ("tokenizer.ggml.scores", 9u, (6u, (object)scores)),
            ],
            tensors:
            [
                ("output_norm.weight", [32UL], 0u, 128, (byte)0x11),
            ],
            alignment: 32);

        using MemoryStream ms = new(gguf);
        ModelGraph graph = await GgufFormatReader.Instance.ReadAsync(ms);

        await Assert.That(graph.Metadata["tokenizer.ggml.tokens"]).StartsWith("[2000 strings: tok_0, tok_1, tok_2, tok_3");
        await Assert.That(graph.Metadata["tokenizer.ggml.scores"]).IsEqualTo("[4000 Float32 values]");
    }

    [Test]
    public async Task ReadAsync_MultiMegabytePayload_AllocatesMinimalHeapMemory()
    {
        const long payloadBytes = 16L * 1024 * 1024; // 16 MB virtual payload
        const long dims0 = 32;
        const long dims1 = payloadBytes / 4 / dims0; // F32 = 4 bytes

        byte[] headerAndMeta = BuildGgufHeaderAndTableOnly(
            kvs:
            [
                ("general.architecture", 8u, "llama"),
                ("general.name", 8u, "large-alloc-probe"),
                ("llama.embedding_length", 4u, 32u),
            ],
            tensors:
            [
                ("token_embd.weight", [(ulong)dims0, (ulong)dims1], 0u, 0UL),
            ],
            alignment: 32,
            out long dataBaseOffset);

        const long sentinelOffset = 1024;
        byte[] sentinel = [0xDE, 0xAD, 0xBE, 0xEF];
        using VirtualPayloadStream stream = new(headerAndMeta, dataBaseOffset + payloadBytes, dataBaseOffset + sentinelOffset, sentinel);

        // Warm up JIT once before measuring allocations.
        _ = await GgufFormatReader.Instance.ReadAsync(stream);
        stream.Position = 0;

        (ModelGraph graph, long allocatedBytes) = await AllocationProbe.MeasureAsync(
            () => GgufFormatReader.Instance.ReadAsync(stream));

        // Must allocate far less than 10% of the 16 MB payload.
        await Assert.That(allocatedBytes).IsLessThan(payloadBytes / 10);

        TensorEdge emb = graph.PrimarySubgraph!.Tensors.First(t => t.Name == "token_embd.weight");
        TensorDataSlice slice = (TensorDataSlice)emb.Data!;
        await Assert.That(slice.StreamOffset).IsEqualTo(dataBaseOffset);
        await Assert.That(slice.ByteLength).IsEqualTo(payloadBytes);

        byte[] check = new byte[4];
        TensorDataLoader.ReadSlice(stream, new TensorDataSlice(slice.StreamOffset + sentinelOffset, 4, DataType.UInt8), check);
        await Assert.That(check).IsEquivalentTo(sentinel);
    }

    [Test]
    public async Task ReadAsync_AllGgmlTypes_ComputesExactByteLengthsAndQuantizationMetadata()
    {
        (uint Code, string Name, int BlockSize, int TypeSize, bool IsQuant, DataType ExpectedDt)[] cases =
        [
            (0, "F32", 1, 4, false, DataType.Float32),
            (1, "F16", 1, 2, false, DataType.Float16),
            (2, "Q4_0", 32, 18, true, DataType.UInt8),
            (3, "Q4_1", 32, 20, true, DataType.UInt8),
            (6, "Q5_0", 32, 22, true, DataType.UInt8),
            (7, "Q5_1", 32, 24, true, DataType.UInt8),
            (8, "Q8_0", 32, 34, true, DataType.UInt8),
            (9, "Q8_1", 32, 36, true, DataType.UInt8),
            (10, "Q2_K", 256, 84, true, DataType.UInt8),
            (11, "Q3_K", 256, 110, true, DataType.UInt8),
            (12, "Q4_K", 256, 144, true, DataType.UInt8),
            (13, "Q5_K", 256, 176, true, DataType.UInt8),
            (14, "Q6_K", 256, 210, true, DataType.UInt8),
            (15, "Q8_K", 256, 292, true, DataType.UInt8),
            (16, "IQ2_XXS", 256, 66, true, DataType.UInt8),
            (17, "IQ2_XS", 256, 74, true, DataType.UInt8),
            (18, "IQ3_XXS", 256, 98, true, DataType.UInt8),
            (19, "IQ1_S", 256, 50, true, DataType.UInt8),
            (20, "IQ4_NL", 32, 18, true, DataType.UInt8),
            (21, "IQ3_S", 256, 110, true, DataType.UInt8),
            (22, "IQ2_S", 256, 82, true, DataType.UInt8),
            (23, "IQ4_XS", 256, 136, true, DataType.UInt8),
            (24, "I8", 1, 1, false, DataType.Int8),
            (25, "I16", 1, 2, false, DataType.Int16),
            (26, "I32", 1, 4, false, DataType.Int32),
            (27, "I64", 1, 8, false, DataType.Int64),
            (28, "F64", 1, 8, false, DataType.Float64),
            (29, "IQ1_M", 256, 56, true, DataType.UInt8),
            (30, "BF16", 1, 2, false, DataType.BFloat16),
            (34, "TQ1_0", 256, 54, true, DataType.UInt8),
            (35, "TQ2_0", 256, 66, true, DataType.UInt8),
            (39, "MXFP4", 32, 17, true, DataType.UInt8),
        ];

        List<(string Name, ulong[] Dims, uint GgmlType, int ByteLen, byte Fill)> tensorSpecs = [];
        for (int i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            ulong ne0 = (ulong)(c.BlockSize > 1 ? c.BlockSize : 4);
            int expectedBytes = c.BlockSize > 1 ? c.TypeSize : 4 * c.TypeSize;
            tensorSpecs.Add(($"w_{c.Name}", [ne0], c.Code, expectedBytes, (byte)(i + 1)));
        }

        byte[] gguf = BuildGguf(
            version: 3,
            bigEndian: false,
            kvs: [("general.architecture", 8u, "llama")],
            tensors: tensorSpecs.ToArray(),
            alignment: 32);

        using MemoryStream ms = new(gguf);
        ModelGraph graph = await GgufFormatReader.Instance.ReadAsync(ms);
        Subgraph sg = graph.PrimarySubgraph!;

        foreach (var c in cases)
        {
            TensorEdge edge = sg.Tensors.First(t => t.Name == $"w_{c.Name}");
            await Assert.That(edge.Descriptor.DataType).IsEqualTo(c.ExpectedDt);
            TensorDataSlice slice = (TensorDataSlice)edge.Data!;
            int expectedBytes = c.BlockSize > 1 ? c.TypeSize : 4 * c.TypeSize;
            await Assert.That(slice.ByteLength).IsEqualTo(expectedBytes);

            if (c.IsQuant)
            {
                await Assert.That(slice.Encoding).IsEqualTo(TensorDataEncoding.GgmlQuantizedBlock);
                await Assert.That(edge.Descriptor.Quantization).IsTypeOf<BlockQuantization>();
                BlockQuantization bq = (BlockQuantization)edge.Descriptor.Quantization!;
                await Assert.That(bq.Scheme).IsEqualTo(c.Name);
                await Assert.That(bq.BlockSize).IsEqualTo(c.BlockSize);
            }
            else
            {
                await Assert.That(slice.Encoding).IsEqualTo(TensorDataEncoding.Raw);
                await Assert.That(edge.Descriptor.Quantization).IsNull();
            }
        }
    }

    [Test]
    public async Task ReadAsync_ArchitectureSynthesis_SupportsModernAndUnknownArchitectures()
    {
        // Moshi full-duplex speech: text_embd + blk.0 + dep.0 + text_head + audio_head
        byte[] moshi = BuildGguf(
            version: 3,
            bigEndian: false,
            kvs: [("general.architecture", 8u, "moshi"), ("moshi.embedding_length", 4u, 32u)],
            tensors:
            [
                ("text_embd.weight", [32UL, 16UL], 1u, 1024, (byte)1),
                ("blk.0.attn_norm.weight", [32UL], 0u, 128, (byte)2),
                ("blk.0.attn_q.weight", [32UL, 32UL], 2u, 576, (byte)3),
                ("dep.0.attn_norm.weight", [32UL], 0u, 128, (byte)4),
                ("dep.0.attn_q.weight", [32UL, 32UL], 2u, 576, (byte)5),
                ("text_head.weight", [32UL, 16UL], 1u, 1024, (byte)6),
                ("audio_head.weight", [32UL, 16UL], 1u, 1024, (byte)7),
            ],
            alignment: 32);

        using (MemoryStream ms = new(moshi))
        {
            ModelGraph graph = await GgufFormatReader.Instance.ReadAsync(ms);
            string[] ops = graph.PrimarySubgraph!.Nodes.Select(n => n.Operator.Name).ToArray();
            await Assert.That(ops).IsEquivalentTo(["TextEmbedding", "TransformerBlock", "DepthTransformerBlock", "TextHead", "AudioHead"]);
        }

        // Unseen future architecture fallback
        byte[] future = BuildGguf(
            version: 3,
            bigEndian: false,
            kvs: [("general.architecture", 8u, "future-arch-99")],
            tensors:
            [
                ("custom_stem.conv.weight", [32UL, 8UL], 0u, 1024, (byte)1),
                ("blk.0.attn_norm.weight", [32UL], 0u, 128, (byte)2),
                ("blk.0.attn_q.weight", [32UL, 32UL], 2u, 576, (byte)3),
                ("blk.0.ffn_gate.weight", [32UL, 32UL], 2u, 576, (byte)4),
                ("output.weight", [32UL, 16UL], 0u, 2048, (byte)5),
            ],
            alignment: 32);

        using (MemoryStream ms = new(future))
        {
            ModelGraph graph = await GgufFormatReader.Instance.ReadAsync(ms);
            string[] ops = graph.PrimarySubgraph!.Nodes.Select(n => n.Operator.Name).ToArray();
            await Assert.That(ops).IsEquivalentTo(["Module", "TransformerBlock", "LMHead"]);

            GraphNode blk0 = graph.PrimarySubgraph!.Nodes[1];
            NodeAttribute? subAttr = blk0.Attributes.FirstOrDefault(a => a.Name == "subgraph");
            await Assert.That(subAttr).IsNotNull();
            await Assert.That(subAttr!.Value).IsTypeOf<AttributeValue.Graph>();
            Subgraph child = ((AttributeValue.Graph)subAttr.Value).Value;
            await Assert.That(child.Nodes.Select(n => n.Operator.Name).ToArray())
                .IsEquivalentTo(["RMSNorm", "Attention", "FeedForward"]);
        }
    }

    [Test]
    public async Task ReadAsync_MalformedOverflowAndTruncatedStreams_ThrowsModelParseException()
    {
        // 1. 128-bit dimension overflow
        byte[] overflow = BuildGgufHeaderAndTableOnly(
            kvs: [],
            tensors: [("huge", [ulong.MaxValue, ulong.MaxValue], 0u, 0UL)],
            alignment: 32,
            out _);
        await AssertThrowsModelParseExceptionAsync(overflow);

        // 2. Quantized ne[0] not divisible by block_size (Q4_0 requires % 32 == 0)
        byte[] badDiv = BuildGgufHeaderAndTableOnly(
            kvs: [],
            tensors: [("bad_q4", [17UL, 4UL], 2u, 0UL)],
            alignment: 32,
            out _);
        await AssertThrowsModelParseExceptionAsync(badDiv);

        // 3. Tensor offset out of stream bounds
        byte[] oobTensor = BuildGgufHeaderAndTableOnly(
            kvs: [],
            tensors: [("oob", [32UL], 0u, 1024UL)],
            alignment: 32,
            out _);
        await AssertThrowsModelParseExceptionAsync(oobTensor);

        // 4. Unknown ggml_type
        byte[] unknownType = BuildGgufHeaderAndTableOnly(
            kvs: [],
            tensors: [("unk", [32UL], 9999u, 0UL)],
            alignment: 32,
            out _);
        await AssertThrowsModelParseExceptionAsync(unknownType);

        // 5. Invalid non-power-of-two alignment
        byte[] badAlign = BuildGguf(
            version: 3,
            bigEndian: false,
            kvs: [("general.alignment", 4u, 24u)],
            tensors: [],
            alignment: 32);
        await AssertThrowsModelParseExceptionAsync(badAlign);

        // 6. Truncated KV section
        byte[] valid = BuildGguf(
            version: 3,
            bigEndian: false,
            kvs: [("general.architecture", 8u, "llama")],
            tensors: [("w", [32UL], 0u, 128, (byte)1)],
            alignment: 32);
        await AssertThrowsModelParseExceptionAsync(valid[..30]);
    }

    private static async Task AssertThrowsModelParseExceptionAsync(byte[] bytes)
    {
        using MemoryStream ms = new(bytes);
        await Assert.That(async () => await GgufFormatReader.Instance.ReadAsync(ms))
            .Throws<ModelParseException>();
    }

    private static byte[] BuildHeaderOnlyBytes(uint version, bool bigEndian, ulong tensorCount, ulong kvCount)
    {
        if (version == 1)
        {
            byte[] buf = new byte[16];
            "GGUF"u8.CopyTo(buf);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), version);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), (uint)tensorCount);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), (uint)kvCount);
            return buf;
        }

        byte[] buf3 = new byte[24];
        "GGUF"u8.CopyTo(buf3);
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf3.AsSpan(4, 4), version);
            BinaryPrimitives.WriteUInt64BigEndian(buf3.AsSpan(8, 8), tensorCount);
            BinaryPrimitives.WriteUInt64BigEndian(buf3.AsSpan(16, 8), kvCount);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf3.AsSpan(4, 4), version);
            BinaryPrimitives.WriteUInt64LittleEndian(buf3.AsSpan(8, 8), tensorCount);
            BinaryPrimitives.WriteUInt64LittleEndian(buf3.AsSpan(16, 8), kvCount);
        }

        return buf3;
    }

    private static byte[] BuildGguf(
        uint version,
        bool bigEndian,
        (string Key, uint Type, object Value)[] kvs,
        (string Name, ulong[] Dims, uint GgmlType, int ByteLen, byte Fill)[] tensors,
        uint alignment)
    {
        using MemoryStream ms = new();
        WriteHeaderAndTable(ms, version, bigEndian, kvs,
            tensors.Select((t, i) => (t.Name, t.Dims, t.GgmlType, ComputeRelOffset(tensors, i, alignment))).ToArray(),
            alignment,
            out long dataBaseOffset);

        long totalSize = dataBaseOffset;
        for (int i = 0; i < tensors.Length; i++)
        {
            long rel = (long)ComputeRelOffset(tensors, i, alignment);
            totalSize = Math.Max(totalSize, dataBaseOffset + rel + tensors[i].ByteLen);
        }

        byte[] full = new byte[totalSize];
        byte[] headerBytes = ms.ToArray();
        headerBytes.CopyTo(full, 0);

        for (int i = 0; i < tensors.Length; i++)
        {
            long rel = (long)ComputeRelOffset(tensors, i, alignment);
            Array.Fill(full, tensors[i].Fill, (int)(dataBaseOffset + rel), tensors[i].ByteLen);
        }

        return full;
    }

    private static ulong ComputeRelOffset(
        (string Name, ulong[] Dims, uint GgmlType, int ByteLen, byte Fill)[] tensors,
        int index,
        uint alignment)
    {
        ulong offset = 0;
        for (int i = 0; i < index; i++)
        {
            offset += (ulong)tensors[i].ByteLen;
            ulong rem = offset % alignment;
            if (rem != 0)
            {
                offset += alignment - rem;
            }
        }

        return offset;
    }

    private static byte[] BuildGgufHeaderAndTableOnly(
        (string Key, uint Type, object Value)[] kvs,
        (string Name, ulong[] Dims, uint GgmlType, ulong RelOffset)[] tensors,
        uint alignment,
        out long dataBaseOffset)
    {
        using MemoryStream ms = new();
        WriteHeaderAndTable(ms, 3, false, kvs, tensors, alignment, out dataBaseOffset);
        return ms.ToArray();
    }

    private static void WriteHeaderAndTable(
        MemoryStream ms,
        uint version,
        bool bigEndian,
        (string Key, uint Type, object Value)[] kvs,
        (string Name, ulong[] Dims, uint GgmlType, ulong RelOffset)[] tensors,
        uint alignment,
        out long dataBaseOffset)
    {
        ms.Write("GGUF"u8);
        WriteU32(ms, version, bigEndian);
        WriteCount(ms, (ulong)tensors.Length, version, bigEndian);
        WriteCount(ms, (ulong)kvs.Length, version, bigEndian);

        foreach (var (key, type, val) in kvs)
        {
            WriteGgufString(ms, key, version, bigEndian);
            WriteU32(ms, type, bigEndian);
            if (type == 4)
            {
                WriteU32(ms, Convert.ToUInt32(val, CultureInfo.InvariantCulture), bigEndian);
            }
            else if (type == 8)
            {
                WriteGgufString(ms, (string)val, version, bigEndian);
            }
            else if (type == 9)
            {
                (uint elemType, object arrObj) = ((uint, object))val;
                WriteU32(ms, elemType, bigEndian);
                if (elemType == 8)
                {
                    string[] strs = (string[])arrObj;
                    WriteCount(ms, (ulong)strs.Length, version, bigEndian);
                    foreach (string s in strs)
                    {
                        WriteGgufString(ms, s, version, bigEndian);
                    }
                }
                else if (elemType == 6)
                {
                    float[] floats = (float[])arrObj;
                    WriteCount(ms, (ulong)floats.Length, version, bigEndian);
                    foreach (float f in floats)
                    {
                        byte[] buf = new byte[4];
                        if (bigEndian)
                        {
                            BinaryPrimitives.WriteSingleBigEndian(buf, f);
                        }
                        else
                        {
                            BinaryPrimitives.WriteSingleLittleEndian(buf, f);
                        }
                        ms.Write(buf);
                    }
                }
            }
        }

        foreach (var (name, dims, ggmlType, relOffset) in tensors)
        {
            WriteGgufString(ms, name, version, bigEndian);
            WriteU32(ms, (uint)dims.Length, bigEndian);
            foreach (ulong d in dims)
            {
                WriteCount(ms, d, version, bigEndian);
            }
            WriteU32(ms, ggmlType, bigEndian);
            WriteU64(ms, relOffset, bigEndian);
        }

        long pos = ms.Position;
        long rem = pos % alignment;
        dataBaseOffset = rem == 0 ? pos : pos + (alignment - rem);
    }

    private static void WriteGgufString(Stream s, string value, uint version, bool bigEndian)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteCount(s, (ulong)bytes.Length, version, bigEndian);
        s.Write(bytes);
    }

    private static void WriteCount(Stream s, ulong value, uint version, bool bigEndian)
    {
        if (version == 1)
        {
            WriteU32(s, (uint)value, bigEndian);
        }
        else
        {
            WriteU64(s, value, bigEndian);
        }
    }

    private static void WriteU32(Stream s, uint value, bool bigEndian)
    {
        Span<byte> buf = stackalloc byte[4];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        }
        s.Write(buf);
    }

    private static void WriteU64(Stream s, ulong value, bool bigEndian)
    {
        Span<byte> buf = stackalloc byte[8];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt64BigEndian(buf, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        }
        s.Write(buf);
    }

    private sealed class VirtualPayloadStream(
        byte[] headerPrefix,
        long virtualLength,
        long sentinelOffset,
        byte[] sentinelBytes) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => virtualLength;
        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= virtualLength || buffer.IsEmpty)
            {
                return 0;
            }

            int toRead = (int)Math.Min(buffer.Length, virtualLength - _position);
            Span<byte> dst = buffer[..toRead];
            dst.Clear();

            if (_position < headerPrefix.Length)
            {
                int copyLen = (int)Math.Min(toRead, headerPrefix.Length - _position);
                headerPrefix.AsSpan((int)_position, copyLen).CopyTo(dst);
            }

            long sentinelEnd = sentinelOffset + sentinelBytes.Length;
            long readEnd = _position + toRead;
            long overlapStart = Math.Max(_position, sentinelOffset);
            long overlapEnd = Math.Min(readEnd, sentinelEnd);
            if (overlapStart < overlapEnd)
            {
                int srcOff = (int)(overlapStart - sentinelOffset);
                int dstOff = (int)(overlapStart - _position);
                int len = (int)(overlapEnd - overlapStart);
                sentinelBytes.AsSpan(srcOff, len).CopyTo(dst.Slice(dstOff, len));
            }

            _position += toRead;
            return toRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => virtualLength + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
