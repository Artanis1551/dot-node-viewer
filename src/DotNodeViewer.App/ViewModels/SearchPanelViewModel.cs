using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Layout;

namespace DotNodeViewer.App.ViewModels;

/// <summary>
/// Specifies the category of a search match item.
/// </summary>
public enum SearchResultCategory
{
    /// <summary>
    /// Matched an operator node name or instance identifier.
    /// </summary>
    Node,

    /// <summary>
    /// Matched an operator type name (e.g., <c>Conv</c>, <c>Relu</c>).
    /// </summary>
    Operator,

    /// <summary>
    /// Matched an operator semantic category (e.g., <c>Convolution</c>, <c>Activation</c>).
    /// </summary>
    Category,

    /// <summary>
    /// Matched a connected input, output, or weight tensor name.
    /// </summary>
    Tensor,
}

/// <summary>
/// Represents a single match item produced by searching across the active subgraph layout.
/// </summary>
public sealed class SearchResultItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SearchResultItem"/> class.
    /// </summary>
    /// <param name="title">The primary headline text for the search result.</param>
    /// <param name="subtitle">The secondary description or context for the search result.</param>
    /// <param name="category">The category of the search match.</param>
    /// <param name="targetNode">The target <see cref="VisualNode"/> to center and select upon navigation.</param>
    /// <param name="categoryBrushKey">The resource brush key for the category accent badge.</param>
    /// <param name="matchedTensorName">The optional tensor name that produced the match.</param>
    public SearchResultItem(
        string title,
        string subtitle,
        SearchResultCategory category,
        VisualNode targetNode,
        string categoryBrushKey,
        string? matchedTensorName = null)
    {
        Title = title;
        Subtitle = subtitle;
        Category = category;
        TargetNode = targetNode;
        CategoryBrushKey = categoryBrushKey;
        MatchedTensorName = matchedTensorName;
    }

    /// <summary>
    /// Gets the primary headline text for the search result.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the secondary description or context for the search result.
    /// </summary>
    public string Subtitle { get; }

    /// <summary>
    /// Gets the match category.
    /// </summary>
    public SearchResultCategory Category { get; }

    /// <summary>
    /// Gets the friendly display name of the match category.
    /// </summary>
    public string CategoryDisplayName => Category switch
    {
        SearchResultCategory.Node => "Node",
        SearchResultCategory.Operator => "Op Type",
        SearchResultCategory.Category => "Category",
        SearchResultCategory.Tensor => "Tensor",
        _ => "Match"
    };

    /// <summary>
    /// Gets the target <see cref="VisualNode"/> to center and select upon navigation.
    /// </summary>
    public VisualNode TargetNode { get; }

    /// <summary>
    /// Gets the resource brush key for the category accent badge.
    /// </summary>
    public string CategoryBrushKey { get; }

    /// <summary>
    /// Gets the matched tensor name, or <see langword="null"/> if not a tensor match.
    /// </summary>
    public string? MatchedTensorName { get; }
}

/// <summary>
/// ViewModel managing fast categorized search and filter queries across the active subgraph.
/// </summary>
public sealed partial class SearchPanelViewModel : ObservableObject
{
    private const int MaxResults = 50;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private SearchResultItem? _selectedResult;

    [ObservableProperty]
    private int _resultCount;

    [ObservableProperty]
    private bool _hasResults;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// Action invoked when a search result is activated for canvas navigation.
    /// </summary>
    public Action<VisualNode>? NavigateToNodeRequested { get; set; }

    /// <summary>
    /// Gets the collection of search results matching the current query.
    /// </summary>
    public ObservableCollection<SearchResultItem> Results { get; } = new();

    /// <summary>
    /// Executes a search query across the specified <paramref name="layout"/>.
    /// </summary>
    /// <param name="layout">The active positioned layout to search.</param>
    public void PerformSearch(VisualSubgraphLayout? layout)
    {
        Results.Clear();
        SelectedResult = null;

        if (layout is null || string.IsNullOrWhiteSpace(Query))
        {
            IsOpen = false;
            HasResults = false;
            ResultCount = 0;
            StatusMessage = string.Empty;
            return;
        }

        string q = Query.Trim();
        List<SearchResultItem> matches = new();

        for (int i = 0; i < layout.Nodes.Count && matches.Count < MaxResults; i++)
        {
            VisualNode node = layout.Nodes[i];
            string opType = node.Title;
            string nodeName = node.GraphNode?.Name ?? node.Subtitle ?? node.Title;
            string categoryName = node.Category.ToString();
            string brushKey = $"OpCategory{node.Category}Brush";

            // 1. Match Node Instance Name
            if (!string.IsNullOrEmpty(nodeName) && nodeName.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new SearchResultItem(
                    title: nodeName,
                    subtitle: $"Operator: {opType} ({categoryName})",
                    category: SearchResultCategory.Node,
                    targetNode: node,
                    categoryBrushKey: brushKey));
                continue;
            }

            // 2. Match Operator Type Name
            if (opType.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new SearchResultItem(
                    title: opType,
                    subtitle: $"Instance: {nodeName} ({categoryName})",
                    category: SearchResultCategory.Operator,
                    targetNode: node,
                    categoryBrushKey: brushKey));
                continue;
            }

            // 3. Match Category Name
            if (categoryName.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new SearchResultItem(
                    title: nodeName,
                    subtitle: $"Category: {categoryName} ({opType})",
                    category: SearchResultCategory.Category,
                    targetNode: node,
                    categoryBrushKey: brushKey));
                continue;
            }

            // 4. Match Connected Tensors
            if (node.GraphNode is not null)
            {
                GraphNode gn = node.GraphNode;
                string? matchedTensor = null;

                for (int p = 0; p < gn.Inputs.Count && matchedTensor is null; p++)
                {
                    NodePort port = gn.Inputs[p];
                    for (int t = 0; t < port.Tensors.Count; t++)
                    {
                        if (port.Tensors[t].Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedTensor = port.Tensors[t].Name;
                            break;
                        }
                    }
                }

                if (matchedTensor is null)
                {
                    for (int p = 0; p < gn.Outputs.Count && matchedTensor is null; p++)
                    {
                        NodePort port = gn.Outputs[p];
                        for (int t = 0; t < port.Tensors.Count; t++)
                        {
                            if (port.Tensors[t].Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                            {
                                matchedTensor = port.Tensors[t].Name;
                                break;
                            }
                        }
                    }
                }

                if (matchedTensor is not null)
                {
                    matches.Add(new SearchResultItem(
                        title: nodeName,
                        subtitle: $"Tensor: {matchedTensor}",
                        category: SearchResultCategory.Tensor,
                        targetNode: node,
                        categoryBrushKey: brushKey,
                        matchedTensorName: matchedTensor));
                }
            }
            else if (node.BoundaryPort is not null)
            {
                for (int t = 0; t < node.BoundaryPort.Tensors.Count; t++)
                {
                    if (node.BoundaryPort.Tensors[t].Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(new SearchResultItem(
                            title: node.Title,
                            subtitle: $"Boundary Tensor: {node.BoundaryPort.Tensors[t].Name}",
                            category: SearchResultCategory.Tensor,
                            targetNode: node,
                            categoryBrushKey: brushKey,
                            matchedTensorName: node.BoundaryPort.Tensors[t].Name));
                        break;
                    }
                }
            }
        }

        foreach (SearchResultItem item in matches)
        {
            Results.Add(item);
        }

        ResultCount = Results.Count;
        HasResults = Results.Count > 0;
        IsOpen = true;
        StatusMessage = HasResults ? $"{ResultCount} match{(ResultCount == 1 ? string.Empty : "es")}" : "No matching nodes found";
        SelectedResult = Results.FirstOrDefault();
    }

    /// <summary>
    /// Selects and navigates to the specified search result.
    /// </summary>
    /// <param name="item">The search result to activate.</param>
    [RelayCommand]
    public void SelectResult(SearchResultItem? item)
    {
        if (item is null)
        {
            return;
        }

        NavigateToNodeRequested?.Invoke(item.TargetNode);
        IsOpen = false;
    }

    /// <summary>
    /// Clears the query and closes the search results popup.
    /// </summary>
    [RelayCommand]
    public void ClearSearch()
    {
        Query = string.Empty;
        Results.Clear();
        SelectedResult = null;
        IsOpen = false;
        HasResults = false;
        ResultCount = 0;
        StatusMessage = string.Empty;
    }

    /// <summary>
    /// Selects the next item in the search results list.
    /// </summary>
    public void SelectNext()
    {
        if (Results.Count == 0)
        {
            return;
        }

        int index = SelectedResult is not null ? Results.IndexOf(SelectedResult) : -1;
        index = (index + 1) % Results.Count;
        SelectedResult = Results[index];
    }

    /// <summary>
    /// Selects the previous item in the search results list.
    /// </summary>
    public void SelectPrevious()
    {
        if (Results.Count == 0)
        {
            return;
        }

        int index = SelectedResult is not null ? Results.IndexOf(SelectedResult) : 0;
        index = (index - 1 + Results.Count) % Results.Count;
        SelectedResult = Results[index];
    }

    /// <summary>
    /// Confirms the current selection and navigates to it.
    /// </summary>
    public void ConfirmSelection()
    {
        if (SelectedResult is not null)
        {
            SelectResult(SelectedResult);
        }
    }
}

