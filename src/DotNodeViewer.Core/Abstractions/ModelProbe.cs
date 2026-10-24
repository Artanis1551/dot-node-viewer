using System.Diagnostics.CodeAnalysis;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.IO.Zip;

namespace DotNodeViewer.Core.Abstractions;

/// <summary>
/// Provides a non-destructive inspection handle over a seekable model <see cref="Stream"/> for format detection.
/// </summary>
/// <remarks>
/// <see cref="ModelProbe"/> exposes up to the first 64 header bytes of the stream, an optional file name hint,
/// and a lazily populated list of ZIP central-directory entry paths while guaranteeing that <see cref="Stream.Position"/>
/// is restored to its initial offset.
/// </remarks>
public sealed class ModelProbe
{
    /// <summary>
    /// The maximum number of header bytes peeked from the beginning of a model stream.
    /// </summary>
    public const int HeaderPeekByteCount = 64;

    private readonly Stream _stream;
    private readonly long _initialPosition;
    private readonly byte[] _header;
    private readonly int _headerLength;
    private IReadOnlyList<string>? _zipEntries;
    private ZipDirectory? _zipDirectory;
    private bool _zipDirectoryAttempted;

    private ModelProbe(
        Stream stream,
        long initialPosition,
        long streamLength,
        byte[] header,
        int headerLength,
        string? fileName)
    {
        _stream = stream;
        _initialPosition = initialPosition;
        StreamLength = streamLength;
        _header = header;
        _headerLength = headerLength;
        FileName = fileName;
        FileExtension = ExtractNormalizedExtension(fileName);
    }

    /// <summary>
    /// Gets the underlying seekable model stream.
    /// </summary>
    /// <value>The seekable <see cref="Stream"/> being probed.</value>
    public Stream Stream => _stream;

    /// <summary>
    /// Gets the total length of the model stream in bytes.
    /// </summary>
    /// <value>The length of the stream in bytes.</value>
    public long StreamLength { get; }

    /// <summary>
    /// Gets the optional file name or path associated with the stream.
    /// </summary>
    /// <value>The file name hint, or <see langword="null"/> if not provided.</value>
    public string? FileName { get; }

    /// <summary>
    /// Gets the normalized lowercase file extension (including the leading period, such as <c>".onnx"</c>), if <see cref="FileName"/> was supplied.
    /// </summary>
    /// <value>The lowercase file extension, or <see langword="null"/> if no extension is present.</value>
    public string? FileExtension { get; }

    /// <summary>
    /// Gets the header bytes (up to 64 bytes) read from offset 0 of the model stream.
    /// </summary>
    /// <value>A read-only span over the peeked header bytes.</value>
    public ReadOnlySpan<byte> Header => _header.AsSpan(0, _headerLength);

    /// <summary>
    /// Gets the header bytes (up to 64 bytes) read from offset 0 of the model stream as a <see cref="ReadOnlyMemory{T}"/>.
    /// </summary>
    /// <value>A read-only memory slice over the peeked header bytes.</value>
    public ReadOnlyMemory<byte> HeaderBytes => _header.AsMemory(0, _headerLength);

    /// <summary>
    /// Gets a value that indicates whether the stream begins with a standard PKZIP local file header or empty-archive signature.
    /// </summary>
    /// <value><see langword="true"/> if the header begins with <c>PK\x03\x04</c> or <c>PK\x05\x06</c>; otherwise, <see langword="false"/>.</value>
    public bool IsZipArchive
    {
        get
        {
            ReadOnlySpan<byte> h = Header;
            return h.Length >= 4
                && h[0] == (byte)'P'
                && h[1] == (byte)'K'
                && ((h[2] == 3 && h[3] == 4) || (h[2] == 5 && h[3] == 6));
        }
    }

    /// <summary>
    /// Gets the parsed <see cref="ZipDirectory"/> if the stream is a valid ZIP archive; otherwise, <see langword="null"/>.
    /// </summary>
    internal ZipDirectory? ZipDirectory
    {
        get
        {
            if (!_zipDirectoryAttempted)
            {
                _zipDirectory = ReadZipDirectoryNonDestructively();
                _zipDirectoryAttempted = true;
            }

            return _zipDirectory;
        }
    }

    /// <summary>
    /// Gets the central-directory entry paths if the stream is a valid ZIP archive, or an empty list otherwise.
    /// </summary>
    /// <value>A read-only list of entry full names in the ZIP archive.</value>
    public IReadOnlyList<string> ZipEntries =>
        _zipEntries ??= (ZipDirectory is not null ? [.. ZipDirectory.Entries.Keys] : []);

    /// <summary>
    /// Determines whether the ZIP archive contains an entry with the exact path or relative name.
    /// </summary>
    /// <param name="entryPath">The entry path or name to search for.</param>
    /// <returns><see langword="true"/> if a matching entry is found; otherwise, <see langword="false"/>.</returns>
    public bool HasZipEntry(string entryPath) =>
        ZipDirectory?.TryFindEntry(entryPath, out _) == true;

    /// <summary>
    /// Determines whether the ZIP archive contains any entry ending with the specified suffix.
    /// </summary>
    /// <param name="suffix">The suffix to match against entry paths (case-insensitive).</param>
    /// <returns><see langword="true"/> if any entry ends with <paramref name="suffix"/>; otherwise, <see langword="false"/>.</returns>
    public bool HasZipEntryEndingWith(string suffix) =>
        ZipDirectory?.TryFindEntry(suffix, out _) == true;

    /// <summary>
    /// Determines whether the ZIP archive contains any entry containing the specified substring.
    /// </summary>
    /// <param name="substring">The case-insensitive substring to search for in entry paths.</param>
    /// <returns><see langword="true"/> if any entry contains <paramref name="substring"/>; otherwise, <see langword="false"/>.</returns>
    public bool ContainsZipEntry(string substring) =>
        ZipDirectory?.ContainsEntry(substring) == true;

    /// <summary>
    /// Attempts to locate a ZIP entry matching the specified suffix or relative path.
    /// </summary>
    /// <param name="entryPath">The entry path or suffix to search for.</param>
    /// <param name="entry">When this method returns, contains the matching <see cref="ZipEntry"/> if found; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the entry was found; otherwise, <see langword="false"/>.</returns>
    internal bool TryGetZipEntry(string entryPath, [NotNullWhen(true)] out ZipEntry? entry)
    {
        if (ZipDirectory is not null && ZipDirectory.TryFindEntry(entryPath, out entry))
        {
            return true;
        }

        entry = null;
        return false;
    }

    /// <summary>
    /// Creates a new <see cref="ModelProbe"/> by peeking up to 64 bytes from the beginning of <paramref name="stream"/> and restoring its position.
    /// </summary>
    /// <param name="stream">The readable, seekable model stream to inspect.</param>
    /// <param name="fileName">An optional file name or path hint.</param>
    /// <returns>A newly initialized <see cref="ModelProbe"/> instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ModelParseException"><paramref name="stream"/> is not readable or not seekable.</exception>
    public static ModelProbe Create(Stream stream, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateSeekableStream(stream);

        long initialPosition = stream.Position;
        long length = stream.Length;
        byte[] header = new byte[HeaderPeekByteCount];
        int bytesRead = 0;

        try
        {
            stream.Position = 0;
            while (bytesRead < HeaderPeekByteCount)
            {
                int n = stream.Read(header, bytesRead, HeaderPeekByteCount - bytesRead);
                if (n == 0)
                {
                    break;
                }

                bytesRead += n;
            }
        }
        finally
        {
            stream.Position = initialPosition;
        }

        return new ModelProbe(stream, initialPosition, length, header, bytesRead, fileName);
    }

    /// <summary>
    /// Asynchronously creates a new <see cref="ModelProbe"/> by peeking up to 64 bytes from the beginning of <paramref name="stream"/> and restoring its position.
    /// </summary>
    /// <param name="stream">The readable, seekable model stream to inspect.</param>
    /// <param name="fileName">An optional file name or path hint.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous probe operation and wraps the initialized <see cref="ModelProbe"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ModelParseException"><paramref name="stream"/> is not readable or not seekable.</exception>
    public static async ValueTask<ModelProbe> CreateAsync(
        Stream stream,
        string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateSeekableStream(stream);

        long initialPosition = stream.Position;
        long length = stream.Length;
        byte[] header = new byte[HeaderPeekByteCount];
        int bytesRead = 0;

        try
        {
            stream.Position = 0;
            while (bytesRead < HeaderPeekByteCount)
            {
                int n = await stream.ReadAsync(header.AsMemory(bytesRead, HeaderPeekByteCount - bytesRead), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                bytesRead += n;
            }
        }
        finally
        {
            stream.Position = initialPosition;
        }

        return new ModelProbe(stream, initialPosition, length, header, bytesRead, fileName);
    }

    /// <summary>
    /// Reads bytes from <see cref="Stream"/> at the specified <paramref name="offset"/> into <paramref name="destination"/> and restores <see cref="Stream.Position"/>.
    /// </summary>
    /// <param name="offset">The zero-based byte offset in the stream from which to begin reading.</param>
    /// <param name="destination">The span that receives the bytes read from the stream.</param>
    /// <returns>The total number of bytes read into <paramref name="destination"/>.</returns>
    public int ReadAt(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= StreamLength || destination.IsEmpty)
        {
            return 0;
        }

        try
        {
            _stream.Position = offset;
            int totalRead = 0;
            while (totalRead < destination.Length)
            {
                int read = _stream.Read(destination[totalRead..]);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return totalRead;
        }
        finally
        {
            _stream.Position = _initialPosition;
        }
    }

    /// <summary>
    /// Restores <see cref="Stream.Position"/> to the position it held when this <see cref="ModelProbe"/> was created.
    /// </summary>
    public void RestorePosition()
    {
        _stream.Position = _initialPosition;
    }

    private static void ValidateSeekableStream(Stream stream)
    {
        if (!stream.CanRead)
        {
            throw new ModelParseException("Model stream must be readable.");
        }

        if (!stream.CanSeek)
        {
            throw new ModelParseException("Model stream must support seeking for non-destructive header probing and lazy weight slicing.");
        }
    }

    private static string? ExtractNormalizedExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        string ext = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(ext) ? null : ext.ToLowerInvariant();
    }

    private ZipDirectory? ReadZipDirectoryNonDestructively()
    {
        if (!IsZipArchive || StreamLength < 22)
        {
            return null;
        }

        try
        {
            RandomAccessSource source = new(_stream);
            if (ZipDirectory.TryRead(source, out ZipDirectory? dir))
            {
                return dir;
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _stream.Position = _initialPosition;
        }
    }
}
