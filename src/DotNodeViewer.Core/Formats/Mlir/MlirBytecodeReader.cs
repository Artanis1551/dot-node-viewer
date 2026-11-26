using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Mlir;

/// <summary>
/// Streaming, zero-allocation-payload reader for binary MLIR Bytecode (<c>.mlirbc</c>) files.
/// </summary>
/// <remarks>
/// Implements LLVM MLIR Bytecode header, <c>PrefixVarInt</c>, section walker (<c>kString</c>, <c>kDialect</c>,
/// <c>kAttrType</c>, <c>kAttrTypeOffset</c>, <c>kIR</c>, <c>kResource</c>, <c>kResourceOffset</c>,
/// <c>kDialectVersions</c>, <c>kProperties</c>), and lazy slicing of <c>kResource</c> and
/// <c>DenseIntOrFPElementsAttr</c> blobs into <see cref="TensorDataSlice"/> without allocating weight arrays.
/// </remarks>
internal static class MlirBytecodeReader
{
    public const ulong MaxSupportedBytecodeVersion = 16;
    private const int MaxSections = 4096;
    private const int MaxStrings = 1_000_000;
    private const int MaxTableEntries = 1_000_000;
    private const int MaxProducerBytes = 1024;
    private const int MaxAttrTypeRecursionDepth = 64;

    private const byte SectionString = 0;
    private const byte SectionDialect = 1;
    private const byte SectionAttrType = 2;
    private const byte SectionAttrTypeOffset = 3;
    private const byte SectionIr = 4;
    private const byte SectionResource = 5;
    private const byte SectionResourceOffset = 6;
    private const byte SectionDialectVersions = 7;
    private const byte SectionProperties = 8;

    private const byte OpMaskHasAttrs = 0x01;
    private const byte OpMaskHasResults = 0x02;
    private const byte OpMaskHasOperands = 0x04;
    private const byte OpMaskHasSuccessors = 0x08;
    private const byte OpMaskHasInlineRegions = 0x10;
    private const byte OpMaskHasUseListOrders = 0x20;
    private const byte OpMaskHasProperties = 0x40;

    /// <summary>
    /// Reads an LLVM MLIR <c>PrefixVarInt</c> from a byte span.
    /// </summary>
    public static bool TryReadPrefixVarInt(ReadOnlySpan<byte> data, out ulong value, out int bytesRead)
    {
        value = 0;
        bytesRead = 0;
        if (data.IsEmpty)
        {
            return false;
        }

        byte b0 = data[0];
        if (b0 == 0)
        {
            if (data.Length < 9)
            {
                return false;
            }

            for (int i = 0; i < 8; i++)
            {
                value |= (ulong)data[1 + i] << (8 * i);
            }

            bytesRead = 9;
            return true;
        }

        int extraBytes = BitOperations.TrailingZeroCount((uint)b0);
        if (data.Length < 1 + extraBytes)
        {
            return false;
        }

        ulong high = (ulong)b0 >> (extraBytes + 1);
        ulong low = 0;
        for (int i = 0; i < extraBytes; i++)
        {
            low |= (ulong)data[1 + i] << (8 * i);
        }

        value = high | (low << (7 - extraBytes));
        bytesRead = 1 + extraBytes;
        return true;
    }

    /// <summary>
    /// Parses an MLIR bytecode stream into a <see cref="ModelGraph"/> without copying resource or dense weight blobs.
    /// </summary>
    public static ModelGraph Read(
        RandomAccessSource source,
        ModelReadOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (source.Length < 6)
        {
            throw new ModelParseException("MLIR bytecode stream is too short.", fileOffset: 0, context: options.FileName);
        }

        Span<byte> magicScratch = stackalloc byte[4];
        ReadOnlySpan<byte> magic = source.GetSpan(0, 4, magicScratch);
        if (!MlirFormatDetector.IsBytecodeMagic(magic))
        {
            throw new ModelParseException("Invalid MLIR bytecode magic header (expected 'ML\\xEFR').", fileOffset: 0, context: options.FileName);
        }

        long cursor = 4;
        ulong bytecodeVersion = ReadPrefixVarInt(source, ref cursor, source.Length, "bytecode version");
        if (bytecodeVersion > MaxSupportedBytecodeVersion)
        {
            throw new ModelParseException(
                $"Unsupported MLIR bytecode version {bytecodeVersion} (max supported {MaxSupportedBytecodeVersion}).",
                fileOffset: 4,
                context: options.FileName);
        }

        string producer = ReadNullTerminatedString(source, ref cursor, source.Length, MaxProducerBytes);

        SectionSpan? stringSection = null;
        SectionSpan? dialectSection = null;
        SectionSpan? attrTypeSection = null;
        SectionSpan? attrTypeOffsetSection = null;
        SectionSpan? irSection = null;
        SectionSpan? resourceSection = null;
        SectionSpan? resourceOffsetSection = null;

        int sectionsRead = 0;
        while (cursor < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++sectionsRead > MaxSections)
            {
                throw new ModelParseException($"MLIR bytecode section count exceeded maximum ({MaxSections}).", fileOffset: cursor);
            }

            SectionHeader sec = ReadSectionHeader(source, ref cursor, source.Length);
            switch (sec.Id)
            {
                case SectionString:
                    stringSection = sec.Span;
                    break;
                case SectionDialect:
                    dialectSection = sec.Span;
                    break;
                case SectionAttrType:
                    attrTypeSection = sec.Span;
                    break;
                case SectionAttrTypeOffset:
                    attrTypeOffsetSection = sec.Span;
                    break;
                case SectionIr:
                    irSection = sec.Span;
                    break;
                case SectionResource:
                    resourceSection = sec.Span;
                    break;
                case SectionResourceOffset:
                    resourceOffsetSection = sec.Span;
                    break;
                case SectionDialectVersions:
                case SectionProperties:
                    break;
            }

            cursor = sec.Span.End;
        }

        string[] strings = stringSection is { } strSec
            ? ParseStringSection(source, strSec)
            : [];

        (string[] dialects, string[] opNames) = dialectSection is { } dialSec
            ? ParseDialectSection(source, dialSec, strings, bytecodeVersion)
            : ([], []);

        List<ResourceBlobInfo> resources = resourceSection is { } resSec && resourceOffsetSection is { } resOffSec
            ? ParseResourceSections(source, resSec, resOffSec, strings, dialects)
            : [];

        BytecodeContext ctx = new(
            source,
            options,
            bytecodeVersion,
            strings,
            dialects,
            opNames,
            resources,
            cancellationToken);

        if (attrTypeSection is { } atSec && attrTypeOffsetSection is { } atoSec)
        {
            ctx.InitializeAttrTypeIndex(atSec, atoSec);
        }

        List<Subgraph> subgraphs = [];
        if (irSection is { } irSec)
        {
            ctx.ParseTopLevelIrSection(irSec, subgraphs);
        }

        if (subgraphs.Count == 0)
        {
            subgraphs.Add(new Subgraph("main", [], [], [], []));
        }

        // Prefer @main or @forward at Subgraphs[0]
        int primaryIdx = -1;
        for (int i = 0; i < subgraphs.Count; i++)
        {
            if (string.Equals(subgraphs[i].Name, "main", StringComparison.Ordinal) ||
                string.Equals(subgraphs[i].Name, "forward", StringComparison.Ordinal))
            {
                primaryIdx = i;
                break;
            }
        }

        if (primaryIdx > 0)
        {
            Subgraph primary = subgraphs[primaryIdx];
            subgraphs.RemoveAt(primaryIdx);
            subgraphs.Insert(0, primary);
        }

        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["mlir.bytecode_version"] = bytecodeVersion.ToString(CultureInfo.InvariantCulture),
        };
        if (dialects.Length > 0)
        {
            metadata["mlir.dialects"] = string.Join(", ", dialects);
        }

        if (resources.Count > 0)
        {
            metadata["mlir.resource_count"] = resources.Count.ToString(CultureInfo.InvariantCulture);
        }

        return new ModelGraph(
            new ModelFormatInfo(ModelFormatId.Mlir, "MLIR", $"Bytecode v{bytecodeVersion}"),
            string.IsNullOrWhiteSpace(producer) ? null : producer,
            subgraphs,
            metadata,
            ctx.Diagnostics);
    }

    private static SectionHeader ReadSectionHeader(RandomAccessSource source, ref long cursor, long limit)
    {
        if (cursor >= limit)
        {
            throw new ModelParseException("Unexpected end of MLIR bytecode while reading section header.", fileOffset: cursor);
        }

        byte idAndAlign = source.ReadByte(cursor++);
        byte id = (byte)(idAndAlign & 0x7F);
        bool hasAlignment = (idAndAlign & 0x80) != 0;

        ulong lenUlong = ReadPrefixVarInt(source, ref cursor, limit, $"section {id} length");
        if (hasAlignment)
        {
            ulong align = ReadPrefixVarInt(source, ref cursor, limit, $"section {id} alignment");
            if (align == 0 || (align & (align - 1)) != 0)
            {
                throw new ModelParseException($"Invalid non-power-of-two section alignment {align}.", fileOffset: cursor);
            }

            long rem = cursor % (long)align;
            if (rem != 0)
            {
                long pad = (long)align - rem;
                if (pad > limit - cursor)
                {
                    throw new ModelParseException($"Truncated section alignment padding in section {id}.", fileOffset: cursor);
                }

                cursor += pad;
            }
        }

        if (lenUlong > (ulong)(limit - cursor))
        {
            throw new ModelParseException(
                $"MLIR bytecode section {id} length ({lenUlong}) exceeds remaining stream length ({limit - cursor}).",
                fileOffset: cursor);
        }

        long start = cursor;
        long length = (long)lenUlong;
        return new SectionHeader(id, new SectionSpan(start, start + length));
    }

    private static string[] ParseStringSection(RandomAccessSource source, SectionSpan sec)
    {
        long cursor = sec.Start;
        ulong numStringsUlong = ReadPrefixVarInt(source, ref cursor, sec.End, "string count");
        if (numStringsUlong > MaxStrings)
        {
            throw new ModelParseException($"MLIR bytecode string count ({numStringsUlong}) exceeds limit ({MaxStrings}).", fileOffset: sec.Start);
        }

        int numStrings = (int)numStringsUlong;
        string[] strings = new string[numStrings];
        long dataEnd = sec.End;

        for (int i = numStrings - 1; i >= 0; i--)
        {
            ulong byteLenUlong = ReadPrefixVarInt(source, ref cursor, dataEnd, $"string[{i}] length");
            if (byteLenUlong == 0 || byteLenUlong > (ulong)(dataEnd - cursor) || byteLenUlong > int.MaxValue)
            {
                throw new ModelParseException($"Invalid MLIR bytecode string length ({byteLenUlong}) at index {i}.", fileOffset: cursor);
            }

            int byteLen = (int)byteLenUlong;
            long strStart = dataEnd - byteLen;
            // Strings in kString are null-terminated; exclude trailing NUL byte.
            strings[i] = source.ReadUtf8String(strStart, byteLen - 1);
            dataEnd = strStart;
        }

        return strings;
    }

    private static (string[] Dialects, string[] OpNames) ParseDialectSection(
        RandomAccessSource source,
        SectionSpan sec,
        string[] strings,
        ulong bytecodeVersion)
    {
        long cursor = sec.Start;
        ulong numDialectsUlong = ReadPrefixVarInt(source, ref cursor, sec.End, "dialect count");
        if (numDialectsUlong > MaxTableEntries)
        {
            throw new ModelParseException($"MLIR bytecode dialect count ({numDialectsUlong}) exceeds limit.", fileOffset: cursor);
        }

        int numDialects = (int)numDialectsUlong;
        string[] dialects = new string[numDialects];

        for (int i = 0; i < numDialects; i++)
        {
            ulong raw = ReadPrefixVarInt(source, ref cursor, sec.End, $"dialect[{i}]");
            ulong strIdx = bytecodeVersion == 0 ? raw : (raw >> 1);
            dialects[i] = strIdx < (ulong)strings.Length ? strings[strIdx] : $"dialect_{i}";
        }

        List<string> opNames = [];
        while (cursor < sec.End)
        {
            ulong dialectIdx = ReadPrefixVarInt(source, ref cursor, sec.End, "op dialect index");
            ulong numOpsUlong = ReadPrefixVarInt(source, ref cursor, sec.End, "op count");
            if (numOpsUlong > MaxTableEntries)
            {
                throw new ModelParseException($"MLIR bytecode op-name group count ({numOpsUlong}) exceeds limit.", fileOffset: cursor);
            }

            string dialectName = dialectIdx < (ulong)dialects.Length ? dialects[dialectIdx] : "unknown";
            int numOps = (int)numOpsUlong;
            for (int i = 0; i < numOps; i++)
            {
                ulong rawOp = ReadPrefixVarInt(source, ref cursor, sec.End, $"op[{i}]");
                ulong nameIdx = bytecodeVersion >= 5 ? (rawOp >> 1) : rawOp;
                string shortOp = nameIdx < (ulong)strings.Length ? strings[nameIdx] : $"op_{i}";
                opNames.Add($"{dialectName}.{shortOp}");
            }
        }

        return (dialects, [.. opNames]);
    }

    private static List<ResourceBlobInfo> ParseResourceSections(
        RandomAccessSource source,
        SectionSpan resSec,
        SectionSpan resOffSec,
        string[] strings,
        string[] dialects)
    {
        List<ResourceBlobInfo> blobs = [];
        long offCursor = resOffSec.Start;
        long dataCursor = resSec.Start;

        ulong numGroupsUlong = ReadPrefixVarInt(source, ref offCursor, resOffSec.End, "resource group count");
        if (numGroupsUlong > MaxTableEntries)
        {
            throw new ModelParseException($"MLIR bytecode resource group count ({numGroupsUlong}) exceeds limit.", fileOffset: offCursor);
        }

        for (ulong g = 0; g < numGroupsUlong; g++)
        {
            ulong groupHeader = ReadPrefixVarInt(source, ref offCursor, resOffSec.End, "resource group header");
            bool isDialect = (groupHeader & 1) != 0;
            ulong nameIdx = groupHeader >> 1;
            string groupName = isDialect
                ? (nameIdx < (ulong)dialects.Length ? dialects[nameIdx] : $"dialect_{nameIdx}")
                : (nameIdx < (ulong)strings.Length ? strings[nameIdx] : $"resource_{nameIdx}");

            ulong numEntries = ReadPrefixVarInt(source, ref offCursor, resOffSec.End, "resource entry count");
            if (numEntries > MaxTableEntries)
            {
                throw new ModelParseException($"MLIR bytecode resource entry count ({numEntries}) exceeds limit.", fileOffset: offCursor);
            }

            for (ulong r = 0; r < numEntries; r++)
            {
                ulong keyIdx = ReadPrefixVarInt(source, ref offCursor, resOffSec.End, "resource key");
                ulong entrySizeUlong = ReadPrefixVarInt(source, ref offCursor, resOffSec.End, "resource size");
                if (offCursor >= resOffSec.End)
                {
                    throw new ModelParseException("Truncated MLIR resource offset entry kind.", fileOffset: offCursor);
                }

                byte kind = source.ReadByte(offCursor++);
                string key = keyIdx < (ulong)strings.Length ? strings[keyIdx] : $"res_{blobs.Count}";

                if (entrySizeUlong > (ulong)(resSec.End - dataCursor))
                {
                    throw new ModelParseException(
                        $"MLIR resource entry '{key}' size ({entrySizeUlong}) exceeds kResource section bounds.",
                        fileOffset: dataCursor);
                }

                long entryStart = dataCursor;
                long entryEnd = entryStart + (long)entrySizeUlong;
                long blobOffset = entryStart;
                long blobLength = (long)entrySizeUlong;

                // Kind 3 = Blob: starts with alignment PrefixVarInt + padding, followed by raw bytes.
                if (kind == 3 && entrySizeUlong > 0)
                {
                    long innerCursor = entryStart;
                    ulong align = ReadPrefixVarInt(source, ref innerCursor, entryEnd, $"resource '{key}' alignment");
                    if (align > 0 && (align & (align - 1)) == 0)
                    {
                        long rem = innerCursor % (long)align;
                        if (rem != 0)
                        {
                            long pad = (long)align - rem;
                            if (pad <= entryEnd - innerCursor)
                            {
                                innerCursor += pad;
                            }
                        }
                    }

                    blobOffset = innerCursor;
                    blobLength = Math.Max(0L, entryEnd - innerCursor);
                }

                blobs.Add(new ResourceBlobInfo(groupName, key, blobOffset, blobLength, kind));
                dataCursor = entryEnd;
            }
        }

        return blobs;
    }

    private static ulong ReadPrefixVarInt(RandomAccessSource source, ref long cursor, long limit, string context)
    {
        if (cursor >= limit)
        {
            throw new ModelParseException($"Unexpected end of MLIR bytecode while reading {context}.", fileOffset: cursor, context: context);
        }

        byte b0 = source.ReadByte(cursor++);
        if (b0 == 0)
        {
            if (limit - cursor < 8)
            {
                throw new ModelParseException($"Truncated 9-byte PrefixVarInt while reading {context}.", fileOffset: cursor, context: context);
            }

            ulong full = source.ReadUInt64LittleEndian(cursor);
            cursor += 8;
            return full;
        }

        int extraBytes = BitOperations.TrailingZeroCount((uint)b0);
        if (limit - cursor < extraBytes)
        {
            throw new ModelParseException($"Truncated PrefixVarInt while reading {context}.", fileOffset: cursor, context: context);
        }

        ulong high = (ulong)b0 >> (extraBytes + 1);
        ulong low = 0;
        for (int i = 0; i < extraBytes; i++)
        {
            low |= (ulong)source.ReadByte(cursor++) << (8 * i);
        }

        return high | (low << (7 - extraBytes));
    }

    private static long ReadSignedPrefixVarInt(RandomAccessSource source, ref long cursor, long limit, string context)
    {
        ulong encoded = ReadPrefixVarInt(source, ref cursor, limit, context);
        return unchecked((long)((encoded >> 1) ^ (0UL - (encoded & 1UL))));
    }

    private static string ReadNullTerminatedString(RandomAccessSource source, ref long cursor, long limit, int maxBytes)
    {
        long start = cursor;
        int len = 0;
        while (cursor < limit && len < maxBytes)
        {
            byte b = source.ReadByte(cursor++);
            if (b == 0)
            {
                return len == 0 ? string.Empty : source.ReadUtf8String(start, len);
            }

            len++;
        }

        throw new ModelParseException("Unterminated or oversized producer string in MLIR bytecode header.", fileOffset: start);
    }

    private sealed class BytecodeContext(
        RandomAccessSource source,
        ModelReadOptions options,
        ulong bytecodeVersion,
        string[] strings,
        string[] dialects,
        string[] opNames,
        List<ResourceBlobInfo> resources,
        CancellationToken cancellationToken)
    {
        private readonly List<AttrTypeIndexEntry> _attrEntries = [];
        private readonly List<AttrTypeIndexEntry> _typeEntries = [];
        private readonly Dictionary<int, DecodedAttr> _attrCache = [];
        private readonly Dictionary<int, DecodedType> _typeCache = [];
        private readonly Dictionary<int, long> _resourceHandleToBlobIndex = [];
        private int _nodeCounter;
        private int _nextResourceHandleIndex;

        public List<ParseDiagnostic> Diagnostics { get; } = [];

        public void InitializeAttrTypeIndex(SectionSpan attrTypeSec, SectionSpan offsetSec)
        {
            long offCursor = offsetSec.Start;
            ulong numAttrs = ReadPrefixVarInt(source, ref offCursor, offsetSec.End, "attr count");
            ulong numTypes = ReadPrefixVarInt(source, ref offCursor, offsetSec.End, "type count");
            if (numAttrs > MaxTableEntries || numTypes > MaxTableEntries)
            {
                throw new ModelParseException("MLIR bytecode AttrType table count exceeds maximum.", fileOffset: offCursor);
            }

            long dataCursor = attrTypeSec.Start;
            long targetTotal = (long)(numAttrs + numTypes);
            long parsed = 0;

            while (offCursor < offsetSec.End && parsed < targetTotal)
            {
                ulong dialectIdx = ReadPrefixVarInt(source, ref offCursor, offsetSec.End, "attr/type dialect idx");
                ulong numGroupEntries = ReadPrefixVarInt(source, ref offCursor, offsetSec.End, "attr/type group count");
                string dialectName = dialectIdx < (ulong)dialects.Length ? dialects[dialectIdx] : "unknown";

                for (ulong i = 0; i < numGroupEntries; i++)
                {
                    ulong sizeAndCustom = ReadPrefixVarInt(source, ref offCursor, offsetSec.End, "attr/type entry size");
                    bool isCustom = (sizeAndCustom & 1) != 0;
                    ulong size = sizeAndCustom >> 1;

                    if (size > (ulong)(attrTypeSec.End - dataCursor))
                    {
                        throw new ModelParseException(
                            $"MLIR AttrType entry size ({size}) exceeds kAttrType section bounds.",
                            fileOffset: dataCursor);
                    }

                    AttrTypeIndexEntry entry = new(
                        dialectName,
                        isCustom,
                        dataCursor,
                        dataCursor + (long)size);

                    if ((ulong)_attrEntries.Count < numAttrs)
                    {
                        _attrEntries.Add(entry);
                    }
                    else
                    {
                        _typeEntries.Add(entry);
                    }

                    dataCursor += (long)size;
                    parsed++;
                }
            }
        }

        public void ParseTopLevelIrSection(SectionSpan irSec, List<Subgraph> subgraphs)
        {
            List<TensorEdge> ssaValues = [];
            Dictionary<string, TensorEdge> topTensors = new(StringComparer.Ordinal);
            List<GraphNode> topNodes = [];
            List<GraphPort> topInputs = [];
            List<GraphPort> topOutputs = [];

            ParseRegion(
                irSec.Start,
                irSec.End,
                scopeName: "module",
                depth: 0,
                ssaValues,
                topTensors,
                topNodes,
                topInputs,
                topOutputs,
                moduleFunctions: subgraphs);

            if (subgraphs.Count == 0 && (topNodes.Count > 0 || topInputs.Count > 0 || topOutputs.Count > 0))
            {
                subgraphs.Add(new Subgraph("main", topNodes, [.. topTensors.Values], topInputs, topOutputs));
            }
        }

        private long ParseRegion(
            long start,
            long limit,
            string scopeName,
            int depth,
            List<TensorEdge> parentValues,
            Dictionary<string, TensorEdge> tensors,
            List<GraphNode> nodes,
            List<GraphPort> inputs,
            List<GraphPort> outputs,
            List<Subgraph>? moduleFunctions)
        {
            if (depth > options.MaxSubgraphDepth)
            {
                throw new ModelParseException(
                    $"MLIR bytecode nested region depth ({depth}) exceeds MaxSubgraphDepth ({options.MaxSubgraphDepth}).",
                    fileOffset: start,
                    context: scopeName);
            }

            long cursor = start;
            if (cursor >= limit)
            {
                return cursor;
            }

            ulong numBlocks = ReadPrefixVarInt(source, ref cursor, limit, "region block count");
            if (numBlocks == 0)
            {
                return cursor;
            }

            ulong numRegionValues = ReadPrefixVarInt(source, ref cursor, limit, "region value count");
            List<TensorEdge> regionValues = new(parentValues.Count + (int)Math.Min(numRegionValues, 1024));
            regionValues.AddRange(parentValues);

            for (ulong b = 0; b < numBlocks; b++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong opsAndArgs = ReadPrefixVarInt(source, ref cursor, limit, "block ops/args");
                bool hasArgs = (opsAndArgs & 1) != 0;
                ulong numOps = opsAndArgs >> 1;

                if (hasArgs)
                {
                    ulong numArgs = ReadPrefixVarInt(source, ref cursor, limit, "block arg count");
                    for (ulong a = 0; a < numArgs; a++)
                    {
                        ulong typeIdx = ReadPrefixVarInt(source, ref cursor, limit, "block arg type");
                        _ = ReadPrefixVarInt(source, ref cursor, limit, "block arg loc");

                        TensorDescriptor desc = ResolveType((int)typeIdx, scopeName).Descriptor;
                        string argName = $"%arg{a}";
                        if (b > 0)
                        {
                            argName = $"%bb{b}_arg{a}";
                        }

                        TensorKind kind = b == 0 && depth <= 1 ? TensorKind.GraphInput : TensorKind.Activation;
                        TensorEdge edge = new(argName, desc, kind);
                        regionValues.Add(edge);
                        tensors[argName] = edge;

                        if (kind == TensorKind.GraphInput)
                        {
                            inputs.Add(new GraphPort(argName, [edge]));
                        }
                    }
                }

                for (ulong opIdx = 0; opIdx < numOps; opIdx++)
                {
                    cursor = ParseOperation(
                        cursor,
                        limit,
                        scopeName,
                        depth,
                        regionValues,
                        tensors,
                        nodes,
                        outputs,
                        moduleFunctions);
                }
            }

            return cursor;
        }

        private long ParseOperation(
            long start,
            long limit,
            string scopeName,
            int depth,
            List<TensorEdge> regionValues,
            Dictionary<string, TensorEdge> tensors,
            List<GraphNode> nodes,
            List<GraphPort> outputs,
            List<Subgraph>? moduleFunctions)
        {
            long cursor = start;
            ulong opNameIdx = ReadPrefixVarInt(source, ref cursor, limit, "op name index");
            if (cursor >= limit)
            {
                throw new ModelParseException("Unexpected end of MLIR bytecode while reading op encoding mask.", fileOffset: cursor);
            }

            byte opMask = source.ReadByte(cursor++);
            _ = ReadPrefixVarInt(source, ref cursor, limit, "op location");

            string opName = opNameIdx < (ulong)opNames.Length ? opNames[opNameIdx] : $"unknown.op_{opNameIdx}";

            List<NodeAttribute> attributes = [];
            TensorDataSlice? denseSlice = null;
            TensorDescriptor? denseDescriptor = null;

            if ((opMask & OpMaskHasAttrs) != 0)
            {
                ulong attrDictIdx = ReadPrefixVarInt(source, ref cursor, limit, "op attributes");
                DecodedAttr decoded = ResolveAttribute((int)attrDictIdx, depth: 0);
                if (decoded.Dictionary is not null)
                {
                    foreach (NodeAttribute attr in decoded.Dictionary)
                    {
                        attributes.Add(attr);
                    }
                }

                if (decoded.DenseSlice is not null)
                {
                    denseSlice = decoded.DenseSlice;
                    denseDescriptor = decoded.DenseDescriptor;
                }
                else if (decoded.FirstDenseSlice is not null)
                {
                    denseSlice = decoded.FirstDenseSlice;
                    denseDescriptor = decoded.FirstDenseDescriptor;
                }
            }

            if ((opMask & OpMaskHasProperties) != 0)
            {
                if (bytecodeVersion >= 5)
                {
                    _ = ReadPrefixVarInt(source, ref cursor, limit, "op properties index");
                }
            }

            List<TensorDescriptor> resultDescs = [];
            if ((opMask & OpMaskHasResults) != 0)
            {
                ulong numResults = ReadPrefixVarInt(source, ref cursor, limit, "op result count");
                for (ulong r = 0; r < numResults; r++)
                {
                    ulong resTypeIdx = ReadPrefixVarInt(source, ref cursor, limit, "op result type");
                    resultDescs.Add(ResolveType((int)resTypeIdx, opName).Descriptor);
                }
            }

            List<TensorEdge> operandEdges = [];
            if ((opMask & OpMaskHasOperands) != 0)
            {
                ulong numOperands = ReadPrefixVarInt(source, ref cursor, limit, "op operand count");
                for (ulong i = 0; i < numOperands; i++)
                {
                    ulong valIdx = ReadPrefixVarInt(source, ref cursor, limit, "op operand index");
                    if (valIdx < (ulong)regionValues.Count)
                    {
                        operandEdges.Add(regionValues[(int)valIdx]);
                    }
                    else
                    {
                        operandEdges.Add(new TensorEdge($"%val_{valIdx}", new TensorDescriptor(DataType.Unknown, null), TensorKind.Activation));
                    }
                }
            }

            List<string> successorNames = [];
            if ((opMask & OpMaskHasSuccessors) != 0)
            {
                ulong numSuccessors = ReadPrefixVarInt(source, ref cursor, limit, "op successor count");
                for (ulong s = 0; s < numSuccessors; s++)
                {
                    ulong blockIdx = ReadPrefixVarInt(source, ref cursor, limit, "op successor block");
                    successorNames.Add($"^bb{blockIdx}");
                }

                if (successorNames.Count > 0)
                {
                    attributes.Add(new NodeAttribute("successors", new AttributeValue.StringList(successorNames)));
                }
            }

            if ((opMask & OpMaskHasUseListOrders) != 0)
            {
                // Skip use-list order entries when present
                for (int r = 0; r < resultDescs.Count; r++)
                {
                    ulong count = ReadPrefixVarInt(source, ref cursor, limit, "use-list order count");
                    if (count > 0)
                    {
                        _ = ReadPrefixVarInt(source, ref cursor, limit, "use-list index");
                        for (ulong u = 0; u < count; u++)
                        {
                            _ = ReadPrefixVarInt(source, ref cursor, limit, "use-list item");
                        }
                    }
                }
            }

            List<Subgraph> childRegions = [];
            if ((opMask & OpMaskHasInlineRegions) != 0)
            {
                ulong numRegions = ReadPrefixVarInt(source, ref cursor, limit, "op region count");
                for (ulong regIdx = 0; regIdx < numRegions; regIdx++)
                {
                    if (cursor >= limit)
                    {
                        throw new ModelParseException("Truncated MLIR bytecode region isolation flag.", fileOffset: cursor);
                    }

                    byte isIsolatedFlag = source.ReadByte(cursor++);
                    bool isIsolated = (isIsolatedFlag & 1) != 0;

                    List<TensorEdge> childBaseValues = isIsolated ? [] : regionValues;
                    Dictionary<string, TensorEdge> childTensors = new(StringComparer.Ordinal);
                    if (!isIsolated)
                    {
                        foreach (KeyValuePair<string, TensorEdge> kvp in tensors)
                        {
                            childTensors[kvp.Key] = kvp.Value;
                        }
                    }

                    List<GraphNode> childNodes = [];
                    List<GraphPort> childInputs = [];
                    List<GraphPort> childOutputs = [];
                    string childName = ExtractFunctionSymbolName(attributes) ?? $"{opName}_region_{regIdx}";

                    // Isolated regions may be wrapped in a nested kIR section header
                    if (isIsolated && cursor < limit && (source.ReadByte(cursor) & 0x7F) == SectionIr)
                    {
                        SectionHeader nestedIr = ReadSectionHeader(source, ref cursor, limit);
                        ParseRegion(
                            nestedIr.Span.Start,
                            nestedIr.Span.End,
                            childName,
                            depth + 1,
                            childBaseValues,
                            childTensors,
                            childNodes,
                            childInputs,
                            childOutputs,
                            moduleFunctions: opName == "builtin.module" ? moduleFunctions : null);
                        cursor = nestedIr.Span.End;
                    }
                    else
                    {
                        cursor = ParseRegion(
                            cursor,
                            limit,
                            childName,
                            depth + 1,
                            childBaseValues,
                            childTensors,
                            childNodes,
                            childInputs,
                            childOutputs,
                            moduleFunctions: opName == "builtin.module" ? moduleFunctions : null);
                    }

                    if (opName != "builtin.module")
                    {
                        childRegions.Add(new Subgraph(childName, childNodes, [.. childTensors.Values], childInputs, childOutputs));
                    }
                }
            }

            // Module wrapper & function definitions become top-level Subgraphs
            if (opName == "builtin.module")
            {
                return cursor;
            }

            if (opName is "func.func" or "tt.func" or "llvm.func" or "util.func" && moduleFunctions is not null)
            {
                if (childRegions.Count > 0)
                {
                    moduleFunctions.Add(childRegions[0]);
                }

                return cursor;
            }

            if (MlirOperatorCatalog.IsReturnOrYieldOp(opName))
            {
                for (int i = 0; i < operandEdges.Count; i++)
                {
                    TensorEdge outEdge = operandEdges[i] with { Kind = TensorKind.GraphOutput };
                    tensors[outEdge.Name] = outEdge;
                    UpdateExistingProducerOutputEdge(nodes, outEdge.Name, outEdge);
                    outputs.Add(new GraphPort(outEdge.Name, [outEdge]));
                }

                return cursor;
            }

            if (childRegions.Count == 1)
            {
                attributes.Add(new NodeAttribute("body", new AttributeValue.Graph(childRegions[0])));
            }
            else if (childRegions.Count == 2 && opName is "scf.if" or "stablehlo.if")
            {
                attributes.Add(new NodeAttribute("then_branch", new AttributeValue.Graph(childRegions[0])));
                attributes.Add(new NodeAttribute("else_branch", new AttributeValue.Graph(childRegions[1])));
            }
            else if (childRegions.Count > 1)
            {
                attributes.Add(new NodeAttribute("regions", new AttributeValue.GraphList(childRegions)));
            }

            string nodeName = $"{opName}_{_nodeCounter++}";
            OperatorInfo opInfo = MlirOperatorCatalog.CreateOperatorInfo(opName);
            bool isConstant = MlirOperatorCatalog.IsConstantOp(opName);

            List<NodePort> inputPorts = new(operandEdges.Count);
            for (int i = 0; i < operandEdges.Count; i++)
            {
                inputPorts.Add(new NodePort($"in_{i}", [operandEdges[i]]));
            }

            List<NodePort> outputPorts = new(resultDescs.Count);
            for (int i = 0; i < resultDescs.Count; i++)
            {
                TensorDescriptor outDesc = resultDescs[i];
                TensorDataSource? dataSource = null;

                if (i == 0 && denseSlice is not null)
                {
                    if (denseDescriptor is not null && outDesc.DataType == DataType.Unknown)
                    {
                        outDesc = denseDescriptor;
                    }

                    if (outDesc.Shape is not null)
                    {
                        int bitWidth = MlirShapeHelper.GetElementBitWidth(outDesc.DataType);
                        if (MlirShapeHelper.TryInferSingleDynamicDimension(outDesc.Shape, denseSlice.ByteLength, bitWidth, out TensorShape inferred))
                        {
                            outDesc = outDesc with { Shape = inferred };
                        }
                    }

                    dataSource = denseSlice with { DataType = outDesc.DataType };
                }

                string valName = $"%{regionValues.Count}";
                TensorKind kind = isConstant ? TensorKind.Constant : TensorKind.Activation;
                TensorEdge outEdge = new(valName, outDesc, kind, dataSource);
                regionValues.Add(outEdge);
                tensors[valName] = outEdge;
                outputPorts.Add(new NodePort(resultDescs.Count == 1 ? "out" : $"out_{i}", [outEdge]));
            }

            nodes.Add(new GraphNode(nodeName, opInfo, inputPorts, outputPorts, attributes));
            return cursor;
        }

        private DecodedType ResolveType(int index, string location)
        {
            if (_typeCache.TryGetValue(index, out DecodedType cached))
            {
                return cached;
            }

            if (index < 0 || index >= _typeEntries.Count)
            {
                return new DecodedType(new TensorDescriptor(DataType.Unknown, null), location);
            }

            DecodedType decoded = DecodeTypeEntry(_typeEntries[index], depth: 0, location);
            _typeCache[index] = decoded;
            return decoded;
        }

        private DecodedType DecodeTypeEntry(AttrTypeIndexEntry entry, int depth, string location)
        {
            if (depth > MaxAttrTypeRecursionDepth)
            {
                throw new ModelParseException("MLIR bytecode type nesting exceeded recursion limit.", fileOffset: entry.Start);
            }

            if (!entry.IsCustom)
            {
                int len = (int)Math.Min(entry.End - entry.Start, MaxProducerBytes);
                string asm = source.ReadUtf8String(entry.Start, len).TrimEnd('\0');
                return new DecodedType(MlirShapeHelper.ParseTensorTypeString(asm, Diagnostics, location), asm);
            }

            if (!string.Equals(entry.DialectName, "builtin", StringComparison.Ordinal))
            {
                return new DecodedType(new TensorDescriptor(DataType.Unknown, null), entry.DialectName);
            }

            long cursor = entry.Start;
            ulong code = ReadPrefixVarInt(source, ref cursor, entry.End, "builtin type code");

            switch (code)
            {
                case 0: // IntegerType
                {
                    ulong widthAndSign = ReadPrefixVarInt(source, ref cursor, entry.End, "integer width");
                    uint width = (uint)(widthAndSign >> 2);
                    uint sign = (uint)(widthAndSign & 0x3); // 0=signless, 1=signed, 2=unsigned
                    DataType dt = (width, sign) switch
                    {
                        (1, _) => DataType.Bool,
                        (4, 2) => DataType.UInt4,
                        (4, _) => DataType.Int4,
                        (8, 2) => DataType.UInt8,
                        (8, _) => DataType.Int8,
                        (16, 2) => DataType.UInt16,
                        (16, _) => DataType.Int16,
                        (32, 2) => DataType.UInt32,
                        (32, _) => DataType.Int32,
                        (64, 2) => DataType.UInt64,
                        (64, _) => DataType.Int64,
                        _ => DataType.Unknown,
                    };
                    if (dt == DataType.Unknown)
                    {
                        Diagnostics.Add(new ParseDiagnostic(
                            DiagnosticSeverity.Warning,
                            MlirShapeHelper.UnsupportedDataTypeDiagnosticCode,
                            $"Unsupported MLIR integer width i{width} mapped to DataType.Unknown.",
                            location));
                    }

                    return new DecodedType(new TensorDescriptor(dt, TensorShape.Scalar), $"i{width}");
                }

                case 1: // IndexType
                    return new DecodedType(new TensorDescriptor(DataType.Int64, TensorShape.Scalar), "index");

                case 2: // FunctionType
                    return new DecodedType(new TensorDescriptor(DataType.Unknown, null), "function");

                case 3: // BFloat16Type
                    return new DecodedType(new TensorDescriptor(DataType.BFloat16, TensorShape.Scalar), "bf16");

                case 4: // Float16Type
                    return new DecodedType(new TensorDescriptor(DataType.Float16, TensorShape.Scalar), "f16");

                case 5: // Float32Type
                    return new DecodedType(new TensorDescriptor(DataType.Float32, TensorShape.Scalar), "f32");

                case 6: // Float64Type
                    return new DecodedType(new TensorDescriptor(DataType.Float64, TensorShape.Scalar), "f64");

                case 9: // ComplexType
                {
                    ulong elemIdx = ReadPrefixVarInt(source, ref cursor, entry.End, "complex element type");
                    DecodedType elem = elemIdx < (ulong)_typeEntries.Count
                        ? DecodeTypeEntry(_typeEntries[(int)elemIdx], depth + 1, location)
                        : new DecodedType(new TensorDescriptor(DataType.Unknown, null), "unknown");
                    DataType complexDt = elem.Descriptor.DataType switch
                    {
                        DataType.Float32 => DataType.Complex64,
                        DataType.Float64 => DataType.Complex128,
                        _ => DataType.Unknown,
                    };
                    if (complexDt == DataType.Unknown)
                    {
                        Diagnostics.Add(new ParseDiagnostic(
                            DiagnosticSeverity.Warning,
                            MlirShapeHelper.UnsupportedDataTypeDiagnosticCode,
                            $"Unsupported MLIR element type 'complex<{elem.RawName}>' mapped to DataType.Unknown.",
                            location));
                    }

                    return new DecodedType(new TensorDescriptor(complexDt, TensorShape.Scalar), $"complex<{elem.RawName}>");
                }

                case 12: // NoneType
                    return new DecodedType(new TensorDescriptor(DataType.Unknown, null), "none");

                case 13: // RankedTensorType
                case 14: // RankedTensorTypeWithEncoding
                case 10: // MemRefType
                case 11: // MemRefTypeWithLayout
                {
                    ulong rank = ReadPrefixVarInt(source, ref cursor, entry.End, "tensor rank");
                    if (rank > 64)
                    {
                        throw new ModelParseException($"MLIR bytecode tensor rank ({rank}) exceeds limit.", fileOffset: cursor);
                    }

                    TensorDimension[] dims = new TensorDimension[rank];
                    for (ulong d = 0; d < rank; d++)
                    {
                        long dimVal = ReadSignedPrefixVarInt(source, ref cursor, entry.End, $"dim[{d}]");
                        dims[d] = dimVal < 0 ? TensorDimension.Dynamic.Instance : new TensorDimension.Fixed(dimVal);
                    }

                    ulong elemTypeIdx = ReadPrefixVarInt(source, ref cursor, entry.End, "tensor element type");
                    DecodedType elemType = elemTypeIdx < (ulong)_typeEntries.Count
                        ? DecodeTypeEntry(_typeEntries[(int)elemTypeIdx], depth + 1, location)
                        : new DecodedType(new TensorDescriptor(DataType.Unknown, null), "unknown");

                    TensorShape shape = dims.Length == 0 ? TensorShape.Scalar : new TensorShape(dims);
                    int bitWidth = MlirShapeHelper.GetElementBitWidth(elemType.Descriptor.DataType, elemType.RawName);
                    if (dims.Length > 0 &&
                        !MlirShapeHelper.TryComputeByteLength(dims, bitWidth > 0 ? bitWidth : 8, out _, out bool overflowed) &&
                        overflowed)
                    {
                        Diagnostics.Add(new ParseDiagnostic(
                            DiagnosticSeverity.Warning,
                            MlirShapeHelper.ShapeOverflowDiagnosticCode,
                            $"MLIR bytecode tensor shape {shape.ToDisplayString()} overflows 64-bit/128-bit bounds.",
                            location));
                    }

                    return new DecodedType(
                        new TensorDescriptor(elemType.Descriptor.DataType, shape, elemType.Descriptor.Quantization),
                        $"tensor<{elemType.RawName}>");
                }

                case 18: // UnrankedTensorType
                {
                    ulong elemTypeIdx = ReadPrefixVarInt(source, ref cursor, entry.End, "unranked element type");
                    DecodedType elemType = elemTypeIdx < (ulong)_typeEntries.Count
                        ? DecodeTypeEntry(_typeEntries[(int)elemTypeIdx], depth + 1, location)
                        : new DecodedType(new TensorDescriptor(DataType.Unknown, null), "unknown");
                    return new DecodedType(new TensorDescriptor(elemType.Descriptor.DataType, null), $"tensor<*x{elemType.RawName}>");
                }

                case 21: // Float8E5M2Type
                    return new DecodedType(new TensorDescriptor(DataType.Float8E5M2, TensorShape.Scalar), "f8E5M2");

                case 22: // Float8E4M3FNType
                    return new DecodedType(new TensorDescriptor(DataType.Float8E4M3, TensorShape.Scalar), "f8E4M3FN");

                default:
                    Diagnostics.Add(new ParseDiagnostic(
                        DiagnosticSeverity.Warning,
                        MlirShapeHelper.UnsupportedDataTypeDiagnosticCode,
                        $"Unsupported MLIR builtin bytecode type code {code} mapped to DataType.Unknown.",
                        location));
                    return new DecodedType(new TensorDescriptor(DataType.Unknown, null), $"builtin_type_{code}");
            }
        }

        private DecodedAttr ResolveAttribute(int index, int depth)
        {
            if (depth > MaxAttrTypeRecursionDepth)
            {
                throw new ModelParseException("MLIR bytecode attribute nesting exceeded recursion limit.", fileOffset: 0);
            }

            if (_attrCache.TryGetValue(index, out DecodedAttr? cached))
            {
                return cached;
            }

            if (index < 0 || index >= _attrEntries.Count)
            {
                return new DecodedAttr(new AttributeValue.String($"attr_{index}"), null, null, null, null, null);
            }

            AttrTypeIndexEntry entry = _attrEntries[index];
            DecodedAttr decoded;

            if (!entry.IsCustom)
            {
                int len = (int)Math.Min(entry.End - entry.Start, MaxProducerBytes);
                string asm = source.ReadUtf8String(entry.Start, len).TrimEnd('\0');
                decoded = new DecodedAttr(new AttributeValue.String(asm), null, null, null, null, null);
            }
            else if (string.Equals(entry.DialectName, "builtin", StringComparison.Ordinal))
            {
                decoded = DecodeBuiltinAttribute(entry, depth);
            }
            else
            {
                decoded = new DecodedAttr(new AttributeValue.String($"#{entry.DialectName}"), null, null, null, null, null);
            }

            _attrCache[index] = decoded;
            return decoded;
        }

        private DecodedAttr DecodeBuiltinAttribute(AttrTypeIndexEntry entry, int depth)
        {
            long cursor = entry.Start;
            ulong code = ReadPrefixVarInt(source, ref cursor, entry.End, "builtin attr code");

            switch (code)
            {
                case 0: // ArrayAttr
                {
                    ulong count = ReadPrefixVarInt(source, ref cursor, entry.End, "array attr count");
                    List<string> items = [];
                    List<long> intItems = [];
                    bool allInts = true;
                    for (ulong i = 0; i < count; i++)
                    {
                        ulong elemIdx = ReadPrefixVarInt(source, ref cursor, entry.End, "array attr item");
                        DecodedAttr item = ResolveAttribute((int)elemIdx, depth + 1);
                        if (item.Value is AttributeValue.Int64 intVal)
                        {
                            intItems.Add(intVal.Value);
                        }
                        else
                        {
                            allInts = false;
                        }

                        items.Add(item.Value.ToString() ?? string.Empty);
                    }

                    AttributeValue val = allInts && intItems.Count > 0
                        ? new AttributeValue.Int64List(intItems)
                        : new AttributeValue.StringList(items);
                    return new DecodedAttr(val, null, null, null, null, null);
                }

                case 1: // DictionaryAttr
                {
                    ulong count = ReadPrefixVarInt(source, ref cursor, entry.End, "dict attr count");
                    List<NodeAttribute> dict = new((int)Math.Min(count, 256));
                    TensorDataSlice? firstSlice = null;
                    TensorDescriptor? firstDesc = null;

                    for (ulong i = 0; i < count; i++)
                    {
                        ulong keyStrIdx = ReadPrefixVarInt(source, ref cursor, entry.End, "dict key");
                        ulong valAttrIdx = ReadPrefixVarInt(source, ref cursor, entry.End, "dict value");
                        string key = keyStrIdx < (ulong)strings.Length ? strings[keyStrIdx] : $"key_{i}";
                        DecodedAttr val = ResolveAttribute((int)valAttrIdx, depth + 1);
                        dict.Add(new NodeAttribute(key, val.Value));
                        if (firstSlice is null && val.DenseSlice is not null)
                        {
                            firstSlice = val.DenseSlice;
                            firstDesc = val.DenseDescriptor;
                        }
                    }

                    return new DecodedAttr(new AttributeValue.String("{dict}"), dict, null, null, firstSlice, firstDesc);
                }

                case 2: // StringAttr
                case 3: // StringAttrWithType
                {
                    ulong strIdx = ReadPrefixVarInt(source, ref cursor, entry.End, "string attr");
                    string s = strIdx < (ulong)strings.Length ? strings[strIdx] : string.Empty;
                    return new DecodedAttr(new AttributeValue.String(s), null, null, null, null, null);
                }

                case 4: // FlatSymbolRefAttr
                {
                    ulong strIdx = ReadPrefixVarInt(source, ref cursor, entry.End, "symref");
                    string s = strIdx < (ulong)strings.Length ? strings[strIdx] : string.Empty;
                    return new DecodedAttr(new AttributeValue.String($"@{s}"), null, null, null, null, null);
                }

                case 6: // TypeAttr
                {
                    ulong typeIdx = ReadPrefixVarInt(source, ref cursor, entry.End, "type attr");
                    DecodedType t = ResolveType((int)typeIdx, "type_attr");
                    return new DecodedAttr(new AttributeValue.DataTypeValue(t.Descriptor.DataType), null, null, null, null, null);
                }

                case 7: // UnitAttr
                    return new DecodedAttr(new AttributeValue.Bool(true), null, null, null, null, null);

                case 8: // IntegerAttr
                {
                    _ = ReadPrefixVarInt(source, ref cursor, entry.End, "int attr type");
                    long val = ReadSignedPrefixVarInt(source, ref cursor, entry.End, "int attr value");
                    return new DecodedAttr(new AttributeValue.Int64(val), null, null, null, null, null);
                }

                case 16: // DenseResourceElementsAttr
                {
                    ulong typeIdx = ReadPrefixVarInt(source, ref cursor, entry.End, "dense_resource type");
                    ulong handleIdx = ReadPrefixVarInt(source, ref cursor, entry.End, "dense_resource handle");
                    DecodedType decodedType = ResolveType((int)typeIdx, "dense_resource");

                    int blobIndex = ResolveResourceHandleBlobIndex((int)handleIdx);
                    if (blobIndex >= 0 && blobIndex < resources.Count)
                    {
                        ResourceBlobInfo blob = resources[blobIndex];
                        TensorDescriptor desc = decodedType.Descriptor;
                        if (desc.Shape is not null)
                        {
                            int bitWidth = MlirShapeHelper.GetElementBitWidth(desc.DataType, decodedType.RawName);
                            if (MlirShapeHelper.TryInferSingleDynamicDimension(desc.Shape, blob.ByteLength, bitWidth, out TensorShape inferred))
                            {
                                desc = desc with { Shape = inferred };
                            }
                        }

                        TensorDataSlice slice = new(blob.StreamOffset, blob.ByteLength, desc.DataType);
                        return new DecodedAttr(
                            new AttributeValue.String($"dense_resource<{blob.Key}>"),
                            null,
                            slice,
                            desc,
                            null,
                            null);
                    }

                    return new DecodedAttr(new AttributeValue.String("dense_resource"), null, null, null, null, null);
                }

                case 18: // DenseIntOrFPElementsAttr (Zero-Allocation payload skipping)
                {
                    ulong typeIdx = ReadPrefixVarInt(source, ref cursor, entry.End, "dense_elements type");
                    ulong blobLenUlong = ReadPrefixVarInt(source, ref cursor, entry.End, "dense_elements blob length");
                    DecodedType decodedType = ResolveType((int)typeIdx, "dense_elements");

                    if (blobLenUlong > (ulong)(entry.End - cursor))
                    {
                        throw new ModelParseException(
                            $"DenseIntOrFPElementsAttr blob length ({blobLenUlong}) exceeds attribute bounds.",
                            fileOffset: cursor);
                    }

                    long blobOffset = cursor;
                    long blobLen = (long)blobLenUlong;
                    TensorDescriptor desc = decodedType.Descriptor;
                    if (desc.Shape is not null)
                    {
                        int bitWidth = MlirShapeHelper.GetElementBitWidth(desc.DataType, decodedType.RawName);
                        if (MlirShapeHelper.TryInferSingleDynamicDimension(desc.Shape, blobLen, bitWidth, out TensorShape inferred))
                        {
                            desc = desc with { Shape = inferred };
                        }
                    }

                    TensorDataSlice slice = new(blobOffset, blobLen, desc.DataType);
                    return new DecodedAttr(
                        new AttributeValue.String($"dense<{blobLen}B>"),
                        null,
                        slice,
                        desc,
                        null,
                        null);
                }

                default:
                    return new DecodedAttr(new AttributeValue.String($"#builtin.attr_{code}"), null, null, null, null, null);
            }
        }

        private int ResolveResourceHandleBlobIndex(int handleIdx)
        {
            if (_resourceHandleToBlobIndex.TryGetValue(handleIdx, out long mapped))
            {
                return (int)mapped;
            }

            if (handleIdx >= 0 && handleIdx < resources.Count)
            {
                return handleIdx;
            }

            int assigned = _nextResourceHandleIndex++;
            _resourceHandleToBlobIndex[handleIdx] = assigned;
            return assigned;
        }

        private static string? ExtractFunctionSymbolName(List<NodeAttribute> attributes)
        {
            for (int i = 0; i < attributes.Count; i++)
            {
                if (attributes[i].Name == "sym_name" && attributes[i].Value is AttributeValue.String s)
                {
                    return s.Value;
                }
            }

            return null;
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

        private readonly record struct AttrTypeIndexEntry(string DialectName, bool IsCustom, long Start, long End);

        private readonly record struct DecodedType(TensorDescriptor Descriptor, string RawName);

        private sealed record DecodedAttr(
            AttributeValue Value,
            List<NodeAttribute>? Dictionary,
            TensorDataSlice? DenseSlice,
            TensorDescriptor? DenseDescriptor,
            TensorDataSlice? FirstDenseSlice,
            TensorDescriptor? FirstDenseDescriptor);
    }

    private readonly record struct SectionSpan(long Start, long End);

    private readonly record struct SectionHeader(byte Id, SectionSpan Span);

    private readonly record struct ResourceBlobInfo(
        string Group,
        string Key,
        long StreamOffset,
        long ByteLength,
        byte Kind);
}
