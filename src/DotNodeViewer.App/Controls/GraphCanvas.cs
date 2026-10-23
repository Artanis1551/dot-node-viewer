using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Input;
using DotNodeViewer.App.Rendering;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Layout;

namespace DotNodeViewer.App.Controls;

/// <summary>
/// A high-performance custom canvas control that renders interactive layered neural network graphs
/// directly via <see cref="DrawingContext"/> with viewport culling, level-of-detail (LOD) degradation,
/// spatial grid hit-testing, and smooth pan/zoom.
/// </summary>
public sealed class GraphCanvas : Control
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 4.0;
    private const double LodThresholdLow = 0.35;
    private const double LodThresholdMedium = 0.65;

    // Direct / Styled Avalonia properties
    /// <summary>
    /// Defines the <see cref="Layout"/> property.
    /// </summary>
    public static readonly StyledProperty<VisualSubgraphLayout?> LayoutProperty =
        AvaloniaProperty.Register<GraphCanvas, VisualSubgraphLayout?>(nameof(Layout));

    /// <summary>
    /// Defines the <see cref="SelectedNode"/> property.
    /// </summary>
    public static readonly StyledProperty<VisualNode?> SelectedNodeProperty =
        AvaloniaProperty.Register<GraphCanvas, VisualNode?>(
            nameof(SelectedNode),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// Defines the <see cref="HoveredNode"/> property.
    /// </summary>
    public static readonly StyledProperty<VisualNode?> HoveredNodeProperty =
        AvaloniaProperty.Register<GraphCanvas, VisualNode?>(nameof(HoveredNode));

    /// <summary>
    /// Defines the <see cref="Zoom"/> property.
    /// </summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<GraphCanvas, double>(nameof(Zoom), defaultValue: 1.0);

    /// <summary>
    /// Defines the <see cref="PanOffset"/> property.
    /// </summary>
    public static readonly StyledProperty<Vector> PanOffsetProperty =
        AvaloniaProperty.Register<GraphCanvas, Vector>(nameof(PanOffset), defaultValue: new Vector(0, 0));

    /// <summary>
    /// Defines the <see cref="ZoomPercentText"/> read-only property.
    /// </summary>
    public static readonly DirectProperty<GraphCanvas, string> ZoomPercentTextProperty =
        AvaloniaProperty.RegisterDirect<GraphCanvas, string>(
            nameof(ZoomPercentText),
            o => o.ZoomPercentText);

    private SpatialGridIndex _spatialIndex = SpatialGridIndex.Empty;
    private readonly Dictionary<string, StreamGeometry> _edgeGeometryCache = new();
    private readonly Dictionary<string, FormattedText> _textCache = new();
    private bool _isPanning;
    private Point _panStartScreenPoint;
    private Vector _panStartOffset;
    private bool _hasInitialFit;

    private readonly FontFamily _uiFont = new("Inter, -apple-system, BlinkMacSystemFont, Segoe UI, Ubuntu, Cantarell, sans-serif");
    private readonly FontFamily _codeFont = new("JetBrains Mono, Cascadia Code, Fira Code, DejaVu Sans Mono, Consolas, monospace");

    static GraphCanvas()
    {
        AffectsRender<GraphCanvas>(LayoutProperty, SelectedNodeProperty, HoveredNodeProperty, ZoomProperty, PanOffsetProperty);
        ClipToBoundsProperty.OverrideDefaultValue<GraphCanvas>(true);
        FocusableProperty.OverrideDefaultValue<GraphCanvas>(true);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GraphCanvas"/> class.
    /// </summary>
    public GraphCanvas()
    {
        ZoomInCommand = new RelayCommand(ZoomIn);
        ZoomOutCommand = new RelayCommand(ZoomOut);
        ZoomToFitCommand = new RelayCommand(ZoomToFit);
        ResetViewCommand = new RelayCommand(ResetView);

        ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    /// <summary>
    /// Gets or sets the positioned <see cref="VisualSubgraphLayout"/> to render.
    /// </summary>
    public VisualSubgraphLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>
    /// Gets or sets the currently selected <see cref="VisualNode"/>, or <see langword="null"/> if no node is selected.
    /// </summary>
    public VisualNode? SelectedNode
    {
        get => GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    /// <summary>
    /// Gets or sets the currently hovered <see cref="VisualNode"/>, or <see langword="null"/> if the pointer is not over any node.
    /// </summary>
    public VisualNode? HoveredNode
    {
        get => GetValue(HoveredNodeProperty);
        set => SetValue(HoveredNodeProperty, value);
    }

    /// <summary>
    /// Gets or sets the zoom scale factor applied to the world coordinates.
    /// </summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set
        {
            double clamped = Math.Clamp(value, MinZoom, MaxZoom);
            SetValue(ZoomProperty, clamped);
            RaisePropertyChanged(ZoomPercentTextProperty, string.Empty, ZoomPercentText);
        }
    }

    /// <summary>
    /// Gets or sets the pan translation offset applied to the canvas in screen pixels.
    /// </summary>
    public Vector PanOffset
    {
        get => GetValue(PanOffsetProperty);
        set => SetValue(PanOffsetProperty, value);
    }

    /// <summary>
    /// Gets a user-facing formatted percentage string representing the active zoom level (e.g. <c>"100%"</c>).
    /// </summary>
    public string ZoomPercentText => $"{(int)Math.Round(Zoom * 100.0, MidpointRounding.AwayFromZero)}%";

    /// <summary>
    /// Gets the command that zooms into the graph center.
    /// </summary>
    public IRelayCommand ZoomInCommand { get; }

    /// <summary>
    /// Gets the command that zooms out of the graph center.
    /// </summary>
    public IRelayCommand ZoomOutCommand { get; }

    /// <summary>
    /// Gets the command that centers and scales the entire graph to fit within the viewport.
    /// </summary>
    public IRelayCommand ZoomToFitCommand { get; }

    /// <summary>
    /// Gets the command that resets the view to 100% zoom and centers the graph origin.
    /// </summary>
    public IRelayCommand ResetViewCommand { get; }

    /// <summary>
    /// Zooms in by a step of 25% around the canvas center.
    /// </summary>
    public void ZoomIn()
    {
        ZoomAroundPoint(new Point(Bounds.Width * 0.5, Bounds.Height * 0.5), 1.25);
    }

    /// <summary>
    /// Zooms out by a step of 20% around the canvas center.
    /// </summary>
    public void ZoomOut()
    {
        ZoomAroundPoint(new Point(Bounds.Width * 0.5, Bounds.Height * 0.5), 0.8);
    }

    /// <summary>
    /// Resets the zoom scale to 1.0 (100%) and centers the layout bounding box.
    /// </summary>
    public void ResetView()
    {
        Zoom = 1.0;
        if (Layout is not null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            BoundingBox b = Layout.Bounds;
            double panX = (Bounds.Width - b.Width) * 0.5 - b.X;
            double panY = (Bounds.Height - b.Height) * 0.5 - b.Y;
            PanOffset = new Vector(panX, panY);
        }
        else
        {
            PanOffset = new Vector(24, 24);
        }
        InvalidateVisual();
    }

    /// <summary>
    /// Fits and centers the entire graph bounding box inside the visible canvas area with a comfortable padding.
    /// </summary>
    public void ZoomToFit()
    {
        if (Layout is null || Bounds.Width <= 10 || Bounds.Height <= 10)
        {
            return;
        }

        BoundingBox b = Layout.Bounds;
        if (b.Width <= 0 || b.Height <= 0)
        {
            return;
        }

        const double padding = 48.0;
        double availWidth = Math.Max(20.0, Bounds.Width - (padding * 2.0));
        double availHeight = Math.Max(20.0, Bounds.Height - (padding * 2.0));

        double scaleX = availWidth / b.Width;
        double scaleY = availHeight / b.Height;
        double fitScale = Math.Min(scaleX, scaleY);
        fitScale = Math.Clamp(fitScale, MinZoom, 1.25);

        double panX = (Bounds.Width - (b.Width * fitScale)) * 0.5 - (b.X * fitScale);
        double panY = (Bounds.Height - (b.Height * fitScale)) * 0.5 - (b.Y * fitScale);

        Zoom = fitScale;
        PanOffset = new Vector(panX, panY);
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LayoutProperty)
        {
            VisualSubgraphLayout? layout = change.GetNewValue<VisualSubgraphLayout?>();
            RebuildLayoutCaches(layout);
            _hasInitialFit = false;

            if (Bounds.Width > 0 && Bounds.Height > 0)
            {
                ZoomToFit();
                _hasInitialFit = true;
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_hasInitialFit && Layout is not null && e.NewSize.Width > 10 && e.NewSize.Height > 10)
        {
            ZoomToFit();
            _hasInitialFit = true;
        }
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        _textCache.Clear();
        InvalidateVisual();
    }

    private void RebuildLayoutCaches(VisualSubgraphLayout? layout)
    {
        _edgeGeometryCache.Clear();
        _textCache.Clear();

        if (layout is null)
        {
            _spatialIndex = SpatialGridIndex.Empty;
            return;
        }

        _spatialIndex = new SpatialGridIndex(layout.Nodes);

        // Precompute edge Bézier StreamGeometry
        for (int i = 0; i < layout.Edges.Count; i++)
        {
            VisualEdge edge = layout.Edges[i];
            StreamGeometry geom = new();
            using (StreamGeometryContext ctx = geom.Open())
            {
                IReadOnlyList<CubicBezierSegment> segments = edge.Layout.BezierSegments;
                if (segments.Count > 0)
                {
                    ctx.BeginFigure(new Point(segments[0].Start.X, segments[0].Start.Y), isFilled: false);
                    for (int s = 0; s < segments.Count; s++)
                    {
                        CubicBezierSegment seg = segments[s];
                        ctx.CubicBezierTo(
                            new Point(seg.Control1.X, seg.Control1.Y),
                            new Point(seg.Control2.X, seg.Control2.Y),
                            new Point(seg.End.X, seg.End.Y));
                    }
                    ctx.EndFigure(isClosed: false);
                }
                else if (edge.Layout.Points.Count > 1)
                {
                    ctx.BeginFigure(new Point(edge.Layout.Points[0].X, edge.Layout.Points[0].Y), isFilled: false);
                    for (int p = 1; p < edge.Layout.Points.Count; p++)
                    {
                        ctx.LineTo(new Point(edge.Layout.Points[p].X, edge.Layout.Points[p].Y));
                    }
                    ctx.EndFigure(isClosed: false);
                }
            }
            _edgeGeometryCache[edge.Id] = geom;
        }
    }

    /// <summary>
    /// Translates a screen point on the control to world coordinates in the graph layout.
    /// </summary>
    /// <param name="screenPoint">The point in canvas pixel coordinates.</param>
    /// <returns>The point in layout space.</returns>
    public Point ScreenToWorld(Point screenPoint)
    {
        double z = Zoom > 0.0001 ? Zoom : 1.0;
        return new Point((screenPoint.X - PanOffset.X) / z, (screenPoint.Y - PanOffset.Y) / z);
    }

    /// <summary>
    /// Translates a point in graph layout space to screen pixel coordinates on this control.
    /// </summary>
    /// <param name="worldPoint">The point in layout space.</param>
    /// <returns>The point in canvas screen coordinates.</returns>
    public Point WorldToScreen(Point worldPoint)
    {
        return new Point((worldPoint.X * Zoom) + PanOffset.X, (worldPoint.Y * Zoom) + PanOffset.Y);
    }

    private void ZoomAroundPoint(Point screenPoint, double factor)
    {
        double oldZoom = Zoom;
        double newZoom = Math.Clamp(oldZoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - oldZoom) < 1e-5)
        {
            return;
        }

        double wx = (screenPoint.X - PanOffset.X) / oldZoom;
        double wy = (screenPoint.Y - PanOffset.Y) / oldZoom;

        Zoom = newZoom;
        PanOffset = new Vector(screenPoint.X - (wx * newZoom), screenPoint.Y - (wy * newZoom));
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        PointerPoint currentPoint = e.GetCurrentPoint(this);
        Point pos = currentPoint.Position;
        Point world = ScreenToWorld(pos);

        if (currentPoint.Properties.IsLeftButtonPressed)
        {
            VisualNode? hitNode = _spatialIndex.FindNodeAt(world.X, world.Y);
            if (hitNode is not null)
            {
                SelectedNode = hitNode;
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Clicked on empty canvas: deselect and start pan
            SelectedNode = null;
            _isPanning = true;
            _panStartScreenPoint = pos;
            _panStartOffset = PanOffset;
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (currentPoint.Properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _panStartScreenPoint = pos;
            _panStartOffset = PanOffset;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        Point pos = e.GetPosition(this);

        if (_isPanning)
        {
            Vector delta = pos - _panStartScreenPoint;
            PanOffset = _panStartOffset + delta;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        Point world = ScreenToWorld(pos);
        VisualNode? hitNode = _spatialIndex.FindNodeAt(world.X, world.Y);
        if (!ReferenceEquals(HoveredNode, hitNode))
        {
            HoveredNode = hitNode;
            InvalidateVisual();
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        Point pos = e.GetPosition(this);

        // Zoom with wheel (or Ctrl+wheel / trackpad pinch)
        double deltaY = e.Delta.Y;
        if (Math.Abs(deltaY) > 0.01)
        {
            double factor = deltaY > 0 ? 1.15 : (1.0 / 1.15);
            ZoomAroundPoint(pos, factor);
            e.Handled = true;
        }
        else if (Math.Abs(e.Delta.X) > 0.01)
        {
            // Horizontal two-finger scroll
            PanOffset = new Vector(PanOffset.X + (e.Delta.X * 24.0), PanOffset.Y);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Layout is null)
        {
            return;
        }

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Compute visible bounding box in layout/world coordinates for culling
        double left = -PanOffset.X / Zoom;
        double top = -PanOffset.Y / Zoom;
        double right = (width - PanOffset.X) / Zoom;
        double bottom = (height - PanOffset.Y) / Zoom;
        BoundingBox visibleWorldBounds = new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

        // Fetch theme-aware brushes
        ThemeBrushes brushes = ResolveThemeBrushes();

        // Push canvas world transform matrix (Translation * Scale)
        Matrix worldMatrix = Matrix.CreateTranslation(PanOffset.X, PanOffset.Y) * Matrix.CreateScale(Zoom, Zoom);
        using (context.PushTransform(worldMatrix))
        {
            // 1. Render Edges (behind nodes)
            RenderEdges(context, visibleWorldBounds, brushes);

            // 2. Render Nodes
            RenderNodes(context, visibleWorldBounds, brushes);
        }
    }

    private void RenderEdges(DrawingContext context, BoundingBox visibleWorldBounds, in ThemeBrushes brushes)
    {
        if (Layout is null)
        {
            return;
        }

        string? selectedNodeId = SelectedNode?.Id;
        bool isLowLod = Zoom < LodThresholdLow;

        for (int i = 0; i < Layout.Edges.Count; i++)
        {
            VisualEdge edge = Layout.Edges[i];

            // Edge culling check: determine if edge bounds intersect visible viewport
            if (!IsEdgeVisible(edge, visibleWorldBounds))
            {
                continue;
            }

            if (!_edgeGeometryCache.TryGetValue(edge.Id, out StreamGeometry? geom))
            {
                continue;
            }

            bool isConnectedToSelected = selectedNodeId is not null
                && (edge.Layout.SourceNodeId == selectedNodeId || edge.Layout.TargetNodeId == selectedNodeId);

            IPen edgePen;
            IBrush arrowBrush;

            if (isConnectedToSelected)
            {
                edgePen = brushes.EdgeSelectedPen;
                arrowBrush = brushes.AccentPrimaryBrush;
            }
            else if (edge.IsQuantized)
            {
                edgePen = brushes.EdgeQuantizedPen;
                arrowBrush = brushes.SuccessBrush;
            }
            else
            {
                edgePen = brushes.EdgeMutedPen;
                arrowBrush = brushes.BorderMutedBrush;
            }

            // Draw cubic Bézier spline
            context.DrawGeometry(null, edgePen, geom);

            // Draw directional arrowhead at target anchor
            DrawArrowhead(context, edge, arrowBrush);

            // Draw edge midpoint tensor shape callout pill (at medium and high zoom)
            if (!isLowLod && edge.Layout.LabelAnchor.HasValue)
            {
                DrawEdgeShapeLabel(context, edge, edge.Layout.LabelAnchor.Value, isConnectedToSelected, brushes);
            }
        }
    }

    private static bool IsEdgeVisible(VisualEdge edge, BoundingBox viewport)
    {
        IReadOnlyList<LayoutPoint> points = edge.Layout.Points;
        if (points.Count == 0)
        {
            return true;
        }

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;

        for (int i = 0; i < points.Count; i++)
        {
            LayoutPoint p = points[i];
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        // Expand bounds slightly for labels and arrowhead
        BoundingBox edgeBox = new(minX - 20, minY - 20, (maxX - minX) + 40, (maxY - minY) + 40);
        return edgeBox.Intersects(viewport);
    }

    private static void DrawArrowhead(DrawingContext context, VisualEdge edge, IBrush fillBrush)
    {
        IReadOnlyList<CubicBezierSegment> segments = edge.Layout.BezierSegments;
        LayoutPoint end;
        LayoutPoint from;

        if (segments.Count > 0)
        {
            CubicBezierSegment last = segments[segments.Count - 1];
            end = last.End;
            from = last.Control2;
        }
        else if (edge.Layout.Points.Count >= 2)
        {
            end = edge.Layout.Points[edge.Layout.Points.Count - 1];
            from = edge.Layout.Points[edge.Layout.Points.Count - 2];
        }
        else
        {
            return;
        }

        double dx = end.X - from.X;
        double dy = end.Y - from.Y;
        double len = Math.Sqrt((dx * dx) + (dy * dy));
        if (len < 1e-4)
        {
            return;
        }

        double ux = dx / len;
        double uy = dy / len;
        double nx = -uy;
        double ny = ux;

        const double arrowLength = 7.0;
        const double arrowHalfWidth = 3.8;

        Point pTip = new(end.X, end.Y);
        Point pBaseLeft = new(end.X - (ux * arrowLength) + (nx * arrowHalfWidth), end.Y - (uy * arrowLength) + (ny * arrowHalfWidth));
        Point pBaseRight = new(end.X - (ux * arrowLength) - (nx * arrowHalfWidth), end.Y - (uy * arrowLength) - (ny * arrowHalfWidth));

        StreamGeometry arrowGeom = new();
        using (StreamGeometryContext arrowCtx = arrowGeom.Open())
        {
            arrowCtx.BeginFigure(pTip, isFilled: true);
            arrowCtx.LineTo(pBaseLeft);
            arrowCtx.LineTo(pBaseRight);
            arrowCtx.EndFigure(isClosed: true);
        }

        context.DrawGeometry(fillBrush, null, arrowGeom);
    }

    private void DrawEdgeShapeLabel(
        DrawingContext context,
        VisualEdge edge,
        LayoutPoint anchor,
        bool isConnectedToSelected,
        in ThemeBrushes brushes)
    {
        string labelText = edge.ShapeLabel;
        if (string.IsNullOrWhiteSpace(labelText))
        {
            return;
        }

        string cacheKey = $"edge:{edge.Id}:{labelText}:{brushes.IsDark}";
        if (!_textCache.TryGetValue(cacheKey, out FormattedText? ft))
        {
            Typeface typeface = new(_codeFont, FontStyle.Normal, FontWeight.Medium);
            ft = new FormattedText(
                labelText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                10.0,
                brushes.TextSecondaryBrush);
            _textCache[cacheKey] = ft;
        }

        double pillWidth = ft.Width + 14.0 + (edge.IsQuantized ? 10.0 : 0.0);
        double pillHeight = 16.0;
        double left = anchor.X - (pillWidth * 0.5);
        double top = anchor.Y - (pillHeight * 0.5);

        Rect pillRect = new(left, top, pillWidth, pillHeight);
        IBrush bg = brushes.SurfaceElevatedBrush;
        IPen borderPen = isConnectedToSelected
            ? brushes.BorderAccentPen
            : (edge.IsQuantized ? brushes.EdgeQuantizedPen : brushes.BorderMutedPen);

        context.DrawRectangle(bg, borderPen, pillRect, 4.0, 4.0);

        double textLeft = left + 7.0;
        if (edge.IsQuantized)
        {
            // Draw small green quantized dot
            context.DrawEllipse(brushes.SuccessBrush, null, new Point(left + 8.0, top + (pillHeight * 0.5)), 2.5, 2.5);
            textLeft += 9.0;
        }

        context.DrawText(ft, new Point(textLeft, top + 1.5));
    }

    private void RenderNodes(DrawingContext context, BoundingBox visibleWorldBounds, in ThemeBrushes brushes)
    {
        if (Layout is null)
        {
            return;
        }

        bool isLowLod = Zoom < LodThresholdLow;
        bool isMediumLod = Zoom < LodThresholdMedium;

        for (int i = 0; i < Layout.Nodes.Count; i++)
        {
            VisualNode node = Layout.Nodes[i];
            BoundingBox b = node.Bounds;

            // Viewport culling check
            if (!b.Intersects(visibleWorldBounds))
            {
                continue;
            }

            bool isSelected = ReferenceEquals(SelectedNode, node);
            bool isHovered = ReferenceEquals(HoveredNode, node);

            if (node.Kind == VisualNodeKind.Operator)
            {
                RenderOperatorNode(context, node, isSelected, isHovered, isLowLod, isMediumLod, brushes);
            }
            else
            {
                RenderTerminalPill(context, node, isSelected, isHovered, isLowLod, brushes);
            }
        }
    }

    private void RenderOperatorNode(
        DrawingContext context,
        VisualNode node,
        bool isSelected,
        bool isHovered,
        bool isLowLod,
        bool isMediumLod,
        in ThemeBrushes brushes)
    {
        BoundingBox b = node.Bounds;
        Rect cardRect = new(b.X, b.Y, b.Width, b.Height);

        // 1. Outer Card Background & Border
        IPen cardPen = isSelected
            ? brushes.BorderSelectedPen
            : (isHovered ? brushes.BorderHoverPen : brushes.BorderMutedPen);

        context.DrawRectangle(brushes.SurfaceBaseBrush, cardPen, cardRect, 8.0, 8.0);

        // Header height
        const double headerHeight = 30.0;
        Rect headerRect = new(b.X, b.Y, b.Width, headerHeight);

        // 2. Header Band Fill (Category Subtle Tint)
        IBrush categorySubtleBrush = brushes.GetCategorySubtleBrush(node.Category);
        IBrush categoryAccentBrush = brushes.GetCategoryAccentBrush(node.Category);

        // Clip header to top rounded corners
        using (context.PushClip(headerRect))
        {
            context.DrawRectangle(categorySubtleBrush, null, cardRect, 8.0, 8.0);
        }

        // Left accent stripe indicator
        Rect stripeRect = new(b.X, b.Y + 4.0, 4.0, headerHeight - 8.0);
        context.DrawRectangle(categoryAccentBrush, null, stripeRect, 2.0, 2.0);

        // Divider below header
        context.DrawLine(brushes.BorderMutedPen, new Point(b.X, b.Y + headerHeight), new Point(b.Right, b.Y + headerHeight));

        if (isLowLod)
        {
            // At low LOD, omit text inside the node for 60fps performance
            return;
        }

        // 3. Header Title (Operator OpType, e.g. "Conv", "Relu")
        string titleKey = $"title:{node.Title}:{brushes.IsDark}";
        if (!_textCache.TryGetValue(titleKey, out FormattedText? titleFt))
        {
            Typeface tf = new(_uiFont, FontStyle.Normal, FontWeight.SemiBold);
            titleFt = new FormattedText(
                node.Title,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                tf,
                12.5,
                brushes.TextPrimaryBrush);
            _textCache[titleKey] = titleFt;
        }
        context.DrawText(titleFt, new Point(b.X + 12.0, b.Y + 7.0));

        // Subtitle (Node Name) if space permits
        if (!string.IsNullOrEmpty(node.Subtitle) && !isMediumLod && b.Width > 170.0)
        {
            string subKey = $"sub:{node.Subtitle}:{brushes.IsDark}";
            if (!_textCache.TryGetValue(subKey, out FormattedText? subFt))
            {
                Typeface tf = new(_codeFont, FontStyle.Normal, FontWeight.Normal);
                subFt = new FormattedText(
                    node.Subtitle,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    tf,
                    9.5,
                    brushes.TextSecondaryBrush)
                {
                    MaxTextWidth = Math.Max(40.0, b.Width - titleFt.Width - 28.0),
                    Trimming = TextTrimming.CharacterEllipsis
                };
                _textCache[subKey] = subFt;
            }
            context.DrawText(subFt, new Point(b.X + titleFt.Width + 18.0, b.Y + 9.0));
        }

        if (isMediumLod)
        {
            // At medium LOD, render title and skip parameter details
            return;
        }

        // 4. Body: Collapsed Parameter Rows
        double currentY = b.Y + headerHeight + 5.0;
        for (int p = 0; p < node.CollapsedParameters.Count; p++)
        {
            if (currentY + 18.0 > b.Bottom)
            {
                break;
            }

            CollapsedParameterSlot param = node.CollapsedParameters[p];
            DrawParameterRow(context, param, b.X + 8.0, currentY, b.Width - 16.0, brushes);
            currentY += 19.0;
        }

        // 5. Fused Activation Badge (e.g. [+ RELU])
        if (!string.IsNullOrEmpty(node.FusedActivationBadge) && currentY + 18.0 <= b.Bottom)
        {
            DrawFusedBadge(context, node.FusedActivationBadge, b.X + 8.0, currentY, brushes);
        }
    }

    private void DrawParameterRow(
        DrawingContext context,
        CollapsedParameterSlot param,
        double x,
        double y,
        double availableWidth,
        in ThemeBrushes brushes)
    {
        // Parameter port label (e.g. "W", "B", "weights")
        string portKey = $"port:{param.PortName}:{brushes.IsDark}";
        if (!_textCache.TryGetValue(portKey, out FormattedText? portFt))
        {
            Typeface tf = new(_codeFont, FontStyle.Normal, FontWeight.Medium);
            portFt = new FormattedText(
                param.PortName,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                tf,
                10.5,
                brushes.TextSecondaryBrush);
            _textCache[portKey] = portFt;
        }
        context.DrawText(portFt, new Point(x + 2.0, y + 1.0));

        // Parameter summary pill (e.g. "float32 [64, 3, 3, 3]")
        string summaryText = param.DisplaySummary;
        string sumKey = $"psum:{summaryText}:{brushes.IsDark}";
        if (!_textCache.TryGetValue(sumKey, out FormattedText? sumFt))
        {
            Typeface tf = new(_codeFont, FontStyle.Normal, FontWeight.Normal);
            sumFt = new FormattedText(
                summaryText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                tf,
                10.0,
                brushes.TextPrimaryBrush)
            {
                MaxTextWidth = Math.Max(40.0, availableWidth - portFt.Width - 32.0),
                Trimming = TextTrimming.CharacterEllipsis
            };
            _textCache[sumKey] = sumFt;
        }

        double pillWidth = sumFt.Width + 10.0;
        double pillHeight = 15.0;
        double pillX = x + availableWidth - pillWidth - (param.IsQuantized ? 24.0 : 0.0);
        Rect pillRect = new(pillX, y + 1.0, pillWidth, pillHeight);

        context.DrawRectangle(brushes.SurfaceElevatedBrush, brushes.BorderMutedPen, pillRect, 3.0, 3.0);
        context.DrawText(sumFt, new Point(pillX + 5.0, y + 2.0));

        // Quantization badge if quantized parameter
        if (param.IsQuantized)
        {
            Rect qRect = new(x + availableWidth - 20.0, y + 1.0, 18.0, pillHeight);
            context.DrawRectangle(brushes.SuccessSubtleBrush, brushes.EdgeQuantizedPen, qRect, 3.0, 3.0);

            string qKey = $"qbadge:{brushes.IsDark}";
            if (!_textCache.TryGetValue(qKey, out FormattedText? qFt))
            {
                Typeface tf = new(_codeFont, FontStyle.Normal, FontWeight.Bold);
                qFt = new FormattedText(
                    "Q",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    tf,
                    9.0,
                    brushes.SuccessBrush);
                _textCache[qKey] = qFt;
            }
            context.DrawText(qFt, new Point(qRect.X + 4.5, y + 2.0));
        }
    }

    private void DrawFusedBadge(
        DrawingContext context,
        string badgeText,
        double x,
        double y,
        in ThemeBrushes brushes)
    {
        string label = $"+ {badgeText}";
        string cacheKey = $"fused:{label}:{brushes.IsDark}";
        if (!_textCache.TryGetValue(cacheKey, out FormattedText? ft))
        {
            Typeface tf = new(_uiFont, FontStyle.Normal, FontWeight.SemiBold);
            ft = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                tf,
                9.5,
                brushes.WarningBrush);
            _textCache[cacheKey] = ft;
        }

        double badgeWidth = ft.Width + 12.0;
        double badgeHeight = 16.0;
        Rect badgeRect = new(x, y + 1.0, badgeWidth, badgeHeight);

        context.DrawRectangle(brushes.WarningSubtleBrush, brushes.WarningBorderPen, badgeRect, 4.0, 4.0);
        context.DrawText(ft, new Point(x + 6.0, y + 2.0));
    }

    private void RenderTerminalPill(
        DrawingContext context,
        VisualNode node,
        bool isSelected,
        bool isHovered,
        bool isLowLod,
        in ThemeBrushes brushes)
    {
        BoundingBox b = node.Bounds;
        Rect pillRect = new(b.X, b.Y, b.Width, b.Height);

        IPen pillPen = isSelected
            ? brushes.BorderSelectedPen
            : (isHovered ? brushes.BorderHoverPen : brushes.BorderMutedPen);

        bool isInput = node.Kind == VisualNodeKind.GraphInput;
        IBrush bg = brushes.SurfaceElevatedBrush;
        context.DrawRectangle(bg, pillPen, pillRect, b.Height * 0.5, b.Height * 0.5);

        // Terminal circle indicator (Green for input, Cyan/Blue for output)
        IBrush dotBrush = isInput ? brushes.SuccessBrush : brushes.AccentPrimaryBrush;
        double dotX = b.X + 14.0;
        double dotY = b.Y + (b.Height * 0.5);
        context.DrawEllipse(dotBrush, null, new Point(dotX, dotY), 4.0, 4.0);

        if (isLowLod)
        {
            return;
        }

        // Pill Title (Port Name)
        string titleKey = $"term:{node.Title}:{brushes.IsDark}";
        if (!_textCache.TryGetValue(titleKey, out FormattedText? titleFt))
        {
            Typeface tf = new(_codeFont, FontStyle.Normal, FontWeight.SemiBold);
            titleFt = new FormattedText(
                node.Title,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                tf,
                11.0,
                brushes.TextPrimaryBrush)
            {
                MaxTextWidth = Math.Max(30.0, b.Width - 36.0),
                Trimming = TextTrimming.CharacterEllipsis
            };
            _textCache[titleKey] = titleFt;
        }

        double textY = string.IsNullOrEmpty(node.Subtitle)
            ? b.Y + ((b.Height - titleFt.Height) * 0.5)
            : b.Y + 5.0;
        context.DrawText(titleFt, new Point(b.X + 24.0, textY));

        // Subtitle (Shape string if space permits)
        if (!string.IsNullOrEmpty(node.Subtitle) && b.Height >= 32.0)
        {
            string subKey = $"term_sub:{node.Subtitle}:{brushes.IsDark}";
            if (!_textCache.TryGetValue(subKey, out FormattedText? subFt))
            {
                Typeface tf = new(_codeFont, FontStyle.Normal, FontWeight.Normal);
                subFt = new FormattedText(
                    node.Subtitle,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    tf,
                    9.5,
                    brushes.TextSecondaryBrush)
                {
                    MaxTextWidth = Math.Max(30.0, b.Width - 36.0),
                    Trimming = TextTrimming.CharacterEllipsis
                };
                _textCache[subKey] = subFt;
            }
            context.DrawText(subFt, new Point(b.X + 24.0, b.Y + 18.0));
        }
    }

    private ThemeBrushes ResolveThemeBrushes()
    {
        bool isDark = ActualThemeVariant != ThemeVariant.Light;
        return new ThemeBrushes(this, isDark);
    }

    private readonly struct ThemeBrushes
    {
        public readonly bool IsDark;
        public readonly IBrush SurfaceBaseBrush;
        public readonly IBrush SurfaceElevatedBrush;
        public readonly IBrush TextPrimaryBrush;
        public readonly IBrush TextSecondaryBrush;
        public readonly IBrush AccentPrimaryBrush;
        public readonly IBrush SuccessBrush;
        public readonly IBrush SuccessSubtleBrush;
        public readonly IBrush WarningBrush;
        public readonly IBrush WarningSubtleBrush;
        public readonly IBrush BorderMutedBrush;

        public readonly IPen BorderMutedPen;
        public readonly IPen BorderHoverPen;
        public readonly IPen BorderSelectedPen;
        public readonly IPen BorderAccentPen;
        public readonly IPen EdgeMutedPen;
        public readonly IPen EdgeQuantizedPen;
        public readonly IPen EdgeSelectedPen;
        public readonly IPen WarningBorderPen;

        private readonly Control _owner;

        public ThemeBrushes(Control owner, bool isDark)
        {
            _owner = owner;
            IsDark = isDark;

            SurfaceBaseBrush = ResolveBrush("ShellSurfaceBaseBrush", isDark ? "#1A1F29" : "#FFFFFF");
            SurfaceElevatedBrush = ResolveBrush("ShellSurfaceElevatedBrush", isDark ? "#232A37" : "#F1F5F9");
            TextPrimaryBrush = ResolveBrush("ShellTextPrimaryBrush", isDark ? "#F1F5F9" : "#0F172A");
            TextSecondaryBrush = ResolveBrush("ShellTextSecondaryBrush", isDark ? "#94A3B8" : "#475569");
            AccentPrimaryBrush = ResolveBrush("ShellAccentPrimaryBrush", isDark ? "#38BDF8" : "#0284C7");
            SuccessBrush = ResolveBrush("ShellSuccessBrush", isDark ? "#10B981" : "#059669");
            SuccessSubtleBrush = ResolveBrush("ShellSuccessSubtleBrush", isDark ? "#133827" : "#DCFCE7");
            WarningBrush = ResolveBrush("ShellWarningBrush", isDark ? "#F59E0B" : "#D97706");
            WarningSubtleBrush = ResolveBrush("OpCategoryActivationSubtleBrush", isDark ? "#3D2908" : "#FEF3C7");
            BorderMutedBrush = ResolveBrush("ShellBorderMutedBrush", isDark ? "#333C4D" : "#CBD5E1");

            BorderMutedPen = new Pen(BorderMutedBrush, 1.0);
            BorderHoverPen = new Pen(AccentPrimaryBrush, 1.5);
            BorderSelectedPen = new Pen(AccentPrimaryBrush, 2.2);
            BorderAccentPen = new Pen(AccentPrimaryBrush, 1.2);
            EdgeMutedPen = new Pen(BorderMutedBrush, 1.4);
            EdgeQuantizedPen = new Pen(SuccessBrush, 1.8);
            EdgeSelectedPen = new Pen(AccentPrimaryBrush, 2.4);
            WarningBorderPen = new Pen(WarningBrush, 1.0);
        }

        public IBrush GetCategoryAccentBrush(OperatorCategory category)
        {
            string key = $"OpCategory{category}Brush";
            string fallback = category switch
            {
                OperatorCategory.Layer => IsDark ? "#6366F1" : "#4F46E5",
                OperatorCategory.Activation => IsDark ? "#F59E0B" : "#D97706",
                OperatorCategory.Normalization => IsDark ? "#A855F7" : "#9333EA",
                OperatorCategory.Pooling => IsDark ? "#14B8A6" : "#0D9488",
                OperatorCategory.Quantization => IsDark ? "#F43F5E" : "#E11D48",
                OperatorCategory.Shape => IsDark ? "#0EA5E9" : "#0284C7",
                OperatorCategory.Tensor => IsDark ? "#10B981" : "#059669",
                OperatorCategory.Constant => IsDark ? "#94A3B8" : "#475569",
                OperatorCategory.ControlFlow => IsDark ? "#EC4899" : "#DB2777",
                OperatorCategory.Data => IsDark ? "#3B82F6" : "#2563EB",
                OperatorCategory.Custom => IsDark ? "#EAB308" : "#CA8A04",
                _ => IsDark ? "#71717A" : "#52525B"
            };
            return ResolveBrush(key, fallback);
        }

        public IBrush GetCategorySubtleBrush(OperatorCategory category)
        {
            string key = $"OpCategory{category}SubtleBrush";
            string fallback = category switch
            {
                OperatorCategory.Layer => IsDark ? "#22234B" : "#EEF2FF",
                OperatorCategory.Activation => IsDark ? "#3D2908" : "#FEF3C7",
                OperatorCategory.Normalization => IsDark ? "#341A50" : "#F3E8FF",
                OperatorCategory.Pooling => IsDark ? "#0B3733" : "#CCFBF1",
                OperatorCategory.Quantization => IsDark ? "#42141F" : "#FFE4E6",
                OperatorCategory.Shape => IsDark ? "#0C2F46" : "#E0F2FE",
                OperatorCategory.Tensor => IsDark ? "#0D3528" : "#D1FAE5",
                OperatorCategory.Constant => IsDark ? "#252D3B" : "#F1F5F9",
                OperatorCategory.ControlFlow => IsDark ? "#3F172B" : "#FCE7F3",
                OperatorCategory.Data => IsDark ? "#13274A" : "#DBEAFE",
                OperatorCategory.Custom => IsDark ? "#392C07" : "#FEF9C3",
                _ => IsDark ? "#27272A" : "#F4F4F5"
            };
            return ResolveBrush(key, fallback);
        }

        private IBrush ResolveBrush(string key, string fallbackHex)
        {
            if (_owner.TryFindResource(key, out object? resource) && resource is IBrush brush)
            {
                return brush;
            }
            return new SolidColorBrush(Color.Parse(fallbackHex));
        }
    }
}

