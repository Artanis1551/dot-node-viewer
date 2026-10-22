using DotNodeViewer.App;

namespace DotNodeViewer.Browser;

/// <summary>
/// Provides the browser host metadata for <c>DotNodeViewer.Browser</c>.
/// </summary>
public static class BrowserHostInfo
{
    /// <summary>
    /// Gets the canonical name of the browser host assembly.
    /// </summary>
    /// <value>The assembly name string.</value>
    public static string Name => "DotNodeViewer.Browser";

    /// <summary>
    /// Gets the shared application assembly name hosted by the browser project.
    /// </summary>
    /// <value>The shared application assembly name.</value>
    public static string HostedAppName => AppLibraryInfo.Name;
}

