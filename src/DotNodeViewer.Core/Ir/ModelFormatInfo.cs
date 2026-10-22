namespace DotNodeViewer.Core.Ir;

/// <summary>
/// Represents a stable, case-insensitive identifier for a neural network model format.
/// </summary>
/// <param name="Value">The canonical lowercase identifier string (for example, <c>"onnx"</c> or <c>"tflite"</c>).</param>
public readonly record struct ModelFormatId(string Value)
{
    /// <summary>
    /// Gets the format identifier for Open Neural Network Exchange (ONNX) models.
    /// </summary>
    /// <value>The ONNX format identifier.</value>
    public static ModelFormatId Onnx => new("onnx");

    /// <summary>
    /// Gets the format identifier for TensorFlow Lite (<c>TFL3</c>) models.
    /// </summary>
    /// <value>The TensorFlow Lite format identifier.</value>
    public static ModelFormatId Tflite => new("tflite");

    /// <summary>
    /// Gets the format identifier for ExecuTorch (<c>.pte</c>) models.
    /// </summary>
    /// <value>The ExecuTorch format identifier.</value>
    public static ModelFormatId ExecuTorch => new("executorch");

    /// <summary>
    /// Gets the format identifier for PyTorch TorchScript and checkpoint (<c>.pt</c> / <c>.pth</c>) models.
    /// </summary>
    /// <value>The PyTorch format identifier.</value>
    public static ModelFormatId PyTorch => new("pytorch");

    /// <summary>
    /// Gets the format identifier for PyTorch 2 <c>torch.export</c> (<c>.pt2</c>) archives.
    /// </summary>
    /// <value>The PyTorch <c>.pt2</c> format identifier.</value>
    public static ModelFormatId PyTorchPt2 => new("pytorch-pt2");

    /// <summary>
    /// Gets the format identifier for Keras (<c>.keras</c> and <c>.h5</c>) models.
    /// </summary>
    /// <value>The Keras format identifier.</value>
    public static ModelFormatId Keras => new("keras");

    /// <summary>
    /// Returns the string representation of the format identifier.
    /// </summary>
    /// <returns>The underlying <see cref="Value"/>, or an empty string if uninitialized.</returns>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// Describes the detected format and schema version of a parsed neural network model.
/// </summary>
/// <param name="Id">The canonical format identifier.</param>
/// <param name="DisplayName">The human-readable display name of the format (for example, <c>"ONNX"</c> or <c>"TensorFlow Lite"</c>).</param>
/// <param name="Version">An optional version string for the container or IR schema (for example, <c>"v3"</c> or <c>"IR v9"</c>).</param>
public sealed record ModelFormatInfo(
    ModelFormatId Id,
    string DisplayName,
    string? Version = null)
{
    /// <summary>
    /// Formats the display name and optional version into a single summary string.
    /// </summary>
    /// <returns>A formatted string combining <see cref="DisplayName"/> and <see cref="Version"/> when present.</returns>
    public string ToDisplayString() =>
        string.IsNullOrWhiteSpace(Version) ? DisplayName : $"{DisplayName} {Version}";
}

