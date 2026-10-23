namespace DotNodeViewer.Layout;

/// <summary>
/// Provides a UI-independent abstraction for measuring rendered text widths during node and label sizing in <see cref="ModelGraphLayoutAdapter"/>.
/// </summary>
public interface ITextMeasurer
{
    /// <summary>
    /// Measures the width (in layout pixels) of the specified <paramref name="text"/> at the given <paramref name="fontSize"/>.
    /// </summary>
    /// <param name="text">The single-line string to measure.</param>
    /// <param name="fontSize">The font size in logical pixels.</param>
    /// <param name="isBold">Whether the text is rendered with a bold font weight.</param>
    /// <returns>The measured width in layout pixels.</returns>
    double MeasureTextWidth(string text, double fontSize, bool isBold = false);
}

/// <summary>
/// Provides a deterministic, zero-dependency character-width text measurer for headless layout calculation and unit tests.
/// </summary>
public sealed class MonospaceTextMeasurer : ITextMeasurer
{
    private readonly double _regularEmRatio;
    private readonly double _boldEmRatio;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonospaceTextMeasurer"/> class.
    /// </summary>
    /// <param name="regularEmRatio">The width-to-fontSize ratio per character for regular weight text (default <c>0.58</c>).</param>
    /// <param name="boldEmRatio">The width-to-fontSize ratio per character for bold weight text (default <c>0.64</c>).</param>
    public MonospaceTextMeasurer(double regularEmRatio = 0.58, double boldEmRatio = 0.64)
    {
        _regularEmRatio = regularEmRatio > 0 ? regularEmRatio : 0.58;
        _boldEmRatio = boldEmRatio > 0 ? boldEmRatio : 0.64;
    }

    /// <summary>
    /// Gets a shared singleton instance of <see cref="MonospaceTextMeasurer"/>.
    /// </summary>
    public static MonospaceTextMeasurer Default { get; } = new();

    /// <inheritdoc/>
    public double MeasureTextWidth(string text, double fontSize, bool isBold = false)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0)
        {
            return 0.0;
        }

        double ratio = isBold ? _boldEmRatio : _regularEmRatio;
        return text.Length * fontSize * ratio;
    }
}
