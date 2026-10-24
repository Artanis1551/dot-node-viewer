using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text;
using DotNodeViewer.Core.Abstractions;

namespace DotNodeViewer.Core.IO.Zip;

/// <summary>
/// Parses and queries ZIP archive directory headers over a <see cref="RandomAccessSource"/> without reading entry payloads.
/// </summary>
internal sealed class ZipDirectory
{
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralDirectorySignature = 0x02014b50;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EocdLocatorSignature = 0x07064b50;
    private const uint Zip64EocdRecordSignature = 0x06064b50;

    private readonly RandomAccessSource _source;
    private readonly Dictionary<string, ZipEntry> _entries;

    /// <summary>
    /// Gets the entries present in the ZIP archive indexed by path.
    /// </summary>
    public IReadOnlyDictionary<string, ZipEntry> Entries => _entries;

    /// <summary>
    /// Gets the detected common archive prefix directory (e.g., <c>"archive/"</c> or <c>"tiny_cnn/"</c>), or empty string.
    /// </summary>
    public string ArchivePrefix { get; }

    private ZipDirectory(RandomAccessSource source, Dictionary<string, ZipEntry> entries, string archivePrefix)
    {
        _source = source;
        _entries = entries;
        ArchivePrefix = archivePrefix;
    }

    /// <summary>
    /// Attempts to parse a ZIP directory from <paramref name="source"/>, returning <see langword="false"/> if not a valid ZIP file.
    /// </summary>
    public static bool TryRead(RandomAccessSource source, [NotNullWhen(true)] out ZipDirectory? directory)
    {
        directory = null;
        if (source.Length < 22)
        {
            return false;
        }

        try
        {
            directory = Read(source);
            return true;
        }
        catch (ModelParseException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a ZIP directory from <paramref name="source"/> or throws <see cref="ModelParseException"/> if malformed.
    /// </summary>
    public static ZipDirectory Read(RandomAccessSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        long fileLength = source.Length;
        if (fileLength < 22)
        {
            throw new ModelParseException("File is too small to be a valid ZIP archive.", fileOffset: 0);
        }

        long eocdOffset = FindEocdOffset(source, fileLength);
        if (eocdOffset < 0)
        {
            throw new ModelParseException("End of Central Directory (EOCD) signature not found.", fileOffset: fileLength);
        }

        ushort diskNumber = source.ReadUInt16LittleEndian(eocdOffset + 4);
        ushort cdStartDisk = source.ReadUInt16LittleEndian(eocdOffset + 6);
        ushort numEntriesThisDisk = source.ReadUInt16LittleEndian(eocdOffset + 8);
        ushort totalEntries16 = source.ReadUInt16LittleEndian(eocdOffset + 10);
        uint cdSize32 = source.ReadUInt32LittleEndian(eocdOffset + 12);
        uint cdOffset32 = source.ReadUInt32LittleEndian(eocdOffset + 16);

        long totalEntries = totalEntries16;
        long cdOffset = cdOffset32;
        long cdSize = cdSize32;

        // Check for ZIP64 EOCD Locator if any 16/32-bit field saturated or if locator is present
        long zip64LocatorOffset = eocdOffset - 20;
        if (zip64LocatorOffset >= 0 && source.ReadUInt32LittleEndian(zip64LocatorOffset) == Zip64EocdLocatorSignature)
        {
            long zip64EocdOffset = (long)source.ReadUInt64LittleEndian(zip64LocatorOffset + 8);
            if (zip64EocdOffset >= 0 && zip64EocdOffset + 56 <= fileLength &&
                source.ReadUInt32LittleEndian(zip64EocdOffset) == Zip64EocdRecordSignature)
            {
                totalEntries = (long)source.ReadUInt64LittleEndian(zip64EocdOffset + 32);
                cdSize = (long)source.ReadUInt64LittleEndian(zip64EocdOffset + 40);
                cdOffset = (long)source.ReadUInt64LittleEndian(zip64EocdOffset + 48);
            }
        }

        if (cdOffset < 0 || cdOffset + cdSize > fileLength)
        {
            throw new ModelParseException(
                $"ZIP Central Directory offset 0x{cdOffset:X} or size 0x{cdSize:X} exceeds file bounds.",
                fileOffset: cdOffset);
        }

        var entries = new Dictionary<string, ZipEntry>(StringComparer.Ordinal);
        long curCdPos = cdOffset;
        for (long i = 0; i < totalEntries && curCdPos + 46 <= fileLength; i++)
        {
            uint sig = source.ReadUInt32LittleEndian(curCdPos);
            if (sig != CentralDirectorySignature)
            {
                break;
            }

            ushort flags = source.ReadUInt16LittleEndian(curCdPos + 8);
            ushort compressionMethod = source.ReadUInt16LittleEndian(curCdPos + 10);
            uint crc32 = source.ReadUInt32LittleEndian(curCdPos + 16);
            uint compSize32 = source.ReadUInt32LittleEndian(curCdPos + 20);
            uint uncompSize32 = source.ReadUInt32LittleEndian(curCdPos + 24);
            ushort nameLen = source.ReadUInt16LittleEndian(curCdPos + 28);
            ushort extraLen = source.ReadUInt16LittleEndian(curCdPos + 30);
            ushort commentLen = source.ReadUInt16LittleEndian(curCdPos + 32);
            uint localOffset32 = source.ReadUInt32LittleEndian(curCdPos + 42);

            long compSize = compSize32;
            long uncompSize = uncompSize32;
            long localHeaderOffset = localOffset32;

            string name = source.ReadUtf8String(curCdPos + 46, nameLen);

            // Parse ZIP64 extra field if present
            if (extraLen > 0)
            {
                ParseZip64Extra(source, curCdPos + 46 + nameLen, extraLen, ref uncompSize, ref compSize, ref localHeaderOffset);
            }

            // Read Local File Header to resolve exact DataOffset
            long dataOffset = ResolveDataOffset(source, localHeaderOffset);

            var entry = new ZipEntry(
                name: name,
                compressionMethod: compressionMethod,
                compressedSize: compSize,
                uncompressedSize: uncompSize,
                crc32: crc32,
                localHeaderOffset: localHeaderOffset,
                dataOffset: dataOffset);

            entries[name] = entry;

            curCdPos += 46 + nameLen + extraLen + commentLen;
        }

        string detectedPrefix = string.Empty;
        foreach (string key in entries.Keys)
        {
            int slashIdx = key.IndexOf('/');
            if (slashIdx > 0)
            {
                string candidatePrefix = key.Substring(0, slashIdx + 1);
                if (key.EndsWith("data.pkl", StringComparison.Ordinal) || key.EndsWith("constants.pkl", StringComparison.Ordinal))
                {
                    detectedPrefix = candidatePrefix;
                    break;
                }
            }
        }

        return new ZipDirectory(source, entries, detectedPrefix);
    }

    private static long ResolveDataOffset(RandomAccessSource source, long localHeaderOffset)
    {
        if (localHeaderOffset < 0 || localHeaderOffset + 30 > source.Length)
        {
            throw new ModelParseException(
                $"ZIP local header offset 0x{localHeaderOffset:X} is out of file bounds.",
                fileOffset: localHeaderOffset);
        }

        uint sig = source.ReadUInt32LittleEndian(localHeaderOffset);
        if (sig != LocalHeaderSignature)
        {
            throw new ModelParseException(
                $"Invalid ZIP local file header signature 0x{sig:X8} at offset 0x{localHeaderOffset:X}.",
                fileOffset: localHeaderOffset);
        }

        ushort fnLen = source.ReadUInt16LittleEndian(localHeaderOffset + 26);
        ushort efLen = source.ReadUInt16LittleEndian(localHeaderOffset + 28);
        long dataOffset = localHeaderOffset + 30 + fnLen + efLen;

        if (dataOffset > source.Length)
        {
            throw new ModelParseException(
                $"ZIP entry data offset 0x{dataOffset:X} exceeds file bounds.",
                fileOffset: dataOffset);
        }

        return dataOffset;
    }

    private static void ParseZip64Extra(
        RandomAccessSource source,
        long extraStart,
        int extraLen,
        ref long uncompressedSize,
        ref long compressedSize,
        ref long localHeaderOffset)
    {
        long pos = extraStart;
        long end = extraStart + extraLen;
        while (pos + 4 <= end)
        {
            ushort tag = source.ReadUInt16LittleEndian(pos);
            ushort size = source.ReadUInt16LittleEndian(pos + 2);
            pos += 4;
            if (pos + size > end)
            {
                break;
            }

            if (tag == 0x0001) // ZIP64 Extended Information Extra Field
            {
                long readPos = pos;
                long extraEnd = pos + size;
                if (uncompressedSize == 0xFFFFFFFF && readPos + 8 <= extraEnd)
                {
                    uncompressedSize = (long)source.ReadUInt64LittleEndian(readPos);
                    readPos += 8;
                }
                if (compressedSize == 0xFFFFFFFF && readPos + 8 <= extraEnd)
                {
                    compressedSize = (long)source.ReadUInt64LittleEndian(readPos);
                    readPos += 8;
                }
                if (localHeaderOffset == 0xFFFFFFFF && readPos + 8 <= extraEnd)
                {
                    localHeaderOffset = (long)source.ReadUInt64LittleEndian(readPos);
                }
                break;
            }

            pos += size;
        }
    }

    private static long FindEocdOffset(RandomAccessSource source, long fileLength)
    {
        long searchWindow = Math.Min(fileLength, 65535 + 22);
        long searchStart = fileLength - searchWindow;

        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)searchWindow);
        try
        {
            Span<byte> slice = buffer.AsSpan(0, (int)searchWindow);
            source.ReadExactly(searchStart, slice);

            // Scan backwards for 0x06054b50 ('P', 'K', 0x05, 0x06)
            for (int i = (int)searchWindow - 22; i >= 0; i--)
            {
                if (slice[i] == 0x50 && slice[i + 1] == 0x4b && slice[i + 2] == 0x05 && slice[i + 3] == 0x06)
                {
                    return searchStart + i;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return -1;
    }

    /// <summary>
    /// Attempts to find an entry whose name exactly matches or ends with <paramref name="suffix"/>.
    /// </summary>
    public bool TryFindEntry(string suffix, [NotNullWhen(true)] out ZipEntry? entry)
    {
        if (_entries.TryGetValue(suffix, out entry))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(ArchivePrefix) && _entries.TryGetValue(ArchivePrefix + suffix, out entry))
        {
            return true;
        }

        foreach (var pair in _entries)
        {
            if (pair.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                entry = pair.Value;
                return true;
            }
        }

        entry = null;
        return false;
    }

    /// <summary>
    /// Checks whether any entry path in this archive contains the specified <paramref name="substring"/>.
    /// </summary>
    /// <param name="substring">The case-insensitive substring to search for in entry paths.</param>
    /// <returns><see langword="true"/> if any entry path contains <paramref name="substring"/>; otherwise, <see langword="false"/>.</returns>
    public bool ContainsEntry(string substring)
    {
        ArgumentNullException.ThrowIfNull(substring);
        foreach (var key in _entries.Keys)
        {
            if (key.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads and returns the entire uncompressed bytes of <paramref name="entry"/>.
    /// </summary>
    public byte[] ReadEntryBytes(ZipEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.UncompressedSize > int.MaxValue)
        {
            throw new ModelParseException(
                $"ZIP entry '{entry.Name}' size ({entry.UncompressedSize}) exceeds maximum memory buffer limit.",
                fileOffset: entry.DataOffset);
        }

        if (entry.UncompressedSize == 0)
        {
            return [];
        }

        if (entry.IsStored)
        {
            byte[] buffer = new byte[entry.UncompressedSize];
            _source.ReadExactly(entry.DataOffset, buffer);
            return buffer;
        }

        if (entry.CompressionMethod == 8) // Deflate
        {
            using var boundedStream = new BoundedRandomAccessStream(_source, entry.DataOffset, entry.CompressedSize);
            using var deflateStream = new DeflateStream(boundedStream, CompressionMode.Decompress, leaveOpen: false);
            byte[] dest = new byte[entry.UncompressedSize];
            deflateStream.ReadExactly(dest);
            return dest;
        }

        throw new ModelParseException(
            $"Unsupported ZIP compression method ({entry.CompressionMethod}) for entry '{entry.Name}'.",
            fileOffset: entry.DataOffset);
    }

    /// <summary>
    /// Reads and decodes the UTF-8 text contents of <paramref name="entry"/>.
    /// </summary>
    public string ReadEntryText(ZipEntry entry, Encoding? encoding = null)
    {
        byte[] bytes = ReadEntryBytes(entry);
        return (encoding ?? Encoding.UTF8).GetString(bytes);
    }

    /// <summary>
    /// Opens a readable <see cref="Stream"/> over the uncompressed payload of <paramref name="entry"/>.
    /// </summary>
    public Stream OpenEntryStream(ZipEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.IsStored)
        {
            return new BoundedRandomAccessStream(_source, entry.DataOffset, entry.UncompressedSize);
        }

        if (entry.CompressionMethod == 8) // Deflate
        {
            var boundedStream = new BoundedRandomAccessStream(_source, entry.DataOffset, entry.CompressedSize);
            return new DeflateStream(boundedStream, CompressionMode.Decompress, leaveOpen: false);
        }

        throw new ModelParseException(
            $"Unsupported ZIP compression method ({entry.CompressionMethod}) for entry '{entry.Name}'.",
            fileOffset: entry.DataOffset);
    }

    /// <summary>
    /// A non-owning, seekable, readable stream bounded to a specific range of a <see cref="RandomAccessSource"/>.
    /// </summary>
    private sealed class BoundedRandomAccessStream : Stream
    {
        private readonly RandomAccessSource _source;
        private readonly long _baseOffset;
        private readonly long _length;
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        public BoundedRandomAccessStream(RandomAccessSource source, long baseOffset, long length)
        {
            _source = source;
            _baseOffset = baseOffset;
            _length = length;
            _position = 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            if (_position >= _length || count == 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(count, _length - _position);
            _source.ReadExactly(_baseOffset + _position, buffer.AsSpan(offset, toRead));
            _position += toRead;
            return toRead;
        }

        public override int Read(Span<byte> destination)
        {
            if (_position >= _length || destination.IsEmpty)
            {
                return 0;
            }

            int toRead = (int)Math.Min(destination.Length, _length - _position);
            _source.ReadExactly(_baseOffset + _position, destination.Slice(0, toRead));
            _position += toRead;
            return toRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            ArgumentOutOfRangeException.ThrowIfNegative(newPos);
            _position = newPos;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
