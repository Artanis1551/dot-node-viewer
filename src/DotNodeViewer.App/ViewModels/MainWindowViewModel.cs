using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Layout;

namespace DotNodeViewer.App.ViewModels;

/// <summary>
/// Primary ViewModel driving the main shell window, file opening, background layout pipeline, and model diagnostics.
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
    private bool _isDarkTheme = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindowViewModel"/> class.
    /// </summary>
    public MainWindowViewModel()
    {
        TrySeedRecentFiles();
    }

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
    /// Gets or sets an optional file-picker delegate used by <see cref="OpenModelAsync"/> in desktop environments.
    /// </summary>
    public Func<Task<string?>>? FilePickerAsync { get; set; }

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

        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken ct = _loadingCts.Token;

        IsLoading = true;
        HasError = false;
        ErrorMessage = null;
        ErrorOffsetHex = null;
        ErrorContext = null;
        LoadingStatus = "Reading model stream...";
        StatusText = $"Opening {Path.GetFileName(path)}...";

        try
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Model file was not found: '{path}'", path);
            }

            ModelGraph model;
            VisualSubgraphLayout layout;
            Stopwatch sw = Stopwatch.StartNew();

            await using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                LoadingStatus = "Parsing model graph...";
                model = await ModelFormatRegistry.Default.ReadAsync(stream, options: null, ct).ConfigureAwait(true);

                LoadingStatus = "Computing layered layout...";
                layout = await Task.Run(() => ModelGraphLayoutAdapter.Default.CreateLayout(model, options: null, ct), ct).ConfigureAwait(true);
            }

            sw.Stop();
            LayoutDurationMs = sw.Elapsed.TotalMilliseconds;

            CurrentModel = model;
            CurrentLayout = layout;
            ModelPath = path;
            ModelName = Path.GetFileName(path);
            Title = $"{ModelName} — dot-node-viewer";

            FormatDisplayName = FormatModelFormatInfo(model.Format);
            ProducerName = string.IsNullOrWhiteSpace(model.Producer) ? "Unknown Producer" : model.Producer;

            TotalNodeCount = model.Subgraphs.Sum(s => s.Nodes.Count);
            TotalEdgeCount = layout.Edges.Count;

            // Subgraphs
            Subgraphs.Clear();
            foreach (Subgraph sg in model.Subgraphs)
            {
                Subgraphs.Add(sg);
            }

            SelectedSubgraph = model.PrimarySubgraph;
            if (SelectedSubgraph is not null)
            {
                PopulateSubgraphSummaries(SelectedSubgraph);
            }

            // Update recent files
            UpdateRecentFiles(path);

            HasModel = true;
            StatusText = $"Loaded {ModelName} in {sw.ElapsedMilliseconds}ms ({TotalNodeCount} nodes, {TotalEdgeCount} edges)";
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
            Application.Current.RequestedThemeVariant = IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        }
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
    /// Selects an active subgraph and updates input, output, and category summaries.
    /// </summary>
    /// <param name="subgraph">The subgraph to inspect.</param>
    [RelayCommand]
    public void SelectSubgraph(Subgraph? subgraph)
    {
        if (subgraph is null || ReferenceEquals(SelectedSubgraph, subgraph))
        {
            return;
        }

        SelectedSubgraph = subgraph;
        PopulateSubgraphSummaries(subgraph);

        if (CurrentModel is not null)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                CurrentLayout = ModelGraphLayoutAdapter.Default.CreateLayout(subgraph);
                sw.Stop();
                LayoutDurationMs = sw.Elapsed.TotalMilliseconds;
                TotalEdgeCount = CurrentLayout.Edges.Count;
                StatusText = $"Subgraph '{subgraph.Name}' laid out in {sw.ElapsedMilliseconds}ms ({subgraph.Nodes.Count} nodes)";
            }
            catch (Exception ex)
            {
                StatusText = $"Layout error: {ex.Message}";
            }
        }
    }

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
            details: details);
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
