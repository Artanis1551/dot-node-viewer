using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.App.ViewModels;

/// <summary>
/// Represents a single element value entry in the tensor weight preview table.
/// </summary>
public sealed class TensorValuePreviewItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TensorValuePreviewItem"/> class.
    /// </summary>
    /// <param name="index">The zero-based linear element index within the tensor.</param>
    /// <param name="rawValue">The formatted string representation of the raw decoded value.</param>
    /// <param name="dequantizedValue">The optional formatted string representation of the affine dequantized floating-point value.</param>
    public TensorValuePreviewItem(int index, string rawValue, string? dequantizedValue = null)
    {
        Index = index;
        RawValue = rawValue;
        DequantizedValue = dequantizedValue;
    }

    /// <summary>
    /// Gets the zero-based linear element index.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets the formatted string representation of the raw decoded value.
    /// </summary>
    public string RawValue { get; }

    /// <summary>
    /// Gets the optional formatted string representation of the affine dequantized value, or <see langword="null"/> if not quantized.
    /// </summary>
    public string? DequantizedValue { get; }

    /// <summary>
    /// Gets a value indicating whether this preview item has a dequantized floating-point value.
    /// </summary>
    public bool HasDequantized => DequantizedValue is not null;
}

/// <summary>
/// ViewModel managing on-demand zero-allocation tensor weight inspection, partial slicing via <see cref="TensorDataLoader"/>,
/// multi-format decoding, statistical aggregation, and dequantization preview.
/// </summary>
public sealed partial class TensorInspectorViewModel : ObservableObject
{
    private const int MaxPreviewElements = 64;
    private const int MaxPreviewBytes = 64 * 1024; // 64 KB capped preview window

    [ObservableProperty]
    private string _tensorName = string.Empty;

    [ObservableProperty]
    private string _dataTypeText = string.Empty;

    [ObservableProperty]
    private string _shapeText = string.Empty;

    [ObservableProperty]
    private long _byteLength;

    [ObservableProperty]
    private string _byteLengthFormatted = string.Empty;

    [ObservableProperty]
    private long _streamOffset;

    [ObservableProperty]
    private string _streamOffsetHex = string.Empty;

    [ObservableProperty]
    private string _encodingText = string.Empty;

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private bool _isExternal;

    [ObservableProperty]
    private string? _externalPath;

    [ObservableProperty]
    private long? _externalOffset;

    [ObservableProperty]
    private long? _externalLength;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private int _sampledElementCount;

    [ObservableProperty]
    private double? _minValue;

    [ObservableProperty]
    private double? _maxValue;

    [ObservableProperty]
    private double? _meanValue;

    [ObservableProperty]
    private double? _zeroPercentage;

    [ObservableProperty]
    private bool _hasNaN;

    [ObservableProperty]
    private bool _hasInfinity;

    [ObservableProperty]
    private bool _hasStatistics;

    [ObservableProperty]
    private bool _hasDequantization;

    [ObservableProperty]
    private string? _quantizationDescription;

    /// <summary>
    /// Initializes a new instance of the <see cref="TensorInspectorViewModel"/> class.
    /// </summary>
    public TensorInspectorViewModel()
    {
    }

    /// <summary>
    /// Gets the collection of preview elements decoded from the tensor slice.
    /// </summary>
    public ObservableCollection<TensorValuePreviewItem> PreviewValues { get; } = new();

    /// <summary>
    /// Loads and decodes tensor slice data on-demand from the supplied model <see cref="Stream"/>.
    /// </summary>
    /// <param name="tensor">The target <see cref="TensorEdge"/> to inspect.</param>
    /// <param name="stream">The readable and seekable stream containing the model binary payload.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous inspection operation.</returns>
    public async Task InspectAsync(TensorEdge tensor, Stream? stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tensor);

        TensorName = tensor.Name;
        DataTypeText = tensor.Descriptor.DataType.ToString();
        ShapeText = tensor.Descriptor.Shape?.ToDisplayString() ?? "[]";
        PreviewValues.Clear();
        ErrorMessage = null;
        HasStatistics = false;
        HasDequantization = false;
        QuantizationDescription = null;
        MinValue = null;
        MaxValue = null;
        MeanValue = null;
        ZeroPercentage = null;
        HasNaN = false;
        HasInfinity = false;

        if (tensor.Descriptor.Quantization is AffineQuantization affine)
        {
            HasDequantization = true;
            double scale = affine.Scale.Count > 0 ? affine.Scale[0] : 1.0;
            long zp = affine.ZeroPoint.Count > 0 ? affine.ZeroPoint[0] : 0;
            QuantizationDescription = string.Create(CultureInfo.InvariantCulture, $"Scale: {scale:G6}, Zero-Point: {zp}");
        }

        if (tensor.Data is null)
        {
            HasData = false;
            IsExternal = false;
            ByteLength = 0;
            ByteLengthFormatted = "0 bytes";
            StreamOffset = 0;
            StreamOffsetHex = "0x00000000";
            EncodingText = "None";
            return;
        }

        HasData = true;

        if (tensor.Data is ExternalTensorDataReference ext)
        {
            IsExternal = true;
            ExternalPath = ext.RelativePath;
            ExternalOffset = ext.Offset;
            ExternalLength = ext.Length;
            ByteLength = ext.Length ?? 0;
            ByteLengthFormatted = ext.Length.HasValue ? $"{ext.Length.Value:N0} bytes" : "External length unknown";
            StreamOffset = ext.Offset;
            StreamOffsetHex = string.Create(CultureInfo.InvariantCulture, $"0x{ext.Offset:X8}");
            EncodingText = "External File Reference";
            return;
        }

        IsExternal = false;
        if (tensor.Data is not TensorDataSlice slice)
        {
            return;
        }

        ByteLength = slice.ByteLength;
        ByteLengthFormatted = $"{slice.ByteLength:N0} bytes";
        StreamOffset = slice.StreamOffset;
        StreamOffsetHex = string.Create(CultureInfo.InvariantCulture, $"0x{slice.StreamOffset:X8}");
        EncodingText = slice.Encoding.ToString();

        if (stream is null)
        {
            ErrorMessage = "Model stream is not available to read weights.";
            return;
        }

        if (slice.ByteLength == 0)
        {
            return;
        }

        IsLoading = true;
        try
        {
            int readLength = (int)Math.Min(slice.ByteLength, MaxPreviewBytes);
            byte[] buffer = new byte[readLength];

            int bytesRead = await TensorDataLoader.ReadSliceAsync(
                stream,
                new TensorDataSlice(slice.StreamOffset, readLength, slice.DataType, slice.Encoding),
                buffer.AsMemory(0, readLength),
                cancellationToken).ConfigureAwait(true);

            DecodeAndComputeStatistics(tensor, slice, buffer.AsSpan(0, bytesRead));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to read tensor slice: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void DecodeAndComputeStatistics(TensorEdge tensor, TensorDataSlice slice, ReadOnlySpan<byte> bytes)
    {
        List<double> rawValues = new();
        List<string> formattedRaws = new();

        AffineQuantization? affine = tensor.Descriptor.Quantization as AffineQuantization;
        double affineScale = affine is not null && affine.Scale.Count > 0 ? affine.Scale[0] : 1.0;
        long affineZp = affine is not null && affine.ZeroPoint.Count > 0 ? affine.ZeroPoint[0] : 0;

        if (slice.Encoding == TensorDataEncoding.EthosUCompressedWeights)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                rawValues.Add(b);
                formattedRaws.Add($"0x{b:X2}");
            }
        }
        else if (slice.Encoding == TensorDataEncoding.VarintPacked)
        {
            DecodeVarints(bytes, slice.DataType, rawValues, formattedRaws);
        }
        else
        {
            DecodeRawBytes(bytes, slice.DataType, rawValues, formattedRaws);
        }

        SampledElementCount = rawValues.Count;

        if (rawValues.Count > 0)
        {
            ComputeStatistics(rawValues);
        }

        int previewCount = Math.Min(rawValues.Count, MaxPreviewElements);
        for (int i = 0; i < previewCount; i++)
        {
            double val = rawValues[i];
            string rawStr = formattedRaws[i];
            string? dequantStr = null;

            if (affine is not null)
            {
                double dequant = (val - affineZp) * affineScale;
                dequantStr = dequant.ToString("G6", CultureInfo.InvariantCulture);
            }

            PreviewValues.Add(new TensorValuePreviewItem(i, rawStr, dequantStr));
        }
    }

    private static void DecodeRawBytes(
        ReadOnlySpan<byte> bytes,
        DataType dataType,
        List<double> numericValues,
        List<string> formattedRaws)
    {
        switch (dataType)
        {
            case DataType.Float32:
                for (int i = 0; i + 4 <= bytes.Length; i += 4)
                {
                    float f = BinaryPrimitives.ReadSingleLittleEndian(bytes[i..]);
                    numericValues.Add(f);
                    formattedRaws.Add(f.ToString("G7", CultureInfo.InvariantCulture));
                }
                break;

            case DataType.Float64:
                for (int i = 0; i + 8 <= bytes.Length; i += 8)
                {
                    double d = BinaryPrimitives.ReadDoubleLittleEndian(bytes[i..]);
                    numericValues.Add(d);
                    formattedRaws.Add(d.ToString("G9", CultureInfo.InvariantCulture));
                }
                break;

            case DataType.Float16:
                for (int i = 0; i + 2 <= bytes.Length; i += 2)
                {
                    Half h = BinaryPrimitives.ReadHalfLittleEndian(bytes[i..]);
                    float f = (float)h;
                    numericValues.Add(f);
                    formattedRaws.Add(f.ToString("G5", CultureInfo.InvariantCulture));
                }
                break;

            case DataType.BFloat16:
                for (int i = 0; i + 2 <= bytes.Length; i += 2)
                {
                    ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(bytes[i..]);
                    float f = BitConverter.UInt32BitsToSingle((uint)raw << 16);
                    numericValues.Add(f);
                    formattedRaws.Add(f.ToString("G5", CultureInfo.InvariantCulture));
                }
                break;

            case DataType.Int8:
            case DataType.QuantizedInt8:
                for (int i = 0; i < bytes.Length; i++)
                {
                    sbyte b = (sbyte)bytes[i];
                    numericValues.Add(b);
                    formattedRaws.Add(b.ToString(CultureInfo.InvariantCulture));
                }
                break;

            case DataType.UInt8:
            case DataType.QuantizedUInt8:
                for (int i = 0; i < bytes.Length; i++)
                {
                    byte b = bytes[i];
                    numericValues.Add(b);
                    formattedRaws.Add(b.ToString(CultureInfo.InvariantCulture));
                }
                break;

            case DataType.Int16:
            case DataType.QuantizedInt16:
                for (int i = 0; i + 2 <= bytes.Length; i += 2)
                {
                    short s = BinaryPrimitives.ReadInt16LittleEndian(bytes[i..]);
                    numericValues.Add(s);
                    formattedRaws.Add(s.ToString(CultureInfo.InvariantCulture));
                }
                break;

            case DataType.UInt16:
                for (int i = 0; i + 2 <= bytes.Length; i += 2)
                {
                    ushort u = BinaryPrimitives.ReadUInt16LittleEndian(bytes[i..]);
                    numericValues.Add(u);
                    formattedRaws.Add(u.ToString(CultureInfo.InvariantCulture));
                }
                break;

            case DataType.Int32:
            case DataType.QuantizedInt32:
                for (int i = 0; i + 4 <= bytes.Length; i += 4)
                {
                    int v = BinaryPrimitives.ReadInt32LittleEndian(bytes[i..]);
                    numericValues.Add(v);
                    formattedRaws.Add(v.ToString(CultureInfo.InvariantCulture));
                }
                break;

            case DataType.UInt32:
                for (int i = 0; i + 4 <= bytes.Length; i += 4)
                {
                    uint u = BinaryPrimitives.ReadUInt32LittleEndian(bytes[i..]);
                    numericValues.Add(u);
                    formattedRaws.Add(u.ToString(CultureInfo.InvariantCulture));
                }
                break;

            case DataType.Int64:
                for (int i = 0; i + 8 <= bytes.Length; i += 8)
                {
                    long l = BinaryPrimitives.ReadInt64LittleEndian(bytes[i..]);
                    numericValues.Add(l);
                    formattedRaws.Add(l.ToString(CultureInfo.InvariantCulture));
                }
                break;

            case DataType.UInt64:
                for (int i = 0; i + 8 <= bytes.Length; i += 8)
                {
                    ulong ul = BinaryPrimitives.ReadUInt64LittleEndian(bytes[i..]);
                    numericValues.Add(ul);
                    formattedRaws.Add(ul.ToString(CultureInfo.InvariantCulture));
                }
                break;

            case DataType.Bool:
                for (int i = 0; i < bytes.Length; i++)
                {
                    bool b = bytes[i] != 0;
                    numericValues.Add(b ? 1.0 : 0.0);
                    formattedRaws.Add(b ? "true" : "false");
                }
                break;

            default:
                for (int i = 0; i < bytes.Length; i++)
                {
                    byte b = bytes[i];
                    numericValues.Add(b);
                    formattedRaws.Add(string.Create(CultureInfo.InvariantCulture, $"0x{b:X2}"));
                }
                break;
        }
    }

    private static void DecodeVarints(
        ReadOnlySpan<byte> bytes,
        DataType dataType,
        List<double> numericValues,
        List<string> formattedRaws)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            ulong val = 0;
            int shift = 0;
            while (offset < bytes.Length)
            {
                byte b = bytes[offset++];
                val |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    break;
                }
                shift += 7;
                if (shift >= 64)
                {
                    break;
                }
            }

            if (dataType == DataType.Int32 || dataType == DataType.Int64)
            {
                long signed = (long)val;
                numericValues.Add(signed);
                formattedRaws.Add(signed.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                numericValues.Add(val);
                formattedRaws.Add(val.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private void ComputeStatistics(List<double> values)
    {
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        double sum = 0.0;
        int zeroCount = 0;
        bool hasNaN = false;
        bool hasInf = false;

        for (int i = 0; i < values.Count; i++)
        {
            double v = values[i];
            if (double.IsNaN(v))
            {
                hasNaN = true;
                continue;
            }

            if (double.IsInfinity(v))
            {
                hasInf = true;
                continue;
            }

            if (v < min)
            {
                min = v;
            }

            if (v > max)
            {
                max = v;
            }

            sum += v;

            if (Math.Abs(v) < 1e-12)
            {
                zeroCount++;
            }
        }

        HasNaN = hasNaN;
        HasInfinity = hasInf;

        if (double.IsPositiveInfinity(min) || double.IsNegativeInfinity(max))
        {
            MinValue = null;
            MaxValue = null;
            MeanValue = null;
        }
        else
        {
            MinValue = min;
            MaxValue = max;
            MeanValue = sum / values.Count;
        }

        ZeroPercentage = values.Count > 0 ? (zeroCount * 100.0) / values.Count : 0.0;
        HasStatistics = true;
    }
}

