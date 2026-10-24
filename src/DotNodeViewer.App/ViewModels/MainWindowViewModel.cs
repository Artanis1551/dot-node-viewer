using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotNodeViewer.App.IO;
using DotNodeViewer.App.Rendering;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Layout;

namespace DotNodeViewer.App.ViewModels;

/// <summary>
/// Primary ViewModel driving the main shell window, file opening, background layout pipeline,
/// on-demand tensor inspection, search, subgraph breadcrumbs, and diagnostics.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private CancellationTokenSource? _loadingCts;

    [ObservableProperty]
    private string _title = "dot-node-viewer";

    [ObservableProperty]
    private string? _modelPath;

    [ObservableProperty]
    private string? _modelName;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _loadingStatus = string.Empty;

    [ObservableProperty]
    private bool _hasModel;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DropTargetHeadline))]
    private bool _isDropTargetActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DropTargetHeadline))]
    private bool _isDropTargetValid = true;

    [ObservableProperty]
    private string _dropTargetFeedbackMessage = "Drop model file to visualize";

    /// <summary>
    /// Gets the headline text displayed on the drag-and-drop visual overlay.
    /// </summary>
    public string DropTargetHeadline => IsDropTargetValid ? "Drop Model to Visualize" : "Unsupported Model Format";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorOffsetHex;

    [ObservableProperty]
    private string? _errorContext;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private double _layoutDurationMs;

    [ObservableProperty]
    private ModelGraph? _currentModel;

    [ObservableProperty]
    private VisualSubgraphLayout? _currentLayout;

    [ObservableProperty]
    private Subgraph? _selectedSubgraph;

    [ObservableProperty]
    private VisualNode? _selectedNode;

    [ObservableProperty]
    private bool _hasSelectedNode;

    [ObservableProperty]
    private string _selectedNodeTitle = string.Empty;

    [ObservableProperty]
    private string? _selectedNodeSubtitle;

    [ObservableProperty]
    private string _selectedNodeCategoryName = string.Empty;

    [ObservableProperty]
    private string _selectedNodeCategoryBrushKey = "OpCategoryUnknownBrush";

    [ObservableProperty]
    private string _selectedNodeCategorySubtleBrushKey = "OpCategoryUnknownSubtleBrush";

    [ObservableProperty]
    private string? _selectedNodeDomain;

    [ObservableProperty]
    private string? _selectedNodeVersion;

    [ObservableProperty]
    private string? _selectedNodeDocSummary;

    [ObservableProperty]
    private bool _isInferredNpuSubgraph;

    [ObservableProperty]
    private bool _canNavigateBackSubgraph;

    [ObservableProperty]
    private bool _canExpandSelectedDelegate;

    [ObservableProperty]
    private string? _selectedDelegateBackend;

    [ObservableProperty]
    private bool _hasNpuDisassembly;

    [ObservableProperty]
    private string _npuDisassemblyText = string.Empty;

    [ObservableProperty]
    private string _formatDisplayName = string.Empty;

    [ObservableProperty]
    private string _producerName = string.Empty;

    [ObservableProperty]
    private int _totalNodeCount;

    [ObservableProperty]
    private int _totalEdgeCount;

    [ObservableProperty]
    private int _totalTensorCount;

    [ObservableProperty]
    private int _quantizedTensorCount;

    [ObservableProperty]
    private long _totalParameters;

    [ObservableProperty]
    private string _totalParametersFormatted = "0 params";

    [ObservableProperty]
    private long _totalWeightBytes;

    [ObservableProperty]
    private string _totalWeightBytesFormatted = "0 bytes";

    [ObservableProperty]
    private string _precisionMixText = "FP32";

    [ObservableProperty]
    private string _precisionBadgeBrushKey = "QuantFp32BackgroundBrush";

    [ObservableProperty]
    private string? _delegateBlocksText;

    [ObservableProperty]
    private bool _hasDelegateBlocks;

    [ObservableProperty]
    private bool _hasDiagnostics;

    [ObservableProperty]
    private int _diagnosticsCount;

    [ObservableProperty]
    private bool _isDiagnosticsDrawerOpen;

    [ObservableProperty]
    private TensorInspectorViewModel? _tensorInspector;

    [ObservableProperty]
    private bool _isTensorInspectorOpen;

    [ObservableProperty]
    private bool _isSearchOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeDisplayName))]
    private bool _isDarkTheme = true;

    /// <summary>
    /// Gets the user-facing theme display name (e.g. <c>"Theme: Dark"</c> or <c>"Theme: Light"</c>).
    /// </summary>
    public string ThemeDisplayName => IsDarkTheme ? "Theme: Dark" : "Theme: Light";

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindowViewModel"/> class.
    /// </summary>
    public MainWindowViewModel()
    {
        SearchPanel.NavigateToNodeRequested = node => NavigateToNode(node);
        TrySeedRecentFiles();
    }

    /// <summary>
    /// Gets the fast categorized search ViewModel.
    /// </summary>
    public SearchPanelViewModel SearchPanel { get; } = new();

    /// <summary>
    /// Gets the breadcrumb hierarchy for subgraph drill-down navigation.
    /// </summary>
    public ObservableCollection<SubgraphBreadcrumbItem> Breadcrumbs { get; } = new();

    /// <summary>
    /// Gets the diagnostic messages emitted during model parsing.
    /// </summary>
    public ObservableCollection<ParseDiagnostic> DiagnosticsList { get; } = new();

    /// <summary>
    /// Gets the collection of recently opened model paths.
    /// </summary>
    public ObservableCollection<string> RecentFiles { get; } = new();

    /// <summary>
    /// Gets the subgraphs available in the loaded model.
    /// </summary>
    public ObservableCollection<Subgraph> Subgraphs { get; } = new();

    /// <summary>
    /// Gets the display items for the current subgraph's inputs.
    /// </summary>
    public ObservableCollection<TensorSummaryItem> Inputs { get; } = new();

    /// <summary>
    /// Gets the display items for the current subgraph's outputs.
    /// </summary>
    public ObservableCollection<TensorSummaryItem> Outputs { get; } = new();

    /// <summary>
    /// Gets the aggregated operator category statistics for the current subgraph.
    /// </summary>
    public ObservableCollection<OperatorCategorySummaryItem> OperatorCategories { get; } = new();

    /// <summary>
    /// Gets the typed attribute items for the currently selected node in the Node Inspector.
    /// </summary>
    public ObservableCollection<NodeAttributeItem> SelectedNodeAttributes { get; } = new();

    /// <summary>
    /// Gets the input connection items for the currently selected node in the Node Inspector.
    /// </summary>
    public ObservableCollection<NodePortItem> SelectedNodeInputs { get; } = new();

    /// <summary>
    /// Gets the output connection items for the currently selected node in the Node Inspector.
    /// </summary>
    public ObservableCollection<NodePortItem> SelectedNodeOutputs { get; } = new();

    /// <summary>
    /// Gets or sets an optional file-picker delegate used by <see cref="OpenModelAsync"/> in desktop environments.
    /// </summary>
    public Func<Task<string?>>? FilePickerAsync { get; set; }

    /// <summary>
    /// Gets or sets an optional factory used to open readable and seekable streams over the active model.
    /// </summary>
    public Func<Stream>? ModelStreamFactory { get; set; }

    /// <summary>
    /// Action invoked when a node should be centered and focused on the graph canvas.
    /// </summary>
    public Action<VisualNode>? RequestCenterOnNode { get; set; }

    partial void OnSelectedNodeChanged(VisualNode? value)
    {
        UpdateSelectedNodeDetails(value);
    }

    /// <summary>
    /// Clears the current node selection, returning the properties sidebar to the Graph Overview state.
    /// </summary>
    [RelayCommand]
    public void DeselectNode()
    {
        SelectedNode = null;
    }

    /// <summary>
    /// Prompts the user to pick a model file using <see cref="FilePickerAsync"/> and loads it.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand]
    public async Task OpenModelAsync(CancellationToken cancellationToken = default)
    {
        if (FilePickerAsync is null)
        {
            return;
        }

        string? selectedPath = await FilePickerAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            await LoadModelAsync(selectedPath, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Supported neural network model file extensions recognized by <c>dot-node-viewer</c>.
    /// </summary>
    public static readonly string[] SupportedModelExtensions =
    [
        ".onnx",
        ".tflite",
        ".pte",
        ".pt",
        ".pt2",
        ".keras",
        ".h5"
    ];

    /// <summary>
    /// Evaluates whether any candidate file names in the specified list have a supported model format extension.
    /// </summary>
    /// <param name="fileNames">The candidate file names or paths.</param>
    /// <returns><see langword="true"/> if at least one file is supported; otherwise, <see langword="false"/>.</returns>
    public static bool CanAcceptFiles(IReadOnlyList<string>? fileNames)
    {
        if (fileNames is null || fileNames.Count == 0)
        {
            return false;
        }

        foreach (string name in fileNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string extension = Path.GetExtension(name);
            if (IsSupportedModelExtension(extension))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether the specified file extension corresponds to a supported neural network model format.
    /// </summary>
    /// <param name="extension">The file extension, including leading dot.</param>
    /// <returns><see langword="true"/> if supported; otherwise, <see langword="false"/>.</returns>
    public static bool IsSupportedModelExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        foreach (string ext in SupportedModelExtensions)
        {
            if (string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (string.Equals(".pth", extension, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Updates the drag-and-drop visual target state.
    /// </summary>
    /// <param name="isActive">Whether the drop target overlay should be active.</param>
    /// <param name="isValid">Whether the candidate item format is supported.</param>
    /// <param name="feedbackMessage">Optional custom feedback message.</param>
    public void SetDropTargetState(bool isActive, bool isValid = true, string? feedbackMessage = null)
    {
        IsDropTargetActive = isActive;
        IsDropTargetValid = isValid;
        DropTargetFeedbackMessage = feedbackMessage ?? (isValid
            ? "Release file to inspect neural network architecture"
            : "Unsupported format. Supported: .onnx, .tflite, .pte, .pt, .pt2, .keras, .h5");
    }

    /// <summary>
    /// Identifies the primary neural network model file from a collection of dropped storage items,
    /// filtering out companion data sidecars (such as <c>.onnx.data</c>).
    /// </summary>
    /// <param name="items">The dropped storage items.</param>
    /// <returns>The primary model <see cref="IStorageItem"/>, or <see langword="null"/> if none found.</returns>
    public static IStorageItem? FindPrimaryModelItem(IReadOnlyList<IStorageItem>? items)
    {
        if (items is null || items.Count == 0)
        {
            return null;
        }

        // First pass: prioritized primary model file formats (e.g. .onnx before .onnx.data)
        foreach (IStorageItem item in items)
        {
            string ext = Path.GetExtension(item.Name);
            if (string.Equals(ext, ".onnx", StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        // Other supported primary extensions
        foreach (IStorageItem item in items)
        {
            string ext = Path.GetExtension(item.Name);
            if (IsSupportedModelExtension(ext))
            {
                return item;
            }
        }

        // Folder item if no model file directly matched
        foreach (IStorageItem item in items)
        {
            if (item is IStorageFolder)
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>
    /// Identifies the primary neural network model file from a collection of dropped file paths,
    /// filtering out companion data sidecars (such as <c>.onnx.data</c>).
    /// </summary>
    /// <param name="paths">The dropped file paths.</param>
    /// <returns>The primary model path, or <see langword="null"/> if none found.</returns>
    public static string? FindPrimaryModelPath(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return null;
        }

        // First pass: prioritized primary model file formats (.onnx before companion .onnx.data)
        foreach (string path in paths)
        {
            if (path.EndsWith(".onnx.data", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string ext = Path.GetExtension(path);
            if (string.Equals(ext, ".onnx", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        // Other supported primary extensions
        foreach (string path in paths)
        {
            if (path.EndsWith(".onnx.data", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string ext = Path.GetExtension(path);
            if (IsSupportedModelExtension(ext))
            {
                return path;
            }
        }

        // Folder item if no model file directly matched
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Processes dropped storage items, resolving primary model files, directories, or WebAssembly streams.
    /// </summary>
    /// <param name="items">The collection of dropped storage items.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task HandleDroppedFilesAsync(IReadOnlyList<IStorageItem>? items, CancellationToken cancellationToken = default)
    {
        if (items is null || items.Count == 0)
        {
            return;
        }

        IStorageItem? primary = FindPrimaryModelItem(items);
        if (primary is null)
        {
            HasError = true;
            ErrorMessage = "Unsupported file format. Supported: .onnx, .tflite, .pte, .pt, .pt2, .keras, .h5";
            StatusText = ErrorMessage;
            return;
        }

        // Handle directory drop
        if (primary is IStorageFolder folder)
        {
            string? localFolderPath = folder.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localFolderPath) && Directory.Exists(localFolderPath))
            {
                string? candidate = Directory.EnumerateFiles(localFolderPath, "*.*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => IsSupportedModelExtension(Path.GetExtension(f)));

                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    await LoadModelAsync(candidate, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                await foreach (IStorageItem child in folder.GetItemsAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (child is IStorageFile file && IsSupportedModelExtension(Path.GetExtension(child.Name)))
                    {
                        await using Stream source = await file.OpenReadAsync().ConfigureAwait(false);
                        MemoryStream seekable = await BrowserStreamAdapter.CreateSeekableStreamAsync(
                            source,
                            file.Name,
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        byte[] buffer = seekable.ToArray();
                        await LoadModelStreamAsync(
                            seekable,
                            file.Name,
                            streamFactory: BrowserStreamAdapter.CreateStreamFactory(buffer),
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
            }

            HasError = true;
            ErrorMessage = $"No supported neural network model found in folder '{folder.Name}'.";
            StatusText = ErrorMessage;
            return;
        }

        // Desktop path available
        string? localPath = primary.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            await LoadModelAsync(localPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        // WebAssembly / Browser stream fallback
        if (primary is IStorageFile storageFile)
        {
            await using Stream source = await storageFile.OpenReadAsync().ConfigureAwait(false);
            MemoryStream seekable = await BrowserStreamAdapter.CreateSeekableStreamAsync(
                source,
                storageFile.Name,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            byte[] buffer = seekable.ToArray();
            await LoadModelStreamAsync(
                seekable,
                storageFile.Name,
                streamFactory: BrowserStreamAdapter.CreateStreamFactory(buffer),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Processes dropped file system paths from desktop platform drop bridges or legacy drag payloads.
    /// </summary>
    /// <param name="filePaths">The collection of dropped file system paths.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task HandleDroppedFilesAsync(IReadOnlyList<string>? filePaths, CancellationToken cancellationToken = default)
    {
        if (filePaths is null || filePaths.Count == 0)
        {
            return;
        }

        string? primary = FindPrimaryModelPath(filePaths);
        if (primary is null)
        {
            HasError = true;
            ErrorMessage = "Unsupported file format. Supported: .onnx, .tflite, .pte, .pt, .pt2, .keras, .h5";
            StatusText = ErrorMessage;
            return;
        }

        // Handle directory drop
        if (Directory.Exists(primary))
        {
            string? candidate = Directory.EnumerateFiles(primary, "*.*", SearchOption.AllDirectories)
                .FirstOrDefault(f => IsSupportedModelExtension(Path.GetExtension(f)));

            if (!string.IsNullOrWhiteSpace(candidate))
            {
                await LoadModelAsync(candidate, cancellationToken).ConfigureAwait(false);
                return;
            }

            HasError = true;
            ErrorMessage = $"No supported neural network model found in folder '{Path.GetFileName(primary)}'.";
            StatusText = ErrorMessage;
            return;
        }

        await LoadModelAsync(primary, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a readable and seekable stream for the active model.
    /// </summary>
    /// <returns>A readable <see cref="Stream"/>, or <see langword="null"/> if not available.</returns>
    public Stream? OpenModelStream()
    {
        if (ModelStreamFactory is not null)
        {
            return ModelStreamFactory();
        }

        if (!string.IsNullOrWhiteSpace(ModelPath) && File.Exists(ModelPath))
        {
            return new FileStream(ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        return null;
    }

    /// <summary>
    /// Loads a neural network model from disk, extracts its Unified Graph IR, and computes its layered layout.
    /// </summary>
    /// <param name="path">The absolute or relative file path to load.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand]
    public async Task LoadModelAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            HasModel = false;
            HasError = true;
            ErrorMessage = $"Model file was not found: '{path}'";
            StatusText = ErrorMessage;
            return;
        }

        ModelPath = path;
        string modelName = Path.GetFileName(path);

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        await LoadModelStreamCoreAsync(
            stream,
            modelName,
            streamFactory: () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
            associatedPath: path,
            cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Loads a neural network model from a seekable stream, extracts its Unified Graph IR, and computes its layered layout.
    /// </summary>
    /// <param name="stream">The seekable stream containing model data.</param>
    /// <param name="modelName">The display name or file name of the model.</param>
    /// <param name="streamFactory">An optional factory to produce seekable streams for on-demand weight inspection.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task LoadModelStreamAsync(
        Stream stream,
        string modelName,
        Func<Stream>? streamFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ModelPath = null;
        await LoadModelStreamCoreAsync(
            stream,
            modelName,
            streamFactory: streamFactory,
            associatedPath: null,
            cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    private async Task LoadModelStreamCoreAsync(
        Stream stream,
        string modelName,
        Func<Stream>? streamFactory,
        string? associatedPath,
        CancellationToken cancellationToken)
    {
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken ct = _loadingCts.Token;

        IsLoading = true;
        HasError = false;
        ErrorMessage = null;
        ErrorOffsetHex = null;
        ErrorContext = null;
        SelectedNode = null;
        IsTensorInspectorOpen = false;
        TensorInspector = null;
        IsDiagnosticsDrawerOpen = false;
        LoadingStatus = "Reading model stream...";
        StatusText = $"Opening {modelName}...";
        ModelStreamFactory = streamFactory;

        try
        {
            ModelGraph model;
            VisualSubgraphLayout layout;
            Stopwatch sw = Stopwatch.StartNew();

            LoadingStatus = "Parsing model graph...";
            model = await ModelFormatRegistry.Default.ReadAsync(
                stream,
                new ModelReadOptions { FileName = modelName },
                ct).ConfigureAwait(true);

            LoadingStatus = "Computing layered layout...";
            ModelGraphLayoutAdapter layoutAdapter = new(textMeasurer: AvaloniaTextMeasurer.Default);
            layout = await Task.Run(() => layoutAdapter.CreateLayout(model, options: null, ct), ct).ConfigureAwait(true);

            sw.Stop();
            LayoutDurationMs = sw.Elapsed.TotalMilliseconds;

            CurrentModel = model;
            CurrentLayout = layout;
            ModelName = modelName;
            Title = $"{ModelName} — dot-node-viewer";

            FormatDisplayName = FormatModelFormatInfo(model.Format);
            ProducerName = string.IsNullOrWhiteSpace(model.Producer) ? "Unknown Producer" : model.Producer;

            TotalNodeCount = model.Subgraphs.Sum(s => s.Nodes.Count);
            TotalEdgeCount = layout.Edges.Count;

            // Architecture and conversion quick summary
            PopulateQuickSummary(model);

            // Diagnostics
            DiagnosticsList.Clear();
            foreach (ParseDiagnostic d in model.Diagnostics)
            {
                DiagnosticsList.Add(d);
            }
            DiagnosticsCount = DiagnosticsList.Count;
            HasDiagnostics = DiagnosticsCount > 0;

            // Subgraphs
            Subgraphs.Clear();
            foreach (Subgraph sg in model.Subgraphs)
            {
                Subgraphs.Add(sg);
            }

            // Breadcrumbs
            Breadcrumbs.Clear();
            CanNavigateBackSubgraph = false;
            IsInferredNpuSubgraph = false;
            SelectedSubgraph = model.PrimarySubgraph;
            if (SelectedSubgraph is not null)
            {
                Breadcrumbs.Add(new SubgraphBreadcrumbItem($"Root: {SelectedSubgraph.Name}", SelectedSubgraph, isCurrent: true));
                PopulateSubgraphSummaries(SelectedSubgraph);
            }

            // Update recent files if associated path is available
            if (!string.IsNullOrWhiteSpace(associatedPath))
            {
                UpdateRecentFiles(associatedPath);
            }

            HasModel = true;
            StatusText = $"Loaded {ModelName} in {sw.ElapsedMilliseconds}ms ({TotalNodeCount} nodes, {TotalEdgeCount} edges)";

            if (SearchPanel.IsOpen)
            {
                SearchPanel.PerformSearch(CurrentLayout);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Loading canceled.";
        }
        catch (ModelParseException ex)
        {
            HasModel = false;
            HasError = true;
            ErrorMessage = ex.Message;
            ErrorOffsetHex = ex.FileOffset.HasValue ? string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", ex.FileOffset.Value) : null;
            ErrorContext = ex.Context;
            StatusText = $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            HasModel = false;
            HasError = true;
            ErrorMessage = ex.Message;
            ErrorOffsetHex = null;
            ErrorContext = null;
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            LoadingStatus = string.Empty;
        }
    }

    /// <summary>
    /// Toggles the active theme variant between Dark and Light.
    /// </summary>
    [RelayCommand]
    public void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        if (Application.Current is not null)
        {
            ThemeVariant target = IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
            if (Dispatcher.UIThread.CheckAccess())
            {
                Application.Current.RequestedThemeVariant = target;
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Application.Current.RequestedThemeVariant = target;
                });
            }
        }
    }

    /// <summary>
    /// Toggles the visibility of the fast search panel.
    /// </summary>
    [RelayCommand]
    public void ToggleSearch()
    {
        IsSearchOpen = !IsSearchOpen;
        if (IsSearchOpen && CurrentLayout is not null)
        {
            SearchPanel.PerformSearch(CurrentLayout);
        }
        else if (!IsSearchOpen)
        {
            SearchPanel.ClearSearch();
        }
    }

    /// <summary>
    /// Closes the fast search panel.
    /// </summary>
    [RelayCommand]
    public void CloseSearch()
    {
        IsSearchOpen = false;
        SearchPanel.ClearSearch();
    }

    /// <summary>
    /// Toggles the diagnostics drawer.
    /// </summary>
    [RelayCommand]
    public void ToggleDiagnosticsDrawer()
    {
        IsDiagnosticsDrawerOpen = !IsDiagnosticsDrawerOpen;
    }

    /// <summary>
    /// Opens and loads the on-demand weight inspector for the specified tensor.
    /// </summary>
    /// <param name="tensor">The target tensor edge to inspect.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand]
    public async Task InspectTensorAsync(TensorEdge? tensor)
    {
        if (tensor is null)
        {
            return;
        }

        TensorInspectorViewModel inspector = new();
        TensorInspector = inspector;
        IsTensorInspectorOpen = true;

        Stream? stream = OpenModelStream();
        try
        {
            await inspector.InspectAsync(tensor, stream).ConfigureAwait(true);
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// Closes the on-demand weight inspector.
    /// </summary>
    [RelayCommand]
    public void CloseTensorInspector()
    {
        IsTensorInspectorOpen = false;
        TensorInspector = null;
    }

    /// <summary>
    /// Selects and centers the specified node in the canvas.
    /// </summary>
    /// <param name="node">The visual node to navigate to.</param>
    [RelayCommand]
    public void NavigateToNode(VisualNode? node)
    {
        if (node is null)
        {
            return;
        }

        SelectedNode = node;
        RequestCenterOnNode?.Invoke(node);
    }

    /// <summary>
    /// Asynchronously unwraps the selected hardware accelerator delegate node into a synthesized subgraph and navigates into it.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand]
    public async Task ExpandSelectedDelegateAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedNode?.GraphNode is not GraphNode gn || CurrentModel is null)
        {
            return;
        }

        IDelegateSubgraphExpander? expander = DelegateSubgraphExpanderRegistry.Default.FindExpander(CurrentModel, gn);
        if (expander is null)
        {
            return;
        }

        Stream? stream = OpenModelStream();
        if (stream is null)
        {
            return;
        }

        try
        {
            Subgraph? expanded = await expander.ExpandAsync(stream, CurrentModel, gn, cancellationToken).ConfigureAwait(true);
            if (expanded is not null)
            {
                DrillDownSubgraph(expanded);
            }
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drills down into a nested child subgraph (e.g. from an If or Loop branch attribute).
    /// </summary>
    /// <param name="subgraph">The child subgraph to inspect.</param>
    [RelayCommand]
    public void DrillDownSubgraph(Subgraph? subgraph)
    {
        if (subgraph is null)
        {
            return;
        }

        // Mark existing breadcrumbs as not current
        for (int i = 0; i < Breadcrumbs.Count; i++)
        {
            SubgraphBreadcrumbItem existing = Breadcrumbs[i];
            if (existing.IsCurrent)
            {
                Breadcrumbs[i] = new SubgraphBreadcrumbItem(existing.Name, existing.Subgraph, isCurrent: false);
            }
        }

        string prefix = SelectedNodeTitle;
        string title = !string.IsNullOrWhiteSpace(prefix) ? $"{prefix}: {subgraph.Name}" : subgraph.Name;
        Breadcrumbs.Add(new SubgraphBreadcrumbItem(title, subgraph, isCurrent: true));

        SelectSubgraphInternal(subgraph);
    }

    /// <summary>
    /// Navigates back up one level in the subgraph breadcrumb hierarchy.
    /// </summary>
    [RelayCommand]
    public void NavigateBackSubgraph()
    {
        if (Breadcrumbs.Count <= 1)
        {
            return;
        }

        Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
        SubgraphBreadcrumbItem target = Breadcrumbs[^1];
        Breadcrumbs[^1] = new SubgraphBreadcrumbItem(target.Name, target.Subgraph, isCurrent: true);
        SelectSubgraphInternal(target.Subgraph);
    }

    /// <summary>
    /// Selects a specific breadcrumb level, navigating back to that level.
    /// </summary>
    /// <param name="item">The target breadcrumb item.</param>
    [RelayCommand]
    public void SelectBreadcrumb(SubgraphBreadcrumbItem? item)
    {
        if (item is null)
        {
            return;
        }

        int index = Breadcrumbs.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        while (Breadcrumbs.Count > index + 1)
        {
            Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
        }

        Breadcrumbs[index] = new SubgraphBreadcrumbItem(item.Name, item.Subgraph, isCurrent: true);
        SelectSubgraphInternal(item.Subgraph);
    }

    /// <summary>
    /// Dismisses the active error state.
    /// </summary>
    [RelayCommand]
    public void ClearError()
    {
        HasError = false;
        ErrorMessage = null;
        ErrorOffsetHex = null;
        ErrorContext = null;
        StatusText = "Ready";
    }

    /// <summary>
    /// Selects an active subgraph from the root dropdown and updates input, output, and category summaries.
    /// </summary>
    /// <param name="subgraph">The subgraph to inspect.</param>
    [RelayCommand]
    public void SelectSubgraph(Subgraph? subgraph)
    {
        if (subgraph is null)
        {
            return;
        }

        // Reset breadcrumb trail when explicitly switching root subgraph from selector
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new SubgraphBreadcrumbItem($"Root: {subgraph.Name}", subgraph, isCurrent: true));

        SelectSubgraphInternal(subgraph);
    }

    private void SelectSubgraphInternal(Subgraph subgraph)
    {
        SelectedSubgraph = subgraph;
        SelectedNode = null;
        CanNavigateBackSubgraph = Breadcrumbs.Count > 1;
        IsInferredNpuSubgraph = subgraph.Name.Contains("Ethos-U", StringComparison.OrdinalIgnoreCase);
        PopulateSubgraphSummaries(subgraph);

        if (CurrentModel is not null)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                ModelGraphLayoutAdapter layoutAdapter = new(textMeasurer: AvaloniaTextMeasurer.Default);
                CurrentLayout = layoutAdapter.CreateLayout(subgraph);
                sw.Stop();
                LayoutDurationMs = sw.Elapsed.TotalMilliseconds;
                TotalEdgeCount = CurrentLayout.Edges.Count;
                StatusText = $"Subgraph '{subgraph.Name}' laid out in {sw.ElapsedMilliseconds}ms ({subgraph.Nodes.Count} nodes)";

                if (SearchPanel.IsOpen)
                {
                    SearchPanel.PerformSearch(CurrentLayout);
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Layout error: {ex.Message}";
            }
        }
    }

    private void UpdateSelectedNodeDetails(VisualNode? node)
    {
        SelectedNodeAttributes.Clear();
        SelectedNodeInputs.Clear();
        SelectedNodeOutputs.Clear();
        CanExpandSelectedDelegate = false;
        SelectedDelegateBackend = null;
        HasNpuDisassembly = false;
        NpuDisassemblyText = string.Empty;

        if (node is null)
        {
            HasSelectedNode = false;
            SelectedNodeTitle = string.Empty;
            SelectedNodeSubtitle = null;
            SelectedNodeCategoryName = string.Empty;
            SelectedNodeCategoryBrushKey = "OpCategoryUnknownBrush";
            SelectedNodeCategorySubtleBrushKey = "OpCategoryUnknownSubtleBrush";
            SelectedNodeDomain = null;
            SelectedNodeVersion = null;
            SelectedNodeDocSummary = null;
            return;
        }

        HasSelectedNode = true;
        SelectedNodeTitle = node.Title;
        SelectedNodeSubtitle = node.Subtitle;
        SelectedNodeCategoryName = node.Category.ToString();
        SelectedNodeCategoryBrushKey = $"OpCategory{node.Category}Brush";
        SelectedNodeCategorySubtleBrushKey = $"OpCategory{node.Category}SubtleBrush";

        if (node.GraphNode is not null)
        {
            GraphNode gn = node.GraphNode;
            SelectedNodeDomain = gn.Operator.Domain;
            SelectedNodeVersion = gn.Operator.Version.HasValue
                ? gn.Operator.Version.Value.ToString(CultureInfo.InvariantCulture)
                : null;
            SelectedNodeDocSummary = $"{gn.Operator.Name} ({gn.Operator.Category})";

            if (CurrentModel is not null && DelegateSubgraphExpanderRegistry.Default.FindExpander(CurrentModel, gn) is IDelegateSubgraphExpander expander)
            {
                CanExpandSelectedDelegate = true;
                SelectedDelegateBackend = expander.BackendId;
            }

            NodeAttribute? cmdAttr = gn.Attributes.FirstOrDefault(a => string.Equals(a.Name, "commands", StringComparison.OrdinalIgnoreCase));
            if (cmdAttr?.Value is AttributeValue.StringList cmdList && cmdList.Values.Count > 0)
            {
                HasNpuDisassembly = true;
                NpuDisassemblyText = string.Join(Environment.NewLine, cmdList.Values);
            }

            // Attributes
            for (int i = 0; i < gn.Attributes.Count; i++)
            {
                NodeAttribute attr = gn.Attributes[i];
                string typeName = GetAttributeTypeName(attr.Value);
                string valText = FormatAttributeValue(attr.Value);

                Subgraph? nestedGraph = attr.Value is AttributeValue.Graph g ? g.Value : null;
                IReadOnlyList<Subgraph>? nestedGraphList = attr.Value is AttributeValue.GraphList gl ? gl.Values : null;

                SelectedNodeAttributes.Add(new NodeAttributeItem(
                    attr.Name,
                    typeName,
                    valText,
                    subgraph: nestedGraph,
                    subgraphList: nestedGraphList));
            }

            // Inputs
            for (int p = 0; p < gn.Inputs.Count; p++)
            {
                NodePort inPort = gn.Inputs[p];
                for (int t = 0; t < inPort.Tensors.Count; t++)
                {
                    TensorEdge tensor = inPort.Tensors[t];
                    bool isParam = tensor.Kind == TensorKind.Initializer || tensor.Kind == TensorKind.Constant;
                    string kindBadge = isParam ? "Weight / Parameter" : "Runtime Activation";

                    TensorSummaryItem item = CreateTensorSummaryItem(tensor.Name, tensor);
                    SelectedNodeInputs.Add(new NodePortItem(
                        portName: inPort.Name,
                        tensorName: tensor.Name,
                        kindBadge: kindBadge,
                        shapeText: item.ShapeText,
                        dataTypeText: item.DataTypeText,
                        quantizationBadge: item.QuantizationBadge,
                        quantizationBrushKey: item.QuantizationBrushKey,
                        quantizationTextBrushKey: item.QuantizationTextBrushKey,
                        isQuantized: item.IsQuantized,
                        details: item.Details,
                        tensor: tensor));
                }
            }

            // Outputs
            for (int p = 0; p < gn.Outputs.Count; p++)
            {
                NodePort outPort = gn.Outputs[p];
                for (int t = 0; t < outPort.Tensors.Count; t++)
                {
                    TensorEdge tensor = outPort.Tensors[t];
                    TensorSummaryItem item = CreateTensorSummaryItem(tensor.Name, tensor);
                    SelectedNodeOutputs.Add(new NodePortItem(
                        portName: outPort.Name,
                        tensorName: tensor.Name,
                        kindBadge: "Output",
                        shapeText: item.ShapeText,
                        dataTypeText: item.DataTypeText,
                        quantizationBadge: item.QuantizationBadge,
                        quantizationBrushKey: item.QuantizationBrushKey,
                        quantizationTextBrushKey: item.QuantizationTextBrushKey,
                        isQuantized: item.IsQuantized,
                        details: item.Details,
                        tensor: tensor));
                }
            }
        }
        else if (node.BoundaryPort is not null)
        {
            SelectedNodeDomain = null;
            SelectedNodeVersion = null;
            SelectedNodeDocSummary = node.Kind == VisualNodeKind.GraphInput
                ? "Graph boundary input tensor feeding downstream operators."
                : "Graph boundary output tensor produced by upstream operators.";

            for (int t = 0; t < node.BoundaryPort.Tensors.Count; t++)
            {
                TensorEdge tensor = node.BoundaryPort.Tensors[t];
                TensorSummaryItem item = CreateTensorSummaryItem(tensor.Name, tensor);

                if (node.Kind == VisualNodeKind.GraphInput)
                {
                    SelectedNodeInputs.Add(new NodePortItem(
                        portName: node.BoundaryPort.Name,
                        tensorName: tensor.Name,
                        kindBadge: "Graph Input",
                        shapeText: item.ShapeText,
                        dataTypeText: item.DataTypeText,
                        quantizationBadge: item.QuantizationBadge,
                        quantizationBrushKey: item.QuantizationBrushKey,
                        quantizationTextBrushKey: item.QuantizationTextBrushKey,
                        isQuantized: item.IsQuantized,
                        details: item.Details,
                        tensor: tensor));
                }
                else
                {
                    SelectedNodeOutputs.Add(new NodePortItem(
                        portName: node.BoundaryPort.Name,
                        tensorName: tensor.Name,
                        kindBadge: "Graph Output",
                        shapeText: item.ShapeText,
                        dataTypeText: item.DataTypeText,
                        quantizationBadge: item.QuantizationBadge,
                        quantizationBrushKey: item.QuantizationBrushKey,
                        quantizationTextBrushKey: item.QuantizationTextBrushKey,
                        isQuantized: item.IsQuantized,
                        details: item.Details,
                        tensor: tensor));
                }
            }
        }
    }

    private static string GetAttributeTypeName(AttributeValue value) => value switch
    {
        AttributeValue.Int64 => "Int64",
        AttributeValue.Float64 => "Float64",
        AttributeValue.Bool => "Bool",
        AttributeValue.String => "String",
        AttributeValue.Int64List => "Int64[]",
        AttributeValue.Float64List => "Float64[]",
        AttributeValue.StringList => "String[]",
        AttributeValue.DataTypeValue => "DataType",
        AttributeValue.ShapeValue => "TensorShape",
        AttributeValue.Tensor => "Tensor",
        AttributeValue.Graph => "Graph",
        AttributeValue.GraphList => "Graph[]",
        AttributeValue.Bytes => "Bytes",
        _ => "Attribute"
    };

    private static string FormatAttributeValue(AttributeValue value) => value switch
    {
        AttributeValue.Int64 v => v.Value.ToString(CultureInfo.InvariantCulture),
        AttributeValue.Float64 v => v.Value.ToString("G6", CultureInfo.InvariantCulture),
        AttributeValue.Bool v => v.Value ? "true" : "false",
        AttributeValue.String v => $"\"{v.Value}\"",
        AttributeValue.Int64List v => $"[{string.Join(", ", v.Values)}]",
        AttributeValue.Float64List v => $"[{string.Join(", ", v.Values.Select(x => x.ToString("G4", CultureInfo.InvariantCulture)))}]",
        AttributeValue.StringList v => $"[{string.Join(", ", v.Values.Select(s => $"\"{s}\""))}]",
        AttributeValue.DataTypeValue v => v.Value.ToString(),
        AttributeValue.ShapeValue v => v.Value.ToDisplayString(),
        AttributeValue.Tensor v => $"Tensor<{v.Value.Descriptor.DataType}>{v.Value.Descriptor.Shape?.ToDisplayString() ?? "[]"}",
        AttributeValue.Graph v => $"Graph '{v.Value.Name}'",
        AttributeValue.GraphList v => $"Graphs ({v.Values.Count})",
        AttributeValue.Bytes v => $"Bytes ({v.Slice.ByteLength} bytes)",
        _ => value.ToString() ?? string.Empty
    };

    private void PopulateSubgraphSummaries(Subgraph subgraph)
    {
        Inputs.Clear();
        foreach (GraphPort port in subgraph.Inputs)
        {
            TensorEdge? tensor = port.Tensors.Count > 0 ? port.Tensors[0] : null;
            Inputs.Add(CreateTensorSummaryItem(port.Name, tensor));
        }

        Outputs.Clear();
        foreach (GraphPort port in subgraph.Outputs)
        {
            TensorEdge? tensor = port.Tensors.Count > 0 ? port.Tensors[0] : null;
            Outputs.Add(CreateTensorSummaryItem(port.Name, tensor));
        }

        // Count tensors and quantized tensors
        int quantizedCount = 0;
        foreach (TensorEdge edge in subgraph.Tensors)
        {
            if (edge.Descriptor.Quantization is not null || IsQuantizedDataType(edge.Descriptor.DataType))
            {
                quantizedCount++;
            }
        }
        TotalTensorCount = subgraph.Tensors.Count;
        QuantizedTensorCount = quantizedCount;

        // Group operators by category
        OperatorCategories.Clear();
        Dictionary<OperatorCategory, int> categoryCounts = new();
        foreach (GraphNode node in subgraph.Nodes)
        {
            OperatorCategory cat = node.Operator.Category;
            categoryCounts[cat] = categoryCounts.TryGetValue(cat, out int existing) ? existing + 1 : 1;
        }

        foreach ((OperatorCategory cat, int count) in categoryCounts.OrderByDescending(kv => kv.Value))
        {
            string catName = cat.ToString();
            OperatorCategories.Add(new OperatorCategorySummaryItem(
                category: cat,
                name: catName,
                count: count,
                categoryBrushKey: $"OpCategory{catName}Brush",
                categorySubtleBrushKey: $"OpCategory{catName}SubtleBrush"));
        }
    }

    private void PopulateQuickSummary(ModelGraph model)
    {
        long totalParams = 0;
        long totalWeightBytes = 0;
        int quantizedCount = 0;
        int totalTensors = 0;
        string? delegateText = null;

        foreach (Subgraph sg in model.Subgraphs)
        {
            foreach (TensorEdge t in sg.Tensors)
            {
                totalTensors++;
                if (t.Descriptor.Quantization is not null || IsQuantizedDataType(t.Descriptor.DataType))
                {
                    quantizedCount++;
                }

                if (t.Kind is TensorKind.Initializer or TensorKind.Constant)
                {
                    if (t.Descriptor.Shape is not null)
                    {
                        totalParams += ComputeElementCount(t.Descriptor.Shape);
                    }
                    if (t.Data is TensorDataSlice s)
                    {
                        totalWeightBytes += s.ByteLength;
                    }
                }
            }

            foreach (GraphNode n in sg.Nodes)
            {
                string opName = n.Operator.Name;
                if (opName.Contains("xnnpack", StringComparison.OrdinalIgnoreCase))
                {
                    delegateText = "Delegate: XNNPACK";
                }
                else if (delegateText is null && opName.Contains("delegate", StringComparison.OrdinalIgnoreCase))
                {
                    delegateText = $"Delegate: {opName}";
                }
            }
        }

        TotalParameters = totalParams;
        TotalParametersFormatted = FormatMetricCount(totalParams, "params");
        TotalWeightBytes = totalWeightBytes;
        TotalWeightBytesFormatted = FormatByteSize(totalWeightBytes);

        if (quantizedCount == 0)
        {
            PrecisionMixText = "FP32 Precision";
            PrecisionBadgeBrushKey = "QuantFp32BackgroundBrush";
        }
        else if (quantizedCount == totalTensors || (totalTensors > 0 && quantizedCount >= totalTensors / 2))
        {
            PrecisionMixText = totalTensors > 0 && quantizedCount == totalTensors
                ? "INT8 Quantized (100%)"
                : $"Mixed INT8 / FP32 ({(quantizedCount * 100) / Math.Max(1, totalTensors)}%)";
            PrecisionBadgeBrushKey = "QuantInt8BackgroundBrush";
        }
        else
        {
            PrecisionMixText = $"Mixed Quantized ({(quantizedCount * 100) / Math.Max(1, totalTensors)}%)";
            PrecisionBadgeBrushKey = "QuantInt8BackgroundBrush";
        }

        DelegateBlocksText = delegateText;
        HasDelegateBlocks = delegateText is not null;
    }

    private static string FormatMetricCount(long count, string suffix)
    {
        if (count >= 1_000_000)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{count / 1_000_000.0:F1}M {suffix}");
        }
        if (count >= 1_000)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{count / 1_000.0:F1}K {suffix}");
        }
        return $"{count} {suffix}";
    }

    private static long ComputeElementCount(TensorShape shape)
    {
        if (shape.IsScalar)
        {
            return 1;
        }

        long count = 1;
        bool hasFixed = false;
        for (int i = 0; i < shape.Dimensions.Count; i++)
        {
            if (shape.Dimensions[i] is TensorDimension.Fixed f)
            {
                count *= f.Value;
                hasFixed = true;
            }
        }
        return hasFixed ? count : 1;
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):F1} MB");
        }
        if (bytes >= 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KB");
        }
        return $"{bytes:N0} bytes";
    }

    private static TensorSummaryItem CreateTensorSummaryItem(string name, TensorEdge? tensor)
    {
        if (tensor is null)
        {
            return new TensorSummaryItem(
                name: name,
                shapeText: "[]",
                dataTypeText: "Unknown",
                quantizationBadge: "FP32",
                quantizationBrushKey: "QuantFp32BackgroundBrush",
                quantizationTextBrushKey: "QuantFp32ForegroundBrush",
                isQuantized: false);
        }

        string shapeText = tensor.Descriptor.Shape?.ToDisplayString() ?? "[]";
        string dataTypeName = tensor.Descriptor.DataType.ToString();
        bool isQuant = tensor.Descriptor.Quantization is not null || IsQuantizedDataType(tensor.Descriptor.DataType);

        string badge;
        string bgKey;
        string fgKey;
        string? details = null;

        if (tensor.Descriptor.Quantization is AffineQuantization affine)
        {
            badge = tensor.Descriptor.DataType is DataType.UInt8 or DataType.QuantizedUInt8 ? "UINT8" : "INT8";
            bgKey = "QuantInt8BackgroundBrush";
            fgKey = "QuantInt8ForegroundBrush";
            details = affine.Scale.Count > 0
                ? string.Format(CultureInfo.InvariantCulture, "Scale: {0:G4}, ZP: {1}", affine.Scale[0], affine.ZeroPoint.Count > 0 ? affine.ZeroPoint[0] : 0)
                : "Affine Quantization";
        }
        else if (tensor.Descriptor.Quantization is BlockQuantization block)
        {
            badge = $"INT{block.Bits}";
            bgKey = block.Bits <= 4 ? "QuantInt4BackgroundBrush" : "QuantInt8BackgroundBrush";
            fgKey = block.Bits <= 4 ? "QuantInt4ForegroundBrush" : "QuantInt8ForegroundBrush";
            details = $"Block size: {block.BlockSize}, Bits: {block.Bits}";
        }
        else if (tensor.Descriptor.DataType is DataType.Int4 or DataType.UInt4)
        {
            badge = "INT4";
            bgKey = "QuantInt4BackgroundBrush";
            fgKey = "QuantInt4ForegroundBrush";
        }
        else if (tensor.Descriptor.DataType is DataType.Int8 or DataType.UInt8 or DataType.QuantizedInt8 or DataType.QuantizedUInt8)
        {
            badge = tensor.Descriptor.DataType is DataType.UInt8 or DataType.QuantizedUInt8 ? "UINT8" : "INT8";
            bgKey = "QuantInt8BackgroundBrush";
            fgKey = "QuantInt8ForegroundBrush";
        }
        else if (tensor.Descriptor.DataType is DataType.Float16 or DataType.BFloat16)
        {
            badge = tensor.Descriptor.DataType == DataType.BFloat16 ? "BF16" : "FP16";
            bgKey = "QuantFp16BackgroundBrush";
            fgKey = "QuantFp16ForegroundBrush";
        }
        else
        {
            badge = "FP32";
            bgKey = "QuantFp32BackgroundBrush";
            fgKey = "QuantFp32ForegroundBrush";
        }

        return new TensorSummaryItem(
            name: name,
            shapeText: shapeText,
            dataTypeText: dataTypeName,
            quantizationBadge: badge,
            quantizationBrushKey: bgKey,
            quantizationTextBrushKey: fgKey,
            isQuantized: isQuant,
            details: details,
            tensor: tensor);
    }

    private static bool IsQuantizedDataType(DataType type) =>
        type is DataType.Int4
            or DataType.UInt4
            or DataType.Int8
            or DataType.UInt8
            or DataType.QuantizedInt8
            or DataType.QuantizedUInt8
            or DataType.QuantizedInt16
            or DataType.QuantizedInt32;

    private static string FormatModelFormatInfo(ModelFormatInfo info)
    {
        string id = info.Id.Value switch
        {
            "onnx" => "ONNX",
            "tflite" => "TensorFlow Lite",
            "executorch" => "ExecuTorch",
            "pytorch" => "PyTorch",
            "keras" => "Keras",
            _ => info.Id.Value.ToUpperInvariant()
        };
        string version = string.IsNullOrWhiteSpace(info.Version) ? string.Empty : $" ({info.Version})";
        return $"{id}{version}";
    }

    private void UpdateRecentFiles(string path)
    {
        string full = Path.GetFullPath(path);
        RecentFiles.Remove(full);
        RecentFiles.Insert(0, full);
        while (RecentFiles.Count > 8)
        {
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
        }
    }

    private void TrySeedRecentFiles()
    {
        string[] candidates =
        [
            "tests/DotNodeViewer.Core.Tests/Fixtures/onnx/tiny_cnn_dynamic_batch.onnx",
            "tests/DotNodeViewer.Core.Tests/Fixtures/onnx/tiny_mlp.onnx",
            "tests/DotNodeViewer.Core.Tests/Fixtures/tflite/tiny_cnn_int8.tflite",
            "tests/DotNodeViewer.Core.Tests/Fixtures/tflite/tiny_cnn_float.tflite"
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                RecentFiles.Add(Path.GetFullPath(candidate));
            }
        }
    }
}
