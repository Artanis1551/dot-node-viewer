using DotNodeViewer.Core;

namespace DotNodeViewer.Layout;

/// <summary>
/// Provides assembly-level metadata for the <c>DotNodeViewer.Layout</c> layered graph layout library.
/// </summary>
public static class LayoutLibraryInfo
{
    /// <summary>
    /// Gets the canonical name of the layout library assembly.
    /// </summary>
    /// <value>The assembly name string.</value>
    public static string Name => "DotNodeViewer.Layout";

    /// <summary>
    /// Gets the name of the core IR library referenced by the layout engine.
    /// </summary>
    /// <value>The core library name string.</value>
    public static string CoreDependencyName => CoreLibraryInfo.Name;
}

