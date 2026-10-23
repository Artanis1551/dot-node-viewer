using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Media;
using DotNodeViewer.Layout;

namespace DotNodeViewer.App.Rendering;

/// <summary>
/// Implements <see cref="ITextMeasurer"/> using Avalonia's <see cref="FormattedText"/> layout engine so that
/// calculated node dimensions match the exact rendered font metrics on the active platform.
/// </summary>
public sealed class AvaloniaTextMeasurer : ITextMeasurer
{
    private readonly FontFamily _fontFamily;
    private readonly ConcurrentDictionary<(string Text, int FontSizeInt, bool IsBold), double> _measurementCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AvaloniaTextMeasurer"/> class with an optional custom font family.
    /// </summary>
    /// <param name="fontFamily">The font family to measure with; defaults to the application sans-serif stack.</param>
    public AvaloniaTextMeasurer(FontFamily? fontFamily = null)
    {
        _fontFamily = fontFamily ?? new FontFamily("Inter, -apple-system, BlinkMacSystemFont, Segoe UI, Ubuntu, Cantarell, sans-serif");
    }

    /// <summary>
    /// Gets a shared singleton instance of <see cref="AvaloniaTextMeasurer"/>.
    /// </summary>
    public static AvaloniaTextMeasurer Default { get; } = new();

    /// <inheritdoc/>
    public double MeasureTextWidth(string text, double fontSize, bool isBold = false)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0)
        {
            return 0.0;
        }

        // Quantize font size to tenths of a pixel for stable cache keys
        int sizeKey = (int)Math.Round(fontSize * 10.0);
        var key = (text, sizeKey, isBold);

        if (_measurementCache.TryGetValue(key, out double cachedWidth))
        {
            return cachedWidth;
        }

        double width;
        try
        {
            Typeface typeface = new(
                _fontFamily,
                FontStyle.Normal,
                isBold ? FontWeight.SemiBold : FontWeight.Normal);

            FormattedText formattedText = new(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                null);

            width = formattedText.Width;
        }
        catch
        {
            width = MonospaceTextMeasurer.Default.MeasureTextWidth(text, fontSize, isBold);
        }

        // Keep cache bounded
        if (_measurementCache.Count < 4096)
        {
            _measurementCache.TryAdd(key, width);
        }

        return width;
    }
}

