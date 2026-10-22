using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Abstractions;

/// <summary>
/// Inspects a <see cref="ModelProbe"/> to determine whether a model stream matches a specific neural network format.
/// </summary>
public interface IModelFormatDetector
{
    /// <summary>
    /// Gets the format descriptor associated with this detector.
    /// </summary>
    /// <value>The <see cref="ModelFormatInfo"/> identified by this detector.</value>
    ModelFormatInfo Format { get; }

    /// <summary>
    /// Determines whether the model stream represented by <paramref name="probe"/> can be read by this format's parser.
    /// </summary>
    /// <param name="probe">The non-destructive probe over the candidate model stream.</param>
    /// <returns><see langword="true"/> if the stream matches this model format; otherwise, <see langword="false"/>.</returns>
    bool CanRead(ModelProbe probe);
}

/// <summary>
/// Reads a seekable neural network model <see cref="Stream"/> and extracts its <see cref="ModelGraph"/> topology without eagerly loading raw weight buffers.
/// </summary>
public interface IModelFormatReader
{
    /// <summary>
    /// Gets the identifier of the model format handled by this reader.
    /// </summary>
    /// <value>The target <see cref="ModelFormatId"/>.</value>
    ModelFormatId FormatId { get; }

    /// <summary>
    /// Asynchronously parses the model topology, attributes, and lazy weight slices from <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">The readable, seekable stream containing the model file.</param>
    /// <param name="options">Optional configuration and file name hints for parsing.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous parse operation and containing the extracted <see cref="ModelGraph"/>.</returns>
    ValueTask<ModelGraph> ReadAsync(
        Stream stream,
        ModelReadOptions? options = null,
        CancellationToken cancellationToken = default);
}

