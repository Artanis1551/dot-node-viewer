using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO.Pickle;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.PyTorch;

/// <summary>
/// Mini-parser for TorchScript serialized Python AST files (<c>code/__torch__/**/*.py</c>).
/// </summary>
internal sealed class TorchScriptParser
{
    private readonly string _pythonSource;
    private readonly PyInstance _rootModule;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly List<GraphNode> _emittedNodes = new();
    private readonly List<TensorEdge> _emittedEdges = new();
    private readonly List<GraphPort> _graphInputs = new();
    private readonly List<GraphPort> _graphOutputs = new();
    private int _tempCounter;

    /// <summary>
    /// Gets a value indicating whether unsupported scripted control flow (<c>if</c>, <c>while</c>, <c>for</c>) was encountered.
    /// </summary>
    public bool HasControlFlow { get; private set; }

    /// <summary>
    /// Gets the list of all tensor edges created while parsing.
    /// </summary>
    public IReadOnlyList<TensorEdge> EmittedEdges => _emittedEdges;

    /// <summary>
    /// Initializes a new instance of the <see cref="TorchScriptParser"/> class.
    /// </summary>
    public TorchScriptParser(string pythonSource, PyInstance rootModule)
    {
        ArgumentNullException.ThrowIfNull(pythonSource);
        ArgumentNullException.ThrowIfNull(rootModule);
        _pythonSource = pythonSource;
        _rootModule = rootModule;
    }

    /// <summary>
    /// Parses the TorchScript forward method and emits nodes, edges, inputs, and outputs.
    /// </summary>
    public bool TryParseForward(
        [NotNullWhen(true)] out List<GraphNode>? nodes,
        [NotNullWhen(true)] out IReadOnlyList<GraphPort>? inputs,
        [NotNullWhen(true)] out IReadOnlyList<GraphPort>? outputs,
        out string? diagnosticMessage)
    {
        nodes = null;
        inputs = null;
        outputs = null;
        diagnosticMessage = null;

        string[] rawLines = _pythonSource.Split('\n');
        int forwardStartLine = -1;
        for (int i = 0; i < rawLines.Length; i++)
        {
            string trimmed = rawLines[i].Trim();
            if (trimmed.StartsWith("def forward(", StringComparison.Ordinal))
            {
                forwardStartLine = i;
                break;
            }
        }

        if (forwardStartLine < 0)
        {
            diagnosticMessage = "Could not find 'def forward(' method signature in TorchScript source.";
            return false;
        }

        // Collect forward signature lines until ':' at line end
        var sigBuilder = new StringBuilder();
        int bodyLineStart = forwardStartLine;
        while (bodyLineStart < rawLines.Length)
        {
            sigBuilder.Append(' ').Append(rawLines[bodyLineStart].Trim());
            if (rawLines[bodyLineStart].TrimEnd().EndsWith(':'))
            {
                bodyLineStart++;
                break;
            }
            bodyLineStart++;
        }

        string signature = sigBuilder.ToString();
        ParseSignatureInputs(signature);

        // Collect body lines
        var bodyLines = new List<string>();
        for (int i = bodyLineStart; i < rawLines.Length; i++)
        {
            string line = rawLines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Check if indented (part of method body)
            if (!char.IsWhiteSpace(line[0]))
            {
                // Unindented line marks end of method
                break;
            }

            string trimmed = line.Trim();
            if (trimmed.StartsWith("if ", StringComparison.Ordinal) ||
                trimmed.StartsWith("while ", StringComparison.Ordinal) ||
                trimmed.StartsWith("for ", StringComparison.Ordinal))
            {
                HasControlFlow = true;
                diagnosticMessage = "TorchScript source contains scripted control flow ('if'/'while'); falling back to module hierarchy.";
                return false;
            }

            bodyLines.Add(trimmed);
        }

        try
        {
            foreach (string stmt in bodyLines)
            {
                ParseStatement(stmt);
            }

            if (_graphOutputs.Count == 0 && _emittedNodes.Count > 0)
            {
                var lastNode = _emittedNodes[^1];
                if (lastNode.Outputs.Count > 0 && lastNode.Outputs[0].Tensors.Count > 0)
                {
                    var outEdge = lastNode.Outputs[0].Tensors[0];
                    _graphOutputs.Add(new GraphPort("output", [outEdge]));
                }
            }

            nodes = _emittedNodes;
            inputs = _graphInputs;
            outputs = _graphOutputs;
            return true;
        }
        catch (Exception ex)
        {
            diagnosticMessage = $"TorchScript forward parsing exception: {ex.Message}";
            return false;
        }
    }

    private void ParseSignatureInputs(string signature)
    {
        int openParen = signature.IndexOf('(');
        int closeParen = signature.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen) return;

        string paramStr = signature.Substring(openParen + 1, closeParen - openParen - 1);
        string[] parts = SplitTopLevel(paramStr, ',');
        foreach (string rawPart in parts)
        {
            string part = rawPart.Trim();
            if (string.IsNullOrEmpty(part) || part.StartsWith("self", StringComparison.Ordinal))
            {
                continue;
            }

            int colonIdx = part.IndexOf(':');
            string paramName = colonIdx > 0 ? part.Substring(0, colonIdx).Trim() : part;
            var edge = GetOrCreateEdge(paramName, TensorKind.GraphInput);
            _graphInputs.Add(new GraphPort(paramName, [edge]));
        }
    }

    private void ParseStatement(string stmt)
    {
        if (stmt.StartsWith("return ", StringComparison.Ordinal))
        {
            string returnExpr = stmt["return ".Length..].Trim();
            string outEdgeName = EmitExpression(returnExpr, null);
            var edge = GetOrCreateEdge(outEdgeName, TensorKind.GraphOutput);
            _graphOutputs.Add(new GraphPort("output", [edge]));
            return;
        }

        int eqIdx = stmt.IndexOf('=');
        if (eqIdx > 0 && stmt[eqIdx - 1] != '!' && stmt[eqIdx - 1] != '=' && stmt[eqIdx - 1] != '<' && stmt[eqIdx - 1] != '>')
        {
            string lhs = stmt.Substring(0, eqIdx).Trim();
            string rhs = stmt.Substring(eqIdx + 1).Trim();

            // Check for alias: var = self.field
            if (rhs.StartsWith("self.", StringComparison.Ordinal))
            {
                string targetField = rhs["self.".Length..].Trim();
                _aliases[lhs] = targetField;
                return;
            }

            EmitExpression(rhs, lhs);
        }
    }

    private string EmitExpression(string expr, string? targetVar)
    {
        expr = expr.Trim();
        while (expr.EndsWith(',')) expr = expr[..^1].Trim();

        // Strip enclosing parentheses if they wrap the entire expression
        if (expr.StartsWith('(') && expr.EndsWith(')'))
        {
            int depth = 0;
            bool wraps = true;
            for (int i = 0; i < expr.Length - 1; i++)
            {
                if (expr[i] == '(') depth++;
                else if (expr[i] == ')') depth--;
                if (depth == 0) { wraps = false; break; }
            }
            if (wraps) expr = expr[1..^1].Trim();
        }

        // Case 1: Expression starts with '(': e.g. (submod).forward(...)
        if (expr.StartsWith('('))
        {
            int depth = 0;
            int closeParen = -1;
            for (int i = 0; i < expr.Length; i++)
            {
                if (expr[i] == '(') depth++;
                else if (expr[i] == ')')
                {
                    depth--;
                    if (depth == 0) { closeParen = i; break; }
                }
            }

            if (closeParen > 0 && closeParen + 1 < expr.Length)
            {
                string afterTarget = expr.Substring(closeParen + 1).Trim();
                if (afterTarget.StartsWith(".forward(", StringComparison.Ordinal) && afterTarget.EndsWith(')'))
                {
                    string target = expr.Substring(1, closeParen - 1).Trim();
                    string innerArgs = afterTarget.Substring(".forward(".Length, afterTarget.Length - ".forward(".Length - 1);
                    return EmitModuleForward(target, innerArgs, targetVar);
                }
            }
        }

        // Case 2: Standard call: <callee>(<args>), e.g. torch.flatten(...) or submod.forward(...)
        int firstParen = expr.IndexOf('(');
        if (firstParen > 0 && expr.EndsWith(')'))
        {
            int depth = 0;
            int matchingClose = -1;
            for (int i = firstParen; i < expr.Length; i++)
            {
                if (expr[i] == '(') depth++;
                else if (expr[i] == ')')
                {
                    depth--;
                    if (depth == 0) { matchingClose = i; break; }
                }
            }

            if (matchingClose == expr.Length - 1)
            {
                string callee = expr.Substring(0, firstParen).Trim();
                string innerArgs = expr.Substring(firstParen + 1, expr.Length - firstParen - 2);

                if (callee.EndsWith(".forward", StringComparison.Ordinal))
                {
                    string target = callee[..^".forward".Length].Trim();
                    return EmitModuleForward(target, innerArgs, targetVar);
                }

                return EmitFunctionCall(callee, innerArgs, targetVar);
            }
        }

        // Simple variable or literal
        return expr;
    }

    private string EmitModuleForward(string modulePath, string innerArgs, string? targetVar)
    {
        if (modulePath.StartsWith('(') && modulePath.EndsWith(')'))
        {
            modulePath = modulePath[1..^1].Trim();
        }
        if (modulePath.StartsWith("self.", StringComparison.Ordinal))
        {
            modulePath = modulePath["self.".Length..].Trim();
        }
        else if (_aliases.TryGetValue(modulePath, out string? realTarget))
        {
            modulePath = realTarget;
        }

        string outName = targetVar ?? $"_temp_{++_tempCounter}";
        string[] args = SplitTopLevel(innerArgs, ',');

        List<TensorEdge> inputEdges = new();
        foreach (string rawArg in args)
        {
            string arg = rawArg.Trim();
            if (string.IsNullOrEmpty(arg)) continue;
            string argEdgeName = EmitExpression(arg, null);
            inputEdges.Add(GetOrCreateEdge(argEdgeName, TensorKind.Activation));
        }

        // Resolve submodule from rootInstance
        PyInstance? subInst = ResolveSubmodule(_rootModule, modulePath);
        string opType = subInst?.Cls.Name ?? "Module";
        OperatorCategory category = PyTorchOperatorCatalog.MapOperatorCategory(opType);

        List<NodePort> inPorts = new();
        if (inputEdges.Count > 0)
        {
            inPorts.Add(new NodePort("input", inputEdges));
        }

        List<NodeAttribute> attributes = new();
        // Attach initializers (weight, bias, running_mean, etc.)
        if (subInst != null)
        {
            foreach (var kv in subInst.Dict)
            {
                if (kv.Value is PyTensor pyTensor)
                {
                    string edgeName = $"{modulePath}.{kv.Key}";
                    var initEdge = new TensorEdge(
                        Name: edgeName,
                        Descriptor: new TensorDescriptor(pyTensor.DataType, pyTensor.Shape),
                        Kind: TensorKind.Initializer,
                        Data: pyTensor.Slice);
                    _emittedEdges.Add(initEdge);
                    inPorts.Add(new NodePort(kv.Key, [initEdge]));
                }
                else if (kv.Value is PyBool pb)
                {
                    attributes.Add(new NodeAttribute(kv.Key, new AttributeValue.Bool(pb.Value)));
                }
                else if (kv.Value is PyInt pi)
                {
                    attributes.Add(new NodeAttribute(kv.Key, new AttributeValue.Int64(pi.Value)));
                }
                else if (kv.Value is PyFloat pf)
                {
                    attributes.Add(new NodeAttribute(kv.Key, new AttributeValue.Float64(pf.Value)));
                }
                else if (kv.Value is PyString ps)
                {
                    attributes.Add(new NodeAttribute(kv.Key, new AttributeValue.String(ps.Value)));
                }
            }
        }

        var outEdge = GetOrCreateEdge(outName, TensorKind.Activation);
        var outPorts = new List<NodePort> { new NodePort("output", [outEdge]) };

        var node = new GraphNode(
            Name: modulePath,
            Operator: new OperatorInfo(opType, category),
            Inputs: inPorts,
            Outputs: outPorts,
            Attributes: attributes);

        _emittedNodes.Add(node);
        return outName;
    }

    private string EmitFunctionCall(string funcName, string innerArgs, string? targetVar)
    {
        string outName = targetVar ?? $"_temp_{++_tempCounter}";
        string[] args = SplitTopLevel(innerArgs, ',');

        List<TensorEdge> inputEdges = new();
        List<NodeAttribute> attributes = new();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].Trim();
            if (string.IsNullOrEmpty(arg)) continue;

            if (arg.Contains(".forward(") || arg.StartsWith("torch.", StringComparison.Ordinal))
            {
                string edgeName = EmitExpression(arg, null);
                inputEdges.Add(GetOrCreateEdge(edgeName, TensorKind.Activation));
            }
            else if (long.TryParse(arg, CultureInfo.InvariantCulture, out long intVal))
            {
                attributes.Add(new NodeAttribute($"arg_{i}", new AttributeValue.Int64(intVal)));
            }
            else if (bool.TryParse(arg, out bool boolVal))
            {
                attributes.Add(new NodeAttribute($"arg_{i}", new AttributeValue.Bool(boolVal)));
            }
            else if (double.TryParse(arg, CultureInfo.InvariantCulture, out double floatVal))
            {
                attributes.Add(new NodeAttribute($"arg_{i}", new AttributeValue.Float64(floatVal)));
            }
            else
            {
                inputEdges.Add(GetOrCreateEdge(arg, TensorKind.Activation));
            }
        }

        OperatorCategory category = PyTorchOperatorCatalog.MapOperatorCategory(funcName);
        var inPorts = new List<NodePort>();
        if (inputEdges.Count > 0)
        {
            inPorts.Add(new NodePort("input", inputEdges));
        }

        var outEdge = GetOrCreateEdge(outName, TensorKind.Activation);
        var outPorts = new List<NodePort> { new NodePort("output", [outEdge]) };

        var node = new GraphNode(
            Name: outName,
            Operator: new OperatorInfo(funcName, category),
            Inputs: inPorts,
            Outputs: outPorts,
            Attributes: attributes);

        _emittedNodes.Add(node);
        return outName;
    }

    private TensorEdge GetOrCreateEdge(string name, TensorKind kind)
    {
        foreach (var edge in _emittedEdges)
        {
            if (string.Equals(edge.Name, name, StringComparison.Ordinal))
            {
                return edge;
            }
        }

        var newEdge = new TensorEdge(
            Name: name,
            Descriptor: new TensorDescriptor(DataType.Float32, null),
            Kind: kind,
            Data: null);
        _emittedEdges.Add(newEdge);
        return newEdge;
    }

    private static PyInstance? ResolveSubmodule(PyInstance root, string path)
    {
        string[] parts = path.Split('.');
        PyInstance current = root;
        foreach (string part in parts)
        {
            if (current.Dict.TryGetValue(part, out var nextObj) && nextObj is PyInstance nextInst)
            {
                current = nextInst;
            }
            else
            {
                return null;
            }
        }
        return current;
    }

    private static string[] SplitTopLevel(string text, char delimiter)
    {
        var result = new List<string>();
        int depth = 0;
        int lastStart = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == delimiter && depth == 0)
            {
                result.Add(text.Substring(lastStart, i - lastStart));
                lastStart = i + 1;
            }
        }
        if (lastStart <= text.Length)
        {
            result.Add(text.Substring(lastStart));
        }
        return result.ToArray();
    }
}
