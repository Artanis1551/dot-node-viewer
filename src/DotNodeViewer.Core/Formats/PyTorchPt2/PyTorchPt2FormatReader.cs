using System.Text.Json;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Formats.PyTorch;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.IO.Zip;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.PyTorchPt2;

/// <summary>
/// Streaming reader for PyTorch 2 <c>torch.export.save</c> (<c>.pt2</c>) archives.
/// </summary>
internal sealed class PyTorchPt2FormatReader : IModelFormatReader
{
    /// <summary>
    /// Gets a shared singleton instance of <see cref="PyTorchPt2FormatReader"/>.
    /// </summary>
    public static PyTorchPt2FormatReader Instance { get; } = new();

    /// <inheritdoc/>
    public ModelFormatId FormatId => ModelFormatId.PyTorchPt2;

    private static readonly ModelFormatInfo FormatInfo = new(
        Id: ModelFormatId.PyTorchPt2,
        DisplayName: "PyTorch ExportedProgram (.pt2)");

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
            throw new ModelParseException("Stream must be readable and seekable to parse PyTorch .pt2 archives.");
        }

        ZipDirectory? zip = options?.CachedZipDirectory;
        if (zip is null)
        {
            var source = new RandomAccessSource(stream);
            if (!ZipDirectory.TryRead(source, out zip))
            {
                throw new ModelParseException("File is not a valid ZIP archive for PyTorch .pt2 export.", fileOffset: 0);
            }
        }

        try
        {
            return new ValueTask<ModelGraph>(ParsePt2Archive(zip, cancellationToken));
        }
        catch (ModelParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModelParseException($"Failed to parse PyTorch .pt2 export archive: {ex.Message}", ex);
        }
    }

    private static ModelGraph ParsePt2Archive(ZipDirectory zip, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Locate model.json or serialized_exported_program.json
        if (!zip.TryFindEntry("models/model.json", out var modelEntry) &&
            !zip.TryFindEntry("serialized_exported_program.json", out modelEntry) &&
            !zip.TryFindEntry("model.json", out modelEntry))
        {
            throw new ModelParseException("ExportedProgram model specification (model.json) not found in .pt2 archive.");
        }

        using var jsonDoc = JsonDocument.Parse(zip.ReadEntryBytes(modelEntry));
        JsonElement root = jsonDoc.RootElement;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);

        // 2. Extract top-level metadata
        if (root.TryGetProperty("torch_version", out var torchVer) && torchVer.ValueKind == JsonValueKind.String)
        {
            metadata["torch_version"] = torchVer.GetString()!;
        }

        if (root.TryGetProperty("schema_version", out var schemaVer))
        {
            metadata["schema_version"] = schemaVer.ToString();
        }

        if (root.TryGetProperty("opset_version", out var opsetVer))
        {
            metadata["opset_version"] = opsetVer.ToString();
        }

        // 3. Extract range constraints
        if (root.TryGetProperty("range_constraints", out var rcElem) && rcElem.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in rcElem.EnumerateObject())
            {
                string symName = prop.Name;
                long minVal = prop.Value.TryGetProperty("min_val", out var minElem) && minElem.TryGetInt64(out long min) ? min : 0;
                long maxVal = prop.Value.TryGetProperty("max_val", out var maxElem) && maxElem.TryGetInt64(out long max) ? max : long.MaxValue;
                metadata[$"range_constraints.{symName}"] = $"[{minVal}, {maxVal}]";
            }
        }

        // 4. Map weight files via model_weights_config.json
        var weightZipEntries = new Dictionary<string, ZipEntry>(StringComparer.Ordinal);
        if (zip.TryFindEntry("data/weights/model_weights_config.json", out var weightsConfigEntry) ||
            zip.TryFindEntry("model_weights_config.json", out weightsConfigEntry) ||
            zip.TryFindEntry("weights_config.json", out weightsConfigEntry))
        {
            using var weightsDoc = JsonDocument.Parse(zip.ReadEntryBytes(weightsConfigEntry));
            if (weightsDoc.RootElement.TryGetProperty("config", out var configElem) && configElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in configElem.EnumerateObject())
                {
                    string paramName = prop.Name;
                    if (prop.Value.TryGetProperty("path_name", out var pathNameElem) &&
                        pathNameElem.ValueKind == JsonValueKind.String)
                    {
                        string pathName = pathNameElem.GetString()!;
                        if (zip.TryFindEntry($"data/weights/{pathName}", out var entry) ||
                            zip.TryFindEntry($"weights/{pathName}", out entry) ||
                            zip.TryFindEntry(pathName, out entry))
                        {
                            weightZipEntries[paramName] = entry;
                        }
                    }
                }
            }
        }

        // Map constants via model_constants_config.json
        if (zip.TryFindEntry("data/constants/model_constants_config.json", out var constConfigEntry) ||
            zip.TryFindEntry("model_constants_config.json", out constConfigEntry))
        {
            using var constDoc = JsonDocument.Parse(zip.ReadEntryBytes(constConfigEntry));
            if (constDoc.RootElement.TryGetProperty("config", out var configElem) && configElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in configElem.EnumerateObject())
                {
                    string constName = prop.Name;
                    if (prop.Value.TryGetProperty("path_name", out var pathNameElem) &&
                        pathNameElem.ValueKind == JsonValueKind.String)
                    {
                        string pathName = pathNameElem.GetString()!;
                        if (zip.TryFindEntry($"data/constants/{pathName}", out var entry) ||
                            zip.TryFindEntry($"constants/{pathName}", out entry) ||
                            zip.TryFindEntry(pathName, out entry))
                        {
                            weightZipEntries[constName] = entry;
                        }
                    }
                }
            }
        }

        // 5. Navigate to graph_module and graph
        if (!root.TryGetProperty("graph_module", out var gmElem))
        {
            throw new ModelParseException("Invalid .pt2 schema: 'graph_module' property missing from model.json.");
        }

        if (!gmElem.TryGetProperty("graph", out var graphElem))
        {
            throw new ModelParseException("Invalid .pt2 schema: 'graph' property missing from graph_module.");
        }

        // 6. Signature analysis (parameters vs user inputs/outputs)
        var parameterTensors = new HashSet<string>(StringComparer.Ordinal);
        var bufferTensors = new HashSet<string>(StringComparer.Ordinal);
        var constantTensors = new HashSet<string>(StringComparer.Ordinal);
        var userInputs = new List<string>();
        var userOutputs = new List<string>();
        var tensorToWeightKey = new Dictionary<string, string>(StringComparer.Ordinal);

        if (gmElem.TryGetProperty("signature", out var sigElem))
        {
            ParseSignature(
                sigElem,
                parameterTensors,
                bufferTensors,
                constantTensors,
                userInputs,
                userOutputs,
                tensorToWeightKey);
        }

        // 7. Parse tensor_values
        var tensorMap = new Dictionary<string, TensorEdge>(StringComparer.Ordinal);
        if (graphElem.TryGetProperty("tensor_values", out var tvElem) && tvElem.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in tvElem.EnumerateObject())
            {
                string tensorName = prop.Name;
                JsonElement tVal = prop.Value;

                int scalarType = tVal.TryGetProperty("dtype", out var dtElem) && dtElem.TryGetInt32(out int dtVal) ? dtVal : 7;
                DataType dataType = MapScalarTypeToDataType(scalarType);

                List<TensorDimension> dims = [];
                if (tVal.TryGetProperty("sizes", out var sizesElem) && sizesElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in sizesElem.EnumerateArray())
                    {
                        dims.Add(ParseDimension(d));
                    }
                }
                TensorShape shape = new(dims);

                // Determine TensorKind
                TensorKind kind;
                if (parameterTensors.Contains(tensorName) || bufferTensors.Contains(tensorName) || constantTensors.Contains(tensorName))
                {
                    kind = TensorKind.Initializer;
                }
                else if (userInputs.Contains(tensorName))
                {
                    kind = TensorKind.GraphInput;
                }
                else if (userOutputs.Contains(tensorName))
                {
                    kind = TensorKind.GraphOutput;
                }
                else
                {
                    kind = TensorKind.Activation;
                }

                // Lazy weight payload slicing
                TensorDataSource? dataSource = null;
                if (kind == TensorKind.Initializer)
                {
                    string? lookupKey = null;
                    if (tensorToWeightKey.TryGetValue(tensorName, out var weightKey))
                    {
                        lookupKey = weightKey;
                    }
                    else if (weightZipEntries.ContainsKey(tensorName))
                    {
                        lookupKey = tensorName;
                    }

                    if (lookupKey != null && weightZipEntries.TryGetValue(lookupKey, out var entry) && entry.IsStored)
                    {
                        dataSource = new TensorDataSlice(entry.DataOffset, entry.UncompressedSize, dataType, TensorDataEncoding.Raw);
                    }
                }

                tensorMap[tensorName] = new TensorEdge(tensorName, new TensorDescriptor(dataType, shape), kind, dataSource);
            }
        }

        // 8. Parse nodes
        List<GraphNode> nodes = [];
        if (graphElem.TryGetProperty("nodes", out var nodesElem) && nodesElem.ValueKind == JsonValueKind.Array)
        {
            int nodeIndex = 0;
            foreach (var nElem in nodesElem.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                string nodeName = nElem.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                    ? nameProp.GetString()!
                    : $"node_{nodeIndex}";

                string target = nElem.TryGetProperty("target", out var targetProp) && targetProp.ValueKind == JsonValueKind.String
                    ? targetProp.GetString()!
                    : "unknown";

                OperatorInfo opInfo = CreateOperatorInfo(target);

                List<NodePort> nodeInputs = [];
                List<NodeAttribute> nodeAttrs = [];

                if (nElem.TryGetProperty("inputs", out var inputsElem) && inputsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var inItem in inputsElem.EnumerateArray())
                    {
                        string formalName = inItem.TryGetProperty("name", out var fnProp) && fnProp.ValueKind == JsonValueKind.String
                            ? fnProp.GetString()!
                            : "arg";

                        if (inItem.TryGetProperty("arg", out var argElem))
                        {
                            ParseNodeArgument(formalName, argElem, tensorMap, nodeInputs, nodeAttrs);
                        }
                    }
                }

                List<NodePort> nodeOutputs = [];
                if (nElem.TryGetProperty("outputs", out var outputsElem) && outputsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var outItem in outputsElem.EnumerateArray())
                    {
                        if (outItem.TryGetProperty("as_tensor", out var outTensorElem) &&
                            outTensorElem.TryGetProperty("name", out var otNameProp) &&
                            otNameProp.ValueKind == JsonValueKind.String)
                        {
                            string outName = otNameProp.GetString()!;
                            if (tensorMap.TryGetValue(outName, out var edge))
                            {
                                nodeOutputs.Add(new NodePort(outName, [edge]));
                            }
                        }
                        else if (outItem.TryGetProperty("as_tensors", out var outTensorsElem) &&
                                 outTensorsElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var otElem in outTensorsElem.EnumerateArray())
                            {
                                if (otElem.TryGetProperty("name", out var subNameProp) &&
                                    subNameProp.ValueKind == JsonValueKind.String)
                                {
                                    string outName = subNameProp.GetString()!;
                                    if (tensorMap.TryGetValue(outName, out var edge))
                                    {
                                        nodeOutputs.Add(new NodePort(outName, [edge]));
                                    }
                                }
                            }
                        }
                    }
                }

                // Extract documentation / stack trace
                string? documentation = null;
                if (nElem.TryGetProperty("metadata", out var metaElem) && metaElem.ValueKind == JsonValueKind.Object)
                {
                    if (metaElem.TryGetProperty("stack_trace", out var stElem) && stElem.ValueKind == JsonValueKind.String)
                    {
                        documentation = stElem.GetString();
                    }

                    if (metaElem.TryGetProperty("nn_module_stack", out var msElem) && msElem.ValueKind == JsonValueKind.String)
                    {
                        nodeAttrs.Add(new NodeAttribute("nn_module_stack", new AttributeValue.String(msElem.GetString()!)));
                    }

                    if (metaElem.TryGetProperty("torch_fn", out var fnElem) && fnElem.ValueKind == JsonValueKind.String)
                    {
                        nodeAttrs.Add(new NodeAttribute("torch_fn", new AttributeValue.String(fnElem.GetString()!)));
                    }
                }

                nodes.Add(new GraphNode(nodeName, opInfo, nodeInputs, nodeOutputs, nodeAttrs, documentation));
                nodeIndex++;
            }
        }

        // 9. Subgraph boundary ports
        List<GraphPort> graphInputs = [];
        foreach (string inName in userInputs)
        {
            if (tensorMap.TryGetValue(inName, out var edge))
            {
                graphInputs.Add(new GraphPort(inName, [edge]));
            }
        }

        if (graphInputs.Count == 0)
        {
            foreach (var edge in tensorMap.Values)
            {
                if (edge.Kind == TensorKind.GraphInput)
                {
                    graphInputs.Add(new GraphPort(edge.Name, [edge]));
                }
            }
        }

        List<GraphPort> graphOutputs = [];
        foreach (string outName in userOutputs)
        {
            if (tensorMap.TryGetValue(outName, out var edge))
            {
                graphOutputs.Add(new GraphPort(outName, [edge]));
            }
        }

        if (graphOutputs.Count == 0)
        {
            foreach (var edge in tensorMap.Values)
            {
                if (edge.Kind == TensorKind.GraphOutput)
                {
                    graphOutputs.Add(new GraphPort(edge.Name, [edge]));
                }
            }
        }

        var subgraph = new Subgraph(
            Name: "main",
            Nodes: nodes,
            Tensors: [.. tensorMap.Values],
            Inputs: graphInputs,
            Outputs: graphOutputs);

        string? producer = metadata.TryGetValue("torch_version", out var tv)
            ? $"PyTorch {tv}"
            : "PyTorch 2 torch.export";

        return new ModelGraph(
            Format: FormatInfo,
            Producer: producer,
            Subgraphs: [subgraph],
            Metadata: metadata,
            Diagnostics: []);
    }

    private static void ParseSignature(
        JsonElement sigElem,
        HashSet<string> parameterTensors,
        HashSet<string> bufferTensors,
        HashSet<string> constantTensors,
        List<string> userInputs,
        List<string> userOutputs,
        Dictionary<string, string> tensorToWeightKey)
    {
        // Modern PyTorch 2.5/2.6+ input_specs
        if (sigElem.TryGetProperty("input_specs", out var inputSpecsElem) && inputSpecsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var spec in inputSpecsElem.EnumerateArray())
            {
                if (spec.TryGetProperty("parameter", out var paramElem))
                {
                    string tensorName = paramElem.GetProperty("arg").GetProperty("name").GetString()!;
                    string paramName = paramElem.TryGetProperty("parameter_name", out var pName)
                        ? pName.GetString()!
                        : paramElem.GetProperty("name").GetString()!;
                    parameterTensors.Add(tensorName);
                    tensorToWeightKey[tensorName] = paramName;
                }
                else if (spec.TryGetProperty("buffer", out var bufElem))
                {
                    string tensorName = bufElem.GetProperty("arg").GetProperty("name").GetString()!;
                    string bufName = bufElem.TryGetProperty("buffer_name", out var bName)
                        ? bName.GetString()!
                        : bufElem.GetProperty("name").GetString()!;
                    bufferTensors.Add(tensorName);
                    tensorToWeightKey[tensorName] = bufName;
                }
                else if (spec.TryGetProperty("lifted_tensor_constant", out var constElem))
                {
                    string tensorName = constElem.GetProperty("arg").GetProperty("name").GetString()!;
                    string constName = constElem.GetProperty("name").GetString()!;
                    constantTensors.Add(tensorName);
                    tensorToWeightKey[tensorName] = constName;
                }
                else if (spec.TryGetProperty("user_input", out var userElem))
                {
                    if (userElem.TryGetProperty("arg", out var arg) &&
                        arg.TryGetProperty("as_tensor", out var asTensor) &&
                        asTensor.TryGetProperty("name", out var nameProp))
                    {
                        userInputs.Add(nameProp.GetString()!);
                    }
                }
            }
        }
        else
        {
            // Legacy PyTorch <= 2.4 inputs_to_parameters, inputs_to_buffers, user_inputs
            if (sigElem.TryGetProperty("inputs_to_parameters", out var itpElem) && itpElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in itpElem.EnumerateObject())
                {
                    parameterTensors.Add(prop.Name);
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        tensorToWeightKey[prop.Name] = prop.Value.GetString()!;
                    }
                }
            }

            if (sigElem.TryGetProperty("inputs_to_buffers", out var itbElem) && itbElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in itbElem.EnumerateObject())
                {
                    bufferTensors.Add(prop.Name);
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        tensorToWeightKey[prop.Name] = prop.Value.GetString()!;
                    }
                }
            }

            if (sigElem.TryGetProperty("inputs_to_lifted_tensor_constants", out var itcElem) && itcElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in itcElem.EnumerateObject())
                {
                    constantTensors.Add(prop.Name);
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        tensorToWeightKey[prop.Name] = prop.Value.GetString()!;
                    }
                }
            }

            if (sigElem.TryGetProperty("user_inputs", out var uiElem) && uiElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in uiElem.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        userInputs.Add(item.GetString()!);
                    }
                }
            }
        }

        // Modern output_specs
        if (sigElem.TryGetProperty("output_specs", out var outputSpecsElem) && outputSpecsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var spec in outputSpecsElem.EnumerateArray())
            {
                if (spec.TryGetProperty("user_output", out var userElem) &&
                    userElem.TryGetProperty("arg", out var arg) &&
                    arg.TryGetProperty("as_tensor", out var asTensor) &&
                    asTensor.TryGetProperty("name", out var nameProp))
                {
                    userOutputs.Add(nameProp.GetString()!);
                }
            }
        }
        else if (sigElem.TryGetProperty("user_outputs", out var uoElem) && uoElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in uoElem.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    userOutputs.Add(item.GetString()!);
                }
            }
        }
    }

    private static OperatorInfo CreateOperatorInfo(string target)
    {
        string lookupName = target;
        if (lookupName.StartsWith("torch.ops.aten.", StringComparison.OrdinalIgnoreCase))
        {
            lookupName = lookupName["torch.ops.aten.".Length..];
        }
        else if (lookupName.StartsWith("torch.ops.", StringComparison.OrdinalIgnoreCase))
        {
            lookupName = lookupName["torch.ops.".Length..];
        }

        int dotIdx = lookupName.IndexOf('.', StringComparison.Ordinal);
        string baseOp = dotIdx >= 0 ? lookupName[..dotIdx] : lookupName;

        OperatorCategory cat = PyTorchOperatorCatalog.MapOperatorCategory(baseOp);
        if (cat == OperatorCategory.Custom && dotIdx >= 0)
        {
            cat = PyTorchOperatorCatalog.MapOperatorCategory(lookupName);
        }

        return new OperatorInfo(target, cat, "aten");
    }

    private static void ParseNodeArgument(
        string formalName,
        JsonElement argElem,
        Dictionary<string, TensorEdge> tensorMap,
        List<NodePort> nodeInputs,
        List<NodeAttribute> nodeAttrs)
    {
        if (argElem.TryGetProperty("as_tensor", out var asTensorElem) &&
            asTensorElem.TryGetProperty("name", out var tNameProp) &&
            tNameProp.ValueKind == JsonValueKind.String)
        {
            string tensorName = tNameProp.GetString()!;
            if (tensorMap.TryGetValue(tensorName, out var edge))
            {
                nodeInputs.Add(new NodePort(formalName, [edge]));
            }
            else
            {
                // Fallback edge for unlisted symbol/tensor
                var fallbackEdge = new TensorEdge(
                    tensorName,
                    new TensorDescriptor(DataType.Float32, TensorShape.Scalar),
                    TensorKind.Activation);
                tensorMap[tensorName] = fallbackEdge;
                nodeInputs.Add(new NodePort(formalName, [fallbackEdge]));
            }
            return;
        }

        if (argElem.TryGetProperty("as_tensors", out var asTensorsElem) &&
            asTensorsElem.ValueKind == JsonValueKind.Array)
        {
            List<TensorEdge> edges = [];
            foreach (var tItem in asTensorsElem.EnumerateArray())
            {
                if (tItem.TryGetProperty("name", out var itemProp) && itemProp.ValueKind == JsonValueKind.String)
                {
                    string tensorName = itemProp.GetString()!;
                    if (tensorMap.TryGetValue(tensorName, out var edge))
                    {
                        edges.Add(edge);
                    }
                }
            }
            nodeInputs.Add(new NodePort(formalName, edges));
            return;
        }

        if (argElem.TryGetProperty("as_int", out var intElem) && intElem.TryGetInt64(out long intVal))
        {
            nodeAttrs.Add(new NodeAttribute(formalName, new AttributeValue.Int64(intVal)));
            return;
        }

        if (argElem.TryGetProperty("as_ints", out var intsElem) && intsElem.ValueKind == JsonValueKind.Array)
        {
            List<long> ints = [];
            foreach (var iItem in intsElem.EnumerateArray())
            {
                if (iItem.TryGetInt64(out long v))
                {
                    ints.Add(v);
                }
            }
            nodeAttrs.Add(new NodeAttribute(formalName, new AttributeValue.Int64List(ints)));
            return;
        }

        if (argElem.TryGetProperty("as_float", out var floatElem) && floatElem.TryGetDouble(out double floatVal))
        {
            nodeAttrs.Add(new NodeAttribute(formalName, new AttributeValue.Float64(floatVal)));
            return;
        }

        if (argElem.TryGetProperty("as_floats", out var floatsElem) && floatsElem.ValueKind == JsonValueKind.Array)
        {
            List<double> floats = [];
            foreach (var fItem in floatsElem.EnumerateArray())
            {
                if (fItem.TryGetDouble(out double v))
                {
                    floats.Add(v);
                }
            }
            nodeAttrs.Add(new NodeAttribute(formalName, new AttributeValue.Float64List(floats)));
            return;
        }

        if (argElem.TryGetProperty("as_bool", out var boolElem) &&
            (boolElem.ValueKind == JsonValueKind.True || boolElem.ValueKind == JsonValueKind.False))
        {
            nodeAttrs.Add(new NodeAttribute(formalName, new AttributeValue.Bool(boolElem.GetBoolean())));
            return;
        }

        if (argElem.TryGetProperty("as_string", out var strElem) && strElem.ValueKind == JsonValueKind.String)
        {
            nodeAttrs.Add(new NodeAttribute(formalName, new AttributeValue.String(strElem.GetString()!)));
            return;
        }

        if (argElem.TryGetProperty("as_strings", out var stringsElem) && stringsElem.ValueKind == JsonValueKind.Array)
        {
            List<string> strings = [];
            foreach (var sItem in stringsElem.EnumerateArray())
            {
                if (sItem.ValueKind == JsonValueKind.String)
                {
                    strings.Add(sItem.GetString()!);
                }
            }
            nodeAttrs.Add(new NodeAttribute(formalName, new AttributeValue.StringList(strings)));
            return;
        }

        if (argElem.TryGetProperty("as_sym_int", out var symElem))
        {
            string symStr = symElem.ValueKind == JsonValueKind.String ? symElem.GetString()! : symElem.ToString();
            nodeAttrs.Add(new NodeAttribute(formalName, new AttributeValue.String(symStr)));
            return;
        }

        if (argElem.TryGetProperty("as_none", out _))
        {
            nodeAttrs.Add(new NodeAttribute(formalName, new AttributeValue.String("None")));
        }
    }

    private static TensorDimension ParseDimension(JsonElement d)
    {
        if (d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out long num))
        {
            return new TensorDimension.Fixed(num);
        }

        if (d.TryGetProperty("as_int", out var intElem) && intElem.TryGetInt64(out long fixedVal))
        {
            return new TensorDimension.Fixed(fixedVal);
        }

        if (d.TryGetProperty("as_sym_int", out var symElem))
        {
            if (symElem.ValueKind == JsonValueKind.String)
            {
                return new TensorDimension.Symbolic(symElem.GetString()!);
            }
            if (symElem.ValueKind == JsonValueKind.Number && symElem.TryGetInt64(out long symInt))
            {
                return new TensorDimension.Fixed(symInt);
            }
        }

        if (d.TryGetProperty("as_expr", out var exprElem))
        {
            if (exprElem.TryGetProperty("expr_str", out var exprStrElem))
            {
                string exprStr = exprStrElem.GetString() ?? string.Empty;
                int symStart = exprStr.IndexOf("Symbol('", StringComparison.Ordinal);
                if (symStart >= 0)
                {
                    int nameStart = symStart + "Symbol('".Length;
                    int nameEnd = exprStr.IndexOf('\'', nameStart);
                    if (nameEnd > nameStart)
                    {
                        return new TensorDimension.Symbolic(exprStr[nameStart..nameEnd]);
                    }
                }

                if (!string.IsNullOrWhiteSpace(exprStr))
                {
                    return new TensorDimension.Symbolic(exprStr);
                }
            }

            if (exprElem.TryGetProperty("hint", out var hintElem) &&
                hintElem.TryGetProperty("as_int", out var hintInt) &&
                hintInt.TryGetInt64(out long hintVal))
            {
                return new TensorDimension.Fixed(hintVal);
            }
        }

        return TensorDimension.Dynamic.Instance;
    }

    private static DataType MapScalarTypeToDataType(int scalarType) => scalarType switch
    {
        1 => DataType.UInt8,
        2 => DataType.Int8,
        3 => DataType.Int16,
        4 => DataType.Int32,
        5 => DataType.Int64,
        6 => DataType.Float16,
        7 => DataType.Float32,
        8 => DataType.Float64,
        10 => DataType.Complex64,
        11 => DataType.Complex128,
        12 => DataType.Bool,
        13 => DataType.BFloat16,
        28 => DataType.UInt16,
        34 => DataType.UInt32,
        35 => DataType.UInt64,
        _ => DataType.Float32,
    };
}

