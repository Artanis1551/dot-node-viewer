using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Mlir;

/// <summary>
/// Streaming reader for MLIR Bytecode (<c>.mlirbc</c>) and Textual MLIR (<c>.mlir</c>) intermediate representations.
/// </summary>
internal sealed class MlirFormatReader : IModelFormatReader
{
    /// <summary>
    /// Gets a shared singleton instance of <see cref="MlirFormatReader"/>.
    /// </summary>
    public static MlirFormatReader Instance { get; } = new();

    /// <inheritdoc/>
    public ModelFormatId FormatId => ModelFormatId.Mlir;

    /// <inheritdoc/>
    public ValueTask<ModelGraph> ReadAsync(
        Stream stream,
        ModelReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ModelParseException("Model stream must be readable and seekable to parse MLIR files.");
        }

        ModelReadOptions effectiveOptions = options ?? ModelReadOptions.Default;

        try
        {
            using RandomAccessSource source = new(stream);
            if (source.Length >= 4)
            {
                Span<byte> magicScratch = stackalloc byte[4];
                ReadOnlySpan<byte> header = source.GetSpan(0, 4, magicScratch);
                if (MlirFormatDetector.IsBytecodeMagic(header))
                {
                    return ValueTask.FromResult(MlirBytecodeReader.Read(source, effectiveOptions, cancellationToken));
                }
            }

            return ValueTask.FromResult(MlirTextParser.Read(source, effectiveOptions, cancellationToken));
        }
        catch (ModelParseException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is EndOfStreamException or OverflowException or FormatException or InvalidDataException)
        {
            throw new ModelParseException(
                $"Malformed MLIR stream: {ex.Message}",
                fileOffset: stream.CanSeek ? stream.Position : null,
                context: effectiveOptions.FileName,
                innerException: ex);
        }
    }
}
