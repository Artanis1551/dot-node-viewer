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

    private static EthosURegisterSnapshot CreateDummySnapshot(
        int opcode,
        int ifmRegion = 1,
        uint ifmBase = 0,
        uint ifmExtent = 64,
        int ofmRegion = 1,
        uint ofmBase = 0,
        uint ofmExtent = 64)
    {
        return new EthosURegisterSnapshot(
            opcode,
            Param: 0,
            ifmRegion,
            ifmBase,
            IfmWidth: 8,
            IfmHeight: 8,
            IfmDepth: (int)ifmExtent / 64,
            IfmStrideX: 1,
            IfmStrideY: 8,
            IfmStrideC: 1,
            IfmPrecision: 1, // Int8
            IfmZeroPoint: 0,
            PadTop: 0,
            PadLeft: 0,
            PadBottom: 0,
            PadRight: 0,
            Ifm2Region: 1,
            Ifm2Base0: 0,
            Ifm2Width: 1,
            Ifm2Height: 1,
            Ifm2Precision: 1,
            Ifm2ZeroPoint: 0,
            Ifm2Broadcast: 0,
            ofmRegion,
            ofmBase,
            OfmWidth: 8,
            OfmHeight: 8,
            OfmDepth: (int)ofmExtent / 64,
            OfmStrideX: 1,
            OfmStrideY: 8,
            OfmStrideC: 1,
            OfmPrecision: 1,
            OfmZeroPoint: 0,
            BlockWidth: 8,
            BlockHeight: 8,
            BlockDepth: 8,
            KernelWidth: 1,
            KernelHeight: 1,
            KernelStride: 1,
            WeightRegion: 0,
            WeightBase: 0,
            WeightLength: 0,
            ScaleRegion: 0,
            ScaleBase: 0,
            ScaleLength: 0,
            Activation: 0,
            ActivationMin: int.MinValue,
            ActivationMax: int.MaxValue,
            DmaSrcRegion: 0,
            DmaSrcAddr: 0,
            DmaDstRegion: 0,
            DmaDstAddr: 0,
            DmaLen: 0,
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
