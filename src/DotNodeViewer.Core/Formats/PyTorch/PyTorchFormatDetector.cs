using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.PyTorch;

/// <summary>
/// Detects PyTorch ZIP container checkpoints, TorchScript archives, and legacy pickle files.
/// </summary>
internal sealed class PyTorchFormatDetector : IModelFormatDetector
{
    /// <summary>
    /// Gets a shared singleton instance of <see cref="PyTorchFormatDetector"/>.
    /// </summary>
    public static PyTorchFormatDetector Instance { get; } = new();

    /// <inheritdoc/>
    public ModelFormatInfo Format { get; } = new(ModelFormatId.PyTorch, "PyTorch (.pt/.pth)");

    /// <inheritdoc/>
    public bool CanRead(ModelProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (probe.StreamLength < 4)
        {
            return false;
        }

        // Case A: ZIP container format (.pt, .pth, TorchScript)
        if (probe.IsZipArchive)
        {
            // Exclude Stage S09 (.pt2 ExportedProgram archives)
            if (probe.HasZipEntryEndingWith("archive_format") ||
                probe.HasZipEntryEndingWith("serialized_exported_program.json"))
            {
                return false;
            }

            // Exclude Stage S10 (Keras v3 archives with config.json)
            if (probe.HasZipEntry("config.json"))
            {
                return false;
            }

            // Must contain data.pkl
            if (probe.HasZipEntryEndingWith("data.pkl"))
            {
                // Verify presence of PyTorch markers: version, byteorder, code directory, or data directory
                if (probe.HasZipEntryEndingWith("version") ||
                    probe.HasZipEntryEndingWith("byteorder") ||
                    probe.HasZipEntryEndingWith(".format_version") ||
                    probe.ContainsZipEntry("/data/") ||
                    probe.ContainsZipEntry("/code/"))
                {
                    return true;
                }
            }
        }

        // Case B: Raw uncompressed Python pickle file (Protocols 2-5)
        ReadOnlySpan<byte> header = probe.Header;
        if (header.Length >= 2 && header[0] == 0x80 && header[1] >= 0x02 && header[1] <= 0x05)
        {
            int probeLength = (int)Math.Min(probe.StreamLength, 1024);
            Span<byte> probeBuf = stackalloc byte[probeLength];
            int read = probe.ReadAt(0, probeBuf);
            if (read > 0)
            {
                string probeText = Encoding.Latin1.GetString(probeBuf[..read]);
                if (probeText.Contains("torch", StringComparison.Ordinal) ||
                    probeText.Contains("_rebuild_tensor", StringComparison.Ordinal) ||
                    probeText.Contains("_rebuild_parameter", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
