using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DotNodeViewer.App.ViewModels;

namespace DotNodeViewer.App.Views;

/// <summary>
/// Primary content view for <c>dot-node-viewer</c> shared across Desktop and WebAssembly heads.
/// </summary>
public partial class MainView : UserControl
{
    private int _dragDepth;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainView"/> class.
    /// </summary>
    public MainView()
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
            vm.RequestCenterOnNode = node => Canvas.CenterOnNode(node);
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        ProcessDragEnter(e);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        ProcessDragOver(e);
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        ProcessDragLeave(e);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        await ProcessDropAsync(e).ConfigureAwait(true);
    }

    /// <summary>
    /// Evaluates and handles a drag enter event, updating visual target cues.
    /// </summary>
    /// <param name="e">The drag event arguments.</param>
    internal void ProcessDragEnter(DragEventArgs e)
    {
        _dragDepth++;
        if (DataContext is MainWindowViewModel vm)
        {
            EvaluateDragState(e, out bool hasFiles, out bool canAccept);

            if (canAccept)
            {
                e.DragEffects = DragDropEffects.Copy;
                vm.SetDropTargetState(isActive: true, isValid: true);
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
                if (hasFiles)
                {
                    vm.SetDropTargetState(isActive: true, isValid: false);
                }
            }
        }

        e.Handled = true;
    }

    /// <summary>
    /// Evaluates and handles a drag over event, continuously updating drop validity and drag effects.
    /// </summary>
    /// <param name="e">The drag event arguments.</param>
    internal void ProcessDragOver(DragEventArgs e)
    {
        if (_dragDepth <= 0)
        {
            _dragDepth = 1;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            EvaluateDragState(e, out bool hasFiles, out bool canAccept);

            if (canAccept)
            {
                e.DragEffects = DragDropEffects.Copy;
                vm.SetDropTargetState(isActive: true, isValid: true);
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
                if (hasFiles)
                {
                    vm.SetDropTargetState(isActive: true, isValid: false);
                }
                else
                {
                    vm.SetDropTargetState(isActive: false);
                }
            }
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private static void EvaluateDragState(DragEventArgs e, out bool hasFiles, out bool canAccept)
    {
        hasFiles = false;
        canAccept = false;

        // 1. Check if the drag operation contains files (XDND text/uri-list on Linux, Files on browser, etc.)
        try
        {
#pragma warning disable CS0618
            if (e.Data.Contains(DataFormats.Files) || e.Data.Contains(DataFormats.FileNames))
            {
                hasFiles = true;
            }
#pragma warning restore CS0618
        }
        catch
        {
            // Ignore format inspection failure
        }

        // 2. Try inspecting explicit storage items (populated in tests, macOS, and some desktop environments)
        try
        {
            IStorageItem[]? files = e.DataTransfer.TryGetFiles();
            if (files is { Length: > 0 })
            {
                hasFiles = true;
                canAccept = files.Any(f => f is IStorageFolder) ||
                            MainWindowViewModel.CanAcceptFiles(files.Select(f => f.Name).ToArray());
                return;
            }
        }
        catch
        {
            // Ignore storage item extraction failure
        }

        // 3. Try inspecting file name paths from legacy/OS clipboard data (DataFormats.FileNames)
        try
        {
#pragma warning disable CS0618
            IEnumerable<string>? names = e.Data.GetFileNames();
            if (names is not null)
            {
                string[] arr = names.ToArray();
                if (arr.Length > 0)
                {
                    hasFiles = true;
                    canAccept = MainWindowViewModel.CanAcceptFiles(arr);
                    return;
                }
            }
#pragma warning restore CS0618
        }
        catch
        {
            // Ignore file names extraction failure
        }

        // 4. Fallback: if the platform reports a file payload (hasFiles == true) but masks the file list
        // until the drop release (standard behavior on Linux X11 XDND and WebAssembly browser sandbox),
        // we must accept the drag and set Copy effect so the OS permits the user to complete the drop.
        if (hasFiles)
        {
            canAccept = true;
        }
    }

    /// <summary>
    /// Handles drag leave transitions, safely dismissing the drop target when leaving the view bounds.
    /// </summary>
    /// <param name="e">The drag event arguments.</param>
    internal void ProcessDragLeave(DragEventArgs e)
    {
        _dragDepth--;
        if (_dragDepth <= 0)
        {
            _dragDepth = 0;
            if (DataContext is MainWindowViewModel vm)
            {
                vm.SetDropTargetState(isActive: false);
            }
        }

        e.Handled = true;
    }

    /// <summary>
    /// Dispatches dropped storage items to the ViewModel for asynchronous parsing and rendering.
    /// </summary>
    /// <param name="e">The drag event arguments.</param>
    /// <returns>A task representing the asynchronous drop operation.</returns>
    internal async Task ProcessDropAsync(DragEventArgs e)
    {
        _dragDepth = 0;
        MainWindowViewModel? vm = null;
        if (Dispatcher.UIThread.CheckAccess())
        {
            vm = DataContext as MainWindowViewModel;
            e.Handled = true;
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm = DataContext as MainWindowViewModel;
                e.Handled = true;
            });
        }

        if (vm is null)
        {
            return;
        }

        vm.SetDropTargetState(isActive: false);

        // 1. Try modern storage items from DataTransfer (WASM, macOS, Windows)
        IStorageItem[]? files = null;
        try
        {
            files = e.DataTransfer.TryGetFiles();
        }
        catch
        {
            // Ignore
        }

        if (files is { Length: > 0 })
        {
            await vm.HandleDroppedFilesAsync(files).ConfigureAwait(false);
            return;
        }

        // 2. Fallback: try raw file paths from Data.GetFileNames() (Linux X11 XDND text/uri-list)
        try
        {
#pragma warning disable CS0618
            if (e.Data.Contains(DataFormats.FileNames))
            {
                IEnumerable<string>? paths = e.Data.GetFileNames();
                if (paths is not null)
                {
                    string[] pathArr = paths.ToArray();
                    if (pathArr.Length > 0)
                    {
                        var topLevel = TopLevel.GetTopLevel(this);
                        if (topLevel?.StorageProvider is { } storageProvider)
                        {
                            List<IStorageItem> resolvedItems = new();
                            foreach (string p in pathArr)
                            {
                                if (System.IO.File.Exists(p))
                                {
                                    IStorageFile? file = await storageProvider.TryGetFileFromPathAsync(new Uri(System.IO.Path.GetFullPath(p)));
                                    if (file is not null)
                                    {
                                        resolvedItems.Add(file);
                                    }
                                }
                                else if (System.IO.Directory.Exists(p))
                                {
                                    IStorageFolder? folder = await storageProvider.TryGetFolderFromPathAsync(new Uri(System.IO.Path.GetFullPath(p)));
                                    if (folder is not null)
                                    {
                                        resolvedItems.Add(folder);
                                    }
                                }
                            }

                            if (resolvedItems.Count > 0)
                            {
                                await vm.HandleDroppedFilesAsync(resolvedItems).ConfigureAwait(false);
                                return;
                            }
                        }

                        // Direct fallback to LoadModelAsync with primary recognized model file
                        string? primary = pathArr.FirstOrDefault(p => MainWindowViewModel.IsSupportedModelExtension(p)) ?? pathArr[0];
                        await vm.LoadModelAsync(primary).ConfigureAwait(false);
                    }
                }
            }
#pragma warning restore CS0618
        }
        catch (Exception ex)
        {
            vm.SetDropTargetState(isActive: true, isValid: false, feedbackMessage: ex.Message);
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel vm && vm.IsDropTargetActive)
        {
            _dragDepth = 0;
            vm.SetDropTargetState(isActive: false);
            e.Handled = true;
        }
    }
}

