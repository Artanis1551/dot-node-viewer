using System.Globalization;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Onnx;

/// <summary>
/// Streams an Open Neural Network Exchange (ONNX) <c>ModelProto</c> binary stream into the Unified Graph IR
/// while recording raw weight buffers as lazy <see cref="TensorDataSlice"/> ranges.
/// </summary>
internal sealed class OnnxFormatReader : IModelFormatReader
{
    /// <summary>
    /// Gets a shared singleton instance of <see cref="OnnxFormatReader"/>.
    /// </summary>
    public static OnnxFormatReader Instance { get; } = new();

    /// <inheritdoc/>
    public ModelFormatId FormatId => ModelFormatId.Onnx;

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

        if (source.Length == 0)
        {
            throw new ModelParseException(
                "Cannot parse an empty stream as an ONNX ModelProto.",
                fileOffset: 0,
                context: effectiveOptions.FileName);
        }

        ProtoReader reader = new(
            source,
            startOffset: 0,
            length: source.Length,
            maxMetadataBytes: effectiveOptions.MaxMetadataBytes);

        ModelGraph graph = ReadModelProto(reader, effectiveOptions, cancellationToken);
        return ValueTask.FromResult(graph);
    }

    private static ModelGraph ReadModelProto(
        ProtoReader reader,
        ModelReadOptions options,
        CancellationToken cancellationToken)
    {
        long irVersion = 0;
        string? producerName = null;
        string? producerVersion = null;
        string? domain = null;
        long? modelVersion = null;
        string? docString = null;
        Dictionary<string, int> opsetImports = new(StringComparer.Ordinal);
        Dictionary<string, string> metadata = new(StringComparer.Ordinal);
        List<ParseDiagnostic> diagnostics = [];
        ParsedGraph? primaryGraph = null;
        List<ParsedFunction> functions = [];

        while (!reader.IsAtEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            switch (tag.FieldNumber)
            {
                case 1: // int64 ir_version
                    reader.ExpectWireType(tag.WireType, ProtoWireType.Varint, "ModelProto.ir_version");
                    irVersion = reader.ReadInt64();
                    if (irVersion <= 0 || irVersion > 50)
                    {
                        throw new ModelParseException(
                            $"Unsupported or corrupted ONNX ModelProto.ir_version ({irVersion}).",
                            fileOffset: reader.Position,
                            context: "ModelProto.ir_version");
                    }

                    break;

                case 2: // string producer_name
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "ModelProto.producer_name");
                    producerName = reader.ReadString();
                    break;

                case 3: // string producer_version
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "ModelProto.producer_version");
                    producerVersion = reader.ReadString();
                    break;

                case 4: // string domain
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "ModelProto.domain");
                    domain = reader.ReadString();
                    break;

                case 5: // int64 model_version
                    reader.ExpectWireType(tag.WireType, ProtoWireType.Varint, "ModelProto.model_version");
                    modelVersion = reader.ReadInt64();
                    break;

                case 6: // string doc_string
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "ModelProto.doc_string");
                    docString = reader.ReadString();
                    break;

                case 7: // GraphProto graph
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "ModelProto.graph");
                    long graphLimit = reader.PushMessageLimit();
                    primaryGraph = ReadGraphProto(reader, depth: 0, options, cancellationToken);
                    reader.PopLimit(graphLimit);
                    break;

                case 8: // repeated OperatorSetIdProto opset_import
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "ModelProto.opset_import");
                    (string opsetDomain, int opsetVersion) = ReadOperatorSetIdProto(reader);
                    string normalizedDomain = NormalizeDomain(opsetDomain);
                    opsetImports[normalizedDomain] = opsetVersion;
                    break;

                case 14: // repeated StringStringEntryProto metadata_props
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "ModelProto.metadata_props");
                    (string metaKey, string metaValue) = ReadStringStringEntryProto(reader);
                    if (!string.IsNullOrEmpty(metaKey))
                    {
                        metadata[metaKey] = metaValue;
                    }

                    break;

                case 25: // repeated FunctionProto functions
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "ModelProto.functions");
                    long funcLimit = reader.PushMessageLimit();
                    functions.Add(ReadFunctionProto(reader, options, cancellationToken));
                    reader.PopLimit(funcLimit);
                    break;

                default:
                    reader.SkipField(tag.WireType);
                    break;
            }
        }

        if (primaryGraph is null)
        {
            throw new ModelParseException(
                "ONNX ModelProto does not contain a primary GraphProto (field 8).",
                fileOffset: reader.Position,
                context: options.FileName);
        }

        if (irVersion > 0)
        {
            metadata.TryAdd("ir_version", irVersion.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(domain))
        {
            metadata.TryAdd("domain", domain);
        }

        if (modelVersion is { } mv && mv != 0)
        {
            metadata.TryAdd("model_version", mv.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(docString))
        {
            metadata.TryAdd("doc_string", docString);
        }

        if (opsetImports.Count > 0)
        {
            string[] opsetParts = new string[opsetImports.Count];
            int idx = 0;
            foreach (KeyValuePair<string, int> kvp in opsetImports)
            {
                opsetParts[idx++] = $"{kvp.Key}:{kvp.Value.ToString(CultureInfo.InvariantCulture)}";
            }

            metadata.TryAdd("opset_import", string.Join(", ", opsetParts));
        }

        List<Subgraph> subgraphs = new(1 + functions.Count)
        {
            BuildSubgraph(primaryGraph, opsetImports, defaultName: "main")
        };

        for (int i = 0; i < functions.Count; i++)
        {
            subgraphs.Add(BuildFunctionSubgraph(functions[i], opsetImports, i));
        }

        string? versionString = irVersion > 0
            ? $"IR v{irVersion.ToString(CultureInfo.InvariantCulture)}"
            : null;
        ModelFormatInfo formatInfo = new(ModelFormatId.Onnx, "ONNX", versionString);
        string? producer = FormatProducer(producerName, producerVersion);

        return new ModelGraph(
            Format: formatInfo,
            Producer: producer,
            Subgraphs: subgraphs,
            Metadata: metadata,
            Diagnostics: diagnostics);
    }

    private static ParsedGraph ReadGraphProto(
        ProtoReader reader,
        int depth,
        ModelReadOptions options,
        CancellationToken cancellationToken)
    {
        if (depth > options.MaxSubgraphDepth)
        {
            throw new ModelParseException(
                $"ONNX subgraph nesting depth ({depth}) exceeded maximum allowed depth ({options.MaxSubgraphDepth}).",
                fileOffset: reader.Position,
                context: "GraphProto");
        }

        string? name = null;
        List<RawNode> nodes = [];
        List<TensorEdge> initializers = [];
        List<RawValueInfo> inputs = [];
        List<RawValueInfo> outputs = [];
        List<RawValueInfo> valueInfos = [];

        while (!reader.IsAtEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            switch (tag.FieldNumber)
            {
                case 1: // repeated NodeProto node
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "GraphProto.node");
                    long nodeLimit = reader.PushMessageLimit();
                    nodes.Add(ReadNodeProto(reader, depth, options, cancellationToken));
                    reader.PopLimit(nodeLimit);
                    break;

                case 2: // string name
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "GraphProto.name");
                    name = reader.ReadString();
                    break;

                case 5: // repeated TensorProto initializer
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "GraphProto.initializer");
                    long initLimit = reader.PushMessageLimit();
                    initializers.Add(ReadTensorProto(reader, TensorKind.Initializer));
                    reader.PopLimit(initLimit);
                    break;

                case 11: // repeated ValueInfoProto input
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "GraphProto.input");
                    long inputLimit = reader.PushMessageLimit();
                    inputs.Add(ReadValueInfoProto(reader));
                    reader.PopLimit(inputLimit);
                    break;

                case 12: // repeated ValueInfoProto output
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "GraphProto.output");
                    long outputLimit = reader.PushMessageLimit();
                    outputs.Add(ReadValueInfoProto(reader));
                    reader.PopLimit(outputLimit);
                    break;

                case 13: // repeated ValueInfoProto value_info
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "GraphProto.value_info");
                    long viLimit = reader.PushMessageLimit();
                    valueInfos.Add(ReadValueInfoProto(reader));
                    reader.PopLimit(viLimit);
                    break;

                case 15: // repeated SparseTensorProto sparse_initializer
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "GraphProto.sparse_initializer");
                    long sparseLimit = reader.PushMessageLimit();
                    initializers.Add(ReadSparseTensorProto(reader, TensorKind.Initializer));
                    reader.PopLimit(sparseLimit);
                    break;

                default:
                    reader.SkipField(tag.WireType);
                    break;
            }
        }

        return new ParsedGraph(name, nodes, initializers, inputs, outputs, valueInfos);
    }

    private static RawNode ReadNodeProto(
        ProtoReader reader,
        int depth,
        ModelReadOptions options,
        CancellationToken cancellationToken)
    {
        string? name = null;
        string? opType = null;
        string? domain = null;
        string? docString = null;
        List<string> inputs = [];
        List<string> outputs = [];
        List<RawAttribute> attributes = [];

        while (!reader.IsAtEnd)
        {
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            switch (tag.FieldNumber)
            {
                case 1: // repeated string input
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "NodeProto.input");
                    inputs.Add(reader.ReadString());
                    break;

                case 2: // repeated string output
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "NodeProto.output");
                    outputs.Add(reader.ReadString());
                    break;

                case 3: // string name
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "NodeProto.name");
                    name = reader.ReadString();
                    break;

                case 4: // string op_type
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "NodeProto.op_type");
                    opType = reader.ReadString();
                    break;

                case 5: // repeated AttributeProto attribute
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "NodeProto.attribute");
                    long attrLimit = reader.PushMessageLimit();
                    RawAttribute? attr = ReadAttributeProto(reader, depth, options, cancellationToken);
                    reader.PopLimit(attrLimit);
                    if (attr is not null)
                    {
                        attributes.Add(attr);
                    }

                    break;

                case 6: // string doc_string
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "NodeProto.doc_string");
                    docString = reader.ReadString();
                    break;

                case 7: // string domain
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "NodeProto.domain");
                    domain = reader.ReadString();
                    break;

                default:
                    reader.SkipField(tag.WireType);
                    break;
            }
        }

        return new RawNode(
            Name: name ?? string.Empty,
            OpType: string.IsNullOrEmpty(opType) ? "Unknown" : opType,
            Domain: domain ?? string.Empty,
            DocString: docString,
            Inputs: inputs,
            Outputs: outputs,
            Attributes: attributes);
    }

    private static RawAttribute? ReadAttributeProto(
        ProtoReader reader,
        int depth,
        ModelReadOptions options,
        CancellationToken cancellationToken)
    {
        string name = string.Empty;
        int attrType = 0;
        double? floatVal = null;
        long? intVal = null;
        string? stringVal = null;
        TensorEdge? tensorVal = null;
        ParsedGraph? graphVal = null;
        TensorDescriptor? typeProtoVal = null;
        List<double>? floatsList = null;
        List<long>? intsList = null;
        List<string>? stringsList = null;
        List<TensorEdge>? tensorsList = null;
        List<ParsedGraph>? graphsList = null;

        while (!reader.IsAtEnd)
        {
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            switch (tag.FieldNumber)
            {
                case 1: // string name
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "AttributeProto.name");
                    name = reader.ReadString();
                    break;

                case 2: // float f
                    reader.ExpectWireType(tag.WireType, ProtoWireType.Fixed32, "AttributeProto.f");
                    floatVal = reader.ReadFloat();
                    break;

                case 3: // int64 i
                    reader.ExpectWireType(tag.WireType, ProtoWireType.Varint, "AttributeProto.i");
                    intVal = reader.ReadInt64();
                    break;

                case 4: // bytes s
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "AttributeProto.s");
                    stringVal = reader.ReadString();
                    break;

                case 5: // TensorProto t
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "AttributeProto.t");
                    long tLimit = reader.PushMessageLimit();
                    tensorVal = ReadTensorProto(reader, TensorKind.Constant);
                    reader.PopLimit(tLimit);
                    break;

                case 6: // GraphProto g
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "AttributeProto.g");
                    long gLimit = reader.PushMessageLimit();
                    graphVal = ReadGraphProto(reader, depth + 1, options, cancellationToken);
                    reader.PopLimit(gLimit);
                    break;

                case 7: // repeated float floats
                    floatsList ??= [];
                    reader.ReadRepeatedFloatAsDouble(tag.WireType, floatsList, "AttributeProto.floats");
                    break;

                case 8: // repeated int64 ints
                    intsList ??= [];
                    reader.ReadRepeatedInt64(tag.WireType, intsList, "AttributeProto.ints");
                    break;

                case 9: // repeated bytes strings
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "AttributeProto.strings");
                    stringsList ??= [];
                    stringsList.Add(reader.ReadString());
                    break;

                case 10: // repeated TensorProto tensors
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "AttributeProto.tensors");
                    long tsLimit = reader.PushMessageLimit();
                    tensorsList ??= [];
                    tensorsList.Add(ReadTensorProto(reader, TensorKind.Constant));
                    reader.PopLimit(tsLimit);
                    break;

                case 11: // repeated GraphProto graphs
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "AttributeProto.graphs");
                    long gsLimit = reader.PushMessageLimit();
                    graphsList ??= [];
                    graphsList.Add(ReadGraphProto(reader, depth + 1, options, cancellationToken));
                    reader.PopLimit(gsLimit);
                    break;

                case 14: // TypeProto tp
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "AttributeProto.tp");
                    long tpLimit = reader.PushMessageLimit();
                    typeProtoVal = ReadTypeProto(reader);
                    reader.PopLimit(tpLimit);
                    break;

                case 20: // AttributeType type
                    reader.ExpectWireType(tag.WireType, ProtoWireType.Varint, "AttributeProto.type");
                    attrType = reader.ReadInt32();
                    break;

                case 22: // SparseTensorProto sparse_tensor
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "AttributeProto.sparse_tensor");
                    long stLimit = reader.PushMessageLimit();
                    tensorVal = ReadSparseTensorProto(reader, TensorKind.Constant);
                    reader.PopLimit(stLimit);
                    break;

                default:
                    reader.SkipField(tag.WireType);
                    break;
            }
        }

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        // Resolve AttributeValue based on explicit AttributeType or populated field fallback.
        switch (attrType)
        {
            case 1: // FLOAT
                return new RawAttribute(name, new AttributeValue.Float64(floatVal ?? 0.0), null, null);

            case 2: // INT
                return new RawAttribute(name, new AttributeValue.Int64(intVal ?? 0L), null, null);

            case 3: // STRING
                return new RawAttribute(name, new AttributeValue.String(stringVal ?? string.Empty), null, null);

            case 4: // TENSOR
            case 11: // SPARSE_TENSOR
                if (tensorVal is not null)
                {
                    return new RawAttribute(name, new AttributeValue.Tensor(tensorVal), null, null);
                }

                break;

            case 5: // GRAPH
                if (graphVal is not null)
                {
                    return new RawAttribute(name, null, graphVal, null);
                }

                break;

            case 6: // FLOATS
                return new RawAttribute(name, new AttributeValue.Float64List(floatsList ?? []), null, null);

            case 7: // INTS
                return new RawAttribute(name, new AttributeValue.Int64List(intsList ?? []), null, null);

            case 8: // STRINGS
                return new RawAttribute(name, new AttributeValue.StringList(stringsList ?? []), null, null);

            case 9: // TENSORS
                if (tensorsList is { Count: > 0 })
                {
                    return new RawAttribute(name, new AttributeValue.Tensor(tensorsList[0]), null, null);
                }

                break;

            case 10: // GRAPHS
                return new RawAttribute(name, null, null, graphsList ?? []);

            case 13: // TYPE_PROTO
                if (typeProtoVal is not null)
                {
                    AttributeValue val = typeProtoVal.Shape is not null
                        ? new AttributeValue.ShapeValue(typeProtoVal.Shape)
                        : new AttributeValue.DataTypeValue(typeProtoVal.DataType);
                    return new RawAttribute(name, val, null, null);
                }

                break;
        }

        // Fallback if AttributeProto.type was omitted (0 = UNDEFINED)
        if (graphVal is not null)
        {
            return new RawAttribute(name, null, graphVal, null);
        }

        if (graphsList is not null)
        {
            return new RawAttribute(name, null, null, graphsList);
        }

        if (tensorVal is not null)
        {
            return new RawAttribute(name, new AttributeValue.Tensor(tensorVal), null, null);
        }

        if (intsList is not null)
        {
            return new RawAttribute(name, new AttributeValue.Int64List(intsList), null, null);
        }

        if (floatsList is not null)
        {
            return new RawAttribute(name, new AttributeValue.Float64List(floatsList), null, null);
        }

        if (stringsList is not null)
        {
            return new RawAttribute(name, new AttributeValue.StringList(stringsList), null, null);
        }

        if (intVal is not null)
        {
            return new RawAttribute(name, new AttributeValue.Int64(intVal.Value), null, null);
        }

        if (floatVal is not null)
        {
            return new RawAttribute(name, new AttributeValue.Float64(floatVal.Value), null, null);
        }

        if (stringVal is not null)
        {
            return new RawAttribute(name, new AttributeValue.String(stringVal), null, null);
        }

        return null;
    }

    private static TensorEdge ReadTensorProto(ProtoReader reader, TensorKind kind)
    {
        List<long> dims = [];
        int rawDataType = 0;
        string name = string.Empty;
        int dataLocation = 0;
        (long Offset, long Length)? rawDataRange = null;
        (long Offset, long Length, TensorDataEncoding Encoding, DataType FallbackType)? typedSliceRange = null;
        Dictionary<string, string>? externalData = null;

        while (!reader.IsAtEnd)
        {
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            switch (tag.FieldNumber)
            {
                case 1: // repeated int64 dims
                    reader.ReadRepeatedInt64(tag.WireType, dims, "TensorProto.dims");
                    break;

                case 2: // int32 data_type
                    reader.ExpectWireType(tag.WireType, ProtoWireType.Varint, "TensorProto.data_type");
                    rawDataType = reader.ReadInt32();
                    break;

                case 4: // repeated float float_data
                    CaptureTypedTensorData(
                        reader,
                        tag.WireType,
                        ProtoWireType.Fixed32,
                        fixedElementBytes: 4,
                        TensorDataEncoding.Raw,
                        DataType.Float32,
                        ref typedSliceRange,
                        "TensorProto.float_data");
                    break;

                case 5: // repeated int32 int32_data
                    CaptureVarintTensorData(
                        reader,
                        tag.WireType,
                        DataType.Int32,
                        ref typedSliceRange,
                        "TensorProto.int32_data");
                    break;

                case 6: // repeated bytes string_data
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "TensorProto.string_data");
                    (long strOffset, long strLen) = reader.CaptureLengthDelimitedRange();
                    typedSliceRange ??= (strOffset, strLen, TensorDataEncoding.Raw, DataType.String);
                    break;

                case 7: // repeated int64 int64_data
                    CaptureVarintTensorData(
                        reader,
                        tag.WireType,
                        DataType.Int64,
                        ref typedSliceRange,
                        "TensorProto.int64_data");
                    break;

                case 8: // string name
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "TensorProto.name");
                    name = reader.ReadString();
                    break;

                case 9: // bytes raw_data
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "TensorProto.raw_data");
                    rawDataRange = reader.CaptureLengthDelimitedRange();
                    break;

                case 10: // repeated double double_data
                    CaptureTypedTensorData(
                        reader,
                        tag.WireType,
                        ProtoWireType.Fixed64,
                        fixedElementBytes: 8,
                        TensorDataEncoding.Raw,
                        DataType.Float64,
                        ref typedSliceRange,
                        "TensorProto.double_data");
                    break;

                case 11: // repeated uint64 uint64_data
                    CaptureVarintTensorData(
                        reader,
                        tag.WireType,
                        DataType.UInt64,
                        ref typedSliceRange,
                        "TensorProto.uint64_data");
                    break;

                case 13: // repeated StringStringEntryProto external_data
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "TensorProto.external_data");
                    (string extKey, string extVal) = ReadStringStringEntryProto(reader);
                    externalData ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    externalData[extKey] = extVal;
                    break;

                case 14: // DataLocation data_location
                    reader.ExpectWireType(tag.WireType, ProtoWireType.Varint, "TensorProto.data_location");
                    dataLocation = reader.ReadInt32();
                    break;

                default:
                    reader.SkipField(tag.WireType);
                    break;
            }
        }

        DataType dataType = OnnxOperatorCatalog.MapDataType(rawDataType);
        if (dataType == DataType.Unknown && typedSliceRange is { FallbackType: not DataType.Unknown } fallback)
        {
            dataType = fallback.FallbackType;
        }

        TensorShape shape = BuildFixedShape(dims);
        TensorDataSource? dataSource = null;

        if (dataLocation == 1 || (externalData is not null && externalData.ContainsKey("location")))
        {
            if (externalData is null
                || !externalData.TryGetValue("location", out string? location)
                || string.IsNullOrWhiteSpace(location))
            {
                throw new ModelParseException(
                    $"ONNX external tensor '{name}' specifies EXTERNAL data_location without a non-empty 'location' entry.",
                    fileOffset: reader.Position,
                    context: name);
            }

            long extOffset = 0;
            if (externalData.TryGetValue("offset", out string? offsetStr)
                && !string.IsNullOrWhiteSpace(offsetStr))
            {
                if (!long.TryParse(offsetStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out extOffset) || extOffset < 0)
                {
                    throw new ModelParseException(
                        $"ONNX external tensor '{name}' has invalid byte offset '{offsetStr}'.",
                        fileOffset: reader.Position,
                        context: name);
                }
            }

            long? extLength = null;
            if (externalData.TryGetValue("length", out string? lengthStr)
                && !string.IsNullOrWhiteSpace(lengthStr))
            {
                if (!long.TryParse(lengthStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedLength) || parsedLength < 0)
                {
                    throw new ModelParseException(
                        $"ONNX external tensor '{name}' has invalid byte length '{lengthStr}'.",
                        fileOffset: reader.Position,
                        context: name);
                }

                extLength = parsedLength;
            }

            dataSource = new ExternalTensorDataReference(location, extOffset, extLength, dataType);
        }
        else if (rawDataRange is { } raw)
        {
            dataSource = new TensorDataSlice(raw.Offset, raw.Length, dataType, TensorDataEncoding.Raw);
        }
        else if (typedSliceRange is { } typed)
        {
            dataSource = new TensorDataSlice(typed.Offset, typed.Length, dataType, typed.Encoding);
        }

        return new TensorEdge(
            Name: name,
            Descriptor: new TensorDescriptor(dataType, shape),
            Kind: kind,
            Data: dataSource);
    }

    private static TensorEdge ReadSparseTensorProto(ProtoReader reader, TensorKind kind)
    {
        TensorEdge? values = null;
        TensorEdge? indices = null;
        List<long> dims = [];

        while (!reader.IsAtEnd)
        {
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            switch (tag.FieldNumber)
            {
                case 1: // TensorProto values
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "SparseTensorProto.values");
                    long valLimit = reader.PushMessageLimit();
                    values = ReadTensorProto(reader, kind);
                    reader.PopLimit(valLimit);
                    break;

                case 2: // TensorProto indices
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "SparseTensorProto.indices");
                    long idxLimit = reader.PushMessageLimit();
                    indices = ReadTensorProto(reader, kind);
                    reader.PopLimit(idxLimit);
                    break;

                case 3: // repeated int64 dims
                    reader.ReadRepeatedInt64(tag.WireType, dims, "SparseTensorProto.dims");
                    break;

                default:
                    reader.SkipField(tag.WireType);
                    break;
            }
        }

        string name = values?.Name ?? indices?.Name ?? string.Empty;
        DataType dataType = values?.Descriptor.DataType ?? DataType.Unknown;
        TensorShape shape = dims.Count > 0 ? BuildFixedShape(dims) : (values?.Descriptor.Shape ?? TensorShape.Scalar);

        return new TensorEdge(
            Name: name,
            Descriptor: new TensorDescriptor(dataType, shape),
            Kind: kind,
            Data: values?.Data);
    }

    private static RawValueInfo ReadValueInfoProto(ProtoReader reader)
    {
        string name = string.Empty;
        TensorDescriptor? descriptor = null;

        while (!reader.IsAtEnd)
        {
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            switch (tag.FieldNumber)
            {
                case 1: // string name
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "ValueInfoProto.name");
                    name = reader.ReadString();
                    break;

                case 2: // TypeProto type
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "ValueInfoProto.type");
                    long typeLimit = reader.PushMessageLimit();
                    descriptor = ReadTypeProto(reader);
                    reader.PopLimit(typeLimit);
                    break;

                default:
                    reader.SkipField(tag.WireType);
                    break;
            }
        }

        return new RawValueInfo(name, descriptor ?? new TensorDescriptor(DataType.Unknown, null));
    }

    private static TensorDescriptor ReadTypeProto(ProtoReader reader)
    {
        TensorDescriptor? descriptor = null;

        while (!reader.IsAtEnd)
        {
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            switch (tag.FieldNumber)
            {
                case 1: // TypeProto.Tensor tensor_type
                case 8: // TypeProto.SparseTensor sparse_tensor_type
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "TypeProto.tensor_type");
                    long tensorLimit = reader.PushMessageLimit();
                    descriptor = ReadTensorTypeProto(reader);
                    reader.PopLimit(tensorLimit);
                    break;

                case 4: // TypeProto.Sequence sequence_type
                case 9: // TypeProto.Optional optional_type
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "TypeProto.wrapper");
                    long wrapperLimit = reader.PushMessageLimit();
                    descriptor = ReadWrapperTypeProto(reader);
                    reader.PopLimit(wrapperLimit);
                    break;

                default:
                    reader.SkipField(tag.WireType);
                    break;
            }
        }

        return descriptor ?? new TensorDescriptor(DataType.Unknown, null);
    }

    private static TensorDescriptor ReadWrapperTypeProto(ProtoReader reader)
    {
        TensorDescriptor? inner = null;
        while (!reader.IsAtEnd)
        {
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            if (tag.FieldNumber == 1 && tag.WireType == ProtoWireType.LengthDelimited)
            {
                long innerLimit = reader.PushMessageLimit();
                inner = ReadTypeProto(reader);
                reader.PopLimit(innerLimit);
            }
            else
            {
                reader.SkipField(tag.WireType);
            }
        }

        return inner ?? new TensorDescriptor(DataType.Unknown, null);
    }

    private static TensorDescriptor ReadTensorTypeProto(ProtoReader reader)
    {
        int elemType = 0;
        TensorShape? shape = null;

        while (!reader.IsAtEnd)
        {
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            switch (tag.FieldNumber)
            {
                case 1: // int32 elem_type
                    reader.ExpectWireType(tag.WireType, ProtoWireType.Varint, "TypeProto.Tensor.elem_type");
                    elemType = reader.ReadInt32();
                    break;

                case 2: // TensorShapeProto shape
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "TypeProto.Tensor.shape");
                    long shapeLimit = reader.PushMessageLimit();
                    shape = ReadTensorShapeProto(reader);
                    reader.PopLimit(shapeLimit);
                    break;

                default:
                    reader.SkipField(tag.WireType);
                    break;
            }
        }

        return new TensorDescriptor(OnnxOperatorCatalog.MapDataType(elemType), shape);
    }

    private static TensorShape ReadTensorShapeProto(ProtoReader reader)
    {
        List<TensorDimension> dimensions = [];

        while (!reader.IsAtEnd)
        {
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            if (tag.FieldNumber == 1) // repeated Dimension dim
            {
                reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "TensorShapeProto.dim");
                long dimLimit = reader.PushMessageLimit();
                dimensions.Add(ReadDimensionProto(reader));
                reader.PopLimit(dimLimit);
            }
            else
            {
                reader.SkipField(tag.WireType);
            }
        }

        return dimensions.Count == 0 ? TensorShape.Scalar : new TensorShape(dimensions);
    }

    private static TensorDimension ReadDimensionProto(ProtoReader reader)
    {
        bool hasDimValue = false;
        long dimValue = 0;
        string? dimParam = null;

        while (!reader.IsAtEnd)
        {
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            switch (tag.FieldNumber)
            {
                case 1: // int64 dim_value
                    reader.ExpectWireType(tag.WireType, ProtoWireType.Varint, "TensorShapeProto.Dimension.dim_value");
                    dimValue = reader.ReadInt64();
                    hasDimValue = true;
                    break;

                case 2: // string dim_param
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "TensorShapeProto.Dimension.dim_param");
                    dimParam = reader.ReadString();
                    break;

                default:
                    reader.SkipField(tag.WireType);
                    break;
            }
        }

        if (!string.IsNullOrEmpty(dimParam))
        {
            return new TensorDimension.Symbolic(dimParam);
        }

        if (hasDimValue && dimValue >= 0)
        {
            return new TensorDimension.Fixed(dimValue);
        }

        return TensorDimension.Dynamic.Instance;
    }

    private static ParsedFunction ReadFunctionProto(
        ProtoReader reader,
        ModelReadOptions options,
        CancellationToken cancellationToken)
    {
        string name = string.Empty;
        string domain = string.Empty;
        List<string> inputs = [];
        List<string> outputs = [];
        List<RawNode> nodes = [];
        List<RawValueInfo> valueInfos = [];
        Dictionary<string, int> opsetImports = new(StringComparer.Ordinal);

        while (!reader.IsAtEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            switch (tag.FieldNumber)
            {
                case 1: // string name
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "FunctionProto.name");
                    name = reader.ReadString();
                    break;

                case 4: // repeated string input
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "FunctionProto.input");
                    inputs.Add(reader.ReadString());
                    break;

                case 5: // repeated string output
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "FunctionProto.output");
                    outputs.Add(reader.ReadString());
                    break;

                case 7: // repeated NodeProto node
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "FunctionProto.node");
                    long nodeLimit = reader.PushMessageLimit();
                    nodes.Add(ReadNodeProto(reader, depth: 1, options, cancellationToken));
                    reader.PopLimit(nodeLimit);
                    break;

                case 9: // repeated OperatorSetIdProto opset_import
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "FunctionProto.opset_import");
                    (string opDomain, int opVer) = ReadOperatorSetIdProto(reader);
                    opsetImports[NormalizeDomain(opDomain)] = opVer;
                    break;

                case 10: // string domain
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "FunctionProto.domain");
                    domain = reader.ReadString();
                    break;

                case 12: // repeated ValueInfoProto value_info
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "FunctionProto.value_info");
                    long viLimit = reader.PushMessageLimit();
                    valueInfos.Add(ReadValueInfoProto(reader));
                    reader.PopLimit(viLimit);
                    break;

                default:
                    reader.SkipField(tag.WireType);
                    break;
            }
        }

        return new ParsedFunction(name, domain, inputs, outputs, nodes, valueInfos, opsetImports);
    }

    private static (string Domain, int Version) ReadOperatorSetIdProto(ProtoReader reader)
    {
        long limit = reader.PushMessageLimit();
        string domain = string.Empty;
        long version = 0;

        while (!reader.IsAtEnd)
        {
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            switch (tag.FieldNumber)
            {
                case 1: // string domain
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "OperatorSetIdProto.domain");
                    domain = reader.ReadString();
                    break;

                case 2: // int64 version
                    reader.ExpectWireType(tag.WireType, ProtoWireType.Varint, "OperatorSetIdProto.version");
                    version = reader.ReadInt64();
                    break;

                default:
                    reader.SkipField(tag.WireType);
                    break;
            }
        }

        reader.PopLimit(limit);
        return (domain, (int)version);
    }

    private static (string Key, string Value) ReadStringStringEntryProto(ProtoReader reader)
    {
        long limit = reader.PushMessageLimit();
        string key = string.Empty;
        string value = string.Empty;

        while (!reader.IsAtEnd)
        {
            ProtoTag tag = reader.ReadTag();
            if (tag.IsEnd)
            {
                break;
            }

            switch (tag.FieldNumber)
            {
                case 1: // string key
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "StringStringEntryProto.key");
                    key = reader.ReadString();
                    break;

                case 2: // string value
                    reader.ExpectWireType(tag.WireType, ProtoWireType.LengthDelimited, "StringStringEntryProto.value");
                    value = reader.ReadString();
                    break;

                default:
                    reader.SkipField(tag.WireType);
                    break;
            }
        }

        reader.PopLimit(limit);
        return (key, value);
    }

    private static Subgraph BuildSubgraph(
        ParsedGraph parsed,
        IReadOnlyDictionary<string, int> opsetImports,
        string defaultName)
    {
        Dictionary<string, TensorEdge> tensorByName = new(StringComparer.Ordinal);
        List<TensorEdge> allTensors = [];
        HashSet<string> initializerNames = new(StringComparer.Ordinal);

        // 1. Register initializers first so ONNX IR v3 duplicate entries in GraphProto.input are filtered out of GraphInput ports.
        for (int i = 0; i < parsed.Initializers.Count; i++)
        {
            TensorEdge initEdge = parsed.Initializers[i];
            if (!string.IsNullOrEmpty(initEdge.Name))
            {
                initializerNames.Add(initEdge.Name);
                tensorByName[initEdge.Name] = initEdge;
            }

            allTensors.Add(initEdge);
        }

        // 2. Register graph inputs, excluding any tensor already declared as an initializer.
        List<GraphPort> graphInputs = [];
        for (int i = 0; i < parsed.Inputs.Count; i++)
        {
            RawValueInfo inputVi = parsed.Inputs[i];
            if (string.IsNullOrEmpty(inputVi.Name) || initializerNames.Contains(inputVi.Name))
            {
                continue;
            }

            TensorEdge inputEdge = new(inputVi.Name, inputVi.Descriptor, TensorKind.GraphInput);
            if (tensorByName.TryAdd(inputVi.Name, inputEdge))
            {
                allTensors.Add(inputEdge);
            }

            graphInputs.Add(new GraphPort(inputVi.Name, [tensorByName[inputVi.Name]]));
        }

        // 3. Register intermediate value_info descriptors so node outputs/inputs pick up inferred shapes & types.
        Dictionary<string, TensorDescriptor> valueInfoByName = new(parsed.ValueInfos.Count, StringComparer.Ordinal);
        for (int i = 0; i < parsed.ValueInfos.Count; i++)
        {
            RawValueInfo vi = parsed.ValueInfos[i];
            if (!string.IsNullOrEmpty(vi.Name))
            {
                valueInfoByName[vi.Name] = vi.Descriptor;
            }
        }

        // 4. Pre-register graph output descriptors so nodes producing graph outputs attach to the GraphOutput TensorEdge.
        Dictionary<string, TensorEdge> outputEdgeByName = new(parsed.Outputs.Count, StringComparer.Ordinal);
        for (int i = 0; i < parsed.Outputs.Count; i++)
        {
            RawValueInfo outputVi = parsed.Outputs[i];
            if (string.IsNullOrEmpty(outputVi.Name))
            {
                continue;
            }

            if (!tensorByName.TryGetValue(outputVi.Name, out TensorEdge? existingEdge))
            {
                existingEdge = new TensorEdge(outputVi.Name, outputVi.Descriptor, TensorKind.GraphOutput);
                tensorByName[outputVi.Name] = existingEdge;
                allTensors.Add(existingEdge);
            }

            outputEdgeByName[outputVi.Name] = existingEdge;
        }

        // 5. Build GraphNode instances and resolve input/output ports against the canonical TensorEdge table.
        List<GraphNode> nodes = new(parsed.Nodes.Count);
        for (int i = 0; i < parsed.Nodes.Count; i++)
        {
            RawNode rawNode = parsed.Nodes[i];
            string nodeName = string.IsNullOrEmpty(rawNode.Name)
                ? $"{rawNode.OpType}_{i.ToString(CultureInfo.InvariantCulture)}"
                : rawNode.Name;

            string normalizedDomain = NormalizeDomain(rawNode.Domain);
            int? opsetVersion = opsetImports.TryGetValue(normalizedDomain, out int ver) ? ver : null;
            OperatorCategory category = OnnxOperatorCatalog.Categorize(rawNode.OpType, normalizedDomain);
            OperatorInfo opInfo = new(rawNode.OpType, category, normalizedDomain, opsetVersion);

            List<NodePort> nodeInputs = new(rawNode.Inputs.Count);
            for (int p = 0; p < rawNode.Inputs.Count; p++)
            {
                string tensorName = rawNode.Inputs[p];
                string portName = OnnxOperatorCatalog.GetInputPortName(rawNode.OpType, p);
                if (string.IsNullOrEmpty(tensorName))
                {
                    nodeInputs.Add(new NodePort(portName, []));
                    continue;
                }

                TensorEdge edge = ResolveOrAddActivationTensor(tensorName, tensorByName, valueInfoByName, allTensors);
                nodeInputs.Add(new NodePort(portName, [edge]));
            }

            List<NodePort> nodeOutputs = new(rawNode.Outputs.Count);
            for (int p = 0; p < rawNode.Outputs.Count; p++)
            {
                string tensorName = rawNode.Outputs[p];
                string portName = OnnxOperatorCatalog.GetOutputPortName(rawNode.OpType, p);
                if (string.IsNullOrEmpty(tensorName))
                {
                    nodeOutputs.Add(new NodePort(portName, []));
                    continue;
                }

                TensorEdge edge = ResolveOrAddActivationTensor(tensorName, tensorByName, valueInfoByName, allTensors);
                nodeOutputs.Add(new NodePort(portName, [edge]));
            }

            List<NodeAttribute> attributes = new(rawNode.Attributes.Count);
            for (int a = 0; a < rawNode.Attributes.Count; a++)
            {
                RawAttribute rawAttr = rawNode.Attributes[a];
                if (rawAttr.DirectValue is not null)
                {
                    attributes.Add(new NodeAttribute(rawAttr.Name, rawAttr.DirectValue));
                }
                else if (rawAttr.SubgraphValue is not null)
                {
                    Subgraph nested = BuildSubgraph(rawAttr.SubgraphValue, opsetImports, defaultName: rawAttr.Name);
                    attributes.Add(new NodeAttribute(rawAttr.Name, new AttributeValue.Graph(nested)));
                }
                else if (rawAttr.SubgraphListValue is not null)
                {
                    List<Subgraph> nestedList = new(rawAttr.SubgraphListValue.Count);
                    for (int g = 0; g < rawAttr.SubgraphListValue.Count; g++)
                    {
                        nestedList.Add(BuildSubgraph(
                            rawAttr.SubgraphListValue[g],
                            opsetImports,
                            defaultName: $"{rawAttr.Name}_{g.ToString(CultureInfo.InvariantCulture)}"));
                    }

                    attributes.Add(new NodeAttribute(rawAttr.Name, new AttributeValue.GraphList(nestedList)));
                }
            }

            nodes.Add(new GraphNode(
                Name: nodeName,
                Operator: opInfo,
                Inputs: nodeInputs,
                Outputs: nodeOutputs,
                Attributes: attributes,
                Documentation: string.IsNullOrEmpty(rawNode.DocString) ? null : rawNode.DocString));
        }

        // 6. Build GraphPort list for subgraph outputs in declaration order.
        List<GraphPort> graphOutputs = new(parsed.Outputs.Count);
        for (int i = 0; i < parsed.Outputs.Count; i++)
        {
            RawValueInfo outputVi = parsed.Outputs[i];
            if (!string.IsNullOrEmpty(outputVi.Name) && outputEdgeByName.TryGetValue(outputVi.Name, out TensorEdge? outEdge))
            {
                graphOutputs.Add(new GraphPort(outputVi.Name, [outEdge]));
            }
        }

        string subgraphName = string.IsNullOrWhiteSpace(parsed.Name) ? defaultName : parsed.Name;
        return new Subgraph(
            Name: subgraphName,
            Nodes: nodes,
            Tensors: allTensors,
            Inputs: graphInputs,
            Outputs: graphOutputs);
    }

    private static Subgraph BuildFunctionSubgraph(
        ParsedFunction func,
        IReadOnlyDictionary<string, int> modelOpsetImports,
        int index)
    {
        Dictionary<string, int> mergedOpsets = new(modelOpsetImports, StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> kvp in func.OpsetImports)
        {
            mergedOpsets[kvp.Key] = kvp.Value;
        }

        Dictionary<string, TensorDescriptor> viLookup = new(func.ValueInfos.Count, StringComparer.Ordinal);
        for (int i = 0; i < func.ValueInfos.Count; i++)
        {
            if (!string.IsNullOrEmpty(func.ValueInfos[i].Name))
            {
                viLookup[func.ValueInfos[i].Name] = func.ValueInfos[i].Descriptor;
            }
        }

        List<RawValueInfo> inputs = new(func.Inputs.Count);
        for (int i = 0; i < func.Inputs.Count; i++)
        {
            string inName = func.Inputs[i];
            TensorDescriptor desc = viLookup.GetValueOrDefault(inName) ?? new TensorDescriptor(DataType.Unknown, null);
            inputs.Add(new RawValueInfo(inName, desc));
        }

        List<RawValueInfo> outputs = new(func.Outputs.Count);
        for (int i = 0; i < func.Outputs.Count; i++)
        {
            string outName = func.Outputs[i];
            TensorDescriptor desc = viLookup.GetValueOrDefault(outName) ?? new TensorDescriptor(DataType.Unknown, null);
            outputs.Add(new RawValueInfo(outName, desc));
        }

        string defaultName = $"function_{index.ToString(CultureInfo.InvariantCulture)}";
        string fullName = string.IsNullOrEmpty(func.Domain) || string.Equals(func.Domain, "ai.onnx", StringComparison.Ordinal)
            ? (string.IsNullOrEmpty(func.Name) ? defaultName : func.Name)
            : $"{func.Domain}.{func.Name}";

        ParsedGraph asGraph = new(fullName, func.Nodes, [], inputs, outputs, func.ValueInfos);
        return BuildSubgraph(asGraph, mergedOpsets, defaultName);
    }

    private static TensorEdge ResolveOrAddActivationTensor(
        string tensorName,
        Dictionary<string, TensorEdge> tensorByName,
        Dictionary<string, TensorDescriptor> valueInfoByName,
        List<TensorEdge> allTensors)
    {
        if (tensorByName.TryGetValue(tensorName, out TensorEdge? existing))
        {
            return existing;
        }

        TensorDescriptor descriptor = valueInfoByName.GetValueOrDefault(tensorName)
            ?? new TensorDescriptor(DataType.Unknown, null);
        TensorEdge created = new(tensorName, descriptor, TensorKind.Activation);
        tensorByName[tensorName] = created;
        allTensors.Add(created);
        return created;
    }

    private static TensorShape BuildFixedShape(List<long> dims)
    {
        if (dims.Count == 0)
        {
            return TensorShape.Scalar;
        }

        TensorDimension[] result = new TensorDimension[dims.Count];
        for (int i = 0; i < dims.Count; i++)
        {
            result[i] = dims[i] >= 0
                ? new TensorDimension.Fixed(dims[i])
                : TensorDimension.Dynamic.Instance;
        }

        return new TensorShape(result);
    }

    private static void CaptureTypedTensorData(
        ProtoReader reader,
        ProtoWireType actualWireType,
        ProtoWireType unpackedWireType,
        int fixedElementBytes,
        TensorDataEncoding encoding,
        DataType fallbackType,
        ref (long Offset, long Length, TensorDataEncoding Encoding, DataType FallbackType)? sliceRange,
        string context)
    {
        if (actualWireType == ProtoWireType.LengthDelimited)
        {
            (long offset, long len) = reader.CaptureLengthDelimitedRange();
            if ((len % fixedElementBytes) != 0)
            {
                throw new ModelParseException(
                    $"Packed tensor field '{context}' byte length ({len}) is not a multiple of element size ({fixedElementBytes}).",
                    fileOffset: offset,
                    context: context);
            }

            sliceRange ??= (offset, len, encoding, fallbackType);
            return;
        }

        if (actualWireType == unpackedWireType)
        {
            long offset = reader.Position;
            reader.SkipBytes(fixedElementBytes);
            sliceRange ??= (offset, fixedElementBytes, encoding, fallbackType);
            return;
        }

        throw new ModelParseException(
            $"Unexpected wire type {actualWireType} for tensor field '{context}'.",
            fileOffset: reader.Position,
            context: context);
    }

    private static void CaptureVarintTensorData(
        ProtoReader reader,
        ProtoWireType actualWireType,
        DataType fallbackType,
        ref (long Offset, long Length, TensorDataEncoding Encoding, DataType FallbackType)? sliceRange,
        string context)
    {
        if (actualWireType == ProtoWireType.LengthDelimited)
        {
            (long offset, long len) = reader.CaptureLengthDelimitedRange();
            sliceRange ??= (offset, len, TensorDataEncoding.VarintPacked, fallbackType);
            return;
        }

        if (actualWireType == ProtoWireType.Varint)
        {
            long start = reader.Position;
            _ = reader.ReadRawVarint64();
            long len = reader.Position - start;
            sliceRange ??= (start, len, TensorDataEncoding.VarintPacked, fallbackType);
            return;
        }

        throw new ModelParseException(
            $"Unexpected wire type {actualWireType} for varint tensor field '{context}'.",
            fileOffset: reader.Position,
            context: context);
    }

    private static string NormalizeDomain(string? domain) =>
        string.IsNullOrEmpty(domain) ? "ai.onnx" : domain;

    private static string? FormatProducer(string? producerName, string? producerVersion)
    {
        bool hasName = !string.IsNullOrWhiteSpace(producerName);
        bool hasVersion = !string.IsNullOrWhiteSpace(producerVersion);
        if (hasName && hasVersion)
        {
            return $"{producerName} {producerVersion}";
        }

        if (hasName)
        {
            return producerName;
        }

        if (hasVersion)
        {
            return producerVersion;
        }

        return null;
    }

    private sealed record ParsedGraph(
        string? Name,
        List<RawNode> Nodes,
        List<TensorEdge> Initializers,
        List<RawValueInfo> Inputs,
        List<RawValueInfo> Outputs,
        List<RawValueInfo> ValueInfos);

    private sealed record RawNode(
        string Name,
        string OpType,
        string Domain,
        string? DocString,
        List<string> Inputs,
        List<string> Outputs,
        List<RawAttribute> Attributes);

    private sealed record RawAttribute(
        string Name,
        AttributeValue? DirectValue,
        ParsedGraph? SubgraphValue,
        List<ParsedGraph>? SubgraphListValue);

    private sealed record RawValueInfo(
        string Name,
        TensorDescriptor Descriptor);

    private sealed record ParsedFunction(
        string Name,
        string Domain,
        List<string> Inputs,
        List<string> Outputs,
        List<RawNode> Nodes,
        List<RawValueInfo> ValueInfos,
        Dictionary<string, int> OpsetImports);
}
