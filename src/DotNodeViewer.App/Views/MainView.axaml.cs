using Avalonia.Controls;
using DotNodeViewer.App.ViewModels;

namespace DotNodeViewer.App.Views;

/// <summary>
/// Primary content view for <c>dot-node-viewer</c> shared across Desktop and WebAssembly heads.
/// </summary>
public partial class MainView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MainView"/> class.
    /// </summary>
    public MainView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.RequestCenterOnNode = node => Canvas.CenterOnNode(node);
        }
    }
}

