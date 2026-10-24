using System.Text.Json;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.IO.Hdf5;
using DotNodeViewer.Core.IO.Zip;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Keras;

/// <summary>
/// Streaming reader for Keras 3 (<c>.keras</c>) archives and legacy HDF5 (<c>.h5</c>) models.
/// </summary>
internal sealed class KerasFormatReader : IModelFormatReader
{
    /// <summary>
    /// Gets a shared singleton instance of <see cref="KerasFormatReader"/>.
    /// </summary>
    public static KerasFormatReader Instance { get; } = new();

    /// <inheritdoc/>
    public ModelFormatId FormatId => ModelFormatId.Keras;

    private static readonly ModelFormatInfo FormatInfo = new(
        Id: ModelFormatId.Keras,
        DisplayName: "Keras (.keras/.h5)");

    /// <inheritdoc/>
    public ValueTask<ModelGraph> ReadAsync(
        Stream stream,
        ModelReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ModelParseException("Stream must be readable and seekable to parse Keras models.");
        }

        var source = new RandomAccessSource(stream);
        try
        {
            ZipDirectory? cachedZip = options?.CachedZipDirectory;
            if (cachedZip is not null)
            {
                return new ValueTask<ModelGraph>(ParseKerasZip(source, cachedZip, cancellationToken));
            }

            // Case A: ZIP archive (.keras)
            if (ZipDirectory.TryRead(source, out var zip))
            {
                return new ValueTask<ModelGraph>(ParseKerasZip(source, zip, cancellationToken));
            }

            // Case B: Standalone HDF5 (.h5 / .weights.h5)
            if (Hdf5File.TryOpen(source, 0, out var hdf5))
            {
                return new ValueTask<ModelGraph>(ParseStandaloneHdf5(hdf5, cancellationToken));
            }

            throw new ModelParseException("File is neither a valid Keras .keras ZIP archive nor an HDF5 container.", fileOffset: 0);
        }
        catch (ModelParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModelParseException($"Failed to parse Keras model: {ex.Message}", ex);
        }
    }

    private static ModelGraph ParseKerasZip(
        RandomAccessSource source,
        ZipDirectory zip,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Locate config.json
        if (!zip.TryFindEntry("config.json", out var configEntry))
        {
            throw new ModelParseException("Required entry 'config.json' not found in Keras archive.", fileOffset: 0);
        }

        using var configDoc = JsonDocument.Parse(zip.ReadEntryBytes(configEntry));
        JsonElement root = configDoc.RootElement;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        List<ParseDiagnostic> diagnostics = [];

        // 2. Read metadata.json if present
        if (zip.TryFindEntry("metadata.json", out var metaEntry))
        {
            try
            {
                using var metaDoc = JsonDocument.Parse(zip.ReadEntryBytes(metaEntry));
                if (metaDoc.RootElement.TryGetProperty("keras_version", out var kvElem) && kvElem.ValueKind == JsonValueKind.String)
                {
                    metadata["keras_version"] = kvElem.GetString()!;
                }

                if (metaDoc.RootElement.TryGetProperty("date_saved", out var dsElem) && dsElem.ValueKind == JsonValueKind.String)
                {
                    metadata["date_saved"] = dsElem.GetString()!;
                }
            }
            catch (JsonException)
            {
                // Non-fatal metadata error
            }
        }

        // 3. Open HDF5 weights from model.weights.h5
        Hdf5File? hdf5 = null;
        if (zip.TryFindEntry("model.weights.h5", out var weightsEntry))
        {
            if (weightsEntry.IsStored)
            {
                Hdf5File.TryOpen(source, weightsEntry.DataOffset, out hdf5);
            }
            else
            {
                // Deflated weights entry fallback
                byte[] uncompressedH5 = zip.ReadEntryBytes(weightsEntry);
                using MemoryStream ms = new(uncompressedH5);
                var memSource = new RandomAccessSource(ms);
                Hdf5File.TryOpen(memSource, 0, out hdf5);
                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Info,
                    "DNV_KERAS_DEFLATED_WEIGHTS",
                    "model.weights.h5 is deflated within the .keras archive. Raw stream offsets point to uncompressed payload."));
            }
        }

        return BuildGraphFromJsonConfig(root, hdf5, metadata, diagnostics, cancellationToken);
    }

    private static ModelGraph ParseStandaloneHdf5(
        Hdf5File hdf5,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        List<ParseDiagnostic> diagnostics = [];

        foreach (var kv in hdf5.Attributes)
        {
            if (string.Equals(kv.Key, "keras_version", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kv.Key, "backend", StringComparison.OrdinalIgnoreCase))
            {
                metadata[kv.Key] = kv.Value;
            }
        }

        // Case 1: Full model with model_config attribute
        if (hdf5.Attributes.TryGetValue("model_config", out string? modelConfigJson) &&
            !string.IsNullOrWhiteSpace(modelConfigJson))
        {
            using var configDoc = JsonDocument.Parse(modelConfigJson);
            return BuildGraphFromJsonConfig(configDoc.RootElement, hdf5, metadata, diagnostics, cancellationToken);
        }

        // Case 2: Weights-only HDF5 fallback
        diagnostics.Add(new ParseDiagnostic(
            DiagnosticSeverity.Info,
            "DNV_KERAS_WEIGHTS_ONLY",
            "HDF5 file contains weights without model_config. Showing weight layers and tensors."));

        return BuildWeightsOnlyGraph(hdf5, metadata, diagnostics);
    }

    private static ModelGraph BuildGraphFromJsonConfig(
        JsonElement root,
        Hdf5File? hdf5,
        Dictionary<string, string> metadata,
        List<ParseDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string className = root.TryGetProperty("class_name", out var cn) && cn.ValueKind == JsonValueKind.String
            ? cn.GetString()!
            : "Sequential";

        JsonElement configElem = root.TryGetProperty("config", out var cElem) && cElem.ValueKind == JsonValueKind.Object
            ? cElem
            : root;

        metadata["model_class"] = className;
        string? producer = metadata.TryGetValue("keras_version", out var kv)
            ? $"Keras {kv}"
            : "Keras";

        bool isSequential = string.Equals(className, "Sequential", StringComparison.OrdinalIgnoreCase);

        var tensorCatalog = new Dictionary<string, TensorEdge>(StringComparer.Ordinal);
        var nodes = new List<GraphNode>();
        var graphInputs = new List<GraphPort>();
        var graphOutputs = new List<GraphPort>();

        if (configElem.TryGetProperty("layers", out var layersElem) && layersElem.ValueKind == JsonValueKind.Array)
        {
            string? previousLayerOutputTensor = null;

            foreach (var lElem in layersElem.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                string layerClass = lElem.TryGetProperty("class_name", out var lc) && lc.ValueKind == JsonValueKind.String
                    ? lc.GetString()!
                    : "Layer";

                JsonElement lConfig = lElem.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object
                    ? cfg
                    : lElem;

                string layerName = lConfig.TryGetProperty("name", out var ln) && ln.ValueKind == JsonValueKind.String
                    ? ln.GetString()!
                    : lElem.TryGetProperty("name", out var en) && en.ValueKind == JsonValueKind.String
                        ? en.GetString()!
                        : $"layer_{nodes.Count}";

                // Special handling for InputLayer
                if (string.Equals(layerClass, "InputLayer", StringComparison.OrdinalIgnoreCase))
                {
                    TensorShape inputShape = ParseInputShape(lConfig);
                    string dtypeStr = lConfig.TryGetProperty("dtype", out var dt) && dt.ValueKind == JsonValueKind.String
                        ? dt.GetString() ?? "float32"
                        : "float32";
                    DataType dataType = KerasOperatorCatalog.MapDataType(dtypeStr);

                    string outTensorName = $"{layerName}:0";
                    var inputEdge = new TensorEdge(
                        outTensorName,
                        new TensorDescriptor(dataType, inputShape),
                        TensorKind.GraphInput);

                    tensorCatalog[outTensorName] = inputEdge;
                    graphInputs.Add(new GraphPort(layerName, [inputEdge]));
                    previousLayerOutputTensor = outTensorName;
                    continue;
                }

                // Operator info
                OperatorCategory opCat = KerasOperatorCatalog.MapLayerCategory(layerClass);
                OperatorInfo opInfo = new(layerClass, opCat, "keras");

                // Parse layer configuration attributes
                List<NodeAttribute> attributes = [];
                ExtractLayerAttributes(lConfig, attributes);

                // Collect input tensor names
                List<string> inputTensorNames = [];
                if (isSequential)
                {
                    if (previousLayerOutputTensor != null)
                    {
                        inputTensorNames.Add(previousLayerOutputTensor);
                    }
                }
                else
                {
                    // Functional model: inspect inbound_nodes
                    if (lElem.TryGetProperty("inbound_nodes", out var inNodesElem))
                    {
                        CollectInboundTensorNames(inNodesElem, inputTensorNames);
                    }
                }

                // Wire input ports
                List<NodePort> nodeInputs = [];
                for (int i = 0; i < inputTensorNames.Count; i++)
                {
                    string inName = inputTensorNames[i];
                    string portName = inputTensorNames.Count == 1 ? "input" : $"input_{i}";
                    if (!tensorCatalog.TryGetValue(inName, out var inEdge))
                    {
                        inEdge = new TensorEdge(inName, new TensorDescriptor(DataType.Float32, TensorShape.Scalar), TensorKind.Activation);
                        tensorCatalog[inName] = inEdge;
                    }
                    nodeInputs.Add(new NodePort(portName, [inEdge]));
                }

                // Bind HDF5 weights as initializers
                if (hdf5 != null)
                {
                    BindLayerWeights(layerName, layerClass, hdf5, tensorCatalog, nodeInputs);
                }

                // Create layer output tensor
                string outputTensorName = $"{layerName}:0";
                DataType outType = DataType.Float32;
                var outEdge = new TensorEdge(outputTensorName, new TensorDescriptor(outType, TensorShape.Scalar), TensorKind.Activation);
                tensorCatalog[outputTensorName] = outEdge;
                List<NodePort> nodeOutputs = [new NodePort("output", [outEdge])];

                // Handle nested sub-models
                if ((string.Equals(layerClass, "Sequential", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(layerClass, "Functional", StringComparison.OrdinalIgnoreCase)) &&
                    lConfig.TryGetProperty("layers", out _))
                {
                    var nestedGraph = BuildGraphFromJsonConfig(lElem, hdf5, metadata, diagnostics, cancellationToken);
                    if (nestedGraph.PrimarySubgraph != null)
                    {
                        attributes.Add(new NodeAttribute("subgraph", new AttributeValue.Graph(nestedGraph.PrimarySubgraph)));
                    }
                }

                nodes.Add(new GraphNode(layerName, opInfo, nodeInputs, nodeOutputs, attributes));
                previousLayerOutputTensor = outputTensorName;
            }
        }

        // Boundary ports for Functional model
        if (!isSequential)
        {
            if (configElem.TryGetProperty("input_layers", out var inLayersElem) && inLayersElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in inLayersElem.EnumerateArray())
                {
                    string? lName = ExtractLayerRef(item);
                    if (lName != null && !graphInputs.Any(p => string.Equals(p.Name, lName, StringComparison.Ordinal)))
                    {
                        string tName = $"{lName}:0";
                        if (!tensorCatalog.TryGetValue(tName, out var inEdge))
                        {
                            inEdge = new TensorEdge(tName, new TensorDescriptor(DataType.Float32, TensorShape.Scalar), TensorKind.GraphInput);
                            tensorCatalog[tName] = inEdge;
                        }
                        graphInputs.Add(new GraphPort(lName, [inEdge]));
                    }
                }
            }

            if (configElem.TryGetProperty("output_layers", out var outLayersElem) && outLayersElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outLayersElem.EnumerateArray())
                {
                    string? lName = ExtractLayerRef(item);
                    if (lName != null && !graphOutputs.Any(p => string.Equals(p.Name, lName, StringComparison.Ordinal)))
                    {
                        string tName = $"{lName}:0";
                        if (!tensorCatalog.TryGetValue(tName, out var outEdge))
                        {
                            outEdge = new TensorEdge(tName, new TensorDescriptor(DataType.Float32, TensorShape.Scalar), TensorKind.GraphOutput);
                            tensorCatalog[tName] = outEdge;
                        }
                        graphOutputs.Add(new GraphPort(lName, [outEdge]));
                    }
                }
            }
        }
        else
        {
            // Sequential: last node output is graph output
            if (nodes.Count > 0 && nodes[^1].Outputs.Count > 0)
            {
                var lastOut = nodes[^1].Outputs[0].Tensors[0];
                graphOutputs.Add(new GraphPort(nodes[^1].Name, [lastOut]));
            }
        }

        var subgraph = new Subgraph(
            Name: "main",
            Nodes: nodes,
            Tensors: [.. tensorCatalog.Values],
            Inputs: graphInputs,
            Outputs: graphOutputs);

        return new ModelGraph(
            Format: FormatInfo,
            Producer: producer,
            Subgraphs: [subgraph],
            Metadata: metadata,
            Diagnostics: diagnostics);
    }

    private static ModelGraph BuildWeightsOnlyGraph(
        Hdf5File hdf5,
        Dictionary<string, string> metadata,
        List<ParseDiagnostic> diagnostics)
    {
        var tensorCatalog = new Dictionary<string, TensorEdge>(StringComparer.Ordinal);
        var nodes = new List<GraphNode>();

        // Group datasets by parent group or layer name
        var grouped = new Dictionary<string, List<Hdf5Dataset>>(StringComparer.Ordinal);
        for (int i = 0; i < hdf5.Datasets.Count; i++)
        {
            Hdf5Dataset ds = hdf5.Datasets[i];
            string layerName = ds.LayerName ?? ExtractLayerNameFromPath(ds.Path);
            if (!grouped.TryGetValue(layerName, out var list))
            {
                list = [];
                grouped[layerName] = list;
            }
            list.Add(ds);
        }

        foreach (var (layerName, datasets) in grouped)
        {
            List<NodePort> nodeInputs = [];
            foreach (var ds in datasets)
            {
                TensorDataSource? dsRef = ds.IsContiguous
                    ? new TensorDataSlice(ds.StreamOffset, ds.ByteLength, ds.DataType, TensorDataEncoding.Raw)
                    : null;

                var edge = new TensorEdge(
                    ds.Path,
                    new TensorDescriptor(ds.DataType, ds.Shape),
                    TensorKind.Initializer,
                    dsRef);

                tensorCatalog[ds.Path] = edge;
                nodeInputs.Add(new NodePort(ds.Name, [edge]));
            }

            OperatorInfo opInfo = new("WeightLayer", OperatorCategory.Layer, "keras");
            nodes.Add(new GraphNode(layerName, opInfo, nodeInputs, [], []));
        }

        var subgraph = new Subgraph(
            Name: "main",
            Nodes: nodes,
            Tensors: [.. tensorCatalog.Values],
            Inputs: [],
            Outputs: []);

        return new ModelGraph(
            Format: FormatInfo,
            Producer: "Keras (Weights Only)",
            Subgraphs: [subgraph],
            Metadata: metadata,
            Diagnostics: diagnostics);
    }

    private static void BindLayerWeights(
        string layerName,
        string layerClass,
        Hdf5File hdf5,
        Dictionary<string, TensorEdge> tensorCatalog,
        List<NodePort> nodeInputs)
    {
        for (int i = 0; i < hdf5.Datasets.Count; i++)
        {
            Hdf5Dataset ds = hdf5.Datasets[i];
            bool matches;

            if (!string.IsNullOrEmpty(ds.LayerName))
            {
                matches = string.Equals(ds.LayerName, layerName, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                matches = ds.Path.Contains($"/{layerName}/", StringComparison.OrdinalIgnoreCase) ||
                          ds.Path.EndsWith($"/{layerName}", StringComparison.OrdinalIgnoreCase) ||
                          ds.Path.StartsWith($"{layerName}/", StringComparison.OrdinalIgnoreCase);
            }

            if (matches)
            {
                string portName = ds.Name switch
                {
                    "0" => "kernel",
                    "1" => "bias",
                    "2" => "gamma",
                    "3" => "beta",
                    _ => ds.Name
                };

                TensorDataSource? dsRef = ds.IsContiguous
                    ? new TensorDataSlice(ds.StreamOffset, ds.ByteLength, ds.DataType, TensorDataEncoding.Raw)
                    : null;

                string tensorName = $"{layerName}/{portName}";
                var edge = new TensorEdge(
                    tensorName,
                    new TensorDescriptor(ds.DataType, ds.Shape),
                    TensorKind.Initializer,
                    dsRef);

                tensorCatalog[tensorName] = edge;
                nodeInputs.Add(new NodePort(portName, [edge]));
            }
        }
    }

    private static void CollectInboundTensorNames(JsonElement inNodesElem, List<string> inputNames)
    {
        if (inNodesElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in inNodesElem.EnumerateArray())
            {
                CollectInboundItem(item, inputNames);
            }
        }
    }

    private static void CollectInboundItem(JsonElement elem, List<string> inputNames)
    {
        if (elem.ValueKind == JsonValueKind.String)
        {
            inputNames.Add($"{elem.GetString()}:0");
            return;
        }

        if (elem.ValueKind == JsonValueKind.Object)
        {
            if (elem.TryGetProperty("keras_history", out var khElem) && khElem.ValueKind == JsonValueKind.Array)
            {
                string? srcLayer = khElem.EnumerateArray().FirstOrDefault().GetString();
                if (!string.IsNullOrEmpty(srcLayer))
                {
                    inputNames.Add($"{srcLayer}:0");
                }
                return;
            }

            if (elem.TryGetProperty("config", out var cfgElem))
            {
                CollectInboundItem(cfgElem, inputNames);
            }

            if (elem.TryGetProperty("args", out var argsElem))
            {
                CollectInboundItem(argsElem, inputNames);
            }
        }
        else if (elem.ValueKind == JsonValueKind.Array)
        {
            var arr = elem.EnumerateArray().ToList();
            if (arr.Count >= 2 && arr[0].ValueKind == JsonValueKind.String && arr[1].ValueKind == JsonValueKind.Number)
            {
                inputNames.Add($"{arr[0].GetString()}:0");
                return;
            }

            foreach (var sub in arr)
            {
                CollectInboundItem(sub, inputNames);
            }
        }
    }

    private static string? ExtractLayerRef(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            return item.GetString();
        }

        if (item.ValueKind == JsonValueKind.Array)
        {
            var first = item.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.String)
            {
                return first.GetString();
            }
        }

        return null;
    }

    private static TensorShape ParseInputShape(JsonElement lConfig)
    {
        JsonElement shapeElem = default;
        if (lConfig.TryGetProperty("batch_shape", out var bs) && bs.ValueKind == JsonValueKind.Array)
        {
            shapeElem = bs;
        }
        else if (lConfig.TryGetProperty("batch_input_shape", out var bis) && bis.ValueKind == JsonValueKind.Array)
        {
            shapeElem = bis;
        }
        else if (lConfig.TryGetProperty("shape", out var s) && s.ValueKind == JsonValueKind.Array)
        {
            shapeElem = s;
        }

        if (shapeElem.ValueKind != JsonValueKind.Array)
        {
            return TensorShape.Scalar;
        }

        List<TensorDimension> dims = [];
        foreach (var d in shapeElem.EnumerateArray())
        {
            if (d.ValueKind == JsonValueKind.Null)
            {
                dims.Add(TensorDimension.Dynamic.Instance);
            }
            else if (d.TryGetInt64(out long val))
            {
                dims.Add(new TensorDimension.Fixed(val));
            }
            else
            {
                dims.Add(TensorDimension.Dynamic.Instance);
            }
        }

        return new TensorShape(dims);
    }

    private static void ExtractLayerAttributes(JsonElement config, List<NodeAttribute> attributes)
    {
        foreach (var prop in config.EnumerateObject())
        {
            string name = prop.Name;
            JsonElement val = prop.Value;

            if (string.Equals(name, "name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "dtype", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "inbound_nodes", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (val.ValueKind)
            {
                case JsonValueKind.String:
                    attributes.Add(new NodeAttribute(name, new AttributeValue.String(val.GetString()!)));
                    break;
                case JsonValueKind.Number when val.TryGetInt64(out long intVal):
                    attributes.Add(new NodeAttribute(name, new AttributeValue.Int64(intVal)));
                    break;
                case JsonValueKind.Number when val.TryGetDouble(out double dblVal):
                    attributes.Add(new NodeAttribute(name, new AttributeValue.Float64(dblVal)));
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    attributes.Add(new NodeAttribute(name, new AttributeValue.Bool(val.GetBoolean())));
                    break;
                case JsonValueKind.Array:
                    var list = val.EnumerateArray().ToList();
                    if (list.All(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out _)))
                    {
                        attributes.Add(new NodeAttribute(name, new AttributeValue.Int64List(list.Select(x => x.GetInt64()).ToList())));
                    }
                    else if (list.All(x => x.ValueKind == JsonValueKind.String))
                    {
                        attributes.Add(new NodeAttribute(name, new AttributeValue.StringList(list.Select(x => x.GetString()!).ToList())));
                    }
                    break;
            }
        }
    }

    private static string ExtractLayerNameFromPath(string path)
    {
        string[] parts = path.Split('/');
        if (parts.Length >= 2)
        {
            if (string.Equals(parts[0], "model_weights", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parts[0], "layers", StringComparison.OrdinalIgnoreCase))
            {
                return parts[1];
            }
        }

        return parts[0];
    }
}
