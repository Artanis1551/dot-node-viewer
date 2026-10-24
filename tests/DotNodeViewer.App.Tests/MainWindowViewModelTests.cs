using DotNodeViewer.App.ViewModels;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.App.Tests;

/// <summary>
/// Unit and headless integration tests for <see cref="MainWindowViewModel"/>.
/// </summary>
public sealed class MainWindowViewModelTests
{
    private const string FixtureDir = "../../../tests/DotNodeViewer.Core.Tests/Fixtures";

    [Test]
    public async Task InitialState_MatchesEmptyDesignContract()
    {
        MainWindowViewModel vm = new();

        await Assert.That(vm.HasModel).IsFalse();
        await Assert.That(vm.HasError).IsFalse();
        await Assert.That(vm.IsLoading).IsFalse();
        await Assert.That(vm.StatusText).IsEqualTo("Ready");
        await Assert.That(vm.Title).IsEqualTo("dot-node-viewer");
        await Assert.That(vm.IsDarkTheme).IsTrue();
        await Assert.That(vm.Inputs).IsEmpty();
        await Assert.That(vm.Outputs).IsEmpty();
        await Assert.That(vm.OperatorCategories).IsEmpty();
    }

    [Test]
    public async Task LoadModelAsync_OnnxTinyMlp_LoadsSuccessfullyWithLayout()
    {
        string path = ResolveFixturePath("onnx/tiny_mlp.onnx");
        MainWindowViewModel vm = new();

        await vm.LoadModelAsync(path);

        await Assert.That(vm.HasModel).IsTrue();
        await Assert.That(vm.HasError).IsFalse();
        await Assert.That(vm.ModelName).IsEqualTo("tiny_mlp.onnx");
        await Assert.That(vm.FormatDisplayName).Contains("ONNX");
        await Assert.That(vm.TotalNodeCount).IsGreaterThan(0);
        await Assert.That(vm.TotalEdgeCount).IsGreaterThan(0);
        await Assert.That(vm.CurrentLayout).IsNotNull();
        await Assert.That(vm.Inputs).IsNotEmpty();
        await Assert.That(vm.Outputs).IsNotEmpty();
        await Assert.That(vm.OperatorCategories).IsNotEmpty();
        await Assert.That(vm.RecentFiles).Contains(Path.GetFullPath(path));
    }

    [Test]
    public async Task LoadModelAsync_OnnxDynamicBatch_PreservesDynamicShapes()
    {
        string path = ResolveFixturePath("onnx/tiny_cnn_dynamic_batch.onnx");
        MainWindowViewModel vm = new();

        await vm.LoadModelAsync(path);

        await Assert.That(vm.HasModel).IsTrue();
        await Assert.That(vm.Inputs.Count).IsGreaterThanOrEqualTo(1);

        TensorSummaryItem inputTensor = vm.Inputs[0];
        await Assert.That(inputTensor.ShapeText).Contains("batch");
    }

    [Test]
    public async Task LoadModelAsync_TfliteInt8_DetectsQuantizationAndPopulatesBadges()
    {
        string path = ResolveFixturePath("tflite/tiny_cnn_int8.tflite");
        MainWindowViewModel vm = new();

        await vm.LoadModelAsync(path);

        await Assert.That(vm.HasModel).IsTrue();
        await Assert.That(vm.FormatDisplayName).Contains("TensorFlow Lite");
        await Assert.That(vm.QuantizedTensorCount).IsGreaterThan(0);

        TensorSummaryItem inputTensor = vm.Inputs[0];
        await Assert.That(inputTensor.IsQuantized).IsTrue();
        await Assert.That(inputTensor.QuantizationBadge).IsEqualTo("INT8");
    }

    [Test]
    public async Task LoadModelAsync_CorruptedFile_PopulatesErrorStateCleanly()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"corrupt_model_{Guid.NewGuid():N}.onnx");
        await File.WriteAllBytesAsync(tempFile, [0x08, 0x01, 0xFF, 0xFF, 0x00]);

        try
        {
            MainWindowViewModel vm = new();
            await vm.LoadModelAsync(tempFile);

            await Assert.That(vm.HasModel).IsFalse();
            await Assert.That(vm.HasError).IsTrue();
            await Assert.That(vm.ErrorMessage).IsNotNull();
            await Assert.That(string.IsNullOrWhiteSpace(vm.ErrorMessage)).IsFalse();

            // ClearError resets error state
            vm.ClearError();
            await Assert.That(vm.HasError).IsFalse();
            await Assert.That(vm.ErrorMessage).IsNull();
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Test]
    public async Task LoadModelAsync_NonExistentFile_PopulatesFileNotFoundError()
    {
        MainWindowViewModel vm = new();
        await vm.LoadModelAsync("/non/existent/path/model.onnx");

        await Assert.That(vm.HasModel).IsFalse();
        await Assert.That(vm.HasError).IsTrue();
        await Assert.That(vm.ErrorMessage).Contains("not found");
    }

    [Test]
    public async Task ToggleTheme_TogglesThemeFlag()
    {
        await HeadlessAppSession.DispatchAsync(async () =>
        {
            MainWindowViewModel vm = new();
            await Assert.That(vm.IsDarkTheme).IsTrue();

            vm.ToggleTheme();
            await Assert.That(vm.IsDarkTheme).IsFalse();

            vm.ToggleTheme();
            await Assert.That(vm.IsDarkTheme).IsTrue();
        });
    }

    [Test]
    public async Task SelectSubgraph_WithMultiSubgraphModel_UpdatesActiveLayout()
    {
        string path = ResolveFixturePath("onnx/if_subgraph.onnx");
        MainWindowViewModel vm = new();

        await vm.LoadModelAsync(path);

        await Assert.That(vm.HasModel).IsTrue();
        if (vm.Subgraphs.Count > 1)
        {
            Subgraph secondary = vm.Subgraphs[1];
            vm.SelectSubgraph(secondary);

            await Assert.That(vm.SelectedSubgraph).IsEqualTo(secondary);
            await Assert.That(vm.CurrentLayout).IsNotNull();
        }
    }

    [Test]
    public async Task KeyGestures_ForOpenModel_ParseProperly()
    {
        var ctrlO = Avalonia.Input.KeyGesture.Parse("Ctrl+O");
        await Assert.That(ctrlO.Key).IsEqualTo(Avalonia.Input.Key.O);
        await Assert.That(ctrlO.KeyModifiers).IsEqualTo(Avalonia.Input.KeyModifiers.Control);

        var cmdO = Avalonia.Input.KeyGesture.Parse("Cmd+O");
        await Assert.That(cmdO.Key).IsEqualTo(Avalonia.Input.Key.O);
        await Assert.That(cmdO.KeyModifiers).IsEqualTo(Avalonia.Input.KeyModifiers.Meta);
    }

    [Test]
    public async Task KeyBindings_CtrlOAndCmdO_ExecuteOpenModelCommand()
    {
        await HeadlessAppSession.DispatchAsync(async () =>
        {
            MainWindowViewModel vm = new();
            int filePickerInvocations = 0;
            vm.FilePickerAsync = () =>
            {
                filePickerInvocations++;
                return Task.FromResult<string?>(null);
            };

            Views.MainView view = new()
            {
                DataContext = vm
            };

            // Directly invoke the bound OpenModelCommand to verify ViewModel delegate
            await vm.OpenModelCommand.ExecuteAsync(null);
            await Assert.That(filePickerInvocations).IsEqualTo(1);

            // Verify KeyBindings collection on MainView
            bool hasCtrlO = false;
            bool hasCmdO = false;
            foreach (var kb in view.KeyBindings)
            {
                if (kb.Gesture is Avalonia.Input.KeyGesture gesture && gesture.Key == Avalonia.Input.Key.O)
                {
                    if (gesture.KeyModifiers == Avalonia.Input.KeyModifiers.Control)
                    {
                        hasCtrlO = true;
                    }
                    else if (gesture.KeyModifiers == Avalonia.Input.KeyModifiers.Meta)
                    {
                        hasCmdO = true;
                    }
                }
            }

            await Assert.That(hasCtrlO).IsTrue();
            await Assert.That(hasCmdO).IsTrue();
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
