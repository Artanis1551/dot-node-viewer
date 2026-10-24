namespace DotNodeViewer.Core.Delegates.EthosU;

/// <summary>
/// Immutable snapshot of the Ethos-U NPU register state captured at the moment an operation is dispatched.
/// </summary>
internal sealed record EthosURegisterSnapshot(
    int Opcode,
    int Param,
    int IfmRegion,
    uint IfmBase0,
    int IfmWidth,
    int IfmHeight,
    int IfmDepth,
    uint IfmStrideX,
    uint IfmStrideY,
    uint IfmStrideC,
    int IfmPrecision,
    int IfmZeroPoint,
    int PadTop,
    int PadLeft,
    int PadBottom,
    int PadRight,
    int Ifm2Region,
    uint Ifm2Base0,
    int Ifm2Width,
    int Ifm2Height,
    int Ifm2Precision,
    int Ifm2ZeroPoint,
    int Ifm2Broadcast,
    int OfmRegion,
    uint OfmBase0,
    int OfmWidth,
    int OfmHeight,
    int OfmDepth,
    uint OfmStrideX,
    uint OfmStrideY,
    uint OfmStrideC,
    int OfmPrecision,
    int OfmZeroPoint,
    int BlockWidth,
    int BlockHeight,
    int BlockDepth,
    int KernelWidth,
    int KernelHeight,
    int KernelStride,
    int WeightRegion,
    uint WeightBase,
    uint WeightLength,
    int ScaleRegion,
    uint ScaleBase,
    uint ScaleLength,
    int Activation,
    int ActivationMin,
    int ActivationMax,
    int DmaSrcRegion,
    uint DmaSrcAddr,
    int DmaDstRegion,
    uint DmaDstAddr,
    uint DmaLen,
    IReadOnlyList<EthosUCommand> PrecedingCommands);

/// <summary>
/// Maintains the active Ethos-U register bank and records immutable snapshots when operations are triggered.
/// </summary>
internal sealed class EthosURegisterState
{
    // IFM
    public int IfmRegion { get; set; } = 1;
    public uint IfmBase0 { get; set; }
    public int IfmWidthM1 { get; set; }
    public int IfmHeight0M1 { get; set; }
    public int IfmHeight1M1 { get; set; }
    public int IfmDepthM1 { get; set; }
    public uint IfmStrideX { get; set; }
    public uint IfmStrideY { get; set; }
    public uint IfmStrideC { get; set; }
    public int IfmPrecision { get; set; }
    public int IfmZeroPoint { get; set; }
    public int PadTop { get; set; }
    public int PadLeft { get; set; }
    public int PadBottom { get; set; }
    public int PadRight { get; set; }
    public int IfmUpscale { get; set; }

    // IFM2
    public int Ifm2Region { get; set; } = 1;
    public uint Ifm2Base0 { get; set; }
    public int Ifm2WidthM1 { get; set; }
    public int Ifm2Height0M1 { get; set; }
    public int Ifm2Height1M1 { get; set; }
    public uint Ifm2StrideX { get; set; }
    public uint Ifm2StrideY { get; set; }
    public uint Ifm2StrideC { get; set; }
    public int Ifm2Precision { get; set; }
    public int Ifm2ZeroPoint { get; set; }
    public int Ifm2Broadcast { get; set; }

    // OFM
    public int OfmRegion { get; set; } = 1;
    public uint OfmBase0 { get; set; }
    public int OfmWidthM1 { get; set; }
    public int OfmHeightM1 { get; set; }
    public int OfmDepthM1 { get; set; }
    public uint OfmStrideX { get; set; }
    public uint OfmStrideY { get; set; }
    public uint OfmStrideC { get; set; }
    public int OfmPrecision { get; set; }
    public int OfmZeroPoint { get; set; }
    public int OfmBlkWidthM1 { get; set; }
    public int OfmBlkHeightM1 { get; set; }
    public int OfmBlkDepthM1 { get; set; }

    // Kernel
    public int KernelWidthM1 { get; set; }
    public int KernelHeightM1 { get; set; }
    public int KernelStride { get; set; }

    // Weights & Scales
    public int WeightRegion { get; set; }
    public uint WeightBase { get; set; }
    public uint WeightLength { get; set; }
    public int ScaleRegion { get; set; }
    public uint ScaleBase { get; set; }
    public uint ScaleLength { get; set; }

    // Activation
    public int Activation { get; set; }
    public int ActivationMin { get; set; } = int.MinValue;
    public int ActivationMax { get; set; } = int.MaxValue;

    // DMA
    public int DmaSrcRegion { get; set; }
    public uint DmaSrcAddr { get; set; }
    public int DmaDstRegion { get; set; }
    public uint DmaDstAddr { get; set; }
    public uint DmaLen { get; set; }

    // Block Config
    public int ParallelMode { get; set; }
    public int AccFormat { get; set; }
    public int BlockDep { get; set; }

    private readonly List<EthosUCommand> _currentCommands = new();

    /// <summary>
    /// Applies a command to the register state.
    /// </summary>
    /// <param name="cmd">The decoded command to apply.</param>
    public void Apply(in EthosUCommand cmd)
    {
        _currentCommands.Add(cmd);

        if (!cmd.IsCmd1)
        {
            ApplyCmd0(cmd.Opcode, cmd.Param);
        }
        else if (cmd.Payload.HasValue)
        {
            ApplyCmd1(cmd.Opcode, cmd.Param, cmd.Payload.Value);
        }
    }

    private void ApplyCmd0(int opcode, int param)
    {
        switch (opcode)
        {
            case EthosUOpcodeCatalog.NpuSetIfmRegion:
                IfmRegion = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfmWidth0M1:
                IfmWidthM1 = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfmHeight0M1:
                IfmHeight0M1 = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfmHeight1M1:
                IfmHeight1M1 = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfmDepthM1:
                IfmDepthM1 = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfmPrecision:
                IfmPrecision = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfmZeroPoint:
                IfmZeroPoint = unchecked((short)param);
                break;
            case EthosUOpcodeCatalog.NpuSetIfmPadTop:
                PadTop = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfmPadLeft:
                PadLeft = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfmPadBottom:
                PadBottom = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfmPadRight:
                PadRight = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfmUpscale:
                IfmUpscale = param;
                break;

            case EthosUOpcodeCatalog.NpuSetIfm2Region:
                Ifm2Region = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfm2Width0M1:
                Ifm2WidthM1 = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfm2Height0M1:
                Ifm2Height0M1 = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfm2Height1M1:
                Ifm2Height1M1 = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfm2Precision:
                Ifm2Precision = param;
                break;
            case EthosUOpcodeCatalog.NpuSetIfm2ZeroPoint:
                Ifm2ZeroPoint = unchecked((short)param);
                break;
            case EthosUOpcodeCatalog.NpuSetIfm2Broadcast:
                Ifm2Broadcast = param;
                break;

            case EthosUOpcodeCatalog.NpuSetOfmRegion:
                OfmRegion = param;
                break;
            case EthosUOpcodeCatalog.NpuSetOfmWidthM1:
            case EthosUOpcodeCatalog.NpuSetOfmWidth0M1:
                OfmWidthM1 = param;
                break;
            case EthosUOpcodeCatalog.NpuSetOfmHeightM1:
            case EthosUOpcodeCatalog.NpuSetOfmHeight0M1:
                OfmHeightM1 = param;
                break;
            case EthosUOpcodeCatalog.NpuSetOfmDepthM1:
                OfmDepthM1 = param;
                break;
            case EthosUOpcodeCatalog.NpuSetOfmPrecision:
                OfmPrecision = param;
                break;
            case EthosUOpcodeCatalog.NpuSetOfmZeroPoint:
                OfmZeroPoint = unchecked((short)param);
                break;
            case EthosUOpcodeCatalog.NpuSetOfmBlkWidthM1:
                OfmBlkWidthM1 = param;
                break;
            case EthosUOpcodeCatalog.NpuSetOfmBlkHeightM1:
                OfmBlkHeightM1 = param;
                break;
            case EthosUOpcodeCatalog.NpuSetOfmBlkDepthM1:
                OfmBlkDepthM1 = param;
                break;

            case EthosUOpcodeCatalog.NpuSetKernelWidthM1:
                KernelWidthM1 = param;
                break;
            case EthosUOpcodeCatalog.NpuSetKernelHeightM1:
                KernelHeightM1 = param;
                break;
            case EthosUOpcodeCatalog.NpuSetKernelStride:
                KernelStride = param;
                break;

            case EthosUOpcodeCatalog.NpuSetWeightRegion:
                WeightRegion = param;
                break;
            case EthosUOpcodeCatalog.NpuSetScaleRegion:
                ScaleRegion = param;
                break;

            case EthosUOpcodeCatalog.NpuSetActivation:
                Activation = param;
                break;
            case EthosUOpcodeCatalog.NpuSetActivationMin:
                ActivationMin = unchecked((short)param);
                break;
            case EthosUOpcodeCatalog.NpuSetActivationMax:
                ActivationMax = unchecked((short)param);
                break;

            case EthosUOpcodeCatalog.NpuSetDma0SrcRegion:
                DmaSrcRegion = param;
                break;
            case EthosUOpcodeCatalog.NpuSetDma0DstRegion:
                DmaDstRegion = param;
                break;

            case EthosUOpcodeCatalog.NpuSetBlockdep:
                BlockDep = param;
                break;
            case EthosUOpcodeCatalog.NpuSetAccFormat:
                AccFormat = param;
                break;
            case EthosUOpcodeCatalog.NpuSetParallelMode:
                ParallelMode = param;
                break;
        }
    }

    private void ApplyCmd1(int opcode, int param, uint payload)
    {
        switch (opcode)
        {
            case EthosUOpcodeCatalog.NpuSetIfmBase0:
                IfmBase0 = payload;
                break;
            case EthosUOpcodeCatalog.NpuSetIfmStrideX:
                IfmStrideX = payload;
                break;
            case EthosUOpcodeCatalog.NpuSetIfmStrideY:
                IfmStrideY = payload;
                break;
            case EthosUOpcodeCatalog.NpuSetIfmStrideC:
                IfmStrideC = payload;
                break;

            case EthosUOpcodeCatalog.NpuSetIfm2Base0:
                Ifm2Base0 = payload;
                break;
            case EthosUOpcodeCatalog.NpuSetIfm2StrideX:
                Ifm2StrideX = payload;
                break;
            case EthosUOpcodeCatalog.NpuSetIfm2StrideY:
                Ifm2StrideY = payload;
                break;
            case EthosUOpcodeCatalog.NpuSetIfm2StrideC:
                Ifm2StrideC = payload;
                break;

            case EthosUOpcodeCatalog.NpuSetOfmBase0:
                OfmBase0 = payload;
                break;
            case EthosUOpcodeCatalog.NpuSetOfmStrideX:
                OfmStrideX = payload;
                break;
            case EthosUOpcodeCatalog.NpuSetOfmStrideY:
                OfmStrideY = payload;
                break;
            case EthosUOpcodeCatalog.NpuSetOfmStrideC:
                OfmStrideC = payload;
                break;

            case EthosUOpcodeCatalog.NpuSetWeightBase:
                WeightBase = payload;
                break;
            case EthosUOpcodeCatalog.NpuSetWeightLength:
                WeightLength = payload;
                break;
            case EthosUOpcodeCatalog.NpuSetScaleBase:
                ScaleBase = payload;
                break;
            case EthosUOpcodeCatalog.NpuSetScaleLength:
                ScaleLength = payload;
                break;

            case EthosUOpcodeCatalog.NpuSetDma0Src:
                DmaSrcAddr = payload;
                break;
            case EthosUOpcodeCatalog.NpuSetDma0Dst:
                DmaDstAddr = payload;
                break;
            case EthosUOpcodeCatalog.NpuSetDma0Len:
                DmaLen = payload;
                break;
        }
    }

    /// <summary>
    /// Creates an immutable snapshot of the current state and resets the accumulated command list.
    /// </summary>
    /// <param name="opcode">The executable operation opcode being triggered.</param>
    /// <param name="param">The operation parameter.</param>
    /// <returns>A new <see cref="EthosURegisterSnapshot"/> instance.</returns>
    public EthosURegisterSnapshot CreateSnapshot(int opcode, int param)
    {
        var snapshot = new EthosURegisterSnapshot(
            opcode,
            param,
            IfmRegion,
            IfmBase0,
            IfmWidthM1 + 1,
            Math.Max(IfmHeight0M1, IfmHeight1M1) + 1,
            IfmDepthM1 + 1,
            IfmStrideX,
            IfmStrideY,
            IfmStrideC,
            IfmPrecision,
            IfmZeroPoint,
            PadTop,
            PadLeft,
            PadBottom,
            PadRight,
            Ifm2Region,
            Ifm2Base0,
            Ifm2WidthM1 + 1,
            Math.Max(Ifm2Height0M1, Ifm2Height1M1) + 1,
            Ifm2Precision,
            Ifm2ZeroPoint,
            Ifm2Broadcast,
            OfmRegion,
            OfmBase0,
            OfmWidthM1 + 1,
            OfmHeightM1 + 1,
            OfmDepthM1 + 1,
            OfmStrideX,
            OfmStrideY,
            OfmStrideC,
            OfmPrecision,
            OfmZeroPoint,
            OfmBlkWidthM1 + 1,
            OfmBlkHeightM1 + 1,
            OfmBlkDepthM1 + 1,
            KernelWidthM1 + 1,
            KernelHeightM1 + 1,
            KernelStride,
            WeightRegion,
            WeightBase,
            WeightLength,
            ScaleRegion,
            ScaleBase,
            ScaleLength,
            Activation,
            ActivationMin,
            ActivationMax,
            DmaSrcRegion,
            DmaSrcAddr,
            DmaDstRegion,
            DmaDstAddr,
            DmaLen,
            _currentCommands.ToArray());

        _currentCommands.Clear();
        return snapshot;
    }
}
