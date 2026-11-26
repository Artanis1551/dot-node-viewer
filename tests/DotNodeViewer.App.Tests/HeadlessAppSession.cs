using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;

[assembly: AvaloniaTestApplication(typeof(DotNodeViewer.App.Tests.TestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace DotNodeViewer.App.Tests;

/// <summary>
/// Configures the headless Avalonia application builder for unit and render test sessions.
/// </summary>
public static class TestAppBuilder
{
    /// <summary>
    /// Builds the configured Avalonia application with headless platform extensions.
    /// </summary>
    /// <returns>The configured <see cref="AppBuilder"/> instance.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = true
            });
}

/// <summary>
/// Manages the headless Avalonia unit test session and dispatches test actions onto its pumped UI thread.
/// </summary>
public static class HeadlessAppSession
{
    private static HeadlessUnitTestSession? _session;
    private static readonly object Gate = new();

    /// <summary>
    /// Initializes the shared headless Avalonia application and <see cref="FontManager"/> once
    /// before any parallel tests in this assembly run.
    /// </summary>
    [Before(Assembly)]
    public static Task InitializeAssemblyAsync() =>
        Dispatch(() =>
        {
            _ = FontManager.Current;
        });

    /// <summary>
    /// Disposes the shared headless Avalonia unit test session after all tests in this assembly complete.
    /// </summary>
    [After(Assembly)]
    public static void ShutdownAssembly()
    {
        HeadlessUnitTestSession? session;
        lock (Gate)
        {
            session = _session;
            _session = null;
        }

        session?.Dispose();
    }

    /// <summary>
    /// Ensures that the headless test session is initialized.
    /// </summary>
    public static void EnsureInitialized() => _ = Session;

    /// <summary>
    /// Gets the shared <see cref="HeadlessUnitTestSession"/> for this test assembly.
    /// </summary>
    public static HeadlessUnitTestSession Session
    {
        get
        {
            lock (Gate)
            {
                return _session ??= HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessAppSession).Assembly);
            }
        }
    }

    /// <summary>
    /// Dispatches an action to the headless UI thread.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    public static Task Dispatch(Action action) => Session.Dispatch(action, CancellationToken.None);

    /// <summary>
    /// Dispatches an asynchronous action to the headless UI thread and pumps the dispatcher frame until completion.
    /// </summary>
    /// <param name="action">The asynchronous action to execute.</param>
    public static Task DispatchAsync(Func<Task> action) =>
        Session.Dispatch(
            async () =>
            {
                await action().ConfigureAwait(true);
                return 0;
            },
            CancellationToken.None);
}
