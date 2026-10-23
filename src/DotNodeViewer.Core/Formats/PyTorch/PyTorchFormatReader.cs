using System.Globalization;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.IO.Pickle;
using DotNodeViewer.Core.IO.Zip;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.PyTorch;

/// <summary>
/// Streaming reader for PyTorch checkpoints, state_dict dictionaries, and TorchScript archives (.pt/.pth).
/// </summary>
internal sealed class PyTorchFormatReader : IModelFormatReader
{
    /// <summary>
    /// Gets a shared singleton instance of <see cref="PyTorchFormatReader"/>.
    /// </summary>
    public static PyTorchFormatReader Instance { get; } = new();

    /// <inheritdoc/>
    public ModelFormatId FormatId => ModelFormatId.PyTorch;

    private static readonly ModelFormatInfo FormatInfo = new(
        Id: ModelFormatId.PyTorch,
        DisplayName: "PyTorch (.pt/.pth)");

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
            throw new ModelParseException("Stream must be readable and seekable to parse PyTorch archives.");
        }

        var source = new RandomAccessSource(stream);
        try
        {
            if (ZipDirectory.TryRead(source, out var zip))
            {
                return new ValueTask<ModelGraph>(ParseZipArchive(zip, cancellationToken));
            }

            // Raw Pickle format fallback
            return new ValueTask<ModelGraph>(ParseRawPickle(stream, cancellationToken));
        }
        catch (ModelParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModelParseException($"Failed to parse PyTorch model archive: {ex.Message}", ex);
        }
    }

    private static ModelGraph ParseZipArchive(ZipDirectory zip, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!zip.TryFindEntry("data.pkl", out var dataPklEntry))
        {
            throw new ModelParseException("PyTorch archive missing required 'data.pkl' entry.");
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format"] = "PyTorch",
            ["container"] = "ZIP"
        };

        if (zip.TryFindEntry("version", out var versionEntry))
        {
            metadata["pytorch_version"] = zip.ReadEntryText(versionEntry).Trim();
        }
        if (zip.TryFindEntry("byteorder", out var byteorderEntry))
        {
            metadata["byteorder"] = zip.ReadEntryText(byteorderEntry).Trim();
        }

        var diagnostics = new List<ParseDiagnostic>();

        // Persistent loader: maps ('storage', storageCls, key, device, numel) -> PyStorageRef
        Func<PyObject, PyObject?> persistentLoad = persid =>
        {
            if (persid is PyTuple tuple && tuple.Items.Count >= 5 &&
                tuple.Items[0] is PyString s && string.Equals(s.Value, "storage", StringComparison.Ordinal) &&
                tuple.Items[1] is PyGlobal storageCls &&
                tuple.Items[3] is PyString dev &&
                tuple.Items[4] is PyInt numel)
            {
                string key = tuple.Items[2] switch
                {
                    PyString strKey => strKey.Value,
                    PyInt intKey => intKey.Value.ToString(CultureInfo.InvariantCulture),
                    _ => "0"
                };

                string storagePath = $"data/{key}";
                if (zip.TryFindEntry(storagePath, out var entry))
                {
                    DataType dt = PyTorchOperatorCatalog.MapStorageClassToDataType(storageCls.Name);
                    return new PyStorageRef(storageCls.Name, key, dev.Value, numel.Value, dt, entry.DataOffset, entry.UncompressedSize);
                }
            }
            return persid;
        };

        PyObject rootObject;
        using (var dataPklStream = zip.OpenEntryStream(dataPklEntry))
        {
            var machine = new PickleMachine(dataPklStream, persistentLoad);
            rootObject = machine.Load();
        }

        // Check if TorchScript: look for code/__torch__.py or similar
        ZipEntry? torchScriptEntry = null;
        foreach (var entry in zip.Entries.Values)
        {
            if (entry.Name.EndsWith("code/__torch__.py", StringComparison.Ordinal) ||
                (!entry.Name.EndsWith(".debug_pkl", StringComparison.Ordinal) && entry.Name.Contains("/code/__torch__") && entry.Name.EndsWith(".py", StringComparison.Ordinal)))
            {
                torchScriptEntry = entry;
                break;
            }
        }

        List<GraphNode> nodes;
        IReadOnlyList<GraphPort> inputs = Array.Empty<GraphPort>();
        IReadOnlyList<GraphPort> outputs = Array.Empty<GraphPort>();
        var tensorCatalog = new Dictionary<string, TensorEdge>(StringComparer.Ordinal);

        if (torchScriptEntry != null && rootObject is PyInstance rootInstance)
        {
            metadata["model_type"] = "TorchScript";
            string pythonSource = zip.ReadEntryText(torchScriptEntry);
            var parser = new TorchScriptParser(pythonSource, rootInstance);

            if (parser.TryParseForward(out var tsNodes, out var tsInputs, out var tsOutputs, out string? diag))
            {
                nodes = tsNodes;
                inputs = tsInputs;
                outputs = tsOutputs;

                foreach (var edge in parser.EmittedEdges)
                {
                    tensorCatalog[edge.Name] = edge;
                }
            }
            else
            {
                if (diag != null)
                {
                    diagnostics.Add(new ParseDiagnostic(
                        Severity: parser.HasControlFlow ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
                        Code: parser.HasControlFlow ? "DNV_TORCH_CONTROL_FLOW" : "DNV_TORCH_FALLBACK",
                        Message: diag));
                }

                nodes = BuildModuleHierarchyNodes(rootInstance, tensorCatalog);
            }
        }
        else if (rootObject is PyDict stateDict)
        {
            metadata["model_type"] = "state_dict";
            diagnostics.Add(new ParseDiagnostic(
                Severity: DiagnosticSeverity.Info,
                Code: "DNV_TORCH_STATE_DICT",
                Message: "PyTorch state_dict checkpoint contains model weights and module parameters without an executable computation graph."));

            nodes = BuildStateDictNodes(stateDict, tensorCatalog);
        }
        else if (rootObject is PyInstance pickledModule)
        {
            metadata["model_type"] = "PickledModule";
            diagnostics.Add(new ParseDiagnostic(
                Severity: DiagnosticSeverity.Info,
                Code: "DNV_TORCH_PICKLED_MODULE",
                Message: "PyTorch pickled nn.Module checkpoint contains serialized module hierarchy and parameters."));

            nodes = BuildModuleHierarchyNodes(pickledModule, tensorCatalog);
        }
        else
        {
            metadata["model_type"] = "GenericPickle";
            nodes = new List<GraphNode>();
        }

        // Collect all tensors referenced in nodes or ports
        foreach (var node in nodes)
        {
            foreach (var port in node.Inputs)
            {
                foreach (var t in port.Tensors)
                {
                    tensorCatalog[t.Name] = t;
                }
            }
            foreach (var port in node.Outputs)
            {
                foreach (var t in port.Tensors)
                {
                    tensorCatalog[t.Name] = t;
                }
            }
        }

        var allTensors = tensorCatalog.Values.ToList();
        var subgraph = new Subgraph("main", nodes, allTensors, inputs, outputs);
        var graph = new ModelGraph(
            Format: FormatInfo,
            Producer: "PyTorch",
            Subgraphs: [subgraph],
            Metadata: metadata,
            Diagnostics: diagnostics);

        return graph;
    }

    private static List<GraphNode> BuildStateDictNodes(PyDict stateDict, Dictionary<string, TensorEdge> tensorCatalog)
    {
        // Group parameters by module prefix (e.g. "layer1.0.conv1.weight" -> module "layer1.0.conv1")
        var moduleMap = new Dictionary<string, List<(string ParamName, PyTensor Tensor)>>(StringComparer.Ordinal);

        foreach (var (keyObj, valObj) in stateDict.Items)
        {
            if (keyObj is not PyString keyStr || valObj is not PyTensor tensor)
            {
                continue;
            }

            string fullKey = keyStr.Value;
            int lastDot = fullKey.LastIndexOf('.');
            string moduleName = lastDot > 0 ? fullKey.Substring(0, lastDot) : "model";
            string paramName = lastDot > 0 ? fullKey.Substring(lastDot + 1) : fullKey;

            if (!moduleMap.TryGetValue(moduleName, out var list))
            {
                list = new List<(string, PyTensor)>();
                moduleMap[moduleName] = list;
            }
            list.Add((paramName, tensor));
        }

        var nodes = new List<GraphNode>(moduleMap.Count);
        foreach (var (modName, paramsList) in moduleMap)
        {
            string inferredOp = InferOperatorName(modName, paramsList);
            OperatorCategory category = PyTorchOperatorCatalog.MapOperatorCategory(inferredOp);

            var inPorts = new List<NodePort>(paramsList.Count);
            foreach (var (paramName, tensor) in paramsList)
            {
                string edgeName = $"{modName}.{paramName}";
                var edge = new TensorEdge(
                    Name: edgeName,
                    Descriptor: new TensorDescriptor(tensor.DataType, tensor.Shape),
                    Kind: TensorKind.Initializer,
                    Data: tensor.Slice);

                tensorCatalog[edgeName] = edge;
                inPorts.Add(new NodePort(paramName, [edge]));
            }

            nodes.Add(new GraphNode(
                Name: modName,
                Operator: new OperatorInfo(inferredOp, category),
                Inputs: inPorts,
                Outputs: Array.Empty<NodePort>(),
                Attributes: Array.Empty<NodeAttribute>()));
        }

        return nodes;
    }

    private static List<GraphNode> BuildModuleHierarchyNodes(PyInstance root, Dictionary<string, TensorEdge> tensorCatalog)
    {
        var nodes = new List<GraphNode>();
        CollectHierarchyNodes(root, string.Empty, nodes, tensorCatalog);
        return nodes;
    }

    private static void CollectHierarchyNodes(PyInstance inst, string prefix, List<GraphNode> nodes, Dictionary<string, TensorEdge> tensorCatalog)
    {
        // Check for submodules in inst.Dict or inst.Dict["_modules"]
        PyDict? modulesDict = null;
        if (inst.Dict.TryGetValue("_modules", out var modObj) && modObj is PyDict md)
        {
            modulesDict = md;
        }

        bool hasChildren = false;
        if (modulesDict != null && modulesDict.Items.Count > 0)
        {
            hasChildren = true;
            foreach (var (k, v) in modulesDict.Items)
            {
                if (k is PyString ks && v is PyInstance childInst)
                {
                    string childPath = string.IsNullOrEmpty(prefix) ? ks.Value : $"{prefix}.{ks.Value}";
                    CollectHierarchyNodes(childInst, childPath, nodes, tensorCatalog);
                }
            }
        }
        else
        {
            foreach (var (k, v) in inst.Dict)
            {
                if (v is PyInstance childInst && !k.StartsWith('_'))
                {
                    hasChildren = true;
                    string childPath = string.IsNullOrEmpty(prefix) ? k : $"{prefix}.{k}";
                    CollectHierarchyNodes(childInst, childPath, nodes, tensorCatalog);
                }
            }
        }

        // If this module has its own tensors (weight, bias, etc.), or is a leaf module, emit a node
        var tensorPorts = new List<NodePort>();
        var attributes = new List<NodeAttribute>();

        // Collect parameters from _parameters or direct dict
        if (inst.Dict.TryGetValue("_parameters", out var paramObj) && paramObj is PyDict pd)
        {
            AddTensorsFromDict(pd, prefix, tensorPorts, tensorCatalog);
        }
        if (inst.Dict.TryGetValue("_buffers", out var bufObj) && bufObj is PyDict bd)
        {
            AddTensorsFromDict(bd, prefix, tensorPorts, tensorCatalog);
        }

        foreach (var (k, v) in inst.Dict)
        {
            if (v is PyTensor pt && !k.StartsWith('_'))
            {
                string edgeName = string.IsNullOrEmpty(prefix) ? k : $"{prefix}.{k}";
                var edge = new TensorEdge(
                    Name: edgeName,
                    Descriptor: new TensorDescriptor(pt.DataType, pt.Shape),
                    Kind: TensorKind.Initializer,
                    Data: pt.Slice);
                tensorCatalog[edgeName] = edge;
                tensorPorts.Add(new NodePort(k, [edge]));
            }
            else if (v is PyBool pb && !k.StartsWith('_'))
            {
                attributes.Add(new NodeAttribute(k, new AttributeValue.Bool(pb.Value)));
            }
            else if (v is PyInt pi && !k.StartsWith('_'))
            {
                attributes.Add(new NodeAttribute(k, new AttributeValue.Int64(pi.Value)));
            }
            else if (v is PyString ps && !k.StartsWith('_'))
            {
                attributes.Add(new NodeAttribute(k, new AttributeValue.String(ps.Value)));
            }
        }

        if (tensorPorts.Count > 0 || !hasChildren)
        {
            string nodeName = string.IsNullOrEmpty(prefix) ? inst.Cls.Name : prefix;
            string opType = inst.Cls.Name;
            OperatorCategory category = PyTorchOperatorCatalog.MapOperatorCategory(opType);

            nodes.Add(new GraphNode(
                Name: nodeName,
                Operator: new OperatorInfo(opType, category),
                Inputs: tensorPorts,
                Outputs: Array.Empty<NodePort>(),
                Attributes: attributes));
        }
    }

    private static void AddTensorsFromDict(PyDict dict, string prefix, List<NodePort> ports, Dictionary<string, TensorEdge> tensorCatalog)
    {
        foreach (var (k, v) in dict.Items)
        {
            if (k is PyString ks && v is PyTensor pt)
            {
                string edgeName = string.IsNullOrEmpty(prefix) ? ks.Value : $"{prefix}.{ks.Value}";
                var edge = new TensorEdge(
                    Name: edgeName,
                    Descriptor: new TensorDescriptor(pt.DataType, pt.Shape),
                    Kind: TensorKind.Initializer,
                    Data: pt.Slice);
                tensorCatalog[edgeName] = edge;
                ports.Add(new NodePort(ks.Value, [edge]));
            }
        }
    }

    private static string InferOperatorName(string moduleName, List<(string ParamName, PyTensor Tensor)> paramsList)
    {
        string lower = moduleName.ToLowerInvariant();
        if (lower.Contains("conv")) return "Conv2d";
        if (lower.Contains("bn") || lower.Contains("batchnorm")) return "BatchNorm2d";
        if (lower.Contains("linear") || lower.Contains("fc") || lower.Contains("dense")) return "Linear";
        if (lower.Contains("relu")) return "ReLU";
        if (lower.Contains("embed")) return "Embedding";
        if (lower.Contains("norm")) return "LayerNorm";

        bool hasWeight = paramsList.Any(p => p.ParamName == "weight");
        bool hasBias = paramsList.Any(p => p.ParamName == "bias");
        bool hasRunningMean = paramsList.Any(p => p.ParamName.Contains("mean"));

        if (hasRunningMean) return "BatchNorm2d";
        if (hasWeight && hasBias) return "Layer";
        return "ParameterGroup";
    }

    private static ModelGraph ParseRawPickle(Stream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var machine = new PickleMachine(stream);
        var root = machine.Load();
        var tensorCatalog = new Dictionary<string, TensorEdge>(StringComparer.Ordinal);

        List<GraphNode> nodes;
        if (root is PyDict dict)
        {
            nodes = BuildStateDictNodes(dict, tensorCatalog);
        }
        else if (root is PyInstance inst)
        {
            nodes = BuildModuleHierarchyNodes(inst, tensorCatalog);
        }
        else
        {
            nodes = new List<GraphNode>();
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format"] = "PyTorch",
            ["container"] = "RawPickle"
        };

        var diagnostics = new List<ParseDiagnostic>
        {
            new(DiagnosticSeverity.Info, "DNV_TORCH_RAW_PICKLE", "PyTorch raw pickle checkpoint loaded.")
        };

        var allTensors = tensorCatalog.Values.ToList();
        var subgraph = new Subgraph("main", nodes, allTensors, Array.Empty<GraphPort>(), Array.Empty<GraphPort>());
        return new ModelGraph(FormatInfo, "PyTorch", [subgraph], metadata, diagnostics);
    }
}
