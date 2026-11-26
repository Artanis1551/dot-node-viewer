using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Gguf;

/// <summary>
/// Streaming <see cref="IModelFormatReader"/> for GGUF (<c>.gguf</c>) model files.
/// </summary>
internal sealed class GgufFormatReader : IModelFormatReader
{
    /// <summary>
    /// Gets the shared singleton instance of <see cref="GgufFormatReader"/>.
    /// </summary>
    public static GgufFormatReader Instance { get; } = new();

    private GgufFormatReader()
    {
    }

    /// <inheritdoc />
    public ModelFormatId FormatId => ModelFormatId.Gguf;

    /// <inheritdoc />
    public ValueTask<ModelGraph> ReadAsync(
        Stream stream,
        ModelReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ModelParseException("GGUF stream must be readable and seekable.", fileOffset: 0);
        }

        stream.Position = 0;
        GgufParsedFile parsed = GgufBinaryReader.Read(stream, cancellationToken);
        ModelGraph graph = GgufGraphSynthesizer.Synthesize(parsed);
        return ValueTask.FromResult(graph);
    }
}
