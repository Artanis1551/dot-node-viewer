using DotNodeViewer.Core;
using DotNodeViewer.Layout;

namespace DotNodeViewer.App;

/// <summary>
/// Provides assembly-level metadata for the shared Avalonia visualizer application library.
/// </summary>
public static class AppLibraryInfo
{
    /// <summary>
    /// Gets the canonical name of the shared application assembly.
    /// </summary>
    /// <value>The assembly name string.</value>
    public static string Name => "DotNodeViewer.App";

    /// <summary>
    /// Gets the core library name used by the application.
    /// </summary>
    /// <value>The core library assembly name.</value>
    public static string CoreName => CoreLibraryInfo.Name;

    /// <summary>
    /// Gets the layout library name used by the application.
    /// </summary>
    /// <value>The layout library assembly name.</value>
    public static string LayoutName => LayoutLibraryInfo.Name;
}

