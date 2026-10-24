using DotNodeViewer.App.IO;

namespace DotNodeViewer.App.Tests;

public class BrowserStreamAdapterTests
{
    [Test]
    public async Task CreateSeekableStreamAsync_WithValidStream_ReturnsSeekableStreamWithExactData()
    {
        byte[] expected = [0x4F, 0x4E, 0x4E, 0x58, 0x01, 0x02, 0x03, 0x04];
        using MemoryStream source = new(expected);

        using MemoryStream seekable = await BrowserStreamAdapter.CreateSeekableStreamAsync(
            source,
            "sample.onnx",
            maxBytes: 1024);

        await Assert.That(seekable.CanSeek).IsTrue();
        await Assert.That(seekable.Length).IsEqualTo(expected.Length);
        await Assert.That(seekable.Position).IsEqualTo(0);

        byte[] actual = seekable.ToArray();
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    public async Task CreateSeekableStreamAsync_WhenExceedingMemoryLimitDuringStreaming_ThrowsInvalidOperationException()
    {
        byte[] payload = new byte[2048];
        Array.Fill(payload, (byte)0xAA);

        // Wrap in non-seekable forward-only stream to force chunking limit check
        NonSeekableForwardOnlyStream source = new(payload);

        var task = BrowserStreamAdapter.CreateSeekableStreamAsync(
            source,
            "large_model.onnx",
            maxBytes: 1024);

        var ex = await Assert.That(async () => await task)
            .Throws<InvalidOperationException>();
        await Assert.That(ex!.Message).Contains("exceeded browser tab memory limit");
    }

    [Test]
    public async Task CreateSeekableStreamAsync_WhenSourceLengthKnownAndExceedsLimit_RejectsEarly()
    {
        byte[] payload = new byte[2048];
        using MemoryStream source = new(payload);

        var task = BrowserStreamAdapter.CreateSeekableStreamAsync(
            source,
            "early_check.tflite",
            maxBytes: 1024);

        var ex = await Assert.That(async () => await task)
            .Throws<InvalidOperationException>();
        await Assert.That(ex!.Message).Contains("exceeds browser tab memory limit");
    }

    [Test]
    public async Task CreateStreamFactory_ReturnsWorkingIndependentSeekableStreams()
    {
        byte[] payload = [0x54, 0x46, 0x4C, 0x33, 0x10, 0x20];
        Func<Stream> factory = BrowserStreamAdapter.CreateStreamFactory(payload);

        await using Stream s1 = factory();
        await using Stream s2 = factory();

        await Assert.That(s1.CanSeek).IsTrue();
        await Assert.That(s2.CanSeek).IsTrue();
        await Assert.That(s1.Length).IsEqualTo(payload.Length);
        await Assert.That(s2.Length).IsEqualTo(payload.Length);

        // Independent seek positions
        s1.Position = 2;
        await Assert.That(s2.Position).IsEqualTo(0);
    }

    [Test]
    public async Task CreateSeekableStreamAsync_WithNullSource_ThrowsArgumentNullException()
    {
        await Assert.That(async () => await BrowserStreamAdapter.CreateSeekableStreamAsync(null!, "test.onnx"))
            .Throws<ArgumentNullException>();
    }

    private sealed class NonSeekableForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

