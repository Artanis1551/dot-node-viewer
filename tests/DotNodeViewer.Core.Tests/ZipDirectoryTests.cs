using System.IO.Compression;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.IO.Zip;

namespace DotNodeViewer.Core.Tests;

/// <summary>
/// Unit tests for <see cref="ZipDirectory"/> container scanning, offset calculation, and entry stream access.
/// </summary>
public sealed class ZipDirectoryTests
{
    [Test]
    public async Task Read_ValidZipWithStoredAndDeflatedEntries_ParsesOffsetsAndReadsContent()
    {
        using MemoryStream ms = new();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var storedEntry = archive.CreateEntry("data/0", CompressionLevel.NoCompression);
            using (var s = storedEntry.Open())
            {
                s.Write("WEIGHT_BYTES_PAYLOAD"u8);
            }

            var deflatedEntry = archive.CreateEntry("code/model.py", CompressionLevel.Optimal);
            using (var s = deflatedEntry.Open())
            {
                s.Write("def forward(x): return x"u8);
            }
        }

        using var source = new RandomAccessSource(ms);
        bool success = ZipDirectory.TryRead(source, out var zip);
        await Assert.That(success).IsTrue();
        await Assert.That(zip).IsNotNull();

        await Assert.That(zip!.Entries.ContainsKey("data/0")).IsTrue();
        await Assert.That(zip.Entries.ContainsKey("code/model.py")).IsTrue();

        var stored = zip.Entries["data/0"];
        await Assert.That(stored.IsStored).IsTrue();
        await Assert.That(stored.DataOffset > 0).IsTrue();
        await Assert.That(stored.UncompressedSize).IsEqualTo("WEIGHT_BYTES_PAYLOAD"u8.Length);

        string storedText = zip.ReadEntryText(stored);
        await Assert.That(storedText).IsEqualTo("WEIGHT_BYTES_PAYLOAD");

        var deflated = zip.Entries["code/model.py"];
        await Assert.That(deflated.UncompressedSize).IsEqualTo("def forward(x): return x"u8.Length);
        string deflatedText = zip.ReadEntryText(deflated);
        await Assert.That(deflatedText).IsEqualTo("def forward(x): return x");

        // Verify OpenEntryStream
        using var entryStream = zip.OpenEntryStream(stored);
        byte[] buf = new byte[stored.UncompressedSize];
        entryStream.ReadExactly(buf);
        await Assert.That(Encoding.UTF8.GetString(buf)).IsEqualTo("WEIGHT_BYTES_PAYLOAD");
    }

    [Test]
    public async Task TryRead_InvalidOrTruncatedArchive_ReturnsFalseWithoutThrowing()
    {
        using MemoryStream empty = new(new byte[10]);
        using var emptySource = new RandomAccessSource(empty);
        bool emptyOk = ZipDirectory.TryRead(emptySource, out var emptyZip);
        await Assert.That(emptyOk).IsFalse();
        await Assert.That(emptyZip).IsNull();

        byte[] garbageBytes = new byte[100];
        Random.Shared.NextBytes(garbageBytes);
        using MemoryStream garbage = new(garbageBytes);
        using var garbageSource = new RandomAccessSource(garbage);
        bool garbageOk = ZipDirectory.TryRead(garbageSource, out var garbageZip);
        await Assert.That(garbageOk).IsFalse();
        await Assert.That(garbageZip).IsNull();
    }

    [Test]
    public async Task Read_CorruptedEocd_ThrowsModelParseException()
    {
        byte[] corruptedEocd = new byte[30];
        // Write EOCD signature at offset 0
        corruptedEocd[0] = 0x50;
        corruptedEocd[1] = 0x4B;
        corruptedEocd[2] = 0x05;
        corruptedEocd[3] = 0x06;
        // Central directory offset 0xFFFF_FFFF out of bounds
        corruptedEocd[16] = 0xFF;
        corruptedEocd[17] = 0xFF;
        corruptedEocd[18] = 0xFF;
        corruptedEocd[19] = 0xFF;

        using MemoryStream ms = new(corruptedEocd);
        using var source = new RandomAccessSource(ms);

        await Assert.That(() => ZipDirectory.Read(source))
            .Throws<ModelParseException>();
    }
}
