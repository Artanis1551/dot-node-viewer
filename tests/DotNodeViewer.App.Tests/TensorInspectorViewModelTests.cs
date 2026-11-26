using System.Buffers.Binary;
using DotNodeViewer.App.ViewModels;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.App.Tests;

/// <summary>
/// Unit tests for <see cref="TensorInspectorViewModel"/> verifying on-demand lazy reading,
/// byte-counting read bounding, multi-type decoding, and statistics computation.
/// </summary>
public sealed class TensorInspectorViewModelTests
{
    private sealed class ByteCountingStream : Stream
    {
        private readonly MemoryStream _inner;
        public long TotalBytesRead { get; private set; }

        public ByteCountingStream(byte[] buffer)
        {
            _inner = new MemoryStream(buffer);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            TotalBytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = _inner.Read(buffer);
            TotalBytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            TotalBytesRead += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    [Test]
    public async Task InspectAsync_ReadsExactlySliceLength_WhenSliceIsSmall()
    {
        // 16 floats (64 bytes)
        byte[] payload = new byte[64];
        for (int i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(i * 4), (float)i);
        }

        // Stream with 1024 bytes total, but slice is only 64 bytes at offset 128
        byte[] fullStreamBytes = new byte[1024];
        Array.Copy(payload, 0, fullStreamBytes, 128, 64);

        using ByteCountingStream stream = new(fullStreamBytes);

        TensorDataSlice slice = new(StreamOffset: 128, ByteLength: 64, DataType: DataType.Float32);
        TensorDescriptor desc = new(DataType: DataType.Float32, Shape: new TensorShape([new TensorDimension.Fixed(16)]));
        TensorEdge tensor = new("weights", desc, TensorKind.Initializer, Data: slice);

        TensorInspectorViewModel vm = new();
        await vm.InspectAsync(tensor, stream);

        // Byte count verification: must only read 64 bytes
        await Assert.That(stream.TotalBytesRead).IsEqualTo(64);
        await Assert.That(vm.HasData).IsTrue();
        await Assert.That(vm.IsExternal).IsFalse();
        await Assert.That(vm.ByteLength).IsEqualTo(64);
        await Assert.That(vm.StreamOffset).IsEqualTo(128);
        await Assert.That(vm.SampledElementCount).IsEqualTo(16);
        await Assert.That(vm.PreviewValues.Count).IsEqualTo(16);
        await Assert.That(vm.MinValue).IsEqualTo(0.0);
        await Assert.That(vm.MaxValue).IsEqualTo(15.0);
        await Assert.That(vm.MeanValue).IsEqualTo(7.5);
    }

    [Test]
    public async Task InspectAsync_CapsReadBuffer_WhenSliceExceeds64KB()
    {
        // 100 KB payload
        const int payloadLength = 100 * 1024;
        byte[] fullStreamBytes = new byte[payloadLength + 256];

        using ByteCountingStream stream = new(fullStreamBytes);

        TensorDataSlice slice = new(StreamOffset: 64, ByteLength: payloadLength, DataType: DataType.UInt8);
        TensorDescriptor desc = new(DataType: DataType.UInt8, Shape: new TensorShape([new TensorDimension.Fixed(payloadLength)]));
        TensorEdge tensor = new("large_tensor", desc, TensorKind.Initializer, Data: slice);

        TensorInspectorViewModel vm = new();
        await vm.InspectAsync(tensor, stream);

        // Max preview buffer is 64 KB (65536 bytes)
        await Assert.That(stream.TotalBytesRead).IsEqualTo(65536);
        await Assert.That(vm.SampledElementCount).IsEqualTo(65536);
        // Preview items capped at 64
        await Assert.That(vm.PreviewValues.Count).IsEqualTo(64);
    }

    [Test]
    public async Task InspectAsync_DecodesAffineQuantization_ComputesDequantizedValues()
    {
        // 4 INT8 quantized bytes: [-10, 0, 10, 20]
        sbyte[] rawValues = [-10, 0, 10, 20];
        byte[] streamBytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            streamBytes[i] = (byte)rawValues[i];
        }

        using ByteCountingStream stream = new(streamBytes);

        AffineQuantization affine = new([0.5f], [0]);
        TensorDataSlice slice = new(StreamOffset: 0, ByteLength: 4, DataType: DataType.Int8);
        TensorDescriptor desc = new(DataType: DataType.Int8, Shape: new TensorShape([new TensorDimension.Fixed(4)]), Quantization: affine);
        TensorEdge tensor = new("quant_conv_weights", desc, TensorKind.Initializer, Data: slice);

        TensorInspectorViewModel vm = new();
        await vm.InspectAsync(tensor, stream);

        await Assert.That(vm.HasDequantization).IsTrue();
        await Assert.That(vm.PreviewValues.Count).IsEqualTo(4);

        // Raw -10 -> dequantized -5.0
        await Assert.That(vm.PreviewValues[0].RawValue).IsEqualTo("-10");
        await Assert.That(vm.PreviewValues[0].DequantizedValue).IsEqualTo("-5");

        // Raw 0 -> dequantized 0.0
        await Assert.That(vm.PreviewValues[1].RawValue).IsEqualTo("0");
        await Assert.That(vm.PreviewValues[1].DequantizedValue).IsEqualTo("0");

        // Raw 10 -> dequantized 5.0
        await Assert.That(vm.PreviewValues[2].RawValue).IsEqualTo("10");
        await Assert.That(vm.PreviewValues[2].DequantizedValue).IsEqualTo("5");
    }

    [Test]
    public async Task InspectAsync_DecodesBFloat16Correctly()
    {
        // 2 BFloat16 elements: 1.0f (0x3F80) and 2.0f (0x4000)
        byte[] streamBytes = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(streamBytes.AsSpan(0), 0x3F80);
        BinaryPrimitives.WriteUInt16LittleEndian(streamBytes.AsSpan(2), 0x4000);

        using MemoryStream stream = new(streamBytes);

        TensorDataSlice slice = new(StreamOffset: 0, ByteLength: 4, DataType: DataType.BFloat16);
        TensorDescriptor desc = new(DataType: DataType.BFloat16, Shape: new TensorShape([new TensorDimension.Fixed(2)]));
        TensorEdge tensor = new("bf16_tensor", desc, TensorKind.Initializer, Data: slice);

        TensorInspectorViewModel vm = new();
        await vm.InspectAsync(tensor, stream);

        await Assert.That(vm.SampledElementCount).IsEqualTo(2);
        await Assert.That(vm.MinValue).IsEqualTo(1.0);
        await Assert.That(vm.MaxValue).IsEqualTo(2.0);
    }

    [Test]
    public async Task InspectAsync_ExternalTensorReference_DoesNotReadStream()
    {
        using ByteCountingStream stream = new(new byte[1024]);

        ExternalTensorDataReference ext = new("model.weights.bin", Offset: 4096, Length: 8192, DataType: DataType.Float32);
        TensorDescriptor desc = new(DataType: DataType.Float32, Shape: new TensorShape([new TensorDimension.Fixed(2048)]));
        TensorEdge tensor = new("external_weights", desc, TensorKind.Initializer, Data: ext);

        TensorInspectorViewModel vm = new();
        await vm.InspectAsync(tensor, stream);

        await Assert.That(vm.IsExternal).IsTrue();
        await Assert.That(vm.ExternalPath).IsEqualTo("model.weights.bin");
        await Assert.That(vm.ExternalOffset).IsEqualTo(4096);
        await Assert.That(vm.ExternalLength).IsEqualTo(8192);
        await Assert.That(stream.TotalBytesRead).IsEqualTo(0);
    }

    [Test]
    public async Task InspectAsync_DecodesFloat8E4M3_HandlesNormalsSubnormalsZerosMaxAndNaN()
    {
        // OCP FP8 E4M3FN test vectors:
        // 0x00 (+0), 0x80 (-0), 0x01 (2^-9 = 0.001953125), 0x07 (7*2^-9 = 0.013671875),
        // 0x08 (2^-6 = 0.015625), 0x38 (1.0), 0xB8 (-1.0), 0x3C (1.5),
        // 0x40 (2.0), 0x7E (448.0), 0xFE (-448.0), 0x7F (+NaN), 0xFF (-NaN)
        byte[] streamBytes = [0x00, 0x80, 0x01, 0x07, 0x08, 0x38, 0xB8, 0x3C, 0x40, 0x7E, 0xFE, 0x7F, 0xFF];
        using MemoryStream stream = new(streamBytes);

        TensorDataSlice slice = new(StreamOffset: 0, ByteLength: streamBytes.Length, DataType: DataType.Float8E4M3);
        TensorDescriptor desc = new(DataType: DataType.Float8E4M3, Shape: new TensorShape([new TensorDimension.Fixed(streamBytes.Length)]));
        TensorEdge tensor = new("fp8_e4m3_weights", desc, TensorKind.Initializer, Data: slice);

        TensorInspectorViewModel vm = new();
        await vm.InspectAsync(tensor, stream);

        await Assert.That(vm.SampledElementCount).IsEqualTo(13);
        await Assert.That(vm.PreviewValues.Count).IsEqualTo(13);
        await Assert.That(vm.HasStatistics).IsTrue();
        await Assert.That(vm.HasNaN).IsTrue();
        await Assert.That(vm.HasInfinity).IsFalse();
        await Assert.That(vm.MinValue).IsEqualTo(-448.0);
        await Assert.That(vm.MaxValue).IsEqualTo(448.0);

        double expectedMean = (0.0 + 0.0 + (1.0 / 512.0) + (7.0 / 512.0) + (1.0 / 64.0) + 1.0 - 1.0 + 1.5 + 2.0 + 448.0 - 448.0) / 11.0;
        await Assert.That(vm.MeanValue!.Value).IsEqualTo(expectedMean).Within(1e-6);

        await Assert.That(vm.PreviewValues[0].RawValue).IsEqualTo("0");
        await Assert.That(vm.PreviewValues[1].RawValue).IsEqualTo("-0");
        await Assert.That(vm.PreviewValues[2].RawValue).IsEqualTo("0.0019531");
        await Assert.That(vm.PreviewValues[4].RawValue).IsEqualTo("0.015625");
        await Assert.That(vm.PreviewValues[5].RawValue).IsEqualTo("1");
        await Assert.That(vm.PreviewValues[6].RawValue).IsEqualTo("-1");
        await Assert.That(vm.PreviewValues[7].RawValue).IsEqualTo("1.5");
        await Assert.That(vm.PreviewValues[8].RawValue).IsEqualTo("2");
        await Assert.That(vm.PreviewValues[9].RawValue).IsEqualTo("448");
        await Assert.That(vm.PreviewValues[10].RawValue).IsEqualTo("-448");
        await Assert.That(vm.PreviewValues[11].RawValue).IsEqualTo("NaN");
        await Assert.That(vm.PreviewValues[12].RawValue).IsEqualTo("NaN");
    }

    [Test]
    public async Task InspectAsync_DecodesFloat8E5M2_HandlesNormalsSubnormalsMaxInfAndNaN()
    {
        // OCP FP8 E5M2 test vectors:
        // 0x00 (+0), 0x80 (-0), 0x01 (2^-16), 0x04 (2^-14), 0x3C (1.0), 0xBC (-1.0),
        // 0x3E (1.5), 0x7B (57344.0), 0xFB (-57344.0), 0x7C (+Inf), 0xFC (-Inf), 0x7D (NaN), 0xFF (NaN)
        byte[] streamBytes = [0x00, 0x80, 0x01, 0x04, 0x3C, 0xBC, 0x3E, 0x7B, 0xFB, 0x7C, 0xFC, 0x7D, 0xFF];
        using MemoryStream stream = new(streamBytes);

        TensorDataSlice slice = new(StreamOffset: 0, ByteLength: streamBytes.Length, DataType: DataType.Float8E5M2);
        TensorDescriptor desc = new(DataType: DataType.Float8E5M2, Shape: new TensorShape([new TensorDimension.Fixed(streamBytes.Length)]));
        TensorEdge tensor = new("fp8_e5m2_weights", desc, TensorKind.Initializer, Data: slice);

        TensorInspectorViewModel vm = new();
        await vm.InspectAsync(tensor, stream);

        await Assert.That(vm.SampledElementCount).IsEqualTo(13);
        await Assert.That(vm.PreviewValues.Count).IsEqualTo(13);
        await Assert.That(vm.HasStatistics).IsTrue();
        await Assert.That(vm.HasNaN).IsTrue();
        await Assert.That(vm.HasInfinity).IsTrue();
        await Assert.That(vm.MinValue).IsEqualTo(-57344.0);
        await Assert.That(vm.MaxValue).IsEqualTo(57344.0);

        double expectedMean = (0.0 + 0.0 + Math.Pow(2, -16) + Math.Pow(2, -14) + 1.0 - 1.0 + 1.5 + 57344.0 - 57344.0) / 9.0;
        await Assert.That(vm.MeanValue!.Value).IsEqualTo(expectedMean).Within(1e-6);

        await Assert.That(vm.PreviewValues[0].RawValue).IsEqualTo("0");
        await Assert.That(vm.PreviewValues[1].RawValue).IsEqualTo("-0");
        await Assert.That(vm.PreviewValues[4].RawValue).IsEqualTo("1");
        await Assert.That(vm.PreviewValues[5].RawValue).IsEqualTo("-1");
        await Assert.That(vm.PreviewValues[6].RawValue).IsEqualTo("1.5");
        await Assert.That(vm.PreviewValues[7].RawValue).IsEqualTo("57344");
        await Assert.That(vm.PreviewValues[8].RawValue).IsEqualTo("-57344");
        await Assert.That(vm.PreviewValues[9].RawValue).IsEqualTo("Infinity");
        await Assert.That(vm.PreviewValues[10].RawValue).IsEqualTo("-Infinity");
        await Assert.That(vm.PreviewValues[11].RawValue).IsEqualTo("NaN");
        await Assert.That(vm.PreviewValues[12].RawValue).IsEqualTo("NaN");
    }

    [Test]
    public async Task InspectAsync_DecodesFloat8E4M3_AllNaNValues_ProducesNullFiniteStatistics()
    {
        byte[] streamBytes = [0x7F, 0xFF];
        using MemoryStream stream = new(streamBytes);

        TensorDataSlice slice = new(StreamOffset: 0, ByteLength: streamBytes.Length, DataType: DataType.Float8E4M3);
        TensorDescriptor desc = new(DataType: DataType.Float8E4M3, Shape: new TensorShape([new TensorDimension.Fixed(2)]));
        TensorEdge tensor = new("fp8_nan_only", desc, TensorKind.Initializer, Data: slice);

        TensorInspectorViewModel vm = new();
        await vm.InspectAsync(tensor, stream);

        await Assert.That(vm.HasStatistics).IsTrue();
        await Assert.That(vm.HasNaN).IsTrue();
        await Assert.That(vm.HasInfinity).IsFalse();
        await Assert.That(vm.MinValue).IsNull();
        await Assert.That(vm.MaxValue).IsNull();
        await Assert.That(vm.MeanValue).IsNull();
        await Assert.That(vm.ZeroPercentage).IsEqualTo(0.0);
    }

    [Test]
    public async Task InspectAsync_DecodesInt4_UnpacksLittleEndianNibblesWithSignExtensionAndDequantization()
    {
        // All 16 signed 4-bit values packed 2 per byte (low nibble first, high nibble second):
        // [0, 1, 2, 3, 4, 5, 6, 7, -8, -7, -6, -5, -4, -3, -2, -1]
        byte[] streamBytes = [0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE];
        using MemoryStream stream = new(streamBytes);

        AffineQuantization affine = new([0.25f], [0]);
        TensorDataSlice slice = new(StreamOffset: 0, ByteLength: streamBytes.Length, DataType: DataType.Int4);
        TensorDescriptor desc = new(
            DataType: DataType.Int4,
            Shape: new TensorShape([new TensorDimension.Fixed(16)]),
            Quantization: affine);
        TensorEdge tensor = new("int4_weights", desc, TensorKind.Initializer, Data: slice);

        TensorInspectorViewModel vm = new();
        await vm.InspectAsync(tensor, stream);

        await Assert.That(vm.SampledElementCount).IsEqualTo(16);
        await Assert.That(vm.PreviewValues.Count).IsEqualTo(16);
        await Assert.That(vm.MinValue).IsEqualTo(-8.0);
        await Assert.That(vm.MaxValue).IsEqualTo(7.0);
        await Assert.That(vm.MeanValue).IsEqualTo(-0.5);

        string[] expectedRaws = ["0", "1", "2", "3", "4", "5", "6", "7", "-8", "-7", "-6", "-5", "-4", "-3", "-2", "-1"];
        for (int i = 0; i < expectedRaws.Length; i++)
        {
            await Assert.That(vm.PreviewValues[i].RawValue).IsEqualTo(expectedRaws[i]);
        }

        await Assert.That(vm.PreviewValues[8].DequantizedValue).IsEqualTo("-2");
        await Assert.That(vm.PreviewValues[7].DequantizedValue).IsEqualTo("1.75");
    }

    [Test]
    public async Task InspectAsync_DecodesUInt4_TrimsTrailingPaddingNibbleForOddElementCount()
    {
        // Shape [5] packed into 3 bytes:
        // byte 0 = 0x10 -> [0, 1]
        // byte 1 = 0xF8 -> [8, 15]
        // byte 2 = 0xA7 -> [7] (high nibble 0xA is padding and must be ignored)
        byte[] streamBytes = [0x10, 0xF8, 0xA7];
        using MemoryStream stream = new(streamBytes);

        TensorDataSlice slice = new(StreamOffset: 0, ByteLength: streamBytes.Length, DataType: DataType.UInt4);
        TensorDescriptor desc = new(DataType: DataType.UInt4, Shape: new TensorShape([new TensorDimension.Fixed(5)]));
        TensorEdge tensor = new("uint4_odd_weights", desc, TensorKind.Initializer, Data: slice);

        TensorInspectorViewModel vm = new();
        await vm.InspectAsync(tensor, stream);

        await Assert.That(vm.SampledElementCount).IsEqualTo(5);
        await Assert.That(vm.PreviewValues.Count).IsEqualTo(5);
        await Assert.That(vm.MinValue).IsEqualTo(0.0);
        await Assert.That(vm.MaxValue).IsEqualTo(15.0);
        await Assert.That(vm.MeanValue!.Value).IsEqualTo(6.2).Within(1e-9);

        string[] expectedRaws = ["0", "1", "8", "15", "7"];
        for (int i = 0; i < expectedRaws.Length; i++)
        {
            await Assert.That(vm.PreviewValues[i].RawValue).IsEqualTo(expectedRaws[i]);
        }
    }

    [Test]
    public async Task InspectAsync_DecodesInt4_WithDynamicShape_UnpacksAllAvailableNibbles()
    {
        // 2 bytes = 4 nibbles: 0x87 -> [7, -8], 0xF0 -> [0, -1]
        byte[] streamBytes = [0x87, 0xF0];
        using MemoryStream stream = new(streamBytes);

        TensorDataSlice slice = new(StreamOffset: 0, ByteLength: streamBytes.Length, DataType: DataType.Int4);
        TensorDescriptor desc = new(
            DataType: DataType.Int4,
            Shape: new TensorShape([TensorDimension.Dynamic.Instance, new TensorDimension.Fixed(2)]));
        TensorEdge tensor = new("int4_dynamic", desc, TensorKind.Initializer, Data: slice);

        TensorInspectorViewModel vm = new();
        await vm.InspectAsync(tensor, stream);

        await Assert.That(vm.SampledElementCount).IsEqualTo(4);
        await Assert.That(vm.PreviewValues[0].RawValue).IsEqualTo("7");
        await Assert.That(vm.PreviewValues[1].RawValue).IsEqualTo("-8");
        await Assert.That(vm.PreviewValues[2].RawValue).IsEqualTo("0");
        await Assert.That(vm.PreviewValues[3].RawValue).IsEqualTo("-1");
        await Assert.That(vm.MinValue).IsEqualTo(-8.0);
        await Assert.That(vm.MaxValue).IsEqualTo(7.0);
        await Assert.That(vm.MeanValue).IsEqualTo(-0.5);
    }

    [Test]
    public async Task InspectAsync_DecodesFloat8E4M3_All256BytePatterns_MatchesOcpSpecStatistics()
    {
        byte[] allBytes = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            allBytes[i] = (byte)i;
        }

        using MemoryStream stream = new(allBytes);
        TensorDataSlice slice = new(StreamOffset: 0, ByteLength: 256, DataType: DataType.Float8E4M3);
        TensorDescriptor desc = new(DataType: DataType.Float8E4M3, Shape: new TensorShape([new TensorDimension.Fixed(256)]));
        TensorEdge tensor = new("fp8_e4m3_all256", desc, TensorKind.Initializer, Data: slice);

        TensorInspectorViewModel vm = new();
        await vm.InspectAsync(tensor, stream);

        // OCP E4M3FN has exact +/- symmetry, 2 zeros (0x00, 0x80), 2 NaNs (0x7F, 0xFF), 0 Infinities, range [-448, 448].
        await Assert.That(vm.SampledElementCount).IsEqualTo(256);
        await Assert.That(vm.PreviewValues.Count).IsEqualTo(64);
        await Assert.That(vm.HasNaN).IsTrue();
        await Assert.That(vm.HasInfinity).IsFalse();
        await Assert.That(vm.MinValue).IsEqualTo(-448.0);
        await Assert.That(vm.MaxValue).IsEqualTo(448.0);
        await Assert.That(vm.MeanValue!.Value).IsEqualTo(0.0).Within(1e-12);
        await Assert.That(vm.ZeroPercentage!.Value).IsEqualTo(2.0 * 100.0 / 256.0).Within(1e-9);
    }

    [Test]
    public async Task InspectAsync_DecodesFloat8E5M2_All256BytePatterns_MatchesOcpSpecStatistics()
    {
        byte[] allBytes = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            allBytes[i] = (byte)i;
        }

        using MemoryStream stream = new(allBytes);
        TensorDataSlice slice = new(StreamOffset: 0, ByteLength: 256, DataType: DataType.Float8E5M2);
        TensorDescriptor desc = new(DataType: DataType.Float8E5M2, Shape: new TensorShape([new TensorDimension.Fixed(256)]));
        TensorEdge tensor = new("fp8_e5m2_all256", desc, TensorKind.Initializer, Data: slice);

        TensorInspectorViewModel vm = new();
        await vm.InspectAsync(tensor, stream);

        // OCP E5M2 has exact +/- symmetry, 2 zeros (0x00, 0x80), 2 Infs (0x7C, 0xFC), 6 NaNs, range [-57344, 57344].
        await Assert.That(vm.SampledElementCount).IsEqualTo(256);
        await Assert.That(vm.PreviewValues.Count).IsEqualTo(64);
        await Assert.That(vm.HasNaN).IsTrue();
        await Assert.That(vm.HasInfinity).IsTrue();
        await Assert.That(vm.MinValue).IsEqualTo(-57344.0);
        await Assert.That(vm.MaxValue).IsEqualTo(57344.0);
        await Assert.That(vm.MeanValue!.Value).IsEqualTo(0.0).Within(1e-12);
        await Assert.That(vm.ZeroPercentage!.Value).IsEqualTo(2.0 * 100.0 / 256.0).Within(1e-9);
    }

    [Test]
    public async Task InspectAsync_DecodesVarintPackedLowBitOnnxTensors_UnpacksFp8Int4AndUInt4()
    {
        // ONNX TensorProto.int32_data (VarintPacked) for FLOAT8E4M3FN: [0x38 (1.0), 0x3C (1.5), 0x7E (448.0)] + truncated trailing varint 0xF8
        byte[] fp8E4Varints = [0x38, 0x3C, 0x7E, 0xF8];
        using (MemoryStream stream = new(fp8E4Varints))
        {
            TensorDataSlice slice = new(0, fp8E4Varints.Length, DataType.Float8E4M3, TensorDataEncoding.VarintPacked);
            TensorDescriptor desc = new(DataType.Float8E4M3, new TensorShape([new TensorDimension.Fixed(3)]));
            TensorInspectorViewModel vm = new();
            await vm.InspectAsync(new TensorEdge("onnx_fp8_e4m3_int32", desc, TensorKind.Initializer, Data: slice), stream);

            await Assert.That(vm.SampledElementCount).IsEqualTo(3);
            await Assert.That(vm.PreviewValues[0].RawValue).IsEqualTo("1");
            await Assert.That(vm.PreviewValues[1].RawValue).IsEqualTo("1.5");
            await Assert.That(vm.PreviewValues[2].RawValue).IsEqualTo("448");
        }

        // ONNX TensorProto.int32_data (VarintPacked) for FLOAT8E5M2: [0x3C (1.0), 0x3E (1.5), 0x7B (57344.0)]
        byte[] fp8E5Varints = [0x3C, 0x3E, 0x7B];
        using (MemoryStream stream = new(fp8E5Varints))
        {
            TensorDataSlice slice = new(0, fp8E5Varints.Length, DataType.Float8E5M2, TensorDataEncoding.VarintPacked);
            TensorDescriptor desc = new(DataType.Float8E5M2, new TensorShape([new TensorDimension.Fixed(3)]));
            TensorInspectorViewModel vm = new();
            await vm.InspectAsync(new TensorEdge("onnx_fp8_e5m2_int32", desc, TensorKind.Initializer, Data: slice), stream);

            await Assert.That(vm.SampledElementCount).IsEqualTo(3);
            await Assert.That(vm.PreviewValues[0].RawValue).IsEqualTo("1");
            await Assert.That(vm.PreviewValues[1].RawValue).IsEqualTo("1.5");
            await Assert.That(vm.PreviewValues[2].RawValue).IsEqualTo("57344");
        }

        // ONNX TensorProto.int32_data (VarintPacked) for INT4 shape [5]:
        // varints [248 (0xF8 -> 0xF8 0x01), 16 (0x10), 7 (0x07)] -> [-8, -1, 0, 1, 7] (trailing nibble trimmed)
        byte[] int4Varints = [0xF8, 0x01, 0x10, 0x07];
        using (MemoryStream stream = new(int4Varints))
        {
            TensorDataSlice slice = new(0, int4Varints.Length, DataType.Int4, TensorDataEncoding.VarintPacked);
            TensorDescriptor desc = new(DataType.Int4, new TensorShape([new TensorDimension.Fixed(5)]));
            TensorInspectorViewModel vm = new();
            await vm.InspectAsync(new TensorEdge("onnx_int4_int32", desc, TensorKind.Initializer, Data: slice), stream);

            await Assert.That(vm.SampledElementCount).IsEqualTo(5);
            await Assert.That(vm.PreviewValues.Select(p => p.RawValue).ToArray())
                .IsEquivalentTo(["-8", "-1", "0", "1", "7"]);
            await Assert.That(vm.MinValue).IsEqualTo(-8.0);
            await Assert.That(vm.MaxValue).IsEqualTo(7.0);
        }

        // ONNX TensorProto.int32_data (VarintPacked) for UINT4 shape [5]:
        // varints [16 (0x10), 248 (0xF8 -> 0xF8 0x01), 7 (0x07)] -> [0, 1, 8, 15, 7]
        byte[] uint4Varints = [0x10, 0xF8, 0x01, 0x07];
        using (MemoryStream stream = new(uint4Varints))
        {
            TensorDataSlice slice = new(0, uint4Varints.Length, DataType.UInt4, TensorDataEncoding.VarintPacked);
            TensorDescriptor desc = new(DataType.UInt4, new TensorShape([new TensorDimension.Fixed(5)]));
            TensorInspectorViewModel vm = new();
            await vm.InspectAsync(new TensorEdge("onnx_uint4_int32", desc, TensorKind.Initializer, Data: slice), stream);

            await Assert.That(vm.SampledElementCount).IsEqualTo(5);
            await Assert.That(vm.PreviewValues.Select(p => p.RawValue).ToArray())
                .IsEquivalentTo(["0", "1", "8", "15", "7"]);
        }
    }

    [Test]
    public async Task InspectAsync_DecodesInt4_HandlesScalarZeroDimOverflowShapeAndLarge64KBCap()
    {
        // 1. Scalar shape [] with 1 byte (0xF5): low nibble 5, high nibble padding ignored
        using (MemoryStream scalarStream = new([0xF5]))
        {
            TensorDataSlice slice = new(0, 1, DataType.Int4);
            TensorDescriptor desc = new(DataType.Int4, TensorShape.Scalar);
            TensorInspectorViewModel vm = new();
            await vm.InspectAsync(new TensorEdge("int4_scalar", desc, TensorKind.Initializer, Data: slice), scalarStream);

            await Assert.That(vm.SampledElementCount).IsEqualTo(1);
            await Assert.That(vm.PreviewValues.Count).IsEqualTo(1);
            await Assert.That(vm.PreviewValues[0].RawValue).IsEqualTo("5");
        }

        // 2. Zero-dimension shape [0] with 1 padding byte -> 0 sampled elements, no statistics
        using (MemoryStream zeroStream = new([0x00]))
        {
            TensorDataSlice slice = new(0, 1, DataType.Int4);
            TensorDescriptor desc = new(DataType.Int4, new TensorShape([new TensorDimension.Fixed(0)]));
            TensorInspectorViewModel vm = new();
            await vm.InspectAsync(new TensorEdge("int4_empty", desc, TensorKind.Initializer, Data: slice), zeroStream);

            await Assert.That(vm.SampledElementCount).IsEqualTo(0);
            await Assert.That(vm.PreviewValues.Count).IsEqualTo(0);
            await Assert.That(vm.HasStatistics).IsFalse();
        }

        // 3. Overflowing shape [long.MaxValue, 2] -> safely falls back to bytes.Length * 2 nibbles
        using (MemoryStream overflowStream = new([0x87]))
        {
            TensorDataSlice slice = new(0, 1, DataType.Int4);
            TensorDescriptor desc = new(
                DataType.Int4,
                new TensorShape([new TensorDimension.Fixed(long.MaxValue), new TensorDimension.Fixed(2)]));
            TensorInspectorViewModel vm = new();
            await vm.InspectAsync(new TensorEdge("int4_overflow_shape", desc, TensorKind.Initializer, Data: slice), overflowStream);

            await Assert.That(vm.SampledElementCount).IsEqualTo(2);
            await Assert.That(vm.PreviewValues[0].RawValue).IsEqualTo("7");
            await Assert.That(vm.PreviewValues[1].RawValue).IsEqualTo("-8");
        }

        // 4. Large 100 KB packed Int4 slice -> read capped at 64 KB (65536 bytes = 131072 nibbles), preview capped at 64
        const int largeBytes = 100 * 1024;
        byte[] payload = new byte[largeBytes];
        Array.Fill(payload, (byte)0x87); // alternating [7, -8]
        using (ByteCountingStream largeStream = new(payload))
        {
            TensorDataSlice slice = new(0, largeBytes, DataType.Int4);
            TensorDescriptor desc = new(DataType.Int4, new TensorShape([new TensorDimension.Fixed(largeBytes * 2L)]));
            TensorInspectorViewModel vm = new();
            await vm.InspectAsync(new TensorEdge("int4_large", desc, TensorKind.Initializer, Data: slice), largeStream);

            await Assert.That(largeStream.TotalBytesRead).IsEqualTo(65536);
            await Assert.That(vm.SampledElementCount).IsEqualTo(131072);
            await Assert.That(vm.PreviewValues.Count).IsEqualTo(64);
            await Assert.That(vm.MinValue).IsEqualTo(-8.0);
            await Assert.That(vm.MaxValue).IsEqualTo(7.0);
            await Assert.That(vm.MeanValue).IsEqualTo(-0.5);

            // 5. Inspecting a data-less tensor afterwards resets SampledElementCount to 0
            TensorEdge noDataTensor = new("activation", new TensorDescriptor(DataType.Float32, null), TensorKind.Activation);
            await vm.InspectAsync(noDataTensor, largeStream);
            await Assert.That(vm.SampledElementCount).IsEqualTo(0);
            await Assert.That(vm.HasStatistics).IsFalse();
        }
    }

    [Test]
    public async Task InspectAsync_GgmlQuantizedBlock_RendersRawHexAndBlockQuantizationMetadata()
    {
        byte[] blockBytes = [0x12, 0x34, 0xAB, 0xCD, 0x00, 0xFF, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90, 0xA0, 0xB0, 0xC0];
        using MemoryStream stream = new(blockBytes);

        TensorDataSlice slice = new(0, blockBytes.Length, DataType.UInt8, TensorDataEncoding.GgmlQuantizedBlock);
        TensorDescriptor desc = new(
            DataType.UInt8,
            new TensorShape([new TensorDimension.Fixed(1), new TensorDimension.Fixed(32)]),
            Quantization: new BlockQuantization("Q4_0", BlockSize: 32, Bits: 4));
        TensorEdge tensor = new("blk.0.attn_q.weight", desc, TensorKind.Initializer, slice);

        TensorInspectorViewModel vm = new();
        await vm.InspectAsync(tensor, stream);

        await Assert.That(vm.ErrorMessage).IsNull();
        await Assert.That(vm.HasData).IsTrue();
        await Assert.That(vm.HasDequantization).IsFalse();
        await Assert.That(vm.DataTypeText).IsEqualTo("Q4_0 (UInt8)");
        await Assert.That(vm.QuantizationDescription).IsEqualTo("Q4_0 (Block Size: 32, 4-bit)");
        await Assert.That(vm.EncodingText).IsEqualTo(nameof(TensorDataEncoding.GgmlQuantizedBlock));
        await Assert.That(vm.SampledElementCount).IsEqualTo(blockBytes.Length);
        await Assert.That(vm.PreviewValues[0].RawValue).IsEqualTo("0x12");
        await Assert.That(vm.PreviewValues[2].RawValue).IsEqualTo("0xAB");
    }
}

