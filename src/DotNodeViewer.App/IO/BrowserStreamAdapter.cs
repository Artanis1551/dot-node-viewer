using System.Buffers;

namespace DotNodeViewer.App.IO;

/// <summary>
/// Provides browser-safe streaming, buffering, and seekable stream adaptation with tab memory limits.
/// </summary>
public static class BrowserStreamAdapter
{
    /// <summary>
    /// Default browser tab memory guard limit (512 MB).
    /// </summary>
    public const long DefaultMaxBrowserModelBytes = 512L * 1024 * 1024; // 512 MB

    /// <summary>
    /// Stages an incoming browser stream into a seekable memory buffer while enforcing browser tab memory safety.
    /// </summary>
    /// <param name="sourceStream">The incoming browser data stream.</param>
    /// <param name="fileName">The original file name for diagnostic reporting.</param>
    /// <param name="maxBytes">Maximum permissible byte length in the browser tab.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A seekable <see cref="MemoryStream"/> containing the model payload.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sourceStream"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the stream payload exceeds <paramref name="maxBytes"/>.</exception>
    public static async Task<MemoryStream> CreateSeekableStreamAsync(
        Stream sourceStream,
        string fileName,
        long maxBytes = DefaultMaxBrowserModelBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceStream);

        if (sourceStream.CanSeek && sourceStream.Length > maxBytes)
        {
            throw new InvalidOperationException(
                $"Model '{fileName}' ({sourceStream.Length / (1024 * 1024)} MB) exceeds browser tab memory limit of {maxBytes / (1024 * 1024)} MB. Use DotNodeViewer.Desktop for large models.");
        }

        int initialCapacity = sourceStream.CanSeek && sourceStream.Length > 0 && sourceStream.Length <= int.MaxValue
            ? (int)sourceStream.Length
            : 0;

        MemoryStream memoryStream = initialCapacity > 0
            ? new MemoryStream(initialCapacity)
            : new MemoryStream();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long totalRead = 0;

        try
        {
            while (true)
            {
                int bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                totalRead += bytesRead;
                if (totalRead > maxBytes)
                {
                    throw new InvalidOperationException(
                        $"Model '{fileName}' exceeded browser tab memory limit of {maxBytes / (1024 * 1024)} MB while streaming. Use DotNodeViewer.Desktop for large models.");
                }

                memoryStream.Write(buffer, 0, bytesRead);
            }

            memoryStream.Position = 0;
            return memoryStream;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Creates a delegate factory producing seekable, read-only memory streams from a byte buffer for on-demand weight inspection.
    /// </summary>
    /// <param name="buffer">The captured model byte buffer.</param>
    /// <returns>A function returning a new read-only <see cref="MemoryStream"/> over the buffer.</returns>
    public static Func<Stream> CreateStreamFactory(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return () => new MemoryStream(buffer, writable: false);
    }
}

