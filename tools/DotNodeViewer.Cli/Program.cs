using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Cli;

/// <summary>
/// Provides the command-line entry point for <c>DotNodeViewer.Cli</c>, supporting <c>detect</c>, <c>inspect</c>, and <c>snapshot</c> commands.
/// </summary>
public static class Program
{
    /// <summary>
    /// Executes the CLI command specified by the command-line arguments.
    /// </summary>
    /// <param name="args">The command-line arguments passed to the CLI tool.</param>
    /// <returns>A task representing the asynchronous execution and returning the process exit code (<c>0</c> for success, non-zero for errors).</returns>
    public static Task<int> Main(string[] args) =>
        RunAsync(args, Console.Out, Console.Error, ModelFormatRegistry.Default);

    /// <summary>
    /// Executes the CLI command using the supplied output writers and <see cref="ModelFormatRegistry"/>.
    /// </summary>
    /// <param name="args">The command-line arguments to process.</param>
    /// <param name="stdout">The writer for standard output.</param>
    /// <param name="stderr">The writer for error output.</param>
    /// <param name="registry">An optional custom <see cref="ModelFormatRegistry"/>; defaults to <see cref="ModelFormatRegistry.Default"/>.</param>
    /// <returns>A task representing the asynchronous execution and returning the exit code.</returns>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        ModelFormatRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        ModelFormatRegistry activeRegistry = registry ?? ModelFormatRegistry.Default;

        if (args.Length == 0 || IsHelpFlag(args[0]))
        {
            WriteUsage(stdout);
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        if (args.Length < 2)
        {
            await stderr.WriteLineAsync($"Error: Missing <file> argument for command '{args[0]}'.").ConfigureAwait(false);
            WriteUsage(stderr);
            return 1;
        }

        string filePath = args[1];
        if (!File.Exists(filePath))
        {
            await stderr.WriteLineAsync($"Error: File not found: '{filePath}'.").ConfigureAwait(false);
            return 1;
        }

        try
        {
            return command switch
            {
                "detect" => await ExecuteDetectAsync(filePath, stdout, stderr, activeRegistry).ConfigureAwait(false),
                "inspect" => await ExecuteInspectAsync(filePath, stdout, activeRegistry).ConfigureAwait(false),
                "snapshot" => await ExecuteSnapshotAsync(filePath, args, stdout, stderr, activeRegistry).ConfigureAwait(false),
                _ => await HandleUnknownCommandAsync(args[0], stderr).ConfigureAwait(false),
            };
        }
        catch (ModelParseException ex)
        {
            await stderr.WriteLineAsync($"Parse error: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
        catch (IOException ex)
        {
            await stderr.WriteLineAsync($"I/O error: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
    }

    private static async Task<int> ExecuteDetectAsync(
        string filePath,
        TextWriter stdout,
        TextWriter stderr,
        ModelFormatRegistry registry)
    {
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelProbe probe = await ModelProbe.CreateAsync(stream, Path.GetFileName(filePath)).ConfigureAwait(false);
        IModelFormatDetector? detector = registry.TryDetect(probe);
        if (detector is null)
        {
            await stderr.WriteLineAsync($"Error: Unrecognized model format for '{filePath}'.").ConfigureAwait(false);
            return 2;
        }

        await stdout.WriteLineAsync(detector.Format.Id.Value).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ExecuteInspectAsync(
        string filePath,
        TextWriter stdout,
        ModelFormatRegistry registry)
    {
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelGraph graph = await registry.ReadAsync(
            stream,
            new ModelReadOptions { FileName = Path.GetFileName(filePath) }).ConfigureAwait(false);

        await stdout.WriteLineAsync($"Format:    {graph.Format.ToDisplayString()} ({graph.Format.Id})").ConfigureAwait(false);
        await stdout.WriteLineAsync($"Producer:  {graph.Producer ?? "n/a"}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"Subgraphs: {graph.Subgraphs.Count}").ConfigureAwait(false);

        for (int s = 0; s < graph.Subgraphs.Count; s++)
        {
            Subgraph subgraph = graph.Subgraphs[s];
            await stdout.WriteLineAsync().ConfigureAwait(false);
            await stdout.WriteLineAsync($"Subgraph [{s}] '{subgraph.Name}':").ConfigureAwait(false);
            await stdout.WriteLineAsync($"  Nodes:   {subgraph.Nodes.Count}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"  Tensors: {subgraph.Tensors.Count}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"  Inputs:  {subgraph.Inputs.Count}").ConfigureAwait(false);
            for (int i = 0; i < subgraph.Inputs.Count; i++)
            {
                GraphPort port = subgraph.Inputs[i];
                for (int t = 0; t < port.Tensors.Count; t++)
                {
                    TensorEdge edge = port.Tensors[t];
                    string shapeStr = edge.Descriptor.Shape?.ToDisplayString() ?? "unranked";
                    await stdout.WriteLineAsync($"    - {edge.Name}: {edge.Descriptor.DataType} {shapeStr}").ConfigureAwait(false);
                }
            }

            await stdout.WriteLineAsync($"  Outputs: {subgraph.Outputs.Count}").ConfigureAwait(false);
            for (int i = 0; i < subgraph.Outputs.Count; i++)
            {
                GraphPort port = subgraph.Outputs[i];
                for (int t = 0; t < port.Tensors.Count; t++)
                {
                    TensorEdge edge = port.Tensors[t];
                    string shapeStr = edge.Descriptor.Shape?.ToDisplayString() ?? "unranked";
                    await stdout.WriteLineAsync($"    - {edge.Name}: {edge.Descriptor.DataType} {shapeStr}").ConfigureAwait(false);
                }
            }
        }

        if (graph.Diagnostics.Count > 0)
        {
            await stdout.WriteLineAsync().ConfigureAwait(false);
            await stdout.WriteLineAsync($"Diagnostics ({graph.Diagnostics.Count}):").ConfigureAwait(false);
            for (int i = 0; i < graph.Diagnostics.Count; i++)
            {
                ParseDiagnostic diag = graph.Diagnostics[i];
                await stdout.WriteLineAsync($"  [{diag.Severity}] {diag.Code}: {diag.Message}").ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static async Task<int> ExecuteSnapshotAsync(
        string filePath,
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        ModelFormatRegistry registry)
    {
        string? modelId = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--id", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    await stderr.WriteLineAsync("Error: Missing value for '--id' option.").ConfigureAwait(false);
                    return 1;
                }

                modelId = args[++i];
            }
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = Path.GetFileNameWithoutExtension(filePath);
        }

        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelGraph graph = await registry.ReadAsync(
            stream,
            new ModelReadOptions { FileName = Path.GetFileName(filePath) }).ConfigureAwait(false);

        string json = CliSnapshotBuilder.BuildJson(graph, modelId);
        await stdout.WriteLineAsync(json).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> HandleUnknownCommandAsync(string command, TextWriter stderr)
    {
        await stderr.WriteLineAsync($"Error: Unknown command '{command}'.").ConfigureAwait(false);
        WriteUsage(stderr);
        return 1;
    }

    private static bool IsHelpFlag(string arg) =>
        string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "help", StringComparison.OrdinalIgnoreCase);

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: DotNodeViewer.Cli <command> <file> [options]");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        writer.WriteLine("  detect <file>                     Print the detected model format ID");
        writer.WriteLine("  inspect <file>                    Print a human-readable model graph summary");
        writer.WriteLine("  snapshot <file> --id <model-id>   Print the golden .expected.json topology snapshot");
    }
}
