using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DotNodeViewer.App.ViewModels;
using DotNodeViewer.App.Views;

namespace DotNodeViewer.App.Tests;

/// <summary>
/// Unit and headless integration tests for cross-platform drag-and-drop neural network model loading,
/// format filtering, multi-file sidecar handling, and visual drop overlay transitions.
/// </summary>
public sealed class DragAndDropModelLoadingTests
{
    private const string FixtureDir = "../../../tests/DotNodeViewer.Core.Tests/Fixtures";

    [Before(Test)]
    public void SetUp()
    {
        HeadlessAppSession.EnsureInitialized();
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

    private static async Task<IStorageFile> GetStorageFileAsync(Window window, string path)
    {
        IStorageFile? file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(Path.GetFullPath(path)));
        if (file is null)
        {
            throw new InvalidOperationException($"Could not obtain IStorageFile for path: {path}");
        }

        return file;
    }

    private static async Task<IStorageFolder> GetStorageFolderAsync(Window window, string dirPath)
    {
        IStorageFolder? folder = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(dirPath)));
        if (folder is null)
        {
            throw new InvalidOperationException($"Could not obtain IStorageFolder for dir: {dirPath}");
        }

        return folder;
    }

    [Test]
    public async Task CanAcceptFiles_RecognizedExtensions_ReturnsTrue()
    {
        string[] validSingle = ["resnet50.onnx"];
        string[] validMultiple = ["model.onnx", "model.onnx.data"];
        string[] validTflite = ["detector.tflite"];
        string[] validExecuTorch = ["model.pte"];
        string[] validPyTorch = ["weights.pt", "model.pt2", "legacy.pth"];
        string[] validKeras = ["network.keras", "legacy.h5"];
        string[] mixedCase = ["MODEL.ONNX", "GRAPH.TFLITE"];

        await Assert.That(MainWindowViewModel.CanAcceptFiles(validSingle)).IsTrue();
        await Assert.That(MainWindowViewModel.CanAcceptFiles(validMultiple)).IsTrue();
        await Assert.That(MainWindowViewModel.CanAcceptFiles(validTflite)).IsTrue();
        await Assert.That(MainWindowViewModel.CanAcceptFiles(validExecuTorch)).IsTrue();
        await Assert.That(MainWindowViewModel.CanAcceptFiles(validPyTorch)).IsTrue();
        await Assert.That(MainWindowViewModel.CanAcceptFiles(validKeras)).IsTrue();
        await Assert.That(MainWindowViewModel.CanAcceptFiles(mixedCase)).IsTrue();
    }

    [Test]
    public async Task CanAcceptFiles_UnsupportedOrEmpty_ReturnsFalse()
    {
        await Assert.That(MainWindowViewModel.CanAcceptFiles(null)).IsFalse();
        await Assert.That(MainWindowViewModel.CanAcceptFiles(Array.Empty<string>())).IsFalse();
        await Assert.That(MainWindowViewModel.CanAcceptFiles(["document.pdf"])).IsFalse();
        await Assert.That(MainWindowViewModel.CanAcceptFiles(["archive.zip", "video.mp4"])).IsFalse();
        await Assert.That(MainWindowViewModel.CanAcceptFiles(["model.onnx.data"])).IsFalse();
    }

    [Test]
    public async Task FindPrimaryModelItem_PrioritizesOnnxOverCompanionData()
    {
        await HeadlessAppSession.DispatchAsync(async () =>
        {
            Window window = new();
            string onnxPath = ResolveFixturePath("onnx/tiny_mlp.onnx");
            IStorageFile onnx = await GetStorageFileAsync(window, onnxPath);
            IStorageFile companion = await GetStorageFileAsync(window, onnxPath);

            IStorageItem? result = MainWindowViewModel.FindPrimaryModelItem([companion, onnx]);

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.Name).IsEqualTo("tiny_mlp.onnx");
        });
    }

    [Test]
    public async Task FindPrimaryModelItem_UnsupportedOnly_ReturnsNull()
    {
        await HeadlessAppSession.DispatchAsync(async () =>
        {
            Window window = new();
            string textPath = Path.Combine(AppContext.BaseDirectory, "test_file.txt");
            await File.WriteAllTextAsync(textPath, "sample text");
            IStorageFile textFile = await GetStorageFileAsync(window, textPath);

            IStorageItem? result = MainWindowViewModel.FindPrimaryModelItem([textFile]);

            await Assert.That(result).IsNull();
        });
    }

    [Test]
    public async Task SetDropTargetState_UpdatesActiveAndValidFeedback()
    {
        MainWindowViewModel vm = new();

        await Assert.That(vm.IsDropTargetActive).IsFalse();
        await Assert.That(vm.IsDropTargetValid).IsTrue();

        // Active + Valid
        vm.SetDropTargetState(isActive: true, isValid: true);
        await Assert.That(vm.IsDropTargetActive).IsTrue();
        await Assert.That(vm.IsDropTargetValid).IsTrue();
        await Assert.That(vm.DropTargetHeadline).IsEqualTo("Drop Model to Visualize");
        await Assert.That(vm.DropTargetFeedbackMessage).Contains("Release");

        // Active + Invalid
        vm.SetDropTargetState(isActive: true, isValid: false);
        await Assert.That(vm.IsDropTargetActive).IsTrue();
        await Assert.That(vm.IsDropTargetValid).IsFalse();
        await Assert.That(vm.DropTargetHeadline).IsEqualTo("Unsupported Model Format");
        await Assert.That(vm.DropTargetFeedbackMessage).Contains("Unsupported format");

        // Custom feedback message
        vm.SetDropTargetState(isActive: true, isValid: true, feedbackMessage: "Custom feedback prompt");
        await Assert.That(vm.DropTargetFeedbackMessage).IsEqualTo("Custom feedback prompt");

        // Dismiss
        vm.SetDropTargetState(isActive: false);
        await Assert.That(vm.IsDropTargetActive).IsFalse();
    }

    [Test]
    public async Task HandleDroppedFilesAsync_LocalPath_LoadsModelSuccessfully()
    {
        Window window = null!;
        IStorageFile file = null!;
        string path = ResolveFixturePath("onnx/tiny_mlp.onnx");
        await HeadlessAppSession.DispatchAsync(async () =>
        {
            window = new Window();
            file = await GetStorageFileAsync(window, path);
        });

        MainWindowViewModel vm = new();
        await vm.HandleDroppedFilesAsync([file]);

        await Assert.That(vm.HasModel).IsTrue();
        await Assert.That(vm.HasError).IsFalse();
        await Assert.That(vm.ModelName).IsEqualTo("tiny_mlp.onnx");
        await Assert.That(vm.FormatDisplayName).Contains("ONNX");
        await Assert.That(vm.TotalNodeCount).IsGreaterThan(0);
        await Assert.That(vm.TotalEdgeCount).IsGreaterThan(0);
    }

    [Test]
    public async Task HandleDroppedFilesAsync_EmptyList_ReturnsGracefully()
    {
        MainWindowViewModel vm = new();

        await vm.HandleDroppedFilesAsync([]);

        await Assert.That(vm.HasModel).IsFalse();
        await Assert.That(vm.HasError).IsFalse();
    }

    [Test]
    public async Task HandleDroppedFilesAsync_DirectoryWithModel_LoadsCandidate()
    {
        Window window = null!;
        IStorageFolder folder = null!;
        string path = ResolveFixturePath("onnx/tiny_mlp.onnx");
        string dir = Path.GetDirectoryName(path)!;
        await HeadlessAppSession.DispatchAsync(async () =>
        {
            window = new Window();
            folder = await GetStorageFolderAsync(window, dir);
        });

        MainWindowViewModel vm = new();
        await vm.HandleDroppedFilesAsync([folder]);

        await Assert.That(vm.HasModel).IsTrue();
        await Assert.That(vm.HasError).IsFalse();
        await Assert.That(vm.TotalNodeCount).IsGreaterThan(0);
    }

    [Test]
    public async Task HandleDroppedFilesAsync_DirectoryWithoutModel_SetsError()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "dotnodeviewer_test_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            Window window = null!;
            IStorageFolder folder = null!;
            await HeadlessAppSession.DispatchAsync(async () =>
            {
                window = new Window();
                folder = await GetStorageFolderAsync(window, tempDir);
            });

            MainWindowViewModel vm = new();
            await vm.HandleDroppedFilesAsync([folder]);

            await Assert.That(vm.HasModel).IsFalse();
            await Assert.That(vm.HasError).IsTrue();
            await Assert.That(vm.ErrorMessage).Contains("No supported neural network model found in folder");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task MainView_ProcessDragEnterAndLeave_UpdatesState()
    {
        await HeadlessAppSession.DispatchAsync(async () =>
        {
            Window window = new();
            MainWindowViewModel vm = new();
            MainView view = new() { DataContext = vm };

            string path = ResolveFixturePath("onnx/tiny_mlp.onnx");
            IStorageFile file = await GetStorageFileAsync(window, path);
            DragEventArgs enterArgs = CreateDragEventArgs(DragDrop.DragEnterEvent, [file], view);

            view.ProcessDragEnter(enterArgs);
            await Assert.That(vm.IsDropTargetActive).IsTrue();
            await Assert.That(vm.IsDropTargetValid).IsTrue();
            await Assert.That(enterArgs.DragEffects).IsEqualTo(DragDropEffects.Copy);

            DragEventArgs leaveArgs = CreateDragEventArgs(DragDrop.DragLeaveEvent, [file], view);
            view.ProcessDragLeave(leaveArgs);
            await Assert.That(vm.IsDropTargetActive).IsFalse();
        });
    }

    [Test]
    public async Task MainView_ProcessDragOver_UnsupportedFile_SetsNoneEffect()
    {
        await HeadlessAppSession.DispatchAsync(async () =>
        {
            Window window = new();
            MainWindowViewModel vm = new();
            MainView view = new() { DataContext = vm };

            string textPath = Path.Combine(AppContext.BaseDirectory, "unsupported_doc.pdf");
            await File.WriteAllTextAsync(textPath, "mock pdf content");
            IStorageFile file = await GetStorageFileAsync(window, textPath);
            DragEventArgs overArgs = CreateDragEventArgs(DragDrop.DragOverEvent, [file], view);

            view.ProcessDragOver(overArgs);
            await Assert.That(vm.IsDropTargetActive).IsTrue();
            await Assert.That(vm.IsDropTargetValid).IsFalse();
            await Assert.That(overArgs.DragEffects).IsEqualTo(DragDropEffects.None);
        });
    }

    [Test]
    public async Task MainView_ProcessDropAsync_LoadsModelAndClearsOverlay()
    {
        Window window = null!;
        MainView view = null!;
        MainWindowViewModel vm = new();
        string path = ResolveFixturePath("onnx/tiny_mlp.onnx");
        IStorageFile file = null!;

        await HeadlessAppSession.DispatchAsync(async () =>
        {
            window = new Window();
            file = await GetStorageFileAsync(window, path);
            view = new MainView { DataContext = vm };
            vm.SetDropTargetState(isActive: true, isValid: true);
        });

        DragEventArgs dropArgs = CreateDragEventArgs(DragDrop.DropEvent, [file], view);
        await view.ProcessDropAsync(dropArgs);

        await Assert.That(vm.IsDropTargetActive).IsFalse();
        await Assert.That(vm.HasModel).IsTrue();
        await Assert.That(vm.ModelName).IsEqualTo("tiny_mlp.onnx");
    }

    [Test]
    public async Task MainWindow_DelegatesDragEvents_ToRootMainView()
    {
        await HeadlessAppSession.DispatchAsync(async () =>
        {
            MainWindowViewModel vm = new();
            MainWindow window = new() { DataContext = vm };

            string path = ResolveFixturePath("onnx/tiny_mlp.onnx");
            IStorageFile file = await GetStorageFileAsync(window, path);
            DragEventArgs enterArgs = CreateDragEventArgs(DragDrop.DragEnterEvent, [file], window);

            window.RaiseEvent(enterArgs);

            await Assert.That(vm.IsDropTargetActive).IsTrue();
            await Assert.That(vm.IsDropTargetValid).IsTrue();
        });
    }

    [Test]
    public async Task MainView_EscapeKey_DismissesDropTarget()
    {
        await HeadlessAppSession.DispatchAsync(async () =>
        {
            MainWindowViewModel vm = new();
            MainView view = new() { DataContext = vm };

            vm.SetDropTargetState(isActive: true, isValid: true);
            await Assert.That(vm.IsDropTargetActive).IsTrue();

            KeyEventArgs escapeArgs = new()
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Escape,
                Source = view
            };

            view.RaiseEvent(escapeArgs);
            await Assert.That(vm.IsDropTargetActive).IsFalse();
        });
    }

    private static DragEventArgs CreateDragEventArgs(
        RoutedEvent<DragEventArgs> routedEvent,
        IStorageItem[] items,
        Interactive target)
    {
#pragma warning disable CS0618
        DataObject data = new();
        data.Set(DataFormats.Files, items);

        DragEventArgs e = new(routedEvent, data, target, new Point(0, 0), KeyModifiers.None)
        {
            Source = target
        };
#pragma warning restore CS0618

        return e;
    }
}
