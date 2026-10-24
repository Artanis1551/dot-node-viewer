using System.Globalization;

namespace DotNodeViewer.Core.Delegates.EthosU;

/// <summary>
/// Provides opcode lookups and classification for Arm Ethos-U55, U65, and U85 register commands.
/// </summary>
internal static class EthosUOpcodeCatalog
{
    // cmd0 Operation Opcodes
    public const int NpuOpStop = 0x0000;
    public const int NpuOpIrq = 0x0001;
    public const int NpuOpConv = 0x0002;
    public const int NpuOpDepthwise = 0x0003;
    public const int NpuOpPool = 0x0005;
    public const int NpuOpElementwise = 0x0006;
    public const int NpuOpDmaStart = 0x0010;
    public const int NpuOpDmaWait = 0x0011;
    public const int NpuOpKernelWait = 0x0012;
    public const int NpuOpPmuMask = 0x0013;

    // cmd0 Register Set Opcodes
    public const int NpuSetIfmPadTop = 0x0100;
    public const int NpuSetIfmPadLeft = 0x0101;
    public const int NpuSetIfmPadRight = 0x0102;
    public const int NpuSetIfmPadBottom = 0x0103;
    public const int NpuSetIfmDepthM1 = 0x0104;
    public const int NpuSetIfmPrecision = 0x0105;
    public const int NpuSetIfmUpscale = 0x0107;
    public const int NpuSetIfmZeroPoint = 0x0109;
    public const int NpuSetIfmWidth0M1 = 0x010A;
    public const int NpuSetIfmHeight0M1 = 0x010B;
    public const int NpuSetIfmHeight1M1 = 0x010C;
    public const int NpuSetIfmIbEnd = 0x010D;
    public const int NpuSetIfmRegion = 0x010F;
    public const int NpuSetOfmWidthM1 = 0x0111;
    public const int NpuSetOfmHeightM1 = 0x0112;
    public const int NpuSetOfmDepthM1 = 0x0113;
    public const int NpuSetOfmPrecision = 0x0114;
    public const int NpuSetOfmBlkWidthM1 = 0x0115;
    public const int NpuSetOfmBlkHeightM1 = 0x0116;
    public const int NpuSetOfmBlkDepthM1 = 0x0117;
    public const int NpuSetOfmZeroPoint = 0x0118;
    public const int NpuSetOfmWidth0M1 = 0x011A;
    public const int NpuSetOfmHeight0M1 = 0x011B;
    public const int NpuSetOfmHeight1M1 = 0x011C;
    public const int NpuSetOfmRegion = 0x011F;
    public const int NpuSetKernelWidthM1 = 0x0120;
    public const int NpuSetKernelHeightM1 = 0x0121;
    public const int NpuSetKernelStride = 0x0122;
    public const int NpuSetParallelMode = 0x0123;
    public const int NpuSetAccFormat = 0x0124;
    public const int NpuSetActivation = 0x0125;
    public const int NpuSetActivationMin = 0x0126;
    public const int NpuSetActivationMax = 0x0127;
    public const int NpuSetWeightRegion = 0x0128;
    public const int NpuSetScaleRegion = 0x0129;
    public const int NpuSetAbStart = 0x012D;
    public const int NpuSetBlockdep = 0x012F;
    public const int NpuSetDma0SrcRegion = 0x0130;
    public const int NpuSetDma0DstRegion = 0x0131;
    public const int NpuSetDma0Size0 = 0x0132;
    public const int NpuSetDma0Size1 = 0x0133;
    public const int NpuSetIfm2Broadcast = 0x0180;
    public const int NpuSetIfm2Scalar = 0x0181;
    public const int NpuSetIfm2Precision = 0x0185;
    public const int NpuSetIfm2ZeroPoint = 0x0189;
    public const int NpuSetIfm2Width0M1 = 0x018A;
    public const int NpuSetIfm2Height0M1 = 0x018B;
    public const int NpuSetIfm2Height1M1 = 0x018C;
    public const int NpuSetIfm2IbStart = 0x018D;
    public const int NpuSetIfm2Region = 0x018F;

    // cmd1 Register Set Opcodes (Payload32)
    public const int NpuSetIfmBase0 = 0x0000;
    public const int NpuSetIfmBase1 = 0x0001;
    public const int NpuSetIfmBase2 = 0x0002;
    public const int NpuSetIfmBase3 = 0x0003;
    public const int NpuSetIfmStrideX = 0x0004;
    public const int NpuSetIfmStrideY = 0x0005;
    public const int NpuSetIfmStrideC = 0x0006;
    public const int NpuSetOfmBase0 = 0x0010;
    public const int NpuSetOfmBase1 = 0x0011;
    public const int NpuSetOfmBase2 = 0x0012;
    public const int NpuSetOfmBase3 = 0x0013;
    public const int NpuSetOfmStrideX = 0x0014;
    public const int NpuSetOfmStrideY = 0x0015;
    public const int NpuSetOfmStrideC = 0x0016;
    public const int NpuSetWeightBase = 0x0020;
    public const int NpuSetWeightLength = 0x0021;
    public const int NpuSetScaleBase = 0x0022;
    public const int NpuSetScaleLength = 0x0023;
    public const int NpuSetOfmScale = 0x0024;
    public const int NpuSetOpaScale = 0x0025;
    public const int NpuSetOpbScale = 0x0026;
    public const int NpuSetDma0Src = 0x0030;
    public const int NpuSetDma0Dst = 0x0031;
    public const int NpuSetDma0Len = 0x0032;
    public const int NpuSetDma0Skip0 = 0x0033;
    public const int NpuSetDma0Skip1 = 0x0034;
    public const int NpuSetIfm2Base0 = 0x0080;
    public const int NpuSetIfm2Base1 = 0x0081;
    public const int NpuSetIfm2Base2 = 0x0082;
    public const int NpuSetIfm2Base3 = 0x0083;
    public const int NpuSetIfm2StrideX = 0x0084;
    public const int NpuSetIfm2StrideY = 0x0085;
    public const int NpuSetIfm2StrideC = 0x0086;
    public const int NpuSetWeight1Base = 0x0090;
    public const int NpuSetWeight1Length = 0x0091;
    public const int NpuSetScale1Base = 0x0092;
    public const int NpuSetScale1Length = 0x0093;

    public static string GetCmd0Name(int opcode) => opcode switch
    {
        NpuOpStop => "NPU_OP_STOP",
        NpuOpIrq => "NPU_OP_IRQ",
        NpuOpConv => "NPU_OP_CONV",
        NpuOpDepthwise => "NPU_OP_DEPTHWISE",
        NpuOpPool => "NPU_OP_POOL",
        NpuOpElementwise => "NPU_OP_ELEMENTWISE",
        NpuOpDmaStart => "NPU_OP_DMA_START",
        NpuOpDmaWait => "NPU_OP_DMA_WAIT",
        NpuOpKernelWait => "NPU_OP_KERNEL_WAIT",
        NpuOpPmuMask => "NPU_OP_PMU_MASK",
        NpuSetIfmPadTop => "NPU_SET_IFM_PAD_TOP",
        NpuSetIfmPadLeft => "NPU_SET_IFM_PAD_LEFT",
        NpuSetIfmPadRight => "NPU_SET_IFM_PAD_RIGHT",
        NpuSetIfmPadBottom => "NPU_SET_IFM_PAD_BOTTOM",
        NpuSetIfmDepthM1 => "NPU_SET_IFM_DEPTH_M1",
        NpuSetIfmPrecision => "NPU_SET_IFM_PRECISION",
        NpuSetIfmUpscale => "NPU_SET_IFM_UPSCALE",
        NpuSetIfmZeroPoint => "NPU_SET_IFM_ZERO_POINT",
        NpuSetIfmWidth0M1 => "NPU_SET_IFM_WIDTH0_M1",
        NpuSetIfmHeight0M1 => "NPU_SET_IFM_HEIGHT0_M1",
        NpuSetIfmHeight1M1 => "NPU_SET_IFM_HEIGHT1_M1",
        NpuSetIfmIbEnd => "NPU_SET_IFM_IB_END",
        NpuSetIfmRegion => "NPU_SET_IFM_REGION",
        NpuSetOfmWidthM1 => "NPU_SET_OFM_WIDTH_M1",
        NpuSetOfmHeightM1 => "NPU_SET_OFM_HEIGHT_M1",
        NpuSetOfmDepthM1 => "NPU_SET_OFM_DEPTH_M1",
        NpuSetOfmPrecision => "NPU_SET_OFM_PRECISION",
        NpuSetOfmBlkWidthM1 => "NPU_SET_OFM_BLK_WIDTH_M1",
        NpuSetOfmBlkHeightM1 => "NPU_SET_OFM_BLK_HEIGHT_M1",
        NpuSetOfmBlkDepthM1 => "NPU_SET_OFM_BLK_DEPTH_M1",
        NpuSetOfmZeroPoint => "NPU_SET_OFM_ZERO_POINT",
        NpuSetOfmWidth0M1 => "NPU_SET_OFM_WIDTH0_M1",
        NpuSetOfmHeight0M1 => "NPU_SET_OFM_HEIGHT0_M1",
        NpuSetOfmHeight1M1 => "NPU_SET_OFM_HEIGHT1_M1",
        NpuSetOfmRegion => "NPU_SET_OFM_REGION",
        NpuSetKernelWidthM1 => "NPU_SET_KERNEL_WIDTH_M1",
        NpuSetKernelHeightM1 => "NPU_SET_KERNEL_HEIGHT_M1",
        NpuSetKernelStride => "NPU_SET_KERNEL_STRIDE",
        NpuSetParallelMode => "NPU_SET_PARALLEL_MODE",
        NpuSetAccFormat => "NPU_SET_ACC_FORMAT",
        NpuSetActivation => "NPU_SET_ACTIVATION",
        NpuSetActivationMin => "NPU_SET_ACTIVATION_MIN",
        NpuSetActivationMax => "NPU_SET_ACTIVATION_MAX",
        NpuSetWeightRegion => "NPU_SET_WEIGHT_REGION",
        NpuSetScaleRegion => "NPU_SET_SCALE_REGION",
        NpuSetAbStart => "NPU_SET_AB_START",
        NpuSetBlockdep => "NPU_SET_BLOCKDEP",
        NpuSetDma0SrcRegion => "NPU_SET_DMA0_SRC_REGION",
        NpuSetDma0DstRegion => "NPU_SET_DMA0_DST_REGION",
        NpuSetDma0Size0 => "NPU_SET_DMA0_SIZE0",
        NpuSetDma0Size1 => "NPU_SET_DMA0_SIZE1",
        NpuSetIfm2Broadcast => "NPU_SET_IFM2_BROADCAST",
        NpuSetIfm2Scalar => "NPU_SET_IFM2_SCALAR",
        NpuSetIfm2Precision => "NPU_SET_IFM2_PRECISION",
        NpuSetIfm2ZeroPoint => "NPU_SET_IFM2_ZERO_POINT",
        NpuSetIfm2Width0M1 => "NPU_SET_IFM2_WIDTH0_M1",
        NpuSetIfm2Height0M1 => "NPU_SET_IFM2_HEIGHT0_M1",
        NpuSetIfm2Height1M1 => "NPU_SET_IFM2_HEIGHT1_M1",
        NpuSetIfm2IbStart => "NPU_SET_IFM2_IB_START",
        NpuSetIfm2Region => "NPU_SET_IFM2_REGION",
        _ => string.Create(CultureInfo.InvariantCulture, $"UNKNOWN_CMD0_0x{opcode:X4}"),
    };

    public static string GetCmd1Name(int opcode) => opcode switch
    {
        NpuSetIfmBase0 => "NPU_SET_IFM_BASE0",
        NpuSetIfmBase1 => "NPU_SET_IFM_BASE1",
        NpuSetIfmBase2 => "NPU_SET_IFM_BASE2",
        NpuSetIfmBase3 => "NPU_SET_IFM_BASE3",
        NpuSetIfmStrideX => "NPU_SET_IFM_STRIDE_X",
        NpuSetIfmStrideY => "NPU_SET_IFM_STRIDE_Y",
        NpuSetIfmStrideC => "NPU_SET_IFM_STRIDE_C",
        NpuSetOfmBase0 => "NPU_SET_OFM_BASE0",
        NpuSetOfmBase1 => "NPU_SET_OFM_BASE1",
        NpuSetOfmBase2 => "NPU_SET_OFM_BASE2",
        NpuSetOfmBase3 => "NPU_SET_OFM_BASE3",
        NpuSetOfmStrideX => "NPU_SET_OFM_STRIDE_X",
        NpuSetOfmStrideY => "NPU_SET_OFM_STRIDE_Y",
        NpuSetOfmStrideC => "NPU_SET_OFM_STRIDE_C",
        NpuSetWeightBase => "NPU_SET_WEIGHT_BASE",
        NpuSetWeightLength => "NPU_SET_WEIGHT_LENGTH",
        NpuSetScaleBase => "NPU_SET_SCALE_BASE",
        NpuSetScaleLength => "NPU_SET_SCALE_LENGTH",
        NpuSetOfmScale => "NPU_SET_OFM_SCALE",
        NpuSetOpaScale => "NPU_SET_OPA_SCALE",
        NpuSetOpbScale => "NPU_SET_OPB_SCALE",
        NpuSetDma0Src => "NPU_SET_DMA0_SRC",
        NpuSetDma0Dst => "NPU_SET_DMA0_DST",
        NpuSetDma0Len => "NPU_SET_DMA0_LEN",
        NpuSetDma0Skip0 => "NPU_SET_DMA0_SKIP0",
        NpuSetDma0Skip1 => "NPU_SET_DMA0_SKIP1",
        NpuSetIfm2Base0 => "NPU_SET_IFM2_BASE0",
        NpuSetIfm2Base1 => "NPU_SET_IFM2_BASE1",
        NpuSetIfm2Base2 => "NPU_SET_IFM2_BASE2",
        NpuSetIfm2Base3 => "NPU_SET_IFM2_BASE3",
        NpuSetIfm2StrideX => "NPU_SET_IFM2_STRIDE_X",
        NpuSetIfm2StrideY => "NPU_SET_IFM2_STRIDE_Y",
        NpuSetIfm2StrideC => "NPU_SET_IFM2_STRIDE_C",
        NpuSetWeight1Base => "NPU_SET_WEIGHT1_BASE",
        NpuSetWeight1Length => "NPU_SET_WEIGHT1_LENGTH",
        NpuSetScale1Base => "NPU_SET_SCALE1_BASE",
        NpuSetScale1Length => "NPU_SET_SCALE1_LENGTH",
        _ => string.Create(CultureInfo.InvariantCulture, $"UNKNOWN_CMD1_0x{opcode:X4}"),
    };

    public static bool IsExecutableOperation(int opcode) => opcode switch
    {
        NpuOpConv or NpuOpDepthwise or NpuOpPool or NpuOpElementwise or NpuOpDmaStart => true,
        _ => false,
    };
}
