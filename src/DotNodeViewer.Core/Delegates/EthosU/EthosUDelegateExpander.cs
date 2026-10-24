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

            ReadOnlySpan<byte> span = cmdBuf.AsSpan(0, (int)cmdSlice.ByteLength);
            EthosUDriverPayload driverPayload = EthosUDriverPayloadReader.Read(span, cmdSlice.StreamOffset);
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
        finally
        {
            ArrayPool<byte>.Shared.Return(cmdBuf);
        }
    }

    private static async ValueTask<Subgraph?> ExpandTfliteDelegateAsync(
        Stream modelStream,
        ModelGraph graph,
        GraphNode node,
        TensorDataSlice cmdSlice,
        CancellationToken cancellationToken)
    {
        // 1. Identify flash weights buffer offset
        long weightBaseOffset = 0;
        if (node.Inputs.Count > 1 && node.Inputs[1].Tensors.Count > 0 && node.Inputs[1].Tensors[0].Data is TensorDataSlice flashSlice)
        {
            weightBaseOffset = flashSlice.StreamOffset;
        }

        // 2. Extract OfflineMemoryAllocation arena offsets from metadata if present
        var inputOffsets = new Dictionary<uint, string>();
        var outputOffsets = new Dictionary<uint, string>();

        await TryExtractTfliteArenaOffsetsAsync(modelStream, graph, inputOffsets, outputOffsets, cancellationToken).ConfigureAwait(false);

        // 3. Read command stream slice
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)cmdSlice.ByteLength);
        try
        {
            modelStream.Position = cmdSlice.StreamOffset;
            await modelStream.ReadExactlyAsync(buffer.AsMemory(0, (int)cmdSlice.ByteLength), cancellationToken).ConfigureAwait(false);

            ReadOnlySpan<byte> cmdSpan = buffer.AsSpan(0, (int)cmdSlice.ByteLength);
            EthosUDriverPayload driverPayload = EthosUDriverPayloadReader.Read(cmdSpan, cmdSlice.StreamOffset);

            ReadOnlySpan<byte> wordsSpan = cmdSpan.Slice(
                driverPayload.CommandStreamByteOffset,
                driverPayload.CommandStreamByteLength);

            var state = new EthosURegisterState();
            var snapshots = new List<EthosURegisterSnapshot>();

            EthosUCommandStreamDecoder.Decode(wordsSpan, (in EthosUCommand cmd) =>
            {
                state.Apply(in cmd);
                if (EthosUOpcodeCatalog.IsExecutableOperation(cmd.Opcode))
                {
                    snapshots.Add(state.CreateSnapshot(cmd.Opcode, cmd.Param));
                }
            });

            var outerInputs = node.Inputs.Select(p => new GraphPort(p.Name, p.Tensors)).ToList();
            var outerOutputs = node.Outputs.Select(p => new GraphPort(p.Name, p.Tensors)).ToList();
            string subgraphName = $"{node.Name} ({driverPayload.Architecture})";

            return EthosUSubgraphBuilder.Build(
                subgraphName,
                driverPayload,
                snapshots,
                weightBaseOffset,
                outerInputs,
                outerOutputs,
                inputOffsets,
                outputOffsets);
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

            ReadOnlySpan<byte> processedSpan = buffer.AsSpan(0, (int)processedSlice.ByteLength);
            VelaBinStream velaStream = VelaBinStreamReader.Read(processedSpan, processedSlice.StreamOffset);

            if (velaStream.CmdData is null)
            {
                return null;
            }

            int cmdLocalOffset = (int)(velaStream.CmdData.Offset - processedSlice.StreamOffset);
            ReadOnlySpan<byte> cmdSpan = processedSpan.Slice(cmdLocalOffset, velaStream.CmdData.Length);
            EthosUDriverPayload driverPayload = EthosUDriverPayloadReader.Read(cmdSpan, velaStream.CmdData.Offset);

            ReadOnlySpan<byte> wordsSpan = cmdSpan.Slice(
                driverPayload.CommandStreamByteOffset,
                driverPayload.CommandStreamByteLength);

            var state = new EthosURegisterState();
            var snapshots = new List<EthosURegisterSnapshot>();

            EthosUCommandStreamDecoder.Decode(wordsSpan, (in EthosUCommand cmd) =>
            {
                state.Apply(in cmd);
                if (EthosUOpcodeCatalog.IsExecutableOperation(cmd.Opcode))
                {
                    snapshots.Add(state.CreateSnapshot(cmd.Opcode, cmd.Param));
                }
            });

            var outerInputs = node.Inputs.Select(p => new GraphPort(p.Name, p.Tensors)).ToList();
            var outerOutputs = node.Outputs.Select(p => new GraphPort(p.Name, p.Tensors)).ToList();

            var inputOffsets = new Dictionary<uint, string>();
            for (int i = 0; i < velaStream.Inputs.Count && i < outerInputs.Count; i++)
            {
                inputOffsets[(uint)velaStream.Inputs[i].Offset] = outerInputs[i].Name;
            }

            var outputOffsets = new Dictionary<uint, string>();
            for (int i = 0; i < velaStream.Outputs.Count && i < outerOutputs.Count; i++)
            {
                outputOffsets[(uint)velaStream.Outputs[i].Offset] = outerOutputs[i].Name;
            }

            long weightBaseOffset = velaStream.WeightData?.Offset ?? 0;
            string subgraphName = $"{node.Name} ({driverPayload.Architecture})";

            return EthosUSubgraphBuilder.Build(
                subgraphName,
                driverPayload,
                snapshots,
                weightBaseOffset,
                outerInputs,
                outerOutputs,
                inputOffsets,
                outputOffsets);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task TryExtractTfliteArenaOffsetsAsync(
        Stream modelStream,
        ModelGraph graph,
        Dictionary<uint, string> inputOffsets,
        Dictionary<uint, string> outputOffsets,
        CancellationToken cancellationToken)
    {
        if (graph.Metadata.TryGetValue("metadata.OfflineMemoryAllocation.offset", out string? offsetStr) &&
            graph.Metadata.TryGetValue("metadata.OfflineMemoryAllocation.length", out string? lengthStr) &&
            long.TryParse(offsetStr, CultureInfo.InvariantCulture, out long metaOffset) &&
            int.TryParse(lengthStr, CultureInfo.InvariantCulture, out int metaLength) &&
            metaLength >= 12)
        {
            byte[] metaBuf = ArrayPool<byte>.Shared.Rent(metaLength);
            try
            {
                modelStream.Position = metaOffset;
                await modelStream.ReadExactlyAsync(metaBuf.AsMemory(0, metaLength), cancellationToken).ConfigureAwait(false);

                ReadOnlySpan<byte> span = metaBuf.AsSpan(0, metaLength);
                int version = BinaryPrimitives.ReadInt32LittleEndian(span[..4]);
                int subgraphIdx = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4, 4));
                int nbrTensors = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8, 4));

                if (nbrTensors > 0 && 12 + nbrTensors * 4 <= metaLength && graph.PrimarySubgraph != null)
                {
                    IReadOnlyList<TensorEdge> tensors = graph.PrimarySubgraph.Tensors;
                    int pos = 12;
                    for (int i = 0; i < nbrTensors && i < tensors.Count; i++)
                    {
                        int arenaOffset = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4));
                        if (arenaOffset >= 0)
                        {
                            inputOffsets[(uint)arenaOffset] = tensors[i].Name;
                            outputOffsets[(uint)arenaOffset] = tensors[i].Name;
                        }
                        pos += 4;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(metaBuf);
            }
        }
    }
}
