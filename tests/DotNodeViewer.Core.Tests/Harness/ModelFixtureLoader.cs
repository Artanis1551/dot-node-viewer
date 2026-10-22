using System.Security.Cryptography;
using System.Text.Json;

namespace DotNodeViewer.Core.Tests.Harness;

/// <summary>
/// Resolves, downloads, verifies, and opens neural network model fixtures registered in <c>Fixtures/models.manifest.json</c>.
/// </summary>
public static class ModelFixtureLoader
{
    private static readonly HttpClient SharedHttpClient = new();

    /// <summary>
    /// Gets the root directory of the <c>DotNodeViewer.Core.Tests</c> project.
    /// </summary>
    /// <value>The absolute directory path containing <c>Fixtures/models.manifest.json</c>.</value>
    public static string TestProjectDirectory { get; } = LocateTestProjectDirectory();

    /// <summary>
    /// Loads and deserializes the <see cref="ModelManifestDocument"/> from <c>Fixtures/models.manifest.json</c>.
    /// </summary>
    /// <param name="manifestPath">An optional custom path to a manifest file. When <see langword="null"/>, uses the default project manifest.</param>
    /// <returns>The parsed <see cref="ModelManifestDocument"/>.</returns>
    public static ModelManifestDocument LoadManifest(string? manifestPath = null)
    {
        string path = manifestPath ?? Path.Combine(TestProjectDirectory, "Fixtures", "models.manifest.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Model manifest file was not found at '{path}'.", path);
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ModelManifestDocument? doc = JsonSerializer.Deserialize(stream, HarnessJsonContext.Default.ModelManifestDocument);
        return doc ?? throw new InvalidOperationException($"Failed to deserialize model manifest at '{path}'.");
    }

    /// <summary>
    /// Loads the golden <see cref="ModelTopologySnapshot"/> associated with <paramref name="entry"/>.
    /// </summary>
    /// <param name="entry">The manifest entry whose <c>expectedSnapshot</c> file will be loaded.</param>
    /// <param name="baseDirectory">An optional base directory for resolving relative paths. Defaults to <see cref="TestProjectDirectory"/>.</param>
    /// <returns>The deserialized <see cref="ModelTopologySnapshot"/>.</returns>
    public static ModelTopologySnapshot LoadExpectedSnapshot(ModelManifestEntry entry, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string root = baseDirectory ?? TestProjectDirectory;
        string fullPath = Path.Combine(root, NormalizeRelativePath(entry.ExpectedSnapshot));
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Expected topology snapshot file was not found at '{fullPath}'.", fullPath);
        }

        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize(stream, HarnessJsonContext.Default.ModelTopologySnapshot)
            ?? throw new InvalidOperationException($"Failed to deserialize expected snapshot at '{fullPath}'.");
    }

    /// <summary>
    /// Opens a read-only, seekable <see cref="FileStream"/> for the specified <paramref name="entry"/>, downloading it into <c>.model-cache/</c>
    /// if necessary and verifying both its byte length and SHA-256 integrity digest.
    /// </summary>
    /// <param name="entry">The manifest entry to open.</param>
    /// <param name="baseDirectory">An optional base directory for resolving relative paths. Defaults to <see cref="TestProjectDirectory"/>.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation and returns an open <see cref="FileStream"/> positioned at byte 0,
    /// or <see langword="null"/> if the model is a local-only cached entry without a <c>sourceUrl</c> and is not present on disk.
    /// </returns>
    public static async Task<FileStream?> OpenVerifiedModelStreamAsync(
        ModelManifestEntry entry,
        string? baseDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string root = baseDirectory ?? TestProjectDirectory;
        string fullPath = Path.Combine(root, NormalizeRelativePath(entry.RelativePath));

        if (!File.Exists(fullPath))
        {
            if (string.IsNullOrWhiteSpace(entry.SourceUrl))
            {
                return null;
            }

            await DownloadAndStageCachedModelAsync(entry, fullPath, cancellationToken).ConfigureAwait(false);
        }

        FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != entry.ByteLength)
            {
                throw new InvalidDataException(
                    $"Model '{entry.Id}' at '{fullPath}' has byte length {stream.Length}, expected {entry.ByteLength}.");
            }

            string actualSha256 = await ComputeSha256LowerHexAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Model '{entry.Id}' at '{fullPath}' failed SHA-256 verification. Expected '{entry.Sha256}', actual '{actualSha256}'.");
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DownloadAndStageCachedModelAsync(
        ModelManifestEntry entry,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using HttpResponseMessage response = await SharedHttpClient
                .GetAsync(new Uri(entry.SourceUrl!), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (FileStream tempStream = new(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                await response.Content.CopyToAsync(tempStream, cancellationToken).ConfigureAwait(false);
                await tempStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                if (tempStream.Length != entry.ByteLength)
                {
                    throw new InvalidDataException(
                        $"Downloaded model '{entry.Id}' from '{entry.SourceUrl}' has byte length {tempStream.Length}, expected {entry.ByteLength}.");
                }

                tempStream.Position = 0;
                string actualSha256 = await ComputeSha256LowerHexAsync(tempStream, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Downloaded model '{entry.Id}' from '{entry.SourceUrl}' failed SHA-256 verification. Expected '{entry.Sha256}', actual '{actualSha256}'.");
                }
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static async Task<string> ComputeSha256LowerHexAsync(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static string LocateTestProjectDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "Fixtures", "models.manifest.json");
            if (File.Exists(candidate))
            {
                return current.FullName;
            }

            string repoCandidate = Path.Combine(
                current.FullName,
                "tests",
                "DotNodeViewer.Core.Tests",
                "Fixtures",
                "models.manifest.json");
            if (File.Exists(repoCandidate))
            {
                return Path.Combine(current.FullName, "tests", "DotNodeViewer.Core.Tests");
            }

            current = current.Parent;
        }

        return AppContext.BaseDirectory;
    }
}

