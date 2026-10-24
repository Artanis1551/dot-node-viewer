using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Keras;

/// <summary>
/// Detects Keras v3 (<c>.keras</c>) ZIP archives and legacy HDF5 (<c>.h5</c>) models.
/// </summary>
internal sealed class KerasFormatDetector : IModelFormatDetector
{
    private static readonly byte[] Hdf5Magic = [0x89, 0x48, 0x44, 0x46, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Gets a shared singleton instance of <see cref="KerasFormatDetector"/>.
    /// </summary>
    public static KerasFormatDetector Instance { get; } = new();

    /// <inheritdoc/>
    public ModelFormatInfo Format { get; } = new(ModelFormatId.Keras, "Keras (.keras/.h5)");

    /// <inheritdoc/>
    public bool CanRead(ModelProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (probe.StreamLength < 8)
        {
            return false;
        }

        // Case A: Modern Keras 3 (.keras) ZIP archive
        if (probe.IsZipArchive)
        {
            // Must contain config.json
            bool hasConfig = probe.HasZipEntry("config.json") ||
                             probe.HasZipEntryEndingWith("/config.json");

            // Must contain metadata.json or model.weights.h5 or have .keras extension
            bool hasKerasMarker = probe.HasZipEntry("metadata.json") ||
                                  probe.HasZipEntryEndingWith("model.weights.h5");

            if (hasConfig && (hasKerasMarker || (probe.FileName != null && probe.FileName.EndsWith(".keras", StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        // Case B: HDF5 (.h5 / .hdf5 / .weights.h5) container
        ReadOnlySpan<byte> header = probe.Header;
        if (header.Length >= 8 && header[..8].SequenceEqual(Hdf5Magic))
        {
            if (probe.FileName != null &&
                (probe.FileName.EndsWith(".h5", StringComparison.OrdinalIgnoreCase) ||
                 probe.FileName.EndsWith(".hdf5", StringComparison.OrdinalIgnoreCase) ||
                 probe.FileName.EndsWith(".keras", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // Inspect probe buffer for Keras indicators
            int probeLen = (int)Math.Min(probe.StreamLength, 4096);
            Span<byte> buf = stackalloc byte[probeLen];
            int read = probe.ReadAt(0, buf);
            if (read > 0)
            {
                string text = Encoding.Latin1.GetString(buf[..read]);
                if (text.Contains("model_config") ||
                    text.Contains("model_weights") ||
                    text.Contains("keras_version") ||
                    text.Contains("layer_names") ||
                    text.Contains("_layer_checkpoint_dependencies"))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

