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

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
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
                new FilePickerFileType("All Supported Models (*.onnx, *.tflite, *.pte, *.pt, *.pt2, *.keras, *.h5)")
                {
                    Patterns = ["*.onnx", "*.tflite", "*.pte", "*.pt", "*.pt2", "*.keras", "*.h5"]
                },
                new FilePickerFileType("ONNX Models (*.onnx)")
                {
                    Patterns = ["*.onnx"]
                },
                new FilePickerFileType("TensorFlow Lite Models (*.tflite)")
                {
                    Patterns = ["*.tflite"]
                },
                new FilePickerFileType("ExecuTorch Models (*.pte)")
                {
                    Patterns = ["*.pte"]
                },
                new FilePickerFileType("PyTorch Models (*.pt, *.pt2)")
                {
                    Patterns = ["*.pt", "*.pt2"]
                },
                new FilePickerFileType("Keras Models (*.keras, *.h5)")
                {
                    Patterns = ["*.keras", "*.h5"]
                },
                FilePickerFileTypes.All
            ]
        }).ConfigureAwait(true);

        return files is { Count: > 0 } ? files[0].TryGetLocalPath() : null;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Source == this)
        {
            RootMainView.ProcessDragEnter(e);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Source == this)
        {
            RootMainView.ProcessDragOver(e);
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (e.Source == this)
        {
            RootMainView.ProcessDragLeave(e);
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Source == this)
        {
            await RootMainView.ProcessDropAsync(e).ConfigureAwait(false);
        }
    }
}
