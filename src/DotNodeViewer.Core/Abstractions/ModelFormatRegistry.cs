using DotNodeViewer.Core.Formats.Onnx;
using DotNodeViewer.Core.Formats.Tflite;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Abstractions;

/// <summary>
/// Maintains an explicit, reflection-free registry of <see cref="IModelFormatDetector"/> and <see cref="IModelFormatReader"/> implementations.
/// </summary>
public sealed class ModelFormatRegistry
{
    private readonly List<RegistrationEntry> _entries = [];
    private readonly Lock _syncRoot = new();

    /// <summary>
    /// Gets the default shared <see cref="ModelFormatRegistry"/> containing all built-in model format parsers.
    /// </summary>
    /// <value>The default <see cref="ModelFormatRegistry"/> instance.</value>
    public static ModelFormatRegistry Default { get; } = CreateDefault();

    /// <summary>
    /// Gets a snapshot of all model formats currently registered in this registry.
    /// </summary>
    /// <value>A read-only list of registered <see cref="ModelFormatInfo"/> descriptors in detection priority order.</value>
    public IReadOnlyList<ModelFormatInfo> RegisteredFormats
    {
        get
        {
            lock (_syncRoot)
            {
                ModelFormatInfo[] result = new ModelFormatInfo[_entries.Count];
                for (int i = 0; i < _entries.Count; i++)
                {
                    result[i] = _entries[i].Detector.Format;
                }

                return result;
            }
        }
    }

    /// <summary>
    /// Registers a format detector and its corresponding streaming reader.
    /// </summary>
    /// <param name="detector">The detector used to probe candidate model streams.</param>
    /// <param name="reader">The reader used to extract the <see cref="ModelGraph"/> when <paramref name="detector"/> matches.</param>
    /// <exception cref="ArgumentNullException"><paramref name="detector"/> or <paramref name="reader"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The detector and reader <see cref="ModelFormatId"/> values do not match.</exception>
    public void Register(IModelFormatDetector detector, IModelFormatReader reader)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(reader);

        if (!string.Equals(detector.Format.Id.Value, reader.FormatId.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Detector format ID '{detector.Format.Id}' does not match reader format ID '{reader.FormatId}'.",
                nameof(reader));
        }

        lock (_syncRoot)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (string.Equals(_entries[i].Detector.Format.Id.Value, detector.Format.Id.Value, StringComparison.OrdinalIgnoreCase))
                {
                    _entries[i] = new RegistrationEntry(detector, reader);
                    return;
                }
            }

            _entries.Add(new RegistrationEntry(detector, reader));
        }
    }

    /// <summary>
    /// Probes <paramref name="probe"/> against all registered detectors in registration order and returns the first matching detector.
    /// </summary>
    /// <param name="probe">The non-destructive probe over the model stream.</param>
    /// <returns>The first matching <see cref="IModelFormatDetector"/>, or <see langword="null"/> if no registered format matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> is <see langword="null"/>.</exception>
    public IModelFormatDetector? TryDetect(ModelProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        RegistrationEntry[] snapshot = GetSnapshot();
        for (int i = 0; i < snapshot.Length; i++)
        {
            IModelFormatDetector detector = snapshot[i].Detector;
            try
            {
                if (detector.CanRead(probe))
                {
                    return detector;
                }
            }
            finally
            {
                probe.RestorePosition();
            }
        }

        return null;
    }

    /// <summary>
    /// Probes <paramref name="stream"/> and returns the detected <see cref="ModelFormatInfo"/> while restoring <see cref="Stream.Position"/>.
    /// </summary>
    /// <param name="stream">The readable, seekable model stream to inspect.</param>
    /// <param name="fileName">An optional file name or path hint.</param>
    /// <returns>The detected <see cref="ModelFormatInfo"/>, or <see langword="null"/> if no registered format matches.</returns>
    public ModelFormatInfo? TryDetect(Stream stream, string? fileName = null)
    {
        ModelProbe probe = ModelProbe.Create(stream, fileName);
        return TryDetect(probe)?.Format;
    }

    /// <summary>
    /// Retrieves the registered <see cref="IModelFormatReader"/> for the specified <paramref name="formatId"/>.
    /// </summary>
    /// <param name="formatId">The format identifier to look up.</param>
    /// <returns>The registered <see cref="IModelFormatReader"/>, or <see langword="null"/> if the format is not registered.</returns>
    public IModelFormatReader? TryGetReader(ModelFormatId formatId)
    {
        RegistrationEntry[] snapshot = GetSnapshot();
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (string.Equals(snapshot[i].Reader.FormatId.Value, formatId.Value, StringComparison.OrdinalIgnoreCase))
            {
                return snapshot[i].Reader;
            }
        }

        return null;
    }

    /// <summary>
    /// Detects the format of <paramref name="stream"/> and parses its <see cref="ModelGraph"/> using the matching <see cref="IModelFormatReader"/>.
    /// </summary>
    /// <param name="stream">The readable, seekable stream containing the model file.</param>
    /// <param name="options">Optional parsing options and file name hints.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous parse operation and containing the extracted <see cref="ModelGraph"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ModelParseException">The stream is not seekable, no registered format detector matched the stream, or parsing failed.</exception>
    public async ValueTask<ModelGraph> ReadAsync(
        Stream stream,
        ModelReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ModelReadOptions effectiveOptions = options ?? ModelReadOptions.Default;

        ModelProbe probe = await ModelProbe.CreateAsync(stream, effectiveOptions.FileName, cancellationToken).ConfigureAwait(false);
        IModelFormatDetector? detector = TryDetect(probe);
        if (detector is null)
        {
            string fileHint = string.IsNullOrWhiteSpace(effectiveOptions.FileName)
                ? "stream"
                : $"'{effectiveOptions.FileName}'";
            throw new ModelParseException(
                $"Unsupported or unrecognized neural network model format in {fileHint}.",
                fileOffset: 0,
                context: effectiveOptions.FileName);
        }

        IModelFormatReader? reader = TryGetReader(detector.Format.Id);
        if (reader is null)
        {
            throw new ModelParseException(
                $"No format reader is registered for detected format '{detector.Format.DisplayName}' ({detector.Format.Id}).",
                fileOffset: 0,
                context: detector.Format.Id.Value);
        }

        stream.Position = 0;
        return await reader.ReadAsync(stream, effectiveOptions, cancellationToken).ConfigureAwait(false);
    }

    private static ModelFormatRegistry CreateDefault()
    {
        ModelFormatRegistry registry = new();
        registry.Register(TfliteFormatDetector.Instance, TfliteFormatReader.Instance);
        registry.Register(OnnxFormatDetector.Instance, OnnxFormatReader.Instance);
        return registry;
    }

    private RegistrationEntry[] GetSnapshot()
    {
        lock (_syncRoot)
        {
            return [.. _entries];
        }
    }

    private readonly record struct RegistrationEntry(
        IModelFormatDetector Detector,
        IModelFormatReader Reader);
}

