using Avalonia.Controls;

namespace DotNodeViewer.App.Views;

/// <summary>
/// A contextual properties sidebar that displays either the Model &amp; Graph Overview (when no node is selected)
/// or the Node Inspector (displaying typed attributes, activations, weights, and quantization parameters).
/// </summary>
public partial class PropertiesPanelView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PropertiesPanelView"/> class.
    /// </summary>
    public PropertiesPanelView()
    {
        InitializeComponent();
    }
}

