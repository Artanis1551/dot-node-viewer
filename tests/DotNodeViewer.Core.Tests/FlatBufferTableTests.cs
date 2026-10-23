using System.Buffers.Binary;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.IO.FlatBuffers;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Tests;

/// <summary>
/// Verifies <see cref="FlatBufferTable"/> vtable decoding, vector reading, byte slice capture, and out-of-bounds offset protection.
/// </summary>
public sealed class FlatBufferTableTests
{
    [Test]
    public async Task ReadRootAndFields_ValidFlatBuffer_DecodesScalarsStringsVectorsAndCapturesByteSlice()
    {
        // Construct a minimal FlatBuffer in memory:
        // [0..4]: root uoffset = 20
        // [4..8]: identifier "TFL3"
        // [8..20]: vtable (len=12, tableInlineSize=20, field0=4(uint32), field1=8(string uoffset), field2=12(int32[] uoffset), field3=16(byte[] uoffset))
        // [20..40]: root table (soffset=12 -> vtable at 8, field0=3u, field1->40, field2->52, field3->68)
        // [40..52]: string "hello" (len=5, 'h','e','l','l','o', 0, pad)
        // [52..68]: int32 vector [10, 20, 30] (count=3, 10, 20, 30)
        // [68..88]: byte vector of 16 bytes (count=16, 16 payload bytes at offset 72)
        byte[] buffer = new byte[88];
        Span<byte> span = buffer;

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], 20u);
        "TFL3"u8.CopyTo(span[4..8]);

        // vtable at offset 8
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..10], 12); // vtable byte length
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..12], 20); // table inline size
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..14], 4); // field 0 at +4
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..16], 8); // field 1 at +8
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..18], 12); // field 2 at +12
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..20], 16); // field 3 at +16

        // root table at offset 20
        BinaryPrimitives.WriteInt32LittleEndian(span[20..24], 12); // soffset: 20 - 12 = 8
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..28], 3u); // field 0: version = 3
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..32], 12u); // field 1 at 28 + 12 = 40
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..36], 20u); // field 2 at 32 + 20 = 52
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..40], 32u); // field 3 at 36 + 32 = 68

        // string at offset 40
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..44], 5u);
        Encoding.UTF8.GetBytes("hello", span[44..49]);

        // int32 vector at offset 52
        BinaryPrimitives.WriteUInt32LittleEndian(span[52..56], 3u);
        BinaryPrimitives.WriteInt32LittleEndian(span[56..60], 10);
        BinaryPrimitives.WriteInt32LittleEndian(span[60..64], 20);
        BinaryPrimitives.WriteInt32LittleEndian(span[64..68], 30);

        // byte vector at offset 68 (payload at 72..88)
        BinaryPrimitives.WriteUInt32LittleEndian(span[68..72], 16u);
        for (int i = 0; i < 16; i++)
        {
            span[72 + i] = (byte)(i + 1);
        }

        using MemoryStream stream = new(buffer);
        using RandomAccessSource source = new(stream);

        FlatBufferTable root = FlatBufferTable.ReadRoot(source, 0, "TFL3"u8);
        await Assert.That(root.ReadUInt32(0)).IsEqualTo(3u);
        await Assert.That(root.ReadString(1)).IsEqualTo("hello");

        int[]? ints = root.ReadInt32Vector(2);
        await Assert.That(ints).IsNotNull();
        await Assert.That(ints!.Length).IsEqualTo(3);
        await Assert.That(ints[0]).IsEqualTo(10);
        await Assert.That(ints[1]).IsEqualTo(20);
        await Assert.That(ints[2]).IsEqualTo(30);

        TensorDataSlice? slice = root.CaptureByteVectorSlice(3, DataType.Int8);
        await Assert.That(slice).IsNotNull();
        await Assert.That(slice!.StreamOffset).IsEqualTo(72L);
        await Assert.That(slice.ByteLength).IsEqualTo(16L);
        await Assert.That(slice.DataType).IsEqualTo(DataType.Int8);

        // Absent field returns default
        await Assert.That(root.HasField(9)).IsFalse();
        await Assert.That(root.ReadInt32(9, defaultValue: 42)).IsEqualTo(42);
    }

    [Test]
    public async Task ReadRootAndFields_CorruptedOffsetsOrWrongIdentifier_ThrowsModelParseException()
    {
        // Wrong file identifier
        byte[] badId = [0x0C, 0x00, 0x00, 0x00, (byte)'B', (byte)'A', (byte)'D', (byte)'!', 0, 0, 0, 0];
        using MemoryStream s1 = new(badId);
        using RandomAccessSource src1 = new(s1);
        await Assert.That(() => FlatBufferTable.ReadRoot(src1, 0, "TFL3"u8)).Throws<ModelParseException>();

        // Out-of-bounds root table offset
        byte[] badRoot = [0xFF, 0x00, 0x00, 0x00, (byte)'T', (byte)'F', (byte)'L', (byte)'3', 0, 0, 0, 0];
        using MemoryStream s2 = new(badRoot);
        using RandomAccessSource src2 = new(s2);
        await Assert.That(() => FlatBufferTable.ReadRoot(src2, 0, "TFL3"u8)).Throws<ModelParseException>();

        // Out-of-bounds vtable soffset
        byte[] badVtable = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(badVtable.AsSpan(0, 4), 8u);
        "TFL3"u8.CopyTo(badVtable.AsSpan(4, 4));
        BinaryPrimitives.WriteInt32LittleEndian(badVtable.AsSpan(8, 4), 100); // 8 - 100 = -92
        using MemoryStream s3 = new(badVtable);
        using RandomAccessSource src3 = new(s3);
        await Assert.That(() => FlatBufferTable.ReadRoot(src3, 0, "TFL3"u8)).Throws<ModelParseException>();
    }
}
