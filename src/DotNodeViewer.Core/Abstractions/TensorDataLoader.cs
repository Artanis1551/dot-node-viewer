using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Abstractions;

/// <summary>
/// Provides on-demand, zero-heap-allocation reading of a <see cref="TensorDataSlice"/> from a seekable model <see cref="Stream"/> into a caller-supplied buffer.
/// </summary>
public static class TensorDataLoader
{
    /// <summary>
    /// Reads up to <paramref name="destination"/>.Length bytes from the model <paramref name="stream"/> for the specified <paramref name="slice"/>.
    /// </summary>
    /// <param name="stream">The readable, seekable model stream containing the tensor payload.</param>
    /// <param name="slice">The <see cref="TensorDataSlice"/> describing the byte offset and length of the tensor payload.</param>
    /// <param name="destination">The caller-supplied buffer that receives the raw tensor bytes.</param>
    /// <returns>The exact number of bytes copied into <paramref name="destination"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="slice"/> is <see langword="null"/>.</exception>
    /// <exception cref="ModelParseException">The stream is not seekable, the slice bounds fall outside the stream, or the stream ends prematurely.</exception>
    public static int ReadSlice(Stream stream, TensorDataSlice slice, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(slice);

        int bytesToRead = ValidateAndComputeReadCount(stream, slice, destination.Length);
        if (bytesToRead == 0)
        {
            return 0;
        }

        long previousPosition = stream.Position;
        try
        {
            stream.Position = slice.StreamOffset;
            Span<byte> target = destination[..bytesToRead];
            stream.ReadExactly(target);
            return bytesToRead;
        }
        catch (EndOfStreamException ex)
        {
            throw new ModelParseException(
                $"Unexpected end of stream while reading tensor slice of {bytesToRead} bytes.",
                fileOffset: slice.StreamOffset,
                innerException: ex);
        }
        finally
        {
            stream.Position = previousPosition;
        }
    }

    /// <summary>
    /// Asynchronously reads up to <paramref name="destination"/>.Length bytes from the model <paramref name="stream"/> for the specified <paramref name="slice"/>.
    /// </summary>
    /// <param name="stream">The readable, seekable model stream containing the tensor payload.</param>
    /// <param name="slice">The <see cref="TensorDataSlice"/> describing the byte offset and length of the tensor payload.</param>
    /// <param name="destination">The caller-supplied memory buffer that receives the raw tensor bytes.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous read operation and containing the number of bytes copied into <paramref name="destination"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="slice"/> is <see langword="null"/>.</exception>
    /// <exception cref="ModelParseException">The stream is not seekable, the slice bounds fall outside the stream, or the stream ends prematurely.</exception>
    public static async ValueTask<int> ReadSliceAsync(
        Stream stream,
        TensorDataSlice slice,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(slice);

        int bytesToRead = ValidateAndComputeReadCount(stream, slice, destination.Length);
        if (bytesToRead == 0)
        {
            return 0;
        }

        long previousPosition = stream.Position;
        try
        {
            stream.Position = slice.StreamOffset;
            Memory<byte> target = destination[..bytesToRead];
            await stream.ReadExactlyAsync(target, cancellationToken).ConfigureAwait(false);
            return bytesToRead;
        }
        catch (EndOfStreamException ex)
        {
            throw new ModelParseException(
                $"Unexpected end of stream while reading tensor slice of {bytesToRead} bytes.",
                fileOffset: slice.StreamOffset,
                innerException: ex);
        }
        finally
        {
            stream.Position = previousPosition;
        }
    }

    private static int ValidateAndComputeReadCount(Stream stream, TensorDataSlice slice, int bufferLength)
    {
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ModelParseException("Model stream must be readable and seekable to load tensor slices.");
        }

        if (slice.StreamOffset < 0 || slice.ByteLength < 0)
        {
            throw new ModelParseException(
                $"Invalid tensor data slice bounds (offset={slice.StreamOffset}, length={slice.ByteLength}).",
                fileOffset: slice.StreamOffset);
        }

        long streamLength = stream.Length;
        if (slice.StreamOffset > streamLength || slice.ByteLength > streamLength - slice.StreamOffset)
        {
            throw new ModelParseException(
                $"Tensor data slice [0x{slice.StreamOffset:X}..0x{slice.StreamOffset + slice.ByteLength:X}) exceeds stream length 0x{streamLength:X}.",
                fileOffset: slice.StreamOffset);
        }

        return (int)Math.Min(slice.ByteLength, bufferLength);
    }
}

