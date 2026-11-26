using System.Globalization;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Gguf;

/// <summary>
/// Synthesizes a hierarchical block-level <see cref="ModelGraph"/> from a parsed GGUF header, metadata table, and tensor info list.
/// </summary>
internal static class GgufGraphSynthesizer
{
    private static readonly string[] CanonicalStageOrder =
    [
        "bert",
        "bert_encoder",
        "text_encoder",
        "encoder",
        "predictor",
        "decoder",
        "depformer",
        "mm",
        "text_head",
        "audio_head",
    ];

    /// <summary>
    /// Builds a <see cref="ModelGraph"/> from <paramref name="parsed"/> without loading any tensor weights.
    /// </summary>
    public static ModelGraph Synthesize(GgufParsedFile parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        ModelFormatInfo format = new(ModelFormatId.Gguf, "GGUF", $"v{parsed.Header.Version}");
        string? producer = ResolveProducer(parsed.Metadata);

        List<ParseDiagnostic> diagnostics = [];
        if (parsed.IntegerMetadata.TryGetValue("split.count", out long splitCount) && splitCount > 1)
        {
            long splitNo = parsed.IntegerMetadata.GetValueOrDefault("split.no");
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Info,
                "DNV_GGUF_SPLIT_SHARD",
                $"GGUF split shard {splitNo + 1} of {splitCount}."));
        }

        if (parsed.Tensors.Count == 0)
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Info,
                "DNV_GGUF_EMPTY_TENSORS",
                "GGUF file contains metadata only and zero tensor entries."));
        }

        string arch = parsed.Metadata.GetValueOrDefault("general.architecture") ?? "gguf";
        ArchParams archParams = ExtractArchParams(arch, parsed.IntegerMetadata);

        // Convert every GGUF tensor into an immutable TensorEdge (TensorKind.Initializer).
        List<TensorEntry> entries = new(parsed.Tensors.Count);
        List<TensorEdge> allTensors = new(parsed.Tensors.Count + 16);
        for (int i = 0; i < parsed.Tensors.Count; i++)
        {
            GgufTensorRecord rec = parsed.Tensors[i];
            TensorEdge edge = CreateInitializerEdge(rec);
            entries.Add(new TensorEntry(rec, edge));
            allTensors.Add(edge);
        }

        // Partition tensors into structural stages.
        List<TensorEntry> tokenEmbeds = [];
        List<TensorEntry> posEmbeds = [];
        List<TensorEntry> typeEmbeds = [];
        List<TensorEntry> textEmbeds = [];
        List<TensorEntry> audioEmbeds = [];
        List<TensorEntry> visionEmbeds = [];

        Dictionary<(string Prefix, int Index), List<TensorEntry>> indexedBlocks = new();
        Dictionary<string, List<TensorEntry>> namedStages = new(StringComparer.Ordinal);
        List<string> namedStageOrder = [];

        List<TensorEntry> outputNorms = [];
        List<TensorEntry> outputHeads = [];

        for (int i = 0; i < entries.Count; i++)
        {
            TensorEntry entry = entries[i];
            string name = entry.Record.Name;

            if (TryParseIndexedBlock(name, out string blockPrefix, out int blockIndex, out string suffix))
            {
                entry.Suffix = suffix;
                (string, int) key = (blockPrefix, blockIndex);
                if (!indexedBlocks.TryGetValue(key, out List<TensorEntry>? bucket))
                {
                    bucket = [];
                    indexedBlocks[key] = bucket;
                }

                bucket.Add(entry);
                continue;
            }

            if (IsPrefix(name, "token_embd"))
            {
                tokenEmbeds.Add(entry);
            }
            else if (IsPrefix(name, "position_embd") || IsPrefix(name, "pos_embd"))
            {
                posEmbeds.Add(entry);
            }
            else if (IsPrefix(name, "token_types"))
            {
                typeEmbeds.Add(entry);
            }
            else if (IsPrefix(name, "text_embd"))
            {
                textEmbeds.Add(entry);
            }
            else if (IsPrefix(name, "audio_embd"))
            {
                audioEmbeds.Add(entry);
            }
            else if (IsPrefix(name, "v"))
            {
                visionEmbeds.Add(entry);
            }
            else if (IsPrefix(name, "output_norm") || IsPrefix(name, "ln_f"))
            {
                outputNorms.Add(entry);
            }
            else if (IsPrefix(name, "output") || IsPrefix(name, "lm_head"))
            {
                outputHeads.Add(entry);
            }
            else
            {
                string stageKey = ExtractTopPrefix(name);
                if (!namedStages.TryGetValue(stageKey, out List<TensorEntry>? stageList))
                {
                    stageList = [];
                    namedStages[stageKey] = stageList;
                    namedStageOrder.Add(stageKey);
                }

                stageList.Add(entry);
            }
        }

        // Assemble ordered high-level module pipeline.
        List<ModuleSpec> pipeline = [];

        AddPrePostModuleIfNonEmpty(pipeline, "token_embd", "TokenEmbedding", OperatorCategory.Layer, tokenEmbeds);
        AddPrePostModuleIfNonEmpty(pipeline, "position_embd", "PositionEmbedding", OperatorCategory.Layer, posEmbeds);
        AddPrePostModuleIfNonEmpty(pipeline, "token_types", "TokenTypeEmbedding", OperatorCategory.Layer, typeEmbeds);
        AddPrePostModuleIfNonEmpty(pipeline, "text_embd", "TextEmbedding", OperatorCategory.Layer, textEmbeds);
        AddPrePostModuleIfNonEmpty(pipeline, "audio_embd", "AudioEmbedding", OperatorCategory.Layer, audioEmbeds);
        AddPrePostModuleIfNonEmpty(pipeline, "vision_embd", "VisionEmbedding", OperatorCategory.Layer, visionEmbeds);

        // Order named stages if any appear BEFORE indexed blocks vs. standalone audio pipelines.
        // Emit vision/encoder indexed blocks first if present, then main blocks, then decoder/depth blocks.
        List<(string Prefix, int Index, List<TensorEntry> Items)> sortedBlocks = indexedBlocks
            .Select(kv => (kv.Key.Prefix, kv.Key.Index, kv.Value))
            .OrderBy(b => GetBlockPrefixOrder(b.Prefix))
            .ThenBy(b => b.Prefix, StringComparer.Ordinal)
            .ThenBy(b => b.Index)
            .ToList();

        // Add pre/body named stages (e.g. audio bert/text_encoder/predictor/decoder), then indexed blocks, then head stages.
        List<string> orderedNamedStages = OrderNamedStages(namedStageOrder);
        List<string> headNamedStages = [];
        foreach (string stageKey in orderedNamedStages)
        {
            if (IsPostBlockNamedStage(stageKey))
            {
                headNamedStages.Add(stageKey);
                continue;
            }

            List<TensorEntry> stageItems = namedStages[stageKey];
            (string opName, OperatorCategory cat) = ClassifyNamedStage(stageKey);
            pipeline.Add(new ModuleSpec(
                Name: stageKey,
                OpName: opName,
                Category: cat,
                Items: stageItems,
                BlockPrefix: null,
                BlockIndex: null));
        }

        foreach ((string prefix, int index, List<TensorEntry> items) in sortedBlocks)
        {
            string nodeName = $"{prefix}.{index.ToString(CultureInfo.InvariantCulture)}";
            (string blockOp, OperatorCategory blockCat) = ClassifyIndexedBlock(prefix, items);
            pipeline.Add(new ModuleSpec(
                Name: nodeName,
                OpName: blockOp,
                Category: blockCat,
                Items: items,
                BlockPrefix: prefix,
                BlockIndex: index));
        }

        foreach (string stageKey in headNamedStages)
        {
            List<TensorEntry> stageItems = namedStages[stageKey];
            (string opName, OperatorCategory cat) = ClassifyNamedStage(stageKey);
            pipeline.Add(new ModuleSpec(
                Name: stageKey,
                OpName: opName,
                Category: cat,
                Items: stageItems,
                BlockPrefix: null,
                BlockIndex: null));
        }

        AddPrePostModuleIfNonEmpty(pipeline, "output_norm", "OutputNorm", OperatorCategory.Normalization, outputNorms);
        AddPrePostModuleIfNonEmpty(pipeline, "output", "LMHead", OperatorCategory.Layer, outputHeads);

        // Synthesize graph boundary ports and sequential activation edges.
        (TensorEdge inputEdge, TensorShape hiddenShape, TensorEdge outputEdgeTemplate) =
            SynthesizeBoundaryDescriptors(arch, archParams, outputHeads, pipeline.Count > 0);

        allTensors.Insert(0, inputEdge);

        List<GraphNode> nodes = new(pipeline.Count);
        TensorEdge currentActivation = inputEdge;

        for (int i = 0; i < pipeline.Count; i++)
        {
            ModuleSpec spec = pipeline[i];
            bool isLast = i == pipeline.Count - 1;

            TensorEdge outEdge = isLast
                ? outputEdgeTemplate
                : new TensorEdge(
                    Name: $"act_{i.ToString(CultureInfo.InvariantCulture)}_{spec.Name}",
                    Descriptor: new TensorDescriptor(DataType.Float32, hiddenShape),
                    Kind: TensorKind.Activation);

            allTensors.Add(outEdge);

            List<NodePort> inputPorts = new(spec.Items.Count + 1)
            {
                new NodePort("x", [currentActivation])
            };

            for (int t = 0; t < spec.Items.Count; t++)
            {
                TensorEntry item = spec.Items[t];
                string portName = StripPrefix(item.Record.Name, spec.Name);
                inputPorts.Add(new NodePort(portName, [item.Edge]));
            }

            List<NodeAttribute> attributes = BuildModuleAttributes(spec, arch, archParams);
            if (spec.BlockIndex.HasValue)
            {
                Subgraph blockSubgraph = SynthesizeBlockSubgraph(spec, hiddenShape, arch, archParams);
                attributes.Add(new NodeAttribute("subgraph", new AttributeValue.Graph(blockSubgraph)));
            }

            nodes.Add(new GraphNode(
                Name: spec.Name,
                Operator: new OperatorInfo(spec.OpName, spec.Category, Domain: "gguf"),
                Inputs: inputPorts,
                Outputs: [new NodePort("y", [outEdge])],
                Attributes: attributes));

            currentActivation = outEdge;
        }

        List<GraphPort> graphInputs = [new GraphPort(inputEdge.Name, [inputEdge])];
        List<GraphPort> graphOutputs = pipeline.Count > 0
            ? [new GraphPort(outputEdgeTemplate.Name, [outputEdgeTemplate])]
            : [];

        string subgraphName = parsed.Metadata.GetValueOrDefault("general.name") ?? arch;
        if (string.IsNullOrWhiteSpace(subgraphName))
        {
            subgraphName = "main";
        }

        Subgraph primary = new(
            Name: subgraphName,
            Nodes: nodes,
            Tensors: allTensors,
            Inputs: graphInputs,
            Outputs: graphOutputs);

        return new ModelGraph(
            Format: format,
            Producer: producer,
            Subgraphs: [primary],
            Metadata: parsed.Metadata,
            Diagnostics: diagnostics);
    }

    private static Subgraph SynthesizeBlockSubgraph(
        ModuleSpec spec,
        TensorShape hiddenShape,
        string arch,
        ArchParams archParams)
    {
        List<TensorEntry> attnNorms = [];
        List<TensorEntry> attnWeights = [];
        List<TensorEntry> postAttnNorms = [];
        List<TensorEntry> ssmWeights = [];
        List<TensorEntry> engramWeights = [];
        List<TensorEntry> ffnNorms = [];
        List<TensorEntry> ffnWeights = [];
        List<TensorEntry> postFfnNorms = [];
        Dictionary<string, List<TensorEntry>> otherBuckets = new(StringComparer.Ordinal);
        List<string> otherOrder = [];

        for (int i = 0; i < spec.Items.Count; i++)
        {
            TensorEntry entry = spec.Items[i];
            string suffix = entry.Suffix ?? StripPrefix(entry.Record.Name, spec.Name);

            if (suffix.StartsWith("attn_norm", StringComparison.Ordinal))
            {
                attnNorms.Add(entry);
            }
            else if (suffix.StartsWith("post_attention_norm", StringComparison.Ordinal) ||
                     suffix.StartsWith("post_attn_norm", StringComparison.Ordinal))
            {
                postAttnNorms.Add(entry);
            }
            else if (suffix.StartsWith("attn_", StringComparison.Ordinal) ||
                     suffix.StartsWith("self_attn", StringComparison.Ordinal))
            {
                attnWeights.Add(entry);
            }
            else if (suffix.StartsWith("ssm_", StringComparison.Ordinal) ||
                     suffix.StartsWith("shortconv", StringComparison.Ordinal))
            {
                ssmWeights.Add(entry);
            }
            else if (suffix.StartsWith("engram_", StringComparison.Ordinal))
            {
                engramWeights.Add(entry);
            }
            else if (suffix.StartsWith("ffn_norm", StringComparison.Ordinal))
            {
                ffnNorms.Add(entry);
            }
            else if (suffix.StartsWith("post_ffw_norm", StringComparison.Ordinal) ||
                     suffix.StartsWith("post_ffn_norm", StringComparison.Ordinal))
            {
                postFfnNorms.Add(entry);
            }
            else if (suffix.StartsWith("ffn_", StringComparison.Ordinal))
            {
                ffnWeights.Add(entry);
            }
            else
            {
                string key = ExtractTopPrefix(suffix);
                if (!otherBuckets.TryGetValue(key, out List<TensorEntry>? bucket))
                {
                    bucket = [];
                    otherBuckets[key] = bucket;
                    otherOrder.Add(key);
                }

                bucket.Add(entry);
            }
        }

        List<(string Name, string Op, OperatorCategory Cat, List<TensorEntry> Items)> steps = [];
        AddSubStepIfNonEmpty(steps, "attn_norm", "RMSNorm", OperatorCategory.Normalization, attnNorms);
        if (attnWeights.Count > 0)
        {
            bool isMla = attnWeights.Any(e => (e.Suffix ?? string.Empty).Contains("q_a", StringComparison.Ordinal) ||
                                              (e.Suffix ?? string.Empty).Contains("kv_a", StringComparison.Ordinal));
            steps.Add(("attention", isMla ? "MultiHeadLatentAttention" : "Attention", OperatorCategory.Layer, attnWeights));
        }

        AddSubStepIfNonEmpty(steps, "post_attn_norm", "RMSNorm", OperatorCategory.Normalization, postAttnNorms);
        AddSubStepIfNonEmpty(steps, "ssm", "StateSpace", OperatorCategory.Layer, ssmWeights);
        AddSubStepIfNonEmpty(steps, "engram", "EngramMemory", OperatorCategory.Layer, engramWeights);
        AddSubStepIfNonEmpty(steps, "ffn_norm", "RMSNorm", OperatorCategory.Normalization, ffnNorms);

        if (ffnWeights.Count > 0)
        {
            bool isMoe = ffnWeights.Any(e => (e.Suffix ?? string.Empty).Contains("exps", StringComparison.Ordinal) ||
                                             (e.Suffix ?? string.Empty).Contains("gate_inp", StringComparison.Ordinal));
            steps.Add(("ffn", isMoe ? "MoEFeedForward" : "FeedForward", OperatorCategory.Layer, ffnWeights));
        }

        AddSubStepIfNonEmpty(steps, "post_ffn_norm", "RMSNorm", OperatorCategory.Normalization, postFfnNorms);

        for (int i = 0; i < otherOrder.Count; i++)
        {
            string key = otherOrder[i];
            (string op, OperatorCategory cat) = ClassifyNamedStage(key);
            steps.Add((key, op, cat, otherBuckets[key]));
        }

        TensorEdge subIn = new("hidden_in", new TensorDescriptor(DataType.Float32, hiddenShape), TensorKind.GraphInput);
        TensorEdge subOut = new("hidden_out", new TensorDescriptor(DataType.Float32, hiddenShape), TensorKind.GraphOutput);

        List<TensorEdge> subTensors = [subIn];
        for (int i = 0; i < spec.Items.Count; i++)
        {
            subTensors.Add(spec.Items[i].Edge);
        }

        List<GraphNode> subNodes = new(steps.Count);
        TensorEdge current = subIn;
        for (int i = 0; i < steps.Count; i++)
        {
            (string stepName, string opName, OperatorCategory cat, List<TensorEntry> items) = steps[i];
            bool isLast = i == steps.Count - 1;
            TensorEdge next = isLast
                ? subOut
                : new TensorEdge($"sub_act_{i.ToString(CultureInfo.InvariantCulture)}_{stepName}",
                    new TensorDescriptor(DataType.Float32, hiddenShape),
                    TensorKind.Activation);

            subTensors.Add(next);

            List<NodePort> ports = new(items.Count + 1) { new NodePort("x", [current]) };
            for (int t = 0; t < items.Count; t++)
            {
                string portName = items[t].Suffix ?? StripPrefix(items[t].Record.Name, spec.Name);
                ports.Add(new NodePort(portName, [items[t].Edge]));
            }

            List<NodeAttribute> attrs =
            [
                new NodeAttribute("tensor_count", new AttributeValue.Int64(items.Count)),
                new NodeAttribute("architecture", new AttributeValue.String(arch)),
            ];

            if (opName is "Attention" or "MultiHeadLatentAttention" && archParams.HeadCount.HasValue)
            {
                attrs.Add(new NodeAttribute("head_count", new AttributeValue.Int64(archParams.HeadCount.Value)));
                if (archParams.HeadCountKv.HasValue)
                {
                    attrs.Add(new NodeAttribute("head_count_kv", new AttributeValue.Int64(archParams.HeadCountKv.Value)));
                }
            }
            else if (opName == "MoEFeedForward" && archParams.ExpertCount.HasValue)
            {
                attrs.Add(new NodeAttribute("expert_count", new AttributeValue.Int64(archParams.ExpertCount.Value)));
                if (archParams.ExpertUsedCount.HasValue)
                {
                    attrs.Add(new NodeAttribute("expert_used_count", new AttributeValue.Int64(archParams.ExpertUsedCount.Value)));
                }
            }

            subNodes.Add(new GraphNode(
                Name: $"{spec.Name}.{stepName}",
                Operator: new OperatorInfo(opName, cat, Domain: "gguf"),
                Inputs: ports,
                Outputs: [new NodePort("y", [next])],
                Attributes: attrs));

            current = next;
        }

        return new Subgraph(
            Name: spec.Name,
            Nodes: subNodes,
            Tensors: subTensors,
            Inputs: [new GraphPort(subIn.Name, [subIn])],
            Outputs: subNodes.Count > 0 ? [new GraphPort(subOut.Name, [subOut])] : []);
    }

    private static List<NodeAttribute> BuildModuleAttributes(ModuleSpec spec, string arch, ArchParams archParams)
    {
        long paramCount = 0;
        HashSet<string> schemes = new(StringComparer.Ordinal);

        for (int i = 0; i < spec.Items.Count; i++)
        {
            GgufTensorRecord rec = spec.Items[i].Record;
            schemes.Add(rec.TypeInfo.Name);

            long elems = 1;
            for (int d = 0; d < rec.RawDims.Length; d++)
            {
                elems = unchecked(elems * (long)rec.RawDims[d]);
            }

            paramCount = unchecked(paramCount + elems);
        }

        List<NodeAttribute> attrs =
        [
            new NodeAttribute("architecture", new AttributeValue.String(arch)),
            new NodeAttribute("tensor_count", new AttributeValue.Int64(spec.Items.Count)),
            new NodeAttribute("parameter_count", new AttributeValue.Int64(paramCount)),
            new NodeAttribute("quantization_mix", new AttributeValue.String(string.Join(", ", schemes.OrderBy(s => s, StringComparer.Ordinal)))),
        ];

        if (spec.BlockIndex.HasValue)
        {
            attrs.Add(new NodeAttribute("block_index", new AttributeValue.Int64(spec.BlockIndex.Value)));
            if (archParams.HeadCount.HasValue)
            {
                attrs.Add(new NodeAttribute("head_count", new AttributeValue.Int64(archParams.HeadCount.Value)));
            }

            if (archParams.HeadCountKv.HasValue)
            {
                attrs.Add(new NodeAttribute("head_count_kv", new AttributeValue.Int64(archParams.HeadCountKv.Value)));
            }

            if (archParams.FeedForwardLength.HasValue)
            {
                attrs.Add(new NodeAttribute("feed_forward_length", new AttributeValue.Int64(archParams.FeedForwardLength.Value)));
            }

            if (archParams.ExpertCount.HasValue)
            {
                attrs.Add(new NodeAttribute("expert_count", new AttributeValue.Int64(archParams.ExpertCount.Value)));
            }

            if (archParams.ExpertUsedCount.HasValue)
            {
                attrs.Add(new NodeAttribute("expert_used_count", new AttributeValue.Int64(archParams.ExpertUsedCount.Value)));
            }
        }

        return attrs;
    }

    private static (TensorEdge Input, TensorShape HiddenShape, TensorEdge Output) SynthesizeBoundaryDescriptors(
        string arch,
        ArchParams archParams,
        List<TensorEntry> outputHeads,
        bool hasNodes)
    {
        TensorDimension batchDim = new TensorDimension.Symbolic("batch");
        TensorDimension seqDim = new TensorDimension.Symbolic("seq_len");

        TensorShape hiddenShape = archParams.EmbeddingLength is > 0
            ? new TensorShape([batchDim, seqDim, new TensorDimension.Fixed(archParams.EmbeddingLength.Value)])
            : new TensorShape([batchDim, seqDim]);

        bool isAudioFeaturesInput = arch.Contains("whisper", StringComparison.OrdinalIgnoreCase);
        TensorEdge inputEdge = isAudioFeaturesInput
            ? new TensorEdge(
                "audio_features",
                new TensorDescriptor(DataType.Float32, new TensorShape([batchDim, new TensorDimension.Fixed(80), new TensorDimension.Symbolic("frames")])),
                TensorKind.GraphInput)
            : new TensorEdge(
                "input_ids",
                new TensorDescriptor(DataType.Int32, new TensorShape([batchDim, seqDim])),
                TensorKind.GraphInput);

        bool isAudioWaveformOutput = arch.Contains("kokoro", StringComparison.OrdinalIgnoreCase)
            || arch.Contains("moshi", StringComparison.OrdinalIgnoreCase);

        TensorEdge outputEdge;
        if (isAudioWaveformOutput && outputHeads.Count == 0)
        {
            outputEdge = new TensorEdge(
                "audio",
                new TensorDescriptor(DataType.Float32, new TensorShape([batchDim, new TensorDimension.Symbolic("samples")])),
                TensorKind.GraphOutput);
        }
        else if (!hasNodes)
        {
            outputEdge = new TensorEdge("output", new TensorDescriptor(DataType.Float32, hiddenShape), TensorKind.GraphOutput);
        }
        else
        {
            long? vocabSize = archParams.VocabSize;
            if (!vocabSize.HasValue && outputHeads.Count > 0 && outputHeads[0].Record.RawDims.Length >= 2)
            {
                // In GGML innermost-first dims, [embed, vocab] -> RawDims[^1] is vocab.
                vocabSize = (long)outputHeads[0].Record.RawDims[^1];
            }

            TensorShape outShape = vocabSize is > 0
                ? new TensorShape([batchDim, seqDim, new TensorDimension.Fixed(vocabSize.Value)])
                : hiddenShape;

            outputEdge = new TensorEdge("logits", new TensorDescriptor(DataType.Float32, outShape), TensorKind.GraphOutput);
        }

        return (inputEdge, hiddenShape, outputEdge);
    }

    private static TensorEdge CreateInitializerEdge(GgufTensorRecord rec)
    {
        TensorShape shape;
        if (rec.RawDims.Length == 0)
        {
            shape = TensorShape.Scalar;
        }
        else
        {
            TensorDimension[] dims = new TensorDimension[rec.RawDims.Length];
            for (int i = 0; i < rec.RawDims.Length; i++)
            {
                dims[i] = new TensorDimension.Fixed((long)rec.RawDims[rec.RawDims.Length - 1 - i]);
            }

            shape = new TensorShape(dims);
        }

        QuantizationInfo? quantization = rec.TypeInfo.IsQuantized
            ? new BlockQuantization(rec.TypeInfo.Name, rec.TypeInfo.BlockSize, rec.TypeInfo.NominalBits)
            : null;

        TensorDataEncoding encoding = rec.TypeInfo.IsQuantized
            ? TensorDataEncoding.GgmlQuantizedBlock
            : TensorDataEncoding.Raw;

        TensorDescriptor descriptor = new(rec.TypeInfo.DataType, shape, quantization);
        TensorDataSlice slice = new(rec.AbsoluteOffset, rec.ByteLength, rec.TypeInfo.DataType, encoding);

        return new TensorEdge(rec.Name, descriptor, TensorKind.Initializer, slice);
    }

    private static bool TryParseIndexedBlock(string name, out string prefix, out int index, out string suffix)
    {
        // Supports patterns like: blk.0.attn_q.weight, encoder.blk.1.attn.weight, decoder.blk.0.x, dep.0.attn.weight, v.blk.0.x, layers.0.x
        ReadOnlySpan<char> span = name.AsSpan();
        int searchStart = 0;
        while (searchStart < span.Length)
        {
            int dot = span[searchStart..].IndexOf('.');
            if (dot < 0)
            {
                break;
            }

            int segmentStart = searchStart;
            int dotAbs = searchStart + dot;
            ReadOnlySpan<char> segment = span[segmentStart..dotAbs];

            if (segment.Equals("blk", StringComparison.Ordinal)
                || segment.Equals("dep", StringComparison.Ordinal)
                || segment.Equals("layers", StringComparison.Ordinal))
            {
                int afterDot = dotAbs + 1;
                int nextDotRel = span[afterDot..].IndexOf('.');
                if (nextDotRel > 0)
                {
                    ReadOnlySpan<char> indexSpan = span.Slice(afterDot, nextDotRel);
                    if (int.TryParse(indexSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedIdx))
                    {
                        prefix = name[..dotAbs];
                        index = parsedIdx;
                        suffix = name[(afterDot + nextDotRel + 1)..];
                        return true;
                    }
                }
            }

            searchStart = dotAbs + 1;
        }

        prefix = string.Empty;
        index = 0;
        suffix = string.Empty;
        return false;
    }

    private static (string OpName, OperatorCategory Category) ClassifyIndexedBlock(string prefix, List<TensorEntry> items)
    {
        if (prefix == "encoder.blk")
        {
            return ("EncoderBlock", OperatorCategory.Layer);
        }

        if (prefix == "decoder.blk")
        {
            return ("DecoderBlock", OperatorCategory.Layer);
        }

        if (prefix == "v.blk")
        {
            return ("VisionTransformerBlock", OperatorCategory.Layer);
        }

        if (prefix == "dep")
        {
            return ("DepthTransformerBlock", OperatorCategory.Layer);
        }

        bool hasEngram = false;
        bool hasSsm = false;
        bool hasAttn = false;
        bool hasMoe = false;

        for (int i = 0; i < items.Count; i++)
        {
            string suffix = items[i].Suffix ?? items[i].Record.Name;
            if (suffix.Contains("engram_", StringComparison.Ordinal))
            {
                hasEngram = true;
            }
            if (suffix.Contains("ssm_", StringComparison.Ordinal) || suffix.Contains("shortconv", StringComparison.Ordinal))
            {
                hasSsm = true;
            }
            if (suffix.Contains("attn_", StringComparison.Ordinal) || suffix.Contains("self_attn", StringComparison.Ordinal))
            {
                hasAttn = true;
            }
            if (suffix.Contains("exps", StringComparison.Ordinal) || suffix.Contains("gate_inp", StringComparison.Ordinal))
            {
                hasMoe = true;
            }
        }

        if (hasEngram)
        {
            return ("EngramTransformerBlock", OperatorCategory.Layer);
        }

        if (hasSsm && hasAttn)
        {
            return ("HybridSSMBlock", OperatorCategory.Layer);
        }

        if (hasSsm)
        {
            return ("SSMBlock", OperatorCategory.Layer);
        }

        if (hasMoe)
        {
            return ("MoETransformerBlock", OperatorCategory.Layer);
        }

        return ("TransformerBlock", OperatorCategory.Layer);
    }

    private static (string OpName, OperatorCategory Category) ClassifyNamedStage(string stageKey)
    {
        if (stageKey.Contains("norm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stageKey, "ln_f", StringComparison.OrdinalIgnoreCase))
        {
            return ("LayerNorm", OperatorCategory.Normalization);
        }

        if (stageKey.Contains("pool", StringComparison.OrdinalIgnoreCase))
        {
            return ("Pooling", OperatorCategory.Pooling);
        }

        if (stageKey.Contains("act", StringComparison.OrdinalIgnoreCase))
        {
            return ("Activation", OperatorCategory.Activation);
        }

        return stageKey switch
        {
            "bert" => ("AlbertEncoder", OperatorCategory.Layer),
            "bert_encoder" => ("BertProjection", OperatorCategory.Layer),
            "text_encoder" => ("TextEncoder", OperatorCategory.Layer),
            "encoder" => ("Encoder", OperatorCategory.Layer),
            "predictor" => ("ProsodyPredictor", OperatorCategory.Layer),
            "decoder" => ("AudioDecoder", OperatorCategory.Layer),
            "depformer" => ("DepthTransformer", OperatorCategory.Layer),
            "mm" => ("MultimodalProjector", OperatorCategory.Layer),
            "text_head" => ("TextHead", OperatorCategory.Layer),
            "audio_head" => ("AudioHead", OperatorCategory.Layer),
            _ => ("Module", OperatorCategory.Layer),
        };
    }

    private static bool IsPostBlockNamedStage(string stageKey) =>
        stageKey.EndsWith("_head", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stageKey, "head", StringComparison.OrdinalIgnoreCase);

    private static List<string> OrderNamedStages(List<string> discovered)
    {
        if (discovered.Count <= 1)
        {
            return discovered;
        }

        Dictionary<string, int> canonicalRank = new(StringComparer.Ordinal);
        for (int i = 0; i < CanonicalStageOrder.Length; i++)
        {
            canonicalRank[CanonicalStageOrder[i]] = i;
        }

        Dictionary<string, int> firstSeenRank = new(StringComparer.Ordinal);
        for (int i = 0; i < discovered.Count; i++)
        {
            firstSeenRank[discovered[i]] = i;
        }

        return discovered
            .OrderBy(s => canonicalRank.TryGetValue(s, out int r) ? r : int.MaxValue)
            .ThenBy(s => firstSeenRank[s])
            .ToList();
    }

    private static int GetBlockPrefixOrder(string prefix) => prefix switch
    {
        "v.blk" => 0,
        "encoder.blk" => 1,
        "blk" => 2,
        "layers" => 2,
        "decoder.blk" => 3,
        "dep" => 4,
        _ => 5,
    };

    private static void AddPrePostModuleIfNonEmpty(
        List<ModuleSpec> pipeline,
        string name,
        string opName,
        OperatorCategory category,
        List<TensorEntry> items)
    {
        if (items.Count > 0)
        {
            pipeline.Add(new ModuleSpec(name, opName, category, items, null, null));
        }
    }

    private static void AddSubStepIfNonEmpty(
        List<(string Name, string Op, OperatorCategory Cat, List<TensorEntry> Items)> steps,
        string name,
        string opName,
        OperatorCategory category,
        List<TensorEntry> items)
    {
        if (items.Count > 0)
        {
            steps.Add((name, opName, category, items));
        }
    }

    private static bool IsPrefix(string name, string prefix) =>
        name.Equals(prefix, StringComparison.Ordinal)
        || (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal) && name[prefix.Length] == '.');

    private static string ExtractTopPrefix(string name)
    {
        int dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static string StripPrefix(string fullName, string prefix)
    {
        if (fullName.Length > prefix.Length + 1
            && fullName.StartsWith(prefix, StringComparison.Ordinal)
            && fullName[prefix.Length] == '.')
        {
            return fullName[(prefix.Length + 1)..];
        }

        return fullName;
    }

    private static string? ResolveProducer(Dictionary<string, string> metadata)
    {
        if (metadata.TryGetValue("general.quantized_by", out string? quantBy) && !string.IsNullOrWhiteSpace(quantBy))
        {
            return quantBy;
        }

        if (metadata.TryGetValue("general.author", out string? author) && !string.IsNullOrWhiteSpace(author))
        {
            return author;
        }

        if (metadata.TryGetValue("general.organization", out string? org) && !string.IsNullOrWhiteSpace(org))
        {
            return org;
        }

        return "GGUF";
    }

    private static ArchParams ExtractArchParams(string arch, Dictionary<string, long> intMetadata)
    {
        long? GetInt(string suffix) =>
            intMetadata.TryGetValue($"{arch}.{suffix}", out long val) ? val : null;

        return new ArchParams(
            EmbeddingLength: GetInt("embedding_length"),
            BlockCount: GetInt("block_count"),
            HeadCount: GetInt("attention.head_count"),
            HeadCountKv: GetInt("attention.head_count_kv"),
            FeedForwardLength: GetInt("feed_forward_length"),
            ExpertCount: GetInt("expert_count"),
            ExpertUsedCount: GetInt("expert_used_count"),
            VocabSize: GetInt("vocab_size"));
    }

    private sealed class TensorEntry(GgufTensorRecord record, TensorEdge edge)
    {
        public GgufTensorRecord Record { get; } = record;
        public TensorEdge Edge { get; } = edge;
        public string? Suffix { get; set; }
    }

    private sealed record ModuleSpec(
        string Name,
        string OpName,
        OperatorCategory Category,
        List<TensorEntry> Items,
        string? BlockPrefix,
        int? BlockIndex);

    private readonly record struct ArchParams(
        long? EmbeddingLength,
        long? BlockCount,
        long? HeadCount,
        long? HeadCountKv,
        long? FeedForwardLength,
        long? ExpertCount,
        long? ExpertUsedCount,
        long? VocabSize);
}
