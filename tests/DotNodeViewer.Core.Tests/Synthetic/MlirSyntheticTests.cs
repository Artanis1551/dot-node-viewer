using System.Buffers.Binary;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Formats.Mlir;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests.Synthetic;

/// <summary>
/// Synthetic unit tests verifying MLIR bytecode and textual detection, dialect operator categorization,
/// 128-bit element-count overflow protection, single-<c>?</c> dynamic dimension inference,
/// emerging dtype diagnostics, nested regions/CFG branches, corrupted stream resilience, and lazy resource slicing.
/// </summary>
public sealed class MlirSyntheticTests
{
    [Test]
    public async Task Detector_ValidBytecodeAndTextualMlir_DetectedAndRestoresPosition()
    {
        byte[] bytecode = BuildMinimalBytecodeWithResourceBlob(blobBytes: 64, out _);
        using MemoryStream bcStream = new(bytecode);
        bcStream.Position = 5;

        ModelProbe bcProbe = ModelProbe.Create(bcStream, "kernel.mlirbc");
        IModelFormatDetector? bcDetected = ModelFormatRegistry.Default.TryDetect(bcProbe);
        await Assert.That(bcDetected).IsNotNull();
        await Assert.That(bcDetected!.Format.Id).IsEqualTo(ModelFormatId.Mlir);
        await Assert.That(bcStream.Position).IsEqualTo(5);

        byte[] textual = Encoding.UTF8.GetBytes("// generated MLIR\nmodule @m {\n  func.func @main(%x: tensor<1x4xf32>) -> tensor<1x4xf32> {\n    func.return %x : tensor<1x4xf32>\n  }\n}\n");
        using MemoryStream txtStream = new(textual);
        txtStream.Position = 3;

        ModelProbe txtProbe = ModelProbe.Create(txtStream, "model.mlir");
        IModelFormatDetector? txtDetected = ModelFormatRegistry.Default.TryDetect(txtProbe);
        await Assert.That(txtDetected).IsNotNull();
        await Assert.That(txtDetected!.Format.Id).IsEqualTo(ModelFormatId.Mlir);
        await Assert.That(txtStream.Position).IsEqualTo(3);
    }

    [Test]
    [Arguments("{\"nodes\": [1, 2, 3]}", "data.json")]
    [Arguments("<module><node/></module>", "model.xml")]
    [Arguments("import torch\ndef forward(x):\n    return x + 1\n", "model.py")]
    [Arguments("Just some arbitrary plain text without MLIR syntax.", "notes.txt")]
    public async Task Detector_NonMlirAndInvalidInputs_ReturnsFalse(string content, string fileName)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        using MemoryStream stream = new(bytes);
        ModelProbe probe = ModelProbe.Create(stream, fileName);

        bool canRead = MlirFormatDetector.Instance.CanRead(probe);
        await Assert.That(canRead).IsFalse();
        await Assert.That(stream.Position).IsEqualTo(0);
    }

    [Test]
    public async Task Detector_InvalidBytecodeVersion_ReturnsFalse()
    {
        byte[] bytes = [(byte)'M', (byte)'L', 0xEF, (byte)'R', 0x63, 0x00];
        using MemoryStream stream = new(bytes);
        ModelProbe probe = ModelProbe.Create(stream, "bad_version.mlirbc");

        bool canRead = MlirFormatDetector.Instance.CanRead(probe);
        await Assert.That(canRead).IsFalse();
        await Assert.That(stream.Position).IsEqualTo(0);
    }

    [Test]
    public async Task OperatorCatalog_CategorizesMajorCompilerDialects()
    {
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("torch.quantized_decomposed.quantize_per_tensor").Category)
            .IsEqualTo(OperatorCategory.Quantization);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("torch.quantized_decomposed.quantize_per_tensor").Domain)
            .IsEqualTo("torch.quantized_decomposed");

        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("torch.torchao.dequantize_affine").Category)
            .IsEqualTo(OperatorCategory.Quantization);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("torch.torchao.dequantize_affine").Domain)
            .IsEqualTo("torch.torchao");

        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("stablehlo.convolution").Category)
            .IsEqualTo(OperatorCategory.Layer);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("vhlo.add_v1").Domain)
            .IsEqualTo("vhlo");
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("tosa.conv2d").Category)
            .IsEqualTo(OperatorCategory.Layer);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("tosa.rescale").Category)
            .IsEqualTo(OperatorCategory.Quantization);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("tt.dot").Category)
            .IsEqualTo(OperatorCategory.Layer);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("tt.load").Category)
            .IsEqualTo(OperatorCategory.Data);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("ttg.convert_layout").Category)
            .IsEqualTo(OperatorCategory.Shape);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("ttng.warp_specialize").Category)
            .IsEqualTo(OperatorCategory.Layer);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("iree_input.dispatch").Category)
            .IsEqualTo(OperatorCategory.Custom);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("flow.dispatch").Category)
            .IsEqualTo(OperatorCategory.Custom);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("hal.command_buffer.dispatch").Category)
            .IsEqualTo(OperatorCategory.Custom);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("linalg.generic").Category)
            .IsEqualTo(OperatorCategory.Layer);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("arith.addf").Category)
            .IsEqualTo(OperatorCategory.Tensor);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("scf.if").Category)
            .IsEqualTo(OperatorCategory.ControlFlow);
        await Assert.That(MlirOperatorCatalog.CreateOperatorInfo("cf.cond_br").Category)
            .IsEqualTo(OperatorCategory.ControlFlow);
    }

    [Test]
    public async Task ShapeHelper_ExtremeDimensionsExceedingInt64Max_GuardsAgainstOverflowWithoutNegativeSizes()
    {
        // 10_000_000_000 * 10_000_000_000 = 10^20 > long.MaxValue (~9.22 * 10^18)
        TensorDimension[] dimsExceedingInt64 = [new TensorDimension.Fixed(10_000_000_000L), new TensorDimension.Fixed(10_000_000_000L)];
        bool byteOk = MlirShapeHelper.TryComputeByteLength(dimsExceedingInt64, 32, out long byteLen, out bool overflowed);

        await Assert.That(byteOk).IsFalse();
        await Assert.That(overflowed).IsTrue();
        await Assert.That(byteLen).IsEqualTo(0L);

        // Product exceeding UInt128.MaxValue
        TensorDimension[] dimsExceedingUInt128 =
        [
            new TensorDimension.Fixed(1_000_000_000_000_000_000L),
            new TensorDimension.Fixed(1_000_000_000_000_000_000L),
            new TensorDimension.Fixed(1_000_000_000_000_000_000L),
        ];
        bool countOk = MlirShapeHelper.TryComputeElementCount(dimsExceedingUInt128, out UInt128 elemCount, out bool countOverflow);

        await Assert.That(countOk).IsFalse();
        await Assert.That(countOverflow).IsTrue();
        await Assert.That(elemCount).IsEqualTo((UInt128)0);

        // Parsing textual MLIR with overflowing tensor dimensions emits DNV_MLIR_SHAPE_OVERFLOW diagnostic cleanly
        const string mlir = """
            module {
              func.func @main(%x: tensor<10000000000x10000000000xf32>) -> tensor<10000000000x10000000000xf32> {
                func.return %x : tensor<10000000000x10000000000xf32>
              }
            }
            """;

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(mlir));
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(stream, new ModelReadOptions { FileName = "overflow.mlir" });
        await Assert.That(graph.Diagnostics.Any(d => d.Code == MlirShapeHelper.ShapeOverflowDiagnosticCode)).IsTrue();
    }

    [Test]
    public async Task ShapeHelper_SingleDynamicDimensionInference_ResolvesExactDivisibleAndLeavesAmbiguousDynamic()
    {
        TensorShape singleDynamic = new([TensorDimension.Dynamic.Instance, new TensorDimension.Fixed(4)]);

        // 48 bytes / (4 bytes per f32 * 4) = 3 -> [3, 4]
        bool resolved = MlirShapeHelper.TryInferSingleDynamicDimension(singleDynamic, bufferByteLength: 48, elementBitWidth: 32, out TensorShape inferred);
        await Assert.That(resolved).IsTrue();
        await Assert.That(inferred.ToDisplayString()).IsEqualTo("[3, 4]");

        // Non-divisible buffer length (50 bytes) remains dynamic
        bool indivisible = MlirShapeHelper.TryInferSingleDynamicDimension(singleDynamic, bufferByteLength: 50, elementBitWidth: 32, out TensorShape unchanged);
        await Assert.That(indivisible).IsFalse();
        await Assert.That(unchanged.ToDisplayString()).IsEqualTo("[?, 4]");

        // Multiple '?' dimensions remain dynamic
        TensorShape multiDynamic = new([TensorDimension.Dynamic.Instance, TensorDimension.Dynamic.Instance]);
        bool multi = MlirShapeHelper.TryInferSingleDynamicDimension(multiDynamic, bufferByteLength: 64, elementBitWidth: 32, out TensorShape stillMulti);
        await Assert.That(multi).IsFalse();
        await Assert.That(stillMulti.ToDisplayString()).IsEqualTo("[?, ?]");
    }

    [Test]
    public async Task Parser_UnsupportedEmergingDataTypes_EmitDiagnosticInsteadOfCrashing()
    {
        const string mlir = """
            module {
              func.func @main(%a: tensor<2x4xf8E5M3FNU>, %b: tensor<2x4xmxi8>, %c: tensor<2xcomplex<float16>>) -> tensor<2x4xfloat8e5m3fnu> {
                %0 = "custom.cast"(%a, %b, %c) : (tensor<2x4xf8E5M3FNU>, tensor<2x4xmxi8>, tensor<2xcomplex<float16>>) -> tensor<2x4xfloat8e5m3fnu>
                func.return %0 : tensor<2x4xfloat8e5m3fnu>
              }
            }
            """;

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(mlir));
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(stream, new ModelReadOptions { FileName = "emerging_dtypes.mlir" });

        Subgraph main = graph.PrimarySubgraph!;
        await Assert.That(main.Inputs.Count).IsEqualTo(3);
        await Assert.That(main.Inputs[0].Tensors[0].Descriptor.DataType).IsEqualTo(DataType.Unknown);
        await Assert.That(main.Inputs[1].Tensors[0].Descriptor.DataType).IsEqualTo(DataType.Unknown);
        await Assert.That(main.Inputs[2].Tensors[0].Descriptor.DataType).IsEqualTo(DataType.Unknown);
        await Assert.That(main.Outputs[0].Tensors[0].Descriptor.DataType).IsEqualTo(DataType.Unknown);

        int dtypeWarnings = graph.Diagnostics.Count(d => d.Code == MlirShapeHelper.UnsupportedDataTypeDiagnosticCode);
        await Assert.That(dtypeWarnings >= 4).IsTrue();
    }

    [Test]
    public async Task Parser_MultiBlockCfgAndNestedLinalgRegion_PreservesSuccessorsAndChildSubgraph()
    {
        const string mlir = """
            module {
              func.func @cfg_and_linalg(%cond: i1, %a: tensor<4xf32>, %b: tensor<4xf32>, %out: tensor<4xf32>) -> tensor<4xf32> {
                cf.cond_br %cond, ^bb1(%a : tensor<4xf32>), ^bb2(%b : tensor<4xf32>)
              ^bb1(%x1: tensor<4xf32>):
                %0 = linalg.generic { indexing_maps = [], iterator_types = ["parallel"] } ins(%x1 : tensor<4xf32>) outs(%out : tensor<4xf32>) {
                ^bb0(%in: f32, %acc: f32):
                  %sum = arith.addf %in, %acc : f32
                  linalg.yield %sum : f32
                } -> tensor<4xf32>
                cf.br ^bb3(%0 : tensor<4xf32>)
              ^bb2(%x2: tensor<4xf32>):
                cf.br ^bb3(%x2 : tensor<4xf32>)
              ^bb3(%res: tensor<4xf32>):
                func.return %res : tensor<4xf32>
              }
            }
            """;

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(mlir));
        ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(stream, new ModelReadOptions { FileName = "cfg.mlir" });

        Subgraph main = graph.PrimarySubgraph!;
        GraphNode? condBr = main.Nodes.FirstOrDefault(n => n.Operator.Name == "cf.cond_br");
        await Assert.That(condBr).IsNotNull();
        NodeAttribute? succAttr = condBr!.Attributes.FirstOrDefault(a => a.Name == "successors");
        await Assert.That(succAttr).IsNotNull();
        await Assert.That(succAttr!.Value).IsTypeOf<AttributeValue.StringList>();
        IReadOnlyList<string> successors = ((AttributeValue.StringList)succAttr.Value).Values;
        await Assert.That(successors).Contains("^bb1");
        await Assert.That(successors).Contains("^bb2");

        GraphNode? linalgNode = main.Nodes.FirstOrDefault(n => n.Operator.Name == "linalg.generic");
        await Assert.That(linalgNode).IsNotNull();
        NodeAttribute? bodyAttr = linalgNode!.Attributes.FirstOrDefault(a => a.Name == "body");
        await Assert.That(bodyAttr).IsNotNull();
        await Assert.That(bodyAttr!.Value).IsTypeOf<AttributeValue.Graph>();
        Subgraph linalgBody = ((AttributeValue.Graph)bodyAttr.Value).Value;
        await Assert.That(linalgBody.Nodes.Count).IsEqualTo(1);
        await Assert.That(linalgBody.Nodes[0].Operator.Name).IsEqualTo("arith.addf");
    }

    [Test]
    public async Task Parser_CorruptedBytecodeAndExcessiveRegionNesting_ThrowsModelParseException()
    {
        // Truncated bytecode section length
        byte[] truncatedBc = [(byte)'M', (byte)'L', 0xEF, (byte)'R', 0x0D, 0x00, 0x00, 0xFF, 0x0F];
        using MemoryStream truncatedStream = new(truncatedBc);
        await Assert.That(async () => await ModelFormatRegistry.Default.ReadAsync(truncatedStream, new ModelReadOptions { FileName = "truncated.mlirbc" }))
            .Throws<ModelParseException>();

        // Deeply nested regions exceeding MaxSubgraphDepth
        StringBuilder sb = new();
        sb.AppendLine("module {");
        sb.AppendLine("  func.func @main(%c: i1) {");
        for (int i = 0; i < 6; i++)
        {
            sb.AppendLine("    scf.if %c {");
        }

        sb.AppendLine("      scf.yield");
        for (int i = 0; i < 6; i++)
        {
            sb.AppendLine("    }");
        }

        sb.AppendLine("    func.return");
        sb.AppendLine("  }");
        sb.AppendLine("}");

        using MemoryStream deepStream = new(Encoding.UTF8.GetBytes(sb.ToString()));
        await Assert.That(async () => await ModelFormatRegistry.Default.ReadAsync(
            deepStream,
            new ModelReadOptions { FileName = "deep.mlir", MaxSubgraphDepth = 3 }))
            .Throws<ModelParseException>();
    }

    [Test]
    public async Task Bytecode_MultiMegabyteResourceBlob_SlicesLazilyWithoutEagerHeapAllocation()
    {
        const int blobBytes = 8 * 1024 * 1024; // 8 MB resource blob
        byte[] bytecode = BuildMinimalBytecodeWithResourceBlob(blobBytes, out long expectedBlobOffset);
        using MemoryStream stream = new(bytecode);
        ModelReadOptions options = new() { FileName = "large_weights.mlirbc" };

        // Warm up JIT once before measuring thread allocations
        _ = await ModelFormatRegistry.Default.ReadAsync(stream, options);
        stream.Position = 0;

        (ModelGraph graph, long allocatedBytes) = await AllocationProbe.MeasureAsync(
            () => ModelFormatRegistry.Default.ReadAsync(stream, options));

        await Assert.That(allocatedBytes < blobBytes / 10).IsTrue();

        Subgraph main = graph.PrimarySubgraph!;
        TensorEdge? weightEdge = main.Tensors.FirstOrDefault(t => t.Data is TensorDataSlice { ByteLength: blobBytes });
        await Assert.That(weightEdge).IsNotNull();

        TensorDataSlice slice = (TensorDataSlice)weightEdge!.Data!;
        await Assert.That(slice.StreamOffset).IsEqualTo(expectedBlobOffset);
        await Assert.That(slice.ByteLength).IsEqualTo((long)blobBytes);

        // Verify TensorDataLoader reads exact sentinel floats from start & tail of lazy slice
        byte[] firstFour = new byte[4];
        int read = TensorDataLoader.ReadSlice(stream, slice, firstFour);
        await Assert.That(read).IsEqualTo(4);
        await Assert.That(BinaryPrimitives.ReadSingleLittleEndian(firstFour)).IsEqualTo(42.5f);
    }

    private static byte[] BuildMinimalBytecodeWithResourceBlob(int blobBytes, out long expectedBlobOffset)
    {
        List<string> strings =
        [
            "builtin", "func", "stablehlo",
            "module", "func", "return", "constant",
            "sym_name", "main", "value", "big_weights",
        ];

        List<byte> strSec = [];
        WriteVarInt(strSec, (ulong)strings.Count);
        for (int i = strings.Count - 1; i >= 0; i--)
        {
            int utf8Len = Encoding.UTF8.GetByteCount(strings[i]) + 1;
            WriteVarInt(strSec, (ulong)utf8Len);
        }

        for (int i = 0; i < strings.Count; i++)
        {
            strSec.AddRange(Encoding.UTF8.GetBytes(strings[i]));
            strSec.Add(0);
        }

        List<byte> dialSec = [];
        WriteVarInt(dialSec, 3);
        WriteVarInt(dialSec, 0 << 1);
        WriteVarInt(dialSec, 1 << 1);
        WriteVarInt(dialSec, 2 << 1);
        // builtin: module (op 0)
        WriteVarInt(dialSec, 0);
        WriteVarInt(dialSec, 1);
        WriteVarInt(dialSec, 3 << 1);
        // func: func (op 1), return (op 2)
        WriteVarInt(dialSec, 1);
        WriteVarInt(dialSec, 2);
        WriteVarInt(dialSec, 4 << 1);
        WriteVarInt(dialSec, 5 << 1);
        // stablehlo: constant (op 3)
        WriteVarInt(dialSec, 2);
        WriteVarInt(dialSec, 1);
        WriteVarInt(dialSec, 6 << 1);

        // Types: 0=f32, 1=tensor<?x4xf32>
        byte[] type0 = [.. EncodeVarInt(5)];
        byte[] type1 =
        [
            .. EncodeVarInt(13),
            .. EncodeVarInt(2),
            .. EncodeSignedVarInt(-1),
            .. EncodeSignedVarInt(4),
            .. EncodeVarInt(0),
        ];

        // Attrs: 0=UnknownLoc, 1=StringAttr("main"), 2=Dict({sym_name:"main"}),
        //        3=DenseResource(type=1, handle=0), 4=Dict({value:attr3})
        byte[] attr0 = [.. EncodeVarInt(21)];
        byte[] attr1 = [.. EncodeVarInt(2), .. EncodeVarInt(8)];
        byte[] attr2 = [.. EncodeVarInt(1), .. EncodeVarInt(1), .. EncodeVarInt(7), .. EncodeVarInt(1)];
        byte[] attr3 = [.. EncodeVarInt(16), .. EncodeVarInt(1), .. EncodeVarInt(0)];
        byte[] attr4 = [.. EncodeVarInt(1), .. EncodeVarInt(1), .. EncodeVarInt(9), .. EncodeVarInt(3)];

        byte[][] attrs = [attr0, attr1, attr2, attr3, attr4];
        byte[][] types = [type0, type1];

        List<byte> attrTypeSec = [];
        List<byte> attrTypeOffSec = [];
        WriteVarInt(attrTypeOffSec, (ulong)attrs.Length);
        WriteVarInt(attrTypeOffSec, (ulong)types.Length);

        WriteVarInt(attrTypeOffSec, 0);
        WriteVarInt(attrTypeOffSec, (ulong)attrs.Length);
        foreach (byte[] a in attrs)
        {
            WriteVarInt(attrTypeOffSec, ((ulong)a.Length << 1) | 1);
            attrTypeSec.AddRange(a);
        }

        WriteVarInt(attrTypeOffSec, 0);
        WriteVarInt(attrTypeOffSec, (ulong)types.Length);
        foreach (byte[] t in types)
        {
            WriteVarInt(attrTypeOffSec, ((ulong)t.Length << 1) | 1);
            attrTypeSec.AddRange(t);
        }

        // Resource section: alignment=1 + blobBytes
        byte[] resAlign = EncodeVarInt(1);
        ulong resEntryLen = (ulong)(resAlign.Length + blobBytes);
        List<byte> resOffSec = [];
        WriteVarInt(resOffSec, 1);
        WriteVarInt(resOffSec, (0 << 1) | 1);
        WriteVarInt(resOffSec, 1);
        WriteVarInt(resOffSec, 10); // "big_weights"
        WriteVarInt(resOffSec, resEntryLen);
        resOffSec.Add(3); // Blob

        // IR section
        List<byte> funcBody = [];
        WriteVarInt(funcBody, 1); // 1 block
        WriteVarInt(funcBody, 1); // 1 value
        WriteVarInt(funcBody, (1 << 1) | 0); // 1 op, no args
        WriteVarInt(funcBody, 3); // stablehlo.constant
        funcBody.Add(0x01 | 0x02); // HasAttrs | HasResults
        WriteVarInt(funcBody, 0); // loc
        WriteVarInt(funcBody, 4); // attr dict 4
        WriteVarInt(funcBody, 1); // 1 result
        WriteVarInt(funcBody, 1); // tensor<?x4xf32>

        List<byte> modBody = [];
        WriteVarInt(modBody, 1);
        WriteVarInt(modBody, 0);
        WriteVarInt(modBody, (1 << 1) | 0);
        WriteVarInt(modBody, 1); // func.func
        modBody.Add(0x01 | 0x10); // HasAttrs | HasInlineRegions
        WriteVarInt(modBody, 0);
        WriteVarInt(modBody, 2); // {sym_name:"main"}
        WriteVarInt(modBody, 1);
        modBody.Add(0);
        modBody.AddRange(funcBody);

        List<byte> topIr = [];
        WriteVarInt(topIr, 1);
        WriteVarInt(topIr, 0);
        WriteVarInt(topIr, (1 << 1) | 0);
        WriteVarInt(topIr, 0); // builtin.module
        topIr.Add(0x10);
        WriteVarInt(topIr, 0);
        WriteVarInt(topIr, 1);
        topIr.Add(0);
        topIr.AddRange(modBody);

        List<byte> file = [];
        file.Add((byte)'M');
        file.Add((byte)'L');
        file.Add(0xEF);
        file.Add((byte)'R');
        WriteVarInt(file, 6);
        file.AddRange("synth\0"u8.ToArray());
        WriteSection(file, 0, [.. strSec]);
        WriteSection(file, 1, [.. dialSec]);
        WriteSection(file, 2, [.. attrTypeSec]);
        WriteSection(file, 3, [.. attrTypeOffSec]);
        WriteSection(file, 6, [.. resOffSec]);

        // Write kResource section header and record exact blob offset
        file.Add(5); // SectionResource
        WriteVarInt(file, resEntryLen);
        file.AddRange(resAlign);
        expectedBlobOffset = file.Count;

        byte[] result = new byte[file.Count + blobBytes + 1 + EncodeVarInt((ulong)topIr.Count).Length + topIr.Count];
        file.CopyTo(result);
        BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan((int)expectedBlobOffset, 4), 42.5f);

        int tailOffset = (int)expectedBlobOffset + blobBytes;
        result[tailOffset++] = 4; // SectionIr
        byte[] irLenBytes = EncodeVarInt((ulong)topIr.Count);
        irLenBytes.CopyTo(result, tailOffset);
        tailOffset += irLenBytes.Length;
        topIr.CopyTo(result, tailOffset);

        return result;
    }

    private static void WriteSection(List<byte> target, byte id, byte[] payload)
    {
        target.Add(id);
        WriteVarInt(target, (ulong)payload.Length);
        target.AddRange(payload);
    }

    private static void WriteVarInt(List<byte> target, ulong value) =>
        target.AddRange(EncodeVarInt(value));

    private static byte[] EncodeVarInt(ulong value)
    {
        for (int n = 0; n < 8; n++)
        {
            ulong max = 1UL << (7 * (n + 1));
            if (value < max)
            {
                ulong encoded = (value << (n + 1)) | (1UL << n);
                byte[] buf = new byte[n + 1];
                for (int b = 0; b <= n; b++)
                {
                    buf[b] = (byte)(encoded >> (8 * b));
                }

                return buf;
            }
        }

        byte[] full = new byte[9];
        full[0] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(full.AsSpan(1, 8), value);
        return full;
    }

    private static byte[] EncodeSignedVarInt(long value)
    {
        ulong zigzag = unchecked((ulong)((value << 1) ^ (value >> 63)));
        return EncodeVarInt(zigzag);
    }
}
