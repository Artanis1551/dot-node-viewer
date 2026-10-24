using System.Buffers.Binary;
using System.Globalization;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.IO.FlatBuffers;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.ExecuTorch;

/// <summary>
/// Streams and parses ExecuTorch (<c>.pte</c>) FlatBuffer models into the Unified Graph IR.
/// </summary>
internal sealed class ExecuTorchFormatReader : IModelFormatReader
{
    /// <summary>
    /// Gets a shared singleton instance of <see cref="ExecuTorchFormatReader"/>.
    /// </summary>
    public static ExecuTorchFormatReader Instance { get; } = new();

    /// <inheritdoc/>
    public ModelFormatId FormatId => ModelFormatId.ExecuTorch;

    /// <inheritdoc/>
    public ValueTask<ModelGraph> ReadAsync(
        Stream stream,
        ModelReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        using RandomAccessSource source = new(stream);

        if (source.Length < 8)
        {
            throw new ModelParseException(
                $"Stream length ({source.Length} bytes) is too short to be an ExecuTorch file.",
                fileOffset: 0);
        }

        // Validate "ET12" or "ET1x" file identifier at offset 4..8
        Span<byte> idScratch = stackalloc byte[4];
        ReadOnlySpan<byte> id = source.GetSpan(4, 4, idScratch);
        bool isEt = id[0] == (byte)'E' && id[1] == (byte)'T' && id[2] == (byte)'1';
        if (!isEt)
        {
            throw new ModelParseException(
                "Invalid ExecuTorch file identifier at offset 0x4.",
                fileOffset: 4);
        }

        List<ParseDiagnostic> diagnostics = [];
        if (id[3] != (byte)'2')
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Warning,
                "DNV_EXECUTORCH_VERSION",
                $"ExecuTorch model uses schema version '{System.Text.Encoding.ASCII.GetString(id)}' (expected 'ET12').",
                "offset: 4"));
        }

        // Check optional extended header "eh00" at offset 8
        long segmentBaseOffset = 0;
        long programSize = 0;
        long segmentDataSize = 0;
        bool hasExtendedHeader = false;

        if (source.Length >= 40)
        {
            Span<byte> ehScratch = stackalloc byte[4];
            ReadOnlySpan<byte> ehMagic = source.GetSpan(8, 4, ehScratch);
            if (ehMagic.SequenceEqual("eh00"u8))
            {
                hasExtendedHeader = true;
                uint ehLength = source.ReadUInt32LittleEndian(12);
                programSize = source.ReadInt64LittleEndian(16);
                segmentBaseOffset = source.ReadInt64LittleEndian(24);
                segmentDataSize = ehLength >= 32 ? source.ReadInt64LittleEndian(32) : 0;
            }
        }

        // Read root Program FlatBuffer table
        FlatBufferTable programTable = FlatBufferTable.ReadRoot(source, bufferStartOffset: 0);
        uint version = programTable.ReadUInt32(0);

        // Read segments: program.segments: [DataSegment] (field 4)
        List<(long offset, long size)> segments = [];
        int segmentCount = programTable.GetVectorLength(4);
        for (int i = 0; i < segmentCount; i++)
        {
            FlatBufferTable segTable = programTable.GetVectorTable(4, i);
            long segOffset = unchecked((long)segTable.ReadUInt64(0));
            long segSize = unchecked((long)segTable.ReadUInt64(1));
            segments.Add((segOffset, segSize));
        }

        // Read constant_segment: SubsegmentOffsets (field 5)
        int constSegIdx = -1;
        long[]? constOffsets = null;
        if (programTable.ReadTable(5) is FlatBufferTable constSegTable)
        {
            constSegIdx = (int)constSegTable.ReadUInt32(0);
            constOffsets = constSegTable.ReadInt64Vector(1);
        }

        // Read execution plans: program.execution_plan: [ExecutionPlan] (field 1)
        int planCount = programTable.GetVectorLength(1);
        if (planCount == 0)
        {
            throw new ModelParseException("ExecuTorch model contains no execution plans.", fileOffset: programTable.TableOffset);
        }

        List<Subgraph> subgraphs = new(planCount);
        for (int p = 0; p < planCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlatBufferTable planTable = programTable.GetVectorTable(1, p);
            Subgraph subgraph = ParseExecutionPlan(
                planTable,
                p,
                programTable,
                segments,
                constSegIdx,
                constOffsets,
                segmentBaseOffset);
            subgraphs.Add(subgraph);
        }

        Dictionary<string, string> metadata = [];
        if (hasExtendedHeader)
        {
            metadata["executorch.extended_header"] = "eh00";
            metadata["executorch.program_size"] = programSize.ToString(CultureInfo.InvariantCulture);
            metadata["executorch.segment_base_offset"] = segmentBaseOffset.ToString(CultureInfo.InvariantCulture);
            metadata["executorch.segment_data_size"] = segmentDataSize.ToString(CultureInfo.InvariantCulture);
        }

        ModelFormatInfo formatInfo = new(
            ModelFormatId.ExecuTorch,
            "ExecuTorch",
            Version: version > 0 ? $"v{version.ToString(CultureInfo.InvariantCulture)}" : null);

        ModelGraph graph = new(
            formatInfo,
            Producer: "executorch",
            subgraphs,
            metadata,
            diagnostics);

        return ValueTask.FromResult(graph);
    }

    private static Subgraph ParseExecutionPlan(
        FlatBufferTable planTable,
        int planIndex,
        FlatBufferTable programTable,
        List<(long offset, long size)> segments,
        int constSegIdx,
        long[]? constOffsets,
        long segmentBaseOffset)
    {
        string planName = planTable.ReadString(0) ?? (planIndex == 0 ? "forward" : $"plan_{planIndex.ToString(CultureInfo.InvariantCulture)}");
        int[] planInputs = planTable.ReadInt32Vector(3) ?? [];
        int[] planOutputs = planTable.ReadInt32Vector(4) ?? [];

        HashSet<int> planInputSet = [.. planInputs];
        HashSet<int> planOutputSet = [.. planOutputs];

        // 1. Decode values: plan.values: [EValue] (field 2)
        int valCount = planTable.GetVectorLength(2);
        TensorEdge?[] tensorEdges = new TensorEdge?[valCount];
        AttributeValue?[] scalarValues = new AttributeValue?[valCount];
        int[]?[] tensorListIndices = new int[]?[valCount];

        for (int i = 0; i < valCount; i++)
        {
            FlatBufferTable valTable = planTable.GetVectorTable(2, i);
            byte valType = valTable.ReadByte(0);
            FlatBufferTable? innerTable = valTable.ReadTable(1);

            switch (valType)
            {
                case 2: // Int
                    if (innerTable is not null)
                    {
                        scalarValues[i] = new AttributeValue.Int64(innerTable.Value.ReadInt64(0));
                    }
                    break;

                case 3: // Bool
                    if (innerTable is not null)
                    {
                        scalarValues[i] = new AttributeValue.Bool(innerTable.Value.ReadBool(0));
                    }
                    break;

                case 4: // Double
                    if (innerTable is not null)
                    {
                        scalarValues[i] = new AttributeValue.Float64(innerTable.Value.ReadDouble(0));
                    }
                    break;

                case 6: // String
                    if (innerTable is not null)
                    {
                        string s = innerTable.Value.ReadString(0) ?? string.Empty;
                        scalarValues[i] = new AttributeValue.String(s);
                    }
                    break;

                case 7: // IntList
                    if (innerTable is not null)
                    {
                        long[]? items = innerTable.Value.ReadInt64Vector(0);
                        scalarValues[i] = new AttributeValue.Int64List(items ?? []);
                    }
                    break;

                case 8: // DoubleList
                    if (innerTable is not null)
                    {
                        double[]? items = innerTable.Value.ReadDoubleVector(0);
                        scalarValues[i] = new AttributeValue.Float64List(items ?? []);
                    }
                    break;

                case 9: // BoolList
                    if (innerTable is not null)
                    {
                        bool[]? items = innerTable.Value.ReadBoolVector(0);
                        scalarValues[i] = new AttributeValue.StringList((items ?? []).Select(b => b ? "true" : "false").ToArray());
                    }
                    break;

                case 10: // TensorList
                case 11: // OptionalTensorList
                    if (innerTable is not null)
                    {
                        tensorListIndices[i] = innerTable.Value.ReadInt32Vector(0);
                    }
                    break;

                case 5: // Tensor
                    if (innerTable is not null)
                    {
                        tensorEdges[i] = ParseTensor(
                            innerTable.Value,
                            i,
                            planInputSet.Contains(i),
                            planOutputSet.Contains(i),
                            programTable,
                            segments,
                            constSegIdx,
                            constOffsets,
                            segmentBaseOffset);
                    }
                    break;
            }
        }

        // 2. Operators catalog: plan.operators: [Operator] (field 6)
        int opCount = planTable.GetVectorLength(6);
        OperatorInfo[] operators = new OperatorInfo[opCount];
        for (int i = 0; i < opCount; i++)
        {
            FlatBufferTable opTable = planTable.GetVectorTable(6, i);
            string opName = opTable.ReadString(0) ?? "unknown";
            string? overload = opTable.ReadString(1);
            string fullOpName = string.IsNullOrEmpty(overload) ? opName : $"{opName}.{overload}";
            OperatorCategory category = ExecuTorchOperatorCatalog.Categorize(fullOpName);
            operators[i] = new OperatorInfo(fullOpName, category, Domain: "aten");
        }

        // 3. Delegates: plan.delegates: [BackendDelegate] (field 7)
        int delCount = planTable.GetVectorLength(7);

        // 4. Chains: plan.chains: [Chain] (field 5)
        int chainCount = planTable.GetVectorLength(5);
        List<GraphNode> nodes = [];
        HashSet<int> producedTensors = [];

        int nodeCounter = 0;
        for (int c = 0; c < chainCount; c++)
        {
            FlatBufferTable chainTable = planTable.GetVectorTable(5, c);
            int instructionCount = chainTable.GetVectorLength(2);

            for (int instIdx = 0; instIdx < instructionCount; instIdx++)
            {
                FlatBufferTable instTable = chainTable.GetVectorTable(2, instIdx);
                byte instrType = instTable.ReadByte(0);
                FlatBufferTable? callTable = instTable.ReadTable(1);

                if (callTable is null)
                {
                    continue;
                }

                switch (instrType)
                {
                    case 1: // KernelCall
                    {
                        int opIdx = callTable.Value.ReadInt32(0);
                        int[] args = callTable.Value.ReadInt32Vector(1) ?? [];
                        OperatorInfo opInfo = opIdx >= 0 && opIdx < operators.Length
                            ? operators[opIdx]
                            : new OperatorInfo("unknown", OperatorCategory.Unknown);

                        GraphNode node = BuildKernelNode(
                            opInfo,
                            args,
                            nodeCounter++,
                            tensorEdges,
                            scalarValues,
                            tensorListIndices,
                            planInputSet,
                            producedTensors);
                        nodes.Add(node);
                        break;
                    }

                    case 2: // DelegateCall
                    {
                        int delegateIdx = callTable.Value.ReadInt32(0);
                        int[] args = callTable.Value.ReadInt32Vector(1) ?? [];
                        GraphNode node = BuildDelegateNode(
                            planTable,
                            delegateIdx,
                            delCount,
                            args,
                            nodeCounter++,
                            programTable,
                            segments,
                            segmentBaseOffset,
                            tensorEdges,
                            planInputSet,
                            producedTensors);
                        nodes.Add(node);
                        break;
                    }

                    case 3: // MoveCall
                    {
                        int moveFrom = callTable.Value.ReadInt32(0);
                        int moveTo = callTable.Value.ReadInt32(1);
                        GraphNode node = BuildMoveNode(
                            moveFrom,
                            moveTo,
                            nodeCounter++,
                            tensorEdges,
                            producedTensors);
                        nodes.Add(node);
                        break;
                    }

                    case 4: // JumpFalseCall
                    {
                        int condValIdx = callTable.Value.ReadInt32(0);
                        int destInst = callTable.Value.ReadInt32(1);
                        GraphNode node = BuildJumpFalseNode(
                            condValIdx,
                            destInst,
                            nodeCounter++,
                            tensorEdges);
                        nodes.Add(node);
                        break;
                    }

                    case 5: // FreeCall - omitted from visual graph
                    default:
                        break;
                }
            }
        }

        // Build Subgraph Inputs and Outputs
        List<GraphPort> inputs = [];
        for (int i = 0; i < planInputs.Length; i++)
        {
            int valIdx = planInputs[i];
            if (valIdx >= 0 && valIdx < tensorEdges.Length && tensorEdges[valIdx] is TensorEdge edge)
            {
                inputs.Add(new GraphPort(edge.Name, [edge]));
            }
        }

        List<GraphPort> outputs = [];
        for (int i = 0; i < planOutputs.Length; i++)
        {
            int valIdx = planOutputs[i];
            if (valIdx >= 0 && valIdx < tensorEdges.Length && tensorEdges[valIdx] is TensorEdge edge)
            {
                outputs.Add(new GraphPort(edge.Name, [edge]));
            }
        }

        List<TensorEdge> allTensors = tensorEdges.Where(t => t is not null).Cast<TensorEdge>().ToList();

        return new Subgraph(
            planName,
            nodes,
            allTensors,
            inputs,
            outputs);
    }

    private static TensorEdge ParseTensor(
        FlatBufferTable tensorTable,
        int valIdx,
        bool isPlanInput,
        bool isPlanOutput,
        FlatBufferTable programTable,
        List<(long offset, long size)> segments,
        int constSegIdx,
        long[]? constOffsets,
        long segmentBaseOffset)
    {
        sbyte scalarType = tensorTable.ReadSByte(0);
        DataType dataType = ExecuTorchOperatorCatalog.MapScalarType(scalarType);
        int[]? sizes = tensorTable.ReadInt32Vector(2);
        uint dataBufferIdx = tensorTable.ReadUInt32(5);
        byte dynamism = tensorTable.ReadByte(8);
        FlatBufferTable? extraInfo = tensorTable.ReadTable(9);

        string? fqn = extraInfo?.ReadString(1);
        string tensorName = !string.IsNullOrWhiteSpace(fqn) ? fqn : $"v{valIdx.ToString(CultureInfo.InvariantCulture)}";

        long numel = 1;
        TensorShape? shape = null;
        if (sizes is not null)
        {
            TensorDimension[] dims = new TensorDimension[sizes.Length];
            for (int d = 0; d < sizes.Length; d++)
            {
                if (sizes[d] > 0)
                {
                    numel *= sizes[d];
                }

                dims[d] = (dynamism == 2 || sizes[d] < 0)
                    ? new TensorDimension.Dynamic()
                    : new TensorDimension.Fixed(sizes[d]);
            }
            shape = new TensorShape(dims);
        }

        TensorDataSource? dataSource = null;
        byte location = extraInfo?.ReadByte(2) ?? 0; // 0: SEGMENT, 1: EXTERNAL

        if (location == 1 && !string.IsNullOrWhiteSpace(fqn))
        {
            long byteLen = numel * ExecuTorchOperatorCatalog.GetElementByteSize(dataType);
            dataSource = new ExternalTensorDataReference($"{fqn}.ptd", 0, byteLen, dataType);
        }
        else if (dataBufferIdx > 0)
        {
            // Case A: Inline constant_buffer
            int constBufCount = programTable.GetVectorLength(2);
            if (constBufCount > 0 && (int)dataBufferIdx < constBufCount)
            {
                FlatBufferTable bufTable = programTable.GetVectorTable(2, (int)dataBufferIdx);
                dataSource = bufTable.CaptureByteVectorSlice(0, dataType);
            }
            else if (constOffsets is not null && (int)dataBufferIdx < constOffsets.Length)
            {
                // Case B: Segmented constant
                long subOffset = constOffsets[(int)dataBufferIdx];
                long segOffset = (constSegIdx >= 0 && constSegIdx < segments.Count) ? segments[constSegIdx].offset : 0;
                long streamOffset = segmentBaseOffset + segOffset + subOffset;
                long byteLen = numel * ExecuTorchOperatorCatalog.GetElementByteSize(dataType);
                if (byteLen <= 0 && constSegIdx >= 0 && constSegIdx < segments.Count)
                {
                    byteLen = segments[constSegIdx].size;
                }
                dataSource = new TensorDataSlice(streamOffset, byteLen, dataType, TensorDataEncoding.Raw);
            }
        }

        TensorKind kind;
        if (dataBufferIdx > 0)
        {
            kind = TensorKind.Initializer;
        }
        else if (isPlanInput)
        {
            kind = TensorKind.GraphInput;
        }
        else if (isPlanOutput)
        {
            kind = TensorKind.GraphOutput;
        }
        else
        {
            kind = TensorKind.Activation;
        }

        TensorDescriptor desc = new(dataType, shape);
        return new TensorEdge(tensorName, desc, kind, dataSource);
    }

    private static GraphNode BuildKernelNode(
        OperatorInfo opInfo,
        int[] args,
        int nodeIndex,
        TensorEdge?[] tensorEdges,
        AttributeValue?[] scalarValues,
        int[]?[] tensorListIndices,
        HashSet<int> planInputSet,
        HashSet<int> producedTensors)
    {
        string nodeName = $"{opInfo.Name}_{nodeIndex.ToString(CultureInfo.InvariantCulture)}";
        List<NodePort> inputs = [];
        List<NodePort> outputs = [];
        List<NodeAttribute> attributes = [];

        int inPortIdx = 0;
        int outPortIdx = 0;
        HashSet<int> seenOutIndices = [];

        for (int a = 0; a < args.Length; a++)
        {
            int valIdx = args[a];
            if (valIdx < 0 || valIdx >= tensorEdges.Length)
            {
                continue;
            }

            if (scalarValues[valIdx] is AttributeValue scalar)
            {
                attributes.Add(new NodeAttribute($"arg{a.ToString(CultureInfo.InvariantCulture)}", scalar));
            }
            else if (tensorListIndices[valIdx] is int[] listIndices)
            {
                List<TensorEdge> listTensors = [];
                for (int li = 0; li < listIndices.Length; li++)
                {
                    int tidx = listIndices[li];
                    if (tidx >= 0 && tidx < tensorEdges.Length && tensorEdges[tidx] is TensorEdge tedge)
                    {
                        listTensors.Add(tedge);
                    }
                }
                if (listTensors.Count > 0)
                {
                    inputs.Add(new NodePort($"in{inPortIdx++.ToString(CultureInfo.InvariantCulture)}", listTensors));
                }
            }
            else if (tensorEdges[valIdx] is TensorEdge edge)
            {
                bool isInput = edge.Kind == TensorKind.Initializer
                    || planInputSet.Contains(valIdx)
                    || producedTensors.Contains(valIdx);

                if (isInput)
                {
                    string pName = edge.Kind == TensorKind.Initializer
                        ? $"weight_{inPortIdx++.ToString(CultureInfo.InvariantCulture)}"
                        : $"in_{inPortIdx++.ToString(CultureInfo.InvariantCulture)}";
                    inputs.Add(new NodePort(pName, [edge]));
                }
                else
                {
                    if (seenOutIndices.Add(valIdx))
                    {
                        outputs.Add(new NodePort($"out_{outPortIdx++.ToString(CultureInfo.InvariantCulture)}", [edge]));
                        producedTensors.Add(valIdx);
                    }
                }
            }
        }

        return new GraphNode(
            nodeName,
            opInfo,
            inputs,
            outputs,
            attributes);
    }

    private static GraphNode BuildDelegateNode(
        FlatBufferTable planTable,
        int delegateIdx,
        int delCount,
        int[] args,
        int nodeIndex,
        FlatBufferTable programTable,
        List<(long offset, long size)> segments,
        long segmentBaseOffset,
        TensorEdge?[] tensorEdges,
        HashSet<int> planInputSet,
        HashSet<int> producedTensors)
    {
        string backendId = "UnknownBackend";
        List<NodeAttribute> attributes = [];

        if (delegateIdx >= 0 && delegateIdx < delCount)
        {
            FlatBufferTable delTable = planTable.GetVectorTable(7, delegateIdx);
            backendId = delTable.ReadString(0) ?? "UnknownBackend";

            if (backendId.Contains("EthosU", StringComparison.OrdinalIgnoreCase))
            {
                attributes.Add(new NodeAttribute("delegate.backend", new AttributeValue.String("ethos-u")));
                attributes.Add(new NodeAttribute("delegate.expandable", new AttributeValue.Bool(true)));
            }

            // Extract processed delegate data slice
            if (delTable.ReadTable(1) is FlatBufferTable refTable)
            {
                byte loc = refTable.ReadByte(0); // 0: INLINE, 1: SEGMENT
                uint refIdx = refTable.ReadUInt32(1);

                if (loc == 0) // INLINE
                {
                    int inlineCount = programTable.GetVectorLength(3);
                    if (inlineCount > 0 && (int)refIdx < inlineCount)
                    {
                        FlatBufferTable inlineTable = programTable.GetVectorTable(3, (int)refIdx);
                        TensorDataSlice? slice = inlineTable.CaptureByteVectorSlice(0, DataType.UInt8);
                        if (slice is not null)
                        {
                            attributes.Add(new NodeAttribute("processed", new AttributeValue.Bytes(slice)));
                        }
                    }
                }
                else if (loc == 1 && (int)refIdx < segments.Count) // SEGMENT
                {
                    var (segOffset, segSize) = segments[(int)refIdx];
                    TensorDataSlice slice = new(segmentBaseOffset + segOffset, segSize, DataType.UInt8, TensorDataEncoding.Raw);
                    attributes.Add(new NodeAttribute("processed", new AttributeValue.Bytes(slice)));
                }
            }

            // Extract compile specs: delTable.compile_specs: [CompileSpec] (field 2)
            int specCount = delTable.GetVectorLength(2);
            for (int s = 0; s < specCount; s++)
            {
                FlatBufferTable specTable = delTable.GetVectorTable(2, s);
                string key = specTable.ReadString(0) ?? $"spec_{s.ToString(CultureInfo.InvariantCulture)}";
                TensorDataSlice? specSlice = specTable.CaptureByteVectorSlice(1, DataType.UInt8);
                if (specSlice is not null)
                {
                    attributes.Add(new NodeAttribute($"compile_spec.{key}", new AttributeValue.Bytes(specSlice)));
                }
            }
        }

        string opName = $"Delegate ({backendId})";
        OperatorInfo opInfo = new(opName, OperatorCategory.Custom, Domain: backendId);
        string nodeName = $"{backendId}_{nodeIndex.ToString(CultureInfo.InvariantCulture)}";

        List<NodePort> inputs = [];
        List<NodePort> outputs = [];
        HashSet<int> seenOut = [];
        int inIdx = 0;
        int outIdx = 0;

        for (int a = 0; a < args.Length; a++)
        {
            int valIdx = args[a];
            if (valIdx < 0 || valIdx >= tensorEdges.Length || tensorEdges[valIdx] is not TensorEdge edge)
            {
                continue;
            }

            bool isInput = edge.Kind == TensorKind.Initializer
                || planInputSet.Contains(valIdx)
                || producedTensors.Contains(valIdx);

            if (isInput)
            {
                inputs.Add(new NodePort($"in_{inIdx++.ToString(CultureInfo.InvariantCulture)}", [edge]));
            }
            else
            {
                if (seenOut.Add(valIdx))
                {
                    outputs.Add(new NodePort($"out_{outIdx++.ToString(CultureInfo.InvariantCulture)}", [edge]));
                    producedTensors.Add(valIdx);
                }
            }
        }

        return new GraphNode(
            nodeName,
            opInfo,
            inputs,
            outputs,
            attributes);
    }

    private static GraphNode BuildMoveNode(
        int moveFrom,
        int moveTo,
        int nodeIndex,
        TensorEdge?[] tensorEdges,
        HashSet<int> producedTensors)
    {
        string nodeName = $"move_{nodeIndex.ToString(CultureInfo.InvariantCulture)}";
        OperatorInfo opInfo = new("executorch_prim::move", OperatorCategory.Tensor);

        List<NodePort> inputs = [];
        if (moveFrom >= 0 && moveFrom < tensorEdges.Length && tensorEdges[moveFrom] is TensorEdge src)
        {
            inputs.Add(new NodePort("from", [src]));
        }

        List<NodePort> outputs = [];
        if (moveTo >= 0 && moveTo < tensorEdges.Length && tensorEdges[moveTo] is TensorEdge dst)
        {
            outputs.Add(new NodePort("to", [dst]));
            producedTensors.Add(moveTo);
        }

        return new GraphNode(nodeName, opInfo, inputs, outputs, []);
    }

    private static GraphNode BuildJumpFalseNode(
        int condValIdx,
        int destinationInstruction,
        int nodeIndex,
        TensorEdge?[] tensorEdges)
    {
        string nodeName = $"jump_false_{nodeIndex.ToString(CultureInfo.InvariantCulture)}";
        OperatorInfo opInfo = new("executorch_prim::jump_false", OperatorCategory.ControlFlow);

        List<NodePort> inputs = [];
        if (condValIdx >= 0 && condValIdx < tensorEdges.Length && tensorEdges[condValIdx] is TensorEdge condEdge)
        {
            inputs.Add(new NodePort("cond", [condEdge]));
        }

        List<NodeAttribute> attributes =
        [
            new NodeAttribute("destination_instruction", new AttributeValue.Int64(destinationInstruction))
        ];

        return new GraphNode(nodeName, opInfo, inputs, [], attributes);
    }
}

