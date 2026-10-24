using System.Buffers.Binary;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Delegates.EthosU;

namespace DotNodeViewer.Core.Tests.Synthetic;

/// <summary>
/// Unit tests for <see cref="VelaBinStreamReader"/> parsing, boundary checks, and resilience to corrupt containers.
/// </summary>
public sealed class VelaBinStreamSyntheticTests
{
    [Test]
    public async Task Read_ValidStreamWithNamedBlocks_ParsesSuccessfully()
    {
        byte[] payload = BuildSyntheticVelaStream(
            ("vela_bin_stream", Array.Empty<byte>()),
            ("cmd_data", new byte[] { 0x43, 0x4F, 0x50, 0x31, 0, 0, 0, 0 }),
            ("weight_data", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
            ("scratch_size", BitConverter.GetBytes((uint)1024)),
            ("vela_end_stream", Array.Empty<byte>()));

        VelaBinStream stream = VelaBinStreamReader.Read(payload);

        await Assert.That(stream.Blocks.Count).IsEqualTo(5);
        await Assert.That(stream.CmdData).IsNotNull();
        await Assert.That(stream.CmdData!.Name).IsEqualTo("cmd_data");
        await Assert.That(stream.CmdData.Length).IsEqualTo(8);
        await Assert.That(stream.WeightData).IsNotNull();
        await Assert.That(stream.WeightData!.Name).IsEqualTo("weight_data");
        await Assert.That(stream.WeightData.Length).IsEqualTo(8);
        await Assert.That(stream.ScratchSize).IsEqualTo(1024u);
    }

    [Test]
    public async Task Read_MissingHeader_ThrowsModelParseException()
    {
        byte[] payload = BuildSyntheticVelaStream(
            ("cmd_data", new byte[] { 1, 2, 3, 4 }),
            ("vela_end_stream", Array.Empty<byte>()));

        var act = () => VelaBinStreamReader.Read(payload);

        await Assert.That(act).Throws<ModelParseException>();
    }

    [Test]
    public async Task Read_MissingEndStreamFooter_ThrowsModelParseException()
    {
        byte[] payload = BuildSyntheticVelaStream(
            ("vela_bin_stream", Array.Empty<byte>()),
            ("cmd_data", new byte[] { 1, 2, 3, 4 }));

        var act = () => VelaBinStreamReader.Read(payload);

        await Assert.That(act).Throws<ModelParseException>();
    }

    [Test]
    public async Task Read_OversizeBlockLength_ThrowsModelParseException()
    {
        byte[] payload = BuildSyntheticVelaStream(
            ("vela_bin_stream", Array.Empty<byte>()),
            ("cmd_data", new byte[] { 1, 2, 3, 4 }),
            ("vela_end_stream", Array.Empty<byte>()));

        // Corrupt length of cmd_data block (starts at offset 32, length is at 32 + 16 = 48)
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(48, 4), 100_000);

        var act = () => VelaBinStreamReader.Read(payload);

        await Assert.That(act).Throws<ModelParseException>();
    }

    [Test]
    public async Task Read_StreamTooShortForHeader_ThrowsModelParseException()
    {
        byte[] payload = new byte[16]; // Smaller than 32-byte header

        var act = () => VelaBinStreamReader.Read(payload);

        await Assert.That(act).Throws<ModelParseException>();
    }

    private static readonly int[] SampleInputShape = [1, 8, 8, 3, 1, 1];
    private static readonly int[] SampleOutputShape = [1, 3, 1, 1, 1, 1];

    [Test]
    public async Task Read_ValidInputsAndOutputsBlocks_ExtractsIoDescriptors()
    {
        // 1 input tensor: shape [1, 8, 8, 3, 1, 1], elemSize = 1, offset = 0x100, region = 1
        byte[] inputsPayload = BuildIoBlockPayload(new[]
        {
            (SampleInputShape, 1, 0x100, 1)
        });

        // 1 output tensor: shape [1, 3, 1, 1, 1, 1], elemSize = 1, offset = 0x0, region = 1
        byte[] outputsPayload = BuildIoBlockPayload(new[]
        {
            (SampleOutputShape, 1, 0x0, 1)
        });

        byte[] payload = BuildSyntheticVelaStream(
            ("vela_bin_stream", Array.Empty<byte>()),
            ("inputs", inputsPayload),
            ("outputs", outputsPayload),
            ("vela_end_stream", Array.Empty<byte>()));

        VelaBinStream stream = VelaBinStreamReader.Read(payload);

        await Assert.That(stream.Inputs.Count).IsEqualTo(1);
        await Assert.That(stream.Inputs[0].Offset).IsEqualTo(0x100);
        await Assert.That(stream.Inputs[0].Region).IsEqualTo(1);
        await Assert.That(stream.Outputs.Count).IsEqualTo(1);
        await Assert.That(stream.Outputs[0].Offset).IsEqualTo(0x0);
    }

    private static byte[] BuildSyntheticVelaStream(params (string Name, byte[] Data)[] blocks)
    {
        using var ms = new MemoryStream();
        foreach (var (name, data) in blocks)
        {
            byte[] header = new byte[32];
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            Array.Copy(nameBytes, header, Math.Min(nameBytes.Length, 15));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), (uint)data.Length);
            header[20] = 0; // not external
            ms.Write(header, 0, header.Length);

            if (data.Length > 0)
            {
                ms.Write(data, 0, data.Length);
                int pad = (16 - (data.Length % 16)) % 16;
                if (pad > 0)
                {
                    ms.Write(new byte[pad], 0, pad);
                }
            }
        }
        return ms.ToArray();
    }

    private static byte[] BuildIoBlockPayload((int[] Shape, int ElemSize, int Offset, int Region)[] tensors)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(tensors.Length);
        foreach (var (shape, elemSize, offset, region) in tensors)
        {
            for (int i = 0; i < 6; i++)
            {
                bw.Write(i < shape.Length ? shape[i] : 1);
            }
            bw.Write(elemSize);
            bw.Write(offset);
            bw.Write(region);
        }
        return ms.ToArray();
    }
}
