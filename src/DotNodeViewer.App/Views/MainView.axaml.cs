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
            IStorageItem[]? files = e.DataTransfer.TryGetFiles();
            bool canAccept = files is { Length: > 0 } &&
                (files.Any(f => f is IStorageFolder) || MainWindowViewModel.CanAcceptFiles(files.Select(f => f.Name).ToArray()));

            e.DragEffects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
            vm.SetDropTargetState(isActive: true, isValid: canAccept);
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
            IStorageItem[]? files = e.DataTransfer.TryGetFiles();
            bool canAccept = files is { Length: > 0 } &&
                (files.Any(f => f is IStorageFolder) || MainWindowViewModel.CanAcceptFiles(files.Select(f => f.Name).ToArray()));

            if (canAccept)
            {
                e.DragEffects = DragDropEffects.Copy;
                vm.SetDropTargetState(isActive: true, isValid: true);
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
                vm.SetDropTargetState(isActive: true, isValid: false);
            }
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }

        e.Handled = true;
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

        if (vm is not null)
        {
            vm.SetDropTargetState(isActive: false);

            IStorageItem[]? files = e.DataTransfer.TryGetFiles();
            if (files is { Length: > 0 })
            {
                await vm.HandleDroppedFilesAsync(files).ConfigureAwait(false);
            }
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

