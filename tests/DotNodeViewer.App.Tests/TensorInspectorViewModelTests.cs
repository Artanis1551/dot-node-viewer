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
}

