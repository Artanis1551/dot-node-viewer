using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Layout;

namespace DotNodeViewer.App.Controls;

/// <summary>
/// A high-performance interactive minimap overlay control rendering a bird's-eye silhouette of the graph
/// and a draggable viewport indicator.
/// </summary>
public sealed class MinimapControl : Control
{
    private const double ExpandedWidth = 190.0;
    private const double ExpandedHeight = 125.0;
    private const double CollapsedSize = 32.0;
    private const double ContentPadding = 8.0;

    /// <summary>
    /// Defines the <see cref="Layout"/> property.
    /// </summary>
    public static readonly StyledProperty<VisualSubgraphLayout?> LayoutProperty =
        AvaloniaProperty.Register<MinimapControl, VisualSubgraphLayout?>(nameof(Layout));

    /// <summary>
    /// Defines the <see cref="Zoom"/> property.
    /// </summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<MinimapControl, double>(nameof(Zoom), defaultValue: 1.0);

    /// <summary>
    /// Defines the <see cref="PanOffset"/> property.
    /// </summary>
    public static readonly StyledProperty<Vector> PanOffsetProperty =
        AvaloniaProperty.Register<MinimapControl, Vector>(
            nameof(PanOffset),
            defaultValue: new Vector(0, 0),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// Defines the <see cref="ViewportSize"/> property.
    /// </summary>
    public static readonly StyledProperty<Size> ViewportSizeProperty =
        AvaloniaProperty.Register<MinimapControl, Size>(nameof(ViewportSize), defaultValue: new Size(800, 600));

    /// <summary>
    /// Defines the <see cref="IsCollapsed"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsCollapsedProperty =
        AvaloniaProperty.Register<MinimapControl, bool>(
            nameof(IsCollapsed),
            defaultValue: false,
            defaultBindingMode: BindingMode.TwoWay);

    private bool _isDraggingViewport;

    static MinimapControl()
    {
        AffectsRender<MinimapControl>(LayoutProperty, ZoomProperty, PanOffsetProperty, ViewportSizeProperty, IsCollapsedProperty);
        ClipToBoundsProperty.OverrideDefaultValue<MinimapControl>(true);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MinimapControl"/> class.
    /// </summary>
    public MinimapControl()
    {
        Width = ExpandedWidth;
        Height = ExpandedHeight;
    }

    /// <summary>
    /// Gets or sets the active positioned <see cref="VisualSubgraphLayout"/> to render in the minimap.
    /// </summary>
    public VisualSubgraphLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>
    /// Gets or sets the zoom scale factor of the primary canvas viewport.
    /// </summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>
    /// Gets or sets the pan translation vector of the primary canvas viewport.
    /// </summary>
    public Vector PanOffset
    {
        get => GetValue(PanOffsetProperty);
        set => SetValue(PanOffsetProperty, value);
    }

    /// <summary>
    /// Gets or sets the visible pixel size of the primary canvas viewport.
    /// </summary>
    public Size ViewportSize
    {
        get => GetValue(ViewportSizeProperty);
        set => SetValue(ViewportSizeProperty, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the minimap is collapsed to a compact button.
    /// </summary>
    public bool IsCollapsed
    {
        get => GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCollapsedProperty)
        {
            bool collapsed = change.GetNewValue<bool>();
            Width = collapsed ? CollapsedSize : ExpandedWidth;
            Height = collapsed ? CollapsedSize : ExpandedHeight;
        }
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        bool isDark = ActualThemeVariant == ThemeVariant.Dark;
        IBrush bgBrush = new SolidColorBrush(isDark ? Color.FromArgb(230, 24, 27, 36) : Color.FromArgb(230, 255, 255, 255));
        IBrush borderBrush = new SolidColorBrush(isDark ? Color.FromRgb(48, 54, 69) : Color.FromRgb(220, 225, 235));
        IPen borderPen = new Pen(borderBrush, 1.0);

        if (IsCollapsed)
        {
            // Render compact toggle pill
            context.DrawRectangle(bgBrush, borderPen, new Rect(0, 0, Width, Height), 6.0, 6.0);
            IBrush iconBrush = new SolidColorBrush(isDark ? Color.FromRgb(140, 150, 170) : Color.FromRgb(100, 110, 130));
            // Small 4-square grid icon representing minimap
            context.DrawRectangle(iconBrush, null, new Rect(7, 7, 7, 7), 1.0, 1.0);
            context.DrawRectangle(iconBrush, null, new Rect(17, 7, 7, 7), 1.0, 1.0);
            context.DrawRectangle(iconBrush, null, new Rect(7, 17, 7, 7), 1.0, 1.0);
            context.DrawRectangle(iconBrush, null, new Rect(17, 17, 7, 7), 1.0, 1.0);
            return;
        }

        // Render card background and border
        Rect cardRect = new(0, 0, Width, Height);
        context.DrawRectangle(bgBrush, borderPen, cardRect, 8.0, 8.0);

        // Header / label strip
        IBrush labelBrush = new SolidColorBrush(isDark ? Color.FromRgb(110, 120, 140) : Color.FromRgb(130, 140, 160));
        FormattedText mapLabel = new(
            "MINIMAP",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter, sans-serif", FontStyle.Normal, FontWeight.SemiBold),
            8.5,
            labelBrush);
        context.DrawText(mapLabel, new Point(8, 6));

        // Collapse button indicator in top-right
        Rect closeTarget = new(Width - 20, 4, 16, 16);
        context.DrawLine(new Pen(labelBrush, 1.2), new Point(closeTarget.Left + 3, closeTarget.Top + 8), new Point(closeTarget.Right - 3, closeTarget.Top + 8));

        if (Layout is null)
        {
            return;
        }

        BoundingBox gBounds = Layout.Bounds;
        if (gBounds.Width <= 0 || gBounds.Height <= 0)
        {
            return;
        }

        // Available area for graph silhouette
        double availX = ContentPadding;
        double availY = 22.0;
        double availW = Width - (ContentPadding * 2.0);
        double availH = Height - availY - ContentPadding;

        double scaleX = availW / gBounds.Width;
        double scaleY = availH / gBounds.Height;
        double mapScale = Math.Min(scaleX, scaleY);

        double offsetX = availX + (availW - (gBounds.Width * mapScale)) * 0.5 - (gBounds.X * mapScale);
        double offsetY = availY + (availH - (gBounds.Height * mapScale)) * 0.5 - (gBounds.Y * mapScale);

        // Draw simplified node silhouettes
        IBrush nodeBrush = new SolidColorBrush(isDark ? Color.FromArgb(160, 70, 80, 105) : Color.FromArgb(160, 180, 190, 210));
        IReadOnlyList<VisualNode> nodes = Layout.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            BoundingBox nb = nodes[i].Bounds;
            double nx = nb.X * mapScale + offsetX;
            double ny = nb.Y * mapScale + offsetY;
            double nw = Math.Max(2.0, nb.Width * mapScale);
            double nh = Math.Max(2.0, nb.Height * mapScale);

            context.DrawRectangle(nodeBrush, null, new Rect(nx, ny, nw, nh), 1.0, 1.0);
        }

        // Compute viewport rectangle in world coordinates
        double z = Zoom > 0.0001 ? Zoom : 1.0;
        double vwX = -PanOffset.X / z;
        double vwY = -PanOffset.Y / z;
        double vwW = ViewportSize.Width / z;
        double vwH = ViewportSize.Height / z;

        // Map viewport to minimap coordinates
        double rx = vwX * mapScale + offsetX;
        double ry = vwY * mapScale + offsetY;
        double rw = Math.Max(6.0, vwW * mapScale);
        double rh = Math.Max(6.0, vwH * mapScale);

        IBrush vpFill = new SolidColorBrush(isDark ? Color.FromArgb(40, 56, 139, 253) : Color.FromArgb(30, 9, 105, 218));
        IPen vpBorder = new Pen(new SolidColorBrush(isDark ? Color.FromArgb(200, 56, 139, 253) : Color.FromArgb(200, 9, 105, 218)), 1.5);

        // Clip viewport rendering to minimap content region
        using (context.PushClip(new Rect(availX - 1, availY - 1, availW + 2, availH + 2)))
        {
            context.DrawRectangle(vpFill, vpBorder, new Rect(rx, ry, rw, rh), 2.0, 2.0);
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        PointerPoint pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsLeftButtonPressed)
        {
            if (IsCollapsed)
            {
                IsCollapsed = false;
                e.Handled = true;
                return;
            }

            // Check if top-right collapse area was clicked
            if (pt.Position.X >= Width - 24 && pt.Position.Y <= 24)
            {
                IsCollapsed = true;
                e.Handled = true;
                return;
            }

            _isDraggingViewport = true;
            e.Pointer.Capture(this);
            CenterCanvasOnMinimapPoint(pt.Position);
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isDraggingViewport)
        {
            PointerPoint pt = e.GetCurrentPoint(this);
            CenterCanvasOnMinimapPoint(pt.Position);
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDraggingViewport)
        {
            _isDraggingViewport = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void CenterCanvasOnMinimapPoint(Point minimapPoint)
    {
        if (Layout is null)
        {
            return;
        }

        BoundingBox gBounds = Layout.Bounds;
        if (gBounds.Width <= 0 || gBounds.Height <= 0)
        {
            return;
        }

        double availX = ContentPadding;
        double availY = 22.0;
        double availW = Width - (ContentPadding * 2.0);
        double availH = Height - availY - ContentPadding;

        double scaleX = availW / gBounds.Width;
        double scaleY = availH / gBounds.Height;
        double mapScale = Math.Min(scaleX, scaleY);

        if (mapScale <= 0.0001)
        {
            return;
        }

        double offsetX = availX + (availW - (gBounds.Width * mapScale)) * 0.5 - (gBounds.X * mapScale);
        double offsetY = availY + (availH - (gBounds.Height * mapScale)) * 0.5 - (gBounds.Y * mapScale);

        // Convert minimap position back to layout/world coordinates
        double worldTargetX = (minimapPoint.X - offsetX) / mapScale;
        double worldTargetY = (minimapPoint.Y - offsetY) / mapScale;

        // Compute new canvas PanOffset so that world point is centered
        double z = Zoom > 0.0001 ? Zoom : 1.0;
        double newPanX = (ViewportSize.Width * 0.5) - (worldTargetX * z);
        double newPanY = (ViewportSize.Height * 0.5) - (worldTargetY * z);

        PanOffset = new Vector(newPanX, newPanY);
    }
}

