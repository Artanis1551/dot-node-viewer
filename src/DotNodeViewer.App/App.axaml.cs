using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DotNodeViewer.App.ViewModels;
using DotNodeViewer.App.Views;

namespace DotNodeViewer.App;

/// <summary>
/// Provides application startup, styling composition, and desktop window lifetime initialization.
/// </summary>
public sealed class App : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindowViewModel viewModel = new();
            MainWindow window = new()
            {
                DataContext = viewModel
            };

            desktop.MainWindow = window;

            if (desktop.Args is { Length: > 0 } args && !string.IsNullOrWhiteSpace(args[0]))
            {
                string initialPath = args[0];
                window.Opened += async (_, _) =>
                {
                    await viewModel.LoadModelAsync(initialPath).ConfigureAwait(true);
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
