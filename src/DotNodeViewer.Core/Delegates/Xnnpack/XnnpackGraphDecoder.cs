using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.IO.FlatBuffers;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Delegates.Xnnpack;

/// <summary>
/// Represents the decoded XNNPACK delegate subgraph together with any non-fatal diagnostics emitted during decoding.
/// </summary>
/// <param name="Subgraph">The synthesized Unified Graph IR <see cref="Ir.Subgraph"/>.</param>
/// <param name="Diagnostics">Any non-fatal warnings emitted while decoding the XNNPACK FlatBuffer.</param>
internal sealed record XnnpackDecodedGraph(
    Subgraph Subgraph,
    IReadOnlyList<ParseDiagnostic> Diagnostics);

/// <summary>
/// Decodes an ExecuTorch <c>XnnpackBackend</c> FlatBuffer payload (<c>XH00</c> / <c>XN01</c>) into a synthesized <see cref="Subgraph"/>
/// with explicit tensor edges and zero-allocation lazy <see cref="TensorDataSlice"/> weight references.
/// </summary>
internal static class XnnpackGraphDecoder
{
    internal const int MaxNodes = 262_144;
    internal const int MaxValues = 1_048_576;
    internal const int MaxConstantEntries = 1_048_576;
    internal const int MaxInlineQuantScales = 65_536;
    private const uint InvalidValueId = uint.MaxValue;

    private readonly record struct ConstantDataEntry(ulong Offset, ulong Size, string? NamedKey);

    private readonly record struct NamedDataLocation(long Offset, long Size);

    /// <summary>
    /// Decodes an XNNPACK delegate payload from <paramref name="modelStream"/> at <paramref name="processedSlice"/>.
    /// </summary>
    /// <param name="modelStream">The seekable stream containing the model file.</param>
    /// <param name="delegateNode">The outer <c>Delegate (XnnpackBackend)</c> node.</param>
    /// <param name="processedSlice">The slice pointing to the processed XNNPACK payload.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="XnnpackDecodedGraph"/> containing the synthesized subgraph and diagnostics.</returns>
    /// <exception cref="ModelParseException">The XNNPACK header, FlatBuffer framing, or schema indices are invalid or out of bounds.</exception>
    public static XnnpackDecodedGraph Decode(
        Stream modelStream,
        GraphNode delegateNode,
        TensorDataSlice processedSlice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelStream);
        ArgumentNullException.ThrowIfNull(delegateNode);
        ArgumentNullException.ThrowIfNull(processedSlice);
        cancellationToken.ThrowIfCancellationRequested();

        XnnpackHeader header = XnnpackHeaderReader.Read(modelStream, processedSlice);
        long flatbufferAbsStart = processedSlice.StreamOffset + header.FlatBufferOffset;
        long constDataAbsStart = processedSlice.StreamOffset + header.ConstantDataOffset;

        using BoundedReadOnlyStream fbWindow = new(modelStream, flatbufferAbsStart, header.FlatBufferSize);
        using RandomAccessSource fbSource = new(fbWindow);

        if (fbSource.Length < 8)
        {
            throw new ModelParseException(
                $"XNNPACK FlatBuffer is too short ({fbSource.Length} bytes).",
                fileOffset: flatbufferAbsStart);
        }

        Span<byte> fbIdScratch = stackalloc byte[4];
        ReadOnlySpan<byte> fbId = fbSource.GetSpan(4, 4, fbIdScratch);
        if (!fbId.SequenceEqual("XN01"u8) && !fbId.SequenceEqual("XN00"u8))
        {
            throw new ModelParseException(
                $"Invalid XNNPACK FlatBuffer identifier at offset 0x{flatbufferAbsStart + 4:X} (expected 'XN01' or 'XN00').",
                fileOffset: flatbufferAbsStart + 4);
        }

        FlatBufferTable graphTable;
        try
        {
            graphTable = FlatBufferTable.ReadRoot(fbSource, bufferStartOffset: 0);
        }
        catch (ModelParseException ex)
        {
            throw new ModelParseException(
                ex.Message,
                fileOffset: ex.FileOffset is long relOffset ? flatbufferAbsStart + relOffset : flatbufferAbsStart,
                context: delegateNode.Name,
                innerException: ex);
        }

        List<ParseDiagnostic> diagnostics = [];

        uint[] inputIds = graphTable.ReadUInt32Vector(4) ?? [];
        uint[] outputIds = graphTable.ReadUInt32Vector(5) ?? [];

        // Read constant_data: [ConstantDataOffset] (field 8)
        int constDataCount = graphTable.GetVectorLength(8);
        if (constDataCount > MaxConstantEntries)
        {
            throw new ModelParseException(
                $"XNNPACK constant_data count ({constDataCount}) exceeds safety limit ({MaxConstantEntries}).",
                fileOffset: flatbufferAbsStart,
                context: delegateNode.Name);
        }

        ConstantDataEntry[] constantData = new ConstantDataEntry[constDataCount];
        bool hasAnyNamedKey = false;
        for (int i = 0; i < constDataCount; i++)
        {
            FlatBufferTable entryTable = graphTable.GetVectorTable(8, i);
            ulong offset = entryTable.ReadUInt64(0);
            ulong size = entryTable.ReadUInt64(1);
            string? namedKey = entryTable.ReadString(2);
            if (!string.IsNullOrEmpty(namedKey))
            {
                hasAnyNamedKey = true;
            }

            constantData[i] = new ConstantDataEntry(offset, size, namedKey);
        }

        int legacyConstBufferCount = graphTable.GetVectorLength(6);
        if (legacyConstBufferCount > MaxConstantEntries)
        {
            throw new ModelParseException(
                $"XNNPACK constant_buffer count ({legacyConstBufferCount}) exceeds safety limit ({MaxConstantEntries}).",
                fileOffset: flatbufferAbsStart,
                context: delegateNode.Name);
        }

        Dictionary<string, NamedDataLocation>? namedDataMap = hasAnyNamedKey
            ? TryReadExecuTorchNamedDataSegments(modelStream)
            : null;

        // Collect outer delegate input/output tensor names in positional order for clean boundary naming
        List<TensorEdge> outerInputTensors = FlattenPortTensors(delegateNode.Inputs);
        List<TensorEdge> outerOutputTensors = FlattenPortTensors(delegateNode.Outputs);

        // Decode xvalues: [XValue] (field 2)
        int valueCount = graphTable.GetVectorLength(2);
        if (valueCount > MaxValues)
        {
            throw new ModelParseException(
                $"XNNPACK xvalues count ({valueCount}) exceeds safety limit ({MaxValues}).",
                fileOffset: flatbufferAbsStart,
                context: delegateNode.Name);
        }

        Dictionary<uint, string> inputIdNames = new(inputIds.Length);
        Dictionary<uint, string> outputIdNames = new(outputIds.Length);
        HashSet<string> usedTensorNames = new(StringComparer.Ordinal);

        for (int i = 0; i < inputIds.Length; i++)
        {
            uint valId = inputIds[i];
            if (valId >= (uint)valueCount)
            {
                throw new ModelParseException(
                    $"XNNPACK input_ids[{i}] ({valId}) is out of range for xvalues (count={valueCount}).",
                    fileOffset: flatbufferAbsStart,
                    context: delegateNode.Name);
            }

            string candidate = i < outerInputTensors.Count && !string.IsNullOrWhiteSpace(outerInputTensors[i].Name)
                ? outerInputTensors[i].Name
                : string.Create(CultureInfo.InvariantCulture, $"xnn_in_{i}");
            inputIdNames[valId] = MakeUniqueName(candidate, usedTensorNames);
        }

        for (int i = 0; i < outputIds.Length; i++)
        {
            uint valId = outputIds[i];
            if (valId >= (uint)valueCount)
            {
                throw new ModelParseException(
                    $"XNNPACK output_ids[{i}] ({valId}) is out of range for xvalues (count={valueCount}).",
                    fileOffset: flatbufferAbsStart,
                    context: delegateNode.Name);
            }

            if (inputIdNames.TryGetValue(valId, out string? existingInputName))
            {
                // Passthrough tensor that is both input and output keeps its input name
                outputIdNames[valId] = existingInputName;
            }
            else
            {
                string candidate = i < outerOutputTensors.Count && !string.IsNullOrWhiteSpace(outerOutputTensors[i].Name)
                    ? outerOutputTensors[i].Name
                    : string.Create(CultureInfo.InvariantCulture, $"xnn_out_{i}");
                outputIdNames[valId] = MakeUniqueName(candidate, usedTensorNames);
            }
        }

        TensorEdge?[] xValueEdges = new TensorEdge?[valueCount];
        List<TensorEdge> allTensors = new(valueCount);
        HashSet<string> missingNamedKeysReported = new(StringComparer.Ordinal);

        for (int i = 0; i < valueCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlatBufferTable xvalTable = graphTable.GetVectorTable(2, i);
            byte unionType = xvalTable.ReadByte(0);
            FlatBufferTable? inner = xvalTable.ReadTable(1);
            if (inner is null)
            {
                continue;
            }

            FlatBufferTable tensorTable;
            FlatBufferTable? quantValueTable = null;
            if (unionType == 1)
            {
                tensorTable = inner.Value;
            }
            else if (unionType == 2)
            {
                quantValueTable = inner.Value;
                FlatBufferTable? baseTensor = quantValueTable.Value.ReadTable(0);
                if (baseTensor is null)
                {
                    continue;
                }

                tensorTable = baseTensor.Value;
            }
            else
            {
                continue;
            }

            short xnnDatatype = tensorTable.ReadInt16(0);
            DataType dataType = XnnpackOperatorCatalog.MapDataType(xnnDatatype);
            uint[]? dims = tensorTable.ReadUInt32Vector(2);
            uint constBufferIdx = tensorTable.ReadUInt32(3);

            TensorShape? shape = null;
            if (dims is not null)
            {
                TensorDimension[] tensorDims = new TensorDimension[dims.Length];
                for (int d = 0; d < dims.Length; d++)
                {
                    tensorDims[d] = new TensorDimension.Fixed(dims[d]);
                }

                shape = new TensorShape(tensorDims);
            }

            QuantizationInfo? quantInfo = null;
            if (quantValueTable is not null)
            {
                quantInfo = DecodeQuantization(
                    quantValueTable.Value,
                    xnnDatatype,
                    modelStream,
                    graphTable,
                    header,
                    constDataAbsStart,
                    flatbufferAbsStart,
                    constantData,
                    legacyConstBufferCount,
                    namedDataMap,
                    delegateNode.Name,
                    diagnostics,
                    missingNamedKeysReported);
            }

            TensorDataSource? dataSource = null;
            if (constBufferIdx > 0)
            {
                dataSource = ResolveConstantDataSource(
                    constBufferIdx,
                    dataType,
                    modelStream,
                    graphTable,
                    header,
                    constDataAbsStart,
                    flatbufferAbsStart,
                    constantData,
                    legacyConstBufferCount,
                    namedDataMap,
                    delegateNode.Name,
                    diagnostics,
                    missingNamedKeysReported);
            }

            uint valKey = (uint)i;
            TensorKind kind;
            string tensorName;
            if (inputIdNames.TryGetValue(valKey, out string? inName))
            {
                kind = TensorKind.GraphInput;
                tensorName = inName;
            }
            else if (outputIdNames.TryGetValue(valKey, out string? outName))
            {
                kind = TensorKind.GraphOutput;
                tensorName = outName;
            }
            else if (constBufferIdx > 0)
            {
                kind = TensorKind.Initializer;
                tensorName = MakeUniqueName(string.Create(CultureInfo.InvariantCulture, $"xnn_const_{i}"), usedTensorNames);
            }
            else
            {
                kind = TensorKind.Activation;
                tensorName = MakeUniqueName(string.Create(CultureInfo.InvariantCulture, $"xnn_val_{i}"), usedTensorNames);
            }

            TensorDescriptor descriptor = new(dataType, shape, quantInfo);
            TensorEdge edge = new(tensorName, descriptor, kind, dataSource);
            xValueEdges[i] = edge;
            allTensors.Add(edge);
        }

        // Decode xnodes: [XNode] (field 1)
        int nodeCount = graphTable.GetVectorLength(1);
        if (nodeCount > MaxNodes)
        {
            throw new ModelParseException(
                $"XNNPACK xnodes count ({nodeCount}) exceeds safety limit ({MaxNodes}).",
                fileOffset: flatbufferAbsStart,
                context: delegateNode.Name);
        }

        List<GraphNode> nodes = new(nodeCount);
        for (int i = 0; i < nodeCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlatBufferTable xnodeTable = graphTable.GetVectorTable(1, i);
            byte unionType = xnodeTable.ReadByte(0);
            FlatBufferTable? opTable = xnodeTable.ReadTable(1);
            uint debugHandle = xnodeTable.ReadUInt32(2);
            FlatBufferTable? minMaxTable = xnodeTable.ReadTable(3);

            XnnpackOpSpec opSpec = XnnpackOperatorCatalog.Resolve(unionType);
            List<NodePort> inPorts = [];
            List<NodePort> outPorts = [];
            List<NodeAttribute> attributes = [];

            if (debugHandle != 0 && debugHandle != 65535 && debugHandle != InvalidValueId)
            {
                attributes.Add(new NodeAttribute("debug_handle", new AttributeValue.Int64(debugHandle)));
            }

            if (opSpec.TableKind == XnnpackTableKind.Unknown)
            {
                string diagMsg = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Unknown XNNPACK operator union discriminant {unionType} at xnodes[{i}].");
                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Warning,
                    "DNV_XNNPACK_UNKNOWN_OP",
                    diagMsg,
                    string.Create(CultureInfo.InvariantCulture, $"xnodes[{i}]")));
                attributes.Add(new NodeAttribute("xnnpack.union_type", new AttributeValue.Int64(unionType)));
                attributes.Add(new NodeAttribute("diagnostic.code", new AttributeValue.String("DNV_XNNPACK_UNKNOWN_OP")));
            }
            else if (opTable is not null)
            {
                DecodeNodeTable(
                    opSpec.TableKind,
                    opTable.Value,
                    xValueEdges,
                    inPorts,
                    outPorts,
                    attributes,
                    flatbufferAbsStart,
                    delegateNode.Name);
            }

            if (minMaxTable is not null)
            {
                DecodeOutputMinMax(minMaxTable.Value, attributes);
            }

            string nodeName = string.Create(CultureInfo.InvariantCulture, $"{opSpec.Name}_{i}");
            OperatorInfo opInfo = new(opSpec.Name, opSpec.Category, Domain: "xnnpack");
            nodes.Add(new GraphNode(nodeName, opInfo, inPorts, outPorts, attributes));
        }

        List<GraphPort> inputs = new(inputIds.Length);
        for (int i = 0; i < inputIds.Length; i++)
        {
            uint valId = inputIds[i];
            if (xValueEdges[(int)valId] is TensorEdge edge)
            {
                inputs.Add(new GraphPort(edge.Name, [edge]));
            }
        }

        List<GraphPort> outputs = new(outputIds.Length);
        for (int i = 0; i < outputIds.Length; i++)
        {
            uint valId = outputIds[i];
            if (xValueEdges[(int)valId] is TensorEdge edge)
            {
                outputs.Add(new GraphPort(edge.Name, [edge]));
            }
        }

        string subgraphName = $"{delegateNode.Name} (XNNPACK)";
        Subgraph subgraph = new(subgraphName, nodes, allTensors, inputs, outputs);
        return new XnnpackDecodedGraph(subgraph, diagnostics);
    }

    private static QuantizationInfo? DecodeQuantization(
        FlatBufferTable quantValueTable,
        short xnnDatatype,
        Stream modelStream,
        FlatBufferTable graphTable,
        XnnpackHeader header,
        long constDataAbsStart,
        long flatbufferAbsStart,
        ConstantDataEntry[] constantData,
        int legacyConstBufferCount,
        Dictionary<string, NamedDataLocation>? namedDataMap,
        string delegateNodeName,
        List<ParseDiagnostic> diagnostics,
        HashSet<string> missingNamedKeysReported)
    {
        byte qType = quantValueTable.ReadByte(1);
        FlatBufferTable? qTable = quantValueTable.ReadTable(2);
        if (qTable is null)
        {
            return null;
        }

        switch (qType)
        {
            case 1: // PerChannelQuant
            {
                float[]? scales = qTable.Value.ReadSingleVector(0);
                int channelDim = qTable.Value.ReadInt32(1);
                uint scaleBufferIdx = qTable.Value.ReadUInt32(2);
                uint numScales = qTable.Value.ReadUInt32(3);

                if ((scales is null || scales.Length == 0) && scaleBufferIdx > 0 && numScales > 0 && numScales <= MaxInlineQuantScales)
                {
                    scales = TryReadFloat32ScalesFromBuffer(
                        scaleBufferIdx,
                        (int)numScales,
                        modelStream,
                        graphTable,
                        header,
                        constDataAbsStart,
                        flatbufferAbsStart,
                        constantData,
                        legacyConstBufferCount,
                        namedDataMap,
                        delegateNodeName,
                        diagnostics,
                        missingNamedKeysReported);
                }

                float[] finalScales = scales ?? [];
                long[] zeroPoints = finalScales.Length > 0 ? new long[finalScales.Length] : [];
                return new AffineQuantization(finalScales, zeroPoints, channelDim);
            }

            case 2: // PerTensorQuant
            {
                float scale = qTable.Value.ReadSingle(0);
                int zeroPoint = qTable.Value.ReadInt32(1);
                return new AffineQuantization([scale], [zeroPoint], QuantizedDimension: null);
            }

            case 3: // PerTokenDynamicQuant
            {
                int numNonbatchDims = qTable.Value.ReadInt32(0);
                return new BlockQuantization("PerTokenDynamic", Math.Max(1, numNonbatchDims), Bits: 8);
            }

            case 4: // PerChannelGroupQuant
            {
                int channelDim = qTable.Value.ReadInt32(1);
                int groupSize = qTable.Value.ReadInt32(2);
                int bits = XnnpackOperatorCatalog.IsFourBitType(xnnDatatype) ? 4 : 8;
                string scheme = string.Create(CultureInfo.InvariantCulture, $"PerChannelGroup(axis={channelDim})");
                return new BlockQuantization(scheme, Math.Max(1, groupSize), bits);
            }

            default:
                return null;
        }
    }

    private static float[]? TryReadFloat32ScalesFromBuffer(
        uint scaleBufferIdx,
        int numScales,
        Stream modelStream,
        FlatBufferTable graphTable,
        XnnpackHeader header,
        long constDataAbsStart,
        long flatbufferAbsStart,
        ConstantDataEntry[] constantData,
        int legacyConstBufferCount,
        Dictionary<string, NamedDataLocation>? namedDataMap,
        string delegateNodeName,
        List<ParseDiagnostic> diagnostics,
        HashSet<string> missingNamedKeysReported)
    {
        TensorDataSource dataSource = ResolveConstantDataSource(
            scaleBufferIdx,
            DataType.Float32,
            modelStream,
            graphTable,
            header,
            constDataAbsStart,
            flatbufferAbsStart,
            constantData,
            legacyConstBufferCount,
            namedDataMap,
            delegateNodeName,
            diagnostics,
            missingNamedKeysReported);

        if (dataSource is not TensorDataSlice slice)
        {
            return null;
        }

        int requiredBytes = numScales * sizeof(float);
        if (slice.ByteLength < requiredBytes)
        {
            return null;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(requiredBytes);
        try
        {
            TensorDataSlice boundedSlice = new(slice.StreamOffset, requiredBytes, DataType.Float32, TensorDataEncoding.Raw);
            int read = TensorDataLoader.ReadSlice(modelStream, boundedSlice, rented.AsSpan(0, requiredBytes));
            if (read < requiredBytes)
            {
                return null;
            }

            float[] scales = new float[numScales];
            ReadOnlySpan<byte> bytes = rented.AsSpan(0, requiredBytes);
            for (int i = 0; i < numScales; i++)
            {
                scales[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * 4, 4));
            }

            return scales;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static TensorDataSource ResolveConstantDataSource(
        uint bufferIdx,
        DataType dataType,
        Stream modelStream,
        FlatBufferTable graphTable,
        XnnpackHeader header,
        long constDataAbsStart,
        long flatbufferAbsStart,
        ConstantDataEntry[] constantData,
        int legacyConstBufferCount,
        Dictionary<string, NamedDataLocation>? namedDataMap,
        string delegateNodeName,
        List<ParseDiagnostic> diagnostics,
        HashSet<string> missingNamedKeysReported)
    {
        if (constantData.Length > 0)
        {
            if (bufferIdx >= (uint)constantData.Length)
            {
                throw new ModelParseException(
                    $"XNNPACK constant_buffer_idx ({bufferIdx}) is out of range for constant_data (count={constantData.Length}).",
                    fileOffset: flatbufferAbsStart,
                    context: delegateNodeName);
            }

            ConstantDataEntry entry = constantData[(int)bufferIdx];
            if (!string.IsNullOrEmpty(entry.NamedKey) || entry.Offset == ulong.MaxValue)
            {
                string key = entry.NamedKey ?? string.Create(CultureInfo.InvariantCulture, $"constant_{bufferIdx}");
                if (namedDataMap is not null && namedDataMap.TryGetValue(key, out NamedDataLocation loc))
                {
                    long sliceLength = entry.Size > 0 && entry.Size <= (ulong)loc.Size
                        ? (long)entry.Size
                        : loc.Size;
                    if (loc.Offset < 0 || sliceLength < 0 || loc.Offset > modelStream.Length || sliceLength > modelStream.Length - loc.Offset)
                    {
                        throw new ModelParseException(
                            $"XNNPACK named_data segment '{key}' [0x{loc.Offset:X}..+0x{sliceLength:X}) exceeds stream bounds (0x{modelStream.Length:X}).",
                            fileOffset: loc.Offset,
                            context: delegateNodeName);
                    }

                    return new TensorDataSlice(loc.Offset, sliceLength, dataType, TensorDataEncoding.Raw);
                }

                long? extLen = entry.Size <= (ulong)long.MaxValue ? (long)entry.Size : null;
                if (missingNamedKeysReported.Add(key))
                {
                    diagnostics.Add(new ParseDiagnostic(
                        DiagnosticSeverity.Warning,
                        "DNV_XNNPACK_EXTERNAL_NAMED_DATA",
                        $"XNNPACK constant key '{key}' is not embedded in Program.named_data; mapped as external .ptd reference.",
                        delegateNodeName));
                }

                return new ExternalTensorDataReference($"{key}.ptd", 0, extLen, dataType);
            }

            if (entry.Offset > (ulong)long.MaxValue ||
                entry.Size > (ulong)long.MaxValue ||
                entry.Offset > (ulong)header.ConstantDataSize ||
                entry.Size > (ulong)header.ConstantDataSize - entry.Offset)
            {
                throw new ModelParseException(
                    $"XNNPACK constant_data[{bufferIdx}] [offset={entry.Offset}, size={entry.Size}] exceeds header constant_data_size ({header.ConstantDataSize}).",
                    fileOffset: constDataAbsStart,
                    context: delegateNodeName);
            }

            long absOffset = constDataAbsStart + (long)entry.Offset;
            long byteLen = (long)entry.Size;
            if (absOffset < 0 || absOffset > modelStream.Length || byteLen > modelStream.Length - absOffset)
            {
                throw new ModelParseException(
                    $"XNNPACK constant_data[{bufferIdx}] [0x{absOffset:X}..+0x{byteLen:X}) exceeds stream length (0x{modelStream.Length:X}).",
                    fileOffset: absOffset,
                    context: delegateNodeName);
            }

            return new TensorDataSlice(absOffset, byteLen, dataType, TensorDataEncoding.Raw);
        }

        if (legacyConstBufferCount > 0)
        {
            if (bufferIdx >= (uint)legacyConstBufferCount)
            {
                throw new ModelParseException(
                    $"XNNPACK constant_buffer_idx ({bufferIdx}) is out of range for legacy constant_buffer (count={legacyConstBufferCount}).",
                    fileOffset: flatbufferAbsStart,
                    context: delegateNodeName);
            }

            FlatBufferTable bufTable = graphTable.GetVectorTable(6, (int)bufferIdx);
            TensorDataSlice? relSlice = bufTable.CaptureByteVectorSlice(0, dataType);
            if (relSlice is not null)
            {
                return new TensorDataSlice(
                    flatbufferAbsStart + relSlice.StreamOffset,
                    relSlice.ByteLength,
                    dataType,
                    TensorDataEncoding.Raw);
            }

            return new TensorDataSlice(flatbufferAbsStart, 0, dataType, TensorDataEncoding.Raw);
        }

        throw new ModelParseException(
            $"XNNPACK constant_buffer_idx ({bufferIdx}) referenced, but neither constant_data nor constant_buffer is present.",
            fileOffset: flatbufferAbsStart,
            context: delegateNodeName);
    }

    private static Dictionary<string, NamedDataLocation>? TryReadExecuTorchNamedDataSegments(Stream modelStream)
    {
        if (!modelStream.CanRead || !modelStream.CanSeek || modelStream.Length < 16)
        {
            return null;
        }

        long savedPos = modelStream.Position;
        try
        {
            Span<byte> header = stackalloc byte[8];
            modelStream.Position = 0;
            modelStream.ReadExactly(header);
            if (header[4] != (byte)'E' || header[5] != (byte)'T' || header[6] != (byte)'1')
            {
                return null;
            }

            using RandomAccessSource source = new(modelStream);
            long segmentBaseOffset = 0;
            if (source.Length >= 40)
            {
                Span<byte> ehScratch = stackalloc byte[4];
                if (source.GetSpan(8, 4, ehScratch).SequenceEqual("eh00"u8))
                {
                    segmentBaseOffset = source.ReadInt64LittleEndian(24);
                }
            }

            FlatBufferTable programTable = FlatBufferTable.ReadRoot(source, bufferStartOffset: 0);
            int segmentCount = programTable.GetVectorLength(4);
            int namedCount = programTable.GetVectorLength(7);
            if (segmentCount <= 0 || namedCount <= 0)
            {
                return null;
            }

            NamedDataLocation[] segments = new NamedDataLocation[segmentCount];
            for (int i = 0; i < segmentCount; i++)
            {
                FlatBufferTable segTable = programTable.GetVectorTable(4, i);
                long offset = unchecked((long)segTable.ReadUInt64(0));
                long size = unchecked((long)segTable.ReadUInt64(1));
                segments[i] = new NamedDataLocation(segmentBaseOffset + offset, size);
            }

            Dictionary<string, NamedDataLocation> map = new(namedCount, StringComparer.Ordinal);
            for (int i = 0; i < namedCount; i++)
            {
                FlatBufferTable namedTable = programTable.GetVectorTable(7, i);
                string? key = namedTable.ReadString(0);
                uint segIdx = namedTable.ReadUInt32(1);
                if (!string.IsNullOrEmpty(key) && segIdx < (uint)segments.Length)
                {
                    map[key] = segments[segIdx];
                }
            }

            return map;
        }
        catch (ModelParseException)
        {
            return null;
        }
        finally
        {
            modelStream.Position = savedPos;
        }
    }

    private static void DecodeNodeTable(
        XnnpackTableKind kind,
        FlatBufferTable table,
        TensorEdge?[] xValueEdges,
        List<NodePort> inPorts,
        List<NodePort> outPorts,
        List<NodeAttribute> attributes,
        long flatbufferAbsStart,
        string delegateNodeName)
    {
        uint flags = 0;

        switch (kind)
        {
            case XnnpackTableKind.Unary1x1:
            {
                AddInputPort("input", table.ReadUInt32(0), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(1), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(2);
                break;
            }

            case XnnpackTableKind.Binary2x1:
            {
                AddInputPort("input1", table.ReadUInt32(0), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddInputPort("input2", table.ReadUInt32(1), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(2), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(3);
                break;
            }

            case XnnpackTableKind.FullyConnected:
            {
                AddInputPort("input", table.ReadUInt32(0), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddInputPort("filter", table.ReadUInt32(1), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOptionalInputPort("bias", table.ReadUInt32(2, InvalidValueId), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(3), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(4);
                break;
            }

            case XnnpackTableKind.Conv:
            {
                uint padTop = table.ReadUInt32(0);
                uint padRight = table.ReadUInt32(1);
                uint padBottom = table.ReadUInt32(2);
                uint padLeft = table.ReadUInt32(3);
                uint kernelH = table.ReadUInt32(4);
                uint kernelW = table.ReadUInt32(5);
                uint strideH = table.ReadUInt32(6);
                uint strideW = table.ReadUInt32(7);
                uint dilationH = table.ReadUInt32(8);
                uint dilationW = table.ReadUInt32(9);
                uint groupIn = table.ReadUInt32(10);
                uint groupOut = table.ReadUInt32(11);
                uint groups = table.ReadUInt32(12);
                uint adjH = table.ReadUInt32(13);
                uint adjW = table.ReadUInt32(14);

                AddInputPort("input", table.ReadUInt32(15), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddInputPort("filter", table.ReadUInt32(16), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOptionalInputPort("bias", table.ReadUInt32(17, InvalidValueId), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(18), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(19);

                attributes.Add(new NodeAttribute("kernel_size", new AttributeValue.Int64List([kernelH, kernelW])));
                attributes.Add(new NodeAttribute("strides", new AttributeValue.Int64List([strideH, strideW])));
                attributes.Add(new NodeAttribute("padding", new AttributeValue.Int64List([padTop, padLeft, padBottom, padRight])));
                attributes.Add(new NodeAttribute("dilation", new AttributeValue.Int64List([dilationH, dilationW])));
                attributes.Add(new NodeAttribute("groups", new AttributeValue.Int64(groups)));
                attributes.Add(new NodeAttribute("group_input_channels", new AttributeValue.Int64(groupIn)));
                attributes.Add(new NodeAttribute("group_output_channels", new AttributeValue.Int64(groupOut)));
                if (adjH != 0 || adjW != 0)
                {
                    attributes.Add(new NodeAttribute("adjustment", new AttributeValue.Int64List([adjH, adjW])));
                }
                break;
            }

            case XnnpackTableKind.Pooling2d:
            {
                uint padTop = table.ReadUInt32(0);
                uint padRight = table.ReadUInt32(1);
                uint padBottom = table.ReadUInt32(2);
                uint padLeft = table.ReadUInt32(3);
                uint poolH = table.ReadUInt32(4);
                uint poolW = table.ReadUInt32(5);
                uint strideH = table.ReadUInt32(6);
                uint strideW = table.ReadUInt32(7);
                uint dilationH = table.ReadUInt32(8);
                uint dilationW = table.ReadUInt32(9);

                AddInputPort("input", table.ReadUInt32(10), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(11), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(12);

                attributes.Add(new NodeAttribute("kernel_size", new AttributeValue.Int64List([poolH, poolW])));
                attributes.Add(new NodeAttribute("strides", new AttributeValue.Int64List([strideH, strideW])));
                attributes.Add(new NodeAttribute("padding", new AttributeValue.Int64List([padTop, padLeft, padBottom, padRight])));
                attributes.Add(new NodeAttribute("dilation", new AttributeValue.Int64List([dilationH, dilationW])));
                break;
            }

            case XnnpackTableKind.ArgMaxPooling2d:
            {
                uint padTop = table.ReadUInt32(0);
                uint padRight = table.ReadUInt32(1);
                uint padBottom = table.ReadUInt32(2);
                uint padLeft = table.ReadUInt32(3);
                uint poolH = table.ReadUInt32(4);
                uint poolW = table.ReadUInt32(5);

                AddInputPort("input", table.ReadUInt32(6), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOptionalOutputPort("output_value", table.ReadUInt32(7, InvalidValueId), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                AddOptionalOutputPort("output_index", table.ReadUInt32(8, InvalidValueId), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(9);

                attributes.Add(new NodeAttribute("kernel_size", new AttributeValue.Int64List([poolH, poolW])));
                attributes.Add(new NodeAttribute("padding", new AttributeValue.Int64List([padTop, padLeft, padBottom, padRight])));
                break;
            }

            case XnnpackTableKind.StaticTranspose:
            {
                uint[]? perm = table.ReadUInt32Vector(1);
                AddInputPort("input", table.ReadUInt32(2), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(3), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(4);

                if (perm is not null)
                {
                    attributes.Add(new NodeAttribute("perm", new AttributeValue.Int64List(ToInt64Array(perm))));
                }
                break;
            }

            case XnnpackTableKind.StaticResizeBilinear2d:
            {
                uint newH = table.ReadUInt32(0);
                uint newW = table.ReadUInt32(1);
                AddInputPort("input", table.ReadUInt32(2), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(3), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(4);

                attributes.Add(new NodeAttribute("new_height", new AttributeValue.Int64(newH)));
                attributes.Add(new NodeAttribute("new_width", new AttributeValue.Int64(newW)));
                break;
            }

            case XnnpackTableKind.StaticConstantPad:
            {
                uint[]? prePads = table.ReadUInt32Vector(0);
                uint[]? postPads = table.ReadUInt32Vector(1);
                float padValue = table.ReadSingle(2);
                AddInputPort("input", table.ReadUInt32(3), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(4), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(5);

                if (prePads is not null)
                {
                    attributes.Add(new NodeAttribute("pre_paddings", new AttributeValue.Int64List(ToInt64Array(prePads))));
                }

                if (postPads is not null)
                {
                    attributes.Add(new NodeAttribute("post_paddings", new AttributeValue.Int64List(ToInt64Array(postPads))));
                }

                attributes.Add(new NodeAttribute("padding_value", new AttributeValue.Float64(padValue)));
                break;
            }

            case XnnpackTableKind.StaticReshape:
            {
                uint[]? newShape = table.ReadUInt32Vector(1);
                AddInputPort("input", table.ReadUInt32(2), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(3), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(4);

                if (newShape is not null)
                {
                    attributes.Add(new NodeAttribute("new_shape", new AttributeValue.Int64List(ToInt64Array(newShape))));
                }
                break;
            }

            case XnnpackTableKind.StaticSlice:
            {
                uint[]? offsets = table.ReadUInt32Vector(1);
                uint[]? sizes = table.ReadUInt32Vector(2);
                AddInputPort("input", table.ReadUInt32(3), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(4), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(5);

                if (offsets is not null)
                {
                    attributes.Add(new NodeAttribute("offsets", new AttributeValue.Int64List(ToInt64Array(offsets))));
                }

                if (sizes is not null)
                {
                    attributes.Add(new NodeAttribute("sizes", new AttributeValue.Int64List(ToInt64Array(sizes))));
                }
                break;
            }

            case XnnpackTableKind.LeakyReLU:
            {
                float negativeSlope = table.ReadSingle(0);
                AddInputPort("input", table.ReadUInt32(1), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(2), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(3);

                attributes.Add(new NodeAttribute("negative_slope", new AttributeValue.Float64(negativeSlope)));
                break;
            }

            case XnnpackTableKind.Elu:
            {
                float alpha = table.ReadSingle(0);
                AddInputPort("input", table.ReadUInt32(1), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(2), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(3);

                attributes.Add(new NodeAttribute("alpha", new AttributeValue.Float64(alpha)));
                break;
            }

            case XnnpackTableKind.Concat2:
            case XnnpackTableKind.Concat3:
            case XnnpackTableKind.Concat4:
            case XnnpackTableKind.Concat5:
            {
                uint axis = table.ReadUInt32(0);
                AddInputPort("input1", table.ReadUInt32(1), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddInputPort("input2", table.ReadUInt32(2), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                if (kind >= XnnpackTableKind.Concat3)
                {
                    AddOptionalInputPort("input3", table.ReadUInt32(3, InvalidValueId), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                }

                if (kind >= XnnpackTableKind.Concat4)
                {
                    AddOptionalInputPort("input4", table.ReadUInt32(4, InvalidValueId), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                }

                AddOutputPort("output", table.ReadUInt32(5), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(6);
                if (kind == XnnpackTableKind.Concat5)
                {
                    AddOptionalInputPort("input5", table.ReadUInt32(7, InvalidValueId), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                }

                attributes.Add(new NodeAttribute("axis", new AttributeValue.Int64(axis)));
                break;
            }

            case XnnpackTableKind.ScaledDotProductAttention:
            {
                AddInputPort("query", table.ReadUInt32(0), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddInputPort("key", table.ReadUInt32(1), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddInputPort("value", table.ReadUInt32(2), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOptionalInputPort("scale", table.ReadUInt32(3, InvalidValueId), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOptionalInputPort("mask", table.ReadUInt32(4, InvalidValueId), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(5), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(6);
                break;
            }

            case XnnpackTableKind.DepthToSpace2d:
            {
                uint blockSize = table.ReadUInt32(0);
                AddInputPort("input", table.ReadUInt32(1), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(2), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(3);

                attributes.Add(new NodeAttribute("block_size", new AttributeValue.Int64(blockSize)));
                break;
            }

            case XnnpackTableKind.StaticReduce:
            {
                int[]? axes = table.ReadInt32Vector(1);
                AddInputPort("input", table.ReadUInt32(2), xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
                AddOutputPort("output", table.ReadUInt32(3), xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
                flags = table.ReadUInt32(4);

                if (axes is not null)
                {
                    long[] longAxes = new long[axes.Length];
                    for (int a = 0; a < axes.Length; a++)
                    {
                        longAxes[a] = axes[a];
                    }

                    attributes.Add(new NodeAttribute("reduction_axes", new AttributeValue.Int64List(longAxes)));
                }
                break;
            }
        }

        if (flags != 0)
        {
            attributes.Add(new NodeAttribute("flags", new AttributeValue.Int64(flags)));
        }
    }

    private static void DecodeOutputMinMax(FlatBufferTable minMaxTable, List<NodeAttribute> attributes)
    {
        float outputMin = minMaxTable.ReadSingle(0, float.NegativeInfinity);
        float outputMax = minMaxTable.ReadSingle(1, float.PositiveInfinity);

        if (outputMin == 0f && float.IsPositiveInfinity(outputMax))
        {
            attributes.Add(new NodeAttribute("fused_activation_function", new AttributeValue.String("RELU")));
        }
        else if (outputMin == 0f && outputMax == 6f)
        {
            attributes.Add(new NodeAttribute("fused_activation_function", new AttributeValue.String("RELU6")));
        }
        else if (!float.IsNegativeInfinity(outputMin) && !float.IsPositiveInfinity(outputMax))
        {
            attributes.Add(new NodeAttribute("fused_activation_function", new AttributeValue.String("CLAMP")));
        }

        if (!float.IsNegativeInfinity(outputMin))
        {
            attributes.Add(new NodeAttribute("output_min", new AttributeValue.Float64(outputMin)));
        }

        if (!float.IsPositiveInfinity(outputMax))
        {
            attributes.Add(new NodeAttribute("output_max", new AttributeValue.Float64(outputMax)));
        }
    }

    private static void AddInputPort(
        string portName,
        uint valueId,
        TensorEdge?[] xValueEdges,
        List<NodePort> inPorts,
        long flatbufferAbsStart,
        string delegateNodeName)
    {
        TensorEdge? edge = ResolveValueEdge(valueId, xValueEdges, flatbufferAbsStart, delegateNodeName, portName);
        if (edge is not null)
        {
            inPorts.Add(new NodePort(portName, [edge]));
        }
    }

    private static void AddOptionalInputPort(
        string portName,
        uint valueId,
        TensorEdge?[] xValueEdges,
        List<NodePort> inPorts,
        long flatbufferAbsStart,
        string delegateNodeName)
    {
        if (valueId == InvalidValueId)
        {
            return;
        }

        AddInputPort(portName, valueId, xValueEdges, inPorts, flatbufferAbsStart, delegateNodeName);
    }

    private static void AddOutputPort(
        string portName,
        uint valueId,
        TensorEdge?[] xValueEdges,
        List<NodePort> outPorts,
        long flatbufferAbsStart,
        string delegateNodeName)
    {
        TensorEdge? edge = ResolveValueEdge(valueId, xValueEdges, flatbufferAbsStart, delegateNodeName, portName);
        if (edge is not null)
        {
            outPorts.Add(new NodePort(portName, [edge]));
        }
    }

    private static void AddOptionalOutputPort(
        string portName,
        uint valueId,
        TensorEdge?[] xValueEdges,
        List<NodePort> outPorts,
        long flatbufferAbsStart,
        string delegateNodeName)
    {
        if (valueId == InvalidValueId)
        {
            return;
        }

        AddOutputPort(portName, valueId, xValueEdges, outPorts, flatbufferAbsStart, delegateNodeName);
    }

    private static TensorEdge? ResolveValueEdge(
        uint valueId,
        TensorEdge?[] xValueEdges,
        long flatbufferAbsStart,
        string delegateNodeName,
        string portName)
    {
        if (valueId == InvalidValueId)
        {
            return null;
        }

        if (valueId >= (uint)xValueEdges.Length)
        {
            throw new ModelParseException(
                $"XNNPACK port '{portName}' references out-of-range value ID {valueId} (xvalues count={xValueEdges.Length}).",
                fileOffset: flatbufferAbsStart,
                context: delegateNodeName);
        }

        return xValueEdges[(int)valueId];
    }

    private static List<TensorEdge> FlattenPortTensors(IReadOnlyList<NodePort> ports)
    {
        List<TensorEdge> flattened = new(ports.Count);
        for (int p = 0; p < ports.Count; p++)
        {
            IReadOnlyList<TensorEdge> tensors = ports[p].Tensors;
            for (int t = 0; t < tensors.Count; t++)
            {
                flattened.Add(tensors[t]);
            }
        }

        return flattened;
    }

    private static string MakeUniqueName(string candidate, HashSet<string> usedNames)
    {
        if (usedNames.Add(candidate))
        {
            return candidate;
        }

        int suffix = 1;
        while (true)
        {
            string next = string.Create(CultureInfo.InvariantCulture, $"{candidate}_{suffix}");
            if (usedNames.Add(next))
            {
                return next;
            }

            suffix++;
        }
    }

    private static long[] ToInt64Array(uint[] values)
    {
        long[] result = new long[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = values[i];
        }

        return result;
    }
}
