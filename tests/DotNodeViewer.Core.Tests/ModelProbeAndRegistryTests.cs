using System.IO.Compression;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Core.Tests.Harness;

namespace DotNodeViewer.Core.Tests;

/// <summary>
/// Verifies <see cref="ModelProbe"/>, <see cref="ModelFormatRegistry"/>, <see cref="GraphIndex"/>, and <see cref="TensorDataLoader"/>.
/// </summary>
public sealed class ModelProbeAndRegistryTests
{
    [Test]
    public async Task ModelProbe_CreateAndZipEntries_RestoresInitialStreamPosition()
    {
        using MemoryStream zipStream = new();
        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry1 = archive.CreateEntry("archive/version");
            await using (Stream writer = entry1.Open())
            {
                writer.Write("3\n"u8);
            }

            ZipArchiveEntry entry2 = archive.CreateEntry("archive/data.pkl");
            await using (Stream writer = entry2.Open())
            {
                writer.Write([0x80, 0x02, (byte)'.']);
            }
        }

        zipStream.Position = 7;
        ModelProbe probe = await ModelProbe.CreateAsync(zipStream, "Path/To/Model.PT");

        await Assert.That(zipStream.Position).IsEqualTo(7);
        await Assert.That(probe.FileExtension).IsEqualTo(".pt");
        await Assert.That(probe.IsZipArchive).IsTrue();

        IReadOnlyList<string> entries = probe.ZipEntries;
        await Assert.That(entries).Contains("archive/version");
        await Assert.That(entries).Contains("archive/data.pkl");
        await Assert.That(zipStream.Position).IsEqualTo(7);
    }

    [Test]
    public async Task ModelProbe_NonSeekableStream_ThrowsModelParseException()
    {
        using NonSeekableStream stream = new([1, 2, 3, 4]);
        await Assert.That(() => ModelProbe.Create(stream)).Throws<ModelParseException>();
    }

    [Test]
    public async Task ModelFormatRegistry_RegisterDetectAndRead_RoutesToMatchingReaderAndRestoresPosition()
    {
        ModelFormatRegistry registry = new();
        FakeMagicFormat parser = new();
        registry.Register(parser, parser);

        byte[] data = Encoding.ASCII.GetBytes("DNVTEST!_payload_bytes");
        using MemoryStream stream = new(data);
        stream.Position = 4;

        ModelFormatInfo? detected = registry.TryDetect(stream, "sample.dnv");
        await Assert.That(detected).IsNotNull();
        await Assert.That(detected!.Id.Value).IsEqualTo("dnv-test");
        await Assert.That(stream.Position).IsEqualTo(4);

        ModelGraph graph = await registry.ReadAsync(stream, new ModelReadOptions { FileName = "sample.dnv" });
        await Assert.That(graph.Format.Id.Value).IsEqualTo("dnv-test");
        await Assert.That(graph.PrimarySubgraph).IsNotNull();
        await Assert.That(graph.PrimarySubgraph!.Name).IsEqualTo("main");
    }

    [Test]
    public async Task TensorDataLoader_ReadSliceAndAllocationCheck_ReadsExactBytesWithoutHeapAllocation()
    {
        byte[] fileBytes = new byte[4096];
        for (int i = 0; i < fileBytes.Length; i++)
        {
            fileBytes[i] = (byte)(i & 0xFF);
        }

        using MemoryStream stream = new(fileBytes);
        stream.Position = 12;
        TensorDataSlice slice = new(StreamOffset: 512, ByteLength: 64, DataType.UInt8);

        byte[] destination = new byte[64];
        // Warm up JIT before measuring thread allocations.
        _ = TensorDataLoader.ReadSlice(stream, slice, destination);

        long allocated = AllocationProbe.MeasureBytes(() =>
        {
            int count = TensorDataLoader.ReadSlice(stream, slice, destination);
            if (count != 64)
            {
                throw new InvalidOperationException("Unexpected byte count.");
            }
        });

        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(stream.Position).IsEqualTo(12);
        await Assert.That(destination[0]).IsEqualTo((byte)(512 & 0xFF));
        await Assert.That(destination[63]).IsEqualTo((byte)(575 & 0xFF));

        TensorDataSlice outOfBounds = new(StreamOffset: 4000, ByteLength: 200, DataType.UInt8);
        await Assert.That(() => TensorDataLoader.ReadSlice(stream, outOfBounds, destination))
            .Throws<ModelParseException>();
    }

    [Test]
    public async Task GraphIndex_ProducerConsumerAndOneHopNeighborhood_ResolvesExpectedNodes()
    {
        TensorEdge tInput = new("input", new TensorDescriptor(DataType.Float32, null), TensorKind.GraphInput);
        TensorEdge tWeight = new("conv_w", new TensorDescriptor(DataType.Float32, null), TensorKind.Initializer);
        TensorEdge tConvOut = new("conv_out", new TensorDescriptor(DataType.Float32, null), TensorKind.Activation);
        TensorEdge tReluOut = new("relu_out", new TensorDescriptor(DataType.Float32, null), TensorKind.GraphOutput);

        GraphNode convNode = new(
            "conv_0",
            new OperatorInfo("Conv", OperatorCategory.Layer),
            Inputs: [new NodePort("X", [tInput]), new NodePort("W", [tWeight])],
            Outputs: [new NodePort("Y", [tConvOut])],
            Attributes: []);

        GraphNode reluNode = new(
            "relu_0",
            new OperatorInfo("Relu", OperatorCategory.Activation),
            Inputs: [new NodePort("X", [tConvOut])],
            Outputs: [new NodePort("Y", [tReluOut])],
            Attributes: []);

        Subgraph subgraph = new(
            "main",
            Nodes: [convNode, reluNode],
            Tensors: [tInput, tWeight, tConvOut, tReluOut],
            Inputs: [new GraphPort("input", [tInput])],
            Outputs: [new GraphPort("relu_out", [tReluOut])]);

        GraphIndex index = new(subgraph);

        await Assert.That(index.GetNodeByName("conv_0")).IsSameReferenceAs(convNode);
        await Assert.That(index.GetTensorByName("conv_out")).IsSameReferenceAs(tConvOut);
        await Assert.That(index.GetProducer(tConvOut)).IsSameReferenceAs(convNode);
        await Assert.That(index.GetProducer(tInput)).IsNull();
        await Assert.That(index.GetConsumers(tConvOut)).Contains(reluNode);
        await Assert.That(index.GetUpstreamNodes(reluNode)).Contains(convNode);
        await Assert.That(index.GetDownstreamNodes(convNode)).Contains(reluNode);
    }

    private sealed class FakeMagicFormat : IModelFormatDetector, IModelFormatReader
    {
        public ModelFormatInfo Format { get; } = new(new ModelFormatId("dnv-test"), "DNV Test", "v1");

        public ModelFormatId FormatId => Format.Id;

        public bool CanRead(ModelProbe probe)
        {
            // Intentionally move stream position to verify ModelFormatRegistry restores it.
            probe.Stream.Position = Math.Min(2, probe.StreamLength);
            return probe.Header.StartsWith("DNVTEST!"u8);
        }

        public ValueTask<ModelGraph> ReadAsync(
            Stream stream,
            ModelReadOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Subgraph main = new("main", [], [], [], []);
            ModelGraph graph = new(Format, "FakeProducer", [main], new Dictionary<string, string>(), []);
            return ValueTask.FromResult(graph);
        }
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}

