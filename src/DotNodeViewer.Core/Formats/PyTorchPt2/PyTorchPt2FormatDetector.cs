using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.PyTorchPt2;

/// <summary>
/// Detects PyTorch 2 <c>torch.export</c> (<c>.pt2</c>) archives.
/// </summary>
internal sealed class PyTorchPt2FormatDetector : IModelFormatDetector
{
    /// <summary>
    /// Gets a shared singleton instance of <see cref="PyTorchPt2FormatDetector"/>.
    /// </summary>
    public static PyTorchPt2FormatDetector Instance { get; } = new();

    /// <inheritdoc/>
    public ModelFormatInfo Format { get; } = new(ModelFormatId.PyTorchPt2, "PyTorch ExportedProgram (.pt2)");

    /// <inheritdoc/>
    public bool CanRead(ModelProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (!probe.IsZipArchive)
        {
            return false;
        }

        // Must contain archive_format (pt2) or ExportedProgram model.json / serialized_exported_program.json
        return probe.HasZipEntryEndingWith("archive_format") ||
               probe.HasZipEntryEndingWith("models/model.json") ||
               probe.HasZipEntryEndingWith("serialized_exported_program.json");
    }
}

