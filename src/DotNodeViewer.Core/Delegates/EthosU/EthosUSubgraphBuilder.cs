using System.Globalization;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Delegates.EthosU;

/// <summary>
/// Maps an outer delegate input or output tensor to its hardware memory region and byte range.
/// </summary>
/// <param name="Region">The memory region identifier (e.g., <c>1</c> for SRAM arena).</param>
/// <param name="Offset">The base byte offset within the memory region.</param>
/// <param name="ByteLength">The byte length of the tensor.</param>
/// <param name="OuterTensor">The corresponding outer graph <see cref="TensorEdge"/>.</param>
internal sealed record EthosUIoRegion(int Region, uint Offset, uint ByteLength, TensorEdge OuterTensor);

/// <summary>
/// Builds a synthesized <see cref="Subgraph"/> from decoded Ethos-U register snapshots and driver payload metadata.
/// </summary>
internal static class EthosUSubgraphBuilder
{
    internal readonly record struct ByteInterval(uint Start, uint End);

    private sealed record WrittenRegion(int Region, ByteInterval[] Intervals, string NodeId, TensorEdge Edge);

    /// <summary>
    /// Projects decoded register snapshots into a Unified Graph IR <see cref="Subgraph"/>.
    /// </summary>
    /// <param name="subgraphName">The name for the synthesized subgraph (for example, <c>"CUSTOM(ethos-u)_0 (Ethos-U55-128)"</c>).</param>
    /// <param name="driverPayload">The parsed Ethos-U driver action payload.</param>
    /// <param name="snapshots">The immutable register snapshots captured at each operation dispatch.</param>
    /// <param name="weightSliceBaseOffset">The absolute stream offset of the flash/weight constant buffer.</param>
    /// <param name="weightSliceByteLength">The total byte length of the flash/weight constant buffer.</param>
    /// <param name="outerInputs">The runtime input ports of the delegate node from the outer graph.</param>
    /// <param name="outerOutputs">The runtime output ports of the delegate node from the outer graph.</param>
    /// <param name="inputRegions">Memory region/offset descriptors mapped to outer input tensors.</param>
    /// <param name="outputRegions">Memory region/offset descriptors mapped to outer output tensors.</param>
    /// <returns>A fully materialized, immutable <see cref="Subgraph"/> representing the internal NPU dataflow.</returns>
    public static Subgraph Build(
        string subgraphName,
        EthosUDriverPayload driverPayload,
        IReadOnlyList<EthosURegisterSnapshot> snapshots,
        long weightSliceBaseOffset,
        long weightSliceByteLength,
        IReadOnlyList<GraphPort> outerInputs,
        IReadOnlyList<GraphPort> outerOutputs,
        IReadOnlyList<EthosUIoRegion>? inputRegions = null,
        IReadOnlyList<EthosUIoRegion>? outputRegions = null)
    {
        var nodes = new List<GraphNode>();
        var allTensors = new Dictionary<string, TensorEdge>(StringComparer.Ordinal);
        var inputs = new List<GraphPort>();
        var outputs = new List<GraphPort>();
        var writtenRegions = new List<WrittenRegion>();

        int opIndex = 0;
        foreach (EthosURegisterSnapshot snap in snapshots)
        {
            if (!EthosUOpcodeCatalog.IsExecutableOperation(snap.Opcode))
            {
                continue;
            }

            string opName = GetOperatorName(snap.Opcode, snap.Param);
            OperatorCategory category = GetOperatorCategory(snap.Opcode, snap.Param);
            string nodeId = string.Create(CultureInfo.InvariantCulture, $"op_{opIndex}");

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
                    opIndex,
                    nodeId,
                    inPorts,
                    outPorts,
                    attributes,
                    allTensors,
                    inputs,
                    writtenRegions,
                    weightSliceBaseOffset,
                    weightSliceByteLength,
                    inputRegions);
            }
            else
            {
                BuildKernelNode(
                    snap,
                    driverPayload,
                    opIndex,
                    nodeId,
                    inPorts,
                    outPorts,
                    attributes,
                    allTensors,
                    inputs,
                    writtenRegions,
                    weightSliceBaseOffset,
                    weightSliceByteLength,
                    inputRegions);
            }

            nodes.Add(new GraphNode(
                nodeId,
                new OperatorInfo(opName, category, "ai.arm.ethos-u", 1),
                inPorts,
                outPorts,
                attributes));

            opIndex++;
        }

        // Resolve formal graph outputs from the final written intervals
        if (outputRegions is { Count: > 0 })
        {
            foreach (EthosUIoRegion outReg in outputRegions)
            {
                uint end = unchecked(outReg.Offset + outReg.ByteLength);
                if (end < outReg.Offset)
                {
                    end = uint.MaxValue;
                }

                (List<TensorEdge> matched, _) = ResolveProducers(
                    outReg.Region,
                    [new ByteInterval(outReg.Offset, end)],
                    writtenRegions);

                if (matched.Count > 0)
                {
                    outputs.Add(new GraphPort(outReg.OuterTensor.Name, matched));
                }
                else
                {
                    allTensors.TryAdd(outReg.OuterTensor.Name, outReg.OuterTensor);
                    outputs.Add(new GraphPort(outReg.OuterTensor.Name, new[] { outReg.OuterTensor }));
                }
            }
        }
        else if (nodes.Count > 0)
        {
            // Fallback when no region mapping is available (e.g. synthetic unit tests)
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
        }

        // Only append fallback outer inputs when no region mapping was supplied (e.g. synthetic tests)
        if (inputRegions is null || inputRegions.Count == 0)
        {
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
        }

        return new Subgraph(subgraphName, nodes, allTensors.Values.ToList(), inputs, outputs);
    }

    private static void BuildKernelNode(
        EthosURegisterSnapshot snap,
        EthosUDriverPayload driverPayload,
        int opIndex,
        string nodeId,
        List<NodePort> inPorts,
        List<NodePort> outPorts,
        List<NodeAttribute> attributes,
        Dictionary<string, TensorEdge> allTensors,
        List<GraphPort> inputs,
        List<WrittenRegion> writtenRegions,
        long weightSliceBaseOffset,
        long weightSliceByteLength,
        IReadOnlyList<EthosUIoRegion>? inputRegions)
    {
        DataType ifmType = MapIfmPrecision(snap.IfmPrecision);
        DataType ofmType = MapOfmPrecision(snap.OfmPrecision);
        bool ifmNhcwb16 = IsNhcwb16(snap.IfmPrecision);
        bool ofmNhcwb16 = IsNhcwb16(snap.OfmPrecision);

        int ifmBytesPerElem = GetElementByteSize(ifmType);
        int ofmBytesPerElem = GetElementByteSize(ofmType);

        ByteInterval[] ifmIntervals = ComputeByteIntervals(
            snap.IfmBase0,
            snap.IfmHeight,
            snap.IfmWidth,
            snap.IfmDepth,
            snap.IfmStrideX,
            snap.IfmStrideY,
            snap.IfmStrideC,
            ifmBytesPerElem,
            ifmNhcwb16);

        ByteInterval[] ofmIntervals = ComputeByteIntervals(
            snap.OfmBase0,
            snap.OfmHeight,
            snap.OfmWidth,
            snap.OfmDepth,
            snap.OfmStrideX,
            snap.OfmStrideY,
            snap.OfmStrideC,
            ofmBytesPerElem,
            ofmNhcwb16);

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
        attributes.Add(new NodeAttribute("ifm_dtype", new AttributeValue.DataTypeValue(ifmType)));
        attributes.Add(new NodeAttribute("ofm_dtype", new AttributeValue.DataTypeValue(ofmType)));
        attributes.Add(new NodeAttribute("ifm_layout", new AttributeValue.String(ifmNhcwb16 ? "NHCWB16" : "NHWC")));
        attributes.Add(new NodeAttribute("ofm_layout", new AttributeValue.String(ofmNhcwb16 ? "NHCWB16" : "NHWC")));
        attributes.Add(new NodeAttribute("ifm_zero_point", new AttributeValue.Int64(snap.IfmZeroPoint)));
        attributes.Add(new NodeAttribute("ofm_zero_point", new AttributeValue.Int64(snap.OfmZeroPoint)));
        attributes.Add(new NodeAttribute("block_config", new AttributeValue.Int64List(new long[] { snap.BlockHeight, snap.BlockWidth, snap.BlockDepth })));

        if (snap.Opcode != EthosUOpcodeCatalog.NpuOpElementwise)
        {
            attributes.Add(new NodeAttribute("kernel", new AttributeValue.Int64List(new long[] { snap.KernelHeight, snap.KernelWidth })));
            attributes.Add(new NodeAttribute("strides", new AttributeValue.Int64List(new long[] { snap.KernelStrideY, snap.KernelStrideX })));
            if (snap.DilationX > 1 || snap.DilationY > 1)
            {
                attributes.Add(new NodeAttribute("dilations", new AttributeValue.Int64List(new long[] { snap.DilationY, snap.DilationX })));
            }
            attributes.Add(new NodeAttribute("padding", new AttributeValue.Int64List(new long[] { snap.PadTop, snap.PadLeft, snap.PadBottom, snap.PadRight })));
        }

        int actCode = snap.Activation & 0xFF;
        string? activationName = FormatActivationName(actCode, ofmType, snap.ActivationMin, snap.ActivationMax);
        if (activationName is not null)
        {
            attributes.Add(new NodeAttribute("activation", new AttributeValue.String(activationName)));
            attributes.Add(new NodeAttribute("clamp_min", new AttributeValue.Int64(snap.ActivationMin)));
            attributes.Add(new NodeAttribute("clamp_max", new AttributeValue.Int64(snap.ActivationMax)));
        }

        // 1. Resolve IFM dataflow edge(s)
        IReadOnlyList<TensorEdge> ifmEdges = ResolveInputEdges(
            snap.IfmRegion,
            snap.IfmBase0,
            ifmIntervals,
            ifmType,
            ifmShape,
            "const",
            opIndex,
            TensorDataEncoding.Raw,
            writtenRegions,
            allTensors,
            inputs,
            weightSliceBaseOffset,
            weightSliceByteLength,
            inputRegions);

        inPorts.Add(new NodePort("ifm", ifmEdges));

        // 2. Resolve IFM2 for binary non-scalar elementwise ops
        if (snap.Opcode == EthosUOpcodeCatalog.NpuOpElementwise)
        {
            if (snap.Ifm2Broadcast != 0)
            {
                attributes.Add(new NodeAttribute("ifm2_broadcast", new AttributeValue.String(FormatBroadcastFlags(snap.Ifm2Broadcast))));
            }

            bool isUnary = EthosUOpcodeCatalog.IsUnaryElementwise(snap.Param);
            bool isScalarIfm2 = (snap.Ifm2Broadcast & 0x80) != 0;

            if (isScalarIfm2)
            {
                attributes.Add(new NodeAttribute("ifm2_scalar", new AttributeValue.Int64(snap.Ifm2Scalar)));
            }
            else if (!isUnary)
            {
                DataType ifm2Type = MapIfmPrecision(snap.Ifm2Precision);
                bool ifm2Nhcwb16 = IsNhcwb16(snap.Ifm2Precision);
                int ifm2BytesPerElem = GetElementByteSize(ifm2Type);
                int ifm2Depth = (snap.Ifm2Broadcast & 0x04) != 0 ? 1 : snap.IfmDepth;

                ByteInterval[] ifm2Intervals = ComputeByteIntervals(
                    snap.Ifm2Base0,
                    snap.Ifm2Height,
                    snap.Ifm2Width,
                    ifm2Depth,
                    snap.Ifm2StrideX,
                    snap.Ifm2StrideY,
                    snap.Ifm2StrideC,
                    ifm2BytesPerElem,
                    ifm2Nhcwb16);

                var ifm2Shape = new TensorShape(new TensorDimension[]
                {
                    new TensorDimension.Fixed(1),
                    new TensorDimension.Fixed(snap.Ifm2Height),
                    new TensorDimension.Fixed(snap.Ifm2Width),
                    new TensorDimension.Fixed(ifm2Depth)
                });

                IReadOnlyList<TensorEdge> ifm2Edges = ResolveInputEdges(
                    snap.Ifm2Region,
                    snap.Ifm2Base0,
                    ifm2Intervals,
                    ifm2Type,
                    ifm2Shape,
                    "const",
                    opIndex,
                    TensorDataEncoding.Raw,
                    writtenRegions,
                    allTensors,
                    inputs,
                    weightSliceBaseOffset,
                    weightSliceByteLength,
                    inputRegions);

                inPorts.Add(new NodePort("ifm2", ifm2Edges));
            }
        }

        // 3. Resolve Weight / Scale Initializers (ONLY for Conv / Depthwise!)
        if (snap.Opcode is EthosUOpcodeCatalog.NpuOpConv or EthosUOpcodeCatalog.NpuOpDepthwise)
        {
            if (snap.WeightLength > 0)
            {
                IReadOnlyList<TensorEdge> weightEdges = ResolveWeightOrScaleEdges(
                    snap.WeightRegion,
                    snap.WeightBase,
                    snap.WeightLength,
                    "weights",
                    opIndex,
                    writtenRegions,
                    allTensors,
                    weightSliceBaseOffset,
                    weightSliceByteLength);

                if (weightEdges.Count > 0)
                {
                    inPorts.Add(new NodePort("weights", weightEdges));
                }
            }

            if (snap.ScaleLength > 0)
            {
                IReadOnlyList<TensorEdge> scaleEdges = ResolveWeightOrScaleEdges(
                    snap.ScaleRegion,
                    snap.ScaleBase,
                    snap.ScaleLength,
                    "scales",
                    opIndex,
                    writtenRegions,
                    allTensors,
                    weightSliceBaseOffset,
                    weightSliceByteLength);

                if (scaleEdges.Count > 0)
                {
                    inPorts.Add(new NodePort("scales", scaleEdges));
                }
            }
        }

        // 4. Resolve LUT dependency when activation uses SHRAM lookup table (0x10..0x17)
        if (actCode is >= 0x10 and <= 0x17)
        {
            uint lutSlotAddr = driverPayload.ShramSizeKb >= 2
                ? (uint)(driverPayload.ShramSizeKb * 1024 - 2048 + (actCode - 0x10) * 256)
                : 0u;

            (List<TensorEdge> lutEdges, _) = ResolveProducers(
                EthosUOpcodeCatalog.BasePointerIndexMem2Mem,
                [new ByteInterval(lutSlotAddr, lutSlotAddr + 256)],
                writtenRegions);

            if (lutEdges.Count == 0)
            {
                // Fallback to the most recent SHRAM LUT DMA write
                for (int i = writtenRegions.Count - 1; i >= 0; i--)
                {
                    if (writtenRegions[i].Region == EthosUOpcodeCatalog.BasePointerIndexMem2Mem)
                    {
                        lutEdges.Add(writtenRegions[i].Edge);
                        break;
                    }
                }
            }

            if (lutEdges.Count > 0)
            {
                inPorts.Add(new NodePort("lut", lutEdges));
            }
        }

        // 5. Create uniquely named OFM Edge and record written intervals AFTER resolving inputs
        string ofmName = FormatUniqueTensorName(snap.OfmRegion, snap.OfmBase0, opIndex);
        var ofmEdge = new TensorEdge(ofmName, new TensorDescriptor(ofmType, ofmShape), TensorKind.Activation);
        allTensors[ofmName] = ofmEdge;
        outPorts.Add(new NodePort("ofm", new[] { ofmEdge }));

        writtenRegions.Add(new WrittenRegion(
            snap.OfmRegion,
            ofmIntervals,
            nodeId,
            ofmEdge));
    }

    private static void BuildDmaNode(
        EthosURegisterSnapshot snap,
        int opIndex,
        string nodeId,
        List<NodePort> inPorts,
        List<NodePort> outPorts,
        List<NodeAttribute> attributes,
        Dictionary<string, TensorEdge> allTensors,
        List<GraphPort> inputs,
        List<WrittenRegion> writtenRegions,
        long weightSliceBaseOffset,
        long weightSliceByteLength,
        IReadOnlyList<EthosUIoRegion>? inputRegions)
    {
        string purpose = snap.DmaDstRegion == EthosUOpcodeCatalog.BasePointerIndexMem2Mem
            ? "LUT -> SHRAM"
            : (snap.DmaSrcRegion == 0 ? "Flash -> SRAM" : "Memory Transfer");

        attributes.Add(new NodeAttribute("dma_purpose", new AttributeValue.String(purpose)));
        attributes.Add(new NodeAttribute("src_region", new AttributeValue.String(FormatRegionAttribute(snap.DmaSrcRegion))));
        attributes.Add(new NodeAttribute("src_addr", new AttributeValue.String(string.Create(CultureInfo.InvariantCulture, $"0x{snap.DmaSrcAddr:X6}"))));
        attributes.Add(new NodeAttribute("dst_region", new AttributeValue.String(FormatRegionAttribute(snap.DmaDstRegion))));
        attributes.Add(new NodeAttribute("dst_addr", new AttributeValue.String(string.Create(CultureInfo.InvariantCulture, $"0x{snap.DmaDstAddr:X6}"))));
        attributes.Add(new NodeAttribute("length", new AttributeValue.Int64(snap.DmaLen)));

        var dmaShape = new TensorShape(new TensorDimension[] { new TensorDimension.Fixed(snap.DmaLen) });
        uint srcEnd = unchecked(snap.DmaSrcAddr + snap.DmaLen);
        if (srcEnd < snap.DmaSrcAddr)
        {
            srcEnd = uint.MaxValue;
        }

        IReadOnlyList<TensorEdge> srcEdges = ResolveInputEdges(
            snap.DmaSrcRegion,
            snap.DmaSrcAddr,
            [new ByteInterval(snap.DmaSrcAddr, srcEnd)],
            DataType.UInt8,
            dmaShape,
            "flash",
            opIndex,
            TensorDataEncoding.EthosUCompressedWeights,
            writtenRegions,
            allTensors,
            inputs,
            weightSliceBaseOffset,
            weightSliceByteLength,
            inputRegions);

        inPorts.Add(new NodePort("src", srcEdges));

        string dstName = FormatUniqueTensorName(snap.DmaDstRegion, snap.DmaDstAddr, opIndex);
        var dstEdge = new TensorEdge(dstName, new TensorDescriptor(DataType.UInt8, dmaShape), TensorKind.Activation);
        allTensors[dstName] = dstEdge;
        outPorts.Add(new NodePort("dst", new[] { dstEdge }));

        uint dstEnd = unchecked(snap.DmaDstAddr + snap.DmaLen);
        if (dstEnd < snap.DmaDstAddr)
        {
            dstEnd = uint.MaxValue;
        }

        writtenRegions.Add(new WrittenRegion(
            snap.DmaDstRegion,
            [new ByteInterval(snap.DmaDstAddr, dstEnd)],
            nodeId,
            dstEdge));
    }

    private static IReadOnlyList<TensorEdge> ResolveWeightOrScaleEdges(
        int region,
        uint baseAddr,
        uint length,
        string prefix,
        int opIndex,
        List<WrittenRegion> writtenRegions,
        Dictionary<string, TensorEdge> allTensors,
        long weightSliceBaseOffset,
        long weightSliceByteLength)
    {
        uint end = unchecked(baseAddr + length);
        if (end < baseAddr)
        {
            end = uint.MaxValue;
        }

        if (region != 0)
        {
            (List<TensorEdge> matched, _) = ResolveProducers(region, [new ByteInterval(baseAddr, end)], writtenRegions);
            if (matched.Count > 0)
            {
                return matched;
            }

            return Array.Empty<TensorEdge>();
        }

        TensorDataSlice? slice = TryCreateFlashSlice(baseAddr, length, DataType.UInt8, TensorDataEncoding.EthosUCompressedWeights, weightSliceBaseOffset, weightSliceByteLength);
        var descriptor = new TensorDescriptor(
            DataType.UInt8,
            new TensorShape(new TensorDimension[] { new TensorDimension.Fixed(length) }));

        string edgeName = CreateUniqueConstantName(prefix, baseAddr, opIndex, descriptor, allTensors);
        if (!allTensors.TryGetValue(edgeName, out TensorEdge? edge))
        {
            edge = new TensorEdge(edgeName, descriptor, TensorKind.Initializer, slice);
            allTensors[edgeName] = edge;
        }

        return new[] { edge };
    }

    private static IReadOnlyList<TensorEdge> ResolveInputEdges(
        int region,
        uint baseAddr,
        ByteInterval[] queryIntervals,
        DataType dataType,
        TensorShape shape,
        string flashPrefix,
        int opIndex,
        TensorDataEncoding flashEncoding,
        List<WrittenRegion> writtenRegions,
        Dictionary<string, TensorEdge> allTensors,
        List<GraphPort> inputs,
        long weightSliceBaseOffset,
        long weightSliceByteLength,
        IReadOnlyList<EthosUIoRegion>? inputRegions)
    {
        // 1. Resolve against previously written intervals (multi-writer last-writer-wins)
        (List<TensorEdge> matched, List<ByteInterval> uncovered) = ResolveProducers(region, queryIntervals, writtenRegions);

        // 2. Flash region (Region 0) reads are constant initializers/LUTs, never GraphInputs
        if (region == 0)
        {
            if (matched.Count > 0)
            {
                return matched;
            }

            uint maxEnd = baseAddr;
            for (int i = 0; i < queryIntervals.Length; i++)
            {
                if (queryIntervals[i].End > maxEnd)
                {
                    maxEnd = queryIntervals[i].End;
                }
            }

            long sliceByteLen = Math.Max(0L, (long)maxEnd - baseAddr);
            TensorDataSlice? slice = TryCreateFlashSlice(baseAddr, sliceByteLen, dataType, flashEncoding, weightSliceBaseOffset, weightSliceByteLength);
            var descriptor = new TensorDescriptor(dataType, shape);
            string constName = CreateUniqueConstantName(flashPrefix, baseAddr, opIndex, descriptor, allTensors);

            if (!allTensors.TryGetValue(constName, out TensorEdge? constEdge))
            {
                constEdge = new TensorEdge(constName, descriptor, TensorKind.Constant, slice);
                allTensors[constName] = constEdge;
            }

            return new[] { constEdge };
        }

        // 3. Match uncovered ranges against outer delegate input regions
        if (uncovered.Count > 0 && inputRegions is { Count: > 0 })
        {
            for (int i = 0; i < inputRegions.Count; i++)
            {
                EthosUIoRegion inReg = inputRegions[i];
                if (inReg.Region != region)
                {
                    continue;
                }

                uint regEnd = unchecked(inReg.Offset + inReg.ByteLength);
                if (regEnd < inReg.Offset)
                {
                    regEnd = uint.MaxValue;
                }

                (bool overlapped, List<ByteInterval> remaining) = SubtractIntervals(
                    uncovered,
                    [new ByteInterval(inReg.Offset, regEnd)]);

                if (overlapped)
                {
                    if (!allTensors.TryGetValue(inReg.OuterTensor.Name, out TensorEdge? inEdge))
                    {
                        inEdge = new TensorEdge(
                            inReg.OuterTensor.Name,
                            inReg.OuterTensor.Descriptor,
                            TensorKind.GraphInput,
                            inReg.OuterTensor.Data);
                        allTensors[inReg.OuterTensor.Name] = inEdge;
                    }

                    if (!inputs.Any(p => string.Equals(p.Name, inEdge.Name, StringComparison.Ordinal)))
                    {
                        inputs.Add(new GraphPort(inEdge.Name, new[] { inEdge }));
                    }

                    if (!matched.Contains(inEdge))
                    {
                        matched.Insert(0, inEdge);
                    }

                    uncovered = remaining;
                    if (uncovered.Count == 0)
                    {
                        break;
                    }
                }
            }
        }

        if (matched.Count > 0)
        {
            return matched;
        }

        // 4. Fallback GraphInput when no prior writer and no region mapping matched
        string fallbackName = string.Create(CultureInfo.InvariantCulture, $"{EthosUOpcodeCatalog.FormatRegionName(region)}@0x{baseAddr:X}");
        if (!allTensors.TryGetValue(fallbackName, out TensorEdge? fallbackEdge))
        {
            fallbackEdge = new TensorEdge(fallbackName, new TensorDescriptor(dataType, shape), TensorKind.GraphInput);
            allTensors[fallbackName] = fallbackEdge;
        }

        if (!inputs.Any(p => string.Equals(p.Name, fallbackName, StringComparison.Ordinal)))
        {
            inputs.Add(new GraphPort(fallbackName, new[] { fallbackEdge }));
        }

        return new[] { fallbackEdge };
    }

    internal static ByteInterval[] ComputeByteIntervals(
        uint baseAddr,
        int height,
        int width,
        int depth,
        uint strideX,
        uint strideY,
        uint strideC,
        int elemBytes,
        bool isNhcwb16,
        int maxStripes = 64)
    {
        int h = Math.Max(1, height);
        int w = Math.Max(1, width);
        int c = Math.Max(1, depth);
        uint elem = (uint)Math.Max(1, elemBytes);

        if (strideX == 0 && strideY == 0 && strideC == 0)
        {
            ulong denseLen = (ulong)h * (ulong)w * (ulong)c * elem;
            uint denseEnd = baseAddr + (uint)Math.Min(denseLen, uint.MaxValue - baseAddr);
            return [new ByteInterval(baseAddr, denseEnd)];
        }

        if (isNhcwb16)
        {
            uint brickBytes = 16u * elem;
            int bricks = (c + 15) / 16;
            ulong span = (ulong)(h - 1) * strideY
                + (ulong)(w - 1) * strideX
                + (ulong)(bricks - 1) * strideC
                + brickBytes;
            uint nhcwbEnd = baseAddr + (uint)Math.Min(span, uint.MaxValue - baseAddr);
            return [new ByteInterval(baseAddr, nhcwbEnd)];
        }

        // NHWC strided tensor: coalesce contiguous inner dimensions and enumerate outer stripes if bounded
        var axes = new List<(int Dim, uint Stride)>(3);
        if (c > 1)
        {
            axes.Add((c, strideC));
        }
        if (w > 1)
        {
            axes.Add((w, strideX));
        }
        if (h > 1)
        {
            axes.Add((h, strideY));
        }

        axes.Sort((a, b) => a.Stride.CompareTo(b.Stride));

        ulong runLen = elem;
        var outerAxes = new List<(int Dim, uint Stride)>(3);
        for (int i = 0; i < axes.Count; i++)
        {
            (int dim, uint stride) = axes[i];
            if (stride <= runLen)
            {
                runLen = (ulong)(dim - 1) * stride + runLen;
            }
            else
            {
                outerAxes.Add((dim, stride));
            }
        }

        long totalStripes = 1;
        for (int i = 0; i < outerAxes.Count; i++)
        {
            totalStripes *= outerAxes[i].Dim;
            if (totalStripes > maxStripes)
            {
                break;
            }
        }

        if (totalStripes <= maxStripes)
        {
            var offsets = new List<ulong>(checked((int)totalStripes)) { baseAddr };
            for (int a = 0; a < outerAxes.Count; a++)
            {
                (int dim, uint stride) = outerAxes[a];
                var nextOffsets = new List<ulong>(offsets.Count * dim);
                for (int o = 0; o < offsets.Count; o++)
                {
                    ulong oBase = offsets[o];
                    for (int k = 0; k < dim; k++)
                    {
                        nextOffsets.Add(oBase + (ulong)k * stride);
                    }
                }
                offsets = nextOffsets;
            }

            var intervals = new List<ByteInterval>(offsets.Count);
            for (int i = 0; i < offsets.Count; i++)
            {
                ulong s = offsets[i];
                ulong e = s + runLen;
                uint clampedS = (uint)Math.Min(s, uint.MaxValue);
                uint clampedE = (uint)Math.Min(e, uint.MaxValue);
                if (clampedE > clampedS)
                {
                    intervals.Add(new ByteInterval(clampedS, clampedE));
                }
            }

            if (intervals.Count > 0)
            {
                intervals.Sort((a, b) =>
                {
                    int cmp = a.Start.CompareTo(b.Start);
                    return cmp != 0 ? cmp : a.End.CompareTo(b.End);
                });

                var merged = new List<ByteInterval>(intervals.Count) { intervals[0] };
                for (int i = 1; i < intervals.Count; i++)
                {
                    ByteInterval prev = merged[^1];
                    ByteInterval cur = intervals[i];
                    if (cur.Start <= prev.End)
                    {
                        merged[^1] = new ByteInterval(prev.Start, Math.Max(prev.End, cur.End));
                    }
                    else
                    {
                        merged.Add(cur);
                    }
                }

                return merged.ToArray();
            }
        }

        ulong boundSpan = (ulong)(h - 1) * strideY
            + (ulong)(w - 1) * strideX
            + (ulong)(c - 1) * strideC
            + elem;
        uint boundEnd = baseAddr + (uint)Math.Min(boundSpan, uint.MaxValue - baseAddr);
        return [new ByteInterval(baseAddr, boundEnd)];
    }

    private static (List<TensorEdge> Matched, List<ByteInterval> Uncovered) ResolveProducers(
        int region,
        ByteInterval[] queryIntervals,
        List<WrittenRegion> writtenRegions)
    {
        var uncovered = new List<ByteInterval>(queryIntervals);
        var matched = new List<TensorEdge>();
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);

        for (int i = writtenRegions.Count - 1; i >= 0; i--)
        {
            WrittenRegion writer = writtenRegions[i];
            if (writer.Region != region)
            {
                continue;
            }

            (bool overlapped, List<ByteInterval> remaining) = SubtractIntervals(uncovered, writer.Intervals);
            if (overlapped)
            {
                uncovered = remaining;
                if (seenNodes.Add(writer.NodeId))
                {
                    matched.Add(writer.Edge);
                }

                if (uncovered.Count == 0)
                {
                    break;
                }
            }
        }

        matched.Reverse();
        return (matched, uncovered);
    }

    private static (bool HadOverlap, List<ByteInterval> Remaining) SubtractIntervals(
        List<ByteInterval> uncovered,
        ByteInterval[] written)
    {
        bool hadOverlap = false;
        var remaining = new List<ByteInterval>(uncovered.Count);

        for (int u = 0; u < uncovered.Count; u++)
        {
            uint cur = uncovered[u].Start;
            uint ue = uncovered[u].End;

            for (int w = 0; w < written.Length; w++)
            {
                uint ws = written[w].Start;
                uint we = written[w].End;
                if (we <= cur)
                {
                    continue;
                }
                if (ws >= ue)
                {
                    break;
                }

                hadOverlap = true;
                if (ws > cur)
                {
                    remaining.Add(new ByteInterval(cur, ws));
                }

                cur = Math.Max(cur, we);
                if (cur >= ue)
                {
                    break;
                }
            }

            if (cur < ue)
            {
                remaining.Add(new ByteInterval(cur, ue));
            }
        }

        return (hadOverlap, remaining);
    }

    private static TensorDataSlice? TryCreateFlashSlice(
        uint baseAddr,
        long byteLength,
        DataType dataType,
        TensorDataEncoding encoding,
        long weightSliceBaseOffset,
        long weightSliceByteLength)
    {
        if (weightSliceBaseOffset <= 0 || byteLength <= 0)
        {
            return null;
        }

        if (weightSliceByteLength > 0 && (long)baseAddr + byteLength > weightSliceByteLength)
        {
            return null;
        }

        return new TensorDataSlice(
            weightSliceBaseOffset + baseAddr,
            byteLength,
            dataType,
            encoding);
    }

    private static string CreateUniqueConstantName(
        string prefix,
        uint baseAddr,
        int opIndex,
        TensorDescriptor descriptor,
        Dictionary<string, TensorEdge> allTensors)
    {
        string baseName = string.Create(CultureInfo.InvariantCulture, $"{prefix}@0x{baseAddr:X}");
        if (!allTensors.TryGetValue(baseName, out TensorEdge? existing))
        {
            return baseName;
        }

        if (existing.Descriptor.DataType == descriptor.DataType &&
            Equals(existing.Descriptor.Shape, descriptor.Shape))
        {
            return baseName;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{baseName}#op{opIndex}");
    }

    private static string FormatUniqueTensorName(int region, uint baseAddr, int opIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"{EthosUOpcodeCatalog.FormatRegionName(region)}@0x{baseAddr:X}#op{opIndex}");

    private static string FormatRegionAttribute(int region) => region switch
    {
        0 => "flash (r0)",
        1 => "sram (r1)",
        2 => "sram_fast (r2)",
        EthosUOpcodeCatalog.BasePointerIndexMem2Mem => "shram_lut (0x103)",
        _ => string.Create(CultureInfo.InvariantCulture, $"r{region}"),
    };

    private static string? FormatActivationName(int actCode, DataType ofmType, int actMin, int actMax)
    {
        switch (actCode)
        {
            case 0x03:
                return "TANH";
            case 0x04:
                return "SIGMOID";
            case >= 0x10 and <= 0x17:
                return string.Create(CultureInfo.InvariantCulture, $"LUT[{actCode - 0x10}]");
            case 0x00:
            {
                (long typeMin, long typeMax) = ofmType switch
                {
                    DataType.UInt8 => (0L, 255L),
                    DataType.Int8 => (-128L, 127L),
                    DataType.UInt16 => (0L, 65535L),
                    DataType.Int16 => (-32768L, 32767L),
                    _ => (long.MinValue, long.MaxValue),
                };

                if (actMin > typeMin || actMax < typeMax)
                {
                    return "CLIP";
                }

                return null;
            }
            default:
                return string.Create(CultureInfo.InvariantCulture, $"ACT(0x{actCode:X2})");
        }
    }

    private static string FormatBroadcastFlags(int flags)
    {
        var parts = new List<string>(5);
        if ((flags & 0x01) != 0)
        {
            parts.Add("H");
        }
        if ((flags & 0x02) != 0)
        {
            parts.Add("W");
        }
        if ((flags & 0x04) != 0)
        {
            parts.Add("C");
        }
        if ((flags & 0x40) != 0)
        {
            parts.Add("reversed");
        }
        if ((flags & 0x80) != 0)
        {
            parts.Add("scalar");
        }

        return parts.Count > 0 ? string.Join("|", parts) : string.Create(CultureInfo.InvariantCulture, $"0x{flags:X}");
    }

    private static string GetOperatorName(int opcode, int param) => opcode switch
    {
        EthosUOpcodeCatalog.NpuOpConv => "NPU_CONV",
        EthosUOpcodeCatalog.NpuOpDepthwise => "NPU_DEPTHWISE",
        EthosUOpcodeCatalog.NpuOpPool => param switch
        {
            0 => "NPU_MAXPOOL",
            1 => "NPU_AVGPOOL",
            2 => "NPU_REDUCE_SUM",
            _ => "NPU_POOL",
        },
        EthosUOpcodeCatalog.NpuOpElementwise => param switch
        {
            0 => "NPU_MUL",
            1 => "NPU_ADD",
            2 => "NPU_SUB",
            3 => "NPU_MIN",
            4 => "NPU_MAX",
            5 => "NPU_LRELU",
            6 => "NPU_ABS",
            7 => "NPU_CLZ",
            8 => "NPU_SHR",
            9 => "NPU_SHL",
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

    internal static DataType MapIfmPrecision(int precision)
    {
        bool isSigned = (precision & 0x01) != 0;
        int sizeCode = (precision >> 2) & 0x03;

        return sizeCode switch
        {
            0 => isSigned ? DataType.Int8 : DataType.UInt8,
            1 => isSigned ? DataType.Int16 : DataType.UInt16,
            2 => isSigned ? DataType.Int32 : DataType.UInt32,
            _ => DataType.Int8,
        };
    }

    internal static DataType MapOfmPrecision(int precision)
    {
        bool isSigned = (precision & 0x01) != 0;
        int sizeCode = (precision >> 1) & 0x03;

        return sizeCode switch
        {
            0 => isSigned ? DataType.Int8 : DataType.UInt8,
            1 => isSigned ? DataType.Int16 : DataType.UInt16,
            2 => isSigned ? DataType.Int32 : DataType.UInt32,
            _ => DataType.Int8,
        };
    }

    private static bool IsNhcwb16(int precision) => (precision & 0x40) != 0;

    internal static int GetElementByteSize(DataType type) => type switch
    {
        DataType.Int8 or DataType.UInt8 => 1,
        DataType.Int16 or DataType.UInt16 or DataType.Float16 or DataType.BFloat16 => 2,
        DataType.Int32 or DataType.UInt32 or DataType.Float32 => 4,
        DataType.Int64 or DataType.UInt64 or DataType.Float64 => 8,
        _ => 1,
    };
}
