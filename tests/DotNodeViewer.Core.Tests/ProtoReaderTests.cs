using System.Buffers.Binary;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Tests;

/// <summary>
/// Verifies <see cref="ProtoReader"/> wire decoding, nested limits, slice capture, and malformed input handling.
/// </summary>
public sealed class ProtoReaderTests
{
    [Test]
    public async Task ReadPrimitivesAndNestedMessages_ValidWireBytes_DecodesExpectedValuesAndCapturesSlice()
    {
        using MemoryStream stream = new();
        // Field 1: varint int64 = 300 (tag 0x08, value 0xAC 0x02)
        stream.Write([0x08, 0xAC, 0x02]);

        // Field 2: string "onnx" (tag 0x12, length 4)
        stream.Write([0x12, 0x04, (byte)'o', (byte)'n', (byte)'n', (byte)'x']);

        // Field 3: float 1.5f (tag 0x1D = (3 << 3) | 5, 4 bytes LE)
        stream.WriteByte(0x1D);
        Span<byte> f32 = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(f32, 1.5f);
        stream.Write(f32);

        // Field 4: nested message (tag 0x22 = (4 << 3) | 2) containing:
        //   Field 1: negative int32 -1 (tag 0x08, 10-byte sign-extended varint)
        //   Field 9: raw_data slice of 16 bytes (tag 0x4A = (9 << 3) | 2, len 16, 16 bytes)
        byte[] nestedPayload =
        [
            0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01,
            0x4A, 0x10,
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16
        ];
        stream.WriteByte(0x22);
        stream.WriteByte((byte)nestedPayload.Length);
        long expectedSliceOffset = stream.Position + 13;
        stream.Write(nestedPayload);

        // Field 5: packed int64 [10, 20] (tag 0x2A = (5 << 3) | 2, len 2, 10, 20)
        stream.Write([0x2A, 0x02, 10, 20]);

        stream.Position = 0;
        using RandomAccessSource source = new(stream);
        ProtoReader reader = new(source);

        ProtoTag tag1 = reader.ReadTag();
        await Assert.That(tag1.FieldNumber).IsEqualTo(1u);
        await Assert.That(tag1.WireType).IsEqualTo(ProtoWireType.Varint);
        await Assert.That(reader.ReadInt64()).IsEqualTo(300L);

        ProtoTag tag2 = reader.ReadTag();
        await Assert.That(tag2.FieldNumber).IsEqualTo(2u);
        await Assert.That(tag2.WireType).IsEqualTo(ProtoWireType.LengthDelimited);
        await Assert.That(reader.ReadString()).IsEqualTo("onnx");

        ProtoTag tag3 = reader.ReadTag();
        await Assert.That(tag3.FieldNumber).IsEqualTo(3u);
        await Assert.That(tag3.WireType).IsEqualTo(ProtoWireType.Fixed32);
        await Assert.That(reader.ReadFloat()).IsEqualTo(1.5f);

        ProtoTag tag4 = reader.ReadTag();
        await Assert.That(tag4.FieldNumber).IsEqualTo(4u);
        long oldLimit = reader.PushMessageLimit();

        ProtoTag innerTag1 = reader.ReadTag();
        await Assert.That(innerTag1.FieldNumber).IsEqualTo(1u);
        await Assert.That(reader.ReadInt32()).IsEqualTo(-1);

        ProtoTag innerTag2 = reader.ReadTag();
        await Assert.That(innerTag2.FieldNumber).IsEqualTo(9u);
        TensorDataSlice slice = reader.CaptureLengthDelimitedSlice(DataType.Float32);
        await Assert.That(slice.StreamOffset).IsEqualTo(expectedSliceOffset);
        await Assert.That(slice.ByteLength).IsEqualTo(16L);
        await Assert.That(slice.DataType).IsEqualTo(DataType.Float32);
        await Assert.That(slice.Encoding).IsEqualTo(TensorDataEncoding.Raw);
        await Assert.That(reader.IsAtEnd).IsTrue();

        reader.PopLimit(oldLimit);

        ProtoTag tag5 = reader.ReadTag();
        await Assert.That(tag5.FieldNumber).IsEqualTo(5u);
        List<long> packedInts = [];
        reader.ReadRepeatedInt64(tag5.WireType, packedInts, "ints");
        await Assert.That(packedInts.Count).IsEqualTo(2);
        await Assert.That(packedInts[0]).IsEqualTo(10L);
        await Assert.That(packedInts[1]).IsEqualTo(20L);

        await Assert.That(reader.IsAtEnd).IsTrue();
        await Assert.That(reader.ReadTag().IsEnd).IsTrue();
    }

    [Test]
    public async Task ReadInvalidOrTruncatedWireBytes_ThrowsModelParseException()
    {
        // Truncated varint (MSB set on final byte)
        using MemoryStream truncatedVarint = new([0x08, 0x80]);
        using RandomAccessSource source1 = new(truncatedVarint);
        ProtoReader reader1 = new(source1);
        _ = reader1.ReadTag();
        await Assert.That(() => reader1.ReadInt64()).Throws<ModelParseException>();

        // Length prefix exceeds stream length
        using MemoryStream truncatedLength = new([0x12, 0x50, 0x01, 0x02]);
        using RandomAccessSource source2 = new(truncatedLength);
        ProtoReader reader2 = new(source2);
        _ = reader2.ReadTag();
        await Assert.That(() => reader2.ReadString()).Throws<ModelParseException>();

        // Invalid wire type 6
        using MemoryStream badWireType = new([(1 << 3) | 6]);
        using RandomAccessSource source3 = new(badWireType);
        ProtoReader reader3 = new(source3);
        await Assert.That(() => reader3.ReadTag()).Throws<ModelParseException>();
    }
}
