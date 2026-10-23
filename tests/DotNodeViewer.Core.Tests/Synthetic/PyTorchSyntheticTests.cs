using System.IO.Compression;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Formats.PyTorch;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Tests.Synthetic;

/// <summary>
/// Synthetic in-memory binary tests for <see cref="PyTorchFormatDetector"/>, <see cref="PyTorchFormatReader"/>, and <see cref="PyTorchOperatorCatalog"/>.
/// </summary>
public sealed class PyTorchSyntheticTests
{
    [Test]
    public async Task CanRead_ValidHeaderTruncatedAndRandomBytes_ReturnsExpectedResultAndRestoresPosition()
    {
        byte[] validZipModel = BuildSyntheticPyTorchZip();
        using MemoryStream validStream = new(validZipModel);
        validStream.Position = 8;

        ModelProbe validProbe = ModelProbe.Create(validStream, "synthetic.pt");
        bool canRead = PyTorchFormatDetector.Instance.CanRead(validProbe);
        validProbe.RestorePosition();

        await Assert.That(canRead).IsTrue();
        await Assert.That(validStream.Position).IsEqualTo(8);

        ModelFormatInfo? detected = ModelFormatRegistry.Default.TryDetect(validStream);
        await Assert.That(detected).IsNotNull();
        await Assert.That(detected!.Id).IsEqualTo(ModelFormatId.PyTorch);
        await Assert.That(validStream.Position).IsEqualTo(8);

        // Raw pickle checkpoint
        byte[] rawPickleModel = BuildSyntheticRawPickle();
        using MemoryStream rawPickleStream = new(rawPickleModel);
        ModelProbe rawProbe = ModelProbe.Create(rawPickleStream, "model.bin");
        await Assert.That(PyTorchFormatDetector.Instance.CanRead(rawProbe)).IsTrue();

        // Truncated stream (< 4 bytes)
        using MemoryStream truncatedStream = new(validZipModel[..3]);
        ModelProbe truncatedProbe = ModelProbe.Create(truncatedStream, "truncated.pt");
        await Assert.That(PyTorchFormatDetector.Instance.CanRead(truncatedProbe)).IsFalse();

        // Random garbage bytes
        byte[] randomBytes = new byte[64];
        Random.Shared.NextBytes(randomBytes);
        using MemoryStream randomStream = new(randomBytes);
        ModelProbe randomProbe = ModelProbe.Create(randomStream, "random.pt");
        await Assert.That(PyTorchFormatDetector.Instance.CanRead(randomProbe)).IsFalse();
    }

    [Test]
    public async Task ReadAsync_TopologyShapesAndModuleNodes_MapsExpectedGraph()
    {
        byte[] modelBytes = BuildSyntheticPyTorchZip();
        using MemoryStream ms = new(modelBytes);

        ModelGraph graph = await PyTorchFormatReader.Instance.ReadAsync(ms);

        await Assert.That(graph.Format.Id).IsEqualTo(ModelFormatId.PyTorch);
        await Assert.That(graph.Subgraphs.Count).IsEqualTo(1);

        Subgraph subgraph = graph.Subgraphs[0];
        await Assert.That(subgraph.Nodes.Count > 0).IsTrue();

        // Find conv1 node
        var conv1Node = subgraph.Nodes.FirstOrDefault(n => n.Name.Contains("conv1"));
        await Assert.That(conv1Node).IsNotNull();
        await Assert.That(conv1Node!.Operator.Category).IsEqualTo(OperatorCategory.Layer);

        // Find weight port on conv1
        var weightPort = conv1Node.Inputs.FirstOrDefault(p => p.Name == "weight");
        await Assert.That(weightPort).IsNotNull();
        await Assert.That(weightPort!.Tensors.Count).IsEqualTo(1);

        var weightTensor = weightPort.Tensors[0];
        await Assert.That(weightTensor.Kind).IsEqualTo(TensorKind.Initializer);
        await Assert.That(weightTensor.Descriptor.DataType).IsEqualTo(DataType.Float32);
        await Assert.That(weightTensor.Descriptor.Shape).IsNotNull();
        await Assert.That(weightTensor.Descriptor.Shape!.Rank).IsEqualTo(4);
    }

    [Test]
    public async Task ReadAsync_LazyWeightSkipping_RecordsExactOffsetsWithoutAllocatingPayload()
    {
        byte[] modelBytes = BuildSyntheticPyTorchZip();
        using MemoryStream ms = new(modelBytes);

        ModelGraph graph = await PyTorchFormatReader.Instance.ReadAsync(ms);

        var conv1 = graph.Subgraphs[0].Nodes.First(n => n.Name.Contains("conv1"));
        var weight = conv1.Inputs.First(p => p.Name == "weight").Tensors[0];

        await Assert.That(weight.Data).IsNotNull();
        await Assert.That(weight.Data).IsTypeOf<TensorDataSlice>();

        var slice = (TensorDataSlice)weight.Data!;
        await Assert.That(slice.StreamOffset > 0).IsTrue();
        await Assert.That(slice.ByteLength).IsEqualTo(72);
        await Assert.That(slice.StreamOffset + slice.ByteLength <= ms.Length).IsTrue();
        await Assert.That(slice.Encoding).IsEqualTo(TensorDataEncoding.Raw);
    }

    [Test]
    public async Task ReadAsync_CorruptedArchive_ThrowsModelParseException()
    {
        byte[] corrupted = new byte[32];
        corrupted[0] = 0x50; corrupted[1] = 0x4B; corrupted[2] = 0x03; corrupted[3] = 0x04;
        using MemoryStream ms = new(corrupted);

        await Assert.That(async () => await PyTorchFormatReader.Instance.ReadAsync(ms))
            .Throws<ModelParseException>();
    }

    private static byte[] BuildSyntheticPyTorchZip()
    {
        using MemoryStream ms = new();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // version
            var versionEntry = zip.CreateEntry("archive/version", CompressionLevel.NoCompression);
            using (var vs = versionEntry.Open()) vs.Write("3"u8);

            // byteorder
            var boEntry = zip.CreateEntry("archive/byteorder", CompressionLevel.NoCompression);
            using (var bs = boEntry.Open()) bs.Write("little"u8);

            // data/0 (weight: 2x1x3x3 float32 = 18 floats = 72 bytes)
            var data0 = zip.CreateEntry("archive/data/0", CompressionLevel.NoCompression);
            using (var ds0 = data0.Open())
            {
                byte[] weights = new byte[72];
                for (int i = 0; i < weights.Length; i++) weights[i] = (byte)(i + 1);
                ds0.Write(weights);
            }

            // data/1 (bias: 2 float32 = 8 bytes)
            var data1 = zip.CreateEntry("archive/data/1", CompressionLevel.NoCompression);
            using (var ds1 = data1.Open())
            {
                byte[] bias = new byte[8];
                ds1.Write(bias);
            }

            // data.pkl: state_dict with conv1.weight and conv1.bias
            var dataPkl = zip.CreateEntry("archive/data.pkl", CompressionLevel.NoCompression);
            using (var pklStream = dataPkl.Open())
            {
                WriteSyntheticStateDictPickle(pklStream);
            }
        }

        return ms.ToArray();
    }

    private static void WriteSyntheticStateDictPickle(Stream stream)
    {
        stream.WriteByte(0x80); stream.WriteByte(0x02); // PROTO 2
        stream.Write("ccollections\nOrderedDict\n)R"u8); // empty OrderedDict
        stream.WriteByte(0x7D); // EMPTY_DICT
        stream.WriteByte(0x28); // MARK

        // Key: "conv1.weight"
        stream.WriteByte(0x8C); stream.WriteByte(12); stream.Write("conv1.weight"u8);
        // Tensor: _rebuild_tensor_v2
        stream.Write("ctorch._utils\n_rebuild_tensor_v2\n"u8);
        stream.WriteByte(0x28); // MARK
        stream.WriteByte(0x28); // MARK
        stream.WriteByte(0x8C); stream.WriteByte(7); stream.Write("storage"u8);
        stream.Write("ctorch\nFloatStorage\n"u8);
        stream.WriteByte(0x8C); stream.WriteByte(1); stream.Write("0"u8);
        stream.WriteByte(0x8C); stream.WriteByte(3); stream.Write("cpu"u8);
        stream.WriteByte(0x4B); stream.WriteByte(18); // numel 18
        stream.WriteByte(0x74); // TUPLE
        stream.WriteByte(0x51); // BINPERSID
        stream.WriteByte(0x4B); stream.WriteByte(0); // offset 0
        // size (2, 1, 3, 3)
        stream.WriteByte(0x28);
        stream.WriteByte(0x4B); stream.WriteByte(2);
        stream.WriteByte(0x4B); stream.WriteByte(1);
        stream.WriteByte(0x4B); stream.WriteByte(3);
        stream.WriteByte(0x4B); stream.WriteByte(3);
        stream.WriteByte(0x74);
        // stride (9, 9, 3, 1)
        stream.WriteByte(0x28);
        stream.WriteByte(0x4B); stream.WriteByte(9);
        stream.WriteByte(0x4B); stream.WriteByte(9);
        stream.WriteByte(0x4B); stream.WriteByte(3);
        stream.WriteByte(0x4B); stream.WriteByte(1);
        stream.WriteByte(0x74);
        stream.WriteByte(0x89); // requires_grad False
        stream.Write("ccollections\nOrderedDict\n)R"u8);
        stream.WriteByte(0x74); // TUPLE
        stream.WriteByte(0x52); // REDUCE

        // Key: "conv1.bias"
        stream.WriteByte(0x8C); stream.WriteByte(10); stream.Write("conv1.bias"u8);
        stream.Write("ctorch._utils\n_rebuild_tensor_v2\n"u8);
        stream.WriteByte(0x28); // MARK
        stream.WriteByte(0x28); // MARK
        stream.WriteByte(0x8C); stream.WriteByte(7); stream.Write("storage"u8);
        stream.Write("ctorch\nFloatStorage\n"u8);
        stream.WriteByte(0x8C); stream.WriteByte(1); stream.Write("1"u8);
        stream.WriteByte(0x8C); stream.WriteByte(3); stream.Write("cpu"u8);
        stream.WriteByte(0x4B); stream.WriteByte(2); // numel 2
        stream.WriteByte(0x74); // TUPLE
        stream.WriteByte(0x51); // BINPERSID
        stream.WriteByte(0x4B); stream.WriteByte(0); // offset 0
        // size (2,)
        stream.WriteByte(0x28); stream.WriteByte(0x4B); stream.WriteByte(2); stream.WriteByte(0x74);
        // stride (1,)
        stream.WriteByte(0x28); stream.WriteByte(0x4B); stream.WriteByte(1); stream.WriteByte(0x74);
        stream.WriteByte(0x89);
        stream.Write("ccollections\nOrderedDict\n)R"u8);
        stream.WriteByte(0x74);
        stream.WriteByte(0x52);

        stream.WriteByte(0x75); // SETITEMS
        stream.WriteByte(0x62); // BUILD
        stream.WriteByte(0x2E); // STOP
    }

    private static byte[] BuildSyntheticRawPickle()
    {
        using MemoryStream ms = new();
        ms.WriteByte(0x80); ms.WriteByte(0x02);
        ms.Write("ctorch._utils\n_rebuild_tensor\n"u8);
        ms.WriteByte(0x29); // EMPTY_TUPLE
        ms.WriteByte(0x52); // REDUCE
        ms.WriteByte(0x2E); // STOP
        return ms.ToArray();
    }
}
