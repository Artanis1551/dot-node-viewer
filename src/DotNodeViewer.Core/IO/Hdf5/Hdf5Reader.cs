using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.IO.Hdf5;

/// <summary>
/// Represents a dataset discovered inside an HDF5 container.
/// </summary>
/// <param name="Name">The dataset name.</param>
/// <param name="Path">The hierarchical path within the HDF5 group tree.</param>
/// <param name="Shape">The dataset shape.</param>
/// <param name="DataType">The mapped element data type.</param>
/// <param name="StreamOffset">The absolute offset within the source stream where the raw payload begins.</param>
/// <param name="ByteLength">The size of the dataset payload in bytes.</param>
/// <param name="IsContiguous"><see langword="true"/> if the dataset is stored as contiguous raw bytes; otherwise, <see langword="false"/>.</param>
/// <param name="LayerName">The optional associated Keras layer name.</param>
internal sealed record Hdf5Dataset(
    string Name,
    string Path,
    TensorShape Shape,
    DataType DataType,
    long StreamOffset,
    long ByteLength,
    bool IsContiguous,
    string? LayerName = null);

/// <summary>
/// Minimal read-only streaming parser for HDF5 containers over a <see cref="RandomAccessSource"/>.
/// </summary>
internal sealed class Hdf5File
{
    private static readonly byte[] Hdf5Magic = [0x89, 0x48, 0x44, 0x46, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly RandomAccessSource _source;
    private readonly long _baseOffset;
    private readonly byte _sizeOfOffsets;
    private readonly byte _sizeOfLengths;
    private readonly Dictionary<string, string> _attributes = new(StringComparer.Ordinal);
    private readonly List<Hdf5Dataset> _datasets = [];

    /// <summary>
    /// Gets all string attributes discovered on the root or top-level group objects.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attributes => _attributes;

    /// <summary>
    /// Gets all datasets discovered in the file.
    /// </summary>
    public IReadOnlyList<Hdf5Dataset> Datasets => _datasets;

    private Hdf5File(RandomAccessSource source, long baseOffset, byte sizeOfOffsets, byte sizeOfLengths)
    {
        _source = source;
        _baseOffset = baseOffset;
        _sizeOfOffsets = sizeOfOffsets;
        _sizeOfLengths = sizeOfLengths;
    }

    /// <summary>
    /// Attempts to open an HDF5 container at <paramref name="baseOffset"/> within <paramref name="source"/>.
    /// </summary>
    public static bool TryOpen(
        RandomAccessSource source,
        long baseOffset,
        [NotNullWhen(true)] out Hdf5File? file)
    {
        file = null;
        if (source.Length < baseOffset + 8)
        {
            return false;
        }

        try
        {
            Span<byte> magic = stackalloc byte[8];
            source.ReadExactly(baseOffset, magic);
            if (!magic.SequenceEqual(Hdf5Magic))
            {
                return false;
            }

            file = Open(source, baseOffset);
            return true;
        }
        catch
        {
            file = null;
            return false;
        }
    }

    /// <summary>
    /// Opens and parses an HDF5 container at <paramref name="baseOffset"/>.
    /// </summary>
    public static Hdf5File Open(RandomAccessSource source, long baseOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Length < baseOffset + 32)
        {
            throw new ModelParseException("File is too small to be a valid HDF5 container.", fileOffset: baseOffset);
        }

        Span<byte> magic = stackalloc byte[8];
        source.ReadExactly(baseOffset, magic);
        if (!magic.SequenceEqual(Hdf5Magic))
        {
            throw new ModelParseException("Invalid HDF5 file signature.", fileOffset: baseOffset);
        }

        byte sbVersion = source.ReadByte(baseOffset + 8);
        byte sizeOfOffsets;
        byte sizeOfLengths;
        long rootObjHeaderAddr;

        if (sbVersion == 0 || sbVersion == 1)
        {
            sizeOfOffsets = source.ReadByte(baseOffset + 13);
            sizeOfLengths = source.ReadByte(baseOffset + 14);
            if (sizeOfOffsets != 8)
            {
                throw new ModelParseException($"Unsupported HDF5 offset size: {sizeOfOffsets}", fileOffset: baseOffset + 13);
            }

            long rootSymEntryOffset = baseOffset + 24 + 4 * sizeOfOffsets;
            rootObjHeaderAddr = baseOffset + (long)source.ReadUInt64LittleEndian(rootSymEntryOffset + 8);
        }
        else if (sbVersion == 2 || sbVersion == 3)
        {
            sizeOfOffsets = source.ReadByte(baseOffset + 9);
            sizeOfLengths = source.ReadByte(baseOffset + 10);
            if (sizeOfOffsets != 8)
            {
                throw new ModelParseException($"Unsupported HDF5 offset size: {sizeOfOffsets}", fileOffset: baseOffset + 9);
            }

            rootObjHeaderAddr = baseOffset + (long)source.ReadUInt64LittleEndian(baseOffset + 36);
        }
        else
        {
            throw new ModelParseException($"Unsupported HDF5 superblock version: {sbVersion}", fileOffset: baseOffset + 8);
        }

        var hdf5 = new Hdf5File(source, baseOffset, sizeOfOffsets, sizeOfLengths);
        hdf5.ParseObject(rootObjHeaderAddr, string.Empty);
        return hdf5;
    }

    /// <summary>
    /// Finds a dataset by exact path or by suffix/leaf name.
    /// </summary>
    public Hdf5Dataset? FindDataset(string pathOrName)
    {
        for (int i = 0; i < _datasets.Count; i++)
        {
            if (string.Equals(_datasets[i].Path, pathOrName, StringComparison.Ordinal) ||
                string.Equals(_datasets[i].Name, pathOrName, StringComparison.Ordinal))
            {
                return _datasets[i];
            }
        }

        for (int i = 0; i < _datasets.Count; i++)
        {
            if (_datasets[i].Path.EndsWith("/" + pathOrName, StringComparison.OrdinalIgnoreCase))
            {
                return _datasets[i];
            }
        }

        return null;
    }

    private void ParseObject(long objAddr, string currentPath, string? activeLayerName = null)
    {
        if (objAddr <= 0 || objAddr >= _source.Length)
        {
            return;
        }

        byte version = _source.ReadByte(objAddr);
        if (version == 1)
        {
            ParseObjectHeaderV1(objAddr, currentPath, activeLayerName);
        }
        else if (version == (byte)'O' && _source.ReadByte(objAddr + 1) == (byte)'H' &&
                 _source.ReadByte(objAddr + 2) == (byte)'D' && _source.ReadByte(objAddr + 3) == (byte)'R')
        {
            ParseObjectHeaderV2(objAddr, currentPath);
        }
    }

    private void ParseObjectHeaderV1(long objAddr, string currentPath, string? activeLayerName = null)
    {
        ushort numMessages = _source.ReadUInt16LittleEndian(objAddr + 2);
        uint headerSize = _source.ReadUInt32LittleEndian(objAddr + 8);

        long blockStart = objAddr + 16;
        long blockEnd = blockStart + headerSize;

        var blocks = new Queue<(long Start, long End, int MaxMessages)>();
        blocks.Enqueue((blockStart, blockEnd, numMessages));

        TensorShape? shape = null;
        DataType? dataType = null;
        (long DataOffset, long DataSize)? layout = null;
        (long BtreeAddr, long HeapAddr)? symTable = null;
        string? layerNameFromAttr = activeLayerName;

        while (blocks.Count > 0)
        {
            var (start, end, maxMsgs) = blocks.Dequeue();
            long pos = start;
            int count = 0;

            while (pos + 8 <= end && count < maxMsgs)
            {
                ushort msgType = _source.ReadUInt16LittleEndian(pos);
                ushort msgSize = _source.ReadUInt16LittleEndian(pos + 2);
                byte msgFlags = _source.ReadByte(pos + 4);
                pos += 8;
                count++;

                if (msgType == 0 && msgSize == 0)
                {
                    break;
                }

                if (pos + msgSize > _source.Length)
                {
                    break;
                }

                switch (msgType)
                {
                    case 0x0010: // Continuation Message
                        if (msgSize >= 16)
                        {
                            long contOffset = _baseOffset + (long)_source.ReadUInt64LittleEndian(pos);
                            long contLen = (long)_source.ReadUInt64LittleEndian(pos + 8);
                            blocks.Enqueue((contOffset, contOffset + contLen, maxMsgs - count));
                        }
                        break;

                    case 0x0011: // Symbol Table Message
                        if (msgSize >= 16)
                        {
                            long btree = _baseOffset + (long)_source.ReadUInt64LittleEndian(pos);
                            long heap = _baseOffset + (long)_source.ReadUInt64LittleEndian(pos + 8);
                            symTable = (btree, heap);
                        }
                        break;

                    case 0x0001: // Dataspace Message
                        shape = ParseDataspace(pos);
                        break;

                    case 0x0003: // Datatype Message
                        dataType = ParseDatatype(pos);
                        break;

                    case 0x0008: // Data Layout Message
                        layout = ParseDataLayout(pos);
                        break;

                    case 0x000C: // Attribute Message
                        var (aName, aVal) = ParseAttribute(pos, msgSize, currentPath);
                        if (string.Equals(aName, "name", StringComparison.OrdinalIgnoreCase))
                        {
                            layerNameFromAttr = aVal;
                        }
                        break;
                }

                // Messages in v1 object headers are aligned to 8-byte boundaries
                int pad = (8 - (msgSize % 8)) % 8;
                pos += msgSize + pad;
            }
        }

        // If this object defines a dataset (dataspace + datatype + layout)
        if (layout.HasValue)
        {
            string dsName = currentPath.Contains('/', StringComparison.Ordinal)
                ? currentPath[(currentPath.LastIndexOf('/') + 1)..]
                : currentPath;

            var dsShape = shape ?? TensorShape.Scalar;
            var dsType = dataType ?? DataType.Float32;
            long dataOffset = _baseOffset + layout.Value.DataOffset;
            long dataLength = layout.Value.DataSize;

            _datasets.Add(new Hdf5Dataset(
                Name: dsName,
                Path: currentPath,
                Shape: dsShape,
                DataType: dsType,
                StreamOffset: dataOffset,
                ByteLength: dataLength,
                IsContiguous: true,
                LayerName: layerNameFromAttr));
        }

        // If this object is a group containing a symbol table, traverse child objects
        if (symTable.HasValue)
        {
            TraverseSymbolTable(symTable.Value.BtreeAddr, symTable.Value.HeapAddr, currentPath, layerNameFromAttr);
        }
    }

    private void ParseObjectHeaderV2(long objAddr, string currentPath)
    {
        byte flags = _source.ReadByte(objAddr + 5);
        long pos = objAddr + 6;
        if ((flags & 0x20) != 0) // Creation order present
        {
            pos += 2;
        }

        uint chunkSizeBytes = (flags & 0x03) switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            3 => 8,
            _ => 1
        };

        ulong chunkSize = ReadVariableLengthUInt(pos, chunkSizeBytes);
        pos += chunkSizeBytes;
        long end = pos + (long)chunkSize;

        while (pos + 4 <= end && pos + 4 <= _source.Length)
        {
            byte msgType = _source.ReadByte(pos);
            ushort msgSize = _source.ReadUInt16LittleEndian(pos + 1);
            byte msgFlags = _source.ReadByte(pos + 3);
            pos += 4;

            if (pos + msgSize > end)
            {
                break;
            }

            if (msgType == 0x01) // Dataspace
            {
                // Not standard for Keras models (Keras uses Superblock v0 / Header v1)
            }

            pos += msgSize;
        }
    }

    private TensorShape ParseDataspace(long pos)
    {
        byte rank = _source.ReadByte(pos + 1);
        if (rank == 0)
        {
            return TensorShape.Scalar;
        }

        long dimStart = pos + 8;
        var dims = new List<TensorDimension>(rank);
        for (int i = 0; i < rank; i++)
        {
            ulong dimVal = _source.ReadUInt64LittleEndian(dimStart + i * 8);
            dims.Add(new TensorDimension.Fixed((long)dimVal));
        }

        return new TensorShape(dims);
    }

    private DataType ParseDatatype(long pos)
    {
        byte classAndVersion = _source.ReadByte(pos);
        byte typeClass = (byte)(classAndVersion & 0x0F);
        uint byteSize = _source.ReadUInt32LittleEndian(pos + 4);

        if (typeClass == 1) // Floating-point
        {
            return byteSize switch
            {
                2 => DataType.Float16,
                4 => DataType.Float32,
                8 => DataType.Float64,
                _ => DataType.Float32
            };
        }

        if (typeClass == 0) // Fixed-point (integer)
        {
            byte flag1 = _source.ReadByte(pos + 1);
            bool isSigned = (flag1 & 0x08) != 0;
            return byteSize switch
            {
                1 => isSigned ? DataType.Int8 : DataType.UInt8,
                2 => isSigned ? DataType.Int16 : DataType.UInt16,
                4 => isSigned ? DataType.Int32 : DataType.UInt32,
                8 => isSigned ? DataType.Int64 : DataType.UInt64,
                _ => DataType.Int32
            };
        }

        return DataType.Float32;
    }

    private (long DataOffset, long DataSize)? ParseDataLayout(long pos)
    {
        byte version = _source.ReadByte(pos);
        byte layoutClass = _source.ReadByte(pos + 1);

        if (layoutClass == 1) // Contiguous
        {
            long dataAddr = (long)_source.ReadUInt64LittleEndian(pos + 2);
            long dataSize = (long)_source.ReadUInt64LittleEndian(pos + 10);
            return (dataAddr, dataSize);
        }

        return null;
    }

    private (string Name, string Value) ParseAttribute(long pos, ushort msgSize, string currentPath)
    {
        ushort nameSize = _source.ReadUInt16LittleEndian(pos + 2);
        ushort typeSize = _source.ReadUInt16LittleEndian(pos + 4);
        ushort spaceSize = _source.ReadUInt16LittleEndian(pos + 6);

        long nameOffset = pos + 8;
        string attrName = ReadNullTerminatedString(nameOffset, nameSize);

        int nPad = (8 - (nameSize % 8)) % 8;
        long typeOffset = nameOffset + nameSize + nPad;

        int tPad = (8 - (typeSize % 8)) % 8;
        long spaceOffset = typeOffset + typeSize + tPad;

        int sPad = (8 - (spaceSize % 8)) % 8;
        long valOffset = spaceOffset + spaceSize + sPad;

        long valLen = (pos + msgSize) - valOffset;
        if (valLen <= 0)
        {
            return (attrName, string.Empty);
        }

        // Variable-Length String (hvl_t: length uint32, colAddr uint64, objIndex uint32)
        if (valLen >= 16)
        {
            uint strLen = _source.ReadUInt32LittleEndian(valOffset);
            ulong colAddr = _source.ReadUInt64LittleEndian(valOffset + 4);
            uint objIdx = _source.ReadUInt32LittleEndian(valOffset + 12);

            if (colAddr > 0 && colAddr + 16 <= (ulong)_source.Length)
            {
                string? vlenStr = ReadGlobalHeapString(_baseOffset + (long)colAddr, objIdx);
                if (vlenStr != null)
                {
                    _attributes[attrName] = vlenStr;
                    return (attrName, vlenStr);
                }
            }
        }

        // Fixed-Length String fallback
        string fixedStr = ReadNullTerminatedString(valOffset, (int)valLen);
        if (!string.IsNullOrEmpty(fixedStr))
        {
            _attributes[attrName] = fixedStr;
        }

        return (attrName, fixedStr);
    }

    private string? ReadGlobalHeapString(long colAddr, uint targetObjIdx)
    {
        if (colAddr + 16 > _source.Length)
        {
            return null;
        }

        Span<byte> gcolSig = stackalloc byte[4];
        _source.ReadExactly(colAddr, gcolSig);
        if (gcolSig[0] != (byte)'G' || gcolSig[1] != (byte)'C' ||
            gcolSig[2] != (byte)'O' || gcolSig[3] != (byte)'L')
        {
            return null;
        }

        ulong colSize = _source.ReadUInt64LittleEndian(colAddr + 8);
        long pos = colAddr + 16;
        long end = colAddr + (long)colSize;

        while (pos + 16 <= end && pos + 16 <= _source.Length)
        {
            ushort objIdx = _source.ReadUInt16LittleEndian(pos);
            ulong objSize = _source.ReadUInt64LittleEndian(pos + 8);
            pos += 16;

            if (objIdx == targetObjIdx)
            {
                if (pos + (long)objSize > _source.Length)
                {
                    return null;
                }

                byte[] buffer = new byte[objSize];
                _source.ReadExactly(pos, buffer);
                int len = Array.IndexOf(buffer, (byte)0);
                if (len < 0)
                {
                    len = buffer.Length;
                }

                return Encoding.UTF8.GetString(buffer, 0, len);
            }

            int pad = (8 - ((int)objSize % 8)) % 8;
            pos += (long)objSize + pad;
        }

        return null;
    }

    private void TraverseSymbolTable(long btreeAddr, long heapAddr, string currentPath, string? activeLayerName = null)
    {
        if (btreeAddr <= 0 || btreeAddr + 24 > _source.Length || heapAddr <= 0 || heapAddr + 32 > _source.Length)
        {
            return;
        }

        Span<byte> treeSig = stackalloc byte[4];
        _source.ReadExactly(btreeAddr, treeSig);
        if (treeSig[0] != (byte)'T' || treeSig[1] != (byte)'R' ||
            treeSig[2] != (byte)'E' || treeSig[3] != (byte)'E')
        {
            return;
        }

        ushort numEntries = _source.ReadUInt16LittleEndian(btreeAddr + 6);
        long entriesStart = btreeAddr + 24;

        // Traverse each B-tree entry
        for (int i = 0; i < numEntries; i++)
        {
            long entryPos = entriesStart + i * 16;
            if (entryPos + 16 > _source.Length)
            {
                break;
            }

            long childNodeAddr = _baseOffset + (long)_source.ReadUInt64LittleEndian(entryPos + 8);
            ParseSymbolTableNode(childNodeAddr, heapAddr, currentPath, activeLayerName);
        }
    }

    private void ParseSymbolTableNode(long snodAddr, long heapAddr, string currentPath, string? activeLayerName = null)
    {
        if (snodAddr <= 0 || snodAddr + 8 > _source.Length)
        {
            return;
        }

        Span<byte> snodSig = stackalloc byte[4];
        _source.ReadExactly(snodAddr, snodSig);
        if (snodSig[0] != (byte)'S' || snodSig[1] != (byte)'N' ||
            snodSig[2] != (byte)'O' || snodSig[3] != (byte)'D')
        {
            return;
        }

        ushort numSymbols = _source.ReadUInt16LittleEndian(snodAddr + 6);
        long symbolsStart = snodAddr + 8;

        for (int s = 0; s < numSymbols; s++)
        {
            long symPos = symbolsStart + s * 40;
            if (symPos + 16 > _source.Length)
            {
                break;
            }

            ulong nameOffset = _source.ReadUInt64LittleEndian(symPos);
            long objAddr = _baseOffset + (long)_source.ReadUInt64LittleEndian(symPos + 8);

            string childName = ReadLocalHeapString(heapAddr, (long)nameOffset);
            string childPath = string.IsNullOrEmpty(currentPath) ? childName : $"{currentPath}/{childName}";

            ParseObject(objAddr, childPath, activeLayerName);
        }
    }

    private string ReadLocalHeapString(long heapAddr, long nameOffset)
    {
        if (heapAddr + 32 > _source.Length)
        {
            return string.Empty;
        }

        long dataSegOffset = _baseOffset + (long)_source.ReadUInt64LittleEndian(heapAddr + 24);
        long strStart = dataSegOffset + nameOffset;

        return ReadNullTerminatedString(strStart, 256);
    }

    private string ReadNullTerminatedString(long startOffset, int maxLength)
    {
        if (startOffset >= _source.Length)
        {
            return string.Empty;
        }

        int max = (int)Math.Min(maxLength, _source.Length - startOffset);
        byte[] buffer = new byte[max];
        _source.ReadExactly(startOffset, buffer);

        int nullIdx = Array.IndexOf(buffer, (byte)0);
        int len = nullIdx >= 0 ? nullIdx : max;

        return Encoding.UTF8.GetString(buffer, 0, len);
    }

    private ulong ReadVariableLengthUInt(long offset, uint byteSize) => byteSize switch
    {
        1 => _source.ReadByte(offset),
        2 => _source.ReadUInt16LittleEndian(offset),
        4 => _source.ReadUInt32LittleEndian(offset),
        8 => _source.ReadUInt64LittleEndian(offset),
        _ => _source.ReadByte(offset)
    };
}
