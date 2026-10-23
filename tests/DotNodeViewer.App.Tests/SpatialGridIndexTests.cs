using DotNodeViewer.App.Rendering;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Layout;

namespace DotNodeViewer.App.Tests;

/// <summary>
/// Unit tests for <see cref="SpatialGridIndex"/> verifying <c>O(1)</c> spatial queries and region culling.
/// </summary>
public sealed class SpatialGridIndexTests
{
    [Test]
    public async Task EmptyIndex_ReturnsNullAndZeroCount()
    {
        SpatialGridIndex index = SpatialGridIndex.Empty;

        await Assert.That(index.Count).IsEqualTo(0);
        await Assert.That(index.FindNodeAt(100, 100)).IsNull();
        await Assert.That(index.FindNodesInRegion(0, 0, 500, 500)).IsEmpty();
    }

    [Test]
    public async Task FindNodeAt_InsideBounds_ReturnsNode()
    {
        PositionedNode layout = new(
            Id: "node:0:Conv_1",
            X: 100,
            Y: 200,
            Width: 160,
            Height: 80,
            Layer: 1,
            OrderInLayer: 0,
            InputPorts: [],
            OutputPorts: []);

        VisualNode node = new(
            Id: "node:0:Conv_1",
            Kind: VisualNodeKind.Operator,
            GraphNode: null,
            BoundaryPort: null,
            Title: "Conv",
            Subtitle: "conv_1",
            Category: OperatorCategory.Layer,
            CollapsedParameters: [],
            FusedActivationBadge: null,
            Layout: layout);

        SpatialGridIndex index = new([node], cellSize: 128.0);

        await Assert.That(index.Count).IsEqualTo(1);

        // Point strictly inside
        VisualNode? hitInside = index.FindNodeAt(150, 240);
        await Assert.That(hitInside).IsNotNull();
        await Assert.That(hitInside!.Id).IsEqualTo("node:0:Conv_1");

        // Top-left corner boundary
        VisualNode? hitCorner = index.FindNodeAt(100, 200);
        await Assert.That(hitCorner).IsNotNull();
        await Assert.That(hitCorner!.Id).IsEqualTo("node:0:Conv_1");

        // Point outside
        await Assert.That(index.FindNodeAt(90, 200)).IsNull();
        await Assert.That(index.FindNodeAt(270, 200)).IsNull();
        await Assert.That(index.FindNodeAt(150, 300)).IsNull();
    }

    [Test]
    public async Task FindNodesInRegion_ReturnsOnlyIntersectingNodesWithoutDuplicates()
    {
        PositionedNode layout1 = new("n1", 0, 0, 100, 100, 0, 0, [], []);
        PositionedNode layout2 = new("n2", 200, 0, 100, 100, 0, 1, [], []);
        PositionedNode layout3 = new("n3", 500, 500, 100, 100, 1, 0, [], []);

        VisualNode node1 = new("n1", VisualNodeKind.Operator, null, null, "Op1", null, OperatorCategory.Layer, [], null, layout1);
        VisualNode node2 = new("n2", VisualNodeKind.Operator, null, null, "Op2", null, OperatorCategory.Layer, [], null, layout2);
        VisualNode node3 = new("n3", VisualNodeKind.Operator, null, null, "Op3", null, OperatorCategory.Layer, [], null, layout3);

        SpatialGridIndex index = new([node1, node2, node3], cellSize: 64.0);

        // Region covering n1 and part of n2
        List<VisualNode> hits = index.FindNodesInRegion(-10, -10, 250, 150);

        await Assert.That(hits.Count).IsEqualTo(2);
        await Assert.That(hits.Select(n => n.Id)).Contains("n1");
        await Assert.That(hits.Select(n => n.Id)).Contains("n2");
        await Assert.That(hits.Select(n => n.Id)).DoesNotContain("n3");
    }
}

