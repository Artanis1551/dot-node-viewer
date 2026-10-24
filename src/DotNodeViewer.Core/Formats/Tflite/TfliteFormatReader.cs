using System.Globalization;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.IO.FlatBuffers;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Tflite;

/// <summary>
/// Reads a TensorFlow Lite (<c>TFL3</c>) FlatBuffers binary stream into the Unified Graph IR
/// while recording raw weight buffers and custom delegate payloads as lazy <see cref="TensorDataSlice"/> ranges.
/// </summary>
internal sealed class TfliteFormatReader : IModelFormatReader
{
    /// <summary>
    /// Gets a shared singleton instance of <see cref="TfliteFormatReader"/>.
    /// </summary>
    public static TfliteFormatReader Instance { get; } = new();

    /// <inheritdoc/>
    public ModelFormatId FormatId => ModelFormatId.Tflite;

    /// <inheritdoc/>
    public ValueTask<ModelGraph> ReadAsync(
        Stream stream,
        ModelReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        ModelReadOptions effectiveOptions = options ?? ModelReadOptions.Default;
        using RandomAccessSource source = new(stream);

        FlatBufferTable rootModel = FlatBufferTable.ReadRoot(
            source,
            bufferStartOffset: 0,
            expectedIdentifier: "TFL3"u8);

        ModelGraph graph = ParseModel(source, rootModel, effectiveOptions, cancellationToken);
        return ValueTask.FromResult(graph);
    }

    private static ModelGraph ParseModel(
        RandomAccessSource source,
        FlatBufferTable modelTable,
        ModelReadOptions options,
        CancellationToken cancellationToken)
    {
        // Field 0: version: uint
        uint version = modelTable.ReadUInt32(0, defaultValue: 3);
        if (version == 0 || version > 100)
        {
            throw new ModelParseException(
                $"Unsupported TensorFlow Lite schema version ({version}).",
                fileOffset: modelTable.TableOffset,
                context: "Model.version");
        }

        // Field 3: description: string
        string? description = modelTable.ReadString(3);

        // Field 1: operator_codes: [OperatorCode]
        List<ParsedOperatorCode> operatorCodes = ParseOperatorCodes(modelTable, cancellationToken);

        // Field 4: buffers: [Buffer]
        List<BufferSliceInfo> buffers = ParseBuffers(source, modelTable, cancellationToken);

        // Field 2: subgraphs: [SubGraph]
        List<RawSubgraph> rawSubgraphs = ParseRawSubgraphs(
            modelTable,
            operatorCodes,
            buffers,
            cancellationToken);

        // Build Subgraph IR instances (resolving nested IF / WHILE / CALL_ONCE subgraph attributes).
        List<Subgraph> builtSubgraphs = new(rawSubgraphs.Count);
        HashSet<int> buildStack = [];
        for (int i = 0; i < rawSubgraphs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builtSubgraphs.Add(BuildSubgraphRecursive(
                i,
                rawSubgraphs,
                options.MaxSubgraphDepth,
                currentDepth: 0,
                buildStack));
        }

        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["schema_version"] = version.ToString(CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrWhiteSpace(description))
        {
            metadata["description"] = description;
        }

        // Field 6: metadata: [Metadata] (optional key-buffer metadata entries)
        ExtractModelMetadataEntries(modelTable, buffers, metadata);

        ModelFormatInfo formatInfo = new(
            ModelFormatId.Tflite,
            "TensorFlow Lite",
            $"v{version.ToString(CultureInfo.InvariantCulture)}");

        string? producer = string.IsNullOrWhiteSpace(description) ? null : description;

        return new ModelGraph(
            Format: formatInfo,
            Producer: producer,
            Subgraphs: builtSubgraphs,
            Metadata: metadata,
            Diagnostics: []);
    }

    private static List<ParsedOperatorCode> ParseOperatorCodes(
        FlatBufferTable modelTable,
        CancellationToken cancellationToken)
    {
        int count = modelTable.GetVectorLength(1);
        if (count == 0)
        {
            return [];
        }

        List<ParsedOperatorCode> codes = new(count);
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlatBufferTable opCodeTable = modelTable.GetVectorTable(1, i);

            // Field 0: deprecated_builtin_code: byte (sbyte)
            sbyte deprecatedCode = opCodeTable.ReadSByte(0, defaultValue: 0);

            // Field 1: custom_code: string
            string? customCode = opCodeTable.ReadString(1);

            // Field 2: version: int = 1
            int version = opCodeTable.ReadInt32(2, defaultValue: 1);

            // Field 3: builtin_code: BuiltinOperator = 0 (int32)
            int builtinCode = opCodeTable.ReadInt32(3, defaultValue: 0);

            int resolvedCode = builtinCode != 0
                ? builtinCode
                : (deprecatedCode >= 0 ? deprecatedCode : (byte)deprecatedCode);

            codes.Add(new ParsedOperatorCode(resolvedCode, customCode, version));
        }

        return codes;
    }

    private static List<BufferSliceInfo> ParseBuffers(
        RandomAccessSource source,
        FlatBufferTable modelTable,
        CancellationToken cancellationToken)
    {
        int count = modelTable.GetVectorLength(4);
        if (count == 0)
        {
            return [];
        }

        List<BufferSliceInfo> buffers = new(count);
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlatBufferTable bufTable = modelTable.GetVectorTable(4, i);

            // Field 0: data: [ubyte] — capture stream offset & byte length WITHOUT reading payload bytes!
            TensorDataSlice? inlineSlice = bufTable.CaptureByteVectorSlice(0, DataType.UInt8);
            if (inlineSlice is not null && inlineSlice.ByteLength > 0)
            {
                buffers.Add(new BufferSliceInfo(inlineSlice.StreamOffset, inlineSlice.ByteLength));
                continue;
            }

            // Field 1: offset: ulong = 0, Field 2: size: ulong = 0 (TFLite v3c appended external-in-file buffers)
            ulong extOffset = bufTable.ReadUInt64(1, defaultValue: 0);
            ulong extSize = bufTable.ReadUInt64(2, defaultValue: 0);
            if (extOffset > 0 && extSize > 0)
            {
                if (extOffset > (ulong)source.Length || extSize > (ulong)source.Length - extOffset)
                {
                    throw new ModelParseException(
                        $"TFLite Buffer[{i}] external offset ({extOffset}) + size ({extSize}) exceeds stream length ({source.Length}).",
                        fileOffset: bufTable.TableOffset,
                        context: $"Model.buffers[{i}]");
                }

                buffers.Add(new BufferSliceInfo((long)extOffset, (long)extSize));
                continue;
            }

            buffers.Add(default);
        }

        return buffers;
    }

    private static List<RawSubgraph> ParseRawSubgraphs(
        FlatBufferTable modelTable,
        List<ParsedOperatorCode> operatorCodes,
        List<BufferSliceInfo> buffers,
        CancellationToken cancellationToken)
    {
        int count = modelTable.GetVectorLength(2);
        if (count == 0)
        {
            return [];
        }

        List<RawSubgraph> subgraphs = new(count);
        for (int s = 0; s < count; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlatBufferTable sgTable = modelTable.GetVectorTable(2, s);
            subgraphs.Add(ParseSingleRawSubgraph(sgTable, s, operatorCodes, buffers, cancellationToken));
        }

        return subgraphs;
    }

    private static RawSubgraph ParseSingleRawSubgraph(
        FlatBufferTable sgTable,
        int subgraphIndex,
        List<ParsedOperatorCode> operatorCodes,
        List<BufferSliceInfo> buffers,
        CancellationToken cancellationToken)
    {
        // Field 4: name: string
        string? rawName = sgTable.ReadString(4);
        string name = !string.IsNullOrWhiteSpace(rawName)
            ? rawName
            : (subgraphIndex == 0 ? "main" : $"subgraph_{subgraphIndex.ToString(CultureInfo.InvariantCulture)}");

        // Field 1: inputs: [int]
        int[] inputIndices = sgTable.ReadInt32Vector(1) ?? [];
        HashSet<int> inputIndexSet = new(inputIndices);

        // Field 2: outputs: [int]
        int[] outputIndices = sgTable.ReadInt32Vector(2) ?? [];
        HashSet<int> outputIndexSet = new(outputIndices);

        // Field 0: tensors: [Tensor]
        int tensorCount = sgTable.GetVectorLength(0);
        List<TensorEdge> tensors = new(tensorCount);
        for (int t = 0; t < tensorCount; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlatBufferTable tensorTable = sgTable.GetVectorTable(0, t);

            tensors.Add(ParseTensor(
                tensorTable,
                t,
                buffers,
                inputIndexSet.Contains(t),
                outputIndexSet.Contains(t)));
        }

        // Validate subgraph input and output tensor indices.
        List<GraphPort> graphInputs = new(inputIndices.Length);
        for (int i = 0; i < inputIndices.Length; i++)
        {
            int idx = inputIndices[i];
            if (idx < 0)
            {
                continue;
            }

            if (idx >= tensors.Count)
            {
                throw new ModelParseException(
                    $"SubGraph[{subgraphIndex}] input index ({idx}) is out of range for tensor table of size {tensors.Count}.",
                    fileOffset: sgTable.TableOffset,
                    context: $"SubGraph[{subgraphIndex}].inputs[{i}]");
            }

            TensorEdge edge = tensors[idx];
            if (edge.Kind != TensorKind.Initializer && edge.Kind != TensorKind.Constant)
            {
                graphInputs.Add(new GraphPort(edge.Name, [edge]));
            }
        }

        List<GraphPort> graphOutputs = new(outputIndices.Length);
        for (int i = 0; i < outputIndices.Length; i++)
        {
            int idx = outputIndices[i];
            if (idx < 0)
            {
                continue;
            }

            if (idx >= tensors.Count)
            {
                throw new ModelParseException(
                    $"SubGraph[{subgraphIndex}] output index ({idx}) is out of range for tensor table of size {tensors.Count}.",
                    fileOffset: sgTable.TableOffset,
                    context: $"SubGraph[{subgraphIndex}].outputs[{i}]");
            }

            TensorEdge edge = tensors[idx];
            graphOutputs.Add(new GraphPort(edge.Name, [edge]));
        }

        // Field 3: operators: [Operator]
        int opCount = sgTable.GetVectorLength(3);
        List<RawOperator> operators = new(opCount);
        for (int o = 0; o < opCount; o++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlatBufferTable opTable = sgTable.GetVectorTable(3, o);

            operators.Add(ParseOperator(
                opTable,
                subgraphIndex,
                o,
                operatorCodes,
                tensors));
        }

        return new RawSubgraph(name, tensors, graphInputs, graphOutputs, operators);
    }

    private static TensorEdge ParseTensor(
        FlatBufferTable tensorTable,
        int tensorIndex,
        List<BufferSliceInfo> buffers,
        bool isSubgraphInput,
        bool isSubgraphOutput)
    {
        // Field 1: type: TensorType (byte, default 0 = FLOAT32)
        byte rawType = tensorTable.ReadByte(1, defaultValue: 0);
        DataType dataType = TfliteOperatorCatalog.MapDataType(rawType);

        // Field 0: shape: [int], Field 7: shape_signature: [int], Field 8: has_rank: bool
        TensorShape? shape = ParseTensorShape(tensorTable);

        // Field 2: buffer: uint
        uint bufferIndex = tensorTable.ReadUInt32(2, defaultValue: 0);
        if (bufferIndex >= (uint)buffers.Count && buffers.Count > 0)
        {
            throw new ModelParseException(
                $"Tensor[{tensorIndex}] references out-of-range buffer index {bufferIndex} (buffers count: {buffers.Count}).",
                fileOffset: tensorTable.TableOffset,
                context: $"Tensor[{tensorIndex}].buffer");
        }

        // Field 3: name: string
        string? rawName = tensorTable.ReadString(3);
        string name = !string.IsNullOrEmpty(rawName)
            ? rawName
            : $"tensor_{tensorIndex.ToString(CultureInfo.InvariantCulture)}";

        // Field 4: quantization: QuantizationParameters
        AffineQuantization? quantization = ParseQuantization(tensorTable);

        // Field 5: is_variable: bool = false
        bool isVariable = tensorTable.ReadBool(5, defaultValue: false);

        BufferSliceInfo sliceInfo = bufferIndex < (uint)buffers.Count ? buffers[(int)bufferIndex] : default;
        TensorDataSource? dataSource = null;
        TensorKind kind;

        if (sliceInfo.HasData)
        {
            dataSource = new TensorDataSlice(
                StreamOffset: sliceInfo.StreamOffset,
                ByteLength: sliceInfo.ByteLength,
                DataType: dataType,
                Encoding: TensorDataEncoding.Raw);
            kind = TensorKind.Initializer;
        }
        else if (isVariable)
        {
            kind = TensorKind.Initializer;
        }
        else if (isSubgraphInput)
        {
            kind = TensorKind.GraphInput;
        }
        else if (isSubgraphOutput)
        {
            kind = TensorKind.GraphOutput;
        }
        else
        {
            kind = TensorKind.Activation;
        }

        TensorDescriptor descriptor = new(
            DataType: dataType,
            Shape: shape,
            Quantization: quantization);

        return new TensorEdge(
            Name: name,
            Descriptor: descriptor,
            Kind: kind,
            Data: dataSource);
    }

    private static TensorShape? ParseTensorShape(FlatBufferTable tensorTable)
    {
        int[]? shapeDims = tensorTable.ReadInt32Vector(0);
        int[]? sigDims = tensorTable.ReadInt32Vector(7);
        bool hasRankFlag = tensorTable.ReadBool(8, defaultValue: false);

        if (shapeDims is null && sigDims is null)
        {
            return hasRankFlag ? TensorShape.Scalar : null;
        }

        int[] activeShape = shapeDims ?? [];
        int[] activeSig = sigDims ?? [];
        int rank = activeSig.Length == activeShape.Length && activeSig.Length > 0
            ? activeSig.Length
            : activeShape.Length;

        if (rank == 0)
        {
            return TensorShape.Scalar;
        }

        TensorDimension[] dimensions = new TensorDimension[rank];
        for (int i = 0; i < rank; i++)
        {
            int sigVal = i < activeSig.Length ? activeSig[i] : activeShape[i];
            int dimVal = i < activeShape.Length ? activeShape[i] : sigVal;

            if (sigVal < 0 || dimVal < 0)
            {
                dimensions[i] = TensorDimension.Dynamic.Instance;
            }
            else
            {
                dimensions[i] = new TensorDimension.Fixed(dimVal);
            }
        }

        return new TensorShape(dimensions);
    }

    private static AffineQuantization? ParseQuantization(FlatBufferTable tensorTable)
    {
        FlatBufferTable? quantTableOpt = tensorTable.ReadTable(4);
        if (quantTableOpt is not FlatBufferTable quantTable)
        {
            return null;
        }

        // Field 2: scale: [float]
        float[]? scales = quantTable.ReadSingleVector(2);
        if (scales is null || scales.Length == 0)
        {
            return null;
        }

        // Field 3: zero_point: [long]
        long[] zeroPoints = quantTable.ReadInt64Vector(3) ?? [];

        // Field 6: quantized_dimension: int = 0
        int? quantizedDimension = (scales.Length > 1 || quantTable.HasField(6))
            ? quantTable.ReadInt32(6, defaultValue: 0)
            : null;

        return new AffineQuantization(
            Scale: scales,
            ZeroPoint: zeroPoints,
            QuantizedDimension: quantizedDimension);
    }

    private static RawOperator ParseOperator(
        FlatBufferTable opTable,
        int subgraphIndex,
        int operatorIndex,
        List<ParsedOperatorCode> operatorCodes,
        List<TensorEdge> tensors)
    {
        // Field 0: opcode_index: uint
        uint opcodeIndex = opTable.ReadUInt32(0, defaultValue: 0);
        if (opcodeIndex >= (uint)operatorCodes.Count && operatorCodes.Count > 0)
        {
            throw new ModelParseException(
                $"SubGraph[{subgraphIndex}].operators[{operatorIndex}] references out-of-range opcode_index {opcodeIndex} (operator_codes count: {operatorCodes.Count}).",
                fileOffset: opTable.TableOffset,
                context: $"SubGraph[{subgraphIndex}].operators[{operatorIndex}].opcode_index");
        }

        ParsedOperatorCode opCode = opcodeIndex < (uint)operatorCodes.Count
            ? operatorCodes[(int)opcodeIndex]
            : new ParsedOperatorCode(0, null, 1);

        string opType = TfliteOperatorCatalog.GetOperatorName(opCode.BuiltinCode, opCode.CustomCode);
        OperatorCategory category = TfliteOperatorCatalog.Categorize(opCode.BuiltinCode);
        string domain = opCode.BuiltinCode == 32 && !string.IsNullOrEmpty(opCode.CustomCode)
            ? "tflite.custom"
            : "tflite";
        OperatorInfo opInfo = new(opType, category, domain, opCode.Version);

        string nodeName = $"{opType}_{operatorIndex.ToString(CultureInfo.InvariantCulture)}";

        // Field 1: inputs: [int]
        int[] inputIndices = opTable.ReadInt32Vector(1) ?? [];
        List<NodePort> inputs = new(inputIndices.Length);
        for (int i = 0; i < inputIndices.Length; i++)
        {
            int tensorIdx = inputIndices[i];
            string portName = TfliteOperatorCatalog.GetInputPortName(opCode.BuiltinCode, i);
            if (tensorIdx < 0)
            {
                // -1 indicates an omitted optional input tensor in TFLite.
                inputs.Add(new NodePort(portName, []));
                continue;
            }

            if (tensorIdx >= tensors.Count)
            {
                throw new ModelParseException(
                    $"Operator '{nodeName}' input[{i}] references out-of-range tensor index {tensorIdx} (tensors count: {tensors.Count}).",
                    fileOffset: opTable.TableOffset,
                    context: $"SubGraph[{subgraphIndex}].operators[{operatorIndex}].inputs[{i}]");
            }

            inputs.Add(new NodePort(portName, [tensors[tensorIdx]]));
        }

        // Field 2: outputs: [int]
        int[] outputIndices = opTable.ReadInt32Vector(2) ?? [];
        List<NodePort> outputs = new(outputIndices.Length);
        for (int i = 0; i < outputIndices.Length; i++)
        {
            int tensorIdx = outputIndices[i];
            string portName = TfliteOperatorCatalog.GetOutputPortName(opCode.BuiltinCode, i);
            if (tensorIdx < 0)
            {
                outputs.Add(new NodePort(portName, []));
                continue;
            }

            if (tensorIdx >= tensors.Count)
            {
                throw new ModelParseException(
                    $"Operator '{nodeName}' output[{i}] references out-of-range tensor index {tensorIdx} (tensors count: {tensors.Count}).",
                    fileOffset: opTable.TableOffset,
                    context: $"SubGraph[{subgraphIndex}].operators[{operatorIndex}].outputs[{i}]");
            }

            outputs.Add(new NodePort(portName, [tensors[tensorIdx]]));
        }

        List<RawAttribute> attributes = [];

        // If CUSTOM operator, record custom_code and custom_options lazy byte slice.
        if (opCode.BuiltinCode == 32 && !string.IsNullOrEmpty(opCode.CustomCode))
        {
            attributes.Add(new RawAttribute("custom_code", new AttributeValue.String(opCode.CustomCode), null));
            if (string.Equals(opCode.CustomCode, "ethos-u", StringComparison.OrdinalIgnoreCase))
            {
                attributes.Add(new RawAttribute("delegate.backend", new AttributeValue.String("ethos-u"), null));
                attributes.Add(new RawAttribute("delegate.expandable", new AttributeValue.Bool(true), null));
            }
        }

        // Field 3: builtin_options_type: BuiltinOptions (ubyte), Field 4: builtin_options (Table)
        byte builtinOptionsType = opTable.ReadByte(3, defaultValue: 0);
        if (opTable.ReadUnionTable(4) is FlatBufferTable optionsTable)
        {
            ExtractBuiltinOptions(opCode.BuiltinCode, builtinOptionsType, optionsTable, attributes);
        }

        // Field 5: custom_options: [ubyte] — record as lazy AttributeValue.Bytes(TensorDataSlice)
        TensorDataSlice? customSlice = opTable.CaptureByteVectorSlice(5, DataType.UInt8);
        if (customSlice is not null && customSlice.ByteLength > 0)
        {
            attributes.Add(new RawAttribute("custom_options", new AttributeValue.Bytes(customSlice), null));
        }

        return new RawOperator(nodeName, opInfo, inputs, outputs, attributes);
    }

    private static void ExtractBuiltinOptions(
        int builtinCode,
        byte builtinOptionsType,
        FlatBufferTable optionsTable,
        List<RawAttribute> attributes)
    {
        switch (builtinCode)
        {
            case 3: // CONV_2D (Conv2DOptions = 1)
                AddPaddingAttribute(optionsTable, 0, attributes);
                AddStrideAttributes(optionsTable, strideWField: 1, strideHField: 2, attributes);
                AddFusedActivationAttribute(optionsTable, 3, attributes);
                AddDilationAttributes(optionsTable, dilationWField: 4, dilationHField: 5, attributes);
                break;

            case 4: // DEPTHWISE_CONV_2D (DepthwiseConv2DOptions = 2)
                AddPaddingAttribute(optionsTable, 0, attributes);
                AddStrideAttributes(optionsTable, strideWField: 1, strideHField: 2, attributes);
                int depthMultiplier = optionsTable.ReadInt32(3, defaultValue: 1);
                attributes.Add(new RawAttribute("depth_multiplier", new AttributeValue.Int64(depthMultiplier), null));
                AddFusedActivationAttribute(optionsTable, 4, attributes);
                AddDilationAttributes(optionsTable, dilationWField: 5, dilationHField: 6, attributes);
                break;

            case 1: // AVERAGE_POOL_2D (Pool2DOptions = 5)
            case 12: // L2_POOL_2D (Pool2DOptions = 5)
            case 17: // MAX_POOL_2D (Pool2DOptions = 5)
                AddPaddingAttribute(optionsTable, 0, attributes);
                AddStrideAttributes(optionsTable, strideWField: 1, strideHField: 2, attributes);
                int filterW = optionsTable.ReadInt32(3, defaultValue: 1);
                int filterH = optionsTable.ReadInt32(4, defaultValue: 1);
                attributes.Add(new RawAttribute("filter_size", new AttributeValue.Int64List([filterH, filterW]), null));
                AddFusedActivationAttribute(optionsTable, 5, attributes);
                break;

            case 9: // FULLY_CONNECTED (FullyConnectedOptions = 8)
                AddFusedActivationAttribute(optionsTable, 0, attributes);
                if (optionsTable.HasField(2))
                {
                    bool keepNumDims = optionsTable.ReadBool(2, defaultValue: false);
                    attributes.Add(new RawAttribute("keep_num_dims", new AttributeValue.Bool(keepNumDims), null));
                }

                break;

            case 25: // SOFTMAX (SoftmaxOptions = 9)
                float beta = optionsTable.ReadSingle(0, defaultValue: 1.0f);
                attributes.Add(new RawAttribute("beta", new AttributeValue.Float64(beta), null));
                break;

            case 2: // CONCATENATION (ConcatenationOptions = 10)
                int concatAxis = optionsTable.ReadInt32(0, defaultValue: 0);
                attributes.Add(new RawAttribute("axis", new AttributeValue.Int64(concatAxis), null));
                AddFusedActivationAttribute(optionsTable, 1, attributes);
                break;

            case 0: // ADD (AddOptions = 11)
            case 41: // SUB (SubOptions = 28)
            case 18: // MUL (MulOptions = 21)
            case 42: // DIV (DivOptions = 29)
            case 11: // L2_NORMALIZATION (L2NormOptions = 12)
                AddFusedActivationAttribute(optionsTable, 0, attributes);
                break;

            case 22: // RESHAPE (ReshapeOptions = 17)
                int[]? newShape = optionsTable.ReadInt32Vector(0);
                if (newShape is { Length: > 0 })
                {
                    long[] shapeList = new long[newShape.Length];
                    for (int i = 0; i < newShape.Length; i++)
                    {
                        shapeList[i] = newShape[i];
                    }

                    attributes.Add(new RawAttribute("new_shape", new AttributeValue.Int64List(shapeList), null));
                }

                break;

            case 45: // STRIDED_SLICE (StridedSliceOptions = 32)
                attributes.Add(new RawAttribute("begin_mask", new AttributeValue.Int64(optionsTable.ReadInt32(0, 0)), null));
                attributes.Add(new RawAttribute("end_mask", new AttributeValue.Int64(optionsTable.ReadInt32(1, 0)), null));
                attributes.Add(new RawAttribute("ellipsis_mask", new AttributeValue.Int64(optionsTable.ReadInt32(2, 0)), null));
                attributes.Add(new RawAttribute("new_axis_mask", new AttributeValue.Int64(optionsTable.ReadInt32(3, 0)), null));
                attributes.Add(new RawAttribute("shrink_axis_mask", new AttributeValue.Int64(optionsTable.ReadInt32(4, 0)), null));
                break;

            case 40: // MEAN (ReducerOptions = 27)
            case 74: // SUM
            case 82: // REDUCE_MAX
            case 89: // REDUCE_MIN
            case 81: // REDUCE_PROD
            case 91: // REDUCE_ANY
            case 140: // REDUCE_ALL
                bool keepDims = optionsTable.ReadBool(0, defaultValue: false);
                attributes.Add(new RawAttribute("keep_dims", new AttributeValue.Bool(keepDims), null));
                break;

            case 43: // SQUEEZE (SqueezeOptions = 30)
                int[]? squeezeDims = optionsTable.ReadInt32Vector(0);
                if (squeezeDims is { Length: > 0 })
                {
                    long[] dims = new long[squeezeDims.Length];
                    for (int i = 0; i < squeezeDims.Length; i++)
                    {
                        dims[i] = squeezeDims[i];
                    }

                    attributes.Add(new RawAttribute("squeeze_dims", new AttributeValue.Int64List(dims), null));
                }

                break;

            case 49: // SPLIT (SplitOptions = 35)
            case 102: // SPLIT_V (SplitVOptions = 79)
                int numSplits = optionsTable.ReadInt32(0, defaultValue: 1);
                attributes.Add(new RawAttribute("num_splits", new AttributeValue.Int64(numSplits), null));
                break;

            case 53: // CAST (CastOptions = 37)
                DataType inType = TfliteOperatorCatalog.MapDataType(optionsTable.ReadByte(0, 0));
                DataType outType = TfliteOperatorCatalog.MapDataType(optionsTable.ReadByte(1, 0));
                attributes.Add(new RawAttribute("in_data_type", new AttributeValue.DataTypeValue(inType), null));
                attributes.Add(new RawAttribute("out_data_type", new AttributeValue.DataTypeValue(outType), null));
                break;

            case 67: // TRANSPOSE_CONV (TransposeConvOptions = 49)
                AddPaddingAttribute(optionsTable, 0, attributes);
                AddStrideAttributes(optionsTable, strideWField: 1, strideHField: 2, attributes);
                AddFusedActivationAttribute(optionsTable, 3, attributes);
                break;

            case 98: // LEAKY_RELU (LeakyReluOptions = 75)
                float alpha = optionsTable.ReadSingle(0, defaultValue: 0.2f);
                attributes.Add(new RawAttribute("alpha", new AttributeValue.Float64(alpha), null));
                break;

            case 118: // IF (IfOptions = 93)
                int thenIdx = optionsTable.ReadInt32(0, defaultValue: -1);
                int elseIdx = optionsTable.ReadInt32(1, defaultValue: -1);
                if (thenIdx >= 0)
                {
                    attributes.Add(new RawAttribute("then_subgraph", null, thenIdx));
                }

                if (elseIdx >= 0)
                {
                    attributes.Add(new RawAttribute("else_subgraph", null, elseIdx));
                }

                break;

            case 119: // WHILE (WhileOptions = 94)
                int condIdx = optionsTable.ReadInt32(0, defaultValue: -1);
                int bodyIdx = optionsTable.ReadInt32(1, defaultValue: -1);
                if (condIdx >= 0)
                {
                    attributes.Add(new RawAttribute("cond_subgraph", null, condIdx));
                }

                if (bodyIdx >= 0)
                {
                    attributes.Add(new RawAttribute("body_subgraph", null, bodyIdx));
                }

                break;

            case 129: // CALL_ONCE (CallOnceOptions = 104)
                int initIdx = optionsTable.ReadInt32(0, defaultValue: -1);
                if (initIdx >= 0)
                {
                    attributes.Add(new RawAttribute("init_subgraph", null, initIdx));
                }

                break;

            case 31: // CALL (CallOptions = 16)
                uint callSubgraphIdx = optionsTable.ReadUInt32(0, defaultValue: 0);
                attributes.Add(new RawAttribute("subgraph", null, (int)callSubgraphIdx));
                break;

            default:
                if (builtinOptionsType == 93)
                {
                    int thenSub = optionsTable.ReadInt32(0, defaultValue: -1);
                    int elseSub = optionsTable.ReadInt32(1, defaultValue: -1);
                    if (thenSub >= 0)
                    {
                        attributes.Add(new RawAttribute("then_subgraph", null, thenSub));
                    }

                    if (elseSub >= 0)
                    {
                        attributes.Add(new RawAttribute("else_subgraph", null, elseSub));
                    }
                }
                else if (builtinOptionsType == 94)
                {
                    int condSub = optionsTable.ReadInt32(0, defaultValue: -1);
                    int bodySub = optionsTable.ReadInt32(1, defaultValue: -1);
                    if (condSub >= 0)
                    {
                        attributes.Add(new RawAttribute("cond_subgraph", null, condSub));
                    }

                    if (bodySub >= 0)
                    {
                        attributes.Add(new RawAttribute("body_subgraph", null, bodySub));
                    }
                }
                else if (builtinOptionsType == 104)
                {
                    int initSub = optionsTable.ReadInt32(0, defaultValue: -1);
                    if (initSub >= 0)
                    {
                        attributes.Add(new RawAttribute("init_subgraph", null, initSub));
                    }
                }

                break;
        }
    }

    private static void AddPaddingAttribute(
        FlatBufferTable optionsTable,
        int fieldIndex,
        List<RawAttribute> attributes)
    {
        byte paddingCode = optionsTable.ReadByte(fieldIndex, defaultValue: 0);
        string paddingName = TfliteOperatorCatalog.FormatPadding(paddingCode);
        attributes.Add(new RawAttribute("padding", new AttributeValue.String(paddingName), null));
    }

    private static void AddStrideAttributes(
        FlatBufferTable optionsTable,
        int strideWField,
        int strideHField,
        List<RawAttribute> attributes)
    {
        int strideW = optionsTable.ReadInt32(strideWField, defaultValue: 1);
        int strideH = optionsTable.ReadInt32(strideHField, defaultValue: 1);
        attributes.Add(new RawAttribute("strides", new AttributeValue.Int64List([strideH, strideW]), null));
    }

    private static void AddDilationAttributes(
        FlatBufferTable optionsTable,
        int dilationWField,
        int dilationHField,
        List<RawAttribute> attributes)
    {
        int dilationW = optionsTable.ReadInt32(dilationWField, defaultValue: 1);
        int dilationH = optionsTable.ReadInt32(dilationHField, defaultValue: 1);
        attributes.Add(new RawAttribute("dilations", new AttributeValue.Int64List([dilationH, dilationW]), null));
    }

    private static void AddFusedActivationAttribute(
        FlatBufferTable optionsTable,
        int fieldIndex,
        List<RawAttribute> attributes)
    {
        byte actCode = optionsTable.ReadByte(fieldIndex, defaultValue: 0);
        if (actCode != 0)
        {
            string actName = TfliteOperatorCatalog.FormatActivationFunction(actCode);
            attributes.Add(new RawAttribute("fused_activation_function", new AttributeValue.String(actName), null));
        }
    }

    private static Subgraph BuildSubgraphRecursive(
        int subgraphIndex,
        List<RawSubgraph> rawSubgraphs,
        int maxDepth,
        int currentDepth,
        HashSet<int> buildStack)
    {
        if (currentDepth > maxDepth)
        {
            throw new ModelParseException(
                $"TFLite control-flow subgraph nesting depth ({currentDepth}) exceeds MaxSubgraphDepth ({maxDepth}).",
                fileOffset: 0,
                context: $"SubGraph[{subgraphIndex}]");
        }

        RawSubgraph raw = rawSubgraphs[subgraphIndex];
        List<GraphNode> nodes = new(raw.Operators.Count);

        buildStack.Add(subgraphIndex);
        try
        {
            for (int i = 0; i < raw.Operators.Count; i++)
            {
                RawOperator rawOp = raw.Operators[i];
                List<NodeAttribute> resolvedAttrs = new(rawOp.Attributes.Count);

                for (int a = 0; a < rawOp.Attributes.Count; a++)
                {
                    RawAttribute rawAttr = rawOp.Attributes[a];
                    if (rawAttr.DirectValue is not null)
                    {
                        resolvedAttrs.Add(new NodeAttribute(rawAttr.Name, rawAttr.DirectValue));
                    }
                    else if (rawAttr.SubgraphIndex is int targetIdx)
                    {
                        if (targetIdx < 0 || targetIdx >= rawSubgraphs.Count)
                        {
                            throw new ModelParseException(
                                $"Operator '{rawOp.Name}' attribute '{rawAttr.Name}' references out-of-range subgraph index {targetIdx} (subgraphs count: {rawSubgraphs.Count}).",
                                fileOffset: 0,
                                context: $"{raw.Name}.{rawOp.Name}.{rawAttr.Name}");
                        }

                        if (!buildStack.Contains(targetIdx))
                        {
                            Subgraph nested = BuildSubgraphRecursive(
                                targetIdx,
                                rawSubgraphs,
                                maxDepth,
                                currentDepth + 1,
                                buildStack);
                            resolvedAttrs.Add(new NodeAttribute(rawAttr.Name, new AttributeValue.Graph(nested)));
                        }
                    }
                }

                nodes.Add(new GraphNode(
                    Name: rawOp.Name,
                    Operator: rawOp.Operator,
                    Inputs: rawOp.Inputs,
                    Outputs: rawOp.Outputs,
                    Attributes: resolvedAttrs));
            }
        }
        finally
        {
            buildStack.Remove(subgraphIndex);
        }

        return new Subgraph(
            Name: raw.Name,
            Nodes: nodes,
            Tensors: raw.Tensors,
            Inputs: raw.Inputs,
            Outputs: raw.Outputs);
    }

    private static void ExtractModelMetadataEntries(
        FlatBufferTable modelTable,
        List<BufferSliceInfo> buffers,
        Dictionary<string, string> metadata)
    {
        int count = modelTable.GetVectorLength(6);
        for (int i = 0; i < count; i++)
        {
            FlatBufferTable metaTable = modelTable.GetVectorTable(6, i);
            string? metaName = metaTable.ReadString(0);
            uint metaBuffer = metaTable.ReadUInt32(1, defaultValue: 0);
            if (!string.IsNullOrEmpty(metaName))
            {
                metadata[$"metadata.{metaName}.buffer"] = metaBuffer.ToString(CultureInfo.InvariantCulture);
                if (metaBuffer < (uint)buffers.Count && buffers[(int)metaBuffer].HasData)
                {
                    metadata[$"metadata.{metaName}.offset"] = buffers[(int)metaBuffer].StreamOffset.ToString(CultureInfo.InvariantCulture);
                    metadata[$"metadata.{metaName}.length"] = buffers[(int)metaBuffer].ByteLength.ToString(CultureInfo.InvariantCulture);
                }
            }
        }
    }

    private readonly record struct ParsedOperatorCode(
        int BuiltinCode,
        string? CustomCode,
        int Version);

    private readonly record struct BufferSliceInfo(
        long StreamOffset,
        long ByteLength)
    {
        public bool HasData => ByteLength > 0 && StreamOffset >= 0;
    }

    private sealed record RawSubgraph(
        string Name,
        List<TensorEdge> Tensors,
        List<GraphPort> Inputs,
        List<GraphPort> Outputs,
        List<RawOperator> Operators);

    private sealed record RawOperator(
        string Name,
        OperatorInfo Operator,
        List<NodePort> Inputs,
        List<NodePort> Outputs,
        List<RawAttribute> Attributes);

    private sealed record RawAttribute(
        string Name,
        AttributeValue? DirectValue,
        int? SubgraphIndex);
}

