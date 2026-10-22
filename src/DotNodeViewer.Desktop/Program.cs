namespace DotNodeViewer.Desktop;

/// <summary>
/// Provides the cross-platform desktop entry point for <c>DotNodeViewer.Desktop</c>.
/// </summary>
public static class Program
{
    /// <summary>
    /// Runs the desktop application.
    /// </summary>
    /// <param name="args">The command-line arguments passed to the process.</param>
    /// <returns>The process exit code.</returns>
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return 0;
    }
}

