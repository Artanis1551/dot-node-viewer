using DotNodeViewer.App.Rendering;

namespace DotNodeViewer.App.Tests;

/// <summary>
/// Unit tests for <see cref="AvaloniaTextMeasurer"/>.
/// </summary>
public sealed class AvaloniaTextMeasurerTests
{
    [Test]
    public async Task MeasureTextWidth_NullOrEmptyString_ReturnsZero()
    {
        AvaloniaTextMeasurer measurer = AvaloniaTextMeasurer.Default;

        await Assert.That(measurer.MeasureTextWidth(string.Empty, 12.0)).IsEqualTo(0.0);
        await Assert.That(measurer.MeasureTextWidth(null!, 12.0)).IsEqualTo(0.0);
        await Assert.That(measurer.MeasureTextWidth("Conv", 0.0)).IsEqualTo(0.0);
    }

    [Test]
    public async Task MeasureTextWidth_ValidString_ReturnsPositiveProportionalMetrics()
    {
        AvaloniaTextMeasurer measurer = AvaloniaTextMeasurer.Default;

        double shortWidth = measurer.MeasureTextWidth("Conv", 12.0);
        double longWidth = measurer.MeasureTextWidth("ConvolutionTranspose2DWithFusedBias", 12.0);

        await Assert.That(shortWidth).IsGreaterThan(0.0);
        await Assert.That(longWidth).IsGreaterThan(shortWidth);
    }

    [Test]
    public async Task MeasureTextWidth_BoldText_IsWiderThanRegular()
    {
        AvaloniaTextMeasurer measurer = AvaloniaTextMeasurer.Default;

        double normal = measurer.MeasureTextWidth("MaxPool", 14.0, isBold: false);
        double bold = measurer.MeasureTextWidth("MaxPool", 14.0, isBold: true);

        await Assert.That(normal).IsGreaterThan(0.0);
        await Assert.That(bold).IsGreaterThanOrEqualTo(normal);
    }
}

