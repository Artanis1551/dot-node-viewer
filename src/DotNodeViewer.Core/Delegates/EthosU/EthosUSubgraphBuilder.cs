using System.Globalization;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Delegates.EthosU;

/// <summary>
/// Builds a synthesized <see cref="Subgraph"/> from decoded Ethos-U register snapshots and driver payload metadata.
/// </summary>
internal static class EthosUSubgraphBuilder
{
    private sealed record WrittenRegion(int Region, uint Start, uint End, string NodeId, TensorEdge Edge);

    /// <summary>
    /// Projects decoded register snapshots into a Unified Graph IR <see cref="Subgraph"/>.
    /// </summary>
    /// <param name="subgraphName">The name for the synthesized subgraph (for example, <c>"ethos-u (Ethos-U55-128)"</c>).</param>
    /// <param name="driverPayload">The parsed Ethos-U driver action payload.</param>
    /// <param name="snapshots">The immutable register snapshots captured at each operation dispatch.</param>
    /// <param name="weightSliceBaseOffset">The absolute stream offset of the flash/weight constant buffer.</param>
    /// <param name="outerInputs">The formal input ports of the delegate node from the outer graph.</param>
    /// <param name="outerOutputs">The formal output ports of the delegate node from the outer graph.</param>
    /// <param name="inputOffsets">Memory arena offsets mapped to outer input tensor names.</param>
    /// <param name="outputOffsets">Memory arena offsets mapped to outer output tensor names.</param>
    /// <returns>A fully materialized, immutable <see cref="Subgraph"/> representing the internal NPU dataflow.</returns>
    public static Subgraph Build(
        string subgraphName,
        EthosUDriverPayload driverPayload,
        IReadOnlyList<EthosURegisterSnapshot> snapshots,
        long weightSliceBaseOffset,
        IReadOnlyList<GraphPort> outerInputs,
        IReadOnlyList<GraphPort> outerOutputs,
        IReadOnlyDictionary<uint, string>? inputOffsets = null,
        IReadOnlyDictionary<uint, string>? outputOffsets = null)
    {
        var nodes = new List<GraphNode>();
        var allTensors = new Dictionary<string, TensorEdge>(StringComparer.Ordinal);
        var inputs = new List<GraphPort>();
        var outputs = new List<GraphPort>();
        var writtenIntervals = new List<WrittenRegion>();

        int opIndex = 0;
        foreach (EthosURegisterSnapshot snap in snapshots)
        {
            if (!EthosUOpcodeCatalog.IsExecutableOperation(snap.Opcode))
            {
                continue;
            }

            string opName = GetOperatorName(snap.Opcode, snap.Param);
            OperatorCategory category = GetOperatorCategory(snap.Opcode, snap.Param);
            string nodeId = string.Create(CultureInfo.InvariantCulture, $"npu_op_{opIndex}_{opName.ToLowerInvariant()}");

            var inPorts = new List<NodePort>();
            var outPorts = new List<NodePort>();
            var attributes = new List<NodeAttribute>();

            // Capture raw command disassembly
            var commandLines = new List<string>(snap.PrecedingCommands.Count);
            foreach (EthosUCommand cmd in snap.PrecedingCommands)
            {
                string line = cmd.IsCmd1
                    ? string.Create(CultureInfo.InvariantCulture, $"0x{cmd.ByteOffset:X6}: {cmd.Payload:X8} {cmd.Param:X4} - {cmd.Name}")
                    : string.Create(CultureInfo.InvariantCulture, $"0x{cmd.ByteOffset:X6}:          {cmd.Param:X4} - {cmd.Name}");
                commandLines.Add(line);
            }
            attributes.Add(new NodeAttribute("commands", new AttributeValue.StringList(commandLines)));

            if (snap.Opcode == EthosUOpcodeCatalog.NpuOpDmaStart)
            {
                BuildDmaNode(
                    snap,
                    nodeId,
                    inPorts,
                    outPorts,
                    attributes,
                    allTensors,
                    inputs,
                    writtenIntervals,
                    inputOffsets,
                    outerInputs);
            }
            else
            {
                BuildKernelNode(
                    snap,
                    nodeId,
                    inPorts,
                    outPorts,
                    attributes,
                    allTensors,
                    inputs,
                    outputs,
                    writtenIntervals,
                    weightSliceBaseOffset,
                    inputOffsets,
                    outputOffsets,
                    outerInputs,
                    outerOutputs);
            }

            nodes.Add(new GraphNode(
                nodeId,
                new OperatorInfo(opName, category, "ai.arm.ethos-u", 1),
                inPorts,
                outPorts,
                attributes));

            opIndex++;
        }

        // Add any formal outer inputs/outputs that were not automatically matched
        foreach (GraphPort outerIn in outerInputs)
        {
            if (!inputs.Any(p => string.Equals(p.Name, outerIn.Name, StringComparison.Ordinal)))
            {
                inputs.Add(outerIn);
                foreach (TensorEdge edge in outerIn.Tensors)
                {
                    allTensors.TryAdd(edge.Name, edge);
                }
            }
        }

        foreach (GraphPort outerOut in outerOutputs)
        {
            if (!outputs.Any(p => string.Equals(p.Name, outerOut.Name, StringComparison.Ordinal)))
            {
                outputs.Add(outerOut);
                foreach (TensorEdge edge in outerOut.Tensors)
                {
                    allTensors.TryAdd(edge.Name, edge);
                }
            }
        }

        return new Subgraph(subgraphName, nodes, allTensors.Values.ToList(), inputs, outputs);
    }

    private static void BuildKernelNode(
        EthosURegisterSnapshot snap,
        string nodeId,
        List<NodePort> inPorts,
        List<NodePort> outPorts,
        List<NodeAttribute> attributes,
        Dictionary<string, TensorEdge> allTensors,
        List<GraphPort> inputs,
        List<GraphPort> outputs,
        List<WrittenRegion> writtenIntervals,
        long weightSliceBaseOffset,
        IReadOnlyDictionary<uint, string>? inputOffsets,
        IReadOnlyDictionary<uint, string>? outputOffsets,
        IReadOnlyList<GraphPort> outerInputs,
        IReadOnlyList<GraphPort> outerOutputs)
    {
        DataType ifmType = MapPrecision(snap.IfmPrecision);
        DataType ofmType = MapPrecision(snap.OfmPrecision);

        int ifmBytesPerElem = GetElementByteSize(ifmType);
        int ofmBytesPerElem = GetElementByteSize(ofmType);

        uint ifmSize = (uint)(snap.IfmWidth * snap.IfmHeight * snap.IfmDepth * ifmBytesPerElem);
        uint ofmSize = (uint)(snap.OfmWidth * snap.OfmHeight * snap.OfmDepth * ofmBytesPerElem);

        var ifmShape = new TensorShape(new TensorDimension[]
        {
            new TensorDimension.Fixed(1),
            new TensorDimension.Fixed(snap.IfmHeight),
            new TensorDimension.Fixed(snap.IfmWidth),
            new TensorDimension.Fixed(snap.IfmDepth)
        });

        var ofmShape = new TensorShape(new TensorDimension[]
        {
            new TensorDimension.Fixed(1),
            new TensorDimension.Fixed(snap.OfmHeight),
            new TensorDimension.Fixed(snap.OfmWidth),
            new TensorDimension.Fixed(snap.OfmDepth)
        });

        // Attributes
        attributes.Add(new NodeAttribute("ifm_shape", new AttributeValue.ShapeValue(ifmShape)));
        attributes.Add(new NodeAttribute("ofm_shape", new AttributeValue.ShapeValue(ofmShape)));
        attributes.Add(new NodeAttribute("ifm_precision", new AttributeValue.Int64(snap.IfmPrecision)));
        attributes.Add(new NodeAttribute("ofm_precision", new AttributeValue.Int64(snap.OfmPrecision)));
        attributes.Add(new NodeAttribute("ifm_zero_point", new AttributeValue.Int64(snap.IfmZeroPoint)));
        attributes.Add(new NodeAttribute("ofm_zero_point", new AttributeValue.Int64(snap.OfmZeroPoint)));
        attributes.Add(new NodeAttribute("kernel", new AttributeValue.Int64List(new long[] { snap.KernelHeight, snap.KernelWidth })));
        attributes.Add(new NodeAttribute("padding", new AttributeValue.Int64List(new long[] { snap.PadTop, snap.PadLeft, snap.PadBottom, snap.PadRight })));
        attributes.Add(new NodeAttribute("block_config", new AttributeValue.Int64List(new long[] { snap.BlockHeight, snap.BlockWidth, snap.BlockDepth })));

        if (snap.Activation != 0 || snap.ActivationMin > int.MinValue || snap.ActivationMax < int.MaxValue)
        {
            attributes.Add(new NodeAttribute("activation", new AttributeValue.Int64(snap.Activation)));
            attributes.Add(new NodeAttribute("clamp_min", new AttributeValue.Int64(snap.ActivationMin)));
            attributes.Add(new NodeAttribute("clamp_max", new AttributeValue.Int64(snap.ActivationMax)));
        }

        // 1. Resolve IFM dataflow edge
        TensorEdge ifmEdge = ResolveInputEdge(
            snap.IfmRegion,
            snap.IfmBase0,
            ifmSize,
            ifmType,
            ifmShape,
            writtenIntervals,
            allTensors,
            inputs,
            inputOffsets,
            outerInputs);

        inPorts.Add(new NodePort("ifm", new[] { ifmEdge }));

        // 2. Resolve IFM2 (for elementwise ops)
        if (snap.Opcode == EthosUOpcodeCatalog.NpuOpElementwise)
        {
            DataType ifm2Type = MapPrecision(snap.Ifm2Precision);
            int ifm2BytesPerElem = GetElementByteSize(ifm2Type);
            uint ifm2Size = (uint)(snap.Ifm2Width * snap.Ifm2Height * snap.IfmDepth * ifm2BytesPerElem);

            var ifm2Shape = new TensorShape(new TensorDimension[]
            {
                new TensorDimension.Fixed(1),
                new TensorDimension.Fixed(snap.Ifm2Height),
                new TensorDimension.Fixed(snap.Ifm2Width),
                new TensorDimension.Fixed(snap.IfmDepth)
            });

            TensorEdge ifm2Edge = ResolveInputEdge(
                snap.Ifm2Region,
                snap.Ifm2Base0,
                ifm2Size,
                ifm2Type,
                ifm2Shape,
                writtenIntervals,
                allTensors,
                inputs,
                inputOffsets,
                outerInputs);

            inPorts.Add(new NodePort("ifm2", new[] { ifm2Edge }));
        }

        // 3. Resolve Weight / Scale Initializers
        if (snap.WeightLength > 0 && weightSliceBaseOffset > 0)
        {
            long weightAbsoluteOffset = weightSliceBaseOffset + snap.WeightBase;
            var weightSlice = new TensorDataSlice(
                weightAbsoluteOffset,
                (long)snap.WeightLength,
                DataType.UInt8,
                TensorDataEncoding.EthosUCompressedWeights);

            var weightDescriptor = new TensorDescriptor(
                DataType.UInt8,
                new TensorShape(new TensorDimension[] { new TensorDimension.Fixed(snap.WeightLength) }));

            string weightName = string.Create(CultureInfo.InvariantCulture, $"{nodeId}_w_0x{snap.WeightBase:X6}");
            var weightEdge = new TensorEdge(weightName, weightDescriptor, TensorKind.Initializer, weightSlice);
            allTensors.TryAdd(weightName, weightEdge);
            inPorts.Add(new NodePort("weights", new[] { weightEdge }));
        }

        if (snap.ScaleLength > 0 && weightSliceBaseOffset > 0)
        {
            long scaleAbsoluteOffset = weightSliceBaseOffset + snap.ScaleBase;
            var scaleSlice = new TensorDataSlice(
                scaleAbsoluteOffset,
                (long)snap.ScaleLength,
                DataType.UInt8,
                TensorDataEncoding.EthosUCompressedWeights);

            var scaleDescriptor = new TensorDescriptor(
                DataType.UInt8,
                new TensorShape(new TensorDimension[] { new TensorDimension.Fixed(snap.ScaleLength) }));

            string scaleName = string.Create(CultureInfo.InvariantCulture, $"{nodeId}_b_0x{snap.ScaleBase:X6}");
            var scaleEdge = new TensorEdge(scaleName, scaleDescriptor, TensorKind.Initializer, scaleSlice);
            allTensors.TryAdd(scaleName, scaleEdge);
            inPorts.Add(new NodePort("scales", new[] { scaleEdge }));
        }

        // 4. Create OFM Edge
        string ofmName = (outputOffsets != null && outputOffsets.TryGetValue(snap.OfmBase0, out string? mappedOutName))
            ? mappedOutName
            : string.Create(CultureInfo.InvariantCulture, $"r{snap.OfmRegion}@0x{snap.OfmBase0:X6}_{nodeId}");

        var ofmEdge = new TensorEdge(ofmName, new TensorDescriptor(ofmType, ofmShape), TensorKind.Activation);
        allTensors[ofmName] = ofmEdge;
        outPorts.Add(new NodePort("ofm", new[] { ofmEdge }));

        // 5. Record OFM written interval for future operations
        writtenIntervals.Add(new WrittenRegion(
            snap.OfmRegion,
            snap.OfmBase0,
            snap.OfmBase0 + ofmSize,
            nodeId,
            ofmEdge));

        // 6. Check if OFM matches a delegate output port
        if (outputOffsets != null && outputOffsets.TryGetValue(snap.OfmBase0, out string? outPortName))
        {
            var graphPort = outerOutputs.FirstOrDefault(p => string.Equals(p.Name, outPortName, StringComparison.Ordinal))
                ?? new GraphPort(outPortName, new[] { ofmEdge });

            if (!outputs.Any(p => string.Equals(p.Name, graphPort.Name, StringComparison.Ordinal)))
            {
                outputs.Add(graphPort);
            }
        }
    }

    private static void BuildDmaNode(
        EthosURegisterSnapshot snap,
        string nodeId,
        List<NodePort> inPorts,
        List<NodePort> outPorts,
        List<NodeAttribute> attributes,
        Dictionary<string, TensorEdge> allTensors,
        List<GraphPort> inputs,
        List<WrittenRegion> writtenIntervals,
        IReadOnlyDictionary<uint, string>? inputOffsets,
        IReadOnlyList<GraphPort> outerInputs)
    {
        attributes.Add(new NodeAttribute("src_region", new AttributeValue.Int64(snap.DmaSrcRegion)));
        attributes.Add(new NodeAttribute("src_addr", new AttributeValue.Int64(snap.DmaSrcAddr)));
        attributes.Add(new NodeAttribute("dst_region", new AttributeValue.Int64(snap.DmaDstRegion)));
        attributes.Add(new NodeAttribute("dst_addr", new AttributeValue.Int64(snap.DmaDstAddr)));
        attributes.Add(new NodeAttribute("length", new AttributeValue.Int64(snap.DmaLen)));

        var dmaShape = new TensorShape(new TensorDimension[] { new TensorDimension.Fixed(snap.DmaLen) });

        TensorEdge srcEdge = ResolveInputEdge(
            snap.DmaSrcRegion,
            snap.DmaSrcAddr,
            snap.DmaLen,
            DataType.UInt8,
            dmaShape,
            writtenIntervals,
            allTensors,
            inputs,
            inputOffsets,
            outerInputs);

        inPorts.Add(new NodePort("src", new[] { srcEdge }));

        string dstName = string.Create(CultureInfo.InvariantCulture, $"r{snap.DmaDstRegion}@0x{snap.DmaDstAddr:X6}_{nodeId}");
        var dstEdge = new TensorEdge(dstName, new TensorDescriptor(DataType.UInt8, dmaShape), TensorKind.Activation);
        allTensors[dstName] = dstEdge;
        outPorts.Add(new NodePort("dst", new[] { dstEdge }));

        writtenIntervals.Add(new WrittenRegion(
            snap.DmaDstRegion,
            snap.DmaDstAddr,
            snap.DmaDstAddr + snap.DmaLen,
            nodeId,
            dstEdge));
    }

    private static TensorEdge ResolveInputEdge(
        int region,
        uint start,
        uint length,
        DataType dataType,
        TensorShape shape,
        List<WrittenRegion> writtenIntervals,
        Dictionary<string, TensorEdge> allTensors,
        List<GraphPort> inputs,
        IReadOnlyDictionary<uint, string>? inputOffsets,
        IReadOnlyList<GraphPort> outerInputs)
    {
        uint end = start + length;

        // Search backward for the most recent producer whose OFM overlapped this interval (last-writer-wins)
        for (int i = writtenIntervals.Count - 1; i >= 0; i--)
        {
            WrittenRegion writer = writtenIntervals[i];
            if (writer.Region == region && !(end <= writer.Start || start >= writer.End))
            {
                return writer.Edge;
            }
        }

        // Check if matching an outer input
        string inputName;
        if (inputOffsets != null && inputOffsets.TryGetValue(start, out string? mappedName))
        {
            inputName = mappedName;
        }
        else
        {
            inputName = string.Create(CultureInfo.InvariantCulture, $"in_r{region}@0x{start:X6}");
        }

        if (allTensors.TryGetValue(inputName, out TensorEdge? existingEdge))
        {
            return existingEdge;
        }

        var inEdge = new TensorEdge(inputName, new TensorDescriptor(dataType, shape), TensorKind.GraphInput);
        allTensors[inputName] = inEdge;

        var graphPort = outerInputs.FirstOrDefault(p => string.Equals(p.Name, inputName, StringComparison.Ordinal))
            ?? new GraphPort(inputName, new[] { inEdge });

        if (!inputs.Any(p => string.Equals(p.Name, graphPort.Name, StringComparison.Ordinal)))
        {
            inputs.Add(graphPort);
        }

        return inEdge;
    }

    private static string GetOperatorName(int opcode, int param) => opcode switch
    {
        EthosUOpcodeCatalog.NpuOpConv => "NPU_CONV",
        EthosUOpcodeCatalog.NpuOpDepthwise => "NPU_DEPTHWISE",
        EthosUOpcodeCatalog.NpuOpPool => "NPU_POOL",
        EthosUOpcodeCatalog.NpuOpElementwise => param switch
        {
            0 => "NPU_MUL",
            1 => "NPU_ADD",
            2 => "NPU_SUB",
            8 => "NPU_SHR",
            _ => "NPU_ELEMENTWISE",
        },
        EthosUOpcodeCatalog.NpuOpDmaStart => "NPU_DMA",
        _ => "NPU_OP",
    };

    private static OperatorCategory GetOperatorCategory(int opcode, int param) => opcode switch
    {
        EthosUOpcodeCatalog.NpuOpConv or EthosUOpcodeCatalog.NpuOpDepthwise => OperatorCategory.Layer,
        EthosUOpcodeCatalog.NpuOpPool => OperatorCategory.Pooling,
        EthosUOpcodeCatalog.NpuOpElementwise => OperatorCategory.Tensor,
        EthosUOpcodeCatalog.NpuOpDmaStart => OperatorCategory.Data,
        _ => OperatorCategory.Custom,
    };

    private static DataType MapPrecision(int precision)
    {
        int precCode = precision & 0x07;
        bool isSigned = (precision & 0x08) != 0 || (precision & 0x01) != 0;

        return precCode switch
        {
            0 => isSigned ? DataType.Int8 : DataType.UInt8,
            1 => DataType.Int8,
            2 => isSigned ? DataType.Int16 : DataType.UInt16,
            3 => isSigned ? DataType.Int32 : DataType.UInt32,
            _ => DataType.Int8,
        };
    }

    private static int GetElementByteSize(DataType type) => type switch
    {
        DataType.Int8 or DataType.UInt8 => 1,
        DataType.Int16 or DataType.UInt16 or DataType.Float16 or DataType.BFloat16 => 2,
        DataType.Int32 or DataType.UInt32 or DataType.Float32 => 4,
        DataType.Int64 or DataType.UInt64 or DataType.Float64 => 8,
        _ => 1,
    };
}

