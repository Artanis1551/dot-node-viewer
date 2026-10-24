using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DotNodeViewer.App.IO;
using DotNodeViewer.App.Platform.Linux;
using DotNodeViewer.App.ViewModels;
using DotNodeViewer.App.Views;

namespace DotNodeViewer.App;

/// <summary>
/// Provides application startup, styling composition, and desktop/browser window lifetime initialization.
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

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                window.Opened += (_, _) =>
                {
                    IDisposable? bridge = LinuxXdndDropBridge.TryAttach(window, viewModel);
                    if (bridge is not null)
                    {
                        window.Closed += (_, _) => bridge.Dispose();
                    }
                };
            }

            if (desktop.Args is { Length: > 0 } args)
            {
                if (args.Contains("--test-drop-overlay=valid"))
                {
                    window.Opened += (_, _) =>
                    {
                        viewModel.SetDropTargetState(isActive: true, isValid: true);
                    };
                }
                else if (args.Contains("--test-drop-overlay=invalid"))
                {
                    window.Opened += (_, _) =>
                    {
                        viewModel.SetDropTargetState(isActive: true, isValid: false);
                    };
                }
                else if (!string.IsNullOrWhiteSpace(args[0]) && !args[0].StartsWith('-'))
                {
                    string initialPath = args[0];
                    window.Opened += async (_, _) =>
                    {
                        await viewModel.LoadModelAsync(initialPath).ConfigureAwait(true);
                    };
                }
            }
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            MainWindowViewModel viewModel = new();
            MainView view = new()
            {
                DataContext = viewModel
            };

            view.Loaded += (_, _) =>
            {
                TopLevel? topLevel = TopLevel.GetTopLevel(view);
                if (topLevel is not null)
                {
                    viewModel.FilePickerAsync = async () =>
                    {
                        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                        {
                            Title = "Open Neural Network Model",
                            AllowMultiple = false,
                            FileTypeFilter =
                            [
                                new FilePickerFileType("All Supported Models (*.onnx, *.tflite, *.pte, *.pt, *.pt2, *.keras, *.h5)")
                                {
                                    Patterns = ["*.onnx", "*.tflite", "*.pte", "*.pt", "*.pt2", "*.keras", "*.h5"]
                                },
                                FilePickerFileTypes.All
                            ]
                        }).ConfigureAwait(true);

                        if (files is { Count: > 0 })
                        {
                            await using Stream source = await files[0].OpenReadAsync().ConfigureAwait(true);
                            MemoryStream seekable = await BrowserStreamAdapter.CreateSeekableStreamAsync(
                                source,
                                files[0].Name).ConfigureAwait(true);

                            byte[] buffer = seekable.ToArray();
                            await viewModel.LoadModelStreamAsync(
                                seekable,
                                files[0].Name,
                                streamFactory: BrowserStreamAdapter.CreateStreamFactory(buffer)).ConfigureAwait(true);
                        }

                        return null;
                    };
                }
            };

            singleView.MainView = view;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
