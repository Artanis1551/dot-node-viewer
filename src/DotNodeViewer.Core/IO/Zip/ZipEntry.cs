using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text;
using DotNodeViewer.Core.Abstractions;

namespace DotNodeViewer.Core.IO.Zip;

/// <summary>
/// Represents a single file entry in a ZIP archive with exact container offsets.
/// </summary>
internal sealed class ZipEntry
{
    /// <summary>
    /// Gets the normalized entry path within the archive.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the compression method (0 = Stored, 8 = Deflated).
    /// </summary>
    public ushort CompressionMethod { get; }

    /// <summary>
    /// Gets the compressed size in bytes.
    /// </summary>
    public long CompressedSize { get; }

    /// <summary>
    /// Gets the uncompressed size in bytes.
    /// </summary>
    public long UncompressedSize { get; }

    /// <summary>
    /// Gets the CRC32 checksum.
    /// </summary>
    public uint Crc32 { get; }

    /// <summary>
    /// Gets the byte offset of the local file header in the underlying stream.
    /// </summary>
    public long LocalHeaderOffset { get; }

    /// <summary>
    /// Gets the absolute byte offset of the entry payload in the underlying stream.
    /// </summary>
    public long DataOffset { get; }

    /// <summary>
    /// Gets a value indicating whether this entry is stored uncompressed.
    /// </summary>
    public bool IsStored => CompressionMethod == 0;

    /// <summary>
    /// Gets a value indicating whether this entry represents a directory.
    /// </summary>
    public bool IsDirectory => Name.EndsWith('/') || Name.EndsWith('\\');

    /// <summary>
    /// Initializes a new instance of the <see cref="ZipEntry"/> class.
    /// </summary>
    public ZipEntry(
        string name,
        ushort compressionMethod,
        long compressedSize,
        long uncompressedSize,
        uint crc32,
        long localHeaderOffset,
        long dataOffset)
    {
        Name = name;
        CompressionMethod = compressionMethod;
        CompressedSize = compressedSize;
        UncompressedSize = uncompressedSize;
        Crc32 = crc32;
        LocalHeaderOffset = localHeaderOffset;
        DataOffset = dataOffset;
    }
}
