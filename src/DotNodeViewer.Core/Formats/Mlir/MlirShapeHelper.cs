using System.Globalization;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Mlir;

/// <summary>
/// Provides defensive MLIR tensor type parsing, 128-bit element-count overflow protection,
/// and single-<c>?</c> dynamic dimension inference from known buffer byte lengths.
/// </summary>
internal static class MlirShapeHelper
{
    /// <summary>
    /// Diagnostic code emitted when tensor element counts or byte sizes overflow 64-bit / 128-bit bounds.
    /// </summary>
    public const string ShapeOverflowDiagnosticCode = "DNV_MLIR_SHAPE_OVERFLOW";

    /// <summary>
    /// Diagnostic code emitted when an MLIR element type has no v1 <see cref="DataType"/> representation.
    /// </summary>
    public const string UnsupportedDataTypeDiagnosticCode = "DNV_MLIR_UNSUPPORTED_DTYPE";

    /// <summary>
    /// Computes the total number of elements for a statically shaped tensor using 128-bit unsigned arithmetic.
    /// </summary>
    /// <param name="dimensions">The ordered tensor dimensions.</param>
    /// <param name="elementCount">When this method returns <see langword="true"/>, contains the exact element count.</param>
    /// <param name="overflowed">When this method returns <see langword="false"/>, indicates whether multiplication overflowed <see cref="UInt128"/>.</param>
    /// <returns><see langword="true"/> if all dimensions are fixed non-negative integers and their product fits in <see cref="UInt128"/>; otherwise, <see langword="false"/>.</returns>
    public static bool TryComputeElementCount(
        IReadOnlyList<TensorDimension> dimensions,
        out UInt128 elementCount,
        out bool overflowed)
    {
        ArgumentNullException.ThrowIfNull(dimensions);

        overflowed = false;
        elementCount = 0;

        for (int i = 0; i < dimensions.Count; i++)
        {
            if (dimensions[i] is not TensorDimension.Fixed fixedDim || fixedDim.Value < 0)
            {
                return false;
            }
        }

        UInt128 product = 1;
        for (int i = 0; i < dimensions.Count; i++)
        {
            long dim = ((TensorDimension.Fixed)dimensions[i]).Value;
            if (dim == 0)
            {
                product = 0;
                continue;
            }

            UInt128 uDim = (UInt128)dim;
            if (product > UInt128.MaxValue / uDim)
            {
                overflowed = true;
                elementCount = 0;
                return false;
            }

            product *= uDim;
        }

        elementCount = product;
        return true;
    }

    /// <summary>
    /// Computes the required byte length for a statically shaped tensor using 128-bit checked arithmetic,
    /// guarding against signed 64-bit wrap-around.
    /// </summary>
    /// <param name="dimensions">The ordered tensor dimensions.</param>
    /// <param name="elementBitWidth">The bit width of a single element.</param>
    /// <param name="byteLength">When this method returns <see langword="true"/>, contains the non-negative byte length.</param>
    /// <param name="overflowed">When this method returns <see langword="false"/>, indicates whether the calculation overflowed <see cref="UInt128"/> or <see cref="long.MaxValue"/>.</param>
    /// <returns><see langword="true"/> if the byte length is statically known and fits in <see cref="long"/>; otherwise, <see langword="false"/>.</returns>
    public static bool TryComputeByteLength(
        IReadOnlyList<TensorDimension> dimensions,
        int elementBitWidth,
        out long byteLength,
        out bool overflowed)
    {
        byteLength = 0;
        overflowed = false;

        if (elementBitWidth <= 0)
        {
            return false;
        }

        if (!TryComputeElementCount(dimensions, out UInt128 elementCount, out overflowed))
        {
            return false;
        }

        UInt128 bitWidth = (UInt128)elementBitWidth;
        if (elementCount > UInt128.MaxValue / bitWidth)
        {
            overflowed = true;
            return false;
        }

        UInt128 totalBits = elementCount * bitWidth;
        UInt128 totalBytes = totalBits == 0 ? 0 : ((totalBits - 1) / 8) + 1;
        if (totalBytes > (UInt128)long.MaxValue)
        {
            overflowed = true;
            return false;
        }

        byteLength = (long)totalBytes;
        return true;
    }

    /// <summary>
    /// Infers the concrete size of a single dynamic (<c>?</c>) dimension when the buffer byte length and element bit width are known.
    /// </summary>
    /// <param name="shape">The candidate tensor shape.</param>
    /// <param name="bufferByteLength">The exact byte length of the backing constant or resource buffer.</param>
    /// <param name="elementBitWidth">The element bit width.</param>
    /// <param name="inferredShape">When this method returns <see langword="true"/>, contains the resolved <see cref="TensorShape"/>.</param>
    /// <returns><see langword="true"/> if <paramref name="shape"/> contained a single <c>?</c> dimension and was cleanly inferred; otherwise, <see langword="false"/>.</returns>
    public static bool TryInferSingleDynamicDimension(
        TensorShape shape,
        long bufferByteLength,
        int elementBitWidth,
        out TensorShape inferredShape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        inferredShape = shape;

        if (bufferByteLength < 0 || elementBitWidth <= 0 || shape.Dimensions.Count == 0)
        {
            return false;
        }

        int dynamicIndex = -1;
        UInt128 staticProduct = 1;

        for (int i = 0; i < shape.Dimensions.Count; i++)
        {
            switch (shape.Dimensions[i])
            {
                case TensorDimension.Dynamic:
                    if (dynamicIndex >= 0)
                    {
                        return false;
                    }

                    dynamicIndex = i;
                    break;

                case TensorDimension.Symbolic:
                    return false;

                case TensorDimension.Fixed fixedDim:
                    if (fixedDim.Value <= 0)
                    {
                        return false;
                    }

                    UInt128 dim = (UInt128)fixedDim.Value;
                    if (staticProduct > UInt128.MaxValue / dim)
                    {
                        return false;
                    }

                    staticProduct *= dim;
                    break;

                default:
                    return false;
            }
        }

        if (dynamicIndex < 0)
        {
            return false;
        }

        UInt128 totalBits = (UInt128)bufferByteLength * 8;
        UInt128 bitWidth = (UInt128)elementBitWidth;
        if (totalBits % bitWidth != 0)
        {
            return false;
        }

        UInt128 totalElements = totalBits / bitWidth;
        if (totalElements % staticProduct != 0)
        {
            return false;
        }

        UInt128 inferred = totalElements / staticProduct;
        if (inferred > (UInt128)long.MaxValue)
        {
            return false;
        }

        TensorDimension[] resolved = new TensorDimension[shape.Dimensions.Count];
        for (int i = 0; i < shape.Dimensions.Count; i++)
        {
            resolved[i] = i == dynamicIndex
                ? new TensorDimension.Fixed((long)inferred)
                : shape.Dimensions[i];
        }

        inferredShape = new TensorShape(resolved);
        return true;
    }

    /// <summary>
    /// Returns the bit width for a known <see cref="DataType"/> or emerging MLIR element type string.
    /// </summary>
    public static int GetElementBitWidth(DataType dataType, string? rawMlirType = null)
    {
        switch (dataType)
        {
            case DataType.Int4:
            case DataType.UInt4:
                return 4;
            case DataType.Bool:
            case DataType.Int8:
            case DataType.UInt8:
            case DataType.Float8E4M3:
            case DataType.Float8E5M2:
            case DataType.QuantizedInt8:
            case DataType.QuantizedUInt8:
                return 8;
            case DataType.Int16:
            case DataType.UInt16:
            case DataType.Float16:
            case DataType.BFloat16:
            case DataType.QuantizedInt16:
                return 16;
            case DataType.Int32:
            case DataType.UInt32:
            case DataType.Float32:
            case DataType.QuantizedInt32:
                return 32;
            case DataType.Int64:
            case DataType.UInt64:
            case DataType.Float64:
            case DataType.Complex64:
                return 64;
            case DataType.Complex128:
                return 128;
        }

        if (!string.IsNullOrWhiteSpace(rawMlirType))
        {
            string normalized = rawMlirType.Trim().ToLowerInvariant();
            if (normalized is "float8e5m3fnu" or "f8e5m3fnu" or "f8e5m3fnuz" or "mxi8" or "f8e8m0fnu" or "f8e4m3fnuz" or "f8e5m2fnuz")
            {
                return 8;
            }

            if (normalized is "f4e2m1fn" or "float4e2m1fn")
            {
                return 4;
            }

            if (normalized is "complex<f16>" or "complex<float16>" or "complex<bf16>")
            {
                return 32;
            }
        }

        return 0;
    }

    /// <summary>
    /// Parses an MLIR element type (including <c>!quant.uniform&lt;...&gt;</c> and <c>complex&lt;...&gt;</c>)
    /// into a <see cref="DataType"/> and optional <see cref="QuantizationInfo"/>.
    /// </summary>
    public static (DataType DataType, QuantizationInfo? Quantization, int BitWidth) ParseElementType(
        string rawType,
        List<ParseDiagnostic>? diagnostics = null,
        string? location = null)
    {
        string trimmed = rawType.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return (DataType.Unknown, null, 0);
        }

        if (trimmed.StartsWith("!quant.uniform<", StringComparison.Ordinal))
        {
            if (TryParseQuantUniformType(trimmed, out DataType storageType, out QuantizationInfo? quantInfo))
            {
                return (storageType, quantInfo, GetElementBitWidth(storageType));
            }

            AddUnsupportedDtypeDiagnostic(trimmed, diagnostics, location);
            return (DataType.Unknown, null, 0);
        }

        string lower = trimmed.ToLowerInvariant();
        DataType mapped = lower switch
        {
            "i1" or "bool" => DataType.Bool,
            "i4" or "si4" => DataType.Int4,
            "ui4" => DataType.UInt4,
            "i8" or "si8" => DataType.Int8,
            "ui8" => DataType.UInt8,
            "i16" or "si16" => DataType.Int16,
            "ui16" => DataType.UInt16,
            "i32" or "si32" => DataType.Int32,
            "ui32" => DataType.UInt32,
            "i64" or "si64" or "index" => DataType.Int64,
            "ui64" => DataType.UInt64,
            "f8e4m3fn" or "f8e4m3" or "f8e4m3fnuz" => DataType.Float8E4M3,
            "f8e5m2" or "f8e5m2fnuz" => DataType.Float8E5M2,
            "f16" or "float16" => DataType.Float16,
            "bf16" or "bfloat16" => DataType.BFloat16,
            "f32" or "float32" => DataType.Float32,
            "f64" or "float64" => DataType.Float64,
            "complex<f32>" or "complex<float32>" => DataType.Complex64,
            "complex<f64>" or "complex<float64>" => DataType.Complex128,
            "!torch.str" or "str" or "string" => DataType.String,
            "none" => DataType.Unknown,
            _ => DataType.Unknown,
        };

        if (mapped == DataType.Unknown && lower != "none")
        {
            AddUnsupportedDtypeDiagnostic(trimmed, diagnostics, location);
        }

        int bitWidth = GetElementBitWidth(mapped, trimmed);
        return (mapped, null, bitWidth);
    }

    /// <summary>
    /// Parses an MLIR type expression (such as <c>tensor&lt;1x3x224x224xf32&gt;</c>, <c>tensor&lt;*xf32&gt;</c>,
    /// <c>!torch.vtensor&lt;[1,8],f32&gt;</c>, <c>memref&lt;...&gt;</c>, or scalar <c>f32</c>) into a <see cref="TensorDescriptor"/>.
    /// </summary>
    public static TensorDescriptor ParseTensorTypeString(
        string typeText,
        List<ParseDiagnostic>? diagnostics = null,
        string? location = null)
    {
        if (string.IsNullOrWhiteSpace(typeText))
        {
            return new TensorDescriptor(DataType.Unknown, null);
        }

        string trimmed = typeText.Trim();

        // Unwrap !tt.ptr<...>
        if (trimmed.StartsWith("!tt.ptr<", StringComparison.Ordinal) && trimmed.EndsWith('>'))
        {
            string innerPtr = UnwrapOuterAngleBrackets(trimmed["!tt.ptr".Length..]);
            // !tt.ptr<f16, 1> -> split at top-level comma
            SplitTopLevelComma(innerPtr, out string pointee, out _);
            return ParseTensorTypeString(pointee, diagnostics, location);
        }

        // Unwrap !torch.vtensor<[dims], dtype> or !torch.tensor<[dims], dtype>
        if ((trimmed.StartsWith("!torch.vtensor<", StringComparison.Ordinal) ||
             trimmed.StartsWith("!torch.tensor<", StringComparison.Ordinal)) &&
            trimmed.EndsWith('>'))
        {
            return ParseTorchMlirTensorType(trimmed, diagnostics, location);
        }

        // Unwrap tensor<...>, memref<...>, vector<...>
        bool isTensor = trimmed.StartsWith("tensor<", StringComparison.Ordinal);
        bool isMemref = trimmed.StartsWith("memref<", StringComparison.Ordinal);
        bool isVector = trimmed.StartsWith("vector<", StringComparison.Ordinal);

        if ((isTensor || isMemref || isVector) && trimmed.EndsWith('>'))
        {
            int ltIndex = trimmed.IndexOf('<');
            string inner = trimmed[(ltIndex + 1)..^1].Trim();
            SplitTopLevelComma(inner, out string shapeAndType, out string? encodingSuffix);

            string? layout = null;
            if (!string.IsNullOrWhiteSpace(encodingSuffix) && isMemref)
            {
                layout = encodingSuffix.Trim();
            }

            if (shapeAndType.StartsWith("*x", StringComparison.Ordinal))
            {
                string elemStr = shapeAndType[2..].Trim();
                (DataType elemType, QuantizationInfo? quant, _) = ParseElementType(elemStr, diagnostics, location);
                return new TensorDescriptor(elemType, null, quant, layout);
            }

            List<TensorDimension> dims = [];
            int cursor = 0;
            bool hadDimensionOverflow = false;
            while (TryReadNextDimensionToken(shapeAndType, ref cursor, out TensorDimension? dim, out bool tokenOverflow))
            {
                if (tokenOverflow)
                {
                    hadDimensionOverflow = true;
                }

                if (dim is not null)
                {
                    dims.Add(dim);
                }
            }

            string remainingElement = shapeAndType[cursor..].Trim();
            (DataType dataType, QuantizationInfo? quantization, int bitWidth) =
                ParseElementType(remainingElement, diagnostics, location);

            TensorShape shape = dims.Count == 0 ? TensorShape.Scalar : new TensorShape(dims);

            if (hadDimensionOverflow ||
                (dims.Count > 0 && !TryComputeByteLength(dims, bitWidth > 0 ? bitWidth : 8, out _, out bool mulOverflow) && mulOverflow))
            {
                diagnostics?.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Warning,
                    ShapeOverflowDiagnosticCode,
                    $"Tensor shape '{trimmed}' overflows 64-bit/128-bit element-count or byte-length bounds.",
                    location));
            }

            return new TensorDescriptor(dataType, shape, quantization, layout);
        }

        // Scalar or unrecognized non-tensor type
        (DataType scalarType, QuantizationInfo? scalarQuant, _) = ParseElementType(trimmed, diagnostics, location);
        return new TensorDescriptor(scalarType, TensorShape.Scalar, scalarQuant);
    }

    private static TensorDescriptor ParseTorchMlirTensorType(
        string trimmed,
        List<ParseDiagnostic>? diagnostics,
        string? location)
    {
        int ltIndex = trimmed.IndexOf('<');
        string inner = trimmed[(ltIndex + 1)..^1].Trim();
        SplitTopLevelComma(inner, out string shapePart, out string? dtypePart);

        TensorShape? shape = null;
        string shapeTrimmed = shapePart.Trim();
        if (shapeTrimmed.StartsWith('[') && shapeTrimmed.EndsWith(']'))
        {
            string listInner = shapeTrimmed[1..^1].Trim();
            if (listInner.Length == 0)
            {
                shape = TensorShape.Scalar;
            }
            else
            {
                string[] parts = listInner.Split(',');
                TensorDimension[] dims = new TensorDimension[parts.Length];
                bool hadOverflow = false;
                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i].Trim();
                    if (part is "?" or "-1")
                    {
                        dims[i] = TensorDimension.Dynamic.Instance;
                    }
                    else if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out long fixedVal) && fixedVal >= 0)
                    {
                        dims[i] = new TensorDimension.Fixed(fixedVal);
                    }
                    else if (IsAllAsciiDigits(part))
                    {
                        hadOverflow = true;
                        dims[i] = TensorDimension.Dynamic.Instance;
                    }
                    else
                    {
                        dims[i] = new TensorDimension.Symbolic(part);
                    }
                }

                shape = new TensorShape(dims);
                if (hadOverflow)
                {
                    diagnostics?.Add(new ParseDiagnostic(
                        DiagnosticSeverity.Warning,
                        ShapeOverflowDiagnosticCode,
                        $"Tensor shape '{trimmed}' contains a dimension overflowing 64-bit bounds.",
                        location));
                }
            }
        }

        if (string.IsNullOrWhiteSpace(dtypePart))
        {
            return new TensorDescriptor(DataType.Unknown, shape);
        }

        (DataType elemType, QuantizationInfo? quant, int bitWidth) = ParseElementType(dtypePart.Trim(), diagnostics, location);
        if (shape is not null && shape.Dimensions.Count > 0 &&
            !TryComputeByteLength(shape.Dimensions, bitWidth > 0 ? bitWidth : 8, out _, out bool mulOverflow) &&
            mulOverflow)
        {
            diagnostics?.Add(new ParseDiagnostic(
                DiagnosticSeverity.Warning,
                ShapeOverflowDiagnosticCode,
                $"Tensor shape '{trimmed}' overflows 64-bit/128-bit element-count or byte-length bounds.",
                location));
        }

        return new TensorDescriptor(elemType, shape, quant);
    }

    private static bool TryReadNextDimensionToken(
        string text,
        ref int cursor,
        out TensorDimension? dimension,
        out bool overflowed)
    {
        dimension = null;
        overflowed = false;

        if (cursor >= text.Length)
        {
            return false;
        }

        int pos = cursor;
        if (text[pos] == '?')
        {
            if (pos + 1 < text.Length && text[pos + 1] == 'x')
            {
                cursor = pos + 2;
                dimension = TensorDimension.Dynamic.Instance;
                return true;
            }

            return false;
        }

        if (char.IsAsciiDigit(text[pos]))
        {
            int start = pos;
            while (pos < text.Length && char.IsAsciiDigit(text[pos]))
            {
                pos++;
            }

            if (pos < text.Length && text[pos] == 'x')
            {
                ReadOnlySpan<char> numSpan = text.AsSpan(start, pos - start);
                cursor = pos + 1;
                if (long.TryParse(numSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long fixedVal))
                {
                    dimension = new TensorDimension.Fixed(fixedVal);
                }
                else
                {
                    overflowed = true;
                    dimension = TensorDimension.Dynamic.Instance;
                }

                return true;
            }
        }

        return false;
    }

    private static bool TryParseQuantUniformType(
        string trimmed,
        out DataType storageType,
        out QuantizationInfo? quantInfo)
    {
        storageType = DataType.Unknown;
        quantInfo = null;

        if (!trimmed.StartsWith("!quant.uniform<", StringComparison.Ordinal) || !trimmed.EndsWith('>'))
        {
            return false;
        }

        string inner = trimmed["!quant.uniform<".Length..^1].Trim();
        SplitTopLevelComma(inner, out string typeSpec, out string? paramSpec);
        if (string.IsNullOrWhiteSpace(paramSpec))
        {
            return false;
        }

        // typeSpec is "<storage>[:<expressed>[:<axis>]]" e.g. "i8:f32" or "i8:f32:0" or "u8<0:255>:f32"
        int colonIdx = typeSpec.IndexOf(':');
        string storagePart = colonIdx >= 0 ? typeSpec[..colonIdx].Trim() : typeSpec.Trim();
        int rangeIdx = storagePart.IndexOf('<');
        if (rangeIdx >= 0)
        {
            storagePart = storagePart[..rangeIdx].Trim();
        }

        storageType = storagePart.ToLowerInvariant() switch
        {
            "i8" or "si8" => DataType.Int8,
            "u8" or "ui8" => DataType.UInt8,
            "i16" or "si16" => DataType.Int16,
            "u16" or "ui16" => DataType.UInt16,
            "i32" or "si32" => DataType.Int32,
            "i4" or "si4" => DataType.Int4,
            "u4" or "ui4" => DataType.UInt4,
            _ => DataType.Unknown,
        };

        if (storageType == DataType.Unknown)
        {
            return false;
        }

        int? quantizedDimension = null;
        if (colonIdx >= 0)
        {
            string restType = typeSpec[(colonIdx + 1)..];
            int secondColon = restType.IndexOf(':');
            if (secondColon >= 0 &&
                int.TryParse(restType[(secondColon + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int axis))
            {
                quantizedDimension = axis;
            }
        }

        string p = paramSpec.Trim();
        if (p.StartsWith('{') && p.EndsWith('}'))
        {
            string listBody = p[1..^1];
            string[] entries = listBody.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<float> scales = new(entries.Length);
            List<long> zeroPoints = new(entries.Length);
            foreach (string entry in entries)
            {
                if (TryParseScaleZeroPoint(entry, out float s, out long zp))
                {
                    scales.Add(s);
                    zeroPoints.Add(zp);
                }
            }

            if (scales.Count > 0)
            {
                quantInfo = new AffineQuantization(scales, zeroPoints, quantizedDimension ?? 0);
            }
        }
        else if (TryParseScaleZeroPoint(p, out float scale, out long zeroPoint))
        {
            quantInfo = new AffineQuantization([scale], [zeroPoint], quantizedDimension);
        }

        return true;
    }

    private static bool TryParseScaleZeroPoint(string entry, out float scale, out long zeroPoint)
    {
        scale = 1.0f;
        zeroPoint = 0;

        string trimmed = entry.Trim();
        int colonIdx = trimmed.IndexOf(':');
        if (colonIdx >= 0)
        {
            string scaleStr = trimmed[..colonIdx].Trim();
            string zpStr = trimmed[(colonIdx + 1)..].Trim();
            bool hasScale = float.TryParse(scaleStr, NumberStyles.Float, CultureInfo.InvariantCulture, out scale);
            bool hasZp = long.TryParse(zpStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out zeroPoint);
            return hasScale && hasZp;
        }

        if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
        {
            zeroPoint = 0;
            return true;
        }

        return false;
    }

    private static void SplitTopLevelComma(string input, out string left, out string? right)
    {
        int angle = 0;
        int paren = 0;
        int bracket = 0;
        int brace = 0;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            switch (c)
            {
                case '<': angle++; break;
                case '>': if (angle > 0) angle--; break;
                case '(': paren++; break;
                case ')': if (paren > 0) paren--; break;
                case '[': bracket++; break;
                case ']': if (bracket > 0) bracket--; break;
                case '{': brace++; break;
                case '}': if (brace > 0) brace--; break;
                case ',' when angle == 0 && paren == 0 && bracket == 0 && brace == 0:
                    left = input[..i].Trim();
                    right = input[(i + 1)..].Trim();
                    return;
            }
        }

        left = input.Trim();
        right = null;
    }

    private static string UnwrapOuterAngleBrackets(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith('<') && trimmed.EndsWith('>') && trimmed.Length >= 2)
        {
            return trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private static bool IsAllAsciiDigits(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddUnsupportedDtypeDiagnostic(
        string rawType,
        List<ParseDiagnostic>? diagnostics,
        string? location)
    {
        diagnostics?.Add(new ParseDiagnostic(
            DiagnosticSeverity.Warning,
            UnsupportedDataTypeDiagnosticCode,
            $"Unsupported MLIR element type '{rawType}' mapped to DataType.Unknown.",
            location));
    }
}
