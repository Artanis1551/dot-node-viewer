using DotNodeViewer.App.Rendering;
using DotNodeViewer.App.ViewModels;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Layout;

namespace DotNodeViewer.App.Tests;

/// <summary>
/// Unit tests for <see cref="SearchPanelViewModel"/> verifying search queries, categorization, and result activation.
/// </summary>
public sealed class SearchPanelViewModelTests
{
    private static VisualSubgraphLayout CreateSampleLayout()
    {
        TensorDescriptor fp32Tensor = new(DataType.Float32, TensorShape.Scalar);
        TensorEdge inTensor = new("input_act", fp32Tensor, TensorKind.Activation);
        TensorEdge weightTensor = new("conv_w", fp32Tensor, TensorKind.Initializer);
        TensorEdge outTensor = new("conv_out", fp32Tensor, TensorKind.Activation);

        GraphNode convNode = new(
            "conv_0",
            new OperatorInfo("Conv", OperatorCategory.Layer),
            Inputs: [new NodePort("X", [inTensor]), new NodePort("W", [weightTensor])],
            Outputs: [new NodePort("Y", [outTensor])],
            Attributes: []);

        GraphNode reluNode = new(
            "relu_1",
            new OperatorInfo("Relu", OperatorCategory.Activation),
            Inputs: [new NodePort("X", [outTensor])],
            Outputs: [new NodePort("Y", [new TensorEdge("relu_out", fp32Tensor, TensorKind.Activation)])],
            Attributes: []);

        Subgraph subgraph = new(
            "main",
            Nodes: [convNode, reluNode],
            Tensors: [inTensor, weightTensor, outTensor],
            Inputs: [new GraphPort("input_act", [inTensor])],
            Outputs: [new GraphPort("relu_out", [outTensor])]);

        ModelGraphLayoutAdapter adapter = new(textMeasurer: AvaloniaTextMeasurer.Default);
        return adapter.CreateLayout(subgraph);
    }

    [Test]
    public async Task PerformSearch_MatchesNodeTitle()
    {
        VisualSubgraphLayout layout = CreateSampleLayout();
        SearchPanelViewModel vm = new();

        vm.Query = "conv_0";
        vm.PerformSearch(layout);

        await Assert.That(vm.HasResults).IsTrue();
        await Assert.That(vm.Results.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(vm.Results[0].Title).IsEqualTo("conv_0");
        await Assert.That(vm.Results[0].Category).IsEqualTo(SearchResultCategory.Node);
    }

    [Test]
    public async Task PerformSearch_MatchesOperatorType()
    {
        VisualSubgraphLayout layout = CreateSampleLayout();
        SearchPanelViewModel vm = new();

        vm.Query = "Relu";
        vm.PerformSearch(layout);

        await Assert.That(vm.HasResults).IsTrue();
        await Assert.That(vm.Results.Any(r => r.Category == SearchResultCategory.Operator)).IsTrue();
    }

    [Test]
    public async Task PerformSearch_MatchesTensorName()
    {
        VisualSubgraphLayout layout = CreateSampleLayout();
        SearchPanelViewModel vm = new();

        vm.Query = "conv_w";
        vm.PerformSearch(layout);

        await Assert.That(vm.HasResults).IsTrue();
        await Assert.That(vm.Results.Any(r => r.Category == SearchResultCategory.Tensor && r.MatchedTensorName == "conv_w")).IsTrue();
    }

    [Test]
    public async Task SelectResult_InvokesCallback()
    {
        VisualSubgraphLayout layout = CreateSampleLayout();
        SearchPanelViewModel vm = new();

        VisualNode? navigatedNode = null;
        vm.NavigateToNodeRequested = node => navigatedNode = node;

        vm.Query = "relu";
        vm.PerformSearch(layout);

        await Assert.That(vm.Results.Count).IsGreaterThan(0);
        vm.SelectResult(vm.Results[0]);

        await Assert.That(navigatedNode).IsNotNull();
        await Assert.That(navigatedNode!.GraphNode?.Name).IsEqualTo("relu_1");
        await Assert.That(navigatedNode!.Title).IsEqualTo("Relu");
        await Assert.That(vm.IsOpen).IsFalse();
    }

    [Test]
    public async Task KeyboardNavigation_SelectsNextAndPrevious()
    {
        VisualSubgraphLayout layout = CreateSampleLayout();
        SearchPanelViewModel vm = new();

        vm.Query = "c"; // Matches conv_0 and other tokens
        vm.PerformSearch(layout);

        if (vm.Results.Count >= 2)
        {
            SearchResultItem first = vm.Results[0];
            SearchResultItem second = vm.Results[1];

            await Assert.That(vm.SelectedResult).IsEqualTo(first);
            vm.SelectNext();
            await Assert.That(vm.SelectedResult).IsEqualTo(second);
            vm.SelectPrevious();
            await Assert.That(vm.SelectedResult).IsEqualTo(first);
        }
    }
}
