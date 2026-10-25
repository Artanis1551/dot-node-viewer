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
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Layout;

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
                string? scenario = args.FirstOrDefault(a => a.StartsWith("--test-scenario=", StringComparison.Ordinal)) is { } sc
                    ? sc["--test-scenario=".Length..]
                    : null;
                string? initialPath = args.FirstOrDefault(a => !a.StartsWith('-'));

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
                else if (!string.IsNullOrWhiteSpace(initialPath))
                {
                    window.Opened += async (_, _) =>
                    {
                        await viewModel.LoadModelAsync(initialPath).ConfigureAwait(true);

                        if (!string.IsNullOrEmpty(scenario))
                        {
                            switch (scenario)
                            {
                                case "drilldown":
                                    var ifNode = viewModel.CurrentLayout?.Nodes.FirstOrDefault(n => n.Title.Contains("If", StringComparison.OrdinalIgnoreCase));
                                    if (ifNode is not null)
                                    {
                                        viewModel.SelectedNode = ifNode;
                                        var branchAttr = viewModel.SelectedNodeAttributes.FirstOrDefault(a => a.HasSubgraph);
                                        if (branchAttr?.Subgraph is not null)
                                        {
                                            viewModel.DrillDownSubgraph(branchAttr.Subgraph);
                                        }
                                    }
                                    break;

                                case "weights":
                                    var nodeWithWeights = viewModel.CurrentLayout?.Nodes.FirstOrDefault(n => n.Kind == VisualNodeKind.Operator && n.CollapsedParameters.Count > 0)
                                        ?? viewModel.CurrentLayout?.Nodes.FirstOrDefault(n => n.Kind == VisualNodeKind.Operator);
                                    if (nodeWithWeights is not null)
                                    {
                                        viewModel.SelectedNode = nodeWithWeights;
                                        TensorEdge? tensor = nodeWithWeights.CollapsedParameters.Count > 0
                                            ? nodeWithWeights.CollapsedParameters[0].Tensor
                                            : viewModel.SelectedNodeInputs.FirstOrDefault(i => i.CanInspectWeights)?.Tensor;
                                        if (tensor is not null)
                                        {
                                            await viewModel.InspectTensorAsync(tensor).ConfigureAwait(true);
                                        }
                                    }
                                    break;

                                case "search":
                                    viewModel.IsSearchOpen = true;
                                    viewModel.SearchPanel.Query = "conv";
                                    if (viewModel.CurrentLayout is not null)
                                    {
                                        viewModel.SearchPanel.PerformSearch(viewModel.CurrentLayout);
                                    }
                                    break;

                                case "neighborhood":
                                    var intermediate = viewModel.CurrentLayout?.Nodes.FirstOrDefault(n => n.Kind == VisualNodeKind.Operator && viewModel.CurrentLayout.GetPredecessors(n.Id).Count > 0 && viewModel.CurrentLayout.GetSuccessors(n.Id).Count > 0)
                                        ?? viewModel.CurrentLayout?.Nodes.FirstOrDefault(n => n.Kind == VisualNodeKind.Operator);
                                    if (intermediate is not null)
                                    {
                                        viewModel.SelectedNode = intermediate;
                                    }
                                    break;

                                case "light":
                                    viewModel.ToggleTheme();
                                    break;
                            }
                        }
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
