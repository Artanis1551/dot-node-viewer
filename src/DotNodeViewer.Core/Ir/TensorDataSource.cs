namespace DotNodeViewer.Core.Ir;

/// <summary>
/// Specifies how tensor elements are encoded within a <see cref="TensorDataSlice"/> byte range.
/// </summary>
public enum TensorDataEncoding
{
    /// <summary>
    /// Elements are stored as contiguous little-endian IEEE/two's-complement raw bytes matching their <see cref="DataType"/>.
    /// </summary>
    Raw,

    /// <summary>
    /// Elements are stored as packed Protobuf base-128 varints (for example, ONNX packed <c>int32_data</c> or <c>int64_data</c>).
    /// </summary>
    VarintPacked,

    /// <summary>
    /// Elements are stored as proprietary hardware-accelerator compressed bitstreams (such as Arm Ethos-U weight and scale streams).
    /// </summary>
    EthosUCompressedWeights,
}

/// <summary>
/// Describes the backing storage location of a tensor's raw weight or constant payload without loading it into memory.
/// </summary>
/// <remarks>
/// <see cref="TensorDataSource"/> is a closed hierarchy consisting of <see cref="TensorDataSlice"/> (for inline stream slices)
/// and <see cref="ExternalTensorDataReference"/> (for external shard files).
/// </remarks>
public abstract record TensorDataSource
{
    private protected TensorDataSource()
    {
    }
}

/// <summary>
/// Represents a lazy, zero-allocation byte range within the model stream containing a tensor's raw data payload.
/// </summary>
/// <param name="StreamOffset">The zero-based absolute byte offset in the source <see cref="Stream"/> where the payload begins.</param>
/// <param name="ByteLength">The length of the payload in bytes.</param>
/// <param name="DataType">One of the enumeration values that specifies the element data type stored in the slice.</param>
/// <param name="Encoding">One of the enumeration values that specifies how the elements are encoded within the byte range.</param>
public sealed record TensorDataSlice(
    long StreamOffset,
    long ByteLength,
    DataType DataType,
    TensorDataEncoding Encoding = TensorDataEncoding.Raw) : TensorDataSource;

/// <summary>
/// Represents a reference to tensor weight data stored in an external file alongside the model file.
/// </summary>
/// <param name="RelativePath">The relative path to the external shard file as recorded in the model metadata.</param>
/// <param name="Offset">The zero-based byte offset within the external file where the tensor data starts.</param>
/// <param name="Length">The optional length in bytes of the tensor data in the external file, or <see langword="null"/> if the remainder of the file is used.</param>
/// <param name="DataType">One of the enumeration values that specifies the element data type of the external tensor.</param>
public sealed record ExternalTensorDataReference(
    string RelativePath,
    long Offset,
    long? Length,
    DataType DataType) : TensorDataSource;

