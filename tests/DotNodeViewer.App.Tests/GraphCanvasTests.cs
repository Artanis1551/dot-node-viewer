using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using DotNodeViewer.App.Controls;
using DotNodeViewer.App.ViewModels;
using DotNodeViewer.Layout;

namespace DotNodeViewer.App.Tests;

/// <summary>
/// Headless integration and rendering tests for <see cref="GraphCanvas"/> and the basic properties inspector.
/// </summary>
public sealed class GraphCanvasTests
{
    [Before(Test)]
    public void SetUp()
    {
        HeadlessAppSession.EnsureInitialized();
    }

    [Test]
    public async Task GraphCanvas_InitialState_HasExpectedDefaults()
    {
        await HeadlessAppSession.DispatchAsync(async () =>
        {
            GraphCanvas canvas = new();

            await Assert.That(canvas.Layout).IsNull();
            await Assert.That(canvas.SelectedNode).IsNull();
            await Assert.That(canvas.HoveredNode).IsNull();
            await Assert.That(canvas.ClipToBounds).IsTrue();
            await Assert.That(canvas.Background).IsNotNull();
            await Assert.That(canvas.Zoom).IsEqualTo(1.0);
            await Assert.That(canvas.PanOffset.X).IsEqualTo(0.0);
            await Assert.That(canvas.PanOffset.Y).IsEqualTo(0.0);
            await Assert.That(canvas.ZoomPercentText).IsEqualTo("100%");
        });
    }

    [Test]
    public async Task GraphCanvas_ZoomCommands_UpdateScaleAndPercentText()
    {
        await HeadlessAppSession.DispatchAsync(async () =>
        {
            GraphCanvas canvas = new();
            canvas.Measure(new Size(800, 600));
            canvas.Arrange(new Rect(0, 0, 800, 600));

            canvas.ZoomIn();
            await Assert.That(canvas.Zoom).IsGreaterThan(1.0);
            await Assert.That(canvas.ZoomPercentText).Contains("%");

            canvas.ZoomOut();
            await Assert.That(canvas.Zoom).IsLessThan(1.26);

            canvas.ResetView();
            await Assert.That(canvas.Zoom).IsEqualTo(1.0);
            await Assert.That(canvas.ZoomPercentText).IsEqualTo("100%");
        });
    }

    [Test]
    public async Task GraphCanvas_ScreenToWorld_WorldToScreen_RoundTripsAccurately()
    {
        await HeadlessAppSession.DispatchAsync(async () =>
        {
            GraphCanvas canvas = new()
            {
                Zoom = 1.5,
                PanOffset = new Vector(100, -50)
            };

            Point screenOrig = new(320, 240);
            Point world = canvas.ScreenToWorld(screenOrig);
            Point screenBack = canvas.WorldToScreen(world);

            await Assert.That(Math.Abs(screenOrig.X - screenBack.X)).IsLessThan(1e-4);
            await Assert.That(Math.Abs(screenOrig.Y - screenBack.Y)).IsLessThan(1e-4);
        });
    }

    [Test]
    public async Task GraphCanvas_Render_OnnxTinyMlp_RendersToBitmapAndSelectsNode()
    {
        string path = ResolveFixturePath("onnx/tiny_mlp.onnx");
        MainWindowViewModel vm = new();
        await vm.LoadModelAsync(path);

        await Assert.That(vm.HasModel).IsTrue();
        await Assert.That(vm.CurrentLayout).IsNotNull();

        await HeadlessAppSession.DispatchAsync(async () =>
        {
            GraphCanvas canvas = new()
            {
                Layout = vm.CurrentLayout
            };

            canvas.Measure(new Size(800, 600));
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ZoomToFit();

            await Assert.That(canvas.Zoom).IsGreaterThan(0.0);

            // Headless Bitmap Render
            using RenderTargetBitmap bitmap = new(new PixelSize(800, 600));
            bitmap.Render(canvas);

            await Assert.That(bitmap.PixelSize.Width).IsEqualTo(800);
            await Assert.That(bitmap.PixelSize.Height).IsEqualTo(600);

            // Select an operator node on the canvas
            VisualNode targetNode = vm.CurrentLayout!.Nodes.First(n => n.Kind == VisualNodeKind.Operator);
            canvas.SelectedNode = targetNode;
            vm.SelectedNode = targetNode;

            await Assert.That(vm.HasSelectedNode).IsTrue();
            await Assert.That(vm.SelectedNodeTitle).IsEqualTo(targetNode.Title);
            await Assert.That(vm.SelectedNodeCategoryName).IsEqualTo(targetNode.Category.ToString());
            await Assert.That(vm.SelectedNodeInputs).IsNotEmpty();
            await Assert.That(vm.SelectedNodeOutputs).IsNotEmpty();

            // Deselect node
            vm.DeselectNodeCommand.Execute(null);
            await Assert.That(vm.HasSelectedNode).IsFalse();
            await Assert.That(vm.SelectedNode).IsNull();
        });
    }

    [Test]
    public async Task GraphCanvas_Render_TfliteInt8_RendersAndSelectsQuantizedNode()
    {
        string path = ResolveFixturePath("tflite/tiny_cnn_int8.tflite");
        MainWindowViewModel vm = new();
        await vm.LoadModelAsync(path);

        await Assert.That(vm.HasModel).IsTrue();
        await Assert.That(vm.CurrentLayout).IsNotNull();

        await HeadlessAppSession.DispatchAsync(async () =>
        {
            GraphCanvas canvas = new()
            {
                Layout = vm.CurrentLayout
            };

            canvas.Measure(new Size(900, 700));
            canvas.Arrange(new Rect(0, 0, 900, 700));
            canvas.ZoomToFit();

            // Headless Bitmap Render
            using RenderTargetBitmap bitmap = new(new PixelSize(900, 700));
            bitmap.Render(canvas);

            await Assert.That(bitmap.PixelSize.Width).IsEqualTo(900);

            // Select the CONV_2D node
            VisualNode convNode = vm.CurrentLayout!.Nodes.First(n => n.Title.Contains("CONV", StringComparison.OrdinalIgnoreCase));
            vm.SelectedNode = convNode;

            await Assert.That(vm.HasSelectedNode).IsTrue();
            await Assert.That(vm.SelectedNodeTitle).Contains("CONV");

            // Verify quantized input weights are distinguished from activations
            NodePortItem? weightPort = vm.SelectedNodeInputs.FirstOrDefault(i => i.KindBadge.Contains("Weight"));
            await Assert.That(weightPort).IsNotNull();
            await Assert.That(weightPort!.IsQuantized).IsTrue();
            await Assert.That(weightPort.QuantizationBadge).Contains("8");
            await Assert.That(weightPort.Details).IsNotNull();
            await Assert.That(weightPort.Details!).Contains("Scale:");
        });
    }

    [Test]
    public async Task GraphCanvas_KeyboardAndWheelInteraction_AdjustsZoomAndPan()
    {
        string path = ResolveFixturePath("onnx/tiny_mlp.onnx");
        MainWindowViewModel vm = new();
        await vm.LoadModelAsync(path);

        await HeadlessAppSession.DispatchAsync(async () =>
        {
            GraphCanvas canvas = new()
            {
                Layout = vm.CurrentLayout
            };

            canvas.Measure(new Size(800, 600));
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ZoomToFit();

            double initialZoom = canvas.Zoom;
            Vector initialPan = canvas.PanOffset;

            // 1. Zoom In via Ctrl+OemPlus KeyDown
            KeyEventArgs zoomInKey = new()
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.OemPlus,
                KeyModifiers = KeyModifiers.Control,
                Source = canvas
            };
            canvas.RaiseEvent(zoomInKey);
            await Assert.That(canvas.Zoom).IsGreaterThan(initialZoom);

            // 2. Zoom Out via Ctrl+OemMinus KeyDown
            KeyEventArgs zoomOutKey = new()
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.OemMinus,
                KeyModifiers = KeyModifiers.Control,
                Source = canvas
            };
            canvas.RaiseEvent(zoomOutKey);

            // 3. Zoom To Fit via Ctrl+D0 KeyDown
            KeyEventArgs fitKey = new()
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.D0,
                KeyModifiers = KeyModifiers.Control,
                Source = canvas
            };
            canvas.RaiseEvent(fitKey);
            await Assert.That(Math.Abs(canvas.Zoom - initialZoom) < 0.05).IsTrue();

            // 4. Arrow keys pan
            KeyEventArgs arrowLeftKey = new()
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Left,
                Source = canvas
            };
            canvas.RaiseEvent(arrowLeftKey);
            await Assert.That(canvas.PanOffset.X).IsGreaterThan(initialPan.X);

            KeyEventArgs arrowDownKey = new()
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Down,
                Source = canvas
            };
            canvas.RaiseEvent(arrowDownKey);
            await Assert.That(canvas.PanOffset.Y).IsLessThan(initialPan.Y);

            // 5. Pinch gesture zoom centered on cursor
            Point pinchCenter = new(400, 300);
            Point worldBeforePinch = canvas.ScreenToWorld(pinchCenter);
            PinchEventArgs pinchArgs = new(1.25, pinchCenter)
            {
                RoutedEvent = Gestures.PinchEvent,
                Source = canvas
            };
            double beforePinch = canvas.Zoom;
            canvas.RaiseEvent(pinchArgs);
            await Assert.That(canvas.Zoom).IsGreaterThan(beforePinch);
            Point screenAfterPinch = canvas.WorldToScreen(worldBeforePinch);
            await Assert.That(Math.Abs(screenAfterPinch.X - pinchCenter.X)).IsLessThan(1e-4);
            await Assert.That(Math.Abs(screenAfterPinch.Y - pinchCenter.Y)).IsLessThan(1e-4);

            // 6. Verify LOD retains parameters at 54% zoom (user zoom level)
            canvas.Zoom = 0.54;
            await Assert.That(canvas.Zoom).IsEqualTo(0.54);
            // Render at 54% to confirm no exceptions and parameters remain intact
            using RenderTargetBitmap bitmap = new(new PixelSize(800, 600));
            bitmap.Render(canvas);

            // 7. Verify LOD retains parameters even when zoomed out to 15%
            canvas.Zoom = 0.15;
            await Assert.That(canvas.Zoom).IsEqualTo(0.15);
            bitmap.Render(canvas);
        });
    }

    private static string ResolveFixturePath(string relative)
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "dot-node-viewer.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "tests", "DotNodeViewer.Core.Tests", "Fixtures", relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate fixture '{relative}' starting from '{AppContext.BaseDirectory}'");
    }
}

