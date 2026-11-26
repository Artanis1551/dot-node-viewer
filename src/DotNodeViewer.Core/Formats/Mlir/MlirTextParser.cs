using System.Globalization;
using System.Text.RegularExpressions;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Mlir;

/// <summary>
/// Streaming recursive-descent parser for textual MLIR (<c>.mlir</c>) modules, functions,
/// nested regions, SSA dataflow, dialect attributes, and <c>dense_resource</c> weight references.
/// </summary>
internal sealed partial class MlirTextParser
{
    private readonly MlirTextLexer _lexer;
    private readonly IReadOnlyList<MlirToken> _tokens;
    private readonly ModelReadOptions _options;
    private readonly CancellationToken _cancellationToken;
    private readonly List<ParseDiagnostic> _diagnostics = [];
    private readonly Dictionary<string, string> _typeAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _attrAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResourceEntryInfo> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _moduleMetadata = new(StringComparer.Ordinal);

    private int _pos;
    private int _anonNodeCounter;

    private MlirTextParser(MlirTextLexer lexer, ModelReadOptions options, CancellationToken cancellationToken)
    {
        _lexer = lexer;
        _tokens = lexer.Tokens;
        _options = options;
        _cancellationToken = cancellationToken;
    }

    public static ModelGraph Read(
        RandomAccessSource source,
        ModelReadOptions options,
        CancellationToken cancellationToken)
    {
        MlirTextLexer lexer = MlirTextLexer.Create(source, options, cancellationToken);
        MlirTextParser parser = new(lexer, options, cancellationToken);
        return parser.ParseModuleGraph();
    }

    private ModelGraph ParseModuleGraph()
    {
        PreScanAliasesAndResources();

        List<Subgraph> functions = [];
        List<GraphNode> looseOps = [];
        Dictionary<string, TensorEdge> looseTensors = new(StringComparer.Ordinal);
        List<GraphPort> looseOutputs = [];

        while (!IsAtEnd())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            MlirToken tok = Peek();

            if (tok.Kind == MlirTokenKind.ResourceBlock)
            {
                Advance();
                continue;
            }

            // Skip file-level #alias = ... or !alias = ...
            if (tok.Kind is MlirTokenKind.AttrAlias or MlirTokenKind.TypeAlias && Peek(1).Kind == MlirTokenKind.Equals)
            {
                SkipTopLevelAliasDefinition();
                continue;
            }

            if (IsModuleHeader(tok))
            {
                ParseModuleContainer(functions);
                continue;
            }

            if (IsFunctionHeader(tok))
            {
                functions.Add(ParseFunction());
                continue;
            }

            // Loose top-level operation fallback
            RegionParseState state = new("main", looseTensors);
            if (TryParseOperation(state, depth: 0, looseOps, looseOutputs))
            {
                continue;
            }

            Advance();
        }

        if (looseOps.Count > 0)
        {
            functions.Add(new Subgraph(
                "main",
                looseOps,
                [.. looseTensors.Values],
                [],
                looseOutputs));
        }

        if (functions.Count == 0)
        {
            functions.Add(new Subgraph("main", [], [], [], []));
        }

        // Ensure primary entry function (@main or @forward) is placed at index 0
        int primaryIndex = -1;
        for (int i = 0; i < functions.Count; i++)
        {
            if (string.Equals(functions[i].Name, "main", StringComparison.Ordinal) ||
                string.Equals(functions[i].Name, "forward", StringComparison.Ordinal))
            {
                primaryIndex = i;
                break;
            }
        }

        if (primaryIndex > 0)
        {
            Subgraph primary = functions[primaryIndex];
            functions.RemoveAt(primaryIndex);
            functions.Insert(0, primary);
        }

        if (_resources.Count > 0)
        {
            _moduleMetadata["mlir.resource_count"] = _resources.Count.ToString(CultureInfo.InvariantCulture);
        }

        return new ModelGraph(
            new ModelFormatInfo(ModelFormatId.Mlir, "MLIR", "Textual"),
            Producer: null,
            functions,
            _moduleMetadata,
            _diagnostics);
    }

    private void PreScanAliasesAndResources()
    {
        for (int i = 0; i < _tokens.Count; i++)
        {
            MlirToken tok = _tokens[i];
            if (tok.Kind == MlirTokenKind.ResourceBlock)
            {
                ParseResourceBlock(_lexer.GetText(tok));
                continue;
            }

            if ((tok.Kind is MlirTokenKind.TypeAlias or MlirTokenKind.AttrAlias)
                && i + 1 < _tokens.Count
                && _tokens[i + 1].Kind == MlirTokenKind.Equals)
            {
                string aliasName = _lexer.GetText(tok);
                int rhsIdx = i + 2;
                if (rhsIdx < _tokens.Count)
                {
                    int startChar = _tokens[rhsIdx].StartChar;
                    int endChar = FindAliasDefinitionEnd(rhsIdx);
                    string rhsText = _lexer.SliceChars(startChar, endChar).Trim();
                    if (tok.Kind == MlirTokenKind.TypeAlias)
                    {
                        _typeAliases[aliasName] = rhsText;
                    }
                    else
                    {
                        _attrAliases[aliasName] = rhsText;
                    }
                }
            }
        }
    }

    private int FindAliasDefinitionEnd(int startIdx)
    {
        int depth = 0;
        int i = startIdx;
        int startLine = _tokens[startIdx].Line;
        while (i < _tokens.Count)
        {
            MlirToken t = _tokens[i];
            if (t.Kind == MlirTokenKind.Eof)
            {
                return t.StartChar;
            }

            if (depth == 0 && i > startIdx && t.Line > startLine)
            {
                return _tokens[i - 1].StartChar + _tokens[i - 1].Length;
            }

            if (t.Kind is MlirTokenKind.LParen or MlirTokenKind.LBrace or MlirTokenKind.LBracket or MlirTokenKind.LAngle)
            {
                depth++;
            }
            else if (t.Kind is MlirTokenKind.RParen or MlirTokenKind.RBrace or MlirTokenKind.RBracket or MlirTokenKind.RAngle)
            {
                if (depth > 0)
                {
                    depth--;
                }
            }

            i++;
        }

        return _lexer.Text.Length;
    }

    private void SkipTopLevelAliasDefinition()
    {
        Advance(); // #alias or !alias
        Advance(); // =
        int line = Peek().Line;
        int depth = 0;
        while (!IsAtEnd())
        {
            MlirToken t = Peek();
            if (depth == 0 && t.Line > line)
            {
                break;
            }

            if (t.Kind is MlirTokenKind.LParen or MlirTokenKind.LBrace or MlirTokenKind.LBracket or MlirTokenKind.LAngle)
            {
                depth++;
            }
            else if (t.Kind is MlirTokenKind.RParen or MlirTokenKind.RBrace or MlirTokenKind.RBracket or MlirTokenKind.RAngle)
            {
                if (depth > 0)
                {
                    depth--;
                }
            }

            Advance();
        }
    }

    private void ParseResourceBlock(string blockText)
    {
        MatchCollection matches = ResourceEntryRegex().Matches(blockText);
        foreach (Match match in matches)
        {
            string key = match.Groups["key"].Value;
            string hex = match.Groups["hex"].Value;
            long byteLength = ComputeMlirHexBlobByteLength(hex);
            _resources[key] = new ResourceEntryInfo(key, byteLength);
        }
    }

    private static long ComputeMlirHexBlobByteLength(string hexLiteral)
    {
        ReadOnlySpan<char> span = hexLiteral.AsSpan();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        int totalBytes = span.Length / 2;
        if (totalBytes > 4 && span.Length >= 8)
        {
            // MLIR AsmPrinter prefixes binary resource hex blobs with a 4-byte little-endian alignment header.
            if (uint.TryParse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint b0)
                && uint.TryParse(span.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint b1)
                && uint.TryParse(span.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint b2)
                && uint.TryParse(span.Slice(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint b3))
            {
                uint alignment = b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
                if (alignment is 1 or 2 or 4 or 8 or 16 or 32 or 64)
                {
                    return totalBytes - 4;
                }
            }
        }

        return totalBytes;
    }

    private bool IsModuleHeader(MlirToken tok)
    {
        if (tok.Kind == MlirTokenKind.Identifier && _lexer.GetSpan(tok).SequenceEqual("module"))
        {
            return true;
        }

        if (tok.Kind == MlirTokenKind.StringLiteral && _lexer.GetUnescapedString(tok) == "builtin.module")
        {
            return true;
        }

        return false;
    }

    private void ParseModuleContainer(List<Subgraph> functions)
    {
        MlirToken modTok = Advance();
        if (modTok.Kind == MlirTokenKind.StringLiteral)
        {
            // Generic form: "builtin.module"() ({ ... }) : () -> ()
            if (Peek().Kind == MlirTokenKind.LParen)
            {
                SkipBalanced(MlirTokenKind.LParen, MlirTokenKind.RParen);
            }

            if (Peek().Kind == MlirTokenKind.LParen)
            {
                Advance(); // (
                if (Peek().Kind == MlirTokenKind.LBrace)
                {
                    ParseModuleBody(functions);
                }

                if (Peek().Kind == MlirTokenKind.RParen)
                {
                    Advance();
                }
            }

            SkipTrailingTypeAndAttributes();
            return;
        }

        // Custom form: module [@name] [attributes {...}] { ... }
        if (Peek().Kind == MlirTokenKind.SymbolRef)
        {
            string modName = _lexer.GetText(Advance()).TrimStart('@').Trim('"');
            _moduleMetadata["mlir.module_name"] = modName;
        }

        if (Peek().Kind == MlirTokenKind.Identifier && _lexer.GetSpan(Peek()).SequenceEqual("attributes"))
        {
            Advance();
            if (Peek().Kind == MlirTokenKind.LBrace)
            {
                List<NodeAttribute> modAttrs = ParseAttributeDictionary();
                foreach (NodeAttribute attr in modAttrs)
                {
                    _moduleMetadata[$"module.{attr.Name}"] = FormatAttributeSummary(attr.Value);
                }
            }
        }

        if (Peek().Kind == MlirTokenKind.LBrace)
        {
            ParseModuleBody(functions);
        }
    }

    private void ParseModuleBody(List<Subgraph> functions)
    {
        Advance(); // {
        while (!IsAtEnd() && Peek().Kind != MlirTokenKind.RBrace)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            MlirToken tok = Peek();

            if (IsModuleHeader(tok))
            {
                ParseModuleContainer(functions);
                continue;
            }

            if (IsFunctionHeader(tok))
            {
                functions.Add(ParseFunction());
                continue;
            }

            if (tok.Kind == MlirTokenKind.ResourceBlock)
            {
                Advance();
                continue;
            }

            if (tok.Kind is MlirTokenKind.AttrAlias or MlirTokenKind.TypeAlias && Peek(1).Kind == MlirTokenKind.Equals)
            {
                SkipTopLevelAliasDefinition();
                continue;
            }

            // Skip unexpected module-level items safely without infinite loop
            Advance();
        }

        if (Peek().Kind == MlirTokenKind.RBrace)
        {
            Advance();
        }
    }

    private bool IsFunctionHeader(MlirToken tok)
    {
        if (tok.Kind != MlirTokenKind.Identifier)
        {
            return false;
        }

        ReadOnlySpan<char> id = _lexer.GetSpan(tok);
        return id.SequenceEqual("func.func")
            || id.SequenceEqual("tt.func")
            || id.SequenceEqual("llvm.func")
            || id.SequenceEqual("util.func");
    }

    private Subgraph ParseFunction()
    {
        MlirToken funcTok = Advance(); // func.func / tt.func / ...

        // Optional visibility modifier: public | private | nested
        if (Peek().Kind == MlirTokenKind.Identifier)
        {
            ReadOnlySpan<char> vis = _lexer.GetSpan(Peek());
            if (vis.SequenceEqual("public") || vis.SequenceEqual("private") || vis.SequenceEqual("nested"))
            {
                Advance();
            }
        }

        string funcName = "func";
        if (Peek().Kind == MlirTokenKind.SymbolRef)
        {
            funcName = _lexer.GetText(Advance()).TrimStart('@').Trim('"');
        }

        Dictionary<string, TensorEdge> tensors = new(StringComparer.Ordinal);
        List<GraphPort> inputs = [];

        // Function parameter list (%arg0: type0, %arg1: type1, ...)
        if (Peek().Kind == MlirTokenKind.LParen)
        {
            ParseArgumentList(inputs, tensors, funcName);
        }

        // Optional return signature -> (...) or -> type
        List<TensorDescriptor> declaredOutputs = [];
        if (Peek().Kind == MlirTokenKind.Arrow)
        {
            Advance();
            if (Peek().Kind == MlirTokenKind.LParen)
            {
                ReadTypeListInParens(declaredOutputs, funcName);
            }
            else
            {
                string retType = ReadSingleTypeExpression();
                declaredOutputs.Add(ResolveTypeDescriptor(retType, funcName));
            }
        }

        // Optional attributes { ... }
        if (Peek().Kind == MlirTokenKind.Identifier && _lexer.GetSpan(Peek()).SequenceEqual("attributes"))
        {
            Advance();
            if (Peek().Kind == MlirTokenKind.LBrace)
            {
                _ = ParseAttributeDictionary();
            }
        }

        // Optional inline attributes before body
        if (Peek().Kind == MlirTokenKind.LBrace && LooksLikeAttributeDict(PeekIndexAfterOpenBrace()))
        {
            _ = ParseAttributeDictionary();
        }

        List<GraphNode> nodes = [];
        List<GraphPort> outputs = [];

        if (Peek().Kind == MlirTokenKind.LBrace)
        {
            RegionParseState regionState = new(funcName, tensors);
            ParseRegion(regionState, depth: 0, nodes, outputs);
        }

        // Skip optional trailing loc(...)
        SkipOptionalLoc();

        // If no return op populated outputs but signature declared outputs, synthesize placeholder outputs
        if (outputs.Count == 0 && declaredOutputs.Count > 0)
        {
            for (int i = 0; i < declaredOutputs.Count; i++)
            {
                string outName = $"{funcName}_out_{i}";
                TensorEdge outEdge = GetOrCreateTensor(tensors, outName, declaredOutputs[i], TensorKind.GraphOutput);
                outputs.Add(new GraphPort(outName, [outEdge]));
            }
        }

        // Ensure all graph inputs are in the subgraph's tensor catalog
        foreach (GraphPort port in inputs)
        {
            foreach (TensorEdge edge in port.Tensors)
            {
                tensors.TryAdd(edge.Name, edge);
            }
        }

        return new Subgraph(funcName, nodes, [.. tensors.Values], inputs, outputs);
    }

    private void ParseArgumentList(
        List<GraphPort> ports,
        Dictionary<string, TensorEdge> tensors,
        string location)
    {
        Advance(); // (
        int argIndex = 0;
        while (!IsAtEnd() && Peek().Kind != MlirTokenKind.RParen)
        {
            if (Peek().Kind == MlirTokenKind.Comma)
            {
                Advance();
                continue;
            }

            string argName;
            if (Peek().Kind == MlirTokenKind.SsaValue)
            {
                argName = _lexer.GetText(Advance());
            }
            else
            {
                argName = $"%arg{argIndex}";
            }

            TensorDescriptor descriptor = new(DataType.Unknown, null);
            if (Peek().Kind == MlirTokenKind.Colon)
            {
                Advance();
                string typeText = ReadSingleTypeExpression();
                descriptor = ResolveTypeDescriptor(typeText, $"{location}:{argName}");
            }

            // Optional arg attribute dict { tt.divisibility = 16 : i32 }
            if (Peek().Kind == MlirTokenKind.LBrace)
            {
                _ = ParseAttributeDictionary();
            }

            SkipOptionalLoc();

            TensorEdge edge = GetOrCreateTensor(tensors, argName, descriptor, TensorKind.GraphInput);
            ports.Add(new GraphPort(argName, [edge]));
            argIndex++;
        }

        if (Peek().Kind == MlirTokenKind.RParen)
        {
            Advance();
        }
    }

    private void ParseRegion(
        RegionParseState state,
        int depth,
        List<GraphNode> nodes,
        List<GraphPort> outputs)
    {
        if (depth > _options.MaxSubgraphDepth)
        {
            throw new ModelParseException(
                $"MLIR nested region depth ({depth}) exceeds MaxSubgraphDepth ({_options.MaxSubgraphDepth}).",
                fileOffset: Peek().ByteOffset,
                context: state.ScopeName);
        }

        if (Peek().Kind != MlirTokenKind.LBrace)
        {
            return;
        }

        Advance(); // {
        while (!IsAtEnd() && Peek().Kind != MlirTokenKind.RBrace)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            MlirToken tok = Peek();

            // Basic block label: ^bb0(%arg0: T0, ...):
            if (tok.Kind == MlirTokenKind.BlockLabel)
            {
                string blockLabel = _lexer.GetText(Advance());
                if (Peek().Kind == MlirTokenKind.LParen)
                {
                    Advance(); // (
                    int blockArgIdx = 0;
                    while (!IsAtEnd() && Peek().Kind != MlirTokenKind.RParen)
                    {
                        if (Peek().Kind == MlirTokenKind.Comma)
                        {
                            Advance();
                            continue;
                        }

                        string argName = Peek().Kind == MlirTokenKind.SsaValue
                            ? _lexer.GetText(Advance())
                            : $"{blockLabel}_arg{blockArgIdx}";

                        TensorDescriptor desc = new(DataType.Unknown, null);
                        if (Peek().Kind == MlirTokenKind.Colon)
                        {
                            Advance();
                            desc = ResolveTypeDescriptor(ReadSingleTypeExpression(), $"{state.ScopeName}:{blockLabel}:{argName}");
                        }

                        GetOrCreateTensor(state.Tensors, argName, desc, TensorKind.Activation);
                        blockArgIdx++;
                    }

                    if (Peek().Kind == MlirTokenKind.RParen)
                    {
                        Advance();
                    }
                }

                if (Peek().Kind == MlirTokenKind.Colon)
                {
                    Advance();
                }

                continue;
            }

            if (!TryParseOperation(state, depth, nodes, outputs))
            {
                Advance();
            }
        }

        if (Peek().Kind == MlirTokenKind.RBrace)
        {
            Advance();
        }
    }

    private bool TryParseOperation(
        RegionParseState state,
        int depth,
        List<GraphNode> nodes,
        List<GraphPort> regionOutputs)
    {
        MlirToken first = Peek();
        if (first.Kind is MlirTokenKind.Eof or MlirTokenKind.RBrace)
        {
            return false;
        }

        // 1. Optional LHS results: %0, %1 = ... or %res:2 = ...
        List<string> resultNames = [];
        int savedPos = _pos;
        if (first.Kind == MlirTokenKind.SsaValue)
        {
            while (Peek().Kind == MlirTokenKind.SsaValue)
            {
                string baseName = _lexer.GetText(Advance());
                if (Peek().Kind == MlirTokenKind.Colon && Peek(1).Kind == MlirTokenKind.Number)
                {
                    Advance(); // :
                    if (int.TryParse(_lexer.GetText(Advance()), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                        && count > 0 && count <= 1024)
                    {
                        for (int r = 0; r < count; r++)
                        {
                            resultNames.Add($"{baseName}#{r}");
                        }
                    }
                    else
                    {
                        resultNames.Add(baseName);
                    }
                }
                else
                {
                    resultNames.Add(baseName);
                }

                if (Peek().Kind == MlirTokenKind.Comma)
                {
                    Advance();
                    continue;
                }

                break;
            }

            if (Peek().Kind == MlirTokenKind.Equals)
            {
                Advance();
            }
            else
            {
                _pos = savedPos;
                resultNames.Clear();
            }
        }

        // 2. Operation mnemonic
        MlirToken opTok = Peek();
        string opName;
        if (opTok.Kind == MlirTokenKind.StringLiteral)
        {
            opName = _lexer.GetUnescapedString(Advance());
        }
        else if (opTok.Kind == MlirTokenKind.Identifier)
        {
            opName = _lexer.GetText(Advance());
        }
        else
        {
            return false;
        }

        // 3. Collect operands, successor labels, properties/attributes, nested regions, and trailing types
        List<string> operandNames = [];
        List<string> successorLabels = [];
        List<NodeAttribute> attributes = [];
        List<Subgraph> childSubgraphs = [];
        List<TensorDescriptor> inputTypes = [];
        List<TensorDescriptor> outputTypes = [];
        TensorDescriptor? singleSharedType = null;
        string? denseResourceKey = null;

        int opLine = opTok.Line;
        bool parsedTrailingType = false;

        while (!IsAtEnd())
        {
            MlirToken cur = Peek();
            if (cur.Kind == MlirTokenKind.RBrace)
            {
                break;
            }

            // Stop when a new statement starts on a subsequent line
            if (cur.Line > opLine && IsStatementBoundaryStart(cur))
            {
                break;
            }

            // loc(...) ends an op
            if (cur.Kind == MlirTokenKind.Identifier && _lexer.GetSpan(cur).SequenceEqual("loc"))
            {
                SkipOptionalLoc();
                break;
            }

            // Generic or custom operand list (...)
            if (cur.Kind == MlirTokenKind.LParen)
            {
                if (IsGenericRegionParenList())
                {
                    ParseGenericRegionList(state, depth, childSubgraphs, attributes, opName);
                    opLine = PreviousLine();
                    continue;
                }

                ParseParenOperandsOrInlineTypedList(operandNames, inputTypes, ref denseResourceKey, attributes, opName);
                opLine = PreviousLine();
                continue;
            }

            // Properties <{ ... }>
            if (cur.Kind == MlirTokenKind.LAngle && Peek(1).Kind == MlirTokenKind.LBrace)
            {
                Advance(); // <
                List<NodeAttribute> props = ParseAttributeDictionary(ref denseResourceKey);
                attributes.AddRange(props);
                if (Peek().Kind == MlirTokenKind.RAngle)
                {
                    Advance();
                }

                opLine = PreviousLine();
                continue;
            }

            // Block { ... } -> attribute dictionary OR nested region
            if (cur.Kind == MlirTokenKind.LBrace)
            {
                if (LooksLikeAttributeDict(PeekIndexAfterOpenBrace()))
                {
                    List<NodeAttribute> dict = ParseAttributeDictionary(ref denseResourceKey);
                    attributes.AddRange(dict);
                }
                else
                {
                    Subgraph child = ParseNestedRegionAsSubgraph(state, depth, opName, childSubgraphs.Count);
                    childSubgraphs.Add(child);
                }

                opLine = PreviousLine();
                continue;
            }

            // Trailing type signature: `:` ...
            if (cur.Kind == MlirTokenKind.Colon)
            {
                Advance();
                ParseTrailingTypeSignature(inputTypes, outputTypes, ref singleSharedType, opName);
                parsedTrailingType = true;
                opLine = PreviousLine();
                continue;
            }

            // Explicit `->` result signature (e.g. scf.if %c -> (tensor<...>) { ... } or linalg.generic ... -> tensor<...>)
            if (cur.Kind == MlirTokenKind.Arrow)
            {
                Advance();
                if (Peek().Kind == MlirTokenKind.LParen)
                {
                    ReadTypeListInParens(outputTypes, opName);
                }
                else
                {
                    outputTypes.Add(ResolveTypeDescriptor(ReadSingleTypeExpression(), opName));
                }

                opLine = PreviousLine();
                continue;
            }

            // SSA operand
            if (cur.Kind == MlirTokenKind.SsaValue)
            {
                string ssa = _lexer.GetText(Advance());
                operandNames.Add(ssa);
                opLine = cur.Line;
                continue;
            }

            // Block successor label ^bb1 or ^bb1(%val : T)
            if (cur.Kind == MlirTokenKind.BlockLabel)
            {
                string label = _lexer.GetText(Advance());
                successorLabels.Add(label);
                if (Peek().Kind == MlirTokenKind.LParen)
                {
                    ParseParenOperandsOrInlineTypedList(operandNames, inputTypes, ref denseResourceKey, attributes, opName);
                }

                opLine = PreviousLine();
                continue;
            }

            // Symbol reference @foo
            if (cur.Kind == MlirTokenKind.SymbolRef)
            {
                string sym = _lexer.GetText(Advance());
                attributes.Add(new NodeAttribute("callee", new AttributeValue.String(sym)));
                opLine = cur.Line;
                continue;
            }

            if (cur.Kind == MlirTokenKind.Identifier)
            {
                string ident = _lexer.GetText(cur);

                if (ident == "dense")
                {
                    Advance();
                    string denseVal = ReadDenseOrResourceBody();
                    attributes.Add(new NodeAttribute("value", new AttributeValue.String($"dense{denseVal}")));
                    opLine = PreviousLine();
                    continue;
                }

                if (ident == "dense_resource")
                {
                    Advance();
                    string resKey = ReadDenseResourceKey();
                    denseResourceKey = resKey;
                    attributes.Add(new NodeAttribute("value_resource", new AttributeValue.String(resKey)));
                    opLine = PreviousLine();
                    continue;
                }

                if (ident is "ins" or "outs" or "iter_args")
                {
                    Advance();
                    if (Peek().Kind == MlirTokenKind.LParen)
                    {
                        ParseParenOperandsOrInlineTypedList(operandNames, inputTypes, ref denseResourceKey, attributes, opName);
                    }

                    opLine = PreviousLine();
                    continue;
                }

                if (ident is "attributes")
                {
                    Advance();
                    if (Peek().Kind == MlirTokenKind.LBrace)
                    {
                        attributes.AddRange(ParseAttributeDictionary(ref denseResourceKey));
                    }

                    opLine = PreviousLine();
                    continue;
                }

                if (ident is "else" or "cond" or "do")
                {
                    Advance();
                    if (Peek().Kind == MlirTokenKind.LBrace)
                    {
                        Subgraph child = ParseNestedRegionAsSubgraph(state, depth, $"{opName}.{ident}", childSubgraphs.Count);
                        childSubgraphs.Add(child);
                    }

                    opLine = PreviousLine();
                    continue;
                }

                if (ident is "true" or "false")
                {
                    Advance();
                    attributes.Add(new NodeAttribute("value", new AttributeValue.Bool(ident == "true")));
                    opLine = cur.Line;
                    continue;
                }

                // Consume remaining custom-syntax identifiers/keywords on the same logical op
                Advance();
                opLine = cur.Line;
                continue;
            }

            if (cur.Kind == MlirTokenKind.Number)
            {
                string numText = _lexer.GetText(Advance());
                if (!parsedTrailingType)
                {
                    if (long.TryParse(numText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long iv))
                    {
                        attributes.Add(new NodeAttribute("scalar_value", new AttributeValue.Int64(iv)));
                    }
                    else if (double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
                    {
                        attributes.Add(new NodeAttribute("scalar_value", new AttributeValue.Float64(dv)));
                    }
                }

                opLine = cur.Line;
                continue;
            }

            if (cur.Kind == MlirTokenKind.StringLiteral)
            {
                string strVal = _lexer.GetUnescapedString(Advance());
                attributes.Add(new NodeAttribute("literal", new AttributeValue.String(strVal)));
                opLine = cur.Line;
                continue;
            }

            if (cur.Kind == MlirTokenKind.LBracket)
            {
                SkipBalanced(MlirTokenKind.LBracket, MlirTokenKind.RBracket);
                opLine = PreviousLine();
                continue;
            }

            Advance();
        }

        // Attach child subgraphs (matching ONNX If/Loop conventions)
        if (childSubgraphs.Count == 1)
        {
            attributes.Add(new NodeAttribute("body", new AttributeValue.Graph(childSubgraphs[0])));
        }
        else if (childSubgraphs.Count == 2 && opName is "scf.if" or "stablehlo.if")
        {
            attributes.Add(new NodeAttribute("then_branch", new AttributeValue.Graph(childSubgraphs[0])));
            attributes.Add(new NodeAttribute("else_branch", new AttributeValue.Graph(childSubgraphs[1])));
        }
        else if (childSubgraphs.Count > 1)
        {
            attributes.Add(new NodeAttribute("regions", new AttributeValue.GraphList(childSubgraphs)));
        }

        if (successorLabels.Count > 0)
        {
            attributes.Add(new NodeAttribute("successors", new AttributeValue.StringList(successorLabels)));
        }

        // Handle return / yield terminators by wiring region outputs without adding redundant terminator nodes
        if (MlirOperatorCatalog.IsReturnOrYieldOp(opName))
        {
            for (int i = 0; i < operandNames.Count; i++)
            {
                string ssaName = operandNames[i];
                TensorDescriptor desc = i < inputTypes.Count
                    ? inputTypes[i]
                    : (singleSharedType ?? new TensorDescriptor(DataType.Unknown, null));

                TensorEdge existing = state.Tensors.TryGetValue(ssaName, out TensorEdge? found)
                    ? found with { Kind = TensorKind.GraphOutput, Descriptor = MergeDescriptor(found.Descriptor, desc) }
                    : new TensorEdge(ssaName, desc, TensorKind.GraphOutput);

                state.Tensors[ssaName] = existing;
                UpdateExistingProducerOutputEdge(nodes, ssaName, existing);
                regionOutputs.Add(new GraphPort(ssaName, [existing]));
            }

            return true;
        }

        // Build GraphNode
        OperatorInfo opInfo = MlirOperatorCatalog.CreateOperatorInfo(opName);
        string nodeName = GenerateUniqueNodeName(opName);
        bool isConstant = MlirOperatorCatalog.IsConstantOp(opName);

        // Input ports
        List<NodePort> inputPorts = new(operandNames.Count);
        for (int i = 0; i < operandNames.Count; i++)
        {
            string ssa = operandNames[i];
            TensorDescriptor inDesc = i < inputTypes.Count
                ? inputTypes[i]
                : (singleSharedType ?? new TensorDescriptor(DataType.Unknown, null));

            TensorEdge edge = state.Tensors.TryGetValue(ssa, out TensorEdge? existingEdge)
                ? existingEdge
                : GetOrCreateTensor(state.Tensors, ssa, inDesc, TensorKind.Activation);

            inputPorts.Add(new NodePort($"in_{i}", [edge]));
        }

        // Output ports
        if (resultNames.Count == 0 && outputTypes.Count > 0)
        {
            for (int i = 0; i < outputTypes.Count; i++)
            {
                resultNames.Add($"{nodeName}:out_{i}");
            }
        }

        List<NodePort> outputPorts = new(resultNames.Count);
        for (int i = 0; i < resultNames.Count; i++)
        {
            string resName = resultNames[i];
            TensorDescriptor outDesc = i < outputTypes.Count
                ? outputTypes[i]
                : (singleSharedType ?? (outputTypes.Count == 1 ? outputTypes[0] : new TensorDescriptor(DataType.Unknown, null)));

            // Apply single-'?' dynamic dimension inference when dense_resource payload size is known
            if (denseResourceKey is not null && _resources.TryGetValue(denseResourceKey, out ResourceEntryInfo resInfo))
            {
                if (outDesc.Shape is not null)
                {
                    int bitWidth = MlirShapeHelper.GetElementBitWidth(outDesc.DataType);
                    if (MlirShapeHelper.TryInferSingleDynamicDimension(outDesc.Shape, resInfo.ByteLength, bitWidth, out TensorShape inferred))
                    {
                        outDesc = outDesc with { Shape = inferred };
                    }
                }

                attributes.Add(new NodeAttribute("resource_byte_length", new AttributeValue.Int64(resInfo.ByteLength)));
            }

            TensorKind kind = isConstant ? TensorKind.Constant : TensorKind.Activation;
            TensorEdge outEdge = new(resName, outDesc, kind);
            state.Tensors[resName] = outEdge;
            outputPorts.Add(new NodePort(resultNames.Count == 1 ? "out" : $"out_{i}", [outEdge]));
        }

        nodes.Add(new GraphNode(nodeName, opInfo, inputPorts, outputPorts, attributes));
        return true;
    }

    private Subgraph ParseNestedRegionAsSubgraph(RegionParseState parentState, int depth, string opName, int regionIndex)
    {
        Dictionary<string, TensorEdge> childTensors = new(StringComparer.Ordinal);
        // Seed child scope with parent's SSA values so captured outer SSA inputs resolve seamlessly
        foreach (KeyValuePair<string, TensorEdge> kvp in parentState.Tensors)
        {
            childTensors[kvp.Key] = kvp.Value;
        }

        string regionName = $"{opName}_region_{regionIndex}";
        RegionParseState childState = new(regionName, childTensors);
        List<GraphNode> childNodes = [];
        List<GraphPort> childOutputs = [];
        ParseRegion(childState, depth + 1, childNodes, childOutputs);

        return new Subgraph(regionName, childNodes, [.. childTensors.Values], [], childOutputs);
    }

    private bool IsGenericRegionParenList()
    {
        if (Peek().Kind != MlirTokenKind.LParen)
        {
            return false;
        }

        return Peek(1).Kind == MlirTokenKind.LBrace;
    }

    private void ParseGenericRegionList(
        RegionParseState state,
        int depth,
        List<Subgraph> childSubgraphs,
        List<NodeAttribute> attributes,
        string opName)
    {
        Advance(); // (
        while (!IsAtEnd() && Peek().Kind != MlirTokenKind.RParen)
        {
            if (Peek().Kind == MlirTokenKind.Comma)
            {
                Advance();
                continue;
            }

            if (Peek().Kind == MlirTokenKind.LBrace)
            {
                if (LooksLikeAttributeDict(PeekIndexAfterOpenBrace()))
                {
                    attributes.AddRange(ParseAttributeDictionary());
                }
                else
                {
                    childSubgraphs.Add(ParseNestedRegionAsSubgraph(state, depth, opName, childSubgraphs.Count));
                }

                continue;
            }

            Advance();
        }

        if (Peek().Kind == MlirTokenKind.RParen)
        {
            Advance();
        }
    }

    private void ParseParenOperandsOrInlineTypedList(
        List<string> operandNames,
        List<TensorDescriptor> inputTypes,
        ref string? denseResourceKey,
        List<NodeAttribute> attributes,
        string opName)
    {
        Advance(); // (
        while (!IsAtEnd() && Peek().Kind != MlirTokenKind.RParen)
        {
            if (Peek().Kind == MlirTokenKind.Comma)
            {
                Advance();
                continue;
            }

            if (Peek().Kind == MlirTokenKind.SsaValue)
            {
                string ssa = _lexer.GetText(Advance());
                if (Peek().Kind == MlirTokenKind.Equals && Peek(1).Kind == MlirTokenKind.SsaValue)
                {
                    // iter_args(%acc = %init) or while(%c = %init)
                    Advance(); // =
                    string initSsa = _lexer.GetText(Advance());
                    operandNames.Add(initSsa);
                }
                else
                {
                    operandNames.Add(ssa);
                }

                if (Peek().Kind == MlirTokenKind.Colon)
                {
                    Advance();
                    string t = ReadSingleTypeExpression();
                    inputTypes.Add(ResolveTypeDescriptor(t, opName));
                }

                continue;
            }

            if (Peek().Kind == MlirTokenKind.Identifier && _lexer.GetSpan(Peek()).SequenceEqual("dense"))
            {
                Advance();
                string denseVal = ReadDenseOrResourceBody();
                attributes.Add(new NodeAttribute("value", new AttributeValue.String($"dense{denseVal}")));
                if (Peek().Kind == MlirTokenKind.Colon)
                {
                    Advance();
                    string t = ReadSingleTypeExpression();
                    inputTypes.Add(ResolveTypeDescriptor(t, opName));
                }

                continue;
            }

            if (Peek().Kind == MlirTokenKind.Identifier && _lexer.GetSpan(Peek()).SequenceEqual("dense_resource"))
            {
                Advance();
                string resKey = ReadDenseResourceKey();
                denseResourceKey = resKey;
                attributes.Add(new NodeAttribute("value_resource", new AttributeValue.String(resKey)));
                if (Peek().Kind == MlirTokenKind.Colon)
                {
                    Advance();
                    string t = ReadSingleTypeExpression();
                    inputTypes.Add(ResolveTypeDescriptor(t, opName));
                }

                continue;
            }

            if (Peek().Kind == MlirTokenKind.Colon)
            {
                Advance();
                while (!IsAtEnd() && Peek().Kind != MlirTokenKind.RParen)
                {
                    if (Peek().Kind == MlirTokenKind.Comma)
                    {
                        Advance();
                        continue;
                    }

                    string t = ReadSingleTypeExpression();
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        inputTypes.Add(ResolveTypeDescriptor(t, opName));
                    }
                    else
                    {
                        break;
                    }
                }

                continue;
            }

            Advance();
        }

        if (Peek().Kind == MlirTokenKind.RParen)
        {
            Advance();
        }
    }

    private void ParseTrailingTypeSignature(
        List<TensorDescriptor> inputTypes,
        List<TensorDescriptor> outputTypes,
        ref TensorDescriptor? singleSharedType,
        string opName)
    {
        if (Peek().Kind == MlirTokenKind.LParen)
        {
            List<TensorDescriptor> leftTypes = [];
            ReadTypeListInParens(leftTypes, opName);
            if (Peek().Kind == MlirTokenKind.Arrow)
            {
                Advance();
                inputTypes.AddRange(leftTypes);
                if (Peek().Kind == MlirTokenKind.LParen)
                {
                    ReadTypeListInParens(outputTypes, opName);
                }
                else
                {
                    outputTypes.Add(ResolveTypeDescriptor(ReadSingleTypeExpression(), opName));
                }
            }
            else
            {
                outputTypes.AddRange(leftTypes);
            }

            return;
        }

        // Comma- or star-separated types possibly followed by `->`
        List<TensorDescriptor> parsed = [];
        while (!IsAtEnd())
        {
            if (Peek().Kind is MlirTokenKind.Arrow or MlirTokenKind.RBrace)
            {
                break;
            }

            if (Peek().Kind == MlirTokenKind.Identifier && _lexer.GetSpan(Peek()).SequenceEqual("loc"))
            {
                break;
            }

            string typeStr = ReadSingleTypeExpression();
            if (string.IsNullOrWhiteSpace(typeStr))
            {
                break;
            }

            parsed.Add(ResolveTypeDescriptor(typeStr, opName));

            if (Peek().Kind is MlirTokenKind.Comma or MlirTokenKind.Star)
            {
                Advance();
                continue;
            }

            break;
        }

        if (Peek().Kind == MlirTokenKind.Arrow)
        {
            Advance();
            inputTypes.AddRange(parsed);
            if (Peek().Kind == MlirTokenKind.LParen)
            {
                ReadTypeListInParens(outputTypes, opName);
            }
            else
            {
                while (!IsAtEnd())
                {
                    string outStr = ReadSingleTypeExpression();
                    if (string.IsNullOrWhiteSpace(outStr))
                    {
                        break;
                    }

                    outputTypes.Add(ResolveTypeDescriptor(outStr, opName));
                    if (Peek().Kind == MlirTokenKind.Comma)
                    {
                        Advance();
                        continue;
                    }

                    break;
                }
            }
        }
        else if (parsed.Count == 1)
        {
            singleSharedType = parsed[0];
            if (inputTypes.Count == 0)
            {
                inputTypes.Add(parsed[0]);
            }

            if (outputTypes.Count == 0)
            {
                outputTypes.Add(parsed[0]);
            }
        }
        else if (parsed.Count > 1)
        {
            inputTypes.AddRange(parsed);
        }
    }

    private void ReadTypeListInParens(List<TensorDescriptor> target, string location)
    {
        Advance(); // (
        while (!IsAtEnd() && Peek().Kind != MlirTokenKind.RParen)
        {
            if (Peek().Kind == MlirTokenKind.Comma)
            {
                Advance();
                continue;
            }

            string typeStr = ReadSingleTypeExpression();
            if (string.IsNullOrWhiteSpace(typeStr))
            {
                Advance();
                continue;
            }

            target.Add(ResolveTypeDescriptor(typeStr, location));
        }

        if (Peek().Kind == MlirTokenKind.RParen)
        {
            Advance();
        }
    }

    private string ReadSingleTypeExpression()
    {
        if (IsAtEnd())
        {
            return string.Empty;
        }

        MlirToken start = Peek();
        if (start.Kind is MlirTokenKind.RParen or MlirTokenKind.RBrace or MlirTokenKind.Comma
            or MlirTokenKind.Arrow or MlirTokenKind.Eof)
        {
            return string.Empty;
        }

        int startChar = start.StartChar;
        Advance();

        // Check for `<...>` suffix on tensor<...>, memref<...>, !torch.vtensor<...>, !quant.uniform<...>, !tt.ptr<...>
        if (Peek().Kind == MlirTokenKind.LAngle)
        {
            SkipBalanced(MlirTokenKind.LAngle, MlirTokenKind.RAngle);
        }

        int endChar = _tokens[_pos - 1].StartChar + _tokens[_pos - 1].Length;
        return _lexer.SliceChars(startChar, endChar).Trim();
    }

    private TensorDescriptor ResolveTypeDescriptor(string rawTypeText, string location)
    {
        string expanded = ExpandTypeAliases(rawTypeText);
        return MlirShapeHelper.ParseTensorTypeString(expanded, _diagnostics, location);
    }

    private string ExpandTypeAliases(string rawTypeText)
    {
        if (_typeAliases.Count == 0 || !rawTypeText.Contains('!', StringComparison.Ordinal))
        {
            return rawTypeText;
        }

        string current = rawTypeText;
        foreach (KeyValuePair<string, string> kvp in _typeAliases)
        {
            if (current.Contains(kvp.Key, StringComparison.Ordinal))
            {
                current = current.Replace(kvp.Key, kvp.Value, StringComparison.Ordinal);
            }
        }

        return current;
    }

    private List<NodeAttribute> ParseAttributeDictionary()
    {
        string? unused = null;
        return ParseAttributeDictionary(ref unused);
    }

    private List<NodeAttribute> ParseAttributeDictionary(ref string? denseResourceKey)
    {
        List<NodeAttribute> result = [];
        if (Peek().Kind != MlirTokenKind.LBrace)
        {
            return result;
        }

        Advance(); // {
        while (!IsAtEnd() && Peek().Kind != MlirTokenKind.RBrace)
        {
            if (Peek().Kind == MlirTokenKind.Comma)
            {
                Advance();
                continue;
            }

            string key;
            MlirToken keyTok = Peek();
            if (keyTok.Kind == MlirTokenKind.Identifier)
            {
                key = _lexer.GetText(Advance());
            }
            else if (keyTok.Kind == MlirTokenKind.StringLiteral)
            {
                key = _lexer.GetUnescapedString(Advance());
            }
            else
            {
                Advance();
                continue;
            }

            if (Peek().Kind != MlirTokenKind.Equals)
            {
                // Unit attribute
                result.Add(new NodeAttribute(key, new AttributeValue.Bool(true)));
                continue;
            }

            Advance(); // =
            AttributeValue attrVal = ParseAttributeValue(ref denseResourceKey);
            result.Add(new NodeAttribute(key, attrVal));
        }

        if (Peek().Kind == MlirTokenKind.RBrace)
        {
            Advance();
        }

        return result;
    }

    private AttributeValue ParseAttributeValue(ref string? denseResourceKey)
    {
        MlirToken tok = Peek();
        if (tok.Kind == MlirTokenKind.StringLiteral)
        {
            string s = _lexer.GetUnescapedString(Advance());
            SkipOptionalAttributeTypeAnnotation();
            return new AttributeValue.String(s);
        }

        if (tok.Kind == MlirTokenKind.Number)
        {
            string n = _lexer.GetText(Advance());
            SkipOptionalAttributeTypeAnnotation();
            if (long.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out long iv))
            {
                return new AttributeValue.Int64(iv);
            }

            if (double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
            {
                return new AttributeValue.Float64(dv);
            }

            return new AttributeValue.String(n);
        }

        if (tok.Kind == MlirTokenKind.Identifier)
        {
            string id = _lexer.GetText(tok);
            if (id is "true" or "false")
            {
                Advance();
                SkipOptionalAttributeTypeAnnotation();
                return new AttributeValue.Bool(id == "true");
            }

            if (id == "dense_resource")
            {
                Advance();
                string key = ReadDenseResourceKey();
                denseResourceKey = key;
                SkipOptionalAttributeTypeAnnotation();
                return new AttributeValue.String($"dense_resource<{key}>");
            }

            if (id == "dense")
            {
                Advance();
                string body = ReadDenseOrResourceBody();
                SkipOptionalAttributeTypeAnnotation();
                return new AttributeValue.String($"dense{body}");
            }

            if (id == "array")
            {
                Advance();
                return ParseTypedDenseArrayAttribute();
            }
        }

        if (tok.Kind == MlirTokenKind.LBracket)
        {
            return ParseBracketListAttribute();
        }

        // Generic fallback: capture balanced expression up to top-level ',' or '}'
        int startChar = tok.StartChar;
        int depth = 0;
        while (!IsAtEnd())
        {
            MlirToken t = Peek();
            if (depth == 0 && t.Kind is MlirTokenKind.Comma or MlirTokenKind.RBrace or MlirTokenKind.RAngle)
            {
                break;
            }

            if (t.Kind is MlirTokenKind.LParen or MlirTokenKind.LBrace or MlirTokenKind.LBracket or MlirTokenKind.LAngle)
            {
                depth++;
            }
            else if (t.Kind is MlirTokenKind.RParen or MlirTokenKind.RBrace or MlirTokenKind.RBracket or MlirTokenKind.RAngle)
            {
                if (depth > 0)
                {
                    depth--;
                }
            }

            Advance();
        }

        int endChar = _pos > 0 ? _tokens[_pos - 1].StartChar + _tokens[_pos - 1].Length : startChar;
        return new AttributeValue.String(_lexer.SliceChars(startChar, endChar).Trim());
    }

    private AttributeValue ParseTypedDenseArrayAttribute()
    {
        if (Peek().Kind != MlirTokenKind.LAngle)
        {
            return new AttributeValue.String("array");
        }

        int startChar = Peek().StartChar;
        SkipBalanced(MlirTokenKind.LAngle, MlirTokenKind.RAngle);
        int endChar = _tokens[_pos - 1].StartChar + _tokens[_pos - 1].Length;
        string raw = _lexer.SliceChars(startChar, endChar);

        // array<i64: 1, 2>
        int colonIdx = raw.IndexOf(':');
        if (colonIdx > 0 && raw.EndsWith('>'))
        {
            string elemType = raw[1..colonIdx].Trim();
            string valuesPart = raw[(colonIdx + 1)..^1].Trim();
            string[] items = valuesPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (elemType.StartsWith('i') || elemType.StartsWith("si", StringComparison.Ordinal) || elemType.StartsWith("ui", StringComparison.Ordinal))
            {
                List<long> longs = new(items.Length);
                foreach (string item in items)
                {
                    if (long.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                    {
                        longs.Add(v);
                    }
                }

                if (longs.Count == items.Length)
                {
                    return new AttributeValue.Int64List(longs);
                }
            }
            else if (elemType.StartsWith('f') || elemType == "bf16")
            {
                List<double> doubles = new(items.Length);
                foreach (string item in items)
                {
                    if (double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    {
                        doubles.Add(v);
                    }
                }

                if (doubles.Count == items.Length)
                {
                    return new AttributeValue.Float64List(doubles);
                }
            }
        }

        return new AttributeValue.String($"array{raw}");
    }

    private AttributeValue ParseBracketListAttribute()
    {
        int startChar = Peek().StartChar;
        Advance(); // [
        List<long> ints = [];
        List<double> floats = [];
        List<string> strings = [];
        bool allInts = true;
        bool allFloats = true;
        bool allStrings = true;

        while (!IsAtEnd() && Peek().Kind != MlirTokenKind.RBracket)
        {
            if (Peek().Kind == MlirTokenKind.Comma)
            {
                Advance();
                continue;
            }

            MlirToken item = Peek();
            if (item.Kind == MlirTokenKind.Number)
            {
                string s = _lexer.GetText(Advance());
                SkipOptionalAttributeTypeAnnotation();
                allStrings = false;
                if (allInts && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long iv))
                {
                    ints.Add(iv);
                    floats.Add(iv);
                }
                else if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
                {
                    allInts = false;
                    floats.Add(dv);
                }
                else
                {
                    allInts = false;
                    allFloats = false;
                }
            }
            else if (item.Kind == MlirTokenKind.StringLiteral)
            {
                allInts = false;
                allFloats = false;
                strings.Add(_lexer.GetUnescapedString(Advance()));
            }
            else
            {
                allInts = false;
                allFloats = false;
                allStrings = false;
                if (item.Kind is MlirTokenKind.LParen or MlirTokenKind.LBrace or MlirTokenKind.LBracket or MlirTokenKind.LAngle)
                {
                    MlirTokenKind close = item.Kind switch
                    {
                        MlirTokenKind.LParen => MlirTokenKind.RParen,
                        MlirTokenKind.LBrace => MlirTokenKind.RBrace,
                        MlirTokenKind.LBracket => MlirTokenKind.RBracket,
                        _ => MlirTokenKind.RAngle,
                    };
                    SkipBalanced(item.Kind, close);
                }
                else
                {
                    Advance();
                }
            }
        }

        if (Peek().Kind == MlirTokenKind.RBracket)
        {
            Advance();
        }

        int endChar = _tokens[_pos - 1].StartChar + _tokens[_pos - 1].Length;
        if (allInts && ints.Count > 0)
        {
            return new AttributeValue.Int64List(ints);
        }

        if (allFloats && floats.Count > 0)
        {
            return new AttributeValue.Float64List(floats);
        }

        if (allStrings && strings.Count > 0)
        {
            return new AttributeValue.StringList(strings);
        }

        return new AttributeValue.String(_lexer.SliceChars(startChar, endChar).Trim());
    }

    private string ReadDenseOrResourceBody()
    {
        if (Peek().Kind != MlirTokenKind.LAngle)
        {
            return string.Empty;
        }

        int startChar = Peek().StartChar;
        SkipBalanced(MlirTokenKind.LAngle, MlirTokenKind.RAngle);
        int endChar = _tokens[_pos - 1].StartChar + _tokens[_pos - 1].Length;
        return _lexer.SliceChars(startChar, endChar);
    }

    private string ReadDenseResourceKey()
    {
        if (Peek().Kind != MlirTokenKind.LAngle)
        {
            return string.Empty;
        }

        Advance(); // <
        string key = Peek().Kind switch
        {
            MlirTokenKind.Identifier => _lexer.GetText(Advance()),
            MlirTokenKind.StringLiteral => _lexer.GetUnescapedString(Advance()),
            _ => string.Empty,
        };

        while (!IsAtEnd() && Peek().Kind != MlirTokenKind.RAngle)
        {
            Advance();
        }

        if (Peek().Kind == MlirTokenKind.RAngle)
        {
            Advance();
        }

        return key;
    }

    private void SkipOptionalAttributeTypeAnnotation()
    {
        if (Peek().Kind == MlirTokenKind.Colon)
        {
            Advance();
            _ = ReadSingleTypeExpression();
        }
    }

    private void SkipTrailingTypeAndAttributes()
    {
        if (Peek().Kind == MlirTokenKind.LBrace && LooksLikeAttributeDict(PeekIndexAfterOpenBrace()))
        {
            _ = ParseAttributeDictionary();
        }

        if (Peek().Kind == MlirTokenKind.Colon)
        {
            Advance();
            if (Peek().Kind == MlirTokenKind.LParen)
            {
                SkipBalanced(MlirTokenKind.LParen, MlirTokenKind.RParen);
            }

            if (Peek().Kind == MlirTokenKind.Arrow)
            {
                Advance();
                if (Peek().Kind == MlirTokenKind.LParen)
                {
                    SkipBalanced(MlirTokenKind.LParen, MlirTokenKind.RParen);
                }
                else
                {
                    _ = ReadSingleTypeExpression();
                }
            }
        }

        SkipOptionalLoc();
    }

    private void SkipOptionalLoc()
    {
        if (Peek().Kind == MlirTokenKind.Identifier && _lexer.GetSpan(Peek()).SequenceEqual("loc"))
        {
            Advance();
            if (Peek().Kind == MlirTokenKind.LParen)
            {
                SkipBalanced(MlirTokenKind.LParen, MlirTokenKind.RParen);
            }
        }
    }

    private void SkipBalanced(MlirTokenKind open, MlirTokenKind close)
    {
        if (Peek().Kind != open)
        {
            return;
        }

        int depth = 0;
        while (!IsAtEnd())
        {
            MlirToken t = Advance();
            if (t.Kind == open)
            {
                depth++;
            }
            else if (t.Kind == close)
            {
                depth--;
                if (depth <= 0)
                {
                    break;
                }
            }
        }
    }

    private int PeekIndexAfterOpenBrace() => _pos + 1;

    private bool LooksLikeAttributeDict(int firstInnerIndex)
    {
        if (firstInnerIndex >= _tokens.Count)
        {
            return false;
        }

        MlirToken first = _tokens[firstInnerIndex];
        if (first.Kind == MlirTokenKind.RBrace)
        {
            return true;
        }

        if (first.Kind is MlirTokenKind.BlockLabel or MlirTokenKind.SsaValue)
        {
            return false;
        }

        if (first.Kind is MlirTokenKind.Identifier or MlirTokenKind.StringLiteral)
        {
            if (firstInnerIndex + 1 < _tokens.Count && _tokens[firstInnerIndex + 1].Kind == MlirTokenKind.Equals)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsStatementBoundaryStart(MlirToken tok)
    {
        if (tok.Kind is MlirTokenKind.SsaValue or MlirTokenKind.BlockLabel or MlirTokenKind.RBrace or MlirTokenKind.ResourceBlock)
        {
            return true;
        }

        if (tok.Kind == MlirTokenKind.StringLiteral && Peek(1).Kind == MlirTokenKind.LParen)
        {
            return true;
        }

        if (tok.Kind == MlirTokenKind.Identifier)
        {
            ReadOnlySpan<char> span = _lexer.GetSpan(tok);
            if (span.SequenceEqual("else") || span.SequenceEqual("cond") || span.SequenceEqual("do")
                || span.SequenceEqual("outs") || span.SequenceEqual("ins") || span.SequenceEqual("iter_args")
                || span.SequenceEqual("attributes") || span.SequenceEqual("loc"))
            {
                return false;
            }

            return span.Contains('.') || span.SequenceEqual("return");
        }

        return false;
    }

    private string GenerateUniqueNodeName(string opName)
    {
        int id = _anonNodeCounter++;
        return $"{opName}_{id}";
    }

    private static TensorEdge GetOrCreateTensor(
        Dictionary<string, TensorEdge> tensors,
        string name,
        TensorDescriptor descriptor,
        TensorKind kind)
    {
        if (tensors.TryGetValue(name, out TensorEdge? existing))
        {
            return existing;
        }

        TensorEdge created = new(name, descriptor, kind);
        tensors[name] = created;
        return created;
    }

    private static TensorDescriptor MergeDescriptor(TensorDescriptor primary, TensorDescriptor fallback)
    {
        DataType dtype = primary.DataType != DataType.Unknown ? primary.DataType : fallback.DataType;
        TensorShape? shape = primary.Shape ?? fallback.Shape;
        QuantizationInfo? quant = primary.Quantization ?? fallback.Quantization;
        string? layout = primary.Layout ?? fallback.Layout;
        return new TensorDescriptor(dtype, shape, quant, layout);
    }

    private static void UpdateExistingProducerOutputEdge(List<GraphNode> nodes, string edgeName, TensorEdge updated)
    {
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            GraphNode node = nodes[i];
            for (int p = 0; p < node.Outputs.Count; p++)
            {
                NodePort port = node.Outputs[p];
                for (int t = 0; t < port.Tensors.Count; t++)
                {
                    if (string.Equals(port.Tensors[t].Name, edgeName, StringComparison.Ordinal))
                    {
                        TensorEdge[] newEdges = [.. port.Tensors];
                        newEdges[t] = updated;
                        NodePort[] newPorts = [.. node.Outputs];
                        newPorts[p] = port with { Tensors = newEdges };
                        nodes[i] = node with { Outputs = newPorts };
                        return;
                    }
                }
            }
        }
    }

    private static string FormatAttributeSummary(AttributeValue value) => value switch
    {
        AttributeValue.String s => s.Value,
        AttributeValue.Int64 i => i.Value.ToString(CultureInfo.InvariantCulture),
        AttributeValue.Float64 f => f.Value.ToString(CultureInfo.InvariantCulture),
        AttributeValue.Bool b => b.Value ? "true" : "false",
        _ => value.ToString() ?? string.Empty,
    };

    private MlirToken Peek(int offset = 0)
    {
        int idx = _pos + offset;
        return idx < _tokens.Count ? _tokens[idx] : _tokens[^1];
    }

    private MlirToken Advance()
    {
        MlirToken tok = Peek();
        if (_pos < _tokens.Count - 1)
        {
            _pos++;
        }

        return tok;
    }

    private int PreviousLine() =>
        _pos > 0 ? _tokens[_pos - 1].Line : 1;

    private bool IsAtEnd() => Peek().Kind == MlirTokenKind.Eof;

    private sealed class RegionParseState(string scopeName, Dictionary<string, TensorEdge> tensors)
    {
        public string ScopeName { get; } = scopeName;

        public Dictionary<string, TensorEdge> Tensors { get; } = tensors;
    }

    private readonly record struct ResourceEntryInfo(string Key, long ByteLength);

    [GeneratedRegex(@"(?<key>[A-Za-z_][A-Za-z0-9_$.-]*|""[^""]+"")\s*:\s*""(?<hex>0x[0-9A-Fa-f]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceEntryRegex();
}
