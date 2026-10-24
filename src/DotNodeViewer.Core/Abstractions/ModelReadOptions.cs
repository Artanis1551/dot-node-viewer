namespace DotNodeViewer.Core.Abstractions;

/// <summary>
/// Configures limits and hints used when detecting and reading a neural network model stream.
/// </summary>
public sealed record ModelReadOptions
{
    /// <summary>
    /// Gets a shared default <see cref="ModelReadOptions"/> instance.
    /// </summary>
    /// <value>The default <see cref="ModelReadOptions"/> instance.</value>
    public static ModelReadOptions Default { get; } = new();

    /// <summary>
    /// Gets the optional file name or path hint associated with the model stream.
    /// </summary>
    /// <value>The file name hint, or <see langword="null"/> if reading from an anonymous stream.</value>
    public string? FileName { get; init; }

    /// <summary>
    /// Gets the maximum number of bytes allowed for a single in-memory metadata section (such as a JSON header or pickle stream).
    /// </summary>
    /// <value>The maximum metadata budget in bytes. The default is 67,108,864 (64 MB).</value>
    public long MaxMetadataBytes { get; init; } = 64L * 1024L * 1024L;

    /// <summary>
    /// Gets the maximum recursion depth allowed when parsing nested control-flow or module subgraphs.
    /// </summary>
    /// <value>The maximum nested subgraph depth. The default is 16.</value>
    public int MaxSubgraphDepth { get; init; } = 16;

    /// <summary>
    /// Gets the memoized ZIP directory pre-parsed during non-destructive stream probing, if available.
    /// </summary>
    internal DotNodeViewer.Core.IO.Zip.ZipDirectory? CachedZipDirectory { get; init; }
}

