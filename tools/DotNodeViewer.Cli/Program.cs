namespace DotNodeViewer.Cli;

/// <summary>
/// Provides the command-line entry point for <c>DotNodeViewer.Cli</c>.
/// </summary>
public static class Program
{
    /// <summary>
    /// Executes the CLI command specified by the command-line arguments.
    /// </summary>
    /// <param name="args">The command-line arguments passed to the CLI tool.</param>
    /// <returns>The process exit code.</returns>
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return 0;
    }
}

