using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using DotNodeViewer.App;

[assembly: SupportedOSPlatform("browser")]

namespace DotNodeViewer.Browser;

/// <summary>
/// WebAssembly entry point for <c>DotNodeViewer.Browser</c>.
/// </summary>
public static class Program
{
    /// <summary>
    /// Starts the browser application.
    /// </summary>
    /// <param name="args">Command line arguments passed to the browser head.</param>
    /// <returns>A task representing the browser event loop.</returns>
    public static async Task Main(string[] args)
    {
        await BuildAvaloniaApp()
            .StartBrowserAppAsync("out");
    }

    /// <summary>
    /// Configures the shared <see cref="AppBuilder"/> for WebAssembly execution.
    /// </summary>
    /// <returns>The configured <see cref="AppBuilder"/>.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DotNodeViewer.App.App>();
}

