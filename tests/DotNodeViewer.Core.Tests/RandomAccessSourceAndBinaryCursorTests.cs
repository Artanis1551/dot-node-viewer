using System.Buffers.Binary;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;

namespace DotNodeViewer.Core.Tests;

/// <summary>
/// Verifies <see cref="RandomAccessSource"/> LRU 64 KB page caching and <see cref="BinaryCursor"/> bounds-checked reading.
/// </summary>
public sealed class RandomAccessSourceAndBinaryCursorTests
{
    [Test]
    public async Task RandomAccessSource_MultiPageReadsAndLruEviction_ReadsAccuratelyAndBoundsChecks()
    {
        // Create 3 pages (192 KB) of deterministic data with a 2-page LRU cache capacity.
        int totalLength = RandomAccessSource.PageSize * 3;
        byte[] backing = new byte[totalLength];
        for (int i = 0; i < backing.Length; i++)
        {
            backing[i] = (byte)((i * 31 + 7) & 0xFF);
        }

        BinaryPrimitives.WriteInt32LittleEndian(backing.AsSpan(RandomAccessSource.PageSize - 2, 4), 0x12345678);

        using MemoryStream stream = new(backing);
        using RandomAccessSource source = new(stream, maxPages: 2);

        await Assert.That(source.Length).IsEqualTo(totalLength);

        // Read within page 0 twice -> 1 miss, 1 hit.
        byte b0 = source.ReadByte(10);
        byte b1 = source.ReadByte(20);
        await Assert.That(b0).IsEqualTo(backing[10]);
        await Assert.That(b1).IsEqualTo(backing[20]);
        await Assert.That(source.CacheMissCount).IsEqualTo(1);
        await Assert.That(source.CacheHitCount).IsEqualTo(1);

        // Read across page 0 -> page 1 boundary.
        int crossBoundaryValue = source.ReadInt32LittleEndian(RandomAccessSource.PageSize - 2);
        await Assert.That(crossBoundaryValue).IsEqualTo(0x12345678);

        // Touch page 2 (evicts least-recently-used page).
        byte bPage2 = source.ReadByte(RandomAccessSource.PageSize * 2 + 5);
        await Assert.That(bPage2).IsEqualTo(backing[RandomAccessSource.PageSize * 2 + 5]);

        // Out-of-bounds read must throw ModelParseException with FileOffset.
        await Assert.That(() => source.ReadInt32LittleEndian(totalLength - 2))
            .Throws<ModelParseException>();
    }

    [Test]
    public async Task BinaryCursor_PrimitivesStringsAndOverrun_EnforcesBoundsWithFileOffset()
    {
        byte[] buffer = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0, 2), 0xABCD);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(2, 4), 0x01020304);
        "ONNX"u8.CopyTo(buffer.AsSpan(6, 4));
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(10, 4), 2.5f);

        long initialOffset;
        ushort u16;
        int i32;
        string str;
        float f32;
        int remaining;
        ModelParseException? caught = null;

        {
            BinaryCursor cursor = new(buffer, baseFileOffset: 0x100);
            initialOffset = cursor.FileOffset;
            u16 = cursor.ReadUInt16LittleEndian();
            i32 = cursor.ReadInt32BigEndian();
            str = cursor.ReadUtf8String(4);
            f32 = cursor.ReadSingleLittleEndian();
            remaining = cursor.Remaining;

            try
            {
                _ = cursor.ReadInt32LittleEndian();
            }
            catch (ModelParseException ex)
            {
                caught = ex;
            }
        }

        await Assert.That(initialOffset).IsEqualTo(0x100);
        await Assert.That(u16).IsEqualTo((ushort)0xABCD);
        await Assert.That(i32).IsEqualTo(0x01020304);
        await Assert.That(str).IsEqualTo("ONNX");
        await Assert.That(f32).IsEqualTo(2.5f);
        await Assert.That(remaining).IsEqualTo(2);
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.FileOffset).IsEqualTo(0x10E);
    }
}
