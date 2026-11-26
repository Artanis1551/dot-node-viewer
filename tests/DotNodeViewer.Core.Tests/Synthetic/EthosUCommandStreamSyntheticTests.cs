using System.Buffers.Binary;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Delegates.EthosU;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Tests.Synthetic;

/// <summary>
/// Synthetic unit tests for Ethos-U command stream decoding, register state tracking, COP1 parsing, and edge inference.
/// </summary>
public sealed class EthosUCommandStreamSyntheticTests
{
    [Test]
    public async Task Decode_Cmd0AndCmd1Words_DecodesCorrectOpcodesAndPayloads()
    {
        // Construct stream:
        // 1. cmd0: NPU_SET_IFM_REGION (0x010F), param = 1
        // 2. cmd1: NPU_SET_IFM_BASE0 (0x0000 | 0x4000 = 0x4000), param = 0, payload = 0x100
        // 3. cmd0: NPU_OP_STOP (0x0000), param = 0xFFFF
        byte[] stream = new byte[]
        {
            // word 0 (cmd0): code = 0x010F, param = 0x0001
            0x0F, 0x01, 0x01, 0x00,
            // word 1 (cmd1 header): code = 0x4000, param = 0x0000
            0x00, 0x40, 0x00, 0x00,
            // word 2 (cmd1 payload): 0x00000100
            0x00, 0x01, 0x00, 0x00,
            // word 3 (cmd0): code = 0x0000, param = 0xFFFF
            0x00, 0x00, 0xFF, 0xFF,
        };

        var commands = EthosUCommandStreamDecoder.DecodeToList(stream);

        await Assert.That(commands.Count).IsEqualTo(3);
        await Assert.That(commands[0].Opcode).IsEqualTo(EthosUOpcodeCatalog.NpuSetIfmRegion);
        await Assert.That(commands[0].Param).IsEqualTo(1);
        await Assert.That(commands[0].IsCmd1).IsFalse();

        await Assert.That(commands[1].Opcode).IsEqualTo(EthosUOpcodeCatalog.NpuSetIfmBase0);
        await Assert.That(commands[1].IsCmd1).IsTrue();
        await Assert.That(commands[1].Payload).IsEqualTo(0x100u);

        await Assert.That(commands[2].Opcode).IsEqualTo(EthosUOpcodeCatalog.NpuOpStop);
    }

    [Test]
    public async Task IsExecutableOperation_Cmd1CollidingOpcodes_ReturnsFalse()
    {
        int[] collidingCmd1Opcodes =
        [
            EthosUOpcodeCatalog.NpuSetIfmBase2,   // 0x0002 (collides with NPU_OP_CONV)
            EthosUOpcodeCatalog.NpuSetIfmBase3,   // 0x0003 (collides with NPU_OP_DEPTHWISE)
            EthosUOpcodeCatalog.NpuSetIfmStrideY, // 0x0005 (collides with NPU_OP_POOL)
            EthosUOpcodeCatalog.NpuSetIfmStrideC, // 0x0006 (collides with NPU_OP_ELEMENTWISE)
            EthosUOpcodeCatalog.NpuSetOfmBase0,   // 0x0010 (collides with NPU_OP_DMA_START)
        ];

        foreach (int opcode in collidingCmd1Opcodes)
        {
            var cmd1 = new EthosUCommand(0, 0, opcode, 0, 0x100, IsCmd1: true, "CMD1");
            await Assert.That(EthosUOpcodeCatalog.IsExecutableOperation(in cmd1)).IsFalse();

            var cmd0 = new EthosUCommand(0, 0, opcode, 0, null, IsCmd1: false, "CMD0");
            await Assert.That(EthosUOpcodeCatalog.IsExecutableOperation(in cmd0)).IsTrue();
        }
    }

    [Test]
    public async Task Decode_TruncatedCmd1_EmitsDiagnosticAndHaltsSafely()
    {
        // cmd1 header without the required 4-byte payload word
        byte[] truncatedStream = new byte[]
        {
            0x00, 0x40, 0x00, 0x00, // NPU_SET_IFM_BASE0 cmd1 header
        };

        var diagnostics = new List<string>();
        var commands = EthosUCommandStreamDecoder.DecodeToList(truncatedStream, diagnostics);

        await Assert.That(commands.Count).IsEqualTo(0);
        await Assert.That(diagnostics.Count).IsEqualTo(1);
        await Assert.That(diagnostics[0]).Contains("Truncated cmd1 payload");
    }

    [Test]
    public async Task Decode_UnknownOpcode_EmitsDiagnosticWithoutThrowing()
    {
        byte[] stream = new byte[]
        {
            // Unknown cmd0 mode (e.g. 0x8000 mode)
            0x12, 0x80, 0x34, 0x12,
            // STOP
            0x00, 0x00, 0xFF, 0xFF,
        };

        var diagnostics = new List<string>();
        var commands = EthosUCommandStreamDecoder.DecodeToList(stream, diagnostics);

        await Assert.That(commands.Count).IsEqualTo(2);
        await Assert.That(diagnostics.Count).IsEqualTo(1);
        await Assert.That(commands[0].Name).Contains("UNKNOWN");
    }

    [Test]
    public async Task DriverPayloadReader_ValidCop1Header_ParsesArchitectureAndCommandBounds()
    {
        byte[] payload = BuildSyntheticCop1Payload(
            macsPerCcLog2: 7, // 1 << 7 = 128
            product: 0,      // U55
            shramSizeKb: 32,
            cmdStreamWords: 16);

        EthosUDriverPayload parsed = EthosUDriverPayloadReader.Read(payload);

        await Assert.That(parsed.Architecture).IsEqualTo("Ethos-U55-128");
        await Assert.That(parsed.MacsPerCycle).IsEqualTo(128);
        await Assert.That(parsed.Product).IsEqualTo(0);
        await Assert.That(parsed.ShramSizeKb).IsEqualTo(32);
        await Assert.That(parsed.CommandWordCount).IsEqualTo(16);
        await Assert.That(parsed.CommandStreamByteLength).IsEqualTo(64);
    }

    [Test]
    public async Task DriverPayloadReader_InvalidMagic_ThrowsModelParseException()
    {
        byte[] payload = new byte[32]; // Zeroed, no "COP1" magic

        var act = () => EthosUDriverPayloadReader.Read(payload);

        await Assert.That(act).Throws<ModelParseException>();
    }

    [Test]
    public async Task DriverPayloadReader_WordLimitExceeded_ThrowsModelParseException()
    {
        // 5 million words exceeds 4 million word limit
        byte[] payload = BuildSyntheticCop1Payload(
            macsPerCcLog2: 7,
            product: 0,
            shramSizeKb: 32,
            cmdStreamWords: 5_000_000);

        var act = () => EthosUDriverPayloadReader.Read(payload);

        await Assert.That(act).Throws<ModelParseException>();
    }

    [Test]
    public async Task RegisterState_CarriesStateAcrossSuccessiveOperations()
    {
        var state = new EthosURegisterState();

        // 1. Set IFM Region = 1
        state.Apply(new EthosUCommand(0, 0, EthosUOpcodeCatalog.NpuSetIfmRegion, 1, null, false, "NPU_SET_IFM_REGION"));
        // 2. Set IFM Base0 = 0x200
        state.Apply(new EthosUCommand(1, 4, EthosUOpcodeCatalog.NpuSetIfmBase0, 0, 0x200, true, "NPU_SET_IFM_BASE0"));
        // 3. Set IFM Width = 8 (Width0M1 = 7)
        state.Apply(new EthosUCommand(3, 12, EthosUOpcodeCatalog.NpuSetIfmWidth0M1, 7, null, false, "NPU_SET_IFM_WIDTH0_M1"));

        // Trigger OP 1: CONV
        EthosURegisterSnapshot snap1 = state.CreateSnapshot(EthosUOpcodeCatalog.NpuOpConv, 0);

        await Assert.That(snap1.IfmRegion).IsEqualTo(1);
        await Assert.That(snap1.IfmBase0).IsEqualTo(0x200u);
        await Assert.That(snap1.IfmWidth).IsEqualTo(8);

        // Now without resetting IFM region, change only Base0 to 0x400
        state.Apply(new EthosUCommand(4, 16, EthosUOpcodeCatalog.NpuSetIfmBase0, 0, 0x400, true, "NPU_SET_IFM_BASE0"));

        // Trigger OP 2: DEPTHWISE
        EthosURegisterSnapshot snap2 = state.CreateSnapshot(EthosUOpcodeCatalog.NpuOpDepthwise, 0);

        // Region & width should be preserved from earlier!
        await Assert.That(snap2.IfmRegion).IsEqualTo(1);
        await Assert.That(snap2.IfmWidth).IsEqualTo(8);
        await Assert.That(snap2.IfmBase0).IsEqualTo(0x400u);
    }

    [Test]
    public async Task PrecisionMapping_DistinguishesIfmAndOfmBitfields()
    {
        // IFM: bit 0 = signed, bits [3:2] = size (0=8b, 1=16b, 2=32b), bit 6 = NHCWB16
        await Assert.That(EthosUSubgraphBuilder.MapIfmPrecision(0x01)).IsEqualTo(DataType.Int8);
        await Assert.That(EthosUSubgraphBuilder.MapIfmPrecision(0x00)).IsEqualTo(DataType.UInt8);
        await Assert.That(EthosUSubgraphBuilder.MapIfmPrecision(0x41)).IsEqualTo(DataType.Int8);
        await Assert.That(EthosUSubgraphBuilder.MapIfmPrecision(0x05)).IsEqualTo(DataType.Int16);
        await Assert.That(EthosUSubgraphBuilder.MapIfmPrecision(0x49)).IsEqualTo(DataType.Int32);

        // OFM: bit 0 = signed, bits [2:1] = size (0=8b, 1=16b, 2=32b), bit 6 = NHCWB16, bit 8 = global scale
        await Assert.That(EthosUSubgraphBuilder.MapOfmPrecision(0x01)).IsEqualTo(DataType.Int8);
        await Assert.That(EthosUSubgraphBuilder.MapOfmPrecision(0x0101)).IsEqualTo(DataType.Int8);
        await Assert.That(EthosUSubgraphBuilder.MapOfmPrecision(0x03)).IsEqualTo(DataType.Int16);
        await Assert.That(EthosUSubgraphBuilder.MapOfmPrecision(0x0145)).IsEqualTo(DataType.Int32);
    }

    [Test]
    public async Task SubgraphBuilder_LastWriterWins_InfersEdgesWithArenaReuse()
    {
        // Op 0: OFM writes to Region 1 at [0x000, 0x100)
        // Op 1: OFM writes to Region 1 at [0x000, 0x100) (overwrites Op 0 in the arena!)
        // Op 2: IFM reads from Region 1 at [0x000, 0x100)
        // Expected: Op 2's IFM connects to Op 1 (last writer wins), NOT Op 0!

        var snap0 = CreateDummySnapshot(EthosUOpcodeCatalog.NpuOpConv, ofmRegion: 1, ofmBase: 0x000, ofmExtent: 0x100);
        var snap1 = CreateDummySnapshot(EthosUOpcodeCatalog.NpuOpConv, ofmRegion: 1, ofmBase: 0x000, ofmExtent: 0x100);
        var snap2 = CreateDummySnapshot(EthosUOpcodeCatalog.NpuOpConv, ifmRegion: 1, ifmBase: 0x000, ifmExtent: 0x100);

        var driver = new EthosUDriverPayload("Ethos-U55-128", 0, 128, 32, 0, 0, 100, 25);

        Subgraph subgraph = EthosUSubgraphBuilder.Build(
            "test_subgraph",
            driver,
            new[] { snap0, snap1, snap2 },
            weightSliceBaseOffset: 0,
            weightSliceByteLength: 0,
            outerInputs: Array.Empty<GraphPort>(),
            outerOutputs: Array.Empty<GraphPort>());

        await Assert.That(subgraph.Nodes.Count).IsEqualTo(3);

        // Check that Op 2's input is connected to Op 1's output (last writer wins), NOT Op 0
        TensorEdge op2InputTensor = subgraph.Nodes[2].Inputs[0].Tensors[0];
        TensorEdge op1OutputTensor = subgraph.Nodes[1].Outputs[0].Tensors[0];
        TensorEdge op0OutputTensor = subgraph.Nodes[0].Outputs[0].Tensors[0];

        await Assert.That(op2InputTensor.Name).IsEqualTo(op1OutputTensor.Name);
        await Assert.That(op2InputTensor.Name).IsNotEqualTo(op0OutputTensor.Name);
    }

    [Test]
    public async Task SubgraphBuilder_MultiSliceWritersAndStridedReader_ConnectsAllSlices()
    {
        // Op 0 writes slice [0x00, 0x40), Op 1 writes slice [0x40, 0x80)
        // Op 2 reads full contiguous [0x00, 0x80) -> must connect to BOTH Op 0 and Op 1!
        var snap0 = CreateDummySnapshot(EthosUOpcodeCatalog.NpuOpPool, ofmRegion: 1, ofmBase: 0x00, ofmExtent: 0x40);
        var snap1 = CreateDummySnapshot(EthosUOpcodeCatalog.NpuOpPool, ofmRegion: 1, ofmBase: 0x40, ofmExtent: 0x40);
        var snap2 = CreateDummySnapshot(EthosUOpcodeCatalog.NpuOpConv, ifmRegion: 1, ifmBase: 0x00, ifmExtent: 0x80, ofmBase: 0x100, ofmExtent: 0x40);

        var driver = new EthosUDriverPayload("Ethos-U55-128", 0, 128, 32, 0, 0, 100, 25);
        Subgraph subgraph = EthosUSubgraphBuilder.Build(
            "slice_test",
            driver,
            new[] { snap0, snap1, snap2 },
            weightSliceBaseOffset: 0,
            weightSliceByteLength: 0,
            outerInputs: Array.Empty<GraphPort>(),
            outerOutputs: Array.Empty<GraphPort>());

        NodePort op2Ifm = subgraph.Nodes[2].Inputs.First(p => p.Name == "ifm");
        await Assert.That(op2Ifm.Tensors.Count).IsEqualTo(2);
        await Assert.That(op2Ifm.Tensors[0].Name).IsEqualTo(subgraph.Nodes[0].Outputs[0].Tensors[0].Name);
        await Assert.That(op2Ifm.Tensors[1].Name).IsEqualTo(subgraph.Nodes[1].Outputs[0].Tensors[0].Name);
    }

    [Test]
    public async Task SubgraphBuilder_UnaryAndScalarElementwise_DoNotAttachIfm2Port_AndPoolDoesNotAttachWeights()
    {
        // Op 0: CONV with weights/scales
        // Op 1: POOL (stale weight/scale registers must NOT attach weights/scales ports)
        // Op 2: NPU_CLZ (param=7, unary elementwise -> must NOT attach ifm2 port)
        // Op 3: NPU_MUL (param=0) with scalar broadcast (Ifm2Broadcast=0x80 -> must NOT attach ifm2 port)
        var snap0 = CreateDummySnapshot(EthosUOpcodeCatalog.NpuOpConv, ofmRegion: 1, ofmBase: 0x100, ofmExtent: 0x40, weightLength: 32, scaleLength: 16);
        var snap1 = CreateDummySnapshot(EthosUOpcodeCatalog.NpuOpPool, ifmRegion: 1, ifmBase: 0x100, ifmExtent: 0x40, ofmRegion: 1, ofmBase: 0x200, ofmExtent: 0x40, weightLength: 32, scaleLength: 16);
        var snap2 = CreateDummySnapshot(EthosUOpcodeCatalog.NpuOpElementwise, param: 7, ifmRegion: 1, ifmBase: 0x200, ifmExtent: 0x40, ofmRegion: 1, ofmBase: 0x300, ofmExtent: 0x40);
        var snap3 = CreateDummySnapshot(EthosUOpcodeCatalog.NpuOpElementwise, param: 0, ifmRegion: 1, ifmBase: 0x300, ifmExtent: 0x40, ofmRegion: 1, ofmBase: 0x400, ofmExtent: 0x40, ifm2Broadcast: 0x80, ifm2Scalar: 42);

        var driver = new EthosUDriverPayload("Ethos-U55-128", 0, 128, 32, 0, 0, 100, 25);
        Subgraph subgraph = EthosUSubgraphBuilder.Build(
            "ports_test",
            driver,
            new[] { snap0, snap1, snap2, snap3 },
            weightSliceBaseOffset: 1000,
            weightSliceByteLength: 500,
            outerInputs: Array.Empty<GraphPort>(),
            outerOutputs: Array.Empty<GraphPort>());

        await Assert.That(subgraph.Nodes[0].Inputs.Any(p => p.Name == "weights")).IsTrue();
        await Assert.That(subgraph.Nodes[0].Inputs.Any(p => p.Name == "scales")).IsTrue();

        await Assert.That(subgraph.Nodes[1].Inputs.Any(p => p.Name == "weights")).IsFalse();
        await Assert.That(subgraph.Nodes[1].Inputs.Any(p => p.Name == "scales")).IsFalse();
        await Assert.That(subgraph.Nodes[1].Attributes.Any(a => a.Name == "kernel")).IsTrue();

        await Assert.That(subgraph.Nodes[2].Inputs.Any(p => p.Name == "ifm2")).IsFalse();
        await Assert.That(subgraph.Nodes[2].Attributes.Any(a => a.Name == "kernel")).IsFalse();

        await Assert.That(subgraph.Nodes[3].Inputs.Any(p => p.Name == "ifm2")).IsFalse();
        await Assert.That(subgraph.Nodes[3].Attributes.Any(a => a.Name == "ifm2_scalar")).IsTrue();
    }

    [Test]
    public async Task SubgraphBuilder_LutDmaAndFlashConstants_WireProperlyWithoutFakeGraphInputs()
    {
        // Op 0: DMA from flash (r0@0x100, len=1024) -> shram_lut (0x103@0x5800) for 24KB SHRAM (24*1024 - 2048 = 0x5800)
        // Op 1: Elementwise ADD reading flash constant (r0@0x500) + SRAM input (r1@0x0) with LUT[0] activation (0x3010)
        var dmaSnap = CreateDummySnapshot(
            EthosUOpcodeCatalog.NpuOpDmaStart,
            dmaSrcRegion: 0,
            dmaSrcAddr: 0x100,
            dmaDstRegion: EthosUOpcodeCatalog.BasePointerIndexMem2Mem,
            dmaDstAddr: 0x5800,
            dmaLen: 1024);

        var opSnap = CreateDummySnapshot(
            EthosUOpcodeCatalog.NpuOpElementwise,
            param: 1,
            ifmRegion: 0,
            ifmBase: 0x500,
            ifmExtent: 64,
            ifm2Region: 1,
            ifm2Base: 0x0,
            ifm2Extent: 64,
            ofmRegion: 1,
            ofmBase: 0x100,
            ofmExtent: 64,
            activation: 0x3010);

        var driver = new EthosUDriverPayload("Ethos-U55-128", 0, 128, 24, 0, 0, 100, 25);
        Subgraph subgraph = EthosUSubgraphBuilder.Build(
            "lut_flash_test",
            driver,
            new[] { dmaSnap, opSnap },
            weightSliceBaseOffset: 2048,
            weightSliceByteLength: 4096,
            outerInputs: Array.Empty<GraphPort>(),
            outerOutputs: Array.Empty<GraphPort>());

        // Flash reads must NOT become GraphInputs
        await Assert.That(subgraph.Inputs.Count).IsEqualTo(1);
        await Assert.That(subgraph.Inputs[0].Name).DoesNotContain("flash");
        await Assert.That(subgraph.Inputs[0].Name).DoesNotContain("r0");

        // DMA src must be Constant with lazy TensorDataSlice
        TensorEdge dmaSrc = subgraph.Nodes[0].Inputs.First(p => p.Name == "src").Tensors[0];
        await Assert.That(dmaSrc.Kind).IsEqualTo(TensorKind.Constant);
        await Assert.That(dmaSrc.Data).IsAssignableTo<TensorDataSlice>();

        // Op 1 must have a 'lut' port connected to DMA dst
        NodePort? lutPort = subgraph.Nodes[1].Inputs.FirstOrDefault(p => p.Name == "lut");
        await Assert.That(lutPort).IsNotNull();
        await Assert.That(lutPort!.Tensors[0].Name).IsEqualTo(subgraph.Nodes[0].Outputs[0].Tensors[0].Name);

        // Op 1 activation attribute must be "LUT[0]"
        NodeAttribute? actAttr = subgraph.Nodes[1].Attributes.FirstOrDefault(a => a.Name == "activation");
        await Assert.That(actAttr?.Value).IsEqualTo(new AttributeValue.String("LUT[0]"));
    }

    private static EthosURegisterSnapshot CreateDummySnapshot(
        int opcode,
        int param = 0,
        int ifmRegion = 1,
        uint ifmBase = 0,
        uint ifmExtent = 64,
        int ifm2Region = 1,
        uint ifm2Base = 0,
        uint ifm2Extent = 64,
        int ifm2Broadcast = 0,
        int ifm2Scalar = 0,
        int ofmRegion = 1,
        uint ofmBase = 0,
        uint ofmExtent = 64,
        uint weightLength = 0,
        uint scaleLength = 0,
        int activation = 0,
        int dmaSrcRegion = 0,
        uint dmaSrcAddr = 0,
        int dmaDstRegion = 0,
        uint dmaDstAddr = 0,
        uint dmaLen = 0)
    {
        return new EthosURegisterSnapshot(
            opcode,
            Param: param,
            ifmRegion,
            ifmBase,
            IfmWidth: 8,
            IfmHeight: 8,
            IfmDepth: Math.Max(1, (int)ifmExtent / 64),
            IfmStrideX: 0,
            IfmStrideY: 0,
            IfmStrideC: 0,
            IfmPrecision: 1, // Int8
            IfmZeroPoint: 0,
            PadTop: 0,
            PadLeft: 0,
            PadBottom: 0,
            PadRight: 0,
            Ifm2Region: ifm2Region,
            Ifm2Base0: ifm2Base,
            Ifm2Width: 8,
            Ifm2Height: Math.Max(1, (int)ifm2Extent / 64),
            Ifm2StrideX: 0,
            Ifm2StrideY: 0,
            Ifm2StrideC: 0,
            Ifm2Scalar: ifm2Scalar,
            Ifm2Precision: 1,
            Ifm2ZeroPoint: 0,
            Ifm2Broadcast: ifm2Broadcast,
            ofmRegion,
            ofmBase,
            OfmWidth: 8,
            OfmHeight: 8,
            OfmDepth: Math.Max(1, (int)ofmExtent / 64),
            OfmStrideX: 0,
            OfmStrideY: 0,
            OfmStrideC: 0,
            OfmPrecision: 1,
            OfmZeroPoint: 0,
            BlockWidth: 8,
            BlockHeight: 8,
            BlockDepth: 8,
            KernelWidth: 1,
            KernelHeight: 1,
            KernelStride: 0,
            KernelStrideX: 1,
            KernelStrideY: 1,
            DilationX: 1,
            DilationY: 1,
            WeightRegion: 0,
            WeightBase: 0,
            WeightLength: weightLength,
            ScaleRegion: 0,
            ScaleBase: 0,
            ScaleLength: scaleLength,
            Activation: activation,
            ActivationMin: -128,
            ActivationMax: 127,
            DmaSrcRegion: dmaSrcRegion,
            DmaSrcAddr: dmaSrcAddr,
            DmaDstRegion: dmaDstRegion,
            DmaDstAddr: dmaDstAddr,
            DmaLen: dmaLen,
            PrecedingCommands: Array.Empty<EthosUCommand>());
    }

    private static byte[] BuildSyntheticCop1Payload(int macsPerCcLog2, int product, int shramSizeKb, int cmdStreamWords)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // 1. "COP1" magic
        bw.Write(0x31504F43u);

        // 2. DACommands.Config tag (id = 1, reserved = 0, param = 1)
        bw.Write(0x00010001u);

        // 3. config_word: macs (bits 0..3), cmd_ver=0 (bits 4..7), shram (bits 8..15), product (bits 28..31)
        uint configWord = (uint)(macsPerCcLog2 & 0x0F) |
                          ((uint)(shramSizeKb & 0xFF) << 8) |
                          ((uint)(product & 0x0F) << 28);
        bw.Write(configWord);

        // 4. id_word
        bw.Write(0x10000000u);

        // 5. NOP tag to pad to 16 bytes (id = 5)
        bw.Write(0x00000005u);

        // 6. DACommands.CmdStream tag (id = 2, reserved = length >> 16, param = length & 0xFFFF)
        uint lenHigh = ((uint)cmdStreamWords >> 16) & 0xFF;
        uint lenLow = (uint)cmdStreamWords & 0xFFFF;
        uint cmdStreamTag = 0x02u | (lenHigh << 8) | (lenLow << 16);
        bw.Write(cmdStreamTag);

        // 7. Command stream words (STOP at end)
        for (int i = 0; i < cmdStreamWords; i++)
        {
            if (i == cmdStreamWords - 1)
            {
                bw.Write(0xFFFF0000u); // NPU_OP_STOP
            }
            else
            {
                bw.Write(0x0000010Fu); // NPU_SET_IFM_REGION
            }
        }

        return ms.ToArray();
    }
}
