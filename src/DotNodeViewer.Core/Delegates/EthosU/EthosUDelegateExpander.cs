using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Delegates.EthosU;

/// <summary>
/// On-demand expander that unpacks Arm Ethos-U command stream payloads from TFLite and ExecuTorch models into synthesized subgraphs.
/// </summary>
internal sealed class EthosUDelegateExpander : IDelegateSubgraphExpander
{
    /// <inheritdoc/>
    public string BackendId => "ethos-u";

    /// <inheritdoc/>
    public bool CanExpand(ModelGraph graph, GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);

        // Check for standard delegate attributes
        for (int i = 0; i < node.Attributes.Count; i++)
        {
            NodeAttribute attr = node.Attributes[i];
            if (string.Equals(attr.Name, "delegate.backend", StringComparison.OrdinalIgnoreCase) &&
                attr.Value is AttributeValue.String s &&
                string.Equals(s.Value, "ethos-u", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(attr.Name, "custom_code", StringComparison.OrdinalIgnoreCase) &&
                attr.Value is AttributeValue.String cc &&
                string.Equals(cc.Value, "ethos-u", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Check for ExecuTorch EthosU delegate
        if (node.Operator.Name.Contains("EthosU", StringComparison.OrdinalIgnoreCase) &&
            node.Attributes.Any(a => string.Equals(a.Name, "processed", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public async ValueTask<Subgraph?> ExpandAsync(
        Stream modelStream,
        ModelGraph graph,
        GraphNode node,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelStream);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);

        if (!modelStream.CanSeek)
        {
            throw new ModelParseException("Model stream must be seekable to expand delegate payloads.", 0, node.Name);
        }

        // 1. Check if ExecuTorch vela_bin_stream payload
        NodeAttribute? processedAttr = node.Attributes.FirstOrDefault(a => string.Equals(a.Name, "processed", StringComparison.OrdinalIgnoreCase));
        if (processedAttr?.Value is AttributeValue.Bytes bytesVal)
        {
            return await ExpandExecuTorchDelegateAsync(modelStream, graph, node, bytesVal.Slice, cancellationToken).ConfigureAwait(false);
        }

        // 2. Check if TFLite CUSTOM(ethos-u) payload
        if (node.Inputs.Count > 0 && node.Inputs[0].Tensors.Count > 0 && node.Inputs[0].Tensors[0].Data is TensorDataSlice cmdSlice)
        {
            return await ExpandTfliteDelegateAsync(modelStream, graph, node, cmdSlice, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<string>?> DisassembleAsync(
        Stream modelStream,
        ModelGraph graph,
        GraphNode node,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelStream);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);

        if (!modelStream.CanSeek)
        {
            throw new ModelParseException("Model stream must be seekable to disassemble delegate payloads.", 0, node.Name);
        }

        TensorDataSlice? cmdSlice = null;

        // 1. ExecuTorch vela_bin_stream
        NodeAttribute? processedAttr = node.Attributes.FirstOrDefault(a => string.Equals(a.Name, "processed", StringComparison.OrdinalIgnoreCase));
        if (processedAttr?.Value is AttributeValue.Bytes bytesVal)
        {
            byte[] processedBuf = ArrayPool<byte>.Shared.Rent((int)bytesVal.Slice.ByteLength);
            try
            {
                modelStream.Position = bytesVal.Slice.StreamOffset;
                await modelStream.ReadExactlyAsync(processedBuf.AsMemory(0, (int)bytesVal.Slice.ByteLength), cancellationToken).ConfigureAwait(false);
                VelaBinStream velaStream = VelaBinStreamReader.Read(processedBuf.AsSpan(0, (int)bytesVal.Slice.ByteLength), bytesVal.Slice.StreamOffset);
                if (velaStream.CmdData is not null)
                {
                    cmdSlice = new TensorDataSlice(velaStream.CmdData.Offset, velaStream.CmdData.Length, DataType.UInt8, TensorDataEncoding.Raw);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(processedBuf);
            }
        }
        else if (node.Inputs.Count > 0 && node.Inputs[0].Tensors.Count > 0 && node.Inputs[0].Tensors[0].Data is TensorDataSlice tflSlice)
        {
            cmdSlice = tflSlice;
        }

        if (cmdSlice is null)
        {
            return null;
        }

        byte[] cmdBuf = ArrayPool<byte>.Shared.Rent((int)cmdSlice.ByteLength);
        try
        {
            modelStream.Position = cmdSlice.StreamOffset;
            await modelStream.ReadExactlyAsync(cmdBuf.AsMemory(0, (int)cmdSlice.ByteLength), cancellationToken).ConfigureAwait(false);

            return DisassembleCommandStream(cmdBuf.AsSpan(0, (int)cmdSlice.ByteLength), cmdSlice.StreamOffset);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cmdBuf);
        }
    }

    private static List<string> DisassembleCommandStream(ReadOnlySpan<byte> span, long streamOffset)
    {
        EthosUDriverPayload driverPayload = EthosUDriverPayloadReader.Read(span, streamOffset);
        ReadOnlySpan<byte> wordsSpan = span.Slice(driverPayload.CommandStreamByteOffset, driverPayload.CommandStreamByteLength);

        var lines = new List<string>(driverPayload.CommandWordCount);
        EthosUCommandStreamDecoder.Decode(wordsSpan, (in EthosUCommand cmd) =>
        {
            string line = cmd.IsCmd1
                ? string.Create(CultureInfo.InvariantCulture, $"0x{cmd.ByteOffset:X6}: {cmd.Payload:X8} {cmd.Param:X4} {cmd.Opcode:X4} - {cmd.Name}")
                : string.Create(CultureInfo.InvariantCulture, $"0x{cmd.ByteOffset:X6}:          {cmd.Param:X4} {cmd.Opcode:X4} - {cmd.Name}");
            lines.Add(line);
        });

        return lines;
    }

    private static async ValueTask<Subgraph?> ExpandTfliteDelegateAsync(
        Stream modelStream,
        ModelGraph graph,
        GraphNode node,
        TensorDataSlice cmdSlice,
        CancellationToken cancellationToken)
    {
        // 1. Identify flash weights buffer offset and byte length
        long weightBaseOffset = 0;
        long weightByteLength = 0;
        if (node.Inputs.Count > 1 && node.Inputs[1].Tensors.Count > 0 && node.Inputs[1].Tensors[0].Data is TensorDataSlice flashSlice)
        {
            weightBaseOffset = flashSlice.StreamOffset;
            weightByteLength = flashSlice.ByteLength;
        }

        // 2. Filter out Vela scaffolding inputs (command stream, flash read_only, scratch, scratch_fast)
        List<TensorEdge> runtimeInputTensors = ExtractTfliteRuntimeInputs(node);
        List<TensorEdge> runtimeOutputTensors = ExtractRuntimeOutputs(node);

        // 3. Extract OfflineMemoryAllocation arena offsets mapped by tensor name
        Dictionary<string, uint> arenaOffsetsByTensorName = await TryExtractTfliteArenaOffsetsAsync(
            modelStream,
            graph,
            node,
            cancellationToken).ConfigureAwait(false);

        var inputRegions = new List<EthosUIoRegion>();
        foreach (TensorEdge inTensor in runtimeInputTensors)
        {
            if (arenaOffsetsByTensorName.TryGetValue(inTensor.Name, out uint offset))
            {
                inputRegions.Add(new EthosUIoRegion(1, offset, ComputeTensorByteLength(inTensor), inTensor));
            }
        }

        var outputRegions = new List<EthosUIoRegion>();
        foreach (TensorEdge outTensor in runtimeOutputTensors)
        {
            if (arenaOffsetsByTensorName.TryGetValue(outTensor.Name, out uint offset))
            {
                outputRegions.Add(new EthosUIoRegion(1, offset, ComputeTensorByteLength(outTensor), outTensor));
            }
        }

        // 4. Read command stream slice
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)cmdSlice.ByteLength);
        try
        {
            modelStream.Position = cmdSlice.StreamOffset;
            await modelStream.ReadExactlyAsync(buffer.AsMemory(0, (int)cmdSlice.ByteLength), cancellationToken).ConfigureAwait(false);

            (EthosUDriverPayload driverPayload, List<EthosURegisterSnapshot> snapshots) = DecodeDriverPayloadAndSnapshots(
                buffer.AsSpan(0, (int)cmdSlice.ByteLength),
                cmdSlice.StreamOffset);

            var outerInputs = runtimeInputTensors.Select(t => new GraphPort(t.Name, new[] { t })).ToList();
            var outerOutputs = runtimeOutputTensors.Select(t => new GraphPort(t.Name, new[] { t })).ToList();
            string subgraphName = $"{node.Name} ({driverPayload.Architecture})";

            return EthosUSubgraphBuilder.Build(
                subgraphName,
                driverPayload,
                snapshots,
                weightBaseOffset,
                weightByteLength,
                outerInputs,
                outerOutputs,
                inputRegions,
                outputRegions);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async ValueTask<Subgraph?> ExpandExecuTorchDelegateAsync(
        Stream modelStream,
        ModelGraph graph,
        GraphNode node,
        TensorDataSlice processedSlice,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)processedSlice.ByteLength);
        try
        {
            modelStream.Position = processedSlice.StreamOffset;
            await modelStream.ReadExactlyAsync(buffer.AsMemory(0, (int)processedSlice.ByteLength), cancellationToken).ConfigureAwait(false);

            (VelaBinStream velaStream, EthosUDriverPayload? driverPayload, List<EthosURegisterSnapshot>? snapshots) = DecodeExecuTorchVelaStream(
                buffer.AsSpan(0, (int)processedSlice.ByteLength),
                processedSlice.StreamOffset);

            if (driverPayload is null || snapshots is null)
            {
                return null;
            }

            List<TensorEdge> runtimeInputTensors = node.Inputs
                .Where(p => p.Tensors.Count > 0)
                .Select(p => p.Tensors[0])
                .ToList();

            List<TensorEdge> runtimeOutputTensors = ExtractRuntimeOutputs(node);

            var inputRegions = new List<EthosUIoRegion>(velaStream.Inputs.Count);
            var outerInputTensors = new List<TensorEdge>(Math.Max(runtimeInputTensors.Count, velaStream.Inputs.Count));
            for (int i = 0; i < velaStream.Inputs.Count; i++)
            {
                VelaIoTensor io = velaStream.Inputs[i];
                TensorEdge tensor = i < runtimeInputTensors.Count
                    ? runtimeInputTensors[i]
                    : CreateSynthesizedIoTensor($"in_{i}", io, TensorKind.GraphInput);

                inputRegions.Add(new EthosUIoRegion(io.Region, (uint)io.Offset, ComputeVelaIoByteLength(io), tensor));
                outerInputTensors.Add(tensor);
            }

            for (int i = velaStream.Inputs.Count; i < runtimeInputTensors.Count; i++)
            {
                outerInputTensors.Add(runtimeInputTensors[i]);
            }

            var outputRegions = new List<EthosUIoRegion>(velaStream.Outputs.Count);
            var outerOutputTensors = new List<TensorEdge>(Math.Max(runtimeOutputTensors.Count, velaStream.Outputs.Count));
            for (int i = 0; i < velaStream.Outputs.Count; i++)
            {
                VelaIoTensor io = velaStream.Outputs[i];
                TensorEdge tensor = i < runtimeOutputTensors.Count
                    ? runtimeOutputTensors[i]
                    : CreateSynthesizedIoTensor($"out_{i}", io, TensorKind.GraphOutput);

                outputRegions.Add(new EthosUIoRegion(io.Region, (uint)io.Offset, ComputeVelaIoByteLength(io), tensor));
                outerOutputTensors.Add(tensor);
            }

            for (int i = velaStream.Outputs.Count; i < runtimeOutputTensors.Count; i++)
            {
                outerOutputTensors.Add(runtimeOutputTensors[i]);
            }

            var outerInputs = outerInputTensors.Select(t => new GraphPort(t.Name, new[] { t })).ToList();
            var outerOutputs = outerOutputTensors.Select(t => new GraphPort(t.Name, new[] { t })).ToList();

            long weightBaseOffset = velaStream.WeightData?.Offset ?? 0;
            long weightByteLength = velaStream.WeightData?.Length ?? 0;
            string subgraphName = $"{node.Name} ({driverPayload.Architecture})";

            return EthosUSubgraphBuilder.Build(
                subgraphName,
                driverPayload,
                snapshots,
                weightBaseOffset,
                weightByteLength,
                outerInputs,
                outerOutputs,
                inputRegions,
                outputRegions);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static List<TensorEdge> ExtractTfliteRuntimeInputs(GraphNode node)
    {
        var result = new List<TensorEdge>();
        string? scratchName = node.Inputs.Count > 2 && node.Inputs[2].Tensors.Count > 0
            ? node.Inputs[2].Tensors[0].Name
            : null;

        for (int i = 0; i < node.Inputs.Count; i++)
        {
            if (node.Inputs[i].Tensors.Count == 0)
            {
                continue;
            }

            TensorEdge tensor = node.Inputs[i].Tensors[0];

            // Skip Vela's internal scaffolding ports:
            // [0] ethos_u_command_stream, [1] read_only (flash weights), [2] scratch, [3] scratch / scratch_fast
            if (i == 0)
            {
                continue;
            }

            if (i == 1 && (tensor.Data is TensorDataSlice || string.Equals(tensor.Name, "read_only", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if ((i == 2 || i == 3) &&
                (string.Equals(tensor.Name, "scratch", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tensor.Name, "scratch_fast", StringComparison.OrdinalIgnoreCase) ||
                 (i == 3 && scratchName is not null && string.Equals(tensor.Name, scratchName, StringComparison.Ordinal))))
            {
                continue;
            }

            result.Add(tensor);
        }

        return result;
    }

    private static List<TensorEdge> ExtractRuntimeOutputs(GraphNode node)
    {
        var result = new List<TensorEdge>(node.Outputs.Count);
        for (int i = 0; i < node.Outputs.Count; i++)
        {
            if (node.Outputs[i].Tensors.Count > 0)
            {
                result.Add(node.Outputs[i].Tensors[0]);
            }
        }
        return result;
    }

    private static uint ComputeTensorByteLength(TensorEdge tensor)
    {
        long elemSize = EthosUSubgraphBuilder.GetElementByteSize(tensor.Descriptor.DataType);
        TensorShape? shape = tensor.Descriptor.Shape;
        if (shape is null || shape.IsScalar)
        {
            return (uint)elemSize;
        }

        long elements = 1;
        bool hasFixed = false;
        for (int i = 0; i < shape.Dimensions.Count; i++)
        {
            if (shape.Dimensions[i] is TensorDimension.Fixed f && f.Value > 0)
            {
                elements *= f.Value;
                hasFixed = true;
            }
        }

        long total = (hasFixed ? elements : 1) * elemSize;
        return (uint)Math.Clamp(total, 1, uint.MaxValue);
    }

    private static uint ComputeVelaIoByteLength(VelaIoTensor io)
    {
        long elements = 1;
        for (int i = 0; i < io.Shape.Count; i++)
        {
            if (io.Shape[i] > 0)
            {
                elements *= io.Shape[i];
            }
        }

        long total = elements * Math.Max(1, io.ElementSize);
        return (uint)Math.Clamp(total, 1, uint.MaxValue);
    }

    private static TensorEdge CreateSynthesizedIoTensor(string name, VelaIoTensor io, TensorKind kind)
    {
        DataType dataType = io.ElementSize switch
        {
            1 => DataType.Int8,
            2 => DataType.Int16,
            4 => DataType.Int32,
            _ => DataType.UInt8,
        };

        var dims = new List<TensorDimension>(io.Shape.Count);
        for (int i = 0; i < io.Shape.Count; i++)
        {
            if (io.Shape[i] > 1 || dims.Count > 0 || i == io.Shape.Count - 1)
            {
                dims.Add(new TensorDimension.Fixed(Math.Max(1, io.Shape[i])));
            }
        }

        var descriptor = new TensorDescriptor(dataType, new TensorShape(dims));
        return new TensorEdge(name, descriptor, kind);
    }

    private static async Task<Dictionary<string, uint>> TryExtractTfliteArenaOffsetsAsync(
        Stream modelStream,
        ModelGraph graph,
        GraphNode node,
        CancellationToken cancellationToken)
    {
        var offsetsByName = new Dictionary<string, uint>(StringComparer.Ordinal);

        if (graph.Metadata.TryGetValue("metadata.OfflineMemoryAllocation.offset", out string? offsetStr) &&
            graph.Metadata.TryGetValue("metadata.OfflineMemoryAllocation.length", out string? lengthStr) &&
            long.TryParse(offsetStr, CultureInfo.InvariantCulture, out long metaOffset) &&
            int.TryParse(lengthStr, CultureInfo.InvariantCulture, out int metaLength) &&
            metaLength >= 12)
        {
            Subgraph? owningSubgraph = FindOwningSubgraph(graph, node) ?? graph.PrimarySubgraph;
            if (owningSubgraph is null)
            {
                return offsetsByName;
            }

            byte[] metaBuf = ArrayPool<byte>.Shared.Rent(metaLength);
            try
            {
                modelStream.Position = metaOffset;
                await modelStream.ReadExactlyAsync(metaBuf.AsMemory(0, metaLength), cancellationToken).ConfigureAwait(false);

                PopulateArenaOffsets(metaBuf.AsSpan(0, metaLength), owningSubgraph.Tensors, offsetsByName);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(metaBuf);
            }
        }

        return offsetsByName;
    }

    private static (EthosUDriverPayload DriverPayload, List<EthosURegisterSnapshot> Snapshots) DecodeDriverPayloadAndSnapshots(
        ReadOnlySpan<byte> cmdSpan,
        long streamOffset)
    {
        EthosUDriverPayload driverPayload = EthosUDriverPayloadReader.Read(cmdSpan, streamOffset);
        ReadOnlySpan<byte> wordsSpan = cmdSpan.Slice(
            driverPayload.CommandStreamByteOffset,
            driverPayload.CommandStreamByteLength);

        var state = new EthosURegisterState();
        var snapshots = new List<EthosURegisterSnapshot>();

        EthosUCommandStreamDecoder.Decode(wordsSpan, (in EthosUCommand cmd) =>
        {
            state.Apply(in cmd);
            if (EthosUOpcodeCatalog.IsExecutableOperation(in cmd))
            {
                snapshots.Add(state.CreateSnapshot(cmd.Opcode, cmd.Param));
            }
        });

        return (driverPayload, snapshots);
    }

    private static (VelaBinStream VelaStream, EthosUDriverPayload? DriverPayload, List<EthosURegisterSnapshot>? Snapshots) DecodeExecuTorchVelaStream(
        ReadOnlySpan<byte> processedSpan,
        long processedStreamOffset)
    {
        VelaBinStream velaStream = VelaBinStreamReader.Read(processedSpan, processedStreamOffset);
        if (velaStream.CmdData is null)
        {
            return (velaStream, null, null);
        }

        int cmdLocalOffset = (int)(velaStream.CmdData.Offset - processedStreamOffset);
        ReadOnlySpan<byte> cmdSpan = processedSpan.Slice(cmdLocalOffset, velaStream.CmdData.Length);
        (EthosUDriverPayload driverPayload, List<EthosURegisterSnapshot> snapshots) = DecodeDriverPayloadAndSnapshots(
            cmdSpan,
            velaStream.CmdData.Offset);

        return (velaStream, driverPayload, snapshots);
    }

    private static void PopulateArenaOffsets(
        ReadOnlySpan<byte> span,
        IReadOnlyList<TensorEdge> tensors,
        Dictionary<string, uint> offsetsByName)
    {
        _ = BinaryPrimitives.ReadInt32LittleEndian(span[..4]); // version
        _ = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4, 4)); // subgraphIdx
        int nbrTensors = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8, 4));

        if (nbrTensors > 0 && 12 + nbrTensors * 4 <= span.Length)
        {
            int pos = 12;
            for (int i = 0; i < nbrTensors && i < tensors.Count; i++)
            {
                int arenaOffset = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4));
                if (arenaOffset >= 0)
                {
                    offsetsByName[tensors[i].Name] = (uint)arenaOffset;
                }
                pos += 4;
            }
        }
    }

    private static Subgraph? FindOwningSubgraph(ModelGraph graph, GraphNode node)
    {
        for (int s = 0; s < graph.Subgraphs.Count; s++)
        {
            Subgraph sg = graph.Subgraphs[s];
            for (int n = 0; n < sg.Nodes.Count; n++)
            {
                if (ReferenceEquals(sg.Nodes[n], node) || string.Equals(sg.Nodes[n].Name, node.Name, StringComparison.Ordinal))
                {
                    return sg;
                }
            }
        }

        return null;
    }
}
