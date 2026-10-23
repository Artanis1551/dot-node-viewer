using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using DotNodeViewer.App.ViewModels;

namespace DotNodeViewer.App.Views;

/// <summary>
/// Primary desktop shell window for <c>dot-node-viewer</c>.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.FilePickerAsync = OpenStoragePickerAsync;
        }
    }

    private async Task<string?> OpenStoragePickerAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Neural Network Model",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Supported Models (*.onnx, *.tflite)")
                {
                    Patterns = ["*.onnx", "*.tflite"]
                },
                new FilePickerFileType("ONNX Models (*.onnx)")
                {
                    Patterns = ["*.onnx"]
                },
                new FilePickerFileType("TensorFlow Lite Models (*.tflite)")
                {
                    Patterns = ["*.tflite"]
                },
                FilePickerFileTypes.All
            ]
        }).ConfigureAwait(true);

        return files is { Count: > 0 } ? files[0].TryGetLocalPath() : null;
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        IStorageItem[]? files = e.DataTransfer.TryGetFiles();
        e.DragEffects = files is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        IStorageItem[]? files = e.DataTransfer.TryGetFiles();
        if (files is { Length: > 0 } && DataContext is MainWindowViewModel vm)
        {
            string? path = files[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                await vm.LoadModelAsync(path).ConfigureAwait(true);
            }
        }

        e.Handled = true;
    }
}
